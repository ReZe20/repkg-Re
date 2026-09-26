using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using RePKG_Re.Application.Texture;
using RePKG_Re.Core.Package;

namespace RePKG_Re.Application.Package
{
    public class MobilePackageOptions
    {
        /// <summary>移动包魔数。真机验过 PKGM0016 与 PKGM0019 都被接受，WE 自己两种都发，不是常量。</summary>
        public string Magic { get; set; } = "PKGM0019";

        /// <summary>丢 sounds/*.mp3。已真机定论：移动端不消费壁纸音频，但带 mp3 的包也被正常接受。</summary>
        public bool DropAudio { get; set; } = true;

        /// <summary>物化出的 RGBA8 是否尝试 LZ4（逐条目择优，压不小就写 lz=0）</summary>
        public bool UseLz4 { get; set; } = true;

        /// <summary>
        /// 纹理缩小除数，1/2/4 对应 WE "纹理缩小"下拉的 原始/2×/4×。
        /// N&gt;1 时除了缩像素，还要往 scene.json 的 general 块里写 "texturereduction" : N —— WE 就是这么发的。
        /// </summary>
        public int Reduction { get; set; } = 1;

        /// <summary>
        /// 缩小过的物化纹理发 ETC2 RGBA8(fmt5，1 字节/像素)而不是 RGBA8。Reduction=1 时无条件忽略。
        /// </summary>
        public bool EncodeEtc2 { get; set; }

        /// <summary>
        /// 着色器源码做 GLSL→GLSL ES 兼容改写（整数字面量落在 float 上下文时补 ".0"）。
        /// 移动端编译失败会让那一层材质回退成基础贴图，画出来是一块白。
        /// </summary>
        public bool ShaderCompat { get; set; } = true;

        /// <summary>
        /// 把非 raw 纹理物化成 RGBA8/ETC2。关掉之后所有 <c>.tex</c> 逐字节照搬 ——
        /// 逆向那条路有同名开关(<c>PcPackageOptions.Dematerialize</c>)而正向一直没有,这是能力缺口不是悬空键:
        /// 想做"只改容器魔数、像素一字节不动"的对照包就得靠它。着色器改写不受影响;
        /// 纹理一字节不动时 scene.json 的 texturereduction 也不会写(那条键描述的是像素,不是请求)。
        /// </summary>
        public bool Dematerialize { get; set; } = true;

        /// <summary>
        /// DXT 块格式的载荷要不要解码重缩。默认只在同时发 fmt5 时做（那是真机验过的那条形路）；
        /// 开它等于把那张图集整个解码+采样一遍，代价是内存与时间。
        /// 只在 <see cref="Reduction"/> &gt; 1 时有意义：<c>mpkgEtc2</c> 与 <c>mpkgShrinkDx</c> 任一为真都会走这条路，
        /// 区别只在发出去的是 fmt5 还是 RGBA8。
        /// </summary>
        public bool ShrinkDx { get; set; }

        /// <summary>同级 loose project.json 路径；包内已有同名条目时忽略</summary>
        public string ProjectJsonPath { get; set; }

        /// <summary>同级 preview.gif 路径；包内已有同名条目时忽略</summary>
        public string PreviewPath { get; set; }

        /// <summary>
        /// 一条纹理<b>内部</b>（多张源图 / 多级 mip）的解压并行度。0 = 按核数自适应的默认口径。
        /// 条目级并行（<see cref="MobilePackagePipeline"/>）跑起来时必须压成 1，否则 N 路条目 × 内层 8 路
        /// 互相超订，而内层那点收益已经被条目级并行覆盖掉了。
        /// </summary>
        public int InnerTextureParallelism { get; set; }
    }

