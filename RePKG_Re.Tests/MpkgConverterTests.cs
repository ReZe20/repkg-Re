using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
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

        [Test]
        public void TestConvert_Dxt5_ShrinkDxWithoutEtc2_ReshrinksToRgba8()
        {
            var dxt = MakeDxt5Tex(Blocks(OpaqueWhiteDxt5, OpaqueBlackDxt5, OpaqueWhiteDxt5, OpaqueBlackDxt5), 8, 8);
            var pkg = WritePackage("scene.pkg",
                ("materials/a.tex", dxt),
                ("scene.json", Encoding.UTF8.GetBytes(WeStyleSceneJson)));
            Directory.CreateDirectory(Path.Combine(_dir, "out"));
            var target = Path.Combine(_dir, "out", "scene.mpkg");

            var report = new MobilePackageConverter().Convert(pkg, target, new MobilePackageOptions
            {
                Reduction = 2,
                EncodeEtc2 = false,
                ShrinkDx = true,
                UseLz4 = false
            });

            Assert.AreEqual(0, report.Warnings.Count, string.Join(" | ", report.Warnings));
            Assert.AreEqual(1, report.DxReencoded, "开了缩 DXT 就要在报告里数出来");
            Assert.AreEqual(0, report.Etc2Encoded, "缩 DXT 不等于发 fmt5：这两件事现在是分开的开关");

            byte[] bytes = null;
            foreach (var e in ReadPackage(target).Entries)
                if (e.FullPath == "materials/a.tex") bytes = e.Bytes;

            var tex = ReadTex(bytes);
            var mip = tex.FirstImage.FirstMipmap;
            Assert.AreEqual(TexFormat.RGBA8888, tex.Header.Format, "发出去的仍是真机验过的 fmt0");
            Assert.AreEqual(4, mip.Width);
            Assert.AreEqual(4, mip.Height);
            Assert.AreEqual(4 * 4 * 4, mip.Bytes.Length, "RGBA8 是 4 字节/像素，不是 ETC2 的 1 字节");
            Assert.AreEqual(8, tex.Header.ImageWidth, "头部四个尺寸字段照旧留原始尺寸");

            // 左半白右半黑：只查尺寸的话，把块字节当像素原样写出来也能一路通过，所以这里读像素本身
            var r0 = mip.Bytes[0];
            var rLast = mip.Bytes[(3 * 4)];
            Assert.Greater(r0, 200, $"第一列该是白的（DXT 解出来 255），实际 {r0}");
            Assert.Less(rLast, 120, $"最后一列该是黑的，实际 {rLast}（Lanczos 在硬边上会振铃，不必等于 0）");

            foreach (var e in ReadPackage(target).Entries)
                if (e.FullPath == "scene.json")
                    Assert.IsTrue(Encoding.UTF8.GetString(e.Bytes).Contains("\"texturereduction\" : 2,"),
                        "像素真缩过就要留下那个键，否则手机按原始尺寸读");
        }

        [Test]
        public void TestConvert_ShrinkDxWithoutReduction_IsInertNotFatal()
        {
            // ÷1 是逐字节验过的形态。这条锁的是"修饰键不报错也不偷偷动手"：
            // 全局写一次 mpkgShrinkDx、个别条目回落到 1× 的清单很常见，报错会让整批退不出去。
            var dxt = MakeDxt5Tex(Blocks(OpaqueWhiteDxt5, OpaqueBlackDxt5, OpaqueWhiteDxt5, OpaqueBlackDxt5), 8, 8);
            var pkg = WritePackage("scene.pkg", ("materials/a.tex", dxt));
            Directory.CreateDirectory(Path.Combine(_dir, "out"));
            var target = Path.Combine(_dir, "out", "scene.mpkg");

            var report = new MobilePackageConverter().Convert(pkg, target, new MobilePackageOptions
            {
                Reduction = 1,
                ShrinkDx = true,
                UseLz4 = false
            });

            Assert.AreEqual(0, report.DxReencoded);
            Assert.AreEqual(0, report.Warnings.Count);
            foreach (var e in ReadPackage(target).Entries)
                if (e.FullPath == "materials/a.tex") Assert.AreEqual(dxt, e.Bytes);
        }

        [Test]
        public void TestConvert_NoDematerialize_CopiesEveryTexAndSaysSo()
        {
            var png = MakePng(37, 19);
            var passthrough = MakePassthroughTex(png, 37, 19);
            var dxt = MakeDxt5Tex(Blocks(OpaqueWhiteDxt5, OpaqueBlackDxt5, OpaqueWhiteDxt5, OpaqueBlackDxt5), 8, 8);
            var pkg = WritePackage("scene.pkg",
                ("materials/icon.tex", passthrough),
                ("materials/a.tex", dxt),
                ("scene.json", Encoding.UTF8.GetBytes(WeStyleSceneJson)));
            Directory.CreateDirectory(Path.Combine(_dir, "out"));
            var target = Path.Combine(_dir, "out", "scene.mpkg");

            var report = new MobilePackageConverter().Convert(pkg, target, new MobilePackageOptions
            {
                Reduction = 2,
                Dematerialize = false,
                UseLz4 = false
            });

            Assert.AreEqual(0, report.Materialized, "关物化就不该有任何一条被重写");
            Assert.AreEqual(2, report.TexturesKept, "摘要要说清这次的\"物化 0\"是你关的，不是包里没东西");
            // 这条必须是 error 级：否则产物会带着 texturereduction 键发出满尺寸纹理
            Assert.AreEqual(1, report.Warnings.Count, string.Join(" | ", report.Warnings));
            StringAssert.Contains("物化", report.Warnings[0]);

            foreach (var e in ReadPackage(target).Entries)
                if (e.FullPath == "materials/icon.tex") Assert.AreEqual(passthrough, e.Bytes);
                else if (e.FullPath == "materials/a.tex") Assert.AreEqual(dxt, e.Bytes);
                else if (e.FullPath == "scene.json")
                    Assert.AreEqual(WeStyleSceneJson, Encoding.UTF8.GetString(e.Bytes),
                        "纹理一字节不动时那个键描述的就是 1×，写进去就是假账");
        }

        [Test]
        public void TestConvert_NoDematerialize_StillPatchesShaders()
        {
            // 那道闸只管 .tex：着色器改写和容器/loose 那几件事与像素无关，关掉物化也得照做。
            var body = "float volume = 2;\n";
            var pkg = WritePackage("scene.pkg",
                ("shaders/audio.frag", Encoding.UTF8.GetBytes(body)),
                ("materials/icon.tex", MakePassthroughTex(MakePng(8, 8), 8, 8)));
            Directory.CreateDirectory(Path.Combine(_dir, "out"));
            var target = Path.Combine(_dir, "out", "scene.mpkg");

            var report = new MobilePackageConverter().Convert(pkg, target, new MobilePackageOptions
            {
                Reduction = 1,
                Dematerialize = false
            });

            Assert.AreEqual(1, report.ShadersRewritten);
            Assert.AreEqual(0, report.Warnings.Count, "没要求缩小就不该有那条自相矛盾警告");
            foreach (var e in ReadPackage(target).Entries)
                if (e.FullPath == "shaders/audio.frag")
                    Assert.AreEqual("float volume = 2.0;\n", Encoding.UTF8.GetString(e.Bytes));
        }

        // ---------- 只读探针：必须和转换器给出同一个答案 ----------

        /// <summary>一张"每种形态各来一条"的包：直通 / DXT5 / 读不动的垃圾 / mp3 / scene.json。</summary>
        private static (byte[] Passthrough, byte[] Dxt, byte[] Garbage) MakeProbeTexSet()
        {
            return (
                MakePassthroughTex(MakePng(8, 8), 8, 8),
                MakeDxt5Tex(Blocks(OpaqueWhiteDxt5, OpaqueBlackDxt5, OpaqueWhiteDxt5, OpaqueBlackDxt5), 8, 8),
                new byte[64]); // 够长，读取器会真去解析然后抛 —— 与转换器的"解析失败即照搬"同一种处置
        }

        [Test]
        public void TestProbe_ClassifiesEveryTex_AndCountsAudioAndScene()
        {
            var (pass, dxt, garbage) = MakeProbeTexSet();
            var pkg = WritePackage("scene.pkg",
                ("materials/pass.tex", pass),
                ("materials/dx.tex", dxt),
                ("materials/broken.tex", garbage),
                ("sounds/track.mp3", Encoding.UTF8.GetBytes("pretend audio")),
                ("scene.json", Encoding.UTF8.GetBytes(WeStyleSceneJson)));

            var probe = MobilePackageProbe.Probe(pkg, new MobilePackageOptions());

            Assert.IsNull(probe.Failure);
            Assert.AreEqual(5, probe.Entries);
            Assert.AreEqual(3, probe.Tex);
            Assert.AreEqual(1, probe.Passthrough);
            Assert.AreEqual(1, probe.Dxt);
            Assert.AreEqual(dxt.Length, probe.DxtBytes, "DXT 那一条占了包里多大一块，正是\"为什么缩了个寂寞\"的答案");
            Assert.AreEqual(1, probe.Unreadable);
            Assert.AreEqual(1, probe.Audio);
            Assert.IsTrue(probe.HasSceneJson);
            // 恒等式：探针报的形态格子必须正好铺满 .tex 计数，谁都不能凭空多出来或漏掉
            Assert.AreEqual(probe.Tex,
                probe.Passthrough + probe.Dxt + probe.Raw + probe.Mask + probe.Video + probe.NoImages + probe.Unreadable);
            Assert.GreaterOrEqual(probe.LargestTexBytes, dxt.Length);
        }

        [Test]
        public void TestProbe_WouldReduce_FollowsTheSameSwitchesAsTheConverter()
        {
            var (pass, dxt, garbage) = MakeProbeTexSet();
            var pkg = WritePackage("scene.pkg",
                ("materials/pass.tex", pass),
                ("materials/dx.tex", dxt),
                ("materials/broken.tex", garbage));

            // ÷1：直通也不缩，因为压根不要求缩
            Assert.AreEqual(0, Probe(pkg, r: 1).WouldReduce);
            // ÷2 不开任何重编：只有直通那条会动，DXT 照搬
            Assert.AreEqual(1, Probe(pkg, r: 2).WouldReduce);
            // ÷2 + fmt5：DXT 跟着一起动（真机验过的那对）
            Assert.AreEqual(2, Probe(pkg, r: 2, etc2: true).WouldReduce);
            // ÷2 + 只开缩 DXT：同样是 2 条，发出去的却是 RGBA8
            Assert.AreEqual(2, Probe(pkg, r: 2, shrinkDx: true).WouldReduce);
            // 关物化：0 条 —— 这一格必须和产物的"物化 0"对上，哪怕档位仍是 ÷2
            Assert.AreEqual(0, Probe(pkg, r: 2, etc2: true, dematerialize: false).WouldReduce);
        }

        /// <summary>
        /// 探针与转换器的同源闸门：同一个包、同一套选项，探针说"会缩几条"，转换就跑出"缩小几条"。
        /// 两边各写一遍判据的话这条断言迟早变红 —— 而那正是它存在的意义。
        /// </summary>
        [Test]
        public void TestProbe_AgreesWithConverter_OnWhatActuallyShrank()
        {
            var (pass, dxt, garbage) = MakeProbeTexSet();
            var pkg = WritePackage("scene.pkg",
                ("materials/pass.tex", pass),
                ("materials/dx.tex", dxt),
                ("materials/broken.tex", garbage),
                ("scene.json", Encoding.UTF8.GetBytes(WeStyleSceneJson)));

            foreach (var etc2 in new[] {true, false})
            foreach (var shrinkDx in new[] {true, false})
            {
                var options = new MobilePackageOptions
                {
                    Reduction = 2, EncodeEtc2 = etc2, ShrinkDx = shrinkDx, UseLz4 = false
                };

                var expected = MobilePackageProbe.Probe(pkg, options).WouldReduce;

                Directory.CreateDirectory(Path.Combine(_dir, "out"));
                var report = new MobilePackageConverter().Convert(pkg,
                    Path.Combine(_dir, "out", $"probe_{etc2}_{shrinkDx}.mpkg"), options);

                Assert.AreEqual(expected, report.Reduced,
                    $"etc2={etc2} shrinkDx={shrinkDx}：探针说 {expected} 条，转换却说 {report.Reduced} 条");
            }
        }

        [Test]
        public void TestProbe_NotAPackage_FailsInsteadOfThrowing()
        {
            var file = Path.Combine(_dir, "not-a-pkg.pkg");
            Directory.CreateDirectory(_dir);
            File.WriteAllBytes(file, new byte[] {1, 2, 3, 4});

            var probe = MobilePackageProbe.Probe(file, new MobilePackageOptions());

            Assert.IsNotNull(probe.Failure, "表都读不动的包要给出可上报的失败，不是裸抛异常");
            Assert.AreEqual(0, probe.Tex);
        }

        private static PackageProbe Probe(string pkg, int r, bool etc2 = false, bool shrinkDx = false,
            bool dematerialize = true)
            => MobilePackageProbe.Probe(pkg, new MobilePackageOptions
            {
                Reduction = r, EncodeEtc2 = etc2, ShrinkDx = shrinkDx, Dematerialize = dematerialize, UseLz4 = false
            });

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

        // ---------- 并行排产：产物必须与串行逐字节相同 ----------

        /// <summary>
        /// 噪声图：物化后的 RGBA8 <b>压不下去</b>。平滑渐变那种会被 LZ4 压到 1MB 以下，
        /// 于是"大条目落盘"这条路根本没被碰到，用例却在绿 —— 这条是踩过一次才知道的。
        /// </summary>
        private static byte[] MakeNoisePng(int w, int h)
        {
            using var image = new Image<Rgba32>(w, h);
            var s = (uint) (w * 73856093) ^ (uint) (h * 19349663) ^ 0x9e3779b9;
            for (var y = 0; y < h; y++)
            for (var x = 0; x < w; x++)
            {
                s ^= s << 13;
                s ^= s >> 17;
                s ^= s << 5;
                image[x, y] = new Rgba32((byte) s, (byte) (s >> 8), (byte) (s >> 16), 255);
            }

            using var stream = new MemoryStream();
            image.SaveAsPng(stream);
            return stream.ToArray();
        }

        /// <summary>
        /// 一张"条目够多、大家伙够大"的包：窗口、落盘、按序拼接三件事都得被跑到，
        /// 否则"并行没改变字节"这条断言只是在验证没并行的那条路。
        /// </summary>
        private string WriteBusyPackage(string name, int bigCount, int smallCount)
        {
            var entries = new List<(string, byte[])>();

            for (var i = 0; i < bigCount; i++)
            {
                // 800x600 物化后 RGBA8 = 1.92MB，且是噪声压不小 → 过 1MB 的落盘阈值
                var w = 800 + i;
                var h = 600 + i;
                entries.Add(($"materials/big{i}.tex", MakePassthroughTex(MakeNoisePng(w, h), w, h)));
            }

            for (var i = 0; i < smallCount; i++)
            {
                var w = 9 + i % 5;
                var h = 7 + i % 3;
                entries.Add(($"materials/small{i}.tex", MakePassthroughTex(MakePng(w, h), w, h)));
                // 每条都要被兼容改写补 .0，所以它们在产出阶段互不相干、顺序却必须保持
                entries.Add(($"shaders/s{i}.frag", Encoding.UTF8.GetBytes($"float v{i} = {i + 2};\n")));
            }

            entries.Add(("scene.json", Encoding.UTF8.GetBytes(WeStyleSceneJson)));
            entries.Add(("sounds/a.mp3", new byte[] {1, 2, 3, 4}));
            return WritePackage(name, entries.ToArray());
        }

        private string WriteLoose(string name, string text)
        {
            Directory.CreateDirectory(_dir);
            var file = Path.Combine(_dir, name);
            File.WriteAllText(file, text);
            return file;
        }

        private static string[] SpillDirs() => Directory.GetDirectories(Path.GetTempPath(), "repkg-spill-*");

        /// <summary>
        /// 每次给一份<b>新的</b> options（loose 那两个字段是 runner 按包现填的，共用一份会让两个包抢同一个文件），
        /// 串行与并行两边都从这里取 —— 否则比的根本是两种输入（第一次跑就因为漏了 loose 差出 80 字节）。
        /// </summary>
        private MobilePackageOptions OptionsWithLoose(int reduction, bool etc2 = false, string tag = "d")
        {
            return new MobilePackageOptions
            {
                Reduction = reduction,
                EncodeEtc2 = etc2,
                ProjectJsonPath = WriteLoose($"project-{tag}.json", "{\"preview\":\"preview.gif\"}"),
                PreviewPath = WriteLoose($"preview-{tag}.gif", "gifbytes")
            };
        }

        [Test]
        public void TestPipeline_ParallelOutput_IsByteIdenticalToSerial()
        {
            var src = WriteBusyPackage("scene.pkg", 3, 6);
            var converter = new MobilePackageConverter();
            var serial = Path.Combine(_dir, "serial.mpkg");
            var parallel = Path.Combine(_dir, "parallel.mpkg");

            converter.Convert(src, serial, OptionsWithLoose(1));

            var spillBefore = SpillDirs();
            var ended = false;
            int spilled;
            using (var pipe = new MobilePackagePipeline(4, 2))
            {
                pipe.Add(converter.BuildPlan(src, OptionsWithLoose(1), parallel), null,
                    p => { ended = true; });
                pipe.Run();
                spilled = pipe.SpilledEntries;
            }

            Assert.IsTrue(ended, "回调没跑到，说明那条包的提交线程根本没结束");

            var a = File.ReadAllBytes(serial);
            var b = File.ReadAllBytes(parallel);
            Assert.AreEqual(a.LongLength, b.LongLength, "并行产物长度不同：先比条目表再比数据区，看是哪一段偏了");
            CollectionAssert.AreEqual(a, b, "并行改变了产物字节 —— 产出阶段串了状态，或拼接不再按表序");

            // 字节比对在前：落盘门没跨过只是"这条用例没测到那条路"，不该把"并行改了字节"这种结论挡住
            Assert.Greater(spilled, 0, "3 条 1.9MB 的 RGBA8 必须有过落盘，否则这条用例压根没碰到临时仓");

            // 临时仓必须自己收干净：漏一次就是一包几十 MB 留在 %TEMP%，而且没人会去看
            CollectionAssert.AreEquivalent(spillBefore, SpillDirs(), "并行结束后 %TEMP% 里留下了临时仓目录");
        }

        [Test]
        public void TestPipeline_TwoPackagesWithDifferentOptions_DoNotCrossTalk()
        {
            // 条目级覆盖（每张壁纸一档）就是靠"每包一份 options"成立的；两包并行时最怕是彼此的档位互串
            var src = WriteBusyPackage("scene.pkg", 1, 4);
            var converter = new MobilePackageConverter();

            var s1 = Path.Combine(_dir, "s1.mpkg");
            var s2 = Path.Combine(_dir, "s2.mpkg");
            var p1 = Path.Combine(_dir, "p1.mpkg");
            var p2 = Path.Combine(_dir, "p2.mpkg");

            converter.Convert(src, s1, OptionsWithLoose(1, tag: "a"));
            converter.Convert(src, s2, OptionsWithLoose(2, true, tag: "b"));

            using (var pipe = new MobilePackagePipeline(4, 2))
            {
                // 故意让"重的"那包先登记：轮转投料下两包同时在产，互串会以"p1 长得像 p2"暴露
                pipe.Add(converter.BuildPlan(src, OptionsWithLoose(2, true, tag: "b"), p2), null, null);
                pipe.Add(converter.BuildPlan(src, OptionsWithLoose(1, tag: "a"), p1), null, null);
                pipe.Run();
            }

            CollectionAssert.AreEqual(File.ReadAllBytes(s1), File.ReadAllBytes(p1), "÷1 那包被另一包的档位影响了");
            CollectionAssert.AreEqual(File.ReadAllBytes(s2), File.ReadAllBytes(p2), "÷2+fmt5 那包被另一包的档位影响了");
            Assert.AreNotEqual(new FileInfo(s1).Length, new FileInfo(s2).Length,
                "两包的产物一模一样，说明档位根本没生效（这条断言是给上面两条兜底的）");
        }

        [Test]
        public void TestPipeline_MergesAsEntriesLand_NotAfterEverythingIsProduced()
        {
            // 这条盯的就是"什么时候合并"：偏移是前缀和，所以提交必须一格一格走。
            // 攒到全部产出再拼的写法同样能出正确字节，但它要把整包字节留在窗口/临时盘上，
            // 而且第一条要等最后一条 —— 进度条会一直停在 0。
            var src = WriteBusyPackage("scene.pkg", 1, 10);
            var converter = new MobilePackageConverter();
            var target = Path.Combine(_dir, "streaming.mpkg");
            var plan = converter.BuildPlan(src, OptionsWithLoose(1), target);

            // 读累计字节数而不是文件长度：FileStream 有内部写缓冲，磁盘上的长度会滞后一两条，
            // 那会让这条用例偶尔假红，而它要证的本来就不是"OS 刷盘时机"。
            var committed = new List<long>();
            using (var pipe = new MobilePackagePipeline(2, 1))
            {
                pipe.Add(plan, (index, ofPackage, name) => committed.Add(plan.Report.OutputBytes), null);
                pipe.Run();
            }

            Assert.Greater(committed.Count, 4, "回调太少，下面那组断言没有区分力");
            Assert.AreEqual(plan.Count, committed.Count, "每条都该有一次提交回调");

            for (var i = 1; i < committed.Count; i++)
                Assert.Greater(committed[i], committed[i - 1],
                    $"写第 {i} 条时累计字节没涨 —— 提交没有在逐条推进，是在攒着一次拼");

            // 回调在写这条<b>之前</b>发：第 0 次时累计字节还是 0，最后一次时它自己那一格还没进账。
            // 这两条钉的是"进度事件与字节的先后"，前端那条进度条靠它才不会倒退或提前。
            Assert.AreEqual(0, committed[0], "第一条的回调时机应该在它写出之前");
            Assert.Less(committed[committed.Count - 1], plan.Report.OutputBytes,
                "最后一次回调已经看到了全部字节，说明回调发晚了");
        }

        [Test]
        public void TestPipeline_OnePackageFailing_LeavesTheOtherOneIntact()
        {
            var src = WriteBusyPackage("scene.pkg", 1, 4);
            var good = WriteBusyPackage("good.pkg", 1, 3);
            var converter = new MobilePackageConverter();

            var serialGood = Path.Combine(_dir, "good_serial.mpkg");
            converter.Convert(good, serialGood, OptionsWithLoose(1, tag: "good"));

            // 造一条"产出阶段必抛"的包：计划建好之后把它要内嵌的 loose 文件删掉
            var doomedTarget = Path.Combine(_dir, "doomed.mpkg");
            var doomed = converter.BuildPlan(src, OptionsWithLoose(1, tag: "doomed"), doomedTarget);
            File.Delete(doomed.Options.ProjectJsonPath);
            File.Delete(doomed.Options.PreviewPath);

            var goodTarget = Path.Combine(_dir, "good_parallel.mpkg");
            var goodPlan = converter.BuildPlan(good, OptionsWithLoose(1, tag: "good"), goodTarget);

            var spillBefore = SpillDirs();
            using (var pipe = new MobilePackagePipeline(3, 2))
            {
                pipe.Add(doomed, null, null);
                pipe.Add(goodPlan, null, null);
                pipe.Run();
            }

            Assert.IsNotNull(doomed.Failure, "loose 文件已被删掉，产出阶段必须抛出来并记在这个包上");
            Assert.IsNull(goodPlan.Failure, "一个包失败不该牵连同批的另一个包");
            Assert.AreEqual(goodPlan.Count, goodPlan.Report.Entries, "好包必须整包写满，一格都不能缺");
            CollectionAssert.AreEqual(File.ReadAllBytes(serialGood), File.ReadAllBytes(goodTarget),
                "好包的产物必须与它单独跑时逐字节相同");

            CollectionAssert.AreEquivalent(spillBefore, SpillDirs(), "失败包的临时条目没被清掉");
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
