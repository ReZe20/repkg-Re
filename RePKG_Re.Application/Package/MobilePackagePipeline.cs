using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using RePKG_Re.Application.Texture;
using Plan = RePKG_Re.Application.Package.MobilePackageConverter.Plan;

namespace RePKG_Re.Application.Package
{
    /// <summary>
    /// pkg → mpkg 的并行排产器：<b>产出</b>按条目铺到 N 个 worker 上，<b>提交</b>每个包一条线程按表序写。
    ///
    /// 为什么能这么切（见 <see cref="MobilePackageConverter"/> 的类注释）：一条产物的字节只取决于它自己的
    /// 源条目，而偏移取决于前面所有条目的产物长度 —— 所以前者天生可并行，后者天生必须串行。
    /// 这也正是 extract 那套"全局条目队列 + N worker"的形状；差别只在 extract 里每个条目自己就是一个输出文件，
    /// 而这里所有条目要落进同一个文件，所以多出"按表序提交"这一段。
    ///
    /// 三道闸门，各管一件事，缺一都会坏：
    /// 1. <b>每包窗口</b>（<c>windowEntries</c>）：一个包内在产的条目数上限。产完但没提交的字节要么驻留内存、
    ///    要么躺在临时盘上；没有这道闸，投料可以一路灌到表尾，内存界就没了。
    /// 2. <b>活跃包数</b>（<c>maxActivePackages</c>）：同时在写、同时各占一个窗口的输出文件数。跨壁纸全局排队的
    ///    目的就是不浪费核，但没有这道闸，一批 200 张壁纸会同时开 200 个输出文件 × 窗口字节。
    /// 3. <b>worker 数</b>：真正动手产出的线程数，也就是核数口径。
    ///
    /// <b>合并（写出）发生在什么时候</b>：每个包的提交线程一拿到该包的第 i 格就写第 i 条，
    /// 既不等整包投完，也不等别的包 —— 一条壁纸的产物在它自己最慢的那几条纹理落地时就开始长了。
    /// 唯一的等待是"必须按表序"：第 3 条不能插在第 2 条前面，因为偏移是前缀和。
    ///
    /// 窗口配额的账本（必须只有一个归还点）：一条被投料的项目最终要么变成槽里的字节、被提交线程写掉并
    /// <see cref="ProducedEntry.Dispose"/>（Dispose 触发归还），要么因为该包已失败/已收尾而被 worker 当场 Dispose；
    /// 从没变成字节的那些（取消、异常）由 worker 直接归还。<see cref="ProducedEntry.Dispose"/> 的归还动作幂等，
    /// 所以上面这几条路重复碰到同一条也不会多还 —— 而少还一次，下一张壁纸就会在窗口上永久卡住一格。
    /// </summary>
    public sealed class MobilePackagePipeline : IDisposable
    {
        private sealed class Job
        {
            public Plan Plan;
            public ProducedEntry[] Slots;
            public readonly object Gate = new object();

            /// <summary>已投料但还没归还窗口的条目数。</summary>
            public int InFlight;

            /// <summary>投料指针：下一个要替这个包投进队列的条目下标（只有投料线程写它）。</summary>
            public int Fed;

            public bool CommitDone;

            /// <summary>产出或提交出过错 —— 之后不再写任何条目，也不再投料。</summary>
            public bool Cancelled;

            public Action<int, int, string> OnEntry;
            public Action<Plan> OnDone;
            public ManualResetEventSlim Finished = new ManualResetEventSlim(false);
            public SemaphoreSlim ActiveLease;

            public void ReturnWindowSlot()
            {
                lock (Gate)
                {
                    InFlight--;
                    Monitor.PulseAll(Gate);
                }
            }
        }

        private readonly List<Job> _jobs = new List<Job>();
        private readonly int _workers;
        private readonly int _window;
        private readonly int _maxActivePackages;
        private MpkgTempWorkspace _workspace;
        private BlockingCollection<KeyValuePair<Job, int>> _queue;

        /// <summary>活跃包名额（投料线程拿，提交线程还）。</summary>
        private SemaphoreSlim _active;

        private int _spilledEntries;
        private long _spilledBytes;
        private int _committedPackages;