    /// <summary>
    /// 一个包的转换结果。<b>计数一律走 <c>Add*</c></b>：条目产出现在同时跑在多个 worker 上，
    /// 裸 <c>int</c> 的 <c>++</c> 不是原子的 —— 摘要行是唯一对外交代"这次动了几条"的地方，丢读数比丢字节还难查。
    /// </summary>
    public class MobilePackageReport
    {
        private int _entries;
        private int _materialized;
        private int _copied;
        private int _dropped;
        private int _reduced;
        private int _texturesKept;
        private int _etc2Encoded;
        private int _dxReencoded;
        private int _framesScaled;
        private int _shadersRewritten;
        private int _shaderLiterals;
        private int _reductionRecorded;
        private long _inputBytes;
        private long _outputBytes;

        public int Entries => _entries;
        public int Materialized => _materialized;
        public int Copied => _copied;
        public int Dropped => _dropped;

        /// <summary>被缩小写出的是哪些条目:计数 + 有没有把键写进 scene.json</summary>
        public int Reduced => _reduced;

        /// <summary>关物化时照搬出去的 .tex 条数 —— 摘要行要说清"物化 0"是"没得物化"还是"你关的"。</summary>
        public int TexturesKept => _texturesKept;

        /// <summary>物化后发 ETC2(fmt5)的条目数</summary>
        public int Etc2Encoded => _etc2Encoded;

        /// <summary>走"解码重缩"那条路的 DXT 条目数 —— 一次跑七八条 DXT5 同时照搬的情况没上过真机，靠这个数定位。</summary>
        public int DxReencoded => _dxReencoded;

        /// <summary>像素被缩过、因此帧表也跟着缩过的动图条目数</summary>
        public int FramesScaled => _framesScaled;

        /// <summary>做过 GLSL→GLSL ES 兼容改写的着色器条目数 / 补上的整数字面量数</summary>
        public int ShadersRewritten => _shadersRewritten;
        public int ShaderLiterals => _shaderLiterals;

        public bool ReductionRecorded => Volatile.Read(ref _reductionRecorded) != 0;

        public long InputBytes => _inputBytes;
        public long OutputBytes => _outputBytes;

        public void AddEntries(int n) => Interlocked.Add(ref _entries, n);
        public void AddMaterialized(int n) => Interlocked.Add(ref _materialized, n);
        public void AddCopied(int n) => Interlocked.Add(ref _copied, n);
        public void AddDropped(int n) => Interlocked.Add(ref _dropped, n);
        public void AddReduced(int n) => Interlocked.Add(ref _reduced, n);
        public void AddTexturesKept(int n) => Interlocked.Add(ref _texturesKept, n);
        public void AddEtc2Encoded(int n) => Interlocked.Add(ref _etc2Encoded, n);
        public void AddDxReencoded(int n) => Interlocked.Add(ref _dxReencoded, n);
        public void AddFramesScaled(int n) => Interlocked.Add(ref _framesScaled, n);
        public void AddShadersRewritten(int n) => Interlocked.Add(ref _shadersRewritten, n);
        public void AddShaderLiterals(int n) => Interlocked.Add(ref _shaderLiterals, n);
        public void MarkReductionRecorded() => Interlocked.Exchange(ref _reductionRecorded, 1);
        public void AddInputBytes(long n) => Interlocked.Add(ref _inputBytes, n);
        public void AddOutputBytes(long n) => Interlocked.Add(ref _outputBytes, n);

        private readonly List<string> _warnings = new List<string>();
        private readonly List<string> _rewrites = new List<string>();
        private readonly List<KeyValuePair<int, string>> _warningDetails = new List<KeyValuePair<int, string>>();
        private readonly List<KeyValuePair<int, string>> _rewriteDetails = new List<KeyValuePair<int, string>>();

        /// <summary>
        /// "该做的没做成"：调用方（MpkgRunner）按错误处理，逐条发 error 事件、前端计入 ErrorCount。
        /// 例行播报不要放这里 —— 这个列表的语义就是"每一条都是错"，塞别的东西会让一次成功转化看起来像失败。
        /// </summary>
        public List<string> Warnings => _warnings;

        /// <summary>逐条动作明细，成功也要说的那种。由调用方打成 stdout 注释行，不进事件协议。</summary>
        public List<string> Rewrites => _rewrites;

