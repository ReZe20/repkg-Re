using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using RePKG_Re.Application.Package;
using RePKG_Re.Core.Json;
using RePKG_Re.Core.Package;

namespace RePKG_Re.Command
{
    /// <summary>
    /// mode=mpkg 执行器。<b>并行度落在条目上</b>：全局条目队列 + N 个产出 worker，每个包一条按表序写的提交线程
    /// （见 <see cref="MobilePackagePipeline"/>）。壁纸这一层不再并行 —— 壁纸循环只读表、建计划这些轻活，
    /// 耗时的产出已经全部交给那 N 个 worker，再叠一层壁纸级并行只会把窗口和临时盘各乘一遍。
    ///
    /// 与 extract 的执行器仍然不通用：打包必须按表序单遍写文件并回填偏移，条目不能交给不同线程<b>写</b>
    /// （可以交给不同线程<b>算</b>，这正是本执行器与 extract 唯一的那点差别）。
    ///
    /// 内存画像：每个 worker 同时只驻留一个条目的字节，其中物化一条 RGBA8 的上限是
    /// Constants.MaximumMipmapByteCount(250MB)；超过 1MB 的产物落进临时仓，所以驻留不随核数线性涨。
    ///
    /// 事件协议与 extract/batch 完全一致(id 打点的 wallpaper start/done + entry + error)，
    /// WE Tool 侧的进度、重启、跳过逻辑无需改动。
    /// </summary>
    public class MpkgRunner
    {
        /// <summary>
        /// worker 数上限。条目级并行之后超线程只给很少的增量，而每个 worker 都可能按住一条 250MB 量级的
        /// 在产字节 —— 与 extract 用"物理核数"是同一条口径，这里只是再加一道硬顶。
        /// </summary>
        private const int MaxEntryWorkers = 16;

        /// <summary>
        /// stdout 写入锁。并行之前一条线程打一行，不存在交错；现在打事件的是 N 条提交线程 + 主线程，
        /// 而 <c>Console.WriteLine(string)</c> 只保证单行不被别人的行插进来，不保证我们把两次写拼成的一行完整 ——
        /// 一行被劈成两半，前端解析到的就是一个残缺 JSON。
        /// </summary>
        private static readonly object Out = new object();

        private readonly Func<BatchWallpaper, MobilePackageOptions> _resolveOptions;
        private readonly List<BatchWallpaper> _wallpapers;
        private readonly int _workers;
        private readonly bool _overwrite;

        public MpkgRunner(Func<BatchWallpaper, MobilePackageOptions> resolveOptions,
            List<BatchWallpaper> wallpapers, int threads, bool overwrite)
        {
            _resolveOptions = resolveOptions;
            _wallpapers = wallpapers;
            _workers = Math.Max(1, Math.Min(threads, MaxEntryWorkers));
            _overwrite = overwrite;
        }

        public void Run()
        {
            var converter = new MobilePackageConverter();
            var count = 0;

            using (var pipeline = new MobilePackagePipeline(_workers))
            {
                foreach (var wallpaper in _wallpapers)
                    count += RegisterWallpaper(converter, wallpaper, pipeline);

                // 一行读数说清这次的并行形状：worker 是核数口径，packages 是同时在产的包数上限的依据
                Console.Error.WriteLine($"* mpkg workers={_workers} packages={count}");

                var startedAt = Environment.TickCount64;
                try
                {
                    pipeline.Run();
                }
                finally
                {
                    var wall = Math.Max(1, Environment.TickCount64 - startedAt);

                    // 落盘读数：并行到底动了多少条大条目、临时仓用了多少，只有这里说得到
                    if (pipeline.SpilledEntries > 0)
                        WriteLine(
                            $"* 临时仓 {pipeline.SpilledEntries}条/{pipeline.SpilledBytes}B (>1MB 的产物先落盘再拼回)");

                    // 峰值驻留：并行改造换来的到底是不是"用内存换时间"，只有这一个数能判
                    using (var self = System.Diagnostics.Process.GetCurrentProcess())
                        WriteLine($"* 驻留峰值 {self.PeakWorkingSet64 / (1024 * 1024)}MB 用时{wall}ms");

                    // 并行度 = 产出耗时之和 ÷ 墙钟。≈ worker 数说明核真的在同时干活；≈ 1 说明时间全压在
                    // 一条条目内部（最慢那条当场给出名字）—— 那种情况下加线程只加内存，不加速度。
                    WriteLine(
                        $"* 并行度 {(double)pipeline.ProducedTotalMs / wall:F1}×/{_workers}worker " +
                        $"产出合计 {pipeline.ProducedTotalMs}ms/{pipeline.ProducedCount}条 " +
                        $"最慢 {pipeline.SlowestEntryName} {pipeline.SlowestEntryMs}ms");
                }
            }
        }

