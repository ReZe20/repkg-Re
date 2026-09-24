using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using RePKG_Re.Core.Texture;
using SixLabors.ImageSharp;

namespace RePKG_Re.Application.Texture
{
    /// <summary>
    /// 把一张源图(png/jpg/bmp/tga)封成 WE 的"直通纹理"(TEXB0004 + imageFormat=FIF_xxx，
    /// 载荷就是那段编码图字节)。
    ///
    /// 【为什么是直通而不是 DXT】实测本地 279 个真实包里的 8152 条 .tex：
    /// 头部 format 分布 DXT5 3364 / RGBA8888 2299 / R8 2037 / RG88 307 / DXT1 110 / DXT3 38，
    /// 其中 <b>1348 条是 imageFormat=13(FIF_PNG) 的 PNG 直通</b>，另有 77 条 imageFormat=2(FIF_JPEG)。
    /// 也就是说"把编码图原样塞进 TEX"不是我们臆造的形态，是 WE 自己导出时就在发的形态之一 ——
    /// 所以封直通字节是"源图 → .tex"里唯一无需新写块编码器、且能拿真实包当证据的那条路。
    /// 代价：包体和运行时显存都比 DXT5 大(4K 一张 RGBA 未压 = 33MB，DXT5 = 8MB)。
    /// 要按 WE 主流的 DXT5 发，得另写 BC1/BC3 编码器 —— 见 README 的 pack 节"已知取舍"。
    ///
    /// 【照抄真实包的哪些字段】(全部来自上面那 1348 条的统计，不是规范)
    ///   - 头部 Format 一律 RGBA8888(1348/1348 都是它，即使载荷是 PNG 字节)。
    ///   - TextureWidth/Height 取图的实际尺寸：1076/1348 就是 tex==img，剩下 272 条才垫到 POT。
    ///     两种 WE 都发得出去，取多数那种，不去猜那 272 条的圆整规则(180→192 这种既不是 4 也不是 POT 的对齐)。
    ///   - mip 记录里 DecompressedBytesCount 写 0、lz4 写 0(1346/1348)，与 MobileTextureMaterializer
    ///     "LZ4 逐条目择优"同一口径：这两个字段只在 lz4=1 时被读，写 0 是最保守的值。
    ///   - 只发一级 mip(链长 1)。真实包里有 1/3/4/5/9 级的，级数没有可复原的规律，
    ///     而 70 条 mip1 的 PNG 直通证明单级是 WE 认的形态；多级的像素要重采样，
    ///     WE 用的核没测出来，猜错比不发更糟(发错的 mip 在近处看不出来、远处缩小时才显现)。
    ///   - UnkInt0：WE 在这几个真实样本里放的是像平均色的打包值(0xFF5E98CE 这种)，
    ///     但字节序与算法都没还原出来。新建的纹理写 0 —— 只有"源图没有 .tex 兄弟"时才会走到这里，
    ///     那种图本来就没有"原件的那个值"可保。
    /// </summary>
    public static class PassthroughTexBuilder
    {
        /// <summary>
        /// 能封成直通纹理的扩展名 → 内层 imageFormat。表里只有 PNG 与 JPEG 两种，因为真实包里只观测到这两种直通形态
        /// (imageFormat=13 共 1348 条、imageFormat=2 共 77 条)。bmp/tga/webp 不在表里 = 不封：
        /// FreeImageFormat 枚举认得它们，但没有一条真包证明 WE 认这种载荷，猜错的症状是整张纹理不显示。
        /// 不在表里的源图交回调用方原样搬运并上报。
        /// </summary>
        private static readonly Dictionary<string, FreeImageFormat> EncodableExtensions =
            new Dictionary<string, FreeImageFormat>(StringComparer.OrdinalIgnoreCase)
            {
                { ".png", FreeImageFormat.FIF_PNG },
                { ".jpg", FreeImageFormat.FIF_JPEG },
                { ".jpeg", FreeImageFormat.FIF_JPEG }
            };

        /// <summary>这个扩展名能不能封成直通纹理。</summary>
        public static bool CanBuild(string path) =>
            path != null && EncodableExtensions.TryGetValue(Path.GetExtension(path), out _);

        /// <summary>
        /// 封一个直通 .tex。读图头只为了拿宽高(ImageSharp 只 Identify，不解码像素，4K PNG 也是几毫秒)。
        /// 编码图本身认不出尺寸时抛 ArgumentException —— 宽高是头部字段，猜不得。
        /// </summary>
        public static byte[] Build(string sourcePath, byte[] imageBytes, TexFlags flags = TexFlags.None)
        {
            if (sourcePath == null) throw new ArgumentNullException(nameof(sourcePath));
            if (imageBytes == null || imageBytes.Length == 0)
                throw new ArgumentException("Image bytes are empty", nameof(imageBytes));

            if (!EncodableExtensions.TryGetValue(Path.GetExtension(sourcePath), out var imageFormat))
                throw new ArgumentException($"Unsupported source image extension: {sourcePath}", nameof(sourcePath));

            IImageInfo info;
            try
            {
                info = Image.Identify(imageBytes);
            }
            catch (Exception e)
            {
                throw new ArgumentException($"Not a readable image: {sourcePath} ({e.Message})", nameof(imageBytes), e);
            }

            if (info == null || info.Width <= 0 || info.Height <= 0)
                throw new ArgumentException($"Cannot read image dimensions: {sourcePath}", nameof(imageBytes));

            return Build(imageFormat, info.Width, info.Height, imageBytes, flags);
        }

        public static byte[] Build(
            FreeImageFormat imageFormat, int width, int height, byte[] imageBytes, TexFlags flags = TexFlags.None)
        {
            var tex = new Tex
            {
                Magic1 = "TEXV0005",
                Magic2 = "TEXI0001",
                Header = new TexHeader
                {
                    Format = TexFormat.RGBA8888,
                    Flags = flags,
                    TextureWidth = width,
                    TextureHeight = height,
                    ImageWidth = width,
                    ImageHeight = height,
                    UnkInt0 = 0
                },
                ImagesContainer = new TexImageContainer
                {
                    Magic = "TEXB0004",
                    // 读侧对 TEXB0004 + 非 MP4 会按 Version3 的 mip 记录解析，写侧必须停在 Version3 才闭合
                    ImageContainerVersion = TexImageContainerVersion.Version3,
                    ImageFormat = imageFormat
                }
            };

            tex.ImagesContainer.Images.Add(new TexImage
            {
                Mipmaps =
                {
                    new TexMipmap
                    {
                        Width = width,
                        Height = height,
                        Format = TexMipmapFormatGetter.GetFormatForTex(imageFormat, TexFormat.RGBA8888),
                        Bytes = imageBytes,
                        DecompressedBytesCount = 0,
                        IsLZ4Compressed = false
                    }
                }
            });

            using var ms = new MemoryStream();
            using (var writer = new BinaryWriter(ms, Encoding.UTF8, true))
                TexWriter.Default.WriteTo(writer, tex);

            return ms.ToArray();
        }
    }
}