        /// <summary>
        /// 明细带条目序号写入、发布前按序号排。并行下"谁先跑完谁先说话"会让每次跑的明细顺序都不一样，
        /// 而这些行是判读"某条到底动没动"的唯一依据（真机一块白时要能分清"没跑到"和"跑偏了"）——
        /// 稳定序不是美观问题。
        /// </summary>
        public void AddWarning(int index, string text)
        {
            lock (_warningDetails) _warningDetails.Add(new KeyValuePair<int, string>(index, text));
        }

        public void AddRewrite(int index, string text)
        {
            lock (_rewriteDetails) _rewriteDetails.Add(new KeyValuePair<int, string>(index, text));
        }

        /// <summary>包内所有条目落地后发布明细（串行路径同样调用，保证两条路径给出的顺序一致）。</summary>
        public void SealDetails()
        {
            Publish(_warningDetails, _warnings);
            Publish(_rewriteDetails, _rewrites);
        }

        private static void Publish(List<KeyValuePair<int, string>> pending, List<string> target)
        {
            lock (pending)
            {
                pending.Sort((a, b) => a.Key.CompareTo(b.Key));
                foreach (var kv in pending) target.Add(kv.Value);
                pending.Clear();
            }
        }
    }

    /// <summary>
    /// pkg(PC) → mpkg(移动) 的容器转换。条目布局与 pkg 完全一致：
    /// [int32 8][magic][int32 条目数] + 每条 [int32 名字字节数][名字 UTF-8][int32 偏移][int32 长度]，
    /// 数据区紧跟表尾，偏移相对数据区起点。
    ///
    /// 三段式：<see cref="BuildPlan"/>（读表、定条目序）→ <see cref="ProduceEntry"/>（单条字节）→
    /// <see cref="MpkgEntryWriter"/>（按表序拼接 + 回填偏移）。这个切法成立是因为<b>每条产物的字节
    /// 只取决于它自己的源条目</b>，而<b>偏移是前面所有产物长度之和</b> —— 所以产出可以交给任意多个 worker，
    /// 拼接必须按序单线程。串行调用 <see cref="Convert"/> 时三段也在，只是中间只驻留一条。
    ///
    /// 并行（<see cref="MobilePackagePipeline"/>）时大条目经 <see cref="MpkgTempWorkspace"/> 落一次临时盘：
    /// 不落的话驻留就是"worker 数 × 单条"，而单条物化后的 RGBA8 上限是 250MB
    /// （<see cref="Constants.MaximumMipmapByteCount"/>）、8K 图集解码就要 232MB —— 按核数放大等于把旧问题换个方向。
    /// 代价说清楚：大条目多写一遍盘、多读一遍，且失败/中止要靠工作区自己回收。
    /// </summary>
    public class MobilePackageConverter
    {
        /// <summary>
        /// 小于这个字节的产出留在内存里，不进临时文件。一个包里九成条目是 json/着色器/模型（几 B 到几十 KB），
        /// 给它们各开一次临时文件再读回来付的是纯开销 —— 要落盘的理由是那几条能长到几十 MB 的纹理，不是它们。
        /// </summary>
        internal const int SpillThresholdBytes = 1 << 20;

        internal sealed class PlanItem
        {
            public string Name;
            public byte[] NameBytes;
            public PackageEntry Source;
            public string LooseFile;
            public long RowFieldPosition; // 该行"偏移"字段的起点，长度紧随其后
            public bool IsSceneFile;      // 移动包的 texturereduction 键要写进这一条
            public bool NeedsCompat;      // 着色器源码要做 GLSL ES 兼容改写
        }

        /// <summary>
        /// 一个包的转换计划。单独成为一个类型是因为"产出可以并行、提交必须按表序"这条分工
        /// 要求计划活得比一次转换调用长：<see cref="MobilePackagePipeline"/> 按 <see cref="Count"/> 建槽位。
        /// </summary>
        public sealed class Plan
        {
            internal readonly List<PlanItem> Items;

