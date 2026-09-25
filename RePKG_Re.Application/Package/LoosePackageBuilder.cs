using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using RePKG_Re.Application.Texture;
using RePKG_Re.Core.Json;
using RePKG_Re.Core.Texture;

namespace RePKG_Re.Application.Package
{
    /// <summary>打包选项。</summary>
    public class LoosePackageOptions
    {
        /// <summary>输出包魔数。和 mode:pkg 用同一个默认值（PKGV0018），"一个 PC 包"只有一种形状。
        /// 实测本地 279 个真实包的分布是 PKGV0023 138 / 0024 62 / 0022 60 / 0021 12 / 0018 4 / 0019 2，
        /// 这几种之间条目表布局没有差异（偏移累计不变式 279/279 精确成立），所以取哪一个都能被 WE 读。</summary>
        public string Magic { get; set; } = "PKGV0018";

        /// <summary>true = 没有 .tex 兄弟的源图封成直通 .tex 进包；false = 源图原样进包(并上报)。</summary>
        public bool EncodeImages { get; set; } = true;

        /// <summary>true = 连源图一起进包(既有 .tex 也有 png/jpg)。默认关：真实 WE 包里一条源图都没有。</summary>
        public bool KeepSourceImages { get; set; }

        /// <summary>额外排除的相对路径前缀(与 --ignorepaths 同语义：前缀 + '/' 边界，大小写不敏感)。</summary>
        public string[] ExcludePaths { get; set; }

        /// <summary>直通 .tex 的头部标志位。WE 的图大多带 ClampUVs(实测 1172/1348)，默认跟它。</summary>
        public TexFlags ImageTexFlags { get; set; } = TexFlags.ClampUVs;

        /// <summary>
        /// 非 null = 源图优先走块编码(DXT1/DXT3/DXT5 + mip 链 + LZ4)，编不动(多帧 GIF、坏图)再回落直通。
        /// null = 维持默认形态:能直通的 PNG/JPEG 封直通 blob,其余原样进包并上报 —— 与真实包里
        /// 1348 条 PNG 直通的观测一致，块编码是显式 opt-in(pack --dxt)。
        /// </summary>
        public TexFormat? EncodeDxtFormat { get; set; }
    }

    public class LoosePackageReport
    {
        public int Entries { get; set; }
        /// <summary>源图 → 直通 .tex 的条数</summary>
        public int Encoded { get; set; }
        /// <summary>逐字节搬运的条数</summary>
        public int Copied { get; set; }
        /// <summary>被排除掉的文件数(源图/缓存/sidecar)</summary>
        public int Dropped { get; set; }
        public long InputBytes { get; set; }
        public long OutputBytes { get; set; }

        /// <summary>包外的同级元数据(project.json / 预览图)源文件路径，调用方按这个把文件拷到 .pkg 旁边。</summary>
        public List<string> MetadataFiles { get; } = new List<string>();

        /// <summary>"该做的没做成"，语义与 MobilePackageReport.Warnings 一致：每一条都是错，例行播报放别处。</summary>
        public List<string> Warnings { get; } = new List<string>();

        /// <summary>例行播报(排除了什么)。由调用方打成 stdout 注释行，不进事件协议。</summary>
        public List<string> Notes { get; } = new List<string>();
    }

