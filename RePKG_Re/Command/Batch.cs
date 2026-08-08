using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Runtime;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using CommandLine;
using Newtonsoft.Json;
using RePKG_Re.Application.Package;
using RePKG_Re.Core.Package;
using RePKG_Re.Core.Package.Enums;

namespace RePKG_Re.Command
{
    [Verb("batch", HelpText =
        "Extract multiple wallpapers from a manifest file. Errors are reported as JSON events; the batch continues.")]
    public class BatchOptions
    {
        [Option('m', "manifest", Required = true, HelpText = "Path to manifest JSON file")]
        public string Manifest { get; set; }

        [Option('t', "threads", HelpText = "Max worker threads (0 = CPU core count)", Default = 0)]
        public int Threads { get; set; }
    }

    /// <summary>
    /// batch 命令入口:manifest → 提取上下文 → 执行器。
    /// 正常完成一律 exit 0(错误以 JSON 事件上报);参数/清单错误 exit 1。
    /// </summary>
    public static class Batch
    {
        public static void Action(BatchOptions options)
        {
            // 强制 UTF-8 输出:事件 JSON 可能含非 ASCII 路径(中文壁纸名),net472 重定向默认 ANSI 编码会乱码
            Console.OutputEncoding = Encoding.UTF8;

            var manifest = BatchManifest.Load(options.Manifest);

            int threads = options.Threads > 0 ? options.Threads
                : manifest.Threads > 0 ? manifest.Threads
                : Environment.ProcessorCount;

            var ctx = new ExtractContext(manifest.ToExtractOptions());
            var runner = new BatchRunner(ctx, manifest.Wallpapers, threads);
            runner.Run();
            Console.WriteLine("{\"type\":\"batch\",\"action\":\"done\"}");
        }
    }

    /// <summary>
    /// 批处理执行器:全局条目队列 + N 个 worker 线程消费。
    /// 队列非空线程不闲 → 天然吃满 --threads。内存有界由 worker 数保证(同时处理的条目 ≤ worker 数,
    /// 在途字节 ≤ worker 数 × 单条目);队列仅存元数据(~100B/条),无界可接受。
    /// 事件协议:id/type/action/entry/pos/total 每行一个 JSON;worker 各自开流 seek 读字节。
    /// </summary>
    public class BatchRunner
    {
        private readonly ExtractContext _ctx;
        private readonly List<BatchWallpaper> _wallpapers;
        private readonly int _threads;

        public BatchRunner(ExtractContext ctx, List<BatchWallpaper> wallpapers, int threads)
        {
            _ctx = ctx;
            _wallpapers = wallpapers;
            _threads = Math.Max(1, threads);
        }

        public void Run()
        {
            // 线程池预热:net472 线程池默认缓慢爬升,直接拉到目标线程数,
            // 否则开头几秒实际并发达不到 --threads
            ThreadPool.SetMinThreads(_threads, _threads);

            // 无界队列:条目只含元数据(路径/偏移/长度,~100B),内存可控;
            // 全部入队后再启动 worker,避免有界队列在 worker 启动前被灌满而阻塞
            var queue = new BlockingCollection<BatchEntryItem>();
            var states = new Dictionary<string, WallpaperState>();

            // 内存闸:预防 OOM(TEX 转换并发按可用内存自适应),worker 数仍是硬上限
            var gate = new MemoryGate();
            gate.Start();
            try
            {
                foreach (var wallpaper in _wallpapers)
                    EnqueueWallpaper(wallpaper, queue, states);

                queue.CompleteAdding();

                var workers = new Task[_threads];
                for (int i = 0; i < workers.Length; i++)
                    workers[i] = Task.Run(() => WorkerLoop(queue, states, gate));

                Task.WaitAll(workers);
            }
            finally
            {
                gate.Stop();
            }
        }

