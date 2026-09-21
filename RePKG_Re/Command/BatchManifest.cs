using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using RePKG_Re.Core.Json;

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
                // JObject 手写解析:NativeAOT 下 Newtonsoft 反射式构造器发现不可用
                // (GetConstructors 返回空,[JsonConstructor] 也无济于事——AOT 裁剪裁掉了构造器元数据,
                //  2026-09-09 实测)。manifest 结构固定,手写零反射、AOT 天然安全。
                manifest = Parse(File.ReadAllText(path));
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

        private static BatchManifest Parse(string json)
        {
            // 结构固定、零反射,NativeAOT 天然安全(见 Load 里那段历史说明);取键一律大小写不敏感,
            // 与旧 Newtonsoft 的属性匹配语义一致,实现收在 LegacyJson.GetProp。
            var root = LegacyJson.Parse(json) ?? throw new JsonException("Manifest is not a valid JSON object.");

            var manifest = new BatchManifest
            {
                Threads = LegacyJson.AsInt(GetProp(root, "threads")) ?? 0,
                Wallpapers = new List<BatchWallpaper>(),
                Options = null
            };

            if (GetProp(root, "wallpapers") is { ValueKind: JsonValueKind.Array } wallpapers)
            {
                foreach (var item in wallpapers.EnumerateArray())
                {
                    manifest.Wallpapers.Add(new BatchWallpaper
                    {
                        Id = LegacyJson.AsString(GetProp(item, "id")),
                        Input = LegacyJson.AsString(GetProp(item, "input")),
                        Output = LegacyJson.AsString(GetProp(item, "output"))
                    });
                }
            }

            if (GetProp(root, "options") is { ValueKind: JsonValueKind.Object } o)
            {
                manifest.Options = new BatchOptionsModel
                {
                    Overwrite = LegacyJson.AsBool(GetProp(o, "overwrite")) ?? false,
                    OnlyPaths = LegacyJson.ToStringArray(GetProp(o, "onlypaths")),
                    IgnorePaths = LegacyJson.ToStringArray(GetProp(o, "ignorepaths")),
                    PathsDepth = LegacyJson.AsInt(GetProp(o, "pathsDepth")) ?? 0,
                    OnlyExts = LegacyJson.ToStringArray(GetProp(o, "onlyexts")),
                    IgnoreExts = LegacyJson.ToStringArray(GetProp(o, "ignoreexts")),
                    OutputOnlyExts = LegacyJson.ToStringArray(GetProp(o, "outputOnlyExts")),
                    OutputIgnoreExts = LegacyJson.ToStringArray(GetProp(o, "outputIgnoreExts")),
                    KeepSubfolderStructure = LegacyJson.AsBool(GetProp(o, "keepSubfolderStructure")) ?? false,
                    NoTexConvert = LegacyJson.AsBool(GetProp(o, "noTexConvert")) ?? false,
                    OnlyTexImages = LegacyJson.AsBool(GetProp(o, "onlyTexImages")) ?? false,
                    FilterEffectImages = LegacyJson.AsInt(GetProp(o, "filterEffectImages")) ?? 0
                };
            }

            return manifest;
        }

        private static JsonElement? GetProp(JsonElement? root, string name) => LegacyJson.GetProp(root, name);

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

        /// <summary>
        /// 输入路径,兼容文件与目录:单个 .pkg/.mpkg 文件 → 只拆该文件;
        /// 目录 → 递归枚举目录内所有 pkg/mpkg 一并拆出。
        /// </summary>
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
