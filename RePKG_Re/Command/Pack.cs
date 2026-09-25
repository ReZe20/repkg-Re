using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using RePKG_Re.Application.Package;
using RePKG_Re.Core.Json;
using RePKG_Re.Core.Texture;

namespace RePKG_Re.Command
{
    /// <summary>pack 的选项(命令行 pack 命令与 batch mode=pack 共用同一套语义)。</summary>
    public class PackOptions
    {
        /// <summary>要打包的目录：壁纸工程目录本身，或装着多个工程目录的父目录。</summary>
        public string Input { get; set; }

        /// <summary>输出目录(放 .pkg 与同级的 project.json/预览图)，留空 = ./output</summary>
        public string OutputDirectory { get; set; }

        /// <summary>输出文件名主干，留空 = 工程目录名</summary>
        public string Name { get; set; }

        /// <summary>输出包魔数，留空 = PKGV0018</summary>
        public string Magic { get; set; }

        /// <summary>目标已存在时覆盖；不覆盖也不是报错 —— 另加序号写新文件</summary>
        public bool Overwrite { get; set; }

        /// <summary>连源图一起进包(默认只带 .tex，与真实 WE 包一致)</summary>
        public bool KeepSourceImages { get; set; }

        /// <summary>关掉"源图 → 直通 .tex"，源图原样进包</summary>
        public bool NoEncodeImages { get; set; }

        /// <summary>不把 project.json/预览图作为同级 loose 文件写到输出目录</summary>
        public bool NoLooseMetadata { get; set; }

        /// <summary>额外排除的相对路径前缀(逗号分隔)</summary>
        public string ExcludePaths { get; set; }

        /// <summary>
        /// 源图块编码目标(DXT1/DXT3/DXT5)，留空 = 默认直通形态。
        /// 与 NoEncodeImages 同时给出时 --no-tex-encode 赢(直通和块编码同属"编码"，关了就都不做)。
        /// 字符串入口见 <see cref="ParseDxt"/>。
        /// </summary>
        public TexFormat? EncodeDxt { get; set; }

        public LoosePackageOptions ToBuilderOptions()
        {
            return new LoosePackageOptions
            {
                Magic = string.IsNullOrWhiteSpace(Magic) ? "PKGV0018" : Magic.Trim(),
                EncodeImages = !NoEncodeImages,
                KeepSourceImages = KeepSourceImages,
                ExcludePaths = string.IsNullOrWhiteSpace(ExcludePaths) ? null : ExcludePaths.Split(','),
                EncodeDxtFormat = EncodeDxt
            };
        }

        /// <summary>命令行 --dxt 与 manifest "packDxt" 共用的解析。非法值抛 ArgumentException，调用方不得吞成"没填"。</summary>
        public static TexFormat? ParseDxt(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return null;
            switch (value.Trim().ToLowerInvariant())
            {
                case "dxt1": case "1": return TexFormat.DXT1;
                case "dxt3": case "3": return TexFormat.DXT3;
                case "dxt5": case "5": return TexFormat.DXT5;
                default:
                    throw new ArgumentException($"dxt 目标只认 dxt1/dxt3/dxt5，给的是 '{value}'");
            }
        }
    }

    /// <summary>
    /// pack 命令入口：把一个壁纸工程目录(编辑器里的那些散文件)打回 .pkg。
    ///
    /// 输入两种都给得通：目录自己带 project.json → 只打它一个；不带 → 它的一级子目录里那些带
    /// project.json 的各自打一个(整份 myprojects 一把梭)。再往里递归是故意不做的：
    /// 效果包里有 <c>preview/project.json</c> 这种子目录条目，递归会把它当成一张新壁纸。
    /// </summary>
    public class Pack
    {
        public static void Action(PackOptions options)
        {
            if (string.IsNullOrEmpty(options.Input))
            {
                Console.WriteLine("Input directory required");
                return;
            }

            var projects = ProjectDirs.Resolve(options.Input);
            if (projects.Count == 0)
            {
                Console.WriteLine($"No wallpaper project found (a directory holding project.json or scene.json): {options.Input}");
                return;
            }

            var output = string.IsNullOrEmpty(options.OutputDirectory)
                ? Path.Combine(Directory.GetCurrentDirectory(), "output")
                : options.OutputDirectory;
            Directory.CreateDirectory(output);

            var runner = new PackRunner(options, 1);
            foreach (var project in projects)
            {
                // 单个输入时 -n 才有意义；一把梭多个工程时共用一个名字会全撞进同一个文件
                var stem = projects.Count > 1 || string.IsNullOrEmpty(options.Name) ? null : options.Name;
                runner.PackOne(project, output, stem, projects.Count > 1, Console.WriteLine, Console.Error.WriteLine);
            }

            Console.WriteLine("Done");
        }
    }