        /// <summary>解析壁纸目录 → 条目元数据入全局队列;解析失败/无条目 → error + done,继续其余壁纸。</summary>
        private void EnqueueWallpaper(BatchWallpaper wallpaper, BlockingCollection<BatchEntryItem> queue,
            Dictionary<string, WallpaperState> states)
        {
            var dir = new DirectoryInfo(wallpaper.Input);
            if (!dir.Exists)
            {
                EmitError(wallpaper.Id, wallpaper.Input, "Input directory not found");
                EmitWallpaperDone(wallpaper.Id);
                return;
            }

            FileInfo[] pkgFiles;
            try
            {
                pkgFiles = dir.EnumerateFiles("*.pkg", SearchOption.AllDirectories)
                    .Concat(dir.EnumerateFiles("*.mpkg", SearchOption.AllDirectories))
                    .ToArray();
            }
            catch (Exception e)
            {
                EmitError(wallpaper.Id, wallpaper.Input, $"Enumerate pkg files failed: {e.Message}");
                EmitWallpaperDone(wallpaper.Id);
                return;
            }

            if (pkgFiles.Length == 0)
            {
                EmitError(wallpaper.Id, wallpaper.Input, "No .pkg/.mpkg files found in input directory");
                EmitWallpaperDone(wallpaper.Id);
                return;
            }

            var state = new WallpaperState();
            int total = 0;
            foreach (var pkg in pkgFiles)
            {
                try
                {
                    using (var stream = pkg.Open(FileMode.Open, FileAccess.Read, FileShare.Read))
                    using (var reader = new BinaryReader(stream, Encoding.UTF8, leaveOpen: true))
                    {
                        var entries = ExtractContext.ParsePkgEntriesTable(stream, reader, out int dataStart);
                        foreach (var entry in _ctx.FilterEntries(entries))
                        {
                            queue.Add(new BatchEntryItem(wallpaper.Id, wallpaper.Output, pkg, dataStart, entry));
                            total++;
                        }
                    }
                }
                catch (Exception e)
                {
                    EmitError(wallpaper.Id, pkg.FullName, $"Parse pkg failed: {e.Message}");
                }
            }

            if (total == 0)
            {
                EmitError(wallpaper.Id, wallpaper.Input, "No extractable entries found");
                EmitWallpaperDone(wallpaper.Id);
                return;
            }

            Directory.CreateDirectory(wallpaper.Output);
            state.Total = total;
            states[wallpaper.Id] = state;

            Console.WriteLine(
                $"{{\"id\":{J(wallpaper.Id)},\"type\":\"wallpaper\",\"action\":\"start\",\"total_entries\":{total}}}");
        }

        private void WorkerLoop(BlockingCollection<BatchEntryItem> queue, Dictionary<string, WallpaperState> states,
            MemoryGate gate)
        {
            foreach (var item in queue.GetConsumingEnumerable())
            {
                var state = states[item.WallpaperId];
                int pos = Interlocked.Increment(ref state.Started);

                // 每条目统一发 entry 事件(处理前发出,pos 单调递增,进度不依赖 TEX 转换事件)
                Console.WriteLine(
                    $"{{\"id\":{J(item.WallpaperId)},\"type\":\"entry\",\"entry\":{J(item.Entry.FullPath)},\"pos\":{pos},\"total\":{state.Total}}}");

                if (!_ctx.ShouldReadEntryBytes(item.Entry))
                {
                    TryFinish(item.WallpaperId, state);
                    continue;
                }

                // 内存闸:仅 TEX 转换条目(ImageSharp 位图是内存大头);raw 拷贝有 worker 数天然上界
                bool isTexConvert = item.Entry.Type == EntryType.Tex && !_ctx.Options.NoTexConvert;
                if (!isTexConvert)
                {
                    ProcessItem(item, state, pos);
                    TryFinish(item.WallpaperId, state);
                    continue;
                }

                long estimate = MemoryEstimate(item.Entry.Length);
                bool reserved = gate.TryAcquire(estimate);
                try
                {
                    ProcessItem(item, state, pos);
                }
                finally
                {
                    if (reserved)
                        gate.Release(estimate);
                }

                TryFinish(item.WallpaperId, state);
            }
        }

        private void ProcessItem(BatchEntryItem item, WallpaperState state, int pos)
        {
            try
            {
                using (var stream = item.Pkg.Open(FileMode.Open, FileAccess.Read, FileShare.Read))
                {
                    var bytes = PackageReader.ReadEntryBytesFromStream(stream, item.DataStart, item.Entry.Offset,
                        item.Entry.Length);
                    item.Entry.Bytes = bytes;

                    var outputDir = item.OutputDir;
                    _ctx.ExtractEntry(item.Entry, ref outputDir, pos, state.Total, item.WallpaperId);

                    item.Entry.Bytes = null; // free memory after processing
                }
            }
            catch (Exception e)
            {
                EmitError(item.WallpaperId, item.Entry.FullPath, e.Message);
            }
        }