        /// <summary>
        /// 一张壁纸 = 若干包。这一层只读表、建计划、发 start；产出与提交都在 pipeline 里跑。
        /// 返回登记的包数（只为那行读数）。
        /// </summary>
        private int RegisterWallpaper(
            MobilePackageConverter converter,
            BatchWallpaper wallpaper,
            MobilePackagePipeline pipeline)
        {
            FileInfo[] found;
            try
            {
                found = ResolvePackages(wallpaper.Input).ToArray();
            }
            catch (Exception e)
            {
                EmitError(wallpaper.Id, wallpaper.Input, e.Message);
                EmitDone(wallpaper.Id);
                return 0;
            }

            if (found.Length == 0)
            {
                EmitError(wallpaper.Id, wallpaper.Input, "No .pkg files found in input");
                EmitDone(wallpaper.Id);
                return 0;
            }

            int total;
            try
            {
                total = found.Sum(p => CountEntries(p));
            }
            catch (Exception e)
            {
                EmitError(wallpaper.Id, wallpaper.Input, $"Parse pkg table failed: {e.Message}");
                EmitDone(wallpaper.Id);
                return 0;
            }

            if (total == 0)
            {
                EmitError(wallpaper.Id, wallpaper.Input, "No entries found");
                EmitDone(wallpaper.Id);
                return 0;
            }

            try
            {
                Directory.CreateDirectory(wallpaper.Output);
            }
            catch (Exception e)
            {
                EmitError(wallpaper.Id, wallpaper.Output, $"Create output directory failed: {e.Message}");
                EmitDone(wallpaper.Id);
                return 0;
            }

            EmitStart(wallpaper.Id, total);

            // 一张壁纸的 done 要等它自己所有包都播完 —— 各包在不同线程上收尾，所以这里用计数器等齐，
            // 不能在各包回调里各发一次 done（converted 列表会缺）。
            var group = new WallpaperGroup(this, wallpaper);
            var registered = 0;

            foreach (var pkg in found)
            {
                var target = Path.Combine(wallpaper.Output,
                    ResolveStem(wallpaper, pkg, found.Length > 1) + ".mpkg");

                var ctx = new PackageContext(this, wallpaper, pkg, target, new ProgressCursor {Total = total}, group);

                if (!_overwrite && File.Exists(target))
                {
                    // 跳过不过 pipeline，就地播报：登记期是本函数独占这条线程，
                    // 留到 pipeline 跑完再播会让这张壁纸的 done 排在它自己的条目事件之前。
                    registered++;
                    ctx.ReportSkip();
                    continue;
                }

                // 按这条壁纸解析(条目级覆盖已在清单侧解析并在 Validate 里校验过)。
                // 每包拿新实例:下面两个 loose 字段写在实例上,不会串到下一张壁纸。
                var options = _resolveOptions(wallpaper);
                options.ProjectJsonPath = FindLoose(pkg, "project.json");
                options.PreviewPath = FindPreview(pkg);
                ctx.Options = options;

                MobilePackageConverter.Plan plan;
                try
                {
                    plan = converter.BuildPlan(pkg.FullName, options, target);
                }
                catch (Exception e)
                {
                    // 连表都读不动：与产出失败同样处理，一条 error 就够，不占这张壁纸的等齐计数
                    registered++;
                    ctx.BuildFailure = e;
                    ctx.Announce();
                    continue;
                }

                registered++;
                group.ExpectOne();
                pipeline.Add(plan, ctx.OnEntry, ctx.OnPlanDone);
            }

            group.Ready();
            return registered;
        }

        /// <summary>一张壁纸的"等齐"计数器：所有包收尾后才发 done。</summary>
        private sealed class WallpaperGroup
        {
            private readonly MpkgRunner _runner;
            private readonly BatchWallpaper _wallpaper;
            private int _pending;
            private bool _ready;
            private readonly List<string> _converted = new List<string>();

