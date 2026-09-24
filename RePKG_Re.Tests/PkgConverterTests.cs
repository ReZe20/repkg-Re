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
    /// mpkg → pkg 逆向转换的端到端测试：容器写回(魔数换回 PKGV0018、条目保序保全)、
    /// 物化 RGBA8 重编码回 PNG 直通 blob 的像素级闭合、texturereduction 键的删除闭合、
    /// 以及"不该动的东西一个字节都不动"(DXT/R8 照搬、非 scene 的 JSON 不碰)。
    /// </summary>
    [TestFixture]
    public class PkgConverterTests
    {
        private string _dir;

        [SetUp]
        public void SetUp() => _dir = Path.Combine(Path.GetTempPath(), "repkg-pkg-" + Guid.NewGuid().ToString("N"));

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
                image[x, y] = new Rgba32((byte) (x * 7), (byte) (y * 11), 200, (byte) ((x + y) % 256));

            using var stream = new MemoryStream();
            image.SaveAsPng(stream);
            return stream.ToArray();
        }

        /// <summary>PC 直通编码图 TEX:TEXB0003 + ifmt=PNG(正向物化的输入)。</summary>
        private static byte[] MakePassthroughTex(byte[] png, int w, int h) =>
            MakeTex("TEXB0003", TexImageContainerVersion.Version3, FreeImageFormat.FIF_PNG,
                new TexMipmap
                {
                    Width = w, Height = h, Format = MipmapFormat.ImagePNG,
                    Bytes = png, DecompressedBytesCount = png.Length, IsLZ4Compressed = false
                }, w, h);

        private static byte[] MakeTex(string containerMagic, TexImageContainerVersion version,
            FreeImageFormat imageFormat, TexMipmap mip, int w, int h, TexFormat format = TexFormat.RGBA8888)
        {
            var tex = new Tex
            {
                Magic1 = "TEXV0005",
                Magic2 = "TEXI0001",
                Header = new TexHeader
                {
                    Format = format,
                    Flags = TexFlags.ClampUVs,
                    TextureWidth = w,
                    TextureHeight = h,
                    ImageWidth = w,
                    ImageHeight = h
                },
                ImagesContainer = new TexImageContainer
                {
                    Magic = containerMagic,
                    ImageContainerVersion = version,
                    ImageFormat = imageFormat
                }
            };
            tex.ImagesContainer.Images.Add(new TexImage {Mipmaps = {mip}});

            using var stream = new MemoryStream();
            using (var writer = new BinaryWriter(stream, Encoding.UTF8, true))
                TexWriter.Default.WriteTo(writer, tex);
            return stream.ToArray();
        }

        private string WritePackage(string name, string magic, params (string Path, byte[] Bytes)[] entries)
        {
            var package = new Package {Magic = magic};
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

        // ---------- 容器层 ----------

        [Test]
        public void TestConvert_SwapsMagic_And_PreservesEntriesInOrder()
        {
            var mpkg = WritePackage("scene.mpkg", "PKGM0019",
                ("scene.json", Encoding.UTF8.GetBytes("{\"general\":{}}")),
                ("txt/a.txt", Encoding.UTF8.GetBytes("aaa")),
                ("materials/b.tex", new byte[] {1, 2, 3}));

            var outPath = Path.Combine(_dir, "back.pkg");
            var report = new PcPackageConverter().Convert(mpkg, outPath, new PcPackageOptions());

            var pkg = ReadPackage(outPath);
            Assert.That(pkg.Magic, Is.EqualTo("PKGV0018"));
            Assert.That(pkg.Entries.Count, Is.EqualTo(3));
            Assert.That(pkg.Entries[0].FullPath, Is.EqualTo("scene.json"));
            Assert.That(pkg.Entries[2].FullPath, Is.EqualTo("materials/b.tex"));
            // 解析失败的 .tex(3 个字节)必须原样搬运,不报错
            Assert.That(pkg.Entries[2].Bytes, Is.EqualTo(new byte[] {1, 2, 3}));
            Assert.That(report.Entries, Is.EqualTo(3));
            Assert.That(report.Dematerialized, Is.EqualTo(0));
        }

        [Test]
        public void TestConvert_CustomMagic()
        {
            var mpkg = WritePackage("x.mpkg", "PKGM0016",
                ("scene.json", Encoding.UTF8.GetBytes("{}")));

            var outPath = Path.Combine(_dir, "x.pkg");
            new PcPackageConverter().Convert(mpkg, outPath, new PcPackageOptions {Magic = "PKGV0023"});

            Assert.That(ReadPackage(outPath).Magic, Is.EqualTo("PKGV0023"));
        }

        // ---------- 逆物化 ----------

        /// <summary>完整链路:PNG 直通 → 正向物化 RGBA8 → 逆向重编码 PNG。像素必须逐字节回来。</summary>
        [Test]
        public void TestRoundTrip_Materialize_Then_Dematerialize_PixelsSurvive()
        {
            const int W = 37, H = 19; // 奇数尺寸,stride 问题藏不住
            var png = MakePng(W, H);

            var pkg = WritePackage("scene.pkg", "PKGV0018",
                ("materials/icon.tex", MakePassthroughTex(png, W, H)),
                ("scene.json", Encoding.UTF8.GetBytes("{\"general\":{}}")));

            // 正向: pkg → mpkg(物化)
            var mpkgPath = Path.Combine(_dir, "scene.mpkg");
            var fwd = new MobilePackageConverter().Convert(pkg, mpkgPath, new MobilePackageOptions
            {
                DropAudio = false, UseLz4 = false, ShaderCompat = false
            });
            Assert.That(fwd.Materialized, Is.EqualTo(1), "正向必须真的物化了,否则这条测试什么都没测");

            // 物化形态抽查:TEXB0004 + FIF_UNKNOWN + RGBA8888
            var mid = ReadTex(mpkgPath, "materials/icon.tex");
            Assert.That(mid.ImagesContainer.Magic, Is.EqualTo("TEXB0004"));
            Assert.That(mid.ImagesContainer.ImageFormat, Is.EqualTo(FreeImageFormat.FIF_UNKNOWN));
            Assert.That(mid.Header.Format, Is.EqualTo(TexFormat.RGBA8888));

            // 逆向: mpkg → pkg
            var backPath = Path.Combine(_dir, "back.pkg");
            var rev = new PcPackageConverter().Convert(mpkgPath, backPath, new PcPackageOptions());
            Assert.That(rev.Dematerialized, Is.EqualTo(1));

            var back = ReadTex(backPath, "materials/icon.tex");
            Assert.That(back.ImagesContainer.Magic, Is.EqualTo("TEXB0004"));
            Assert.That(back.ImagesContainer.ImageFormat, Is.EqualTo(FreeImageFormat.FIF_PNG),
                "逆物化后应回到 PNG 直通 blob");

            var payload = back.ImagesContainer.Images[0].Mipmaps[0].Bytes;
            using var decoded = Image.Load<Rgba32>(payload);
            Assert.That(decoded.Width, Is.EqualTo(W));
            Assert.That(decoded.Height, Is.EqualTo(H));

            // 像素级断言:逆向 PNG 解出来和最初造 PNG 时的像素一一对应(直色,无 alpha 变换)
            for (var y = 0; y < H; y++)
            for (var x = 0; x < W; x++)
            {
                var p = decoded[x, y];
                Assert.Multiple(() =>
                {
                    Assert.That(p.R, Is.EqualTo((byte) (x * 7)), $"x={x},y={y}");
                    Assert.That(p.G, Is.EqualTo((byte) (y * 11)), $"x={x},y={y}");
                    Assert.That(p.B, Is.EqualTo(200), $"x={x},y={y}");
                    Assert.That(p.A, Is.EqualTo((byte) ((x + y) % 256)), $"x={x},y={y}");
                });
            }
        }

        [Test]
        public void TestConvert_DxtTex_IsCopiedByteForByte()
        {
            // DXT5 载荷(任意字节):不满足物化判据,一个字节都不能动
            var dxt = MakeTex("TEXB0003", TexImageContainerVersion.Version3, FreeImageFormat.FIF_UNKNOWN,
                new TexMipmap
                {
                    Width = 16, Height = 16, Format = MipmapFormat.CompressedDXT5,
                    Bytes = new byte[] {9, 8, 7, 6}, DecompressedBytesCount = 4, IsLZ4Compressed = false
                }, 16, 16, TexFormat.DXT5);

            var mpkg = WritePackage("x.mpkg", "PKGM0019", ("materials/t.tex", dxt));
            var outPath = Path.Combine(_dir, "x.pkg");
            var report = new PcPackageConverter().Convert(mpkg, outPath, new PcPackageOptions());

            Assert.That(report.Dematerialized, Is.EqualTo(0));
            Assert.That(report.Warnings, Is.Empty);
            Assert.That(ReadPackage(outPath).Entries[0].Bytes, Is.EqualTo(dxt));
        }

        [Test]
        public void TestConvert_UnparseableTex_CopiedWithWarning()
        {
            var mpkg = WritePackage("x.mpkg", "PKGM0019",
                ("materials/garbage.tex", Encoding.UTF8.GetBytes("not a tex at all")));
            var outPath = Path.Combine(_dir, "x.pkg");
            var report = new PcPackageConverter().Convert(mpkg, outPath, new PcPackageOptions());

            Assert.That(report.Dematerialized, Is.EqualTo(0));
            Assert.That(report.Warnings.Count, Is.EqualTo(1));
            Assert.That(ReadPackage(outPath).Entries[0].Bytes,
                Is.EqualTo(Encoding.UTF8.GetBytes("not a tex at all")));
        }

        [Test]
        public void TestConvert_DematerializeOff_CopiesMaterializedRgbaVerbatim()
        {
            const int W = 8, H = 8;
            var rgba = new byte[W * H * 4];
            new Random(42).NextBytes(rgba);
            var materialized = MakeTex("TEXB0004", TexImageContainerVersion.Version3, FreeImageFormat.FIF_UNKNOWN,
                new TexMipmap
                {
                    Width = W, Height = H, Format = MipmapFormat.RGBA8888,
                    Bytes = rgba, DecompressedBytesCount = rgba.Length, IsLZ4Compressed = false
                }, W, H);

            var mpkg = WritePackage("x.mpkg", "PKGM0019", ("materials/m.tex", materialized));
            var outPath = Path.Combine(_dir, "x.pkg");
            var report = new PcPackageConverter().Convert(mpkg, outPath,
                new PcPackageOptions {Dematerialize = false});

            Assert.That(report.Dematerialized, Is.EqualTo(0));
            Assert.That(ReadPackage(outPath).Entries[0].Bytes, Is.EqualTo(materialized));
        }

        // ---------- scene.json 键删除 ----------

        [Test]
        public void TestConvert_ClearsTextureReduction()
        {
            var scene = Encoding.UTF8.GetBytes(
                "{\"general\":{\"allowinitdrag\":true,\r\n\"emittersdisabled\":true,\r\n\"gameresponse\":0,\r\n\"texturereduction\":2,\r\n\"version\":26},\r\n\"title\":\"x\"}");

            var mpkg = WritePackage("x.mpkg", "PKGM0019", ("scene.json", scene));
            var outPath = Path.Combine(_dir, "x.pkg");
            var report = new PcPackageConverter().Convert(mpkg, outPath, new PcPackageOptions());

            Assert.That(report.ReductionCleared, Is.EqualTo(1));
            var back = Encoding.UTF8.GetString(ReadPackage(outPath).Entries[0].Bytes);
            Assert.That(back, Does.Not.Contain("texturereduction"));
            // 除这一行外其余字节不动(逐字符对照插入侧产物)
            Assert.That(back, Is.EqualTo(
                "{\"general\":{\"allowinitdrag\":true,\r\n\"emittersdisabled\":true,\r\n\"gameresponse\":0,\r\n\"version\":26},\r\n\"title\":\"x\"}"));
        }

        [Test]
        public void TestConvert_ReductionKeyInMiddle_RestoresExactBytes()
        {
            var without = "{\"general\":{\"allowinitdrag\":true,\r\n\"gameresponse\":0,\r\n\"version\":26}}";
            // 先走插入器,再走删除器:闭合必须逐字节
            var with = SceneJsonPatcher.SetTextureReduction(Encoding.UTF8.GetBytes(without), 4, out var f1);
            Assert.That(f1, Is.Null);
            var removed = SceneJsonPatcher.RemoveTextureReduction(with, out var f2);
            Assert.That(f2, Is.Null);
            Assert.That(Encoding.UTF8.GetString(removed), Is.EqualTo(without));
        }

        [Test]
        public void TestConvert_LastMemberAndSoleMember()
        {
            // 末尾成员:连前一个值后的逗号一起收
            var text = "{\"general\":{\"allowinitdrag\":true,\"texturereduction\":2}}";
            var removed = SceneJsonPatcher.RemoveTextureReduction(Encoding.UTF8.GetBytes(text), out var f);
            Assert.That(f, Is.Null);
            Assert.That(Encoding.UTF8.GetString(removed), Is.EqualTo("{\"general\":{\"allowinitdrag\":true}}"));

            // 唯一成员:拒绝掏空,返回 null 且给出原因
            var sole = SceneJsonPatcher.RemoveTextureReduction(
                Encoding.UTF8.GetBytes("{\"general\":{\"texturereduction\":2}}"), out var f2);
            Assert.That(sole, Is.Null);
            Assert.That(f2, Is.Not.Null);
        }

        [Test]
        public void TestConvert_NoKey_NoTouch_NoWarning()
        {
            var scene = Encoding.UTF8.GetBytes("{\"general\":{\"allowinitdrag\":true}}");
            var mpkg = WritePackage("x.mpkg", "PKGM0019", ("scene.json", scene));
            var outPath = Path.Combine(_dir, "x.pkg");
            var report = new PcPackageConverter().Convert(mpkg, outPath, new PcPackageOptions());

            Assert.That(report.ReductionCleared, Is.EqualTo(0));
            Assert.That(report.Warnings, Is.Empty);
            Assert.That(ReadPackage(outPath).Entries[0].Bytes, Is.EqualTo(scene));
        }

        [Test]
        public void TestConvert_KeepReductionKey()
        {
            var scene = Encoding.UTF8.GetBytes(
                "{\"general\":{\"gameresponse\":0,\r\n\"texturereduction\":2}}");
            var mpkg = WritePackage("x.mpkg", "PKGM0019", ("scene.json", scene));
            var outPath = Path.Combine(_dir, "x.pkg");
            var report = new PcPackageConverter().Convert(mpkg, outPath,
                new PcPackageOptions {ClearTextureReduction = false});

            Assert.That(report.ReductionCleared, Is.EqualTo(0));
            Assert.That(ReadPackage(outPath).Entries[0].Bytes, Is.EqualTo(scene));
        }

        // ---------- helpers ----------

        private static ITex ReadTex(string pkgPath, string entryName)
        {
            var package = ReadPackage(pkgPath);
            byte[] bytes = null;
            foreach (var e in package.Entries)
                if (e.FullPath == entryName) bytes = e.Bytes;
            Assert.That(bytes, Is.Not.Null, $"entry {entryName} not found");

            using var reader = new BinaryReader(new MemoryStream(bytes), Encoding.UTF8, true);
            return TexReader.Default.ReadFrom(reader);
        }
    }
}
