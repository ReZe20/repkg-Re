using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime;
using System.Runtime.InteropServices;
using System.Text;
using RePKG_Re.Core.Texture;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace RePKG_Re.Application.Texture
{
    /// <summary>物化结果的动作。</summary>
    public enum MaterializeAction
    {
        /// <summary>本来就是原始像素/视频，原样搬运</summary>
        Copy,
        /// <summary>直通编码图已解码成 RGBA8</summary>
        Materialized,
        /// <summary>拒绝物化（超尺寸上限/解码失败），调用方应按 Copy 处理并上报</summary>
        Refused
    }

    public class MaterializeResult
    {
        public MaterializeAction Action { get; set; }
        /// <summary>要写进条目的字节（Refused 时等于原字节）</summary>
        public byte[] Bytes { get; set; }
        public string Reason { get; set; }
        /// <summary>物化了几条 image（帧）</summary>
        public int Images { get; set; }
        public int Width { get; set; }
        public int Height { get; set; }
        /// <summary>这次的像素是不是被缩小过（Reduction &gt; 1 且真的改变了尺寸）</summary>
        public bool Reduced { get; set; }
        /// <summary>这次的像素发的是 ETC2 RGBA8（fmt5）而不是 RGBA8（fmt0）</summary>
        public bool EncodedEtc2 { get; set; }
        /// <summary>这次把动图帧表里的矩形跟着像素一起缩了（只有带帧容器又真缩过的条目才会）</summary>
        public bool FramesScaled { get; set; }
    }

    /// <summary>
    /// 把 PC 包里"直通编码图"（TEX 容器 imageFormat != FIF_UNKNOWN，载荷是一段 PNG/JPEG 字节）
    /// 重写成移动端要的原始 RGBA8。移动端按 fmt0 ⇒ w*h*4 读载荷，喂编码图会整张纹理作废。
    ///
    /// 已真机/逐字节定死的口径，改动前先看这些证据：
    /// - alpha 是**直色**（非预乘）：17/17 条与 WE 移动导出逐字节相同，预乘每条差 8 万字节。
    ///   所以解码后原样写字节，不做任何 alpha/色彩变换。
    /// - 写出容器用 TEXB0004 + mipCnt=1 + 真实字节数；DXT/R8/RG88/原始像素/内嵌 mp4 一律不碰。
    /// - LZ4 逐条目择优：WE 对 180×180 小图标写 lz=0，只对 10.9MB 大图压。
    /// </summary>
    public class MobileTextureMaterializer
    {
        /// <summary>RGBA8 一像素 4 字节；单条 mip 的字节数上限沿用 TEX 读取侧那道闸</summary>
        private const int BytesPerPixel = 4;

        /// <summary>源像素超过这个大小就主动回收一次（见物化循环末尾那段 LOH 的说明）。</summary>
        private const long ReclaimThresholdBytes = 64L * 1024 * 1024;

        private readonly ITexReader _texReader = TexReader.Default;
        private readonly ITexWriter _texWriter = TexWriter.Default;
        private readonly ITexMipmapCompressor _compressor = new TexMipmapCompressor();

        public bool UseLz4 { get; set; } = true;

        /// <summary>
        /// 纹理缩小除数，对齐 WE 的"纹理缩小"下拉：1=原始、2、4。只有会被物化的条目参与，
        /// DXT/R8/RG88/原始像素/视频都是逐字节搬运，没有可缩的机会（要缩它们得先有块编码器）。
        /// </summary>
        public int Reduction { get; set; } = 1;

        /// <summary>
        /// 缩小过的那批像素改发 ETC2 RGBA8(头部 format=5)而不是 RGBA8。1 字节/像素，比 fmt0 小 4 倍，
        /// 代价是有损且**这套字节还没上过真机** —— 所以默认关。只在 <see cref="Reduction"/> &gt; 1 时生效：
        /// ÷1 那条路是逐字节验过的形态，不能因为尺寸刚好 4 对齐就偷偷换编码器。
        /// </summary>
        public bool EncodeEtc2 { get; set; }

        public MaterializeResult Materialize(byte[] texBytes)
        {
            if (texBytes == null || texBytes.Length < 8)
                return Copy(texBytes, "too small to be a TEX");

            ITex tex;
            try
            {
                // 第一遍只读结构:读侧会把 DXT 解成 RGBA8,而我们大多数条目是逐字节照搬,那份解压是白花的。
                // 实测一张 DXT 密集的壁纸在 ÷1 下峰值 1579MB,全压在"读了就扔"的条目上。
                tex = Read(texBytes, readPixels: false);
                if (!NeedsPixels(tex))
                    return Copy(texBytes, CopyReason(tex));
            }
            catch (Exception e)
            {
                return Copy(texBytes, $"TEX 解析失败，原样搬运: {e.GetType().Name} {e.Message}");
            }

            var container = tex.ImagesContainer;
            var output = new Tex
            {
                Magic1 = tex.Magic1,
                Magic2 = tex.Magic2,
                Header = new TexHeader
                {
                    Format = TexFormat.RGBA8888,
                    Flags = tex.Header.Flags,
                    UnkInt0 = tex.Header.UnkInt0
                },
                ImagesContainer = new TexImageContainer
                {
                    Magic = "TEXB0004",
                    // 读侧对 TEXB0004+非 MP4 会降级成 Version3 的 mip 记录，这里保持一致才能写出/读回闭合
                    ImageContainerVersion = TexImageContainerVersion.Version3,
                    ImageFormat = FreeImageFormat.FIF_UNKNOWN
                },
                FrameInfoContainer = tex.FrameInfoContainer
            };

            // 视频纹理的载荷是内嵌 mp4，解码成像素会把它直接写坏；原始像素/R8/RG88 一律照搬。
            var shrinkingDx = WantsDxReencode(tex);

            int width = 0, height = 0;
            int logicalWidth = 0, logicalHeight = 0;
            var encodedEtc2 = false;

            var reducing = Reduction > 1;
            var resized = false;
            for (var i = 0; i < container.Images.Count; i++)
            {
                // 一次只把一张 image 的像素读进来:图集那种五条 7680×7560 的 DXT 全解码驻留就是 1.1GB,
                // 逐张处理能把峰值压到单张的量级。结构上面那一遍已经走过,这里只多跳几条 mip 记录。
                var source = Read(texBytes, readPixels: true, onlyImage: i).ImagesContainer.Images[i].FirstMipmap;
                if (source?.Bytes == null || source.Bytes.Length == 0)
                    return Copy(texBytes, "某帧无载荷，整条目放弃物化");

                byte[] rgba;
                int pixelWidth, pixelHeight, logicalThisWidth, logicalThisHeight;
                if (shrinkingDx)
                {
                    // 读侧已经把 DXT 解成 RGBA8；DX 的像素尺寸是 4 对齐补齐后的值，
                    // 逻辑尺寸只能取头部 iw/ih（WE 也按 iw/ih 算缩小，不按 tw/th）
                    rgba = source.Bytes;
                    pixelWidth = source.Width;
                    pixelHeight = source.Height;
                    // 读侧只在 LZ4 解开之后才把 DXT 解成 RGBA8;没压过的 DX 载荷到这里还是块字节,
                    // 当成 RGBA8 采样会写出一张花屏 —— 认字节数,不认就照搬。
                    if (rgba == null || rgba.Length != (long) pixelWidth * pixelHeight * BytesPerPixel)
                        return Copy(texBytes, "DX 载荷没被解成 RGBA8，原样搬运");
                    logicalThisWidth = PickLogical(tex.Header.ImageWidth, pixelWidth);
                    logicalThisHeight = PickLogical(tex.Header.ImageHeight, pixelHeight);
                }
                else
                {
                    var decoded = DecodeRgba(source.Bytes);
                    if (decoded.Failure != null)
                        return Copy(texBytes, $"解码失败: {decoded.Failure}");
                    rgba = decoded.Pixels;
                    pixelWidth = decoded.Width;
                    pixelHeight = decoded.Height;
                    logicalThisWidth = pixelWidth;
                    logicalThisHeight = pixelHeight;
                }

                // WE 的公式:先整除截断、再往上补到 4 的倍数(它出块格式需要 4x4 对齐)。
                // 我们出 fmt0 其实不需要补，但跟 WE 保持同一尺寸才谈得上"对齐它的行为"。
                width = reducing ? Reduce(logicalThisWidth, Reduction) : pixelWidth;
                height = reducing ? Reduce(logicalThisHeight, Reduction) : pixelHeight;
                logicalWidth = logicalThisWidth;
                logicalHeight = logicalThisHeight;

                var needed = (long) width * height * BytesPerPixel;
                if (needed > Constants.MaximumMipmapByteCount)
                    return Refused($"{width}x{height} RGBA8 = {needed}B 超过上限 {Constants.MaximumMipmapByteCount}B");

                var pixels = rgba;
                if (width != pixelWidth || height != pixelHeight ||
                    logicalThisWidth != pixelWidth || logicalThisHeight != pixelHeight)
                {
                    pixels = Resample(rgba, pixelWidth, pixelHeight, logicalThisWidth, logicalThisHeight, width, height);
                    resized = true;
                }

                // 缩过之后宽高必然是 4 的倍数(Reduce 往上补到 4)，正好是块格式的要求；
                // ÷1 保持原样不编，一是那条形路真机逐字节验过，二是尺寸未必 4 对齐。
                // WE 的口径也是这样：它 max 档发 fmt0 RGBA8，medium 起才转 fmt5。
                var etc2 = EncodeEtc2 && reducing && width % 4 == 0 && height % 4 == 0;
                if (etc2)
                {
                    encodedEtc2 = true;
                    pixels = Etc2Encoder.EncodeRgba8(width, height, pixels);
                }

                var mip = new TexMipmap
                {
                    Width = width,
                    Height = height,
                    Format = etc2 ? MipmapFormat.CompressedETC2RGBA8 : MipmapFormat.RGBA8888,
                    Bytes = pixels,
                    DecompressedBytesCount = pixels.Length,
                    IsLZ4Compressed = false
                };

                if (UseLz4)
                    CompressIfItPays(mip);

                output.ImagesContainer.Images.Add(new TexImage {Mipmaps = {mip}});

                // 逐张处理只是不再"同时"持有全部像素：上一张解出来的那几百 MB 还挂在 LOH 上等人来收。
                // 实测一张 5×7680×7560 的图集即便逐张跑，峰值仍顶到 1.2GB（÷4 也还有 1.23GB），
                // 而那些全是没人引用的垃圾。大缓冲编完就主动收一次：一张一次 full GC（几十毫秒），
                // 换掉约 900MB 峰值。小图标过不了这条门，所以常规壁纸一分开销都不加。
                if ((long) pixelWidth * pixelHeight * BytesPerPixel < ReclaimThresholdBytes) continue;
                rgba = null;
                source.Bytes = null;
                GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.CompactOnce;
                GC.Collect();
            }

            // 动图纹理的帧表记的是"这一帧在图集像素里的矩形"。像素被缩过，矩形必须跟着缩，
            // 否则每帧都从图集的错位置上取图。WE 移动导出的口径（3577990983 的图集逐字段对过）：
            // 帧的 X/Y/宽/高按缩小后的比例走（1920×1080 → 960×540），GifWidth/GifHeight 保持原值不动。
            var framesScaled = false;
            if (resized && output.FrameInfoContainer != null && logicalWidth > 0 && logicalHeight > 0)
            {
                ScaleFrames(output.FrameInfoContainer, logicalWidth, width, logicalHeight, height);
                framesScaled = true;
            }

            // 头部四个尺寸字段写"解码出来的原始尺寸"，不是缩小后的尺寸：真机对照过，
            // 把这四个值一起改写成缩小后的尺寸，画面上就有一块背景直接不见了（R5），
            // 留原始尺寸则正常（R3/R4）。真实像素宽高只由 mip 记录说话，WE 也是这个口径。
            // ÷1 时两者相等，所以这一条对真机验过的那版是逐字节等价的。
            output.Header.Format = encodedEtc2 ? TexFormat.ETC2_RGBA8 : TexFormat.RGBA8888;
            output.Header.TextureWidth = logicalWidth;
            output.Header.TextureHeight = logicalHeight;
            output.Header.ImageWidth = logicalWidth;
            output.Header.ImageHeight = logicalHeight;

            using var stream = new MemoryStream();
            using (var writer = new BinaryWriter(stream, Encoding.UTF8, true))
            {
                _texWriter.WriteTo(writer, output);
            }

            return new MaterializeResult
            {
                Action = MaterializeAction.Materialized,
                Bytes = stream.ToArray(),
                Images = output.ImagesContainer.Images.Count,
                Width = width,
                Height = height,
                Reduced = resized,
                EncodedEtc2 = encodedEtc2,
                FramesScaled = framesScaled
            };
        }

        private ITex Read(byte[] texBytes, bool readPixels, int onlyImage = -1)
        {
            using var reader = new BinaryReader(new MemoryStream(texBytes), Encoding.UTF8, true);
            return _texReader.ReadFrom(reader, readPixels, onlyImage);
        }

        /// <summary>
        /// 这条目的像素要不要读进内存。只用到头部和容器头,所以不读载荷的探针就能回答。
        /// </summary>
        private bool NeedsPixels(ITex tex)
        {
            if (tex.IsVideoTexture) return false;
            var container = tex.ImagesContainer;
            if (container == null || container.Images.Count == 0) return false;
            return container.ImageFormat != FreeImageFormat.FIF_UNKNOWN || WantsDxReencode(tex);
        }

        /// <summary>
        /// DX 块格式（DXT5/3/1）的载荷在读侧已经被解成 RGBA8，所以"要缩小 + 有 ETC2 编码器"时可以走重编；
        /// 其余情况（fmt0 原始像素、R8/RG88 遮罩）继续逐字节照搬 —— 那是真机验过的形态。
        /// 动图图集一起走：WE 自己就是这么发的（3577990983 那张 5×7680×7560 的图集，PC 23,105,402B
        /// → 它发的 fmt5 移动包里 6,307,131B），代价是帧表要跟着缩，见 <see cref="ScaleFrames"/>。
        /// </summary>
        private bool WantsDxReencode(ITex tex)
            => EncodeEtc2 && Reduction > 1 && IsDxBlockFormat(tex.Header.Format);

        private static string CopyReason(ITex tex)
        {
            if (tex.IsVideoTexture) return "视频纹理，原样搬运";
            if (tex.ImagesContainer == null || tex.ImagesContainer.Images.Count == 0) return "无 image 容器";
            return IsDxBlockFormat(tex.Header.Format)
                ? "DX 块格式，本次不重编，原样搬运"
                : "已是原始像素/R8/RG88";
        }

        private readonly struct DecodeOutcome
        {
            public byte[] Pixels { get; }
            public int Width { get; }
            public int Height { get; }
            public string Failure { get; }

            public DecodeOutcome(byte[] pixels, int width, int height, string failure)
            {
                Pixels = pixels;
                Width = width;
                Height = height;
                Failure = failure;
            }
        }

        /// <summary>
        /// 解成直色 RGBA8。尺寸一律取解码结果，不信 TEX 头部记录 —— 头部的 tw/th 可能是块对齐后的值。
        /// 像素是拷出来的，位图在这里就释放，不留到缩放那一步。
        /// </summary>
        private static DecodeOutcome DecodeRgba(byte[] encoded)
        {
            Image<Rgba32> image = null;
            try
            {
                image = Image.Load<Rgba32>(encoded);
                var w = image.Width;
                var h = image.Height;
                if (w <= 0 || h <= 0)
                    return new DecodeOutcome(null, 0, 0, $"非法尺寸 {w}x{h}");

                var pixels = new byte[w * h * BytesPerPixel];
                image.CopyPixelDataTo(MemoryMarshal.Cast<byte, Rgba32>(pixels.AsSpan()));
                return new DecodeOutcome(pixels, w, h, null);
            }
            catch (Exception e)
            {
                return new DecodeOutcome(null, 0, 0, $"{e.GetType().Name} {e.Message}");
            }
            finally
            {
                image?.Dispose();
            }
        }

        /// <summary>WE 的移动尺寸 = 先整除截断、再补到 4 的倍数（下限 4，避免出现 0 宽）。</summary>
        public static int Reduce(int size, int divisor)
        {
            var halved = size / divisor;
            var padded = (halved + 3) & ~3;
            return padded > 0 ? padded : 4;
        }

        /// <summary>头部记的逻辑尺寸不可信（可能是 0，也可能比像素还大），越界就退回像素尺寸。</summary>
        private static int PickLogical(int headerSize, int pixelSize)
            => headerSize > 0 && headerSize <= pixelSize ? headerSize : pixelSize;

        /// <summary>只有这三种是"块压缩的彩色纹理"，能解成 RGBA8 再重编；R8/RG88 是遮罩，照搬。</summary>
        private static bool IsDxBlockFormat(TexFormat format)
            => format == TexFormat.DXT5 || format == TexFormat.DXT3 || format == TexFormat.DXT1;

        /// <summary>
        /// 像素被缩过，帧表里的矩形必须跟着缩 —— 它是"这一帧在图集像素里的位置"，不缩就每帧取错图。
        /// 比例取 缩小后/逻辑 的实际比值，不是 1/Reduction：Reduce 会把尺寸补到 4 的倍数，
        /// 补完的比例和除数未必相等。GifWidth/GifHeight 不动 —— WE 的移动导出就是只缩帧、不缩这两个
        /// （3577990983 那张 5×7680×7560 的图集逐字段对过：帧 1920×1080→960×540，GifW/H 仍是 1920×1080）。
        /// WidthY/HeightX 在见过的所有真包里都是 0，跟着各自那根轴走是"至少不会更错"的选择。
        /// </summary>
        private static void ScaleFrames(ITexFrameInfoContainer frames, int srcWidth, int dstWidth,
            int srcHeight, int dstHeight)
        {
            foreach (var frame in frames.Frames)
            {
                frame.X = Scale(frame.X, dstWidth, srcWidth);
                frame.Width = Scale(frame.Width, dstWidth, srcWidth);
                frame.WidthY = Scale(frame.WidthY, dstWidth, srcWidth);
                frame.Y = Scale(frame.Y, dstHeight, srcHeight);
                frame.Height = Scale(frame.Height, dstHeight, srcHeight);
                frame.HeightX = Scale(frame.HeightX, dstHeight, srcHeight);
            }
        }

        private static float Scale(float value, int dst, int src) => (float) System.Math.Round(value * dst / src);

        /// <summary>
        /// RGBA8 → 先裁到逻辑尺寸（DX 的像素带 4 对齐的补边）→ 缩到目标尺寸。
        /// 包装像素的位图禁止原地 Mutate，所以一律 Clone。
        /// </summary>
        private static byte[] Resample(byte[] rgba, int pixelWidth, int pixelHeight, int cropWidth, int cropHeight,
            int width, int height)
        {
            using var source = Image.LoadPixelData(
                MemoryMarshal.Cast<byte, Rgba32>(rgba.AsSpan()), pixelWidth, pixelHeight);
            using var resized = source.Clone(x =>
            {
                if (cropWidth != pixelWidth || cropHeight != pixelHeight)
                    x.Crop(cropWidth, cropHeight);
                x.Resize(width, height, KnownResamplers.Lanczos3);
            });

            var pixels = new byte[width * height * BytesPerPixel];
            resized.CopyPixelDataTo(MemoryMarshal.Cast<byte, Rgba32>(pixels.AsSpan()));
            return pixels;
        }

        /// <summary>LZ4 只在真的变小时采用；WE 对小图标就是不压，跟着它走。</summary>
        private void CompressIfItPays(TexMipmap mip)
        {
            var raw = mip.Bytes;

            try
            {
                // 目标格式必须跟着 mip 走：压缩器只搬字节不改格式，传 fmt0 去压一个 fmt5 块
                // 会被它的格式一致性检查直接拒掉。
                _compressor.CompressMipmap(mip, mip.Format, true);
            }
            catch (Exception)
            {
                Revert(mip, raw);
                return;
            }

            if (mip.Bytes == null || mip.Bytes.Length >= raw.Length)
                Revert(mip, raw);
        }

        private static void Revert(TexMipmap mip, byte[] raw)
        {
            mip.Bytes = raw;
            mip.IsLZ4Compressed = false;
            mip.DecompressedBytesCount = raw.Length;
        }

        private static MaterializeResult Copy(byte[] bytes, string reason)
            => new MaterializeResult {Action = MaterializeAction.Copy, Bytes = bytes, Reason = reason};

        private static MaterializeResult Refused(string reason)
            => new MaterializeResult {Action = MaterializeAction.Refused, Bytes = null, Reason = reason};
    }
}
