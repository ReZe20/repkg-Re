using System;
using System.CommandLine;
using RePKG_Re.Command;

namespace RePKG_Re.Cli
{
    /// <summary>
    /// System.CommandLine 2.0 命令树定义(2026-09-09 AOT 迁移,替代 CommandLineParser 2.9.1)。
    /// 选项名/短名/默认值 与旧特性 1:1 对照(WE Tool 传参面禁改);
    /// handler 只负责把 ParseResult 组装成 Options DTO 并调用既有 Action,业务逻辑零改动。
    /// 默认值语义:Option 不设默认值,缺省时 GetValue 返回 null/0,由 handler 的 ?? 兜底
    /// (OutputDirectory/./output、SortBy/"name" 等,与旧 [Option Default] 一一对应)。
    /// </summary>
    internal static class CliBuilder
    {
        public static RootCommand BuildRoot()
        {
            var root = new RootCommand("RePKG - Wallpaper Engine pkg extractor / tex converter");

            root.Add(BuildExtract());
            root.Add(BuildInfo());
            root.Add(BuildBatch());

            return root;
        }

        private static System.CommandLine.Command BuildExtract()
        {
            var cmd = new System.CommandLine.Command("extract", "Extract PKG/Convert TEX into image.");

            var input = new Argument<string>("input") { Description = "Path to file/directory" };
            cmd.Add(input);

            var output = Opt<string>("--output", "-o", "Output directory");
            var ignoreExts = Opt<string>("--ignoreexts", "-i",
                "Don't extract files with specified extensions (delimited by comma \",\")");
            var onlyExts = Opt<string>("--onlyexts", "-e",
                "Only extract files with specified extensions (delimited by comma \",\")");
            var outputIgnoreExts = Opt<string>("--output-ignoreexts", "-I",
                "Don't write files with specified extensions (delimited by comma \",\"). " +
                "Output-level filter: entries are still parsed (TEX converted), skipped when writing. " +
                "TEX converted images are judged by their converted extension.");
            var outputOnlyExts = Opt<string>("--output-onlyexts", "-E",
                "Only write files with specified extensions (delimited by comma \",\"). " +
                "Output-level filter: entries are still parsed (TEX converted), skipped when writing. " +
                "TEX converted images are judged by their converted extension.");
            var tex = Flag("--tex", "-t", "Convert all tex files into images from specified directory in input");
            var singleDir = Flag("--singledir", "-s", "Should all extracted files be put in one directory instead of their entry path");
            var recursive = Flag("--recursive", "-r", "Recursive search in all subfolders of specified directory");
            var copyProject = Flag("--copyproject", "-c", "Copy project.json and preview.jpg from beside PKG into output directory");
            var useName = Flag("--usename", "-n", "Use name from project.json as project subfolder name instead of id");
            var noTexConvert = Flag("--no-tex-convert", null, "Don't convert TEX files into images while extracting PKG");
            var onlyTexImages = Flag("--only-tex-images", "-p", "Only output converted TEX images; skip saving raw .tex files");
            var filterEffectImages = Opt<double>("--filter-effect-images", null,
                "Skip entries whose converted image is mostly transparent or black (effect images). " +
                "Value = threshold percent (1-100), e.g. 85 = skip when transparent OR black ratio >= 85%. 0 = off");
            var onlyPaths = Opt<string>("--onlypaths", null,
                "Only extract entries under the specified directory prefix(es) (delimited by comma \",\", " +
                "e.g. materials or materials/masks). Subfolders included; \\\\ and / both accepted");
            var ignorePaths = Opt<string>("--ignorepaths", null,
                "Don't extract entries under the specified directory prefix(es) (delimited by comma \",\", " +
                "e.g. effects,sounds). Subfolders included; \\\\ and / both accepted");
            var pathsDepth = Opt<int>("--paths-depth", null,
                "Limit --onlypaths/--ignorepaths to N path segments after the prefix " +
                "(1 = direct children only, subfolders excluded). 0 = unlimited (default)");
            var overwrite = Flag("--overwrite", null, "Overwrite all existing files");
            var lazy = Flag("--lazy", null, "Lazy/chunked mode: read entries one by one instead of loading all into memory");
            var maxEntrySize = Opt<long>("--max-entry-size", null, "Skip entries larger than specified size (KB)");
            var minEntrySize = Opt<long>("--min-entry-size", null, "Skip entries smaller than specified size (KB)");

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
            var cmd = new System.CommandLine.Command("info", "Dumps PKG/TEX info.");

            var input = new Argument<string>("input") { Description = "Path to file which you want to get info about" };
            cmd.Add(input);

            var sort = Flag("--sort", "-s", "Sort entries a-z");
            var sortBy = Opt<string>("--sortby", "-b", "Sort by ... (available options: name, extension, size)");
            var tex = Flag("--tex", "-t", "Dump info about all tex files from specified directory");
            var projectInfo = Opt<string>("--projectinfo", "-p", "Keys to dump from project.json (delimit using comma) (* for all)");
            var printEntries = Flag("--printentries", "-e", "Print entries in packages");
            var titleFilter = Opt<string>("--title-filter", null, "Title filter");

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
                "Extract multiple wallpapers from a manifest file. Errors are reported as JSON events; the batch continues.");

            var manifest = new Option<string>("--manifest", new[] { "-m" })
            {
                Description = "Path to manifest JSON file",
                Required = true
            };
            cmd.Add(manifest);
            var threads = Opt<int>("--threads", "-t", "Max worker threads (0 = CPU core count)");
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

        private static Option<T> Opt<T>(string name, string alias, string description)
        {
            return new Option<T>(name, alias == null ? Array.Empty<string>() : new[] { alias })
            {
                Description = description
            };
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