    /// <summary>
    /// 壁纸工程目录(编辑器里的一份壁纸文件) → .pkg。
    ///
    /// 与另外两个打包器的关系：MobilePackageConverter 是 pkg→mpkg(改容器 + 物化纹理)，
    /// PcPackageConverter 是 mpkg→pkg(逆向)，这两个的输入都是<b>一个包</b>；
    /// 本类的输入是<b>一个目录</b>，条目从磁盘上的散文件来，所以没有"源条目表"可抄，
    /// 计划阶段改成一趟目录遍历。容器写入形态与那两个一致：
    /// [int32 8][magic][int32 条目数] + 每条 [int32 名字字节数][名字 UTF-8][int32 偏移][int32 长度]，
    /// 数据区紧跟表尾，偏移相对数据区起点，单遍写 + 回填，不攒整包。
    ///
    /// 【条目取舍的判据全部来自实测】扫本地 279 个真实包(8152 条 .tex + 17144 条 .json)得到的扩展名全集是：
    /// json / tex / frag / vert / mdl / mp3 / ttf / otf / wav / ogg / flac / ttc / gif，
    /// <b>没有</b> png、jpg、tga、obj、mtl、dxs、tex-json、pkg、mpkg、也<b>没有</b> project.json 和 preview.*。
    /// 于是排除规则就是照这份清单反着写：
    ///   - 源图：有 .tex 兄弟就丢掉源图(WE 包里只有 .tex)；没有就封成 .tex(见 <see cref="PassthroughTexBuilder"/>)。
    ///   - .obj/.mtl：有 .mdl 兄弟就丢掉(WE 把模型转成 .mdl 进包)；没有就原样带着并上报 —— 我们没有 .mdl 编码器。
    ///   - *.tex-json：WE 包里没有这种条目。工程目录里那份是导入设置(编辑器写的)或 repkg 的说明文件，都不该进包。
    ///   - *.dxs / shaders/blobsSM*：WE 的着色器编译缓存，包里没有。
    ///   - project.json 与预览图：包里没有，交给调用方作为同级 loose 文件(实测工坊订阅目录就是这个布局)。
    ///   - 目录里套着的 .pkg/.mpkg：绝不套第二个包进去(myprojects 下就有 340MB 的 scene.pkg 躺在解包目录里)。
    /// </summary>
    public class LoosePackageBuilder
    {
        private static readonly string[] ImageExtensions =
            { ".png", ".jpg", ".jpeg", ".bmp", ".tga", ".targ", ".gif", ".webp", ".tif", ".tiff" };

        private static readonly string[] ModelSourceExtensions = { ".obj", ".mtl" };

        private sealed class PlanItem
        {
            public string Name;
            public byte[] NameBytes;
            public string SourceFile;
            public byte[] Generated;
            public long RowFieldPosition;
        }

