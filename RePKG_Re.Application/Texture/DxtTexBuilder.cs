using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using RePKG_Re.Core.Texture;
using RePKG_Re.Application.Texture.Helpers;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace RePKG_Re.Application.Texture
{
    /// <summary>
    /// 把一张源图(png/jpg/bmp/tga…)编成块压缩的 .tex(DXT1/DXT3/DXT5：TEXB0002 + V2 mip 记录 + LZ4)。
    ///
    /// 【为什么在直通之外再开这条路】实测本地 279 个真实包 8152 条 .tex 的头部 format 分布是
    /// DXT5 3364 / RGBA8888 2299 / R8 2037 / RG88 307 / DXT1 110 / DXT3 38 —— WE 主流形态就是 DXT5，
    /// 而 pack 此前只会封 PNG/JPEG 直通 blob(4K 一张约 33MB，DXT5 是 8MB)。
    /// PassthroughTexBuilder 类注释点名的"要按 WE 主流的 DXT5 发，得另写 BC1/BC3 编码器"就是这里。
    ///
    /// 【mip 链怎么造】源图 → mip0；此后逐级 ×0.5(ImageSharp Box 采样)，止于 4x4(DXT 的最小完整块)。
    /// 真实包的级数是 1/3/4/5/9 混合、没有可复原的规律(与 PassthroughTexBuilder 同一判断)，
    /// 所以这里不宣称与 WE 的链同形 —— 只承诺"自己发的链自己(以及任何认 V2+LZ4+DXT 的读侧)读得回"。
    /// 每一级 LZ4 择优、只在真变小时采用，与 MobileTextureMaterializer.CompressIfItPays 同一口径：
    /// WE 对 DXT 载荷普遍压，对 180x180 小图标就是不压，跟着它走。
    ///
    /// 【拒绝什么】多帧 GIF(flags 带 IsGif 或帧数 >1)、小于 4x4 的图、非 DXT 目标 —— 一律抛
    /// ArgumentException，由调用方报一句后回落直通。帧表(TEXS)的逐帧时长编码不在这一版里，半做比不做更糟。
    ///
    /// 【与解码链的契约】产物必须能被 TexReader.Default 读回并把每级 DXT 解成 RGBA(测试
    /// DxtTexBuilderTests)—— 生成物先过自家解码关，是 pack 的最低正确性底线。
    /// </summary>
    public static class DxtTexBuilder
    {
        /// <summary>这个头部格式能不能当 pack 的块编码目标(只有 BC1/BC2/BC3 有编码器)。</summary>
        public static bool Supports(TexFormat format) =>
            format == TexFormat.DXT1 || format == TexFormat.DXT3 || format == TexFormat.DXT5;

        /// <summary>
        /// 编一张 DXT .tex。flags 沿用调用方的 sidecar/默认判定(ClampUVs 等)；
        /// UnkInt0 写 0，口径与 PassthroughTexBuilder 一致：只有"源图没有 .tex 兄弟"时才走到这里。
        /// </summary>
        public static byte[] Build(string sourcePath, byte[] imageBytes, TexFormat format, TexFlags flags)
        {
            if (sourcePath == null) throw new ArgumentNullException(nameof(sourcePath));
            if (imageBytes == null || imageBytes.Length == 0)
                throw new ArgumentException("Image bytes are empty", nameof(imageBytes));
            if (!Supports(format))
                throw new ArgumentException($"No block encoder for {format}", nameof(format));
            if ((flags & (TexFlags.IsGif | TexFlags.IsVideoTexture)) != 0)
                throw new ArgumentException($"头部标志 {flags} 与单帧 DXT 载荷矛盾", nameof(flags));

            using var image = Image.Load<Rgba32>(imageBytes);

            if (image.Frames.Count > 1)
                throw new ArgumentException($"多帧图({image.Frames.Count} 帧)不走 DXT：帧表没有编码实现");

            var width = image.Width;
            var height = image.Height;
            if (width < 4 || height < 4)
                throw new ArgumentException($"小于 4x4 的图({width}x{height})不够一个完整 DXT 块");

            var target = format == TexFormat.DXT1 ? MipmapFormat.CompressedDXT1
                : format == TexFormat.DXT3 ? MipmapFormat.CompressedDXT3
                : MipmapFormat.CompressedDXT5;

            var tex = new Tex
            {
                Magic1 = "TEXV0005",
                Magic2 = "TEXI0001",
                Header = new TexHeader
                {
                    Format = format,
                    Flags = flags,
                    TextureWidth = width,
                    TextureHeight = height,
                    ImageWidth = width,
                    ImageHeight = height,
                    UnkInt0 = 0
                },
                ImagesContainer = new TexImageContainer
                {
                    Magic = "TEXB0002",
                    ImageContainerVersion = TexImageContainerVersion.Version2,
                    ImageFormat = FreeImageFormat.FIF_UNKNOWN
                }
            };
            var texImage = new TexImage();
            tex.ImagesContainer.Images.Add(texImage);

            // mip0 直接从解码后的源图来；更深的级从上一级(而非源图)减半 —— 与真实 mip 链同构，
            // 也让小尺寸的深级天然平滑,块编码的误差不会在末级放大。
            // 原地 Mutate 在这里是合法的：image 是 Load 出来的自有内存(LoadPixelData 包装位图
            // 才禁止原地改，见 MobileTextureMaterializer 的同款注释)。
            var w = width; var h = height;
            while (true)
            {
                var level = new byte[w * h * 4];
                image.CopyPixelDataTo(MemoryMarshal.Cast<byte, Rgba32>(level.AsSpan()));

                var packed = DxtEncoder.Compress(w, h, level, target);
                var mipmap = new TexMipmap
                {
                    Width = w,
                    Height = h,
                    Format = target,
                    Bytes = packed,
                    DecompressedBytesCount = packed.Length,
                    IsLZ4Compressed = false
                };
                CompressIfItPays(mipmap, target);
                texImage.Mipmaps.Add(mipmap);

                if (w <= 4 && h <= 4) break;
                // Box(面积均值)最接近"mip 就是上一级的缩小版"；非 POT 尺寸 ImageSharp 的 Box
                // 在分数比时同样稳定,不必特判。
                var nw = Math.Max(4, w / 2);
                var nh = Math.Max(4, h / 2);
                image.Mutate(x => x.Resize(nw, nh, KnownResamplers.Box));
                w = nw; h = nh;
            }

            using var ms = new MemoryStream();
            using (var writer = new BinaryWriter(ms, Encoding.UTF8, true))
                TexWriter.Default.WriteTo(writer, tex);
            return ms.ToArray();
        }

        /// <summary>
        /// LZ4 只在真变小时采用(与 MobileTextureMaterializer.CompressIfItPays 同一判据)。
        /// CompressMipmap 要求 targetCompressFormat == mipmap.Format，这里传的就是块格式本身 ——
        /// 它只搬字节并翻标志位,不改格式。
        /// </summary>
        private static void CompressIfItPays(TexMipmap mipmap, MipmapFormat target)
        {
            var raw = mipmap.Bytes;
            new TexMipmapCompressor().CompressMipmap(mipmap, target, true);
            if (mipmap.Bytes.Length >= raw.Length)
            {
                mipmap.Bytes = raw;
                mipmap.IsLZ4Compressed = false;
                mipmap.DecompressedBytesCount = raw.Length;
            }
        }
    }
}
