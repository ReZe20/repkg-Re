using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using RePKG_Re.Application.Package;
using RePKG_Re.Core.Json;
using RePKG_Re.Core.Package;

namespace RePKG_Re.Command
{
    /// <summary>
    /// mode=mpkg 执行器。与 extract 的执行器不通用：打包必须按表序单遍写文件并回填偏移，
    /// 条目之间不能交给不同线程，所以并行度落在"壁纸"这一层（一个壁纸 = 一个输出文件）。
    ///
    /// 内存画像：每个 worker 同时只驻留一个条目的字节，其中物化一条 RGBA8 的上限是
    /// Constants.MaximumMipmapByteCount(250MB)，再算上 LZ4 临时缓冲。因此壁纸级并发默认锁 2 ——
    /// 沿用 extract 的"物理核数"默认值会直接按核数倍数吃显存级内存。
    ///
    /// 事件协议与 extract/batch 完全一致(id 打点的 wallpaper start/done + entry + error)，
    /// WE Tool 侧的进度、重启、跳过逻辑无需改动。
    /// </summary>
    public class MpkgRunner
    {
        private const int MaxWallpaperConcurrency = 2;

        private readonly MobilePackageOptions _options;
        private readonly List<BatchWallpaper> _wallpapers;
        private readonly int _threads;
        private readonly bool _overwrite;

        public MpkgRunner(MobilePackageOptions options, List<BatchWallpaper> wallpapers, int threads, bool overwrite)
        {
            _options = options;
            _wallpapers = wallpapers;
            _threads = Math.Max(1, Math.Min(threads, MaxWallpaperConcurrency));
            _overwrite = overwrite;
        }

        public void Run()
        {
            var po = new System.Threading.Tasks.ParallelOptions {MaxDegreeOfParallelism = _threads};
            System.Threading.Tasks.Parallel.ForEach(_wallpapers, po, ConvertWallpaper);
        }

        private void ConvertWallpaper(BatchWallpaper wallpaper)
        {
            var packages = ResolvePackages(wallpaper.Input);

            if (packages.Count == 0)
            {
                EmitError(wallpaper.Id, wallpaper.Input, "No .pkg files found in input");
                EmitDone(wallpaper.Id);
                return;
            }

            int total;
            try
            {
                total = packages.Sum(p => CountEntries(p));
            }
            catch (Exception e)
            {
                EmitError(wallpaper.Id, wallpaper.Input, $"Parse pkg table failed: {e.Message}");
                EmitDone(wallpaper.Id);
                return;
            }

            if (total == 0)
            {
                EmitError(wallpaper.Id, wallpaper.Input, "No entries found");
                EmitDone(wallpaper.Id);
                return;
            }

            try
            {
                Directory.CreateDirectory(wallpaper.Output);
            }
            catch (Exception e)
            {
                EmitError(wallpaper.Id, wallpaper.Output, $"Create output directory failed: {e.Message}");
                EmitDone(wallpaper.Id);
                return;
            }

            EmitStart(wallpaper.Id, total);

            var converted = new List<string>();
            var cursor = new ProgressCursor {Total = total};

            foreach (var pkg in packages)
            {
                try
                {
                    ConvertPackage(wallpaper, pkg, cursor, packages.Count > 1);
                    converted.Add(pkg.FullName);
                }
                catch (Exception e)
                {
                    EmitError(wallpaper.Id, pkg.FullName, e.Message);
                }
            }

            Console.WriteLine(
                $"{{\"id\":{J(wallpaper.Id)},\"type\":\"wallpaper\",\"action\":\"done\",\"converted\":{J(string.Join("|", converted.Select(Path.GetFileName)))}}}");
        }