        /// <summary>
        /// TEX 转换内存预估:解压位图 + ImageSharp 解码/克隆/编码缓冲(4K 实测 ~150-200MB)。
        /// 按压缩字节数放大估算(压缩率未知),保守偏大;上下限夹取。
        /// </summary>
        private static long MemoryEstimate(long entryLength)
        {
            const long minEstimate = 8L * 1024 * 1024;
            const long maxEstimate = 384L * 1024 * 1024;
            if (entryLength <= 0)
                return 32L * 1024 * 1024;
            return Math.Min(Math.Max(entryLength * 64, minEstimate), maxEstimate);
        }

        private void TryFinish(string wallpaperId, WallpaperState state)
        {
            // 最后一个处理完该壁纸条目的 worker 发出 done 事件(Interlocked 保证恰好一次)
            if (Interlocked.Increment(ref state.Done) == state.Total)
            {
                EmitWallpaperDone(wallpaperId);
                MaybeTrimMemory();
            }
        }

        // ---------- 内存整理 ----------
        // net472 的 GC 高水位设计:对象回收后托管堆(尤其 LOH)不归还给 OS,
        // 批量处理大纹理时任务管理器内存曲线持续爬升。在壁纸边界主动整理:
        // 压缩 LOH + 强制回收 + 修剪工作集,让内存回落。
        // 触发:每个壁纸完成时尝试,距上次整理至少 5 秒(小壁纸批量由时间节流兜底)。

        private const int TrimMinIntervalMs = 5000;

        private int _lastTrimTicks; // Environment.TickCount(int,32 位;批处理时长内无回绕问题)

        private void MaybeTrimMemory()
        {
            if (Environment.TickCount - Volatile.Read(ref _lastTrimTicks) < TrimMinIntervalMs) return;
            Volatile.Write(ref _lastTrimTicks, Environment.TickCount);
            TrimMemory();
        }

        private static void TrimMemory()
        {
            try
            {
                // 压缩 LOH 一次 + 两轮回收(第二轮清掉第一轮 WaitForPendingFinalizers 产生的可回收对象)
                GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.CompactOnce;
                GC.Collect(2, GCCollectionMode.Optimized, blocking: true);
                GC.WaitForPendingFinalizers();
                GC.Collect(2, GCCollectionMode.Optimized, blocking: true);

                // 修剪物理工作集,把内存页归还给系统(下次用到再缺页读回,壁纸边界低频触发,影响可忽略)
                SetProcessWorkingSetSize(Process.GetCurrentProcess().Handle, (IntPtr)(-1), (IntPtr)(-1));
            }
            catch (Exception e)
            {
                Console.Error.WriteLine($"* Memory trim failed: {e.Message}");
            }
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool SetProcessWorkingSetSize(IntPtr process, IntPtr min, IntPtr max);

        private static string J(string s) => JsonConvert.SerializeObject(s);

        private static void EmitError(string id, string entry, string msg)
            => Console.WriteLine($"{{\"id\":{J(id)},\"type\":\"error\",\"entry\":{J(entry)},\"msg\":{J(msg)}}}");

        private static void EmitWallpaperDone(string id)
            => Console.WriteLine($"{{\"id\":{J(id)},\"type\":\"wallpaper\",\"action\":\"done\"}}");
    }

    /// <summary>队列条目:条目元数据 + 所属壁纸与 pkg 定位信息(不含字节,内存有界)。</summary>
    internal sealed class BatchEntryItem
    {
        public string WallpaperId { get; }
        public string OutputDir { get; }
        public FileInfo Pkg { get; }
        public int DataStart { get; }
        public PackageEntry Entry { get; }

        public BatchEntryItem(string wallpaperId, string outputDir, FileInfo pkg, int dataStart, PackageEntry entry)
        {
            WallpaperId = wallpaperId;
            OutputDir = outputDir;
            Pkg = pkg;
            DataStart = dataStart;
            Entry = entry;
        }
    }

    /// <summary>每壁纸进度状态(Interlocked 字段,worker 并发更新)。</summary>
    internal sealed class WallpaperState
    {
        public int Total;
        public int Started;
        public int Done;
    }
}
