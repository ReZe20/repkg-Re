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
    }
}
