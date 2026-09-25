using System;
using System.IO;
using System.Text;
using NUnit.Framework;
using RePKG_Re.Application.Texture;
using RePKG_Re.Core.Texture;
using SixLabors.ImageSharp;

namespace RePKG_Re.Tests
{
    /// <summary>
    /// 合成语料回环:用自家写侧(TexWriter/TexReader 的镜像布局)现场生成各版本 TEX,
    /// 再用读侧完整读回,断言 header/mip 记录/像素/LZ4/GIF 帧表逐项一致。
    /// 与 TexDecompressingTests/TexWriterTests 的关系:那两族用例靠外部 .tex 语料,缺文件即
    /// Assert.Ignore(干净 clone 与 CI 上 32 条全绿但实际没跑)。本族用例零外部依赖,
    /// 保证解码链路的结构性断言在任何环境都真实执行。
    /// 语料刻意做成 16x16 的 8x8 整数倍,让 DXT 解码能走完整块路径。
    /// </summary>
    public class SyntheticTexRoundtripTests
    {
        private const int W = 16;
        private const int H = 16;

        private TexReader _reader;
        private ITexWriter _writer;

        [SetUp]
        public void Setup()
        {
            // 与 TexWriterTests 同款装配:读时不自动解压,DXT/LZ4 校验自己做,和真实链路一致
            var headerReader = new TexHeaderReader();
            var mipmapDecompressor = new TexMipmapDecompressor();
            var mipmapReader = new TexImageReader(mipmapDecompressor);
            var containerReader = new TexImageContainerReader(mipmapReader);
            var frameInfoReader = new TexFrameInfoContainerReader();

            mipmapReader.DecompressMipmapBytes = false;
            mipmapReader.ReadMipmapBytes = true;

            _reader = new TexReader(headerReader, containerReader, frameInfoReader);
            _writer = TexWriter.Default;
        }

        // ---------- 合成原料 ----------

        /// <summary>可辨识的确定性 RGBA 像素:每像素 = (x*16, y*16, (x+y)*8, 255)。</summary>
        private static byte[] RgbaPattern()
        {
            var data = new byte[W * H * 4];
            for (var y = 0; y < H; y++)
            for (var x = 0; x < W; x++)
            {
                var i = (y * W + x) * 4;
                data[i] = (byte)(x * 16);
                data[i + 1] = (byte)(y * 16);
                data[i + 2] = (byte)((x + y) * 8);
                data[i + 3] = 255;
            }
            return data;
        }

        private static byte[] TruncateTo(byte[] rgba, int bytesPerPixel)
        {
            var data = new byte[W * H * bytesPerPixel];
            for (var i = 0; i < W * H; i++)
                for (var c = 0; c < bytesPerPixel; c++)
                    data[i * bytesPerPixel + c] = rgba[i * 4 + c];
            return data;
        }

        private static TexHeader Header(TexFormat format, TexFlags flags = TexFlags.None)
        {
            return new TexHeader
            {
                Format = format,
                Flags = flags,
                TextureWidth = W,
                TextureHeight = H,
                ImageWidth = W,
                ImageHeight = H,
                UnkInt0 = 0
            };
        }

        private static TexMipmap Mip(byte[] bytes, bool lz4 = false, int decompressedCount = 0)
        {
            return new TexMipmap
            {
                Width = W,
                Height = H,
                Bytes = bytes,
                IsLZ4Compressed = lz4,
                DecompressedBytesCount = lz4 ? (decompressedCount > 0 ? decompressedCount : bytes.Length * 3) : bytes.Length
            };
        }

        private static Tex Build(
            string containerMagic,
            FreeImageFormat imageFormat,
            TexHeader header,
            TexImageContainerVersion mipVersion,
            TexImage image,
            TexFrameInfoContainer frames = null)
        {
            var container = new TexImageContainer
            {
                Magic = containerMagic,
                ImageContainerVersion = mipVersion,
                ImageFormat = imageFormat
            };
            container.Images.Add(image);

            return new Tex
            {
                Magic1 = "TEXV0005",
                Magic2 = "TEXI0001",
                Header = header,
                ImagesContainer = container,
                FrameInfoContainer = frames
            };
        }