        /// <summary>
        /// 产出阶段的耗时画像。<b>判据是这两个数的比</b>：
        /// <c>ProducedTotalMs / 墙钟</c> ≈ worker 数 ⇒ 核真的在同时干活；≈ 1 ⇒ 时间全在一条条目里，
        /// 条目级并行对它无能为力（而这时候加线程只会加内存，不会加速度）。
        /// </summary>
        private long _slowestMs;
        private string _slowestName;
        private long _producedTotalMs;
        private int _producedCount;
        private readonly object _timingGate = new object();

        /// <summary>最慢的那一条产出耗时（毫秒）与其条目名。</summary>
        public long SlowestEntryMs => Volatile.Read(ref _slowestMs);
        public string SlowestEntryName => Volatile.Read(ref _slowestName);

        /// <summary>所有条目产出耗时之和 / 条数 —— 与墙钟之比就是这次并发的实际并行度。</summary>
        public long ProducedTotalMs => Volatile.Read(ref _producedTotalMs);
        public int ProducedCount => Volatile.Read(ref _producedCount);

        /// <summary>落进临时仓的条目数与字节数（只有并行路径会非零）。</summary>
        public int SpilledEntries => _spilledEntries;
        public long SpilledBytes => _spilledBytes;

        /// <summary>真正写完并过了自检的包数 —— 用来核对"失败被吞掉、少出一个包"这种事没发生。</summary>
        public int CommittedPackages => _committedPackages;

        public MobilePackagePipeline(int workers, int windowEntries = 0, int maxActivePackages = 0)
        {
            _workers = Math.Max(1, workers);
            // 窗口默认 = worker 数：再多就是让 N 个产出线程给 1 个提交线程攒货。提交只是搬运已经算好的字节，
            // 不需要靠攒货来喂饱。
            _window = Math.Max(1, windowEntries > 0 ? windowEntries : _workers);
            // 活跃包默认 = worker 数的一半、至少 2：一个包被几条大纹理卡住时，另一包的小条目还能吃核。
            _maxActivePackages = Math.Max(2,
                maxActivePackages > 0 ? maxActivePackages : Math.Max(2, _workers / 2));
        }

        /// <summary>登记一个包。<b>不</b>在这里投料 —— 投料发生在 <see cref="Run"/>，窗口配额才有人统一管。</summary>
        public void Add(Plan plan, Action<int, int, string> onEntry, Action<Plan> onDone)
        {
            if (plan == null) throw new ArgumentNullException(nameof(plan));

            _jobs.Add(new Job
            {
                Plan = plan,
                Slots = new ProducedEntry[plan.Count],
                OnEntry = onEntry,
                OnDone = onDone
            });
        }

        /// <summary>
        /// 跑完所有已登记的包并等它们收尾。投料跑在当前线程上（它本来就是串行的：按登记顺序推进，
        /// 且被窗口和活跃包两道闸压着），worker 与提交线程各自独立。
        /// </summary>
        public void Run()
        {
            if (_queue != null) throw new InvalidOperationException("一个 pipeline 只跑一次");
            _workspace = new MpkgTempWorkspace();
            _queue = new BlockingCollection<KeyValuePair<Job, int>>();

            var workers = new Thread[_workers];
            for (var i = 0; i < workers.Length; i++)
            {
                workers[i] = new Thread(WorkerLoop) {IsBackground = true, Name = $"mpkg-produce-{i}"};
                workers[i].Start();
            }

            var active = _active = new SemaphoreSlim(_maxActivePackages, _maxActivePackages);
            var commits = new Thread[_jobs.Count];
            for (var i = 0; i < commits.Length; i++)
            {
                commits[i] = new Thread(CommitLoop) {IsBackground = true, Name = $"mpkg-commit-{i}"};
                var job = _jobs[i];
                job.ActiveLease = active;
                commits[i].Start(job);
            }

            try
            {
                Feed();
            }
            finally
            {
                _queue.CompleteAdding();
                foreach (var t in workers) t.Join();
                foreach (var t in commits) t.Join();
            }
        }