            public WallpaperGroup(MpkgRunner runner, BatchWallpaper wallpaper)
            {
                _runner = runner;
                _wallpaper = wallpaper;
            }

            public void ExpectOne()
            {
                lock (this) _pending++;
            }

            /// <summary>
            /// 登记阶段结束。<b>必须</b>调用，哪怕一格都没等齐（全是跳过或全是建计划失败）：
            /// 那种情况下 done 就在这里发，否则这张壁纸永远没有 done，前端的进度条会停在 100% 之外。
            /// </summary>
            public void Ready()
            {
                bool fire;
                lock (this)
                {
                    _ready = true;
                    fire = _pending == 0;
                }
                if (fire) EmitDoneWithConverted();
            }

            public void Completed(string targetFileName)
            {
                lock (this) _converted.Add(targetFileName);
                FinishOne();
            }

            public void FinishOne()
            {
                bool fire;
                lock (this)
                {
                    _pending--;
                    fire = _pending == 0;
                }
                if (fire && _ready) EmitDoneWithConverted();
            }

            private void EmitDoneWithConverted()
            {
                string[] converted;
                lock (this) converted = _converted.ToArray();

                _runner.WriteLine(
                    $"{{\"id\":{J(_wallpaper.Id)},\"type\":\"wallpaper\",\"action\":\"done\"," +
                    $"\"converted\":{J(string.Join("|", converted))}}}");
            }
        }

        /// <summary>
        /// 一个包从登记到播报的全部上下文。产出与提交跑在别的线程上，所以它只当数据 + 两个回调，
        /// 不持有任何流。
        /// </summary>
        private sealed class PackageContext
        {
            private readonly MpkgRunner _runner;
            private readonly WallpaperGroup _group;

            public PackageContext(MpkgRunner runner, BatchWallpaper wallpaper, FileInfo pkg, string target,
                ProgressCursor cursor, WallpaperGroup group)
            {
                _runner = runner;
                _group = group;
                Wallpaper = wallpaper;
                Pkg = pkg;
                Target = target;
                Cursor = cursor;
            }

            public BatchWallpaper Wallpaper;
            public FileInfo Pkg;
            public string Target;
            public ProgressCursor Cursor;
            public MobilePackageOptions Options;

            public Exception BuildFailure;
            public Exception Failure;
            public MobilePackageReport Report;

            public void OnEntry(int index, int ofPackage, string entry)
            {
                if (Program.Closing) Environment.Exit(0);
                _runner.EmitEntry(Wallpaper.Id, Cursor, entry);
            }

            public void OnPlanDone(MobilePackageConverter.Plan plan)
            {
                Options = plan.Options;
                Failure = plan.Failure;
                Report = plan.Report;

                try
                {
                    Announce();
                }
                finally
                {
                    _group.Completed(Path.GetFileName(Target));
                }
            }

            /// <summary>跳过：与改造前一样，把这一包的进度推满再打一行。</summary>
            public void ReportSkip()
            {
                try
                {
                    var n = CountEntries(Pkg);
                    for (var i = 0; i < n; i++) _runner.EmitEntry(Wallpaper.Id, Cursor, Pkg.Name);
                }
                catch
                {
                    // 推不了进度也要把跳过说出来，别让一个包静默消失
                }

                _runner.WriteLine($"* Skipping, already exists: {Target}");
            }