        /// <summary>
        /// 把一个目录打成 .pkg。outputPkgPath 是最终文件路径(重名消歧由调用方负责)。
        /// </summary>
        public LoosePackageReport Build(
            string projectDirectory,
            string outputPkgPath,
            LoosePackageOptions options,
            Action<int, int, string> entryProgress = null)
        {
            if (projectDirectory == null) throw new ArgumentNullException(nameof(projectDirectory));
            if (outputPkgPath == null) throw new ArgumentNullException(nameof(outputPkgPath));
            options ??= new LoosePackageOptions();

            var root = Path.GetFullPath(projectDirectory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (!Directory.Exists(root))
                throw new DirectoryNotFoundException($"Project directory not found: {root}");

            var report = new LoosePackageReport();
            var plan = BuildPlan(root, options, report);

            if (plan.Count == 0)
                throw new InvalidOperationException($"Nothing to pack in {root} (every file is editor cache / package metadata)");

            using (var output = new FileStream(outputPkgPath, FileMode.Create, FileAccess.ReadWrite, FileShare.None))
            {
                var magic = Encoding.ASCII.GetBytes(options.Magic);
                output.Write(BitConverter.GetBytes(magic.Length), 0, 4);
                output.Write(magic, 0, magic.Length);
                output.Write(BitConverter.GetBytes(plan.Count), 0, 4);

                foreach (var item in plan)
                {
                    var name = item.NameBytes;
                    output.Write(BitConverter.GetBytes(name.Length), 0, 4);
                    output.Write(name, 0, name.Length);
                    item.RowFieldPosition = output.Position;
                    output.Write(new byte[8], 0, 8); // 偏移 + 长度：回填
                }

                var dataStart = output.Position;
                if (dataStart > int.MaxValue)
                    throw new InvalidOperationException($"条目表超出 int32 寻址范围: {dataStart}");

                long offset = 0;
                for (var i = 0; i < plan.Count; i++)
                {
                    var item = plan[i];
                    entryProgress?.Invoke(i + 1, plan.Count, item.Name);

                    // 两种条目两条路：封出来的 .tex 只有内存里那份；磁盘上的原件一律流式搬运。
                    // 不 ReadAllBytes 是因为视频纹理那条目动辄几百 MB，攒完整包更是不可能。
                    long written;
                    if (item.Generated != null)
                    {
                        output.Write(item.Generated, 0, item.Generated.Length);
                        written = item.Generated.Length;
                        report.Encoded++;
                    }
                    else
                    {
                        written = AppendFile(output, item.SourceFile);
                        report.Copied++;
                    }

                    PatchRow(output, item.RowFieldPosition, offset, written);
                    offset += written;

                    report.Entries++;
                    report.OutputBytes += written;
                }

                output.SetLength(dataStart + offset);

                if (output.Length != dataStart + offset)
                    throw new InvalidOperationException("写完自检失败：文件大小 != 表尾 + Σ条目长度");
            }

            return report;
        }

        private List<PlanItem> BuildPlan(string root, LoosePackageOptions options, LoosePackageReport report)
        {
            var files = EnumerateFiles(root);
            var byName = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var file in files) byName[Relative(root, file)] = file;

            var metadata = PickMetadata(root, byName);
            foreach (var path in metadata) report.MetadataFiles.Add(path);

            if (Lookup(byName, "project.json") == null)
                report.Warnings.Add("目录里没有 project.json：包能照打，但 WE 认不出这张壁纸(它只从包的同级目录读这一份)");
            else
                WarnForNonPackageableWallpaperType(root, report);

            var plan = new List<PlanItem>(files.Count);
            var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var exclude = NormalizePrefixes(options.ExcludePaths);

            foreach (var diskPath in files)
            {
                var name = Relative(root, diskPath);
                var extension = Path.GetExtension(name);

                if (MatchesPrefix(exclude, name))
                {
                    report.Dropped++;
                    report.Notes.Add($"按 --excludepaths 排除 {name}");
                    continue;
                }

                if (IsPackageFile(extension))
                {
                    report.Dropped++;
                    report.Notes.Add($"不带已有包 {name}");
                    continue;
                }

                if (IsSidecar(name) || IsShaderCache(name) || metadata.Contains(diskPath, StringComparer.OrdinalIgnoreCase))
                {
                    report.Dropped++;
                    continue;
                }

                if (IsSourceImage(extension))
                {
                    var tex = ChangeExtension(name, ".tex");
                    var texOnDisk = Lookup(byName, tex);

                    if (texOnDisk != null)
                    {
                        // 同名 .tex 就是这张源图的已编形态(WE 自己产的，比我们再封一遍更接近原件)。
                        // 默认丢掉源图；--keep-source-images 时才把源图也带上，但绝不封出第二条同名条目 ——
                        // 条目序是 ordinal，"x.png" 排在 "x.tex" 前面，真去封就会把 WE 那份 .tex 挤成重名条目扔掉。
                        if (!options.KeepSourceImages)
                        {
                            report.Dropped++;
                            report.Notes.Add($"源图交给同名的 .tex {name}");
                            continue;
                        }

                        report.Notes.Add($"同时保留源图与 .tex {name}");
                    }
                    else if (options.EncodeImages)
                    {
                        var encoded = TryEncode(diskPath, name, SidecarFlags(byName, name, options), options, report);
                        if (encoded != null)
                        {
                            if (!used.Add(tex))
                            {
                                report.Warnings.Add($"条目名重复，跳过 {tex}");
                                continue;
                            }
                            report.InputBytes += encoded.Length; // 计的是封出来的字节，源图那一份不再计
                            plan.Add(new PlanItem { Name = tex, Generated = encoded });
                            continue;
                        }

                        // TryEncode 返回 null 的两条路:
                        //   - 直通形态都当不了(PNG/JPEG 之外)且 DXT 也没成 —— DXT 试过则 TryEncode 已报过失败原因,
                        //     没试过(DXT 关着)才由这里报"找不到先例",同一个文件不报两遍。
                        //   - 直通能封但封失败 —— TryEncode 已报"封 .tex 失败",这里不重复。
                        if (options.EncodeDxtFormat == null && !PassthroughTexBuilder.CanBuild(diskPath))
                        {
                            report.Warnings.Add($"这种源图格式在真实 WE 包里找不到先例，不封 .tex、原样进包 {name}");
                        }
                    }
                }

                if (IsModelSource(extension))
                {
                    // WE 把模型转成 .mdl 再进包(defaultprojects/audiophile 里 bars.obj/.mtl/.mdl 三件都在，
                    // 而真实包的条目表里只有 .mdl、零条 .obj/.mtl)。我们没有 .mdl 编码器，所以只能选"带原件 + 报一句"。
                    if (Lookup(byName, ChangeExtension(name, ".mdl")) != null)
                    {
                        if (!options.KeepSourceImages)
                        {
                            report.Dropped++;
                            report.Notes.Add($"模型交给同名的 .mdl {name}");
                            continue;
                        }
                    }
                    else
                    {
                        report.Warnings.Add($"没有 .mdl 版本，原始模型文件进包 {name}");
                    }
                }

                var length = new FileInfo(diskPath).Length;
                if (length > int.MaxValue)
                {
                    report.Warnings.Add($"条目超过 2GB，int32 长度字段装不下，跳过 {name}");
                    continue;
                }

                if (!used.Add(name))
                {
                    report.Warnings.Add($"条目名重复，跳过 {name}");
                    continue;
                }

                report.InputBytes += length;
                plan.Add(new PlanItem { Name = name, SourceFile = diskPath });
            }

            // 条目序：磁盘遍历的顺序取决于文件系统，打包两次得到两个字节流就没法比对。
            // 真实 WE 包的条目表看着是散序(2636878454 的第一条是 materials/masks/...)，
            // 所以这里定序只是为了可复现，不宣称与 WE 同序。
            plan.Sort((a, b) => string.CompareOrdinal(a.Name, b.Name));
            // 名字定完序再算字节：条目名要按 UTF-8 字节数记账(读侧就是按字节数读的)
            foreach (var item in plan) item.NameBytes = Encoding.UTF8.GetBytes(item.Name);
            return plan;
        }

