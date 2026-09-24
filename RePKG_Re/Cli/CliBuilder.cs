using System;
using System.CommandLine;
using RePKG_Re.Command;

namespace RePKG_Re.Cli
{
    /// <summary>
    /// System.CommandLine 2.0 命令树定义(2026-09-09 AOT 迁移,替代 CommandLineParser 2.9.1)。
    /// 选项名/短名/默认值 与旧特性 1:1 对照(WE Tool 传参面禁改);
    /// handler 只负责把 ParseResult 组装成 Options DTO 并调用既有 Action,业务逻辑零改动。
    /// 默认值语义:一律由 handler 的 ?? 兜底(OutputDirectory/./output、SortBy/"name" 等,与旧
    /// [Option Default] 一一对应);其中 ./output 与 name 两处**另外**声明了 DefaultValueFactory,
    /// 因为 help 只在选项带默认值工厂时才打印 (DEFAULT: ...) —— 同一个值写两处,改动必须同步。
    /// helpName 只影响帮助里 &lt;...&gt; 占位符的显示单位,不参与解析。
    /// </summary>
    internal static class CliBuilder
    {
        public static RootCommand BuildRoot()
        {
            var root = new RootCommand("RePKG - Wallpaper Engine pkg extractor / tex converter");

            root.Add(BuildExtract());
            root.Add(BuildInfo());
            root.Add(BuildBatch());
            root.Add(BuildPack());

            return root;
        }

        /// <summary>
        /// pack:壁纸工程的散文件 → .pkg(extract 的反向)。
        /// 排除规则写在 LoosePackageBuilder 的类注释里(判据是本地 279 个真实包的条目普查),
        /// 这里只暴露开关,不在命令行上重复那套规则。
        /// </summary>
        private static System.CommandLine.Command BuildPack()
        {
            var cmd = new System.CommandLine.Command("pack",
                "Pack a wallpaper project directory (loose files) back into a PKG");

            var input = new Argument<string>("input")
            {
                Description = "Wallpaper project directory (the one holding project.json), or a parent of several"
            };
            cmd.Add(input);

            var output = Opt<string>("--output", "-o", "Directory for the produced .pkg", "DIR");
            output.DefaultValueFactory = _ => "./output";
            var name = Opt<string>("--name", "-n", "File name stem for the produced .pkg (default: project folder name)", "NAME");
            var magic = Opt<string>("--magic", null, "Package magic to write (default PKGV0018)", "MAGIC");
            var overwrite = Flag("--overwrite", null, "Overwrite an existing target PKG instead of writing name_1.pkg");
            var keepImages = Flag("--keep-source-images", null,
                "Also pack the source images alongside their .tex (real WE packages carry none)");
            var noEncode = Flag("--no-tex-encode", null,
                "Do not wrap source images into passthrough .tex; ship them as their own entries");
            var noLoose = Flag("--no-loose-metadata", null, "Do not copy project.json / preview next to the produced .pkg");
            var exclude = Opt<string>("--excludepaths", null,
                "Also skip these relative path prefixes (comma-delimited)", "PREFIXES");

            cmd.Add(output);
            cmd.Add(name);
            cmd.Add(magic);
            cmd.Add(overwrite);
            cmd.Add(keepImages);
            cmd.Add(noEncode);
            cmd.Add(noLoose);
            cmd.Add(exclude);

            cmd.SetAction(pr =>
            {
                Pack.Action(new PackOptions
                {
                    Input = pr.GetValue(input) ?? string.Empty,
                    OutputDirectory = pr.GetValue(output),
                    Name = pr.GetValue(name),
                    Magic = pr.GetValue(magic),
                    Overwrite = pr.GetValue(overwrite),
                    KeepSourceImages = pr.GetValue(keepImages),
                    NoEncodeImages = pr.GetValue(noEncode),
                    NoLooseMetadata = pr.GetValue(noLoose),
                    ExcludePaths = pr.GetValue(exclude)
                });
                return 0;
            });

            return cmd;
        }