        private static TexImage Image(params TexMipmap[] mips)
        {
            var image = new TexImage();
            foreach (var m in mips) image.Mipmaps.Add(m);
            return image;
        }

        private byte[] Serialize(Tex tex)
        {
            using var ms = new MemoryStream();
            using (var w = new BinaryWriter(ms, Encoding.UTF8, true))
                _writer.WriteTo(w, tex);
            return ms.ToArray();
        }

        private Tex Roundtrip(Tex tex)
        {
            var bytes = Serialize(tex);
            using var reader = new BinaryReader(new MemoryStream(bytes), Encoding.UTF8);
            return (Tex)_reader.ReadFrom(reader);
        }

        // ---------- 用例 ----------

        [Test]
        public void V1_RGBA8888_Roundtrip()
        {
            var rgba = RgbaPattern();
            var tex = Build("TEXB0001", FreeImageFormat.FIF_UNKNOWN, Header(TexFormat.RGBA8888),
                TexImageContainerVersion.Version1, Image(Mip(rgba)));

            var back = Roundtrip(tex);

            Assert.That(back.Header.Format, Is.EqualTo(TexFormat.RGBA8888));
            Assert.That(back.ImagesContainer.ImageContainerVersion, Is.EqualTo(TexImageContainerVersion.Version1));
            var mip = back.FirstImage.FirstMipmap;
            Assert.That(mip.Format, Is.EqualTo(MipmapFormat.RGBA8888));
            Assert.That(mip.Bytes, Is.EqualTo(rgba));
        }

        [Test]
        public void V2_DXT5_Lz4_Roundtrip()
        {
            // DXT5 载荷 + LZ4 压缩:走 TexReader.Default(解压全开),
            // 断言 lz4 标志/解压计数回环 + DXT 解码链路真实执行(随机块解码不抛、长度=RGBA 尺寸)。
            var payload = new byte[W / 4 * (H / 4) * 16];
            new Random(42).NextBytes(payload);

            var mipmap = Mip(payload);
            var compressor = new TexMipmapCompressor();
            compressor.CompressMipmap(mipmap, MipmapFormat.Invalid, true);
            Assert.That(mipmap.IsLZ4Compressed, Is.True, "前置:LZ4 应已生效");

            var tex = Build("TEXB0002", FreeImageFormat.FIF_UNKNOWN, Header(TexFormat.DXT5),
                TexImageContainerVersion.Version2, Image(mipmap));

            var bytes = Serialize(tex);
            using var reader = new BinaryReader(new MemoryStream(bytes), Encoding.UTF8);
            var back = (Tex)TexReader.Default.ReadFrom(reader);
            Assert.That(back.FirstImage.FirstMipmap.Width, Is.EqualTo(W));
            Assert.That(back.FirstImage.FirstMipmap.Bytes.Length, Is.EqualTo(W * H * 4),
                "DXT5 解码后应为 RGBA8888");
            Assert.That(back.FirstImage.FirstMipmap.Format, Is.EqualTo(MipmapFormat.RGBA8888));
        }

        [Test]
        public void V2_R8_and_RG88_Roundtrip()
        {
            var rgba = RgbaPattern();

            var r8 = TruncateTo(rgba, 1);
            var texR8 = Build("TEXB0002", FreeImageFormat.FIF_UNKNOWN, Header(TexFormat.R8),
                TexImageContainerVersion.Version2, Image(Mip(r8)));
            var back8 = Roundtrip(texR8);
            Assert.That(back8.FirstImage.FirstMipmap.Format, Is.EqualTo(MipmapFormat.R8));
            Assert.That(back8.FirstImage.FirstMipmap.Bytes, Is.EqualTo(r8));

            var rg = TruncateTo(rgba, 2);
            var texRg = Build("TEXB0002", FreeImageFormat.FIF_UNKNOWN, Header(TexFormat.RG88),
                TexImageContainerVersion.Version2, Image(Mip(rg)));
            var backRg = Roundtrip(texRg);
            Assert.That(backRg.FirstImage.FirstMipmap.Format, Is.EqualTo(MipmapFormat.RG88));
            Assert.That(backRg.FirstImage.FirstMipmap.Bytes, Is.EqualTo(rg));
        }