        private static void PatchRow(Stream output, long rowFieldPosition, long offset, long length)
        {
            if (offset > int.MaxValue)
                throw new InvalidOperationException($"条目偏移超出 int32: {offset}");
            if (length > int.MaxValue)
                throw new InvalidOperationException($"条目长度超出 int32: {length}");

            var tail = output.Position;
            output.Seek(rowFieldPosition, SeekOrigin.Begin);
            output.Write(BitConverter.GetBytes((int) offset), 0, 4);
            output.Write(BitConverter.GetBytes((int) length), 0, 4);
            output.Seek(tail, SeekOrigin.Begin);
        }

        /// <summary>把一个磁盘文件原样接到输出流尾巴上，返回写出去的字节数。</summary>
        private static long AppendFile(Stream output, string path)
        {
            using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            input.CopyTo(output, 1 << 20);
            return input.Length;
        }

        /// <summary>
        /// 视频/网页壁纸打成 pkg 是"能出文件但 WE 不会用它"：实测 279 个真实包里没有任何 .mp4 条目
        /// (视频壁纸的 mp4 是与 project.json 同级的散文件，场景壁纸的视频是躺在 .tex 里的)，
        /// 所以一张 222MB 的视频壁纸能被打包成一个 WE 永远不加载的 222MB 容器。
        /// 这里只是报一句 —— 要不要打是调用方决定的，我们不替它否决。
        /// </summary>
        private static void WarnForNonPackageableWallpaperType(string root, LoosePackageReport report)
        {
            var project = Path.Combine(root, "project.json");
            if (!File.Exists(project)) return;

            try
            {
                var json = LegacyJson.Parse(File.ReadAllText(project));
                var file = LegacyJson.AsString(LegacyJson.GetProp(json, "file")) ?? "";
                foreach (var video in new[] { ".mp4", ".webm", ".mov", ".avi" })
                    if (file.EndsWith(video, StringComparison.OrdinalIgnoreCase))
                    {
                        report.Warnings.Add(
                            $"这是一张视频壁纸(file={file})：真实 WE 包里没有 .mp4 条目，打出来的包 WE 不会当壁纸用" +
                            "（要分享的直接把目录连同 mp4 一起拷走就行）");
                        return;
                    }
            }
            catch
            {
                // 清单读不动就不猜类型，交给"包里到底有什么"去说明
            }
        }