            /// <summary>摘要行 / 失败 —— 文案与改造前逐字一致，只是现在由提交线程在该包收尾时调用。</summary>
            public void Announce()
            {
                if (BuildFailure != null)
                {
                    _runner.EmitError(Wallpaper.Id, Pkg.FullName, BuildFailure.Message);
                    return;
                }

                if (Report == null) return; // 登记了但从未跑到

                if (Failure != null)
                {
                    _runner.EmitError(Wallpaper.Id, Pkg.FullName, Failure.Message);
                    return;
                }

                var report = Report;
                var options = Options;

                // 缩了就必须交代场景文件里那个键：纹理缩了但键没写进去，手机侧的行为是未知的
                var reduced = options.Reduction > 1
                    ? $" 缩小 {report.Reduced}(÷{options.Reduction}) 场景键 {(report.ReductionRecorded ? "已写" : "未写")}" +
                      (options.EncodeEtc2 ? $" fmt5 {report.Etc2Encoded}" : "") +
                      // DXT 那条解码路平时是跟着 fmt5 一起走的,所以开着没数也要报:一张壁纸里 7 条 DXT5
                      // 同时被重编这种情况没上过真机,读数得能看出这次到底动了几条。
                      (options.ShrinkDx || report.DxReencoded > 0 ? $" DXT重缩 {report.DxReencoded}" : "") +
                      (report.FramesScaled > 0 ? $" 帧表 {report.FramesScaled}" : "")
                    : "";

                // 物化 0 有两种意思:"包里没有可物化的东西"和"你把物化关了"。产物字节完全不同,不能都读成 0。
                var kept = options.Dematerialize ? "" : $" 物化关 {report.TexturesKept}条照搬";

                // 这串必须无条件报：真机上一块白，要能分清是"改写没跑到"还是"改写不到位"，
                // 而 0 条本身就是读数 —— 不含整数字面量的着色器包不该被改动。
                var compat = options.ShaderCompat
                    ? $" 着色器改写 {report.ShadersRewritten}条/{report.ShaderLiterals}处"
                    : " 着色器改写 关";

                _runner.WriteLine(
                    $"* mpkg {Pkg.Name} → {Path.GetFileName(Target)}  " +
                    $"{report.InputBytes}→{report.OutputBytes}B 条目 {report.Entries} 物化 {report.Materialized} " +
                    $"搬运 {report.Copied} 丢弃 {report.Dropped}{kept}{reduced}{compat}");

                // 逐文件明细走 stdout 注释行，不进事件协议：前端只解析带 '{' 的行，
                // 而报错通道 report.Warnings 的语义是"该做的没做成"，例行播报混进去会让成功看起来像失败。
                foreach (var rewrite in report.Rewrites)
                    _runner.WriteLine($"*   着色器兼容改写 {rewrite}");

                foreach (var warning in report.Warnings)
                    _runner.EmitError(Wallpaper.Id, Pkg.Name, warning);
            }
        }

        /// <summary>条目级进度游标（lambda 里不能碰 ref 参数，所以用可变态对象承载）。</summary>
        private sealed class ProgressCursor
        {
            public int Total;
            private int _pos;

            /// <summary>推进一格并返回截断后的 pos。多条提交线程会并发推同一个游标，所以必须原子。</summary>
            public int Advance()
            {
                var pos = Interlocked.Increment(ref _pos);
                // total 只数了输入包里的条目,而 project.json/preview.gif 是打包时新增的条目,
                // 分母不知道它们 —— 不截的话 pos/total 会报到 113%(UI 侧的百分比越过上界)
                return Math.Min(pos, Total);
            }
        }

        private void EmitEntry(string id, ProgressCursor cursor, string entry)
            => WriteLine($"{{\"id\":{J(id)},\"type\":\"entry\",\"entry\":{J(entry)},\"pos\":{cursor.Advance()}," +
                         $"\"total\":{cursor.Total}}}");

        /// <summary>
        /// 输出文件名主干:清单没给 outputName 就用源包名;给了就清洗掉非法文件名字符(调用方给的往往是壁纸标题,
        /// 标题里有 : / ? 这类字符是常态),并在一个壁纸解出多个包时缀上源包名 —— 否则几个包会写进同一个文件。
        /// 正向(mpkg)与逆向(pkg)共用,清洗逻辑必须只有一份(见 Extensions.IsInvalidFileNameChar)。
        /// </summary>
        internal static string ResolveStem(BatchWallpaper wallpaper, FileInfo pkg, bool multiple)
            => ResolveStem(wallpaper, Path.GetFileNameWithoutExtension(pkg.Name), multiple);