        private void ConvertPackage(BatchWallpaper wallpaper, FileInfo pkg, ProgressCursor cursor, bool multiple)
        {
            var target = Path.Combine(wallpaper.Output, ResolveStem(wallpaper, pkg, multiple) + ".mpkg");

            if (!_overwrite && File.Exists(target))
            {
                // 跳过也要把这一包的进度推满，否则 WE Tool 的 pos 永远到不了 total
                var skipped = CountEntries(pkg);
                for (var i = 0; i < skipped; i++)
                    EmitEntry(wallpaper.Id, cursor, pkg.Name);

                Console.WriteLine($"* Skipping, already exists: {target}");
                return;
            }

            var options = new MobilePackageOptions
            {
                Magic = _options.Magic,
                DropAudio = _options.DropAudio,
                UseLz4 = _options.UseLz4,
                Reduction = _options.Reduction,
                EncodeEtc2 = _options.EncodeEtc2,
                ShaderCompat = _options.ShaderCompat,
                ProjectJsonPath = FindLoose(pkg, "project.json"),
                PreviewPath = FindPreview(pkg)
            };

            var converter = new MobilePackageConverter();

            var report = converter.Convert(pkg.FullName, target, options, (index, ofPackage, entry) =>
            {
                if (Program.Closing)
                    Environment.Exit(0);

                EmitEntry(wallpaper.Id, cursor, entry);
            });

            // 缩了就必须交代场景文件里那个键：纹理缩了但键没写进去，手机侧的行为是未知的
            var reduced = options.Reduction > 1
                ? $" 缩小 {report.Reduced}(÷{options.Reduction}) 场景键 {(report.ReductionRecorded ? "已写" : "未写")}" +
                  (options.EncodeEtc2 ? $" fmt5 {report.Etc2Encoded}" : "") +
                  (report.FramesScaled > 0 ? $" 帧表 {report.FramesScaled}" : "")
                : "";

            // 这串必须无条件报：真机上一块白，要能分清是"改写没跑到"还是"改写不到位"，
            // 而 0 条本身就是读数 —— 不含整数字面量的着色器包不该被改动。
            var compat = options.ShaderCompat
                ? $" 着色器改写 {report.ShadersRewritten}条/{report.ShaderLiterals}处"
                : " 着色器改写 关";

            Console.WriteLine(
                $"* mpkg {pkg.Name} → {Path.GetFileName(target)}  " +
                $"{report.InputBytes}→{report.OutputBytes}B 条目 {report.Entries} 物化 {report.Materialized} " +
                $"搬运 {report.Copied} 丢弃 {report.Dropped}{reduced}{compat}");

            // 逐文件明细走 stdout 注释行，不进事件协议：前端只解析带 '{' 的行，
            // 而报错通道 report.Warnings 的语义是"该做的没做成"，例行播报混进去会让成功看起来像失败。
            foreach (var rewrite in report.Rewrites)
                Console.WriteLine($"*   着色器兼容改写 {rewrite}");

            foreach (var warning in report.Warnings)
                EmitError(wallpaper.Id, pkg.Name, warning);
        }

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
            name = new string(chars).Trim();

            return name.Length == 0
                ? source
                : multiple ? $"{name}_{source}" : name;
        }

        /// <summary>条目级进度游标（lambda 里不能碰 ref 参数，所以用可变态对象承载）。</summary>
        private sealed class ProgressCursor
        {
            public int Total;
            public int Pos;
        }

        private static void EmitEntry(string id, ProgressCursor cursor, string entry)
        {
            cursor.Pos++;
            // total 只数了输入包里的条目,而 project.json/preview.gif 是打包时新增的条目,
            // 分母不知道它们 —— 不截的话 pos/total 会报到 113%(UI 侧的百分比越过上界)
            var pos = Math.Min(cursor.Pos, cursor.Total);
            Console.WriteLine(
                $"{{\"id\":{J(id)},\"type\":\"entry\",\"entry\":{J(entry)},\"pos\":{pos},\"total\":{cursor.Total}}}");
        }

        private static List<FileInfo> ResolvePackages(string input)
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

        private static int CountEntries(FileInfo pkg)
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

        private static void EmitStart(string id, int total)
            => Console.WriteLine($"{{\"id\":{J(id)},\"type\":\"wallpaper\",\"action\":\"start\",\"total_entries\":{total}}}");

        private static void EmitDone(string id)
            => Console.WriteLine($"{{\"id\":{J(id)},\"type\":\"wallpaper\",\"action\":\"done\"}}");

        private static void EmitError(string id, string entry, string msg)
            => Console.WriteLine($"{{\"id\":{J(id)},\"type\":\"error\",\"entry\":{J(entry)},\"msg\":{J(msg)}}}");
    }
}