        /// <summary>
        /// 从 <c>名字.tex-json</c> 那份导入设置里取头部标志位。工程目录里的 sidecar 有两种来源，
        /// 键集不同但同名键的含义一致，所以两边都读得到东西：
        ///   - 编辑器自己写的：只列非默认的键(实测 beach.tex-json 就 3 个键)，缺键 = 默认(关)。
        ///   - repkg 解包时从原 .tex 头部反推的：6 个键全在，值还是字符串("false")。
        /// 为什么值得读：真实 WE 包里 1172/1348 条直通纹理的 flags 是 ClampUVs，
        /// 而这个开关决定贴图在边缘会不会采样到隔壁，抄错是有画面的。
        /// </summary>
        private static TexFlags SidecarFlags(
            Dictionary<string, string> byName, string imageEntryName, LoosePackageOptions options)
        {
            var sidecar = Lookup(byName, ChangeExtension(imageEntryName, ".tex-json"));
            if (sidecar == null) return options.ImageTexFlags;

            JsonElement? json;
            try
            {
                json = LegacyJson.Parse(File.ReadAllText(sidecar));
            }
            catch
            {
                return options.ImageTexFlags; // 说明文件读不动就用默认，不值得为它中断打包
            }

            var flags = TexFlags.None;
            if (LegacyJson.AsBool(LegacyJson.GetProp(json, "clampuvs")) ?? false) flags |= TexFlags.ClampUVs;
            if (LegacyJson.AsBool(LegacyJson.GetProp(json, "nointerpolation")) ?? false) flags |= TexFlags.NoInterpolation;
            return flags;
        }

        private byte[] TryEncode(
            string diskPath, string entryName, TexFlags flags, LoosePackageOptions options, LoosePackageReport report)
        {
            var bytes = File.ReadAllBytes(diskPath);

            // 显式 --dxt 时块编码优先，失败(GIF 多帧、坏图、小于 4x4)回落直通 —— 回落不是错误，
            // 但必须报一句，否则"整包都是 DXT"的预期与实际产物不符还查不出来。
            if (options.EncodeDxtFormat is { } fmt)
            {
                try
                {
                    return DxtTexBuilder.Build(diskPath, bytes, fmt, flags);
                }
                catch (Exception e)
                {
                    if (!PassthroughTexBuilder.CanBuild(diskPath))
                    {
                        report.Warnings.Add(
                            $"{fmt} 编码失败({e.GetType().Name}: {Shorten(e.Message)})，源图原样进包 {entryName}");
                        return null;
                    }
                    report.Notes.Add($"{entryName} 不走 {fmt}({Shorten(e.Message)})，回落直通");
                }
            }

            if (!PassthroughTexBuilder.CanBuild(diskPath)) return null;

            try
            {
                return PassthroughTexBuilder.Build(diskPath, bytes, flags);
            }
            catch (Exception e)
            {
                report.Warnings.Add($"封 .tex 失败({e.GetType().Name}: {Shorten(e.Message)})，源图原样进包 {entryName}");
                return null;
            }
        }

        /// <summary>
        /// 工程目录判据：根目录有 project.json / scene.json / index.html / assets.json 之一。
        /// 不能只认 project.json —— 实测 myprojects 下有好几个"解包出来的"目录只剩 scene.json
        /// (导入时没带 -c，project.json 是后来手工补的)，它们照样是要打包的对象。
        /// </summary>
        public static bool IsProjectDir(string directory)
        {
            foreach (var marker in ProjectMarkers)
                if (File.Exists(Path.Combine(directory, marker))) return true;
            return false;
        }

        private static readonly string[] ProjectMarkers = { "project.json", "scene.json", "index.html", "assets.json" };

        /// <summary>
        /// project.json 与它点名的预览图。只认根目录下那一份 —— 工坊效果包里有 preview/project.json
        /// 这种子目录条目，那是包内容，不能被当成壁纸元数据剔掉。
        /// </summary>
        private static List<string> PickMetadata(string root, Dictionary<string, string> byName)
        {
            var picked = new List<string>();
            var project = Lookup(byName, "project.json");
            if (project != null) picked.Add(project);

            foreach (var name in new[] { "preview.gif", "preview.jpg", "preview.png", "preview.jpeg" })
            {
                var path = Lookup(byName, name);
                if (path != null) picked.Add(path);
            }

            // project.json 的 preview 字段可以指到别的文件名上，跟着它走
            if (project != null)
            {
                var declared = ReadPreviewField(project);
                if (!string.IsNullOrWhiteSpace(declared))
                {
                    var path = Lookup(byName, declared.Replace('\\', '/'));
                    if (path != null && !picked.Contains(path, StringComparer.OrdinalIgnoreCase))
                        picked.Add(path);
                }
            }

            return picked;
        }

