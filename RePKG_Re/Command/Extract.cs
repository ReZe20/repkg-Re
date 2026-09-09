using System;

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

    public class ExtractOptions
    {
        public string OutputDirectory { get; set; }

        public string IgnoreExts { get; set; }

        public string OnlyExts { get; set; }

        public string OutputIgnoreExts { get; set; }

        public string OutputOnlyExts { get; set; }

        public bool TexDirectory { get; set; }

        public bool SingleDir { get; set; }

        public bool Recursive { get; set; }

        public bool CopyProject { get; set; }

        public bool UseName { get; set; }

        public bool NoTexConvert { get; set; }

        public bool OnlyTexImages { get; set; }

        public double FilterEffectImages { get; set; }

        public string OnlyPaths { get; set; }

        public string IgnorePaths { get; set; }

        public int PathsDepth { get; set; }

        public bool Overwrite { get; set; }

        public bool Lazy { get; set; }

        public long MaxEntrySize { get; set; }

        public long MinEntrySize { get; set; }


        public string Input { get; set; }
    }
}