        /// <summary>
        /// 字符串版给 mode=pack 用：打包的输入是一个目录，没有"文件名去掉扩展名"这一步，
        /// 主干就是工程目录名。清洗规则只有一份(见 Extensions.IsInvalidFileNameChar)。
        /// </summary>
        internal static string ResolveStem(BatchWallpaper wallpaper, string sourceStem, bool multiple)
        {
            var source = sourceStem ?? "";
            var name = (wallpaper.OutputName ?? "").Trim();
            if (name.Length == 0) return source;

            // 跨平台固定集合(见 Extensions.InvalidFileNameChars),不用平台相关的
            // Path.GetInvalidFileNameChars():否则同一标题在 Linux/Windows 洗出不同文件名。
            var chars = name.ToCharArray();
            for (var i = 0; i < chars.Length; i++)
                if (Extensions.IsInvalidFileNameChar(chars[i])) chars[i] = '_';
            name = new string(chars);
            name = name.Trim();

            return name.Length == 0
                ? source
                : multiple ? $"{name}_{source}" : name;
        }

        // internal:mode:inspect 的执行器复用同一条"什么算一个包"的规则(含 scene.pkg 排最前那条排序),
        // 两处各写一遍迟早会分叉成分叉。
        internal static List<FileInfo> ResolvePackages(string input)
        {
            if (File.Exists(input))
                return IsPackage(input)
                    ? new List<FileInfo> {new FileInfo(input)}
                    : new List<FileInfo>();

            if (!Directory.Exists(input))
                return new List<FileInfo>();

            // "*.pkg" 不匹配 .mpkg，两个模式都得枚举（与 extract 的执行器一致）
            return Directory.EnumerateFiles(input, "*.pkg", SearchOption.AllDirectories)
                .Concat(Directory.EnumerateFiles(input, "*.mpkg", SearchOption.AllDirectories))
                .OrderByDescending(f => string.Equals(Path.GetFileName(f), "scene.pkg", StringComparison.OrdinalIgnoreCase))
                .Select(f => new FileInfo(f))
                .ToList();
        }

        private static bool IsPackage(string path) =>
            path.EndsWith(".pkg", StringComparison.OrdinalIgnoreCase) ||
            path.EndsWith(".mpkg", StringComparison.OrdinalIgnoreCase);

        internal static int CountEntries(FileInfo pkg)
        {
            using var stream = pkg.Open(FileMode.Open, FileAccess.Read, FileShare.Read);
            using var reader = new BinaryReader(stream, Encoding.UTF8, true);
            return new PackageReader {ReadEntryBytes = false}.ReadFrom(reader).Entries.Count;
        }

        /// <summary>找包同级的 loose 文件（工坊订阅项目录里 140/140 都有 project.json）。</summary>
        private static string FindLoose(FileInfo pkg, string fileName)
        {
            var directory = pkg.Directory;
            if (directory == null)
                return null;

            var path = Path.Combine(directory.FullName, fileName);
            return File.Exists(path) ? path : null;
        }

        /// <summary>预览图按 project.json 的 preview 字段取，取不到回落 preview.gif。</summary>
        private static string FindPreview(FileInfo pkg)
        {
            var directory = pkg.Directory;
            if (directory == null)
                return null;

            var projectPath = Path.Combine(directory.FullName, "project.json");
            if (File.Exists(projectPath))
            {
                try
                {
                    var json = LegacyJson.Parse(File.ReadAllText(projectPath));
                    var preview = LegacyJson.AsString(LegacyJson.GetProp(json, "preview"));
                    if (!string.IsNullOrWhiteSpace(preview))
                    {
                        var declared = Path.Combine(directory.FullName, preview);
                        if (File.Exists(declared))
                            return declared;
                    }
                }
                catch
                {
                    // 清单读不动就退回默认名，不值得为预览图失败中断整次转换
                }
            }

            return FindLoose(pkg, "preview.gif");
        }

        private static string J(string s) => LegacyJson.QuoteString(s);

        /// <summary>
        /// 实例方法只是形式：<see cref="Out"/> 是静态的，为的是"整个进程一个 stdout 写入序列"。
        /// </summary>
        private void WriteLine(string line)
        {
            lock (Out) Console.WriteLine(line);
        }

        private void EmitStart(string id, int total)
            => WriteLine($"{{\"id\":{J(id)},\"type\":\"wallpaper\",\"action\":\"start\",\"total_entries\":{total}}}");

        private void EmitDone(string id)
            => WriteLine($"{{\"id\":{J(id)},\"type\":\"wallpaper\",\"action\":\"done\"}}");

        private void EmitError(string id, string entry, string msg)
            => WriteLine($"{{\"id\":{J(id)},\"type\":\"error\",\"entry\":{J(entry)},\"msg\":{J(msg)}}}");
    }
}
