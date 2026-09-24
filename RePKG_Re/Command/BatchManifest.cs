using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using RePKG_Re.Application.Package;
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

        /// <summary>"extract"(默认,拆包成文件) | "mpkg"(整包转移动包) | "pkg"(移动包转回 PC 包)。三者执行器不同。</summary>
        public string Mode { get; set; }

        public bool IsMpkg => string.Equals(Mode, "mpkg", StringComparison.OrdinalIgnoreCase);

        public bool IsPkg => string.Equals(Mode, "pkg", StringComparison.OrdinalIgnoreCase);

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
                Mode = LegacyJson.AsString(GetProp(root, "mode")),
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
                        Output = LegacyJson.AsString(GetProp(item, "output")),
                        OutputName = LegacyJson.AsString(GetProp(item, "outputName"))
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
                    FilterEffectImages = LegacyJson.AsInt(GetProp(o, "filterEffectImages")) ?? 0,
                    MpkgMagic = LegacyJson.AsString(GetProp(o, "mpkgMagic")),
                    KeepAudio = LegacyJson.AsBool(GetProp(o, "keepAudio")) ?? false,
                    NoLz4 = LegacyJson.AsBool(GetProp(o, "noLz4")) ?? false,
                    MpkgReduction = LegacyJson.AsInt(GetProp(o, "mpkgReduction")) ?? 1,
                    MpkgEtc2 = LegacyJson.AsBool(GetProp(o, "mpkgEtc2")) ?? false,
                    MpkgNoShaderCompat = LegacyJson.AsBool(GetProp(o, "mpkgNoShaderCompat")) ?? false,
                    PkgMagic = LegacyJson.AsString(GetProp(o, "pkgMagic")),
                    NoDematerialize = LegacyJson.AsBool(GetProp(o, "noDematerialize")) ?? false,
                    KeepReductionKey = LegacyJson.AsBool(GetProp(o, "keepReductionKey")) ?? false
                };
            }

            return manifest;
        }

        private static JsonElement? GetProp(JsonElement? root, string name) => LegacyJson.GetProp(root, name);

        private void Validate()
        {
            if (!string.IsNullOrEmpty(Mode) && !IsMpkg && !IsPkg && !Mode.Equals("extract", StringComparison.OrdinalIgnoreCase))
            {
                Console.Error.WriteLine($"Invalid manifest: unknown mode \"{Mode}\" (expected extract|mpkg|pkg)");
                Environment.Exit(1);
            }

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

        /// <summary>manifest 选项 → 转包选项。project.json/preview.gif 由 runner 按每个包的位置单独填。</summary>
        public MobilePackageOptions ToMobileOptions()
        {
            var o = Options ?? new BatchOptionsModel();
            return new MobilePackageOptions
            {
                Magic = string.IsNullOrWhiteSpace(o.MpkgMagic) ? "PKGM0019" : o.MpkgMagic,
                DropAudio = !o.KeepAudio,
                UseLz4 = !o.NoLz4,
                Reduction = o.MpkgReduction switch
                {
                    < 1 => throw new ArgumentException($"mpkgReduction 必须 >= 1（1=不缩，WE 的下拉只有 1/2/4），当前 {o.MpkgReduction}"),
                    _ => o.MpkgReduction
                },
                // 编码只在缩过之后才有意义（÷1 那条路是逐字节验过的形态），不缩又开编码一定是配错了
                EncodeEtc2 = !o.MpkgEtc2
                    ? false
                    : o.MpkgReduction > 1
                        ? true
                        : throw new ArgumentException("mpkgEtc2 只在 mpkgReduction > 1 时有意义（÷1 发的是已验过的 RGBA8 形态）"),
                ShaderCompat = !o.MpkgNoShaderCompat
            };
        }

        /// <summary>manifest 选项 → mpkg→pc 逆向选项。project.json/preview 与正向同理由 runner 定位。</summary>
        public PcPackageOptions ToPcOptions()
        {
            var o = Options ?? new BatchOptionsModel();
            return new PcPackageOptions
            {
                Magic = string.IsNullOrWhiteSpace(o.PkgMagic) ? "PKGV0018" : o.PkgMagic,
                Dematerialize = !o.NoDematerialize,
                ClearTextureReduction = !o.KeepReductionKey
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

        /// <summary>
        /// 仅 mode = "mpkg" 生效:输出 .mpkg 的文件名主干(不含扩展名),让调用方按壁纸标题或创意工坊 ID 命名。
        /// 留空 = 用源包文件名。非法文件名字符由 repkg 清洗,不假定调用方 sanitize 过。
        /// </summary>
        public string OutputName { get; set; }
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

        // ---------- 以下仅 mode = "mpkg" 生效 ----------

        /// <summary>输出包魔数;留空 = PKGM0019(真机也接受 PKGM0016,WE 自己两种都发)</summary>
        public string MpkgMagic { get; set; }

        /// <summary>true = 保留 sounds/*.mp3。默认丢弃:移动端不消费壁纸音频(2/2 真机复现)</summary>
        public bool KeepAudio { get; set; }

        /// <summary>true = 物化出的 RGBA8 不试 LZ4(排查压缩侧问题时用)</summary>
        public bool NoLz4 { get; set; }

        /// <summary>纹理缩小除数,对应 WE "纹理缩小"下拉的 原始/2×/4×。1=不缩(默认)</summary>
        public int MpkgReduction { get; set; } = 1;

        /// <summary>true = 缩小过的物化纹理发 ETC2 RGBA8(fmt5)而不是 RGBA8。默认关:这套字节还没上真机</summary>
        public bool MpkgEtc2 { get; set; }

        /// <summary>true = 关掉着色器兼容改写(整数字面量不补 ".0")。默认开:不改写时手机编译失败、材质回退成基础贴图,画面就是一块白</summary>
        public bool MpkgNoShaderCompat { get; set; }

        // ---------- 以下仅 mode = "pkg"(mpkg→pc 逆向)生效 ----------

        /// <summary>输出 PC 包魔数;留空 = PKGV0018(实测 WE PC 场景包用这个)</summary>
        public string PkgMagic { get; set; }

        /// <summary>true = 不把物化 RGBA8 重编码回 PNG 直通 blob,原样搬运(排查逆向时用)</summary>
        public bool NoDematerialize { get; set; }

        /// <summary>true = 保留 scene.json 的 texturereduction 键(默认删,与正向成对)</summary>
        public bool KeepReductionKey { get; set; }
    }
}