            internal Plan(List<PlanItem> items) => Items = items;

            public string InputPkgPath;
            public string OutputMpkgPath;
            public MobilePackageOptions Options;
            public MobilePackageReport Report;

            /// <summary>输入流里数据区的起点（条目偏移就是相对它记的）。</summary>
            public int InputDataStart;

            /// <summary>产出阶段抛出的异常；拼接线程读到它就把这个包判失败，不写半截表。</summary>
            public Exception Failure;

            public int Count => Items.Count;
            public string SourceFileName => Path.GetFileName(InputPkgPath);
        }

        public MobilePackageReport Convert(
            string inputPkgPath,
            string outputMpkgPath,
            MobilePackageOptions options,
            Action<int, int, string> entryProgress = null)
        {
            if (inputPkgPath == null) throw new ArgumentNullException(nameof(inputPkgPath));
            if (outputMpkgPath == null) throw new ArgumentNullException(nameof(outputMpkgPath));

            var plan = BuildPlan(inputPkgPath, options ?? new MobilePackageOptions(), outputMpkgPath);

            // 串行路径：产一条、写一条、还一条，同时在产永远只有一条 —— 所以一个字节都不必落临时盘
            // （阈值给 int.MaxValue 就是"永不过"，落盘的意义只在并行窗口里才成立）。
            var m = NewMaterializer(plan.Options);
            using (var w = new MpkgEntryWriter(plan))
            {
                for (var i = 0; i < plan.Count; i++)
                {
                    entryProgress?.Invoke(i + 1, plan.Count, plan.Items[i].Name);
                    w.Write(ProducedEntry.FromBytes(ProduceEntry(plan, i, m), null, i, int.MaxValue), i);
                }

                w.Finish();
            }

            return plan.Report;
        }

        /// <summary>
        /// 每个 worker 一份物化器：<see cref="MobileTextureMaterializer"/> 带 UseLz4/Reduction/EncodeEtc2
        /// 这些每次转换才定的状态，共享等于让两个 worker 互相改参数。
        /// </summary>
        internal static MobileTextureMaterializer NewMaterializer(MobilePackageOptions options)
        {
            var m = new MobileTextureMaterializer
            {
                // 0 必须原样传下去："0 = 按核数自适应"是 TexDecodeParallelism 的语义，
                // 拿 Math.Max(1, …) 一夹就成了"永远锁一路"，实测把 ÷2 那条路拖回串行水平。
                Parallelism = Math.Max(0, options.InnerTextureParallelism)
            };
            Configure(m, options);
            return m;
        }

        /// <summary>
        /// 读表 + 决定哪些条目进产物（丢音频、加 loose 的 project.json/preview.gif）。
        /// 纯串行、纯内存，而且它定的条目序<b>就是</b>输出表序 —— 后面所有阶段都以它的下标为准。
        /// </summary>
        public Plan BuildPlan(string inputPkgPath, MobilePackageOptions options, string outputMpkgPath)
        {
            options ??= new MobilePackageOptions();
            var report = new MobilePackageReport();
            var package = ReadTable(inputPkgPath);
            var items = new List<PlanItem>(package.Entries.Count + 2);
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var sceneSlotOpen = true;

            foreach (var entry in package.Entries)
            {
                if (options.DropAudio && entry.FullPath.EndsWith(".mp3", StringComparison.OrdinalIgnoreCase))
                {
                    report.AddDropped(1);
                    report.AddInputBytes(entry.Length);
                    continue;
                }

                names.Add(entry.FullPath);
                items.Add(new PlanItem
                {
                    Name = entry.FullPath,
                    NameBytes = Encoding.UTF8.GetBytes(entry.FullPath),
                    Source = entry,
                    // WE 把 texturereduction 写在场景文件的 general 块里;只认第一条同名条目
                    IsSceneFile = sceneSlotOpen && Path.GetFileName(entry.FullPath)
                        .Equals("scene.json", StringComparison.OrdinalIgnoreCase),
                    NeedsCompat = options.ShaderCompat && ShaderCompatPatcher.IsShaderPath(entry.FullPath)
                });
                if (items[items.Count - 1].IsSceneFile) sceneSlotOpen = false;
                report.AddInputBytes(entry.Length);
            }

            AddLoose(items, names, "project.json", options.ProjectJsonPath);
            AddLoose(items, names, "preview.gif", options.PreviewPath);

            return new Plan(items)
            {
                InputPkgPath = inputPkgPath,
                OutputMpkgPath = outputMpkgPath,
                Options = options,
                Report = report,
                InputDataStart = package.HeaderSize
            };
        }