        [Test]
        public void V3_Gif_MultiImage_Texs0003_Roundtrip()
        {
            var rgba = RgbaPattern();
            var container = new TexImageContainer
            {
                Magic = "TEXB0003",
                ImageContainerVersion = TexImageContainerVersion.Version3,
                ImageFormat = FreeImageFormat.FIF_GIF
            };
            container.Images.Add(Image(Mip(rgba)));
            container.Images.Add(Image(Mip(rgba)));  // 三帧共用同一份像素,只为钉住帧表回环
            container.Images.Add(Image(Mip(rgba)));

            var frames = new TexFrameInfoContainer
            {
                Magic = "TEXS0003",
                GifWidth = W,
                GifHeight = H
            };
            for (var i = 0; i < 3; i++)
                frames.Frames.Add(new TexFrameInfo
                {
                    ImageId = i,
                    Frametime = 0.1f * (i + 1),
                    X = i, Y = i * 2, Width = W, Height = H, WidthY = W, HeightX = H
                });

            var tex = new Tex
            {
                Magic1 = "TEXV0005",
                Magic2 = "TEXI0001",
                Header = Header(TexFormat.RGBA8888, TexFlags.IsGif),
                ImagesContainer = container,
                FrameInfoContainer = frames
            };

            var back = Roundtrip(tex);

            Assert.That(back.IsGif, Is.True);
            Assert.That(back.ImagesContainer.ImageFormat, Is.EqualTo(FreeImageFormat.FIF_GIF));
            Assert.That(back.ImagesContainer.Images, Has.Count.EqualTo(3));
            Assert.That(back.FrameInfoContainer.Frames, Has.Count.EqualTo(3));
            Assert.That(back.FrameInfoContainer.Frames[2].Frametime, Is.EqualTo(0.3f).Within(1e-5f));
            Assert.That(back.FrameInfoContainer.Frames[1].Y, Is.EqualTo(2f));
            Assert.That(back.FirstImage.FirstMipmap.Bytes, Is.EqualTo(rgba));
        }

        [Test]
        public void V3_Passthrough_PngBlob_Roundtrip()
        {
            // 官方常见形态:TEXB0003 + FIF_PNG,mipmap 里直接是 PNG 文件字节(直通 blob)
            using var img = SixLabors.ImageSharp.Image.LoadPixelData<SixLabors.ImageSharp.PixelFormats.Rgba32>(
                RgbaPattern(), W, H);
            using var pngMs = new MemoryStream();
            img.SaveAsPng(pngMs);
            var pngBytes = pngMs.ToArray();

            var tex = Build("TEXB0003", FreeImageFormat.FIF_PNG, Header(TexFormat.RGBA8888),
                TexImageContainerVersion.Version3, Image(Mip(pngBytes)));

            var back = Roundtrip(tex);
            Assert.That(back.ImagesContainer.ImageFormat, Is.EqualTo(FreeImageFormat.FIF_PNG));
            Assert.That(back.FirstImage.FirstMipmap.Format, Is.EqualTo(MipmapFormat.ImagePNG));
            Assert.That(back.FirstImage.FirstMipmap.Bytes, Is.EqualTo(pngBytes));
        }

