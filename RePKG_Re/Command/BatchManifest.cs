using System;
using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json;

namespace RePKG_Re.Command
{
    /// <summary>
    /// batch 清单:壁纸列表 + 全局提取选项(与 WE Tool BuildArgs 分支 1:1 对应)。
    /// 解析/校验失败 → stderr 错误 + exit 1(参数错误是唯一允许非 0 退出的路径)。
    /// </summary>
    public class BatchManifest
    {
        /// <summary>最大线程数(0 = CPU 核心数;Phase 1 生效)</summary>
        public int Threads { get; set; }

        public List<BatchWallpaper> Wallpapers { get; set; }

        public BatchOptionsModel Options { get; set; }

        public static BatchManifest Load(string path)
        {
            if (!File.Exists(path))
            {
                Console.Error.WriteLine($"Manifest file not found: {path}");
                Environment.Exit(1);
            }

            BatchManifest manifest;
            try
            {
                manifest = JsonConvert.DeserializeObject<BatchManifest>(File.ReadAllText(path));
            }
            catch (Exception e)
            {
                Console.Error.WriteLine($"Invalid manifest: {e.Message}");
                Environment.Exit(1);
                return null;
            }

            manifest.Validate();
            return manifest;
        }

        private void Validate()
        {
            if (Threads < 0)
            {
                Console.Error.WriteLine("Invalid manifest: threads must be >= 0");
                Environment.Exit(1);
            }

            if (Wallpapers == null || Wallpapers.Count == 0)
            {
                Console.Error.WriteLine("Invalid manifest: wallpapers list must not be empty");
                Environment.Exit(1);
            }

            foreach (var w in Wallpapers)
            {
                if (string.IsNullOrEmpty(w.Id) || string.IsNullOrEmpty(w.Input) || string.IsNullOrEmpty(w.Output))
                {
                    Console.Error.WriteLine("Invalid manifest: each wallpaper needs non-empty id/input/output");
                    Environment.Exit(1);
                }
            }
        }

        /// <summary>manifest 选项 → ExtractOptions。OutputDirectory 由批处理按壁纸单独指定。</summary>
        public ExtractOptions ToExtractOptions()
        {
            var o = Options ?? new BatchOptionsModel();
            return new ExtractOptions
            {
                OutputDirectory = "",
                IgnoreExts = Join(o.IgnoreExts),
                OnlyExts = Join(o.OnlyExts),
                OutputIgnoreExts = Join(o.OutputIgnoreExts),
                OutputOnlyExts = Join(o.OutputOnlyExts),
                OnlyPaths = Join(o.OnlyPaths),
                IgnorePaths = Join(o.IgnorePaths),
                PathsDepth = o.PathsDepth,
                SingleDir = o.KeepSubfolderStructure,
                NoTexConvert = o.NoTexConvert,
                OnlyTexImages = o.OnlyTexImages,
                Overwrite = o.Overwrite,
                FilterEffectImages = o.FilterEffectImages,
                Lazy = false // batch 使用自己的按需读取循环,lazy 选项无意义
            };
        }

        private static string Join(string[] array)
            => array == null || array.Length == 0 ? null : string.Join(",", array);
    }

    /// <summary>单个壁纸条目。Id 由调用方分配,repkg 原样回显到每个 JSON 事件。</summary>
    public class BatchWallpaper
    {
        public string Id { get; set; }
        public string Input { get; set; }
        public string Output { get; set; }
    }

    /// <summary>manifest 全局提取选项(映射到 ExtractOptions 的过滤/输出开关)。</summary>
    public class BatchOptionsModel
    {
        public bool Overwrite { get; set; }

        /// <summary>--onlypaths(解析前目录前缀过滤)</summary>
        public string[] OnlyPaths { get; set; }

        /// <summary>--ignorepaths(解析前目录前缀过滤)</summary>
        public string[] IgnorePaths { get; set; }

        /// <summary>--paths-depth(目录前缀深度限制,0 = 不限)</summary>
        public int PathsDepth { get; set; }

        /// <summary>-e/--onlyexts(解析前扩展名过滤)</summary>
        public string[] OnlyExts { get; set; }

        /// <summary>-i/--ignoreexts(解析前扩展名过滤)</summary>
        public string[] IgnoreExts { get; set; }

        /// <summary>-E/--output-onlyexts(输出层过滤)</summary>
        public string[] OutputOnlyExts { get; set; }

        /// <summary>-I/--output-ignoreexts(输出层过滤)</summary>
        public string[] OutputIgnoreExts { get; set; }

        /// <summary>-s/--singledir:true = 全部文件平铺进输出目录(WE Tool KeepSubfolderStructure==1)</summary>
        public bool KeepSubfolderStructure { get; set; }

        /// <summary>--no-tex-convert</summary>
        public bool NoTexConvert { get; set; }

        /// <summary>-p/--only-tex-images</summary>
        public bool OnlyTexImages { get; set; }

        /// <summary>--filter-effect-images(0 = 关,1-100 = 阈值)</summary>
        public int FilterEffectImages { get; set; }
    }
}