        /// <summary>
        /// 一条条目 → 要写进数据区的字节。<b>必须是纯函数</b>：只吃这一条的源字节 + 传进来的那份物化器。
        /// 并行下任何"顺手用一下共享流/共享状态"的写法都会变成竞态，而症状是内容错、长度对。
        /// 输入流按 worker 自开：FileStream 的 Seek+Read 不是线程安全的（extract 的 worker 同样各开各的）。
        /// </summary>
        internal static byte[] ProduceEntry(Plan plan, int index, MobileTextureMaterializer m)
        {
            var item = plan.Items[index];
            var options = plan.Options;
            var report = plan.Report;

            if (item.LooseFile != null)
                return File.ReadAllBytes(item.LooseFile);

            using var input = new FileStream(plan.InputPkgPath, FileMode.Open, FileAccess.Read, FileShare.Read);

            // 两个"表尾"绝不能混用：InputDataStart 是输入流里数据区的起点（条目偏移就是相对它记的），
            // outputDataStart 是正在写的那张新表的尾巴。两者字节数不同（条目数、名字长度都变了），
            // 拿后者去读前者会整体错位，症状正是"长度对、内容全错"。
            var bytes = PackageReader.ReadEntryBytesFromStream(input, plan.InputDataStart, item.Source.Offset,
                item.Source.Length);

            // 键要说的是"这个包里的纹理缩了几倍"。关物化时它是 1(没缩),照抄请求值就是假账。
            if (item.IsSceneFile)
                return RecordReduction(bytes, item, report, index,
                    options.Dematerialize ? EffectiveReduction(options) : 1);

            if (item.NeedsCompat)
                return ApplyShaderCompat(bytes, item, report, index);

            if (!item.Name.EndsWith(".tex", StringComparison.OrdinalIgnoreCase))
            {
                report.AddCopied(1);
                return bytes;
            }

            // 关物化:.tex 一字节不动。上面那两行(scene.json / 着色器)已经处理过了,那两件事不属于"物化"。
            if (!options.Dematerialize)
            {
                report.AddCopied(1);
                report.AddTexturesKept(1);
                return bytes;
            }

            var result = m.Materialize(bytes);

            switch (result.Action)
            {
                case MaterializeAction.Materialized:
                    report.AddMaterialized(1);
                    if (result.Reduced) report.AddReduced(1);
                    if (result.EncodedEtc2) report.AddEtc2Encoded(1);
                    if (result.DxReencoded) report.AddDxReencoded(1);
                    if (result.FramesScaled) report.AddFramesScaled(1);
                    return result.Bytes;

                case MaterializeAction.Refused:
                    report.AddWarning(index, $"{item.Name}: 拒绝物化，按原样写出（{result.Reason}）");
                    report.AddCopied(1);
                    return bytes;

                default:
                    report.AddCopied(1);
                    return bytes;
            }
        }