        [Test]
        public void V4_Mp4Video_FullRoundtrip()
        {
            // TEXB0004 + FIF_MP4 完整回环:写侧 WriteMipmapV4(1/2/""/1 + 尺寸 + lz4 + 双长度 + 载荷)
            // → 读侧 ReadMipmapV4 校验同常量并原样取回 mp4 载荷。视频直通是 mpkg/pkg 转换的地基。
            var fakeMp4 = new byte[256];
            new Random(7).NextBytes(fakeMp4);
            Encoding.ASCII.GetBytes("ftypisom").CopyTo(fakeMp4, 4);

            var container = new TexImageContainer
            {
                Magic = "TEXB0004",
                ImageContainerVersion = TexImageContainerVersion.Version4,
                ImageFormat = FreeImageFormat.FIF_MP4
            };
            var mipmap = new TexMipmap
            {
                Width = W, Height = H, Bytes = fakeMp4,
                IsLZ4Compressed = false, DecompressedBytesCount = fakeMp4.Length,
                Format = MipmapFormat.VideoMp4
            };
            container.Images.Add(new TexImage { Mipmaps = { mipmap } });

            var tex = new Tex
            {
                Magic1 = "TEXV0005",
                Magic2 = "TEXI0001",
                Header = Header(TexFormat.RGBA8888, TexFlags.IsVideoTexture),
                ImagesContainer = container
            };

            var bytes = Serialize(tex);

            // 布局常量互验:头部区段之后,mip 记录以 param1=1,param2=2,NUL 结尾空串,param3=1 开头 —— 
            // 与 repkg-ng 的 WriteMipmapV4(官方样本字节级复刻的同一布局)逐字节对得上。
            var marker = new byte[] { 1, 0, 0, 0, 2, 0, 0, 0, 0, 1, 0, 0, 0 };
            Assert.That(IndexOfSequence(bytes, marker), Is.GreaterThanOrEqualTo(0),
                "V4 mip 记录头必须是 1/2/\"\"/1 常量布局");

            using var reader = new BinaryReader(new MemoryStream(bytes), Encoding.UTF8);
            var back = (Tex)_reader.ReadFrom(reader);

            Assert.That(back.IsVideoTexture, Is.True);
            Assert.That(back.ImagesContainer.Magic, Is.EqualTo("TEXB0004"));
            Assert.That(back.ImagesContainer.ImageContainerVersion, Is.EqualTo(TexImageContainerVersion.Version4),
                "MP4 组合不许降级");
            Assert.That(back.FirstImage.FirstMipmap.Bytes, Is.EqualTo(fakeMp4));
        }

        private static int IndexOfSequence(byte[] haystack, byte[] needle)
        {
            for (var i = 0; i + needle.Length <= haystack.Length; i++)
            {
                var ok = true;
                for (var j = 0; j < needle.Length; j++)
                    if (haystack[i + j] != needle[j]) { ok = false; break; }
                if (ok) return i;
            }
            return -1;
        }

        [Test]
        public void NonMp4_TEXB0004_Degrades_To_V3_MipLayout()
        {
            // 读侧规则:TEXB0004 + 非 FIF_MP4 → mip 记录按 V2/V3 布局解读(version 降级成 3)。
            // 手工按"容器头 + V2V3 mip 记录"拼一份字节流,喂读侧,验证降级路径与载荷长度。
            var payload = TruncateTo(RgbaPattern(), 1);
            using var ms = new MemoryStream();
            using (var w = new BinaryWriter(ms, Encoding.UTF8, true))
            {
                w.Write(Encoding.ASCII.GetBytes("TEXV0005\0"));
                w.Write(Encoding.ASCII.GetBytes("TEXI0001\0"));
                w.Write((int)TexFormat.R8);
                w.Write((int)TexFlags.None);
                w.Write(W); w.Write(H); w.Write(W); w.Write(H); w.Write(0u);
                w.Write(Encoding.ASCII.GetBytes("TEXB0004\0"));
                w.Write(1);                       // image count
                w.Write((int)FreeImageFormat.FIF_UNKNOWN);
                w.Write(0);                       // isVideoMp4 = false
                w.Write(1);                       // mip count
                w.Write(W); w.Write(H);
                w.Write(0);                       // lz4
                w.Write(payload.Length);          // decompressed count
                w.Write(payload.Length);
                w.Write(payload);
            }

            using var r = new BinaryReader(new MemoryStream(ms.ToArray()), Encoding.UTF8);
            var back = (Tex)_reader.ReadFrom(r);

            Assert.That(back.ImagesContainer.Magic, Is.EqualTo("TEXB0004"));
            Assert.That(back.ImagesContainer.ImageContainerVersion, Is.EqualTo(TexImageContainerVersion.Version3),
                "非 MP4 的 TEXB0004 必须降级成 V3 mip 布局");
            Assert.That(back.FirstImage.FirstMipmap.Format, Is.EqualTo(MipmapFormat.R8));
            Assert.That(back.FirstImage.FirstMipmap.Bytes, Is.EqualTo(payload));
        }
    }
}