    /// <summary>工程目录判据见 <see cref="LoosePackageBuilder.IsProjectDir"/>(判据只放一份)。</summary>
    internal static class ProjectDirs
    {
        public static List<string> Resolve(string input)
        {
            var full = Path.GetFullPath(input);
            if (!Directory.Exists(full)) return new List<string>();

            if (LoosePackageBuilder.IsProjectDir(full))
                return new List<string> { full };

            return Directory.EnumerateDirectories(full)
                .Where(LoosePackageBuilder.IsProjectDir)
                .OrderBy(d => d, StringComparer.Ordinal)
                .ToList();
        }
    }

    /// <summary>
    /// mode=pack 的执行器：一个工程目录 = 一个 .pkg。
    ///
    /// 事件协议与 extract/mpkg/pkg 完全一致(wallpaper start/done + entry + error)，前端的进度、
    /// 崩溃重启、跳过逻辑不用改。壁纸级并发锁 2：打包时一条纹理整份进内存再落盘，
    /// 沿用 extract 的"物理核数"默认值会让几百 MB 的大条目按核数倍数驻留。
    /// </summary>
    public class PackRunner
    {
        private const int MaxWallpaperConcurrency = 2;

        private readonly PackOptions _options;
        private readonly int _threads;

        public PackRunner(PackOptions options, int threads)
        {
            _options = options ?? new PackOptions();
            _threads = Math.Max(1, Math.Min(threads, MaxWallpaperConcurrency));
        }

        public void Run(List<BatchWallpaper> wallpapers)
        {
            var po = new System.Threading.Tasks.ParallelOptions { MaxDegreeOfParallelism = _threads };
            System.Threading.Tasks.Parallel.ForEach(wallpapers, po, PackWallpaper);
        }