        /// <summary>
        /// 包写入器：先占位写整张表，再逐条追加数据并回填该行的偏移/长度。
        ///
        /// 之所以是"一条条写"而不是"一次写完整个包"：并行下产出是陆续到达的，提交线程必须能写一条、
        /// 还一条配额、再等下一条，否则窗口就退化成"整包驻留"。<b>表序与布局因此只有一份实现</b>，
        /// 串行与并行共用它 —— 两条路径各写一遍偏移回填，就是"并行改出来的包和串行不一样"的成因。
        /// </summary>
        internal sealed class MpkgEntryWriter : IDisposable
        {
            private readonly Plan _plan;
            private readonly FileStream _output;
            private readonly long _dataStart;
            private long _offset;
            private bool _finished;

            public MpkgEntryWriter(Plan plan)
            {
                _plan = plan;
                _output = new FileStream(plan.OutputMpkgPath, FileMode.Create, FileAccess.ReadWrite, FileShare.None);
                var writer = new BinaryWriter(_output, Encoding.UTF8, true);

                var magic = Encoding.ASCII.GetBytes(plan.Options.Magic);
                writer.Write(magic.Length);
                writer.Write(magic);
                writer.Write(plan.Items.Count);

                foreach (var item in plan.Items)
                {
                    writer.Write(item.NameBytes.Length);
                    writer.Write(item.NameBytes);
                    item.RowFieldPosition = _output.Position;
                    writer.Write(0); // 偏移：回填
                    writer.Write(0); // 长度：回填
                }

                _dataStart = _output.Position;
                if (_dataStart > int.MaxValue)
                    throw new InvalidOperationException($"条目表超出 int32 寻址范围: {_dataStart}");
            }

            /// <summary>写第 <paramref name="index"/> 条并回填它那一行；写完立刻归还字节。</summary>
            public void Write(ProducedEntry bytes, int index)
            {
                if (_finished) throw new InvalidOperationException("包已经收尾，不能再写条目");
                if (bytes == null)
                    throw new InvalidOperationException($"条目 {index}({_plan.Items[index].Name}) 没有产出字节，表会缺一格");

                _output.Seek(_dataStart + _offset, SeekOrigin.Begin);
                var written = bytes.CopyTo(_output);

                PatchRow(_output, _plan.Items[index].RowFieldPosition, _offset, written);
                _offset += written;

                _plan.Report.AddEntries(1);
                _plan.Report.AddOutputBytes(written);

                // 交完就还：并行窗口里每条都继续驻留的话，落临时盘就白落了
                bytes.Dispose();
            }

            public void Finish()
            {
                if (_finished) return;
                _finished = true;

                _output.SetLength(_dataStart + _offset);

                if (_output.Length != _dataStart + _offset)
                    throw new InvalidOperationException("写完自检失败：文件大小 != 表尾 + Σ条目长度");

                var report = _plan.Report;
                var options = _plan.Options;
                var count = _plan.Items.Count;

                // 关物化 + 要求缩小 = 一个字节都不会缩。这必须是"每一条都是错"的那类，
                // 否则产物会带着 texturereduction 键发出满尺寸纹理，比报错难查得多。
                // 序号给到表长之后，所以它永远排在按条目号排的那些明细后面。
                if (!options.Dematerialize && EffectiveReduction(options) > 1)
                    report.AddWarning(count,
                        $"关物化(dematerialize=false)时纹理一字节不动，缩小 ÷{EffectiveReduction(options)} 没有生效；" +
                        "scene.json 也因此没写 texturereduction");

                report.SealDetails();
            }

            public void Dispose() => _output.Dispose();
        }

        /// <summary>除数 1 就是"不缩"；这条归一在物化器与场景键两处必须同一个口径。</summary>
        internal static int EffectiveReduction(MobilePackageOptions options) =>
            options.Reduction > 1 ? options.Reduction : 1;

        /// <summary>
        /// 转换器和只读探针共用的那一段参数落地。<b>必须只有一份</b>：探针靠重放这几行来回答"这个档位在这张
        /// 壁纸上到底会不会动手"，两边各写一遍的话，探针说有、转换说没有（或反过来）就是最难查的那种不一致。
        /// </summary>
        internal static void Configure(MobileTextureMaterializer materializer, MobilePackageOptions options)
        {
            materializer.UseLz4 = options.UseLz4;
            materializer.Reduction = EffectiveReduction(options);
            materializer.EncodeEtc2 = options.EncodeEtc2 && materializer.Reduction > 1;
            materializer.ShrinkDx = options.ShrinkDx;
        }