        private static System.CommandLine.Command BuildExtract()
        {
            var cmd = new System.CommandLine.Command("extract", "Extract PKG files or convert TEX files into images");

            var input = new Argument<string>("input") { Description = "Path to a PKG/TEX file, or to a directory" };
            cmd.Add(input);

            var output = Opt<string>("--output", "-o", "Output directory", "DIR");
            // 默认值必须声明在这里,help 才会打印 (DEFAULT: ...);handler 里的 ?? 兜底保留,两处取值必须一致
            output.DefaultValueFactory = _ => "./output";
            var ignoreExts = Opt<string>("--ignoreexts", "-i",
                "Don't extract files with these extensions (comma-delimited)", "EXTS");
            var onlyExts = Opt<string>("--onlyexts", "-e",
                "Only extract files with these extensions (comma-delimited)", "EXTS");
            var outputIgnoreExts = Opt<string>("--output-ignoreexts", "-I",
                "Don't write files with these extensions (parsing and TEX conversion still run)", "EXTS");
            var outputOnlyExts = Opt<string>("--output-onlyexts", "-E",
                "Only write files with these extensions (parsing and TEX conversion still run)", "EXTS");
            var tex = Flag("--tex", "-t", "Convert all TEX files into images from the directory given as input");
            var singleDir = Flag("--singledir", "-s", "Put all extracted files in one directory instead of their entry path");
            var recursive = Flag("--recursive", "-r", "Search all subfolders of the specified directory");
            var copyProject = Flag("--copyproject", "-c", "Copy project.json and preview.jpg from beside the PKG into output");
            var useName = Flag("--usename", "-n", "Use the title in project.json as the subfolder name instead of the id");
            var noTexConvert = Flag("--no-tex-convert", null, "Don't convert TEX files into images while extracting PKG");
            var onlyTexImages = Flag("--only-tex-images", "-p",
                "Skip raw .tex output, keep only converted images (.tex-json still written)");
            var filterEffectImages = Opt<double>("--filter-effect-images", null,
                "Skip entries whose converted image is mostly transparent or black (threshold 1-100, 0 = off)", "PERCENT");
            var onlyPaths = Opt<string>("--onlypaths", null,
                "Only extract entries under these directory prefixes (comma-delimited, subfolders included)", "PREFIXES");
            var ignorePaths = Opt<string>("--ignorepaths", null,
                "Don't extract entries under these directory prefixes (comma-delimited, subfolders included)", "PREFIXES");
            var pathsDepth = Opt<int>("--paths-depth", null,
                "Limit --onlypaths/--ignorepaths to N segments below the prefix (1 = direct children, 0 = unlimited)", "N");
            var overwrite = Flag("--overwrite", null, "Overwrite all existing files");
            var lazy = Flag("--lazy", null, "Read entries one by one instead of loading the whole package into memory");
            var maxEntrySize = Opt<long>("--max-entry-size", null, "Skip entries larger than this size in KB", "KB");
            var minEntrySize = Opt<long>("--min-entry-size", null, "Skip entries smaller than this size in KB", "KB");

            cmd.Add(output);
            cmd.Add(ignoreExts);
            cmd.Add(onlyExts);
            cmd.Add(outputIgnoreExts);
            cmd.Add(outputOnlyExts);
            cmd.Add(tex);
            cmd.Add(singleDir);
            cmd.Add(recursive);
            cmd.Add(copyProject);
            cmd.Add(useName);
            cmd.Add(noTexConvert);
            cmd.Add(onlyTexImages);
            cmd.Add(filterEffectImages);
            cmd.Add(onlyPaths);
            cmd.Add(ignorePaths);
            cmd.Add(pathsDepth);
            cmd.Add(overwrite);
            cmd.Add(lazy);
            cmd.Add(maxEntrySize);
            cmd.Add(minEntrySize);

            cmd.SetAction(pr =>
            {
                Extract.Action(new ExtractOptions
                {
                    Input = pr.GetValue(input) ?? string.Empty,
                    OutputDirectory = pr.GetValue(output) ?? "./output",
                    IgnoreExts = pr.GetValue(ignoreExts),
                    OnlyExts = pr.GetValue(onlyExts),
                    OutputIgnoreExts = pr.GetValue(outputIgnoreExts),
                    OutputOnlyExts = pr.GetValue(outputOnlyExts),
                    TexDirectory = pr.GetValue(tex),
                    SingleDir = pr.GetValue(singleDir),
                    Recursive = pr.GetValue(recursive),
                    CopyProject = pr.GetValue(copyProject),
                    UseName = pr.GetValue(useName),
                    NoTexConvert = pr.GetValue(noTexConvert),
                    OnlyTexImages = pr.GetValue(onlyTexImages),
                    FilterEffectImages = pr.GetValue(filterEffectImages),
                    OnlyPaths = pr.GetValue(onlyPaths),
                    IgnorePaths = pr.GetValue(ignorePaths),
                    PathsDepth = pr.GetValue(pathsDepth),
                    Overwrite = pr.GetValue(overwrite),
                    Lazy = pr.GetValue(lazy),
                    MaxEntrySize = pr.GetValue(maxEntrySize),
                    MinEntrySize = pr.GetValue(minEntrySize)
                });
                return 0;
            });

            return cmd;
        }