        private static string ReadPreviewField(string projectJsonPath)
        {
            try
            {
                var json = LegacyJson.Parse(File.ReadAllText(projectJsonPath));
                return LegacyJson.AsString(LegacyJson.GetProp(json, "preview"));
            }
            catch
            {
                // 清单读不动就只按默认文件名剔预览图，不值得为这个中断打包
                return null;
            }
        }

        /// <summary>
        /// 工程目录下的全部文件。不排除隐藏/系统属性 —— 工坊包里有以点开头的资源，
        /// 隐藏位不是"不属于这个壁纸"的证据；属于排除对象的都是按名字/扩展名点出来的。
        /// </summary>
        private static List<string> EnumerateFiles(string root)
        {
            var list = Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories).ToList();
            list.Sort(StringComparer.Ordinal); // 磁盘遍历序不保证跨机器一致
            return list;
        }

        private static string Relative(string root, string fullPath)
        {
            var relative = fullPath.Substring(root.Length).TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            // 条目名一律正斜杠：实测 279 个真实包 0 条含反斜杠
            return relative.Replace('\\', '/');
        }

        private static string Lookup(Dictionary<string, string> byName, string name) =>
            byName.TryGetValue(name, out var path) ? path : null;

        private static string ChangeExtension(string name, string extension)
        {
            var slash = name.LastIndexOf('/');
            var dot = name.LastIndexOf('.');
            return dot <= slash ? name + extension : name.Substring(0, dot) + extension;
        }

        private static bool IsPackageFile(string extension) =>
            extension.Equals(".pkg", StringComparison.OrdinalIgnoreCase) ||
            extension.Equals(".mpkg", StringComparison.OrdinalIgnoreCase);

        private static bool IsSourceImage(string extension) =>
            ImageExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase);

        private static bool IsModelSource(string extension) =>
            ModelSourceExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase);

        /// <summary>repkg 解包时写的说明文件；WE 的包里没有这一类条目。</summary>
        private static bool IsSidecar(string name) => name.EndsWith(".tex-json", StringComparison.OrdinalIgnoreCase);

        /// <summary>WE 的着色器编译缓存(shaders/blobsSM40/&lt;sha&gt;.dxs)，包里没有。</summary>
        private static bool IsShaderCache(string name)
        {
            if (name.EndsWith(".dxs", StringComparison.OrdinalIgnoreCase)) return true;
            var segments = name.Split('/');
            for (var i = 0; i < segments.Length - 1; i++)
                if (segments[i].StartsWith("blobs", StringComparison.OrdinalIgnoreCase))
                    return true;
            return false;
        }

        private static string[] NormalizePrefixes(string[] prefixes)
        {
            if (prefixes == null) return null;
            var list = new List<string>();
            foreach (var raw in prefixes)
            {
                if (string.IsNullOrWhiteSpace(raw)) continue;
                var p = raw.Trim().Replace('\\', '/').TrimEnd('/');
                if (p.Length > 0) list.Add(p);
            }
            return list.Count > 0 ? list.ToArray() : null;
        }

        private static bool MatchesPrefix(string[] prefixes, string name)
        {
            if (prefixes == null) return false;
            foreach (var prefix in prefixes)
            {
                if (!name.StartsWith(prefix + "/", StringComparison.OrdinalIgnoreCase) &&
                    !string.Equals(name, prefix, StringComparison.OrdinalIgnoreCase)) continue;
                return true;
            }
            return false;
        }

        private static string Shorten(string s)
        {
            s = (s ?? "").Replace("\r", " ").Replace("\n", " ");
            return s.Length <= 160 ? s : s.Substring(0, 160) + "…";
        }
    }
}