        /// <summary>
        /// 投料：按登记顺序一个包一个包推进。每个包动手前先拿"活跃包"名额（名额由那条包的提交线程在收尾时归还，
        /// <b>不是</b>这里 —— 在投料线程上等它结束就等于把活跃包数压成 1，跨壁纸并行也就没了），
        /// 然后在活跃包之间<b>轮转</b>投料：一轮里每个包最多推进一格。
        ///
        /// 为什么必须轮转（这条是被指出来才改的）：一个包的窗口腾位要等它自己提交，而提交又在等它的头几条 ——
        /// 一旦"喂完 A 才碰 B"，B 的条目就要等 A 快收尾才第一次进队列，于是同时在产的只有一个包、
        /// 同时打开的输出文件也只有一个。A 里有几条 8K 图集时，其余核与其余壁纸全在等它。
        /// 轮转之后每个包的提交线程都在自己头几条落地时就开始写文件，互不等待。
        /// </summary>
        private void Feed()
        {
            var next = 0;
            var active = new List<Job>();

            while (true)
            {
                // 1) 有名额就补新包进来（名额在各自的提交线程收尾时归还）
                while (active.Count < _maxActivePackages && next < _jobs.Count && _active.Wait(0))
                    active.Add(_jobs[next++]);

                // 2) 轮转投料：每个活跃包最多推一格
                var progressed = false;
                for (var i = active.Count - 1; i >= 0; i--)
                {
                    var job = active[i];
                    if (AdmitOne(job)) progressed = true;

                    // 投满或已取消的包退出活跃集：它剩下的事由它自己的提交线程收尾
                    if (job.Fed >= job.Slots.Length || Volatile.Read(ref job.Cancelled)) active.RemoveAt(i);
                }

                if (active.Count == 0 && next >= _jobs.Count) break;

                // 3) 这一轮谁都推不动（窗口全满，或没有名额）。轮询而不是多路等待：投料一次只推一格，
                // 2ms 相对一条纹理的产出时长可以忽略，而"每个 job 一个句柄再 WaitAny"会把死锁面翻一倍。
                if (!progressed) Thread.Sleep(2);
            }
        }

        /// <summary>
        /// 替 <paramref name="job"/> 投一格：占一个窗口位 + 入队。<b>不占位就不入队</b>，
        /// 所以窗口的账本始终是"已投料但还没归还"，而归还只可能来自条目的终点（提交写完 / 失败清仓）。
        /// </summary>
        private bool AdmitOne(Job job)
        {
            int index;

            lock (job.Gate)
            {
                if (job.Cancelled || job.Fed >= job.Slots.Length) return false;
                if (job.InFlight >= _window) return false;

                job.InFlight++;
                index = job.Fed++;
            }

            try
            {
                _queue.Add(new KeyValuePair<Job, int>(job, index));
            }
            catch
            {
                // 入队失败（队列已被标记完成）就是"这一格从没进过队列"，额度必须当场还回去
                job.ReturnWindowSlot();
                throw;
            }

            return true;
        }

        /// <summary>
        /// 每包一条提交线程。它必须与 worker 分开：投料是"按窗口往前推"的，提交是"按表序往后收"的，
        /// 同一条线程做这两件事会让第 0 条还没产出时整条流水线闲在那里。
        /// </summary>
        private void CommitLoop(object state)
        {
            var job = (Job) state;
            var plan = job.Plan;

            try
            {
                using (var writer = new MobilePackageConverter.MpkgEntryWriter(plan))
                {
                    var abort = false;

                    for (var i = 0; i < plan.Count; i++)
                    {
                        ProducedEntry entry;
                        lock (job.Gate)
                        {
                            while (!job.Cancelled && job.Slots[i] == null && plan.Failure == null) Monitor.Wait(job.Gate);
                            entry = job.Slots[i];
                            job.Slots[i] = null;
                        }

                        if (entry == null)
                        {
                            // 这一格永远不会有字节了（产出它的那次尝试抛了）。表不能留空洞，整包判失败。
                            abort = true;
                            break;
                        }

                        job.OnEntry?.Invoke(i + 1, plan.Count, plan.Items[i].Name);
                        writer.Write(entry, i); // 写完由 writer 归还这一格的窗口
                    }

                    if (!abort)
                    {
                        writer.Finish();
                        Interlocked.Increment(ref _committedPackages);
                    }
                }
            }
            catch (Exception e)
            {
                if (plan.Failure == null) plan.Failure = e;
                job.Cancelled = true;
                lock (job.Gate) Monitor.PulseAll(job.Gate);
            }
            finally
            {
                // 中止/收尾时把窗口里剩下的字节全清掉：临时文件不删就会一直留到进程退出。
                lock (job.Gate)
                {
                    job.CommitDone = true;
                    for (var i = 0; i < job.Slots.Length; i++)
                    {
                        var s = job.Slots[i];
                        if (s == null) continue;
                        job.Slots[i] = null;
                        s.Dispose();
                    }
                }

                try
                {
                    job.OnDone?.Invoke(plan);
                }
                catch
                {
                    // 回调是调用方的播报，播报失败不该把一个已经落盘的包改判成失败
                }

                // Finished 必须在归还活跃包名额之前置好：投料线程可能正卡在等这个包结束上。
                job.Finished.Set();
                job.ActiveLease?.Release();
            }
        }

