using System;
using CommandLine;

namespace RePKG_Re.Command
{
    /// <summary>
    /// extract 命令入口。所有提取逻辑已迁至 <see cref="ExtractContext"/>;
    /// 本类保持静态无状态,与 batch 命令各自持有独立上下文,互不污染。
    /// </summary>
    public static class Extract
    {
        public static void Action(ExtractOptions options)
        {
            var ctx = new ExtractContext(options);
            ctx.Run();
        }
    }

    [Verb("extract", HelpText = "Extract PKG/Convert TEX into image.")]
    public class ExtractOptions
    {
        [Option('o', "output", Required = false, HelpText = "Output directory", Default = "./output")]
        public string OutputDirectory { get; set; }

        [Option('i', "ignoreexts", HelpText =
            "Don't extract files with specified extensions (delimited by comma \",\")")]
        public string IgnoreExts { get; set; }

        [Option('e', "onlyexts", HelpText = "Only extract files with specified extensions (delimited by comma \",\")")]
        public string OnlyExts { get; set; }

        [Option('I', "output-ignoreexts", HelpText =
            "Don't write files with specified extensions (delimited by comma \",\"). " +
            "Output-level filter: entries are still parsed (TEX converted), skipped when writing. " +
            "TEX converted images are judged by their converted extension.")]
        public string OutputIgnoreExts { get; set; }

        [Option('E', "output-onlyexts", HelpText =
            "Only write files with specified extensions (delimited by comma \",\"). " +
            "Output-level filter: entries are still parsed (TEX converted), skipped when writing. " +
            "TEX converted images are judged by their converted extension.")]
        public string OutputOnlyExts { get; set; }

        [Option('t', "tex", HelpText = "Convert all tex files into images from specified directory in input")]
        public bool TexDirectory { get; set; }

        [Option('s', "singledir", HelpText =
            "Should all extracted files be put in one directory instead of their entry path")]
        public bool SingleDir { get; set; }

        [Option('r', "recursive", HelpText = "Recursive search in all subfolders of specified directory")]
        public bool Recursive { get; set; }

        [Option('c', "copyproject", HelpText =
            "Copy project.json and preview.jpg from beside PKG into output directory")]
        public bool CopyProject { get; set; }

        [Option('n', "usename", HelpText = "Use name from project.json as project subfolder name instead of id")]
        public bool UseName { get; set; }

        [Option("no-tex-convert", HelpText = "Don't convert TEX files into images while extracting PKG")]
        public bool NoTexConvert { get; set; }

        [Option('p', "only-tex-images", HelpText = "Only output converted TEX images; skip saving raw .tex files")]
        public bool OnlyTexImages { get; set; }

        [Option("filter-effect-images", HelpText =
            "Skip entries whose converted image is mostly transparent or black (effect images). " +
            "Value = threshold percent (1-100), e.g. 85 = skip when transparent OR black ratio >= 85%. 0 = off", Default = 0.0)]
        public double FilterEffectImages { get; set; }

        [Option("onlypaths", HelpText =
            "Only extract entries under the specified directory prefix(es) (delimited by comma \",\", " +
            "e.g. materials or materials/masks). Subfolders included; \\\\ and / both accepted")]
        public string OnlyPaths { get; set; }

        [Option("ignorepaths", HelpText =
            "Don't extract entries under the specified directory prefix(es) (delimited by comma \",\", " +
            "e.g. effects,sounds). Subfolders included; \\\\ and / both accepted")]
        public string IgnorePaths { get; set; }

        [Option("paths-depth", HelpText =
            "Limit --onlypaths/--ignorepaths to N path segments after the prefix " +
            "(1 = direct children only, subfolders excluded). 0 = unlimited (default)", Default = 0)]
        public int PathsDepth { get; set; }

        [Option("overwrite", HelpText = "Overwrite all existing files")]
        public bool Overwrite { get; set; }

        [Option("lazy", HelpText = "Lazy/chunked mode: read entries one by one instead of loading all into memory")]
        public bool Lazy { get; set; }

        [Option("max-entry-size", HelpText = "Skip entries larger than specified size (KB)", Default = 0L)]
        public long MaxEntrySize { get; set; }

        [Option("min-entry-size", HelpText = "Skip entries smaller than specified size (KB)", Default = 0L)]
        public long MinEntrySize { get; set; }


        [Value(0, Required = true, HelpText = "Path to file/directory", MetaName = "Input")]
        public string Input { get; set; }
    }
}
