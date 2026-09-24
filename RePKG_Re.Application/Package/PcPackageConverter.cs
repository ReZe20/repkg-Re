using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using RePKG_Re.Application.Texture;
using RePKG_Re.Core.Package;
using RePKG_Re.Core.Texture;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace RePKG_Re.Application.Package
{
    /// <summary>
    /// mpkg(移动) → pkg(PC) 反向转换选项。字段与 MobilePackageOptions 一一镜像,
    /// 语义取反:正向对纹理做什么,逆向就把那一步反回来。
    /// </summary>
    public class PcPackageOptions
    {
        /// <summary>输出 PC 包魔数。实测 WE PC 场景包用 PKGV0018(见 CHANGELOG 的 mpkg 支持条目)。</summary>
        public string Magic { get; set; } = "PKGV0018";

        /// <summary>
        /// 是否把"我们物化出来的 RGBA8"(TEXB0004 + imageFormat=FIF_UNKNOWN + fmt0 + 单帧)
        /// 重新编码回 PNG 直通 blob。关掉则这类纹理原样搬运(手机态留在 PC 包里)。
        /// </summary>
        public bool Dematerialize { get; set; } = true;

        /// <summary>去掉 scene.json 里的 texturereduction 键(逆向对应正向的 RecordReduction)。</summary>
        public bool ClearTextureReduction { get; set; } = true;

        /// <summary>同级 loose project.json / preview 路径(一般逆向用不到,包内已带)。</summary>
        public string ProjectJsonPath { get; set; }
        public string PreviewPath { get; set; }
    }

    public class PcPackageReport
    {
        public int Entries { get; set; }
        public int Dematerialized { get; set; }
        public int Copied { get; set; }
        public int ReductionCleared { get; set; }
        public long InputBytes { get; set; }
        public long OutputBytes { get; set; }

        /// <summary>"该做的没做成":逐条发 error 事件(与 MobilePackageReport 同语义)。</summary>
        public List<string> Warnings { get; } = new List<string>();
    }

    /// <summary>
    /// mpkg → pkg 的容器写入器,结构上是 MobilePackageConverter 的镜像:
    /// 条目布局一致([int32 8][magic][int32 条目数] + 每条 [名字字节数][名字][偏移][长度]),
    /// 单遍写 + 回填,不攒整包。差异全在 Produce:正向物化 PNG→RGBA8,逆向把 RGBA8→PNG。
    ///
    /// 逆向只对"正向能确定产生"的形态动手(TEXB0004 + FIF_UNKNOWN + fmt0 + 单帧),
    /// 原始 PC 的 raw 像素活在 TEXB0001/2/3,不在此判据内,因此不会被误伤;
    /// ETC2/DXT/R8/RG88/多帧物化产物无法无损还原 → 原样搬运并上报(不猜)。
    /// </summary>
    public class PcPackageConverter
    {
        private readonly ITexReader _texReader = TexReader.Default;
        private readonly ITexWriter _texWriter = TexWriter.Default;

        private sealed class PlanItem
        {
            public string Name;
            public byte[] NameBytes;
            public PackageEntry Source;
            public string LooseFile;
            public long RowFieldPosition;
            public bool IsSceneFile;
        }

        public PcPackageReport Convert(
            string inputMpkgPath,
            string outputPkgPath,
            PcPackageOptions options,
            Action<int, int, string> entryProgress = null)
        {
            if (inputMpkgPath == null) throw new ArgumentNullException(nameof(inputMpkgPath));
            if (outputPkgPath == null) throw new ArgumentNullException(nameof(outputPkgPath));
            options ??= new PcPackageOptions();

            var report = new PcPackageReport();
            var plan = BuildPlan(inputMpkgPath, options, report, out var inputDataStart);

            using var input = new FileStream(inputMpkgPath, FileMode.Open, FileAccess.Read, FileShare.Read);
            using var output = new FileStream(outputPkgPath, FileMode.Create, FileAccess.ReadWrite, FileShare.None);
            using var writer = new BinaryWriter(output, Encoding.UTF8, true);

            var magic = Encoding.ASCII.GetBytes(options.Magic);
            writer.Write(magic.Length);
            writer.Write(magic);
            writer.Write(plan.Count);

            foreach (var item in plan)
            {
                writer.Write(item.NameBytes.Length);
                writer.Write(item.NameBytes);
                item.RowFieldPosition = output.Position;
                writer.Write(0); // 偏移:回填
                writer.Write(0); // 长度:回填
            }

            var outputDataStart = output.Position;
            if (outputDataStart > int.MaxValue)
                throw new InvalidOperationException($"条目表超出 int32 寻址范围: {outputDataStart}");

            long offset = 0;
            for (var i = 0; i < plan.Count; i++)
            {
                var item = plan[i];
                entryProgress?.Invoke(i + 1, plan.Count, item.Name);

                var bytes = Produce(item, inputDataStart, input, options, report);

                output.Seek(outputDataStart + offset, SeekOrigin.Begin);
                output.Write(bytes, 0, bytes.Length);

                PatchRow(output, item.RowFieldPosition, offset, bytes.Length);
                offset += bytes.Length;

                report.Entries++;
                report.OutputBytes += bytes.Length;
            }

            output.SetLength(outputDataStart + offset);
            if (output.Length != outputDataStart + offset)
                throw new InvalidOperationException("写完自检失败:文件大小 != 表尾 + Σ条目长度");

            return report;
        }

        private List<PlanItem> BuildPlan(
            string inputMpkgPath,
            PcPackageOptions options,
            PcPackageReport report,
            out int inputDataStart)
        {
            var package = ReadTable(inputMpkgPath);
            inputDataStart = package.HeaderSize;
            var plan = new List<PlanItem>(package.Entries.Count + 2);
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var sceneSlotOpen = true;

            foreach (var entry in package.Entries)
            {
                names.Add(entry.FullPath);
                plan.Add(new PlanItem
                {
                    Name = entry.FullPath,
                    NameBytes = Encoding.UTF8.GetBytes(entry.FullPath),
                    Source = entry,
                    IsSceneFile = sceneSlotOpen && Path.GetFileName(entry.FullPath)
                        .Equals("scene.json", StringComparison.OrdinalIgnoreCase)
                });
                if (plan[plan.Count - 1].IsSceneFile) sceneSlotOpen = false;
                report.InputBytes += entry.Length;
            }

            AddLoose(plan, names, "project.json", options.ProjectJsonPath);
            AddLoose(plan, names, "preview.gif", options.PreviewPath);
            return plan;
        }

        private static void AddLoose(List<PlanItem> plan, HashSet<string> names, string entryName, string path)
        {
            if (string.IsNullOrEmpty(path) || !File.Exists(path)) return;
            if (!names.Add(entryName)) return;

            plan.Add(new PlanItem
            {
                Name = entryName,
                NameBytes = Encoding.UTF8.GetBytes(entryName),
                LooseFile = path
            });
        }

        private static Core.Package.Package ReadTable(string inputMpkgPath)
        {
            using var stream = new FileStream(inputMpkgPath, FileMode.Open, FileAccess.Read, FileShare.Read);
            using var reader = new BinaryReader(stream, Encoding.UTF8, true);
            return new PackageReader {ReadEntryBytes = false}.ReadFrom(reader);
        }

        private byte[] Produce(
            PlanItem item, int inputDataStart, Stream input, PcPackageOptions options, PcPackageReport report)
        {
            if (item.LooseFile != null)
                return File.ReadAllBytes(item.LooseFile);

            var bytes = PackageReader.ReadEntryBytesFromStream(
                input, inputDataStart, item.Source.Offset, item.Source.Length);

            if (item.IsSceneFile && options.ClearTextureReduction)
                return ClearReduction(bytes, item, report);

            if (options.Dematerialize && item.Name.EndsWith(".tex", StringComparison.OrdinalIgnoreCase))
            {
                var tex = TryDematerialize(bytes, item, report);
                if (tex != null) return tex;
            }

            report.Copied++;
            return bytes;
        }

        private static byte[] ClearReduction(byte[] bytes, PlanItem item, PcPackageReport report)
        {
            report.Copied++;
            var patched = SceneJsonPatcher.RemoveTextureReduction(bytes, out var failure);
            if (patched == null)
            {
                if (failure != null)
                    report.Warnings.Add($"{item.Name}: 没能清掉 texturereduction({failure}),按原样写出");
                return bytes;
            }

            report.ReductionCleared++;
            return patched;
        }

        /// <summary>
        /// 命中"我们物化出来的 RGBA8"判据就重编码成 PNG 直通 blob 并返回新字节;
        /// 否则返回 null(调用方原样搬运)。任何解析/编码异常也返回 null —— 逆向宁可不动也别写坏。
        /// </summary>
        private byte[] TryDematerialize(byte[] texBytes, PlanItem item, PcPackageReport report)
        {
            if (texBytes == null || texBytes.Length < 8) return null;

            ITex tex;
            try
            {
                // 两遍读,同物化器的口径:第一遍只读结构判断形态 —— DXT 在第一遍就解成 RGBA8
                // 是白花几百 MB(本仓库实测的"读了就扔"峰值),而且读不动 ≠ 该报警。
                using var probe = new BinaryReader(new MemoryStream(texBytes), Encoding.UTF8, true);
                tex = _texReader.ReadFrom(probe, readPixels: false);
                if (!IsMaterialized(tex))
                    return null; // 原始形态(PC 的 raw 像素在 TEXB0001/2/3),计数交给调用方,避免双计
            }
            catch
            {
                // 结构都读不动:可能根本不是 TEX(逆向宁可不动)
                report.Warnings.Add($"{item.Name}: TEX 解析失败,原样搬运");
                return null;
            }

            try
            {
                using var full = new BinaryReader(new MemoryStream(texBytes), Encoding.UTF8, true);
                tex = _texReader.ReadFrom(full, readPixels: true, onlyImage: 0);
            }
            catch (Exception e)
            {
                report.Warnings.Add($"{item.Name}: 物化形态像素读取失败({e.GetType().Name}),原样搬运");
                return null;
            }

            var image = tex.ImagesContainer.Images[0];
            var mip = image.Mipmaps[0];
            if (mip.Bytes == null || (long) mip.Width * mip.Height * 4 != mip.Bytes.LongLength)
            {
                report.Warnings.Add($"{item.Name}: 物化纹理像素尺寸与字节数不符,原样搬运");
                return null;
            }

            byte[] png;
            try
            {
                png = Rgba8ToPng(mip.Width, mip.Height, mip.Bytes);
            }
            catch (Exception e)
            {
                report.Warnings.Add($"{item.Name}: PNG 重编码失败({e.GetType().Name}),原样搬运");
                return null;
            }

            try
            {
                // 重建成 PC 直通形态:TEXB0004 + imageFormat=FIF_PNG,载荷就是这段 PNG(读回即 V3 mip 记录)。
                var output = new Tex
                {
                    Magic1 = tex.Magic1,
                    Magic2 = tex.Magic2,
                    Header = new TexHeader
                    {
                        Format = tex.Header.Format,
                        Flags = tex.Header.Flags,
                        TextureWidth = mip.Width,
                        TextureHeight = mip.Height,
                        ImageWidth = mip.Width,
                        ImageHeight = mip.Height,
                        UnkInt0 = tex.Header.UnkInt0
                    },
                    ImagesContainer = new TexImageContainer
                    {
                        Magic = "TEXB0004",
                        ImageContainerVersion = TexImageContainerVersion.Version3,
                        ImageFormat = FreeImageFormat.FIF_PNG
                    },
                    FrameInfoContainer = tex.FrameInfoContainer
                };

                output.ImagesContainer.Images.Add(new TexImage
                {
                    Mipmaps =
                    {
                        new TexMipmap
                        {
                            Width = mip.Width,
                            Height = mip.Height,
                            Format = MipmapFormat.ImagePNG,
                            Bytes = png,
                            DecompressedBytesCount = png.Length,
                            IsLZ4Compressed = false
                        }
                    }
                });

                using var ms = new MemoryStream();
                using (var w = new BinaryWriter(ms, Encoding.UTF8, true))
                    _texWriter.WriteTo(w, output);

                report.Dematerialized++;
                return ms.ToArray();
            }
            catch (Exception e)
            {
                report.Warnings.Add($"{item.Name}: 重建 PC 纹理失败({e.GetType().Name}),原样搬运");
                return null;
            }
        }

        /// <summary>
        /// 物化纹理判据:TEXB0004(物化器固定写它)+ imageFormat=FIF_UNKNOWN + 头部 fmt0(RGBA8888)
        /// + 非视频 + 单帧 + 单 mip。原始 PC 的 raw 像素活在 TEXB0001/2/3,不会撞上 TEXB0004;
        /// ETC2(fmt5)物化产物我们带不动解码器 → 不满足 fmt0,照搬并另计。
        /// </summary>
        private static bool IsMaterialized(ITex tex)
        {
            if (tex.IsVideoTexture) return false;
            var c = tex.ImagesContainer;
            if (c == null || c.Magic != "TEXB0004") return false;
            if (c.ImageFormat != FreeImageFormat.FIF_UNKNOWN) return false;
            if (tex.Header.Format != TexFormat.RGBA8888) return false;
            if (c.Images.Count != 1) return false;
            var mips = c.Images[0].Mipmaps;
            return mips.Count == 1;
        }

        /// <summary>RGBA8(直色)→ PNG 字节。无损:像素逐字节进 ImageSharp,再存 PNG。</summary>
        private static byte[] Rgba8ToPng(int width, int height, byte[] rgba)
        {
            using var image = Image.LoadPixelData<Rgba32>(
                MemoryMarshal.Cast<byte, Rgba32>(rgba.AsSpan()), width, height);

            using var ms = new MemoryStream();
            image.SaveAsPng(ms);
            return ms.ToArray();
        }

        private static void PatchRow(Stream output, long rowFieldPosition, long offset, int length)
        {
            if (offset > int.MaxValue)
                throw new InvalidOperationException($"条目偏移超出 int32: {offset}");

            var tail = output.Position;
            output.Seek(rowFieldPosition, SeekOrigin.Begin);
            output.Write(BitConverter.GetBytes((int) offset), 0, 4);
            output.Write(BitConverter.GetBytes(length), 0, 4);
            output.Seek(tail, SeekOrigin.Begin);
        }
    }
}