        /// <summary>
        /// 产出 worker。每个线程一份 <see cref="MobileTextureMaterializer"/>（它带每次转换才定的状态），
        /// 输入流也每条自开 —— 见 <see cref="MobilePackageConverter.ProduceEntry"/>。
        /// </summary>
        private void WorkerLoop()
        {
            MobileTextureMaterializer m = null;
            MobilePackageOptions lastOptions = null;

            foreach (var work in _queue.GetConsumingEnumerable())
            {
                var job = work.Key;
                var index = work.Value;
                ProducedEntry entry = null;
                var handedOff = false;

                try
                {
                    if (job.Cancelled || Volatile.Read(ref job.Plan.Failure) != null) continue;

                    if (!ReferenceEquals(lastOptions, job.Plan.Options))
                    {
                        lastOptions = job.Plan.Options;
                        m = MobilePackageConverter.NewMaterializer(lastOptions);
                        // 内层解压<b>不</b>压成 1（这条我试过，实测更慢）：一张壁纸里真正耗时的往往就是
                        // 一两条 8K 图集，条目级并行对"一条"无能为力，而它内部的多张源图正好可以分出去。
                        // 3577990983 的 ÷2：内层锁 1 时 8 worker 是 252s（几乎没快过串行 266s），
                        // 放开内层自适应之后才是并行该有的样子。任务进的是全局线程池，池本身的并发上界
                        // 就是核数，所以"8×8 互相超订"不会真的产生 64 条 runnable。
                    }

                    var started = Environment.TickCount64;
                    var bytes = MobilePackageConverter.ProduceEntry(job.Plan, index, m);
                    var took = Environment.TickCount64 - started;

                    lock (_timingGate)
                    {
                        _producedTotalMs += took;
                        _producedCount++;
                        if (took > _slowestMs)
                        {
                            _slowestMs = took;
                            _slowestName = job.Plan.Items[index].Name;
                        }
                    }

                    entry = ProducedEntry.FromBytes(bytes, _workspace, index,
                        MobilePackageConverter.SpillThresholdBytes, job.ReturnWindowSlot);

                    if (entry.WasSpilled)
                    {
                        Interlocked.Increment(ref _spilledEntries);
                        Interlocked.Add(ref _spilledBytes, bytes.LongLength);
                    }

                    lock (job.Gate)
                    {
                        if (!job.Cancelled && !job.CommitDone && job.Plan.Failure == null)
                        {
                            job.Slots[index] = entry;
                            handedOff = true;
                            Monitor.PulseAll(job.Gate);
                        }
                        // 否则：提交那边已经不等这一格了，交给下面的 finally 当场退盘退额度
                    }
                }
                catch (Exception e)
                {
                    if (job.Plan.Failure == null) job.Plan.Failure = e;
                    job.Cancelled = true;
                    lock (job.Gate) Monitor.PulseAll(job.Gate);
                }
                finally
                {
                    if (!handedOff)
                    {
                        if (entry != null) entry.Dispose(); // 会归还额度
                        else job.ReturnWindowSlot();
                    }
                }
            }
        }

        public void Dispose()
        {
            _workspace?.Dispose();
            _workspace = null;
        }
    }
}