        private static System.CommandLine.Command BuildInfo()
        {
            var cmd = new System.CommandLine.Command("info", "Dump PKG/TEX info");

            var input = new Argument<string>("input") { Description = "Path to the file or directory to inspect" };
            cmd.Add(input);

            var sort = Flag("--sort", "-s", "Sort entries a-z");
            var sortBy = Opt<string>("--sortby", "-b",
                "Sort entries by name, extension or size (unrecognized values fall back to name)", "KEY");
            // 同上:默认值要声明出来 help 才看得见,handler 的 ?? "name" 保留
            sortBy.DefaultValueFactory = _ => "name";
            var tex = Flag("--tex", "-t", "Dump info about all TEX files from the directory given as input");
            var projectInfo = Opt<string>("--projectinfo", "-p",
                "Keys to dump from project.json (comma-delimited, * for all)", "KEYS");
            var printEntries = Flag("--printentries", "-e", "Print entries in packages");
            var titleFilter = Opt<string>("--title-filter", null,
                "Only list packages whose project.json title contains this text (case-insensitive)", "TEXT");

            cmd.Add(sort);
            cmd.Add(sortBy);
            cmd.Add(tex);
            cmd.Add(projectInfo);
            cmd.Add(printEntries);
            cmd.Add(titleFilter);

            cmd.SetAction(pr =>
            {
                Info.Action(new InfoOptions
                {
                    Input = pr.GetValue(input) ?? string.Empty,
                    Sort = pr.GetValue(sort),
                    SortBy = pr.GetValue(sortBy) ?? "name",
                    TexDirectory = pr.GetValue(tex),
                    ProjectInfo = pr.GetValue(projectInfo),
                    PrintEntries = pr.GetValue(printEntries),
                    TitleFilter = pr.GetValue(titleFilter)
                });
                return 0;
            });

            return cmd;
        }

        private static System.CommandLine.Command BuildBatch()
        {
            var cmd = new System.CommandLine.Command("batch",
                "Extract wallpapers listed in a manifest file; errors are reported as JSON events and the batch continues");

            var manifest = new Option<string>("--manifest", new[] { "-m" })
            {
                Description = "Path to the manifest JSON file (schema: README, section batch)",
                Required = true,
                HelpName = "FILE"
            };
            cmd.Add(manifest);
            // 原描述"0 = CPU core count"与实现不符:0 是"沿用 manifest 里的 threads",
            // 两处都为 0 才取物理核数(Batch.cs:39),且命令行优先于 manifest
            var threads = Opt<int>("--threads", "-t",
                "Max worker threads; overrides the manifest value (0 = follow the manifest)", "N");
            cmd.Add(threads);

            cmd.SetAction(pr =>
            {
                Batch.Action(new BatchOptions
                {
                    Manifest = pr.GetValue(manifest) ?? string.Empty,
                    Threads = pr.GetValue(threads)
                });
                return 0;
            });

            return cmd;
        }

        // ---- 选项构建辅助(统一短名别名/描述;默认值由 handler ?? 兜底) ----
        // helpName:帮助里 <...> 占位符写的单位(默认会复读选项名,--max-entry-size <max-entry-size> 那种),
        // 只给带值的选项设;开关(bool)不显示占位符,一律传 null。
        private static Option<T> Opt<T>(string name, string alias, string description, string helpName = null)
        {
            var opt = new Option<T>(name, alias == null ? Array.Empty<string>() : new[] { alias })
            {
                Description = description
            };
            if (helpName != null) opt.HelpName = helpName;
            return opt;
        }

        private static Option<bool> Flag(string name, string alias, string description)
        {
            return new Option<bool>(name, alias == null ? Array.Empty<string>() : new[] { alias })
            {
                Description = description
            };
        }
    }
}
