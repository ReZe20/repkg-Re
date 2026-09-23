using System;
using System.IO;
using System.Text;
using NUnit.Framework;
using RePKG_Re.Application.Package;
using RePKG_Re.Application.Texture;
using RePKG_Re.Core.Package;
using RePKG_Re.Core.Package.Interfaces;
using RePKG_Re.Core.Texture;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace RePKG_Re.Tests
{
    /// <summary>
    /// pkg → mpkg 转换的端到端测试（不落真机）：容器写回、音频丢弃、loose 清单内嵌、
    /// 直通编码图物化成直色 RGBA8，以及 TEXB0004 写侧的读写闭合。
    /// </summary>
    [TestFixture]
    public class MpkgConverterTests
    {
        private string _dir;

        [SetUp]
        public void SetUp() => _dir = Path.Combine(Path.GetTempPath(), "repkg-mpkg-" + Guid.NewGuid().ToString("N"));

        [TearDown]
        public void TearDown()
        {
            try { if (Directory.Exists(_dir)) Directory.Delete(_dir, true); } catch { }
        }

        private static byte[] MakePng(int w, int h)
        {
            using var image = new Image<Rgba32>(w, h);
            for (var y = 0; y < h; y++)
            for (var x = 0; x < w; x++)
                // 半透明 + 逐像素变化：直色/预乘两种口径在这里必然分道
                image[x, y] = new Rgba32((byte) (x * 7), (byte) (y * 11), 200, (byte) ((x + y) % 256));

            using var stream = new MemoryStream();
            image.SaveAsPng(stream);
            return stream.ToArray();
        }

        /// <summary>造一个"PC 侧直通编码图"的 TEX：TEXB0003 + ifmt=PNG，载荷就是 PNG 字节。</summary>
        private static byte[] MakePassthroughTex(byte[] png, int w, int h)
        {
            var tex = new Tex
            {
                Magic1 = "TEXV0005",
                Magic2 = "TEXI0001",
                Header = new TexHeader
                {
                    Format = TexFormat.RGBA8888,
                    Flags = TexFlags.ClampUVs,
                    TextureWidth = w,
                    TextureHeight = h,
                    ImageWidth = w,
                    ImageHeight = h
                },
                ImagesContainer = new TexImageContainer
                {
                    Magic = "TEXB0003",
                    ImageContainerVersion = TexImageContainerVersion.Version3,
                    ImageFormat = FreeImageFormat.FIF_PNG
                }
            };
            tex.ImagesContainer.Images.Add(new TexImage
            {
                Mipmaps =
                {
                    new TexMipmap
                    {
                        Width = w, Height = h, Format = MipmapFormat.ImagePNG,
                        Bytes = png, DecompressedBytesCount = png.Length, IsLZ4Compressed = false
                    }
                }
            });

            using var stream = new MemoryStream();
            using (var writer = new BinaryWriter(stream, Encoding.UTF8, true))
                TexWriter.Default.WriteTo(writer, tex);
            return stream.ToArray();
        }

        private string WritePackage(string name, params (string Path, byte[] Bytes)[] entries)
        {
            var package = new Package {Magic = "PKGV0023"};
            foreach (var (path, bytes) in entries)
                package.Entries.Add(new PackageEntry {FullPath = path, Bytes = bytes});

            var file = Path.Combine(_dir, name);
            Directory.CreateDirectory(_dir);
            using (var stream = File.Create(file))
            using (var writer = new BinaryWriter(stream, Encoding.UTF8, true))
            {
                IPackageWriter writerImpl = new PackageWriter();
                writerImpl.WriteTo(writer, package);
            }

            return file;
        }

        private static Package ReadPackage(string file, bool withBytes = true)
        {
            using var stream = File.OpenRead(file);
            using var reader = new BinaryReader(stream, Encoding.UTF8, true);
            return new PackageReader {ReadEntryBytes = withBytes}.ReadFrom(reader);
        }

        [Test]
        public void TestConvert_PassthroughTex_MaterializedToStraightRgba()
        {
            const int W = 37, H = 19; // 奇数尺寸：stride/对齐若有问题会立刻暴露
            var png = MakePng(W, H);
            var pkg = WritePackage("scene.pkg",
                ("materials/icon.tex", MakePassthroughTex(png, W, H)),
                ("sounds/track.mp3", Encoding.UTF8.GetBytes("pretend audio")),
                ("scene.json", Encoding.UTF8.GetBytes("{\"a\":1}")));

            var project = Path.Combine(_dir, "project.json");
            File.WriteAllText(project, "{\"type\":\"Scene\",\"file\":\"scene.json\"}");

            Directory.CreateDirectory(Path.Combine(_dir, "out"));
            var target = Path.Combine(_dir, "out", "scene.mpkg");

            var report = new MobilePackageConverter().Convert(pkg, target, new MobilePackageOptions
            {
                DropAudio = true,
                UseLz4 = true,
                ProjectJsonPath = project
            });

            Assert.AreEqual(0, report.Warnings.Count, "不该有物化失败/拒绝");
            Assert.AreEqual(1, report.Materialized);
            Assert.AreEqual(1, report.Dropped);
            Assert.AreEqual(1, report.Copied);

            var read = ReadPackage(target);
            Assert.AreEqual("PKGM0019", read.Magic);

            byName(read, out var icon, out var audio, out var scene, out var projectJson);
            Assert.IsNull(audio, "sounds/*.mp3 必须被丢弃");
            Assert.IsNotNull(scene);
            Assert.AreEqual(Encoding.UTF8.GetBytes("{\"a\":1}"), scene.Bytes);
            Assert.IsNotNull(projectJson, "loose project.json 必须内嵌");
            Assert.AreEqual(File.ReadAllText(project), Encoding.UTF8.GetString(projectJson.Bytes));

            // 物化结果必须能被 repkg 自己的读侧读回，且是直色 RGBA8
            var tex = ReadTex(icon.Bytes);
            var mip = tex.FirstImage.FirstMipmap;
            Assert.AreEqual(MipmapFormat.RGBA8888, mip.Format);
            Assert.AreEqual(TexFormat.RGBA8888, tex.Header.Format);
            Assert.AreEqual(W, mip.Width);
            Assert.AreEqual(H, mip.Height);
            Assert.AreEqual("TEXB0004", tex.ImagesContainer.Magic);
            Assert.AreEqual(FreeImageFormat.FIF_UNKNOWN, tex.ImagesContainer.ImageFormat);
            Assert.AreEqual(1, tex.ImagesContainer.Images.Count);
            Assert.AreEqual(1, mip.Bytes.Length / (W * H * 4));

            using var decoded = Image.Load<Rgba32>(png);
            Assert.AreEqual(StraightRgba(decoded), mip.Bytes, "物化像素必须是直色 RGBA（非预乘）");
        }

        private static byte[] StraightRgba(Image<Rgba32> image)
        {
            var bytes = new byte[image.Width * image.Height * 4];
            image.CopyPixelDataTo(System.Runtime.InteropServices.MemoryMarshal.Cast<byte, Rgba32>(bytes.AsSpan()));
            return bytes;
        }

        private static ITex ReadTex(byte[] bytes)
        {
            using var reader = new BinaryReader(new MemoryStream(bytes), Encoding.UTF8);
            return TexReader.Default.ReadFrom(reader);
        }

        private static void byName(Package p, out PackageEntry icon, out PackageEntry audio, out PackageEntry scene,
            out PackageEntry projectJson)
        {
            icon = audio = scene = projectJson = null;
            foreach (var e in p.Entries)
            {
                if (e.FullPath == "materials/icon.tex") icon = e;
                else if (e.FullPath.EndsWith(".mp3")) audio = e;
                else if (e.FullPath == "scene.json") scene = e;
                else if (e.FullPath == "project.json") projectJson = e;
            }
        }

        [Test]
        public void TestConvert_RawTex_IsCopiedByteForByte()
        {
            // 手工造一个"已是原始像素/DX 块"的 TEX：ifmt=-1，不该被物化路径碰
            var tex = new Tex
            {
                Magic1 = "TEXV0005",
                Magic2 = "TEXI0001",
                Header = new TexHeader {Format = TexFormat.DXT5, Flags = TexFlags.ClampUVs, TextureWidth = 4, TextureHeight = 4, ImageWidth = 4, ImageHeight = 4},
                ImagesContainer = new TexImageContainer
                {
                    Magic = "TEXB0003",
                    ImageContainerVersion = TexImageContainerVersion.Version3,
                    ImageFormat = FreeImageFormat.FIF_UNKNOWN
                }
            };
            tex.ImagesContainer.Images.Add(new TexImage
            {
                Mipmaps = {new TexMipmap {Width = 4, Height = 4, Format = MipmapFormat.CompressedDXT5, Bytes = new byte[32]}}
            });
            using var stream = new MemoryStream();
            using (var writer = new BinaryWriter(stream, Encoding.UTF8, true))
                TexWriter.Default.WriteTo(writer, tex);
            var dxt = stream.ToArray();

            var pkg = WritePackage("scene.pkg", ("materials/a.tex", dxt), ("readme.txt", Encoding.UTF8.GetBytes("x")));
            Directory.CreateDirectory(Path.Combine(_dir, "out"));
            var target = Path.Combine(_dir, "out", "scene.mpkg");

            var report = new MobilePackageConverter().Convert(pkg, target, new MobilePackageOptions {UseLz4 = false});

            Assert.AreEqual(0, report.Materialized, "非直通条目不该被动过");
            Assert.AreEqual(2, report.Copied);

            var read = ReadPackage(target);
            foreach (var e in read.Entries)
            {
                if (e.FullPath == "materials/a.tex") Assert.AreEqual(dxt, e.Bytes, "TEX 必须逐字节照搬");
                if (e.FullPath == "readme.txt") Assert.AreEqual(Encoding.UTF8.GetBytes("x"), e.Bytes);
            }
        }

        [Test]
        public void TestTexB0004_WriterRoundTrips()
        {
            var pixels = StraightRgba(MakePngImage(5, 6));
            var tex = new Tex
            {
                Magic1 = "TEXV0005",
                Magic2 = "TEXI0001",
                Header = new TexHeader {Format = TexFormat.RGBA8888, Flags = TexFlags.ClampUVs, TextureWidth = 5, TextureHeight = 6, ImageWidth = 5, ImageHeight = 6},
                ImagesContainer = new TexImageContainer
                {
                    // 读侧对 TEXB0004+非 MP4 会降级成 Version3 的 mip 记录，写出必须按同一个规则
                    Magic = "TEXB0004",
                    ImageContainerVersion = TexImageContainerVersion.Version3,
                    ImageFormat = FreeImageFormat.FIF_UNKNOWN
                }
            };
            tex.ImagesContainer.Images.Add(new TexImage
            {
                Mipmaps =
                {
                    new TexMipmap
                    {
                        Width = 5, Height = 6, Format = MipmapFormat.RGBA8888, Bytes = pixels,
                        DecompressedBytesCount = pixels.Length, IsLZ4Compressed = false
                    }
                }
            });

            using var stream = new MemoryStream();
            using (var writer = new BinaryWriter(stream, Encoding.UTF8, true))
                TexWriter.Default.WriteTo(writer, tex);

            var written = stream.ToArray();
            var back = ReadTex(written);

            Assert.AreEqual("TEXB0004", back.ImagesContainer.Magic);
            Assert.AreEqual(FreeImageFormat.FIF_UNKNOWN, back.ImagesContainer.ImageFormat);
            Assert.AreEqual(TexImageContainerVersion.Version3, back.ImagesContainer.ImageContainerVersion);
            var mip = back.FirstImage.FirstMipmap;
            Assert.AreEqual(5, mip.Width);
            Assert.AreEqual(6, mip.Height);
            Assert.AreEqual(pixels, mip.Bytes, "mip 记录错位就会在这里露出来");
            Assert.AreEqual(written.Length, stream.Length);
        }

        private static Image<Rgba32> MakePngImage(int w, int h)
        {
            var image = new Image<Rgba32>(w, h);
            for (var y = 0; y < h; y++)
            for (var x = 0; x < w; x++)
                image[x, y] = new Rgba32((byte) (x + 1), (byte) (y + 2), 3, 255);
            return image;
        }

        /// <summary>WE 的移动尺寸：先整除截断再补到 4 的倍数（真机量出来的公式，四组样本全中）。</summary>
        [TestCase(1332, 2, 668)]
        [TestCase(2048, 2, 1024)]
        [TestCase(2867, 2, 1436)]
        [TestCase(717, 2, 360)]
        [TestCase(717, 4, 180)]
        [TestCase(959, 4, 240)]
        [TestCase(3, 2, 4)] // 下限：补不出来就留 4，不能写 0 宽
        public void TestReduce_MatchesWeFormula(int size, int divisor, int expected)
        {
            Assert.AreEqual(expected, MobileTextureMaterializer.Reduce(size, divisor));
        }

        [Test]
        public void TestConvert_Reduction2_ShrinksPixels_AndKeepsHeaderSizes()
        {
            const int W = 37, H = 19; // → 20x12
            var png = MakePng(W, H);
            // 头部故意写块对齐后的 40x20（真包里 2636878454 的 7d2ONWk.tex 是 ifmt=2 的 JPEG 直通，
            // 头部报 2048x2048 而图本身是 1332x2048），
            // 这样"照抄头部"和"写解码出来的真实尺寸"两种口径能分出来
            var pkg = WritePackage("scene.pkg",
                ("materials/icon.tex", MakePassthroughTex(png, 40, 20)),
                ("scene.json", Encoding.UTF8.GetBytes(WeStyleSceneJson)));

            Directory.CreateDirectory(Path.Combine(_dir, "out"));
            var target = Path.Combine(_dir, "out", "scene.mpkg");

            var report = new MobilePackageConverter().Convert(pkg, target, new MobilePackageOptions
            {
                Reduction = 2,
                UseLz4 = false
            });

            Assert.AreEqual(0, report.Warnings.Count, string.Join(" | ", report.Warnings));
            Assert.AreEqual(1, report.Materialized);
            Assert.AreEqual(1, report.Reduced, "报告要说明这次是真缩了");
            Assert.IsTrue(report.ReductionRecorded, "缩了就得把键写进 scene.json");

            PackageEntry icon = null, scene = null;
            foreach (var e in ReadPackage(target).Entries)
            {
                if (e.FullPath == "materials/icon.tex") icon = e;
                if (e.FullPath == "scene.json") scene = e;
            }

            var tex = ReadTex(icon.Bytes);
            var mip = tex.FirstImage.FirstMipmap;
            Assert.AreEqual(20, mip.Width);
            Assert.AreEqual(12, mip.Height);
            Assert.AreEqual(20 * 12 * 4, mip.Bytes.Length);

            // 头部四个尺寸字段写"解码出来的原始尺寸"，不是缩小后的、也不是源头部那个块对齐值
            // （R5 那次真机翻车证明移动端读这四个字段：把它们改成缩小后的尺寸，一块背景就没了）
            Assert.AreEqual(W, tex.Header.ImageWidth);
            Assert.AreEqual(H, tex.Header.ImageHeight);
            Assert.AreEqual(W, tex.Header.TextureWidth);
            Assert.AreEqual(H, tex.Header.TextureHeight);

            Assert.AreEqual(WeStyleSceneJsonReduced2, Encoding.UTF8.GetString(scene.Bytes),
                "注入的那一行要逐字节对齐 WE 的排版：制表符缩进、CRLF、按字母序插在 skylightcolor 之后");
        }

        /// <summary>
        /// 读侧解出来的 DX 像素到底是 R,G,B,A 还是 B,G,R,A —— ETC2 重编那条路整个建立在这个假设上，
        /// 而单靠自洽的编解码对照永远查不出它。这里用一个人手搓得出来的 DXT5 块钉死：
        /// color0 = 0xF800 按 DXT5 规范就是 565 的纯红（高 5 位是 R），选择子全 0 = 整块取 color0，
        /// alpha0 = 255 + 索引全 0 = 恒定不透明。所以这块解出来必须是"红 + 不透明"，逐字节看谁在 0 位。
        /// </summary>
        [Test]
        public void TestRead_Dxt5Block_ByteOrderIsRgba()
        {
            var red = new byte[]
            {
                0xff, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,     // alpha0=255，其余索引 0 → 全不透明
                0x00, 0xf8, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00      // color0=0xF800=565 红，color1=0，选择子全 0
            };
            var tex = ReadDxt5(red);
            var mip = tex.ImagesContainer.Images[0].FirstMipmap;
            Assert.That(mip.Bytes, Has.Length.EqualTo(4 * 4 * 4), "4x4 的 RGBA8");
            Assert.That(mip.Format, Is.EqualTo(MipmapFormat.RGBA8888), "解完就该是 RGBA8");

            TestContext.Out.WriteLine($"块内前 4 字节 = {mip.Bytes[0]},{mip.Bytes[1]},{mip.Bytes[2]},{mip.Bytes[3]}");
            Assert.That(mip.Bytes[3], Is.EqualTo(255), "alpha 在第 3 字节");
            // 565 的红 = (11111) → 补到 8 位是 11111111 = 255（低位用高位填），不是 248
            Assert.That(mip.Bytes[0], Is.EqualTo(255), "红在第 0 字节（R,G,B,A）");
            Assert.That(mip.Bytes[1], Is.EqualTo(0), "绿在第 1 字节");
            Assert.That(mip.Bytes[2], Is.EqualTo(0), "蓝在第 2 字节");
        }

        private static ITex ReadDxt5(byte[] blocks)
        {
            var bytes = MakeDxt5Tex(blocks, 4, 4);
            using var reader = new BinaryReader(new MemoryStream(bytes), Encoding.UTF8);
            return TexReader.Default.ReadFrom(reader);
        }

        /// <summary>造一个 DXT5 的 TEX：块按行主序排，读侧会自动解成 RGBA8。</summary>
        private static byte[] MakeDxt5Tex(byte[] blocks, int pixelWidth, int pixelHeight)
        {
            var tex = new Tex
            {
                Magic1 = "TEXV0005",
                Magic2 = "TEXI0001",
                Header = new TexHeader
                {
                    Format = TexFormat.DXT5,
                    Flags = TexFlags.ClampUVs,
                    TextureWidth = pixelWidth,
                    TextureHeight = pixelHeight,
                    ImageWidth = pixelWidth,
                    ImageHeight = pixelHeight
                },
                ImagesContainer = new TexImageContainer
                {
                    Magic = "TEXB0003",
                    ImageContainerVersion = TexImageContainerVersion.Version3,
                    ImageFormat = FreeImageFormat.FIF_UNKNOWN
                }
            };
            tex.ImagesContainer.Images.Add(new TexImage
            {
                Mipmaps =
                {
                    new TexMipmap
                    {
                        Width = pixelWidth, Height = pixelHeight, Format = MipmapFormat.CompressedDXT5,
                        Bytes = blocks, DecompressedBytesCount = blocks.Length, IsLZ4Compressed = false
                    }
                }
            });

            using var stream = new MemoryStream();
            using (var writer = new BinaryWriter(stream, Encoding.UTF8, true))
                TexWriter.Default.WriteTo(writer, tex);
            return stream.ToArray();
        }

        // DXT5 的两个标准块：alpha0=255 + 选择子全 0 = 恒定不透明；颜色 color0 决定整块色
        private static readonly byte[] OpaqueWhiteDxt5 =
        {
            0xff, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
            0xff, 0xff, 0xff, 0xff, 0x00, 0x00, 0x00, 0x00
        };

        private static readonly byte[] OpaqueBlackDxt5 =
        {
            0xff, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
            0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00
        };

        private static byte[] Blocks(params byte[][] blocks)
        {
            var output = new byte[blocks.Length * 16];
            for (var i = 0; i < blocks.Length; i++)
                Array.Copy(blocks[i], 0, output, i * 16, 16);
            return output;
        }

        [Test]
        public void TestConvert_Dxt5_WithReductionAndEtc2_IsReencoded()
        {
            // 8x8 = 2x2 块，左半白右半黑
            var dxt = MakeDxt5Tex(Blocks(OpaqueWhiteDxt5, OpaqueBlackDxt5, OpaqueWhiteDxt5, OpaqueBlackDxt5), 8, 8);
            var pkg = WritePackage("scene.pkg", ("materials/a.tex", dxt));
            Directory.CreateDirectory(Path.Combine(_dir, "out"));
            var target = Path.Combine(_dir, "out", "scene.mpkg");

            var report = new MobilePackageConverter().Convert(pkg, target, new MobilePackageOptions
            {
                Reduction = 2,
                EncodeEtc2 = true,
                UseLz4 = false
            });

            Assert.AreEqual(0, report.Warnings.Count, string.Join(" | ", report.Warnings));
            Assert.AreEqual(1, report.Etc2Encoded, "DXT5 在「缩小 + 开编码」时要走重编");

            byte[] bytes = null;
            foreach (var e in ReadPackage(target).Entries)
                if (e.FullPath == "materials/a.tex") bytes = e.Bytes;

            var tex = ReadTex(bytes);
            var mip = tex.FirstImage.FirstMipmap;
            Assert.AreEqual(TexFormat.ETC2_RGBA8, tex.Header.Format);
            Assert.AreEqual(4, mip.Width);
            Assert.AreEqual(4, mip.Height);
            Assert.AreEqual(16, mip.Bytes.Length, "ETC2 RGBA8 = 1 字节/像素");
            Assert.AreEqual(16, mip.DecompressedBytesCount);
            // 头部仍写原始逻辑尺寸（R5 那条真机结论对 DX 路同样成立）
            Assert.AreEqual(8, tex.Header.ImageWidth);
            Assert.AreEqual(8, tex.Header.ImageHeight);
        }

        [Test]
        public void TestConvert_Dxt5_WithoutEtc2_StillByteForByte()
        {
            var dxt = MakeDxt5Tex(Blocks(OpaqueWhiteDxt5, OpaqueBlackDxt5, OpaqueWhiteDxt5, OpaqueBlackDxt5), 8, 8);
            var pkg = WritePackage("scene.pkg", ("materials/a.tex", dxt));
            Directory.CreateDirectory(Path.Combine(_dir, "out"));
            var target = Path.Combine(_dir, "out", "scene.mpkg");

            var report = new MobilePackageConverter().Convert(pkg, target, new MobilePackageOptions
            {
                Reduction = 2,
                EncodeEtc2 = false,
                UseLz4 = false
            });

            Assert.AreEqual(0, report.Etc2Encoded);
            foreach (var e in ReadPackage(target).Entries)
                if (e.FullPath == "materials/a.tex")
                    Assert.AreEqual(dxt, e.Bytes, "不开编码时 DXT 必须逐字节照搬（真机验过的形态）");
        }

        /// <summary>缩小路径的像素内容闸门：全零载荷在"只查尺寸"的断言下能一路通过，真机才会露馅。</summary>
        [Test]
        public void TestConvert_Reduction2_PixelsAreResizedContent_NotBlank()
        {
            const int W = 64, H = 64; // → 32x32
            byte[] png;
            using (var image = new Image<Rgba32>(W, H))
            {
                for (var y = 0; y < H; y++)
                for (var x = 0; x < W; x++)
                {
                    var v = (byte) (255 - y * 4); // 上白下黑
                    image[x, y] = new Rgba32(v, v, v, 255);
                }

                using var stream = new MemoryStream();
                image.SaveAsPng(stream);
                png = stream.ToArray();
            }

            var pkg = WritePackage("scene.pkg", ("materials/grad.tex", MakePassthroughTex(png, W, H)));
            Directory.CreateDirectory(Path.Combine(_dir, "out"));
            var target = Path.Combine(_dir, "out", "scene.mpkg");

            new MobilePackageConverter().Convert(pkg, target, new MobilePackageOptions {Reduction = 2, UseLz4 = false});

            byte[] pixels = null;
            foreach (var e in ReadPackage(target).Entries)
                if (e.FullPath == "materials/grad.tex")
                    pixels = ReadTex(e.Bytes).FirstImage.FirstMipmap.Bytes;

            Assert.IsNotNull(pixels);
            Assert.AreEqual(32, pixels.Length / (32 * 4));
            var top = pixels[0];
            var bottom = pixels[(32 * 31) * 4];
            Assert.Greater(top, 200, $"第一行该是亮的（源顶行 255），实际 {top}");
            Assert.Less(bottom, 60, $"最后一行该是暗的（源底行 1 附近），实际 {bottom}");
            Assert.Greater(pixels[3], 200, "alpha 不该被缩小弄没");
        }

        [Test]
        public void TestConvert_Reduction2WithEtc2_EmitsFormat5()
        {
            const int W = 37, H = 19; // → 20x12，正好 5x3=15 块
            var png = MakePng(W, H);
            var pkg = WritePackage("scene.pkg",
                ("materials/icon.tex", MakePassthroughTex(png, W, H)),
                ("scene.json", Encoding.UTF8.GetBytes(WeStyleSceneJson)));

            Directory.CreateDirectory(Path.Combine(_dir, "out"));
            var target = Path.Combine(_dir, "out", "scene.mpkg");

            var report = new MobilePackageConverter().Convert(pkg, target, new MobilePackageOptions
            {
                Reduction = 2,
                EncodeEtc2 = true,
                UseLz4 = false
            });

            Assert.AreEqual(0, report.Warnings.Count, string.Join(" | ", report.Warnings));
            Assert.AreEqual(1, report.Etc2Encoded, "报告要说明这次发的是 fmt5");

            PackageEntry icon = null;
            foreach (var e in ReadPackage(target).Entries)
                if (e.FullPath == "materials/icon.tex") icon = e;

            // 读侧现在认 format=5：容器要能闭合，字节不该被当成像素解或 DXT 解
            var tex = ReadTex(icon.Bytes);
            var mip = tex.FirstImage.FirstMipmap;
            Assert.AreEqual(TexFormat.ETC2_RGBA8, tex.Header.Format);
            Assert.AreEqual(MipmapFormat.CompressedETC2RGBA8, mip.Format);
            Assert.AreEqual(20, mip.Width);
            Assert.AreEqual(12, mip.Height);
            Assert.AreEqual(20 * 12, mip.Bytes.Length, "ETC2 RGBA8 是 1 字节/像素");
            Assert.AreEqual(20 * 12, mip.DecompressedBytesCount);

            // 头部四个尺寸字段照旧留原始尺寸：这条和 fmt0 那版唯一的区别就是 format 那一个 int32
            Assert.AreEqual(W, tex.Header.ImageWidth);
            Assert.AreEqual(H, tex.Header.TextureHeight);
        }

        [Test]
        public void TestConvert_Etc2WithoutReduction_StaysOnTheVerifiedRgba8Shape()
        {
            const int W = 37, H = 19;
            var png = MakePng(W, H);
            var pkg = WritePackage("scene.pkg", ("materials/icon.tex", MakePassthroughTex(png, W, H)));

            Directory.CreateDirectory(Path.Combine(_dir, "out"));
            var target = Path.Combine(_dir, "out", "scene.mpkg");

            var report = new MobilePackageConverter().Convert(pkg, target, new MobilePackageOptions
            {
                Reduction = 1,
                EncodeEtc2 = true,
                UseLz4 = false
            });

            Assert.AreEqual(0, report.Etc2Encoded);
            foreach (var e in ReadPackage(target).Entries)
            {
                if (e.FullPath != "materials/icon.tex") continue;
                var tex = ReadTex(e.Bytes);
                Assert.AreEqual(TexFormat.RGBA8888, tex.Header.Format, "÷1 是真机逐字节验过的形态，不该被换掉");
                Assert.AreEqual(W * H * 4, tex.FirstImage.FirstMipmap.Bytes.Length);
            }
        }

        [Test]
        public void TestConvert_NoReduction_LeavesSceneJsonByteIdentical()
        {
            var pkg = WritePackage("scene.pkg", ("scene.json", Encoding.UTF8.GetBytes(WeStyleSceneJson)));
            Directory.CreateDirectory(Path.Combine(_dir, "out"));
            var target = Path.Combine(_dir, "out", "scene.mpkg");

            var report = new MobilePackageConverter().Convert(pkg, target, new MobilePackageOptions {Reduction = 1});

            Assert.IsFalse(report.ReductionRecorded);
            Assert.AreEqual(0, report.Warnings.Count);

            foreach (var e in ReadPackage(target).Entries)
                if (e.FullPath == "scene.json")
                    Assert.AreEqual(WeStyleSceneJson, Encoding.UTF8.GetString(e.Bytes), "不缩就一个字节都不能动");
        }

        [Test]
        public void TestSceneJsonPatcher_ReplacesExistingKey_InPlace()
        {
            var patched = SceneJsonPatcher.SetTextureReduction(
                Encoding.UTF8.GetBytes(WeStyleSceneJsonReduced2), 4, out var failure);

            Assert.IsNull(failure);
            Assert.IsNotNull(patched);
            var text = Encoding.UTF8.GetString(patched);
            Assert.IsTrue(text.Contains("\"texturereduction\" : 4,"), text);
            Assert.AreEqual(2, Count(text, "texturereduction"), "换值不该变成追加第二个同名键");
            Assert.IsFalse(text.Contains("\"texturereduction\" : 2,"));
            Assert.IsTrue(text.Contains("\"texturereduction\" : 99,"), "objects 里那个同名诱饵不能被碰");
        }

        [Test]
        public void TestSceneJsonPatcher_RefusesWhenNoGeneralBlock()
        {
            // 找不到 general 就不能硬塞：宁可不写这个键，让上层报出来
            var patched = SceneJsonPatcher.SetTextureReduction(Encoding.UTF8.GetBytes("{\"a\":1}"), 2, out var failure);
            Assert.IsNull(patched);
            Assert.IsNotNull(failure);
        }

        private static int Count(string haystack, string needle)
        {
            var n = 0;
            for (var i = haystack.IndexOf(needle, StringComparison.Ordinal);
                 i >= 0;
                 i = haystack.IndexOf(needle, i + 1, StringComparison.Ordinal))
                n++;
            return n;
        }

        // WE 自己的 scene.json 排版：制表符缩进、CRLF、"key" : value、成员按字母序、'{' 单独占一行
        private const string WeStyleSceneJson =
            "{\r\n" +
            "\t\"general\" : \r\n" +
            "\t{\r\n" +
            "\t\t\"hdr\" : true,\r\n" +
            "\t\t\"nearz\" : 0.0099999998,\r\n" +
            "\t\t\"skylightcolor\" : \"0.30000 0.30000 0.30000\",\r\n" +
            "\t\t\"zoom\" : 1.0\r\n" +
            "\t},\r\n" +
            "\t\"objects\" : \r\n" +
            "\t[\r\n" +
            "\t\t{\r\n" +
            "\t\t\t\"texturereduction\" : 99,\r\n" +
            "\t\t\t\"visible\" : true\r\n" +
            "\t\t}\r\n" +
            "\t],\r\n" +
            "\t\"version\" : 1\r\n" +
            "}";

        // 只有 general 块里多了一行；objects 里那个同名键是诱饵，不该被碰
        private const string WeStyleSceneJsonReduced2 =
            "{\r\n" +
            "\t\"general\" : \r\n" +
            "\t{\r\n" +
            "\t\t\"hdr\" : true,\r\n" +
            "\t\t\"nearz\" : 0.0099999998,\r\n" +
            "\t\t\"skylightcolor\" : \"0.30000 0.30000 0.30000\",\r\n" +
            "\t\t\"texturereduction\" : 2,\r\n" +
            "\t\t\"zoom\" : 1.0\r\n" +
            "\t},\r\n" +
            "\t\"objects\" : \r\n" +
            "\t[\r\n" +
            "\t\t{\r\n" +
            "\t\t\t\"texturereduction\" : 99,\r\n" +
            "\t\t\t\"visible\" : true\r\n" +
            "\t\t}\r\n" +
            "\t],\r\n" +
            "\t\"version\" : 1\r\n" +
            "}";
    }
}