        private void PackWallpaper(BatchWallpaper wallpaper)
        {
            var projects = ProjectDirs.Resolve(wallpaper.Input);

            if (projects.Count == 0)
            {
                EmitError(wallpaper.Id, wallpaper.Input, "No wallpaper project found (a directory holding project.json or scene.json)");
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

            var produced = new List<string>();
            var cursor = new ProgressCursor();
            var oneProject = projects.Count == 1;

            foreach (var project in projects)
            {
                var projectName = ProjectName(project);

                try
                {
                    var target = PackOne(
                        project,
                        wallpaper.Output,
                        MpkgRunner.ResolveStem(wallpaper, projectName, !oneProject),
                        !oneProject,
                        Console.WriteLine,
                        msg => EmitError(wallpaper.Id, projectName, msg),
                        (index, total, entry) =>
                        {
                            if (!cursor.Started)
                            {
                                cursor.Started = true;
                                EmitStart(wallpaper.Id, total, projectName);
                            }
                            EmitEntry(wallpaper.Id, cursor, total, entry);
                        });

                    if (target != null) produced.Add(Path.GetFileName(target));
                }
                catch (Exception e)
                {
                    EmitError(wallpaper.Id, projectName, e.Message);
                }
            }

            Console.WriteLine(
                $"{{\"id\":{J(wallpaper.Id)},\"type\":\"wallpaper\",\"action\":\"done\",\"converted\":{J(string.Join("|", produced))}}}");
        }

        /// <summary>
        /// 打一个工程目录，返回写出的 .pkg 全路径。
        /// <paramref name="progress"/>(条目序号, 条目总数, 条目名) 由打包器在写每一条之前回调，
        /// 第一条就是"总数已知"的时刻 —— start 事件因此能带准确分母，又不用先走一遍目录。
        /// </summary>
        public string PackOne(
            string projectDir,
            string outputDir,
            string nameStem,
            bool multiple,
            Action<string> note,
            Action<string> warn,
            Action<int, int, string> progress = null)
        {
            var projectName = ProjectName(projectDir);
            var stem = string.IsNullOrWhiteSpace(nameStem) ? projectName : nameStem;
            var target = UniquePath(outputDir, stem);

            var report = new LoosePackageBuilder().Build(projectDir, target, _options.ToBuilderOptions(),
                (index, total, name) =>
                {
                    if (Program.Closing) Environment.Exit(0);
                    progress?.Invoke(index, total, name);
                });

            if (!_options.NoLooseMetadata)
                CopyMetadata(report.MetadataFiles, outputDir, warn);

            note?.Invoke(
                $"* pkg {projectName} → {Path.GetFileName(target)}  " +
                $"条目 {report.Entries} 封纹理 {report.Encoded} 搬运 {report.Copied} 排除 {report.Dropped}  " +
                $"{report.InputBytes}→{report.OutputBytes}B");

            // 例行播报走 stdout 注释行，不进事件协议(与 MpkgRunner 的着色器明细同一口径)：
            // 前端只解析以 '{' 开头的行，而报错通道的语义是"每一条都是错"，
            // 把"源图交给同名的 .tex"这种正常动作塞进去会让一次成功看起来像失败。
            foreach (var line in report.Notes.Take(60))
                note?.Invoke($"*   {line}");
            if (report.Notes.Count > 60)
                note?.Invoke($"*   …另有 {report.Notes.Count - 60} 条排除明细省略");

            foreach (var line in report.Warnings)
                warn?.Invoke(line);

            return target;
        }

        /// <summary>
        /// 重名就不覆盖，另加序号：scene.pkg → scene_1.pkg → scene_2.pkg…
        /// 理由是把输出目录指到工坊订阅目录时，同名原件是 Steam 下载来的东西，
        /// 静默覆盖之后 WE 校验文件哈希会认为内容损坏并整份重下。
        /// </summary>
        private string UniquePath(string outputDir, string stem)
        {
            var candidate = Path.Combine(outputDir, stem.GetSafeFilename() + ".pkg");
            if (_options.Overwrite || !File.Exists(candidate)) return candidate;

            for (var i = 1; i < 1000; i++)
            {
                candidate = Path.Combine(outputDir, $"{stem.GetSafeFilename()}_{i}.pkg");
                if (!File.Exists(candidate)) return candidate;
            }

            throw new InvalidOperationException($"{outputDir} 里叫 {stem}*.pkg 的文件太多了");
        }

        /// <summary>
        /// project.json 与预览图作为同级 loose 文件拷过去(实测工坊目录就是这个布局)。
        /// 已存在就不覆盖：一个输出目录里打多张壁纸时(整份 myprojects 一把梭就是这个形状)，
        /// 每张都有一份叫 project.json 的元数据，后一张会把前一张的静默盖掉 —— 而这两份内容不同，
        /// 盖完前那张的包就带着别人的标题/标签/属性了。宁可留一份 + 报一句。
        /// </summary>
        private static void CopyMetadata(List<string> sources, string outputDir, Action<string> warn)
        {
            foreach (var source in sources)
            {
                var target = Path.Combine(outputDir, Path.GetFileName(source));

                // 输出目录就是工程目录时源与目标是同一个文件，拷自己只会把时间戳弄脏
                if (string.Equals(Path.GetFullPath(source), Path.GetFullPath(target), StringComparison.OrdinalIgnoreCase))
                    continue;

                try
                {
                    if (File.Exists(target))
                    {
                        warn?.Invoke($"同级已有 {Path.GetFileName(target)}，没覆盖(一张壁纸一个输出目录才拿得齐这套文件)");
                        continue;
                    }

                    File.Copy(source, target);
                }
                catch (Exception e)
                {
                    warn?.Invoke($"同级 {Path.GetFileName(target)} 没拷过去: {e.Message}");
                }
            }
        }

        private static string ProjectName(string path) =>
            Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));

        private sealed class ProgressCursor
        {
            public bool Started;
            public int Pos;
        }

        private static string J(string s) => LegacyJson.QuoteString(s);

        private static void EmitStart(string id, int total, string name)
            => Console.WriteLine(
                $"{{\"id\":{J(id)},\"type\":\"wallpaper\",\"action\":\"start\",\"total_entries\":{total},\"name\":{J(name)}}}");

        private static void EmitDone(string id)
            => Console.WriteLine($"{{\"id\":{J(id)},\"type\":\"wallpaper\",\"action\":\"done\"}}");

        private static void EmitError(string id, string entry, string msg)
            => Console.WriteLine($"{{\"id\":{J(id)},\"type\":\"error\",\"entry\":{J(entry)},\"msg\":{J(msg)}}}");

        private static void EmitEntry(string id, ProgressCursor cursor, int total, string entry)
        {
            cursor.Pos++;
            // 与 MpkgRunner 同一口径：分母是这一张壁纸当前这个工程的条目数，多工程时每条 done 前重新起算
            var pos = Math.Min(cursor.Pos, total);
            Console.WriteLine(
                $"{{\"id\":{J(id)},\"type\":\"entry\",\"entry\":{J(entry)},\"pos\":{pos},\"total\":{total}}}");
        }
    }
}
