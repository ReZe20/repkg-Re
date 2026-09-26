using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using RePKG_Re.Application.Package;
using RePKG_Re.Core.Json;
using RePKG_Re.Core.Texture;

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

        /// <summary>
        /// "extract"(默认,拆包成文件) | "mpkg"(整包转移动包) | "pkg"(移动包转回 PC 包) |
        /// "pack"(反过来:把壁纸工程的散文件打成 PC 包) | "inspect"(只读体检:一个字节都不写)。执行器各一套。
        /// </summary>
        public string Mode { get; set; }

        public bool IsMpkg => string.Equals(Mode, "mpkg", StringComparison.OrdinalIgnoreCase);

        public bool IsPkg => string.Equals(Mode, "pkg", StringComparison.OrdinalIgnoreCase);

        public bool IsPack => string.Equals(Mode, "pack", StringComparison.OrdinalIgnoreCase);

        /// <summary>只读探测:回答"这个档位在这张壁纸上到底会不会动手"，不产出任何文件。</summary>
        public bool IsInspect => string.Equals(Mode, "inspect", StringComparison.OrdinalIgnoreCase);

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
                        OutputName = LegacyJson.AsString(GetProp(item, "outputName")),
                        Options = ParseWallpaperOptions(GetProp(item, "options"))
                    });
                }
            }

            if (GetProp(root, "options") is { ValueKind: JsonValueKind.Object } o)
            {
                var presetName = LegacyJson.AsString(GetProp(o, "preset"));
                var hasPreset = !string.IsNullOrWhiteSpace(presetName);
                int presetReduction = 1;
                bool presetEtc2 = false;
                // 非法档位名是清单写错,Load 期就抛(调用方 Batch.Action 收成 Invalid manifest + 退出码 1),
                // 绝不能当成"没填"退回 1× —— 那会把一整批包按原始尺寸发出去。
                if (hasPreset) MpkgPresets.Require(presetName, out presetReduction, out presetEtc2, "options.preset");

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
                    SingleDir = ReadSingleDir(o),
                    NoTexConvert = LegacyJson.AsBool(GetProp(o, "noTexConvert")) ?? false,
                    OnlyTexImages = LegacyJson.AsBool(GetProp(o, "onlyTexImages")) ?? false,
                    FilterEffectImages = LegacyJson.AsInt(GetProp(o, "filterEffectImages")) ?? 0,
                    MpkgMagic = LegacyJson.AsString(GetProp(o, "mpkgMagic")),
                    KeepAudio = LegacyJson.AsBool(GetProp(o, "keepAudio")) ?? false,
                    NoLz4 = LegacyJson.AsBool(GetProp(o, "noLz4")) ?? false,
                    Preset = presetName,
                    // 预设先占缺省位,显式键覆盖它:写了 mpkgReduction/mpkgEtc2 就以它为准
                    MpkgReduction = LegacyJson.AsInt(GetProp(o, "mpkgReduction")) ?? (hasPreset ? presetReduction : 1),
                    MpkgEtc2 = LegacyJson.AsBool(GetProp(o, "mpkgEtc2")) ?? (hasPreset ? presetEtc2 : false),
                    MpkgNoShaderCompat = LegacyJson.AsBool(GetProp(o, "mpkgNoShaderCompat")) ?? false,
                    MpkgNoDematerialize = LegacyJson.AsBool(GetProp(o, "mpkgNoDematerialize")) ?? false,
                    MpkgShrinkDx = LegacyJson.AsBool(GetProp(o, "mpkgShrinkDx")) ?? false,
                    PkgMagic = LegacyJson.AsString(GetProp(o, "pkgMagic")),
                    NoDematerialize = LegacyJson.AsBool(GetProp(o, "noDematerialize")) ?? false,
                    KeepReductionKey = LegacyJson.AsBool(GetProp(o, "keepReductionKey")) ?? false,
                    PackKeepSourceImages = LegacyJson.AsBool(GetProp(o, "packKeepSourceImages")) ?? false,
                    PackNoEncode = LegacyJson.AsBool(GetProp(o, "packNoEncode")) ?? false,
                    PackNoLooseMetadata = LegacyJson.AsBool(GetProp(o, "packNoLooseMetadata")) ?? false,
                    // 非法值在 Load 期就炸(与 mpkgReduction 同一口径):这是清单写错了,不是运行期偶发错误
                    PackDxt = PackOptions.ParseDxt(LegacyJson.AsString(GetProp(o, "packDxt"))),
                    PackExcludePaths = LegacyJson.ToStringArray(GetProp(o, "packExcludePaths"))
                };
            }

            return manifest;
        }

        private static JsonElement? GetProp(JsonElement? root, string name) => LegacyJson.GetProp(root, name);

        /// <summary>
        /// wallpapers[].options 的读取。整段缺省(或不是对象)返回 null = 这条完全回落全局 options;
        /// 写了的键才覆盖,所以这里一律走可空 As*,不做 ?? 折默认。
        /// </summary>
        private static BatchWallpaperOptions ParseWallpaperOptions(JsonElement? node)
            => node is { ValueKind: JsonValueKind.Object } o
                ? new BatchWallpaperOptions
                {
                    Preset = LegacyJson.AsString(GetProp(o, "preset")),
                    MpkgMagic = LegacyJson.AsString(GetProp(o, "mpkgMagic")),
                    KeepAudio = LegacyJson.AsBool(GetProp(o, "keepAudio")),
                    NoLz4 = LegacyJson.AsBool(GetProp(o, "noLz4")),
                    MpkgReduction = LegacyJson.AsInt(GetProp(o, "mpkgReduction")),
                    MpkgEtc2 = LegacyJson.AsBool(GetProp(o, "mpkgEtc2")),
                    MpkgNoShaderCompat = LegacyJson.AsBool(GetProp(o, "mpkgNoShaderCompat")),
                    MpkgNoDematerialize = LegacyJson.AsBool(GetProp(o, "mpkgNoDematerialize")),
                    MpkgShrinkDx = LegacyJson.AsBool(GetProp(o, "mpkgShrinkDx"))
                }
                : null;

        /// <summary>
        /// 读平铺开关:正名键 "singleDir"(与 -s/--singledir 同名同义)优先;
        /// 历史键 "keepSubfolderStructure" 名字与行为相反(true = 压平),保留兼容但走 stderr 弃用警告
        /// (batch 的 stdout 只允许 JSON 事件,警告必须进 stderr)。两键都给时 singleDir 获胜。
        /// </summary>
        private static bool ReadSingleDir(JsonElement options)
        {
            var modern = LegacyJson.AsBool(GetProp(options, "singleDir"));
            var legacy = LegacyJson.AsBool(GetProp(options, "keepSubfolderStructure"));

            if (modern is not null && legacy is not null)
                Console.Error.WriteLine("Warning: manifest options has both singleDir and keepSubfolderStructure;" +
                                        " singleDir wins (" + modern + "). keepSubfolderStructure is deprecated" +
                                        " and its name is the opposite of what it does.");
            else if (legacy is not null)
                Console.Error.WriteLine("Warning: manifest option keepSubfolderStructure is deprecated and does the" +
                                        " opposite of its name (true = flatten into one directory);" +
                                        " use singleDir instead.");

            return modern ?? legacy ?? false;
        }

        private void Validate()
        {
            if (!string.IsNullOrEmpty(Mode) && !IsMpkg && !IsPkg && !IsPack && !IsInspect &&
                !Mode.Equals("extract", StringComparison.OrdinalIgnoreCase))
            {
                Console.Error.WriteLine($"Invalid manifest: unknown mode \"{Mode}\" (expected extract|mpkg|pkg|pack|inspect)");
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
                // mode:inspect 一个字节都不写，所以它不要求 output —— 让调用方编一个用不到的目录，
                // 只会让人以为探测会往那里放东西。
                var needed = IsInspect ? (string.IsNullOrEmpty(w.Id) || string.IsNullOrEmpty(w.Input))
                                       : (string.IsNullOrEmpty(w.Id) || string.IsNullOrEmpty(w.Input) || string.IsNullOrEmpty(w.Output));
                if (needed)
                {
                    Console.Error.WriteLine("Invalid manifest: each wallpaper needs non-empty id/input/output");
                    Environment.Exit(1);
                }
            }

            // mode:mpkg 的选项校验按条目跑一遍:条目级覆盖能拼出全局看不到的非法组合(比如某行 etc2 开着、
            // 解析后的 reduction 却是 1),这类必须在动手前退,不能跑到那张壁纸才炸。
            // mode:inspect 共用同一份 ToMobileOptions，所以它也走这道校验：探测和转换读的是同一套档位，
            // 档位本身非法时两边都不该给出可信的数字。
            if (IsMpkg || IsInspect)
            {
                foreach (var w in Wallpapers)
                {
                    try
                    {
                        ToMobileOptions(w);
                    }
                    catch (ArgumentException e)
                    {
                        Console.Error.WriteLine($"Invalid manifest: {e.Message}");
                        Environment.Exit(1);
                    }
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
                SingleDir = o.SingleDir,
                NoTexConvert = o.NoTexConvert,
                OnlyTexImages = o.OnlyTexImages,
                Overwrite = o.Overwrite,
                FilterEffectImages = o.FilterEffectImages,
                Lazy = false // batch 使用自己的按需读取循环,lazy 选项无意义
            };
        }

        /// <summary>manifest 选项 → 转包选项(全局口径)。project.json/preview.gif 由 runner 按每个包的位置单独填。</summary>
        public MobilePackageOptions ToMobileOptions() => ToMobileOptions(null);

        /// <summary>
        /// 全局 options 套上这条壁纸自己的覆盖项(wallpapers[].options)。
        /// 校验留在这里而不是挪到调用方:Validate 与 runner 因此共用同一份口径,
        /// 不会出现"清单校验放过、跑到第 7 张才炸"。
        /// </summary>
        public MobilePackageOptions ToMobileOptions(BatchWallpaper wallpaper)
        {
            var o = Options ?? new BatchOptionsModel();
            var w = wallpaper?.Options;

            // 只有条目里"写了"的键才覆盖;字符串键按非空白判定,空串等于没写
            var magic = !string.IsNullOrWhiteSpace(w?.MpkgMagic) ? w.MpkgMagic : o.MpkgMagic;
            var keepAudio = w?.KeepAudio ?? o.KeepAudio;
            var noLz4 = w?.NoLz4 ?? o.NoLz4;
            var noShaderCompat = w?.MpkgNoShaderCompat ?? o.MpkgNoShaderCompat;
            var noDematerialize = w?.MpkgNoDematerialize ?? o.MpkgNoDematerialize;
            var shrinkDx = w?.MpkgShrinkDx ?? o.MpkgShrinkDx;
            var who = wallpaper is null ? "" : $"（壁纸 {wallpaper.Id}）";

            // 条目级 preset 只填这条没写死的两格(全局那份在 Load 期已经折进 o.MpkgReduction/o.MpkgEtc2)。
            // 顺序 = 显式键 > 条目预设 > 全局(含全局预设) > 缺省。
            var explicitReduction = w?.MpkgReduction;
            var explicitEtc2 = w?.MpkgEtc2;
            if (!string.IsNullOrWhiteSpace(w?.Preset))
            {
                MpkgPresets.Require(w.Preset, out var presetReduction, out var presetEtc2,
                    "wallpapers[].options.preset", who);
                explicitReduction ??= presetReduction;
                explicitEtc2 ??= presetEtc2;
            }
            var reduction = explicitReduction ?? o.MpkgReduction;
            var etc2 = explicitEtc2 ?? o.MpkgEtc2;

            return new MobilePackageOptions
            {
                Magic = string.IsNullOrWhiteSpace(magic) ? "PKGM0019" : magic,
                DropAudio = !keepAudio,
                UseLz4 = !noLz4,
                Reduction = reduction switch
                {
                    < 1 => throw new ArgumentException($"mpkgReduction 必须 >= 1（1=不缩，WE 的下拉只有 1/2/4），当前 {reduction}{who}"),
                    _ => reduction
                },
                // 编码只在缩过之后才有意义（÷1 那条路是逐字节验过的形态），不缩又开编码一定是配错了
                EncodeEtc2 = !etc2
                    ? false
                    : reduction > 1
                        ? true
                        : throw new ArgumentException($"mpkgEtc2 只在 mpkgReduction > 1 时有意义（÷1 发的是已验过的 RGBA8 形态）{who}"),
                ShaderCompat = !noShaderCompat,
                // 这两条一律不做"必须 mpkgReduction > 1"的交叉校验，和上面的 mpkgEtc2 不同：
                // 它们不会与档位组成非法产物（关物化只是不出缩过的像素，ShrinkDx 在下层有 Reduction > 1 前提），
                // 而清单里全局写一次、个别条目改档位的写法很常见，报错会让整批退不出去。
                // "关了物化又要求缩小"那种自相矛盾的清单由转换器出 error 级警告兜底。
                Dematerialize = !noDematerialize,
                ShrinkDx = shrinkDx
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

        /// <summary>
        /// manifest 选项 → 打包(mode=pack)选项。输出文件名主干走每条壁纸自己的 outputName，
        /// 与 mode=mpkg 同一套机制，所以这里没有 name 字段。
        /// </summary>
        public PackOptions ToPackOptions()
        {
            var o = Options ?? new BatchOptionsModel();
            return new PackOptions
            {
                Magic = string.IsNullOrWhiteSpace(o.PkgMagic) ? "PKGV0018" : o.PkgMagic,
                Overwrite = o.Overwrite,
                KeepSourceImages = o.PackKeepSourceImages,
                NoEncodeImages = o.PackNoEncode,
                NoLooseMetadata = o.PackNoLooseMetadata,
                EncodeDxt = o.PackDxt,
                ExcludePaths = o.PackExcludePaths == null || o.PackExcludePaths.Length == 0
                    ? null
                    : string.Join(",", o.PackExcludePaths)
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
        /// mode = "pack" 时语义反过来:输入必须是**壁纸工程目录**(里面直接放着 project.json),
        /// 或装着多个工程目录的父目录(此时每个子目录各出一个包),不递归到第三层。
        /// </summary>
        public string Input { get; set; }

        public string Output { get; set; }

        /// <summary>
        /// 仅 mode = "mpkg"/"pkg"/"pack" 生效:输出包的文件名主干(不含扩展名),让调用方按壁纸标题或创意工坊 ID 命名。
        /// 留空 = 用源包文件名(pack 没有源包,留空 = 用工程目录名)。非法文件名字符由 repkg 清洗,不假定调用方 sanitize 过。
        /// </summary>
        public string OutputName { get; set; }

        /// <summary>
        /// 条目级覆盖(wallpapers[].options),目前只对 mode="mpkg" 生效;缺省 = 整条回落全局 options。
        /// 存在的意义:打包口径是每批一份,前端想给每张壁纸不同档位就得一批一起跑,而不是按档切成多批。
        /// </summary>
        public BatchWallpaperOptions Options { get; set; }
    }

    /// <summary>
    /// mpkg 的三档预设。这条"除数 + 由它派生要不要编 ETC2"的规则以前在两端各写一份 —— 调用方拿它派生参数、
    /// repkg 拿它做校验，规则一变就得同步改两处，漏一边是静默错包。挪进来之后只此一处。
    /// 预设只是**默认值的来源**：同一格写了显式键（mpkgReduction / mpkgEtc2）就以显式键为准，
    /// 否则"2× 档但我不想要 ETC2"这种组合就表达不出来了。
    /// </summary>
    public static class MpkgPresets
    {
        /// <summary>档位名 → (除数, ETC2)。大小写不敏感，界面里的乘号 '×' 也当 'x' 收。</summary>
        public static bool TryResolve(string name, out int reduction, out bool etc2)
        {
            reduction = 1;
            etc2 = false;
            switch ((name ?? "").Trim().Replace('×', 'x').ToLowerInvariant())
            {
                case "1x": reduction = 1; etc2 = false; return true;   // 原始档：逐字节对齐真机包的形态
                case "2x": reduction = 2; etc2 = true; return true;
                case "4x": reduction = 4; etc2 = true; return true;
                default: return false;
            }
        }

        /// <summary>非法名抛 ArgumentException —— 清单写错了，不许当成"没填"静默按 1× 跑。</summary>
        public static void Require(string name, out int reduction, out bool etc2, string key, string who = "")
        {
            if (TryResolve(name, out reduction, out etc2)) return;
            throw new ArgumentException($"{key} 只认 1x/2x/4x（WE 的 原始/2×/4× 三档），给的是 '{name}'{who}");
        }
    }

    /// <summary>
    /// wallpapers[].options 的稀疏模型:一律可空,"没写"和"写了 false/1"必须分得开,
    /// 所以不复用 BatchOptionsModel(那边在解析期就用 ?? 折成默认值了)。
    /// 键名与全局 options 完全一致,只有 mode:mpkg 那几个。
    /// </summary>
    public class BatchWallpaperOptions
    {
        /// <summary>档位预设(1x/2x/4x)，展开成 mpkgReduction + mpkgEtc2 的默认值；写了那两个键的以键为准。</summary>
        public string Preset { get; set; }
        public string MpkgMagic { get; set; }
        public bool? KeepAudio { get; set; }
        public bool? NoLz4 { get; set; }
        public int? MpkgReduction { get; set; }
        public bool? MpkgEtc2 { get; set; }
        public bool? MpkgNoShaderCompat { get; set; }
        public bool? MpkgNoDematerialize { get; set; }
        public bool? MpkgShrinkDx { get; set; }
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

        /// <summary>
        /// -s/--singledir:true = 全部文件平铺进输出目录。
        /// manifest 键 "singleDir";历史键 "keepSubfolderStructure"(WE Tool 传来,名字与行为相反)
        /// 仍可读但已弃用,见 ReadSingleDir。
        /// </summary>
        public bool SingleDir { get; set; }

        /// <summary>--no-tex-convert</summary>
        public bool NoTexConvert { get; set; }

        /// <summary>-p/--only-tex-images</summary>
        public bool OnlyTexImages { get; set; }

        /// <summary>--filter-effect-images(0 = 关,1-100 = 阈值)</summary>
        public int FilterEffectImages { get; set; }

        // ---------- 以下仅 mode = "mpkg" 生效 ----------

        /// <summary>
        /// 档位预设(1x/2x/4x)。解析期就展开进 MpkgReduction/MpkgEtc2 的缺省值,
        /// 同一份 options 里写了那两个键的以键为准 —— 预设管默认,显式键管覆盖。
        /// </summary>
        public string Preset { get; set; }

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

        /// <summary>true = 所有 .tex 逐字节照搬,不物化成 RGBA8/ETC2。出"只改容器、像素不动"的对照包用。
        /// 与逆向的 noDematerialize 同名同义,带 mpkg 前缀只是因为它读自 mode:mpkg 那一组。</summary>
        public bool MpkgNoDematerialize { get; set; }

        /// <summary>true = DXT 块格式也解码重缩(输出仍是 RGBA8)。默认只有同时开 mpkgEtc2 才走这条路,
        /// 因为那条形路的字节真机验过;不开的话 DXT 密集的包无论缩几倍都一字节不动。</summary>
        public bool MpkgShrinkDx { get; set; }

        // ---------- 以下仅 mode = "pkg"(mpkg→pc 逆向)生效 ----------

        /// <summary>输出 PC 包魔数;留空 = PKGV0018(实测 WE PC 场景包用这个)</summary>
        public string PkgMagic { get; set; }

        /// <summary>true = 不把物化 RGBA8 重编码回 PNG 直通 blob,原样搬运(排查逆向时用)</summary>
        public bool NoDematerialize { get; set; }

        /// <summary>true = 保留 scene.json 的 texturereduction 键(默认删,与正向成对)</summary>
        public bool KeepReductionKey { get; set; }

        // ---------- 以下仅 mode = "pack"(工程目录 → PC 包)生效 ----------

        /// <summary>true = 源图也一起进包(默认只带 .tex)。排查"包里为什么没有我那张图"时才开。</summary>
        public bool PackKeepSourceImages { get; set; }

        /// <summary>true = 不把源图封成直通 .tex，源图原样进包。</summary>
        public bool PackNoEncode { get; set; }

        /// <summary>true = 不把 project.json/预览图作为同级 loose 文件写到输出目录。</summary>
        public bool PackNoLooseMetadata { get; set; }

        /// <summary>dxt1/dxt3/dxt5 = 源图块编码(见 pack --dxt)；null = 默认直通形态。load 期已解析成枚举。</summary>
        public TexFormat? PackDxt { get; set; }

        /// <summary>额外排除的相对路径前缀(如 "samplemedia,docs")。</summary>
        public string[] PackExcludePaths { get; set; }
    }
}