        private static void AddLoose(List<PlanItem> plan, HashSet<string> names, string entryName, string path)
        {
            if (string.IsNullOrEmpty(path) || !File.Exists(path)) return;
            if (!names.Add(entryName)) return; // 包内已自带，不覆盖

            plan.Add(new PlanItem
            {
                Name = entryName,
                NameBytes = Encoding.UTF8.GetBytes(entryName),
                LooseFile = path
            });
        }

        // 本文件在 RePKG_Re.Application.Package 命名空间内，裸写 Package 会命中命名空间本身
        private static Core.Package.Package ReadTable(string inputPkgPath)
        {
            using var stream = new FileStream(inputPkgPath, FileMode.Open, FileAccess.Read, FileShare.Read);
            using var reader = new BinaryReader(stream, Encoding.UTF8, true);
            return new PackageReader {ReadEntryBytes = false}.ReadFrom(reader);
        }

        private static byte[] ApplyShaderCompat(byte[] bytes, PlanItem item, MobilePackageReport report, int index)
        {
            var source = Encoding.UTF8.GetString(bytes);
            var result = ShaderCompatPatcher.Patch(source);
            if (!result.Changed)
            {
                report.AddCopied(1);
                return bytes;
            }
            var patched = Encoding.UTF8.GetBytes(result.Text);
            // 只补 ".0"，所以改写前后的 UTF-8 字节必须只差在插入的字符上：整体长度差 = 2 × 字面量数。
            // 对不上就说明解码/编码本身没还原原文（异常字节），宁可这条不动也别把着色器写坏。
            if (patched.LongLength - bytes.LongLength != result.LiteralsChanged * 2L)
            {
                report.AddWarning(index, $"{item.Name}: 着色器含无法按 UTF-8 往返的字节，放弃兼容改写、按原样写出");
                report.AddCopied(1);
                return bytes;
            }
            report.AddShadersRewritten(1);
            report.AddShaderLiterals(result.LiteralsChanged);
            report.AddRewrite(index, $"{item.Name} {result.LinesChanged} 行/{result.LiteralsChanged} 处");
            return patched;
        }

        /// <summary>
        /// 缩了像素就要在 scene.json 里留下 texturereduction —— WE 的移动包两件事总是成对出现。
        /// 没缩时一个字节都不改：真机验过的产物形态就是"scene.json 与 PC 版逐字节相同"。
        /// </summary>
        private static byte[] RecordReduction(byte[] bytes, PlanItem item, MobilePackageReport report, int index,
            int reduction)
        {
            report.AddCopied(1);

            if (reduction <= 1)
            {
                if (SceneJsonPatcher.HasTextureReduction(bytes))
                    report.AddWarning(index, $"{item.Name}: 包内已带 texturereduction 键，本次没缩纹理，按原样写出");
                return bytes;
            }

            var patched = SceneJsonPatcher.SetTextureReduction(bytes, reduction, out var failure);
            if (patched == null)
            {
                report.AddWarning(index, $"{item.Name}: 写不进 texturereduction（{failure}），纹理已缩但场景文件保持原样");
                return bytes;
            }

            report.MarkReductionRecorded();
            return patched;
        }

        private static void PatchRow(Stream output, long rowFieldPosition, long offset, int length)
        {
            if (offset > int.MaxValue)
                throw new InvalidOperationException($"条目偏移超出 int32: {offset}");

            var tail = output.Position;
            output.Seek(rowFieldPosition, SeekOrigin.Begin);
            output.Write(BitConverter.GetBytes((int) offset), 0, 4);
            output.Write(BitConverter.GetBytes(length), 0, 4);
            output.Seek(tail, SeekOrigin.Begin);
        }
    }
}
