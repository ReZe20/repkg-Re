using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using NUnit.Framework;
using RePKG_Re.Application.Package;
using RePKG_Re.Command;
using RePKG_Re.Core.Package;
using RePKG_Re.Core.Texture;
using RePKG_Re.Application.Texture;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace RePKG_Re.Tests
{
    /// <summary>
    /// 工程目录 → pkg 的打包测试。三件事各自要闭合：
    ///   1. 条目取舍(什么该进包、什么该丢) —— 判据来自本地 279 个真实包的条目普查；
    ///   2. 容器字节形状(魔数/条目表/偏移累计/文件大小不变式) —— 与解包侧共用 PackageReader，
    ///      所以"读得回来"本身就是对写入的正确性检查；
    ///   3. 直通 .tex 无损 —— mip0 载荷必须与源图逐字节相同。
    /// </summary>
    [TestFixture]
    public class LoosePackageTests
    {
        private string _dir;

        [SetUp]
        public void SetUp() => _dir = Path.Combine(Path.GetTempPath(), "repkg-pack-" + Guid.NewGuid().ToString("N"));

        [TearDown]
        public void TearDown()
        {
            try { if (Directory.Exists(_dir)) Directory.Delete(_dir, true); } catch { }
        }

        private string Mkdir(params string[] parts)
        {
            var path = Path.Combine(new[] { _dir }.Concat(parts).ToArray());
            Directory.CreateDirectory(Path.GetDirectoryName(path) ?? path);
            return path;
        }

        private static byte[] MakePng(int w = 8, int h = 8)
        {
            using var image = new Image<Rgba32>(w, h);
            for (var y = 0; y < h; y++)
            for (var x = 0; x < w; x++)
                image[x, y] = new Rgba32((byte) (x * 29), (byte) (y * 47), (byte) (x + y), 255);

            using var stream = new MemoryStream();
            image.SaveAsPng(stream);
            return stream.ToArray();
        }

        /// <summary>造一个像模像样的工程目录：scene.json + 素材 + 着色器缓存 + 预览图 + 已经封好的 .tex。</summary>
        private string MakeProject()
        {
            var root = Mkdir("proj");
            Write(root, "project.json", "{\"file\":\"scene.json\",\"preview\":\"preview.gif\",\"type\":\"scene\"}");
            Write(root, "scene.json", "{\"general\":{}}");
            Write(root, "preview.gif", new byte[] { 1, 2, 3, 4 });
            Write(root, "materials/has_tex.json", "{\"passes\":[]}");
            Write(root, "materials/has_tex.png", MakePng());
            Write(root, "materials/has_tex.tex", new byte[] { 0xDE, 0xAD, 0xBE, 0xEF });
            Write(root, "materials/has_tex.tex-json", "{\"clampuvs\": true}");
            Write(root, "materials/only_png.png", MakePng(16, 8));
            Write(root, "shaders/foo.vert", "void main(){}");
            Write(root, "shaders/blobsSM40/cache.dxs", new byte[] { 9, 9 });
            Write(root, "models/car.obj", "v 0 0 0");
            Write(root, "models/car.mtl", "newmtl a");
            Write(root, "models/car.mdl", new byte[] { 5, 6, 7 });
            Write(root, "scene.pkg", new byte[] { 1 }); // 解包时留下的原件，绝不能套进新包
            return root;
        }

        private void Write(string root, string relative, string content) =>
            File.WriteAllText(Of(root, relative), content, new UTF8Encoding(false));

        private void Write(string root, string relative, byte[] content) =>
            File.WriteAllBytes(Of(root, relative), content);

        private static string Of(string root, string relative)
        {
            var path = Path.Combine(new[] { root }.Concat(relative.Split('/')).ToArray());
            Directory.CreateDirectory(Path.GetDirectoryName(path) ?? path);
            return path;
        }

        private static List<PackageEntry> Table(string pkgPath)
        {
            using var stream = File.OpenRead(pkgPath);
            using var reader = new BinaryReader(stream, Encoding.UTF8, true);
            return new PackageReader { ReadEntryBytes = false }.ReadFrom(reader).Entries;
        }

        private static byte[] BytesOf(string pkgPath, PackageEntry entry)
        {
            using var stream = File.OpenRead(pkgPath);
            var package = new PackageReader { ReadEntryBytes = false }.ReadFrom(
                new BinaryReader(stream, Encoding.UTF8, true));
            return PackageReader.ReadEntryBytesFromStream(stream, package.HeaderSize, entry.Offset, entry.Length);
        }

        private static void AssertContainerIsValid(string pkgPath, string magic)
        {
            using var stream = File.OpenRead(pkgPath);
            using var reader = new BinaryReader(stream, Encoding.UTF8, true);
            var package = new PackageReader { ReadEntryBytes = false }.ReadFrom(reader);

            Assert.That(package.Magic, Is.EqualTo(magic));
            Assert.That(package.Entries.Count, Is.GreaterThan(0));
            Assert.That(package.Entries.Any(e => e.FullPath.Contains('\\')), Is.False, "条目名一律正斜杠");
            Assert.That(
                package.Entries.Select(e => e.FullPath.ToLower()).Distinct().Count(),
                Is.EqualTo(package.Entries.Count), "条目名不能重复");

            var cursor = 0L;
            foreach (var entry in package.Entries)
            {
                Assert.That(entry.Offset, Is.EqualTo(cursor), $"{entry.FullPath} 的偏移不是累计值");
                cursor += entry.Length;
            }

            Assert.That(package.HeaderSize + cursor, Is.EqualTo(new FileInfo(pkgPath).Length),
                "表大小 + Σ条目长度 必须正好等于文件大小");
        }

        // ---------- 条目取舍 ----------

        [Test]
        public void PacksOnlyWhatARealWePackageCarries()
        {
            var root = MakeProject();
            var outPkg = Mkdir("out.pkg");

            var report = new LoosePackageBuilder().Build(root, outPkg, new LoosePackageOptions());

            var names = Table(outPkg).Select(e => e.FullPath).ToList();
            Assert.That(names, Is.EquivalentTo(new[]
            {
                "materials/has_tex.json",
                "materials/has_tex.tex",
                "materials/only_png.tex", // 源图被封成了 .tex
                "models/car.mdl",
                "scene.json",
                "shaders/foo.vert"
            }));

            // 真实 WE 包里一条都没有的东西：源图、sidecar、着色器缓存、元数据、嵌套包、原始模型
            Assert.That(names.Any(n => n.EndsWith(".png") || n.EndsWith(".jpg")), Is.False);
            Assert.That(names.Any(n => n.EndsWith(".tex-json")), Is.False);
            Assert.That(names.Any(n => n.EndsWith(".dxs")), Is.False);
            Assert.That(names.Any(n => n.EndsWith(".pkg")), Is.False);
            Assert.That(names.Any(n => n.EndsWith(".obj") || n.EndsWith(".mtl")), Is.False);
            Assert.That(names, Does.Not.Contain("project.json"));
            Assert.That(names, Does.Not.Contain("preview.gif"));
            // 数一遍被剔掉的：project.json、preview.gif、has_tex.png、has_tex.tex-json、
            // blobsSM40/cache.dxs、car.obj、car.mtl、scene.pkg
            Assert.That(report.Dropped, Is.EqualTo(8));
        }

        [Test]
        public void ReportsProjectJsonAndPreviewAsLooseSiblings()
        {
            var root = MakeProject();
            var outPkg = Mkdir("out.pkg");

            var report = new LoosePackageBuilder().Build(root, outPkg, new LoosePackageOptions());

            Assert.That(report.MetadataFiles.Select(Path.GetFileName), Is.EquivalentTo(new[] { "project.json", "preview.gif" }));
            Assert.That(report.MetadataFiles.All(File.Exists), Is.True);
        }

        [Test]
        public void PrefersTheExistingTexOverReEncodingTheSourceImage()
        {
            var root = MakeProject();
            var outPkg = Mkdir("out.pkg");

            new LoosePackageBuilder().Build(root, outPkg, new LoosePackageOptions());

            var entry = Table(outPkg).Single(e => e.FullPath == "materials/has_tex.tex");
            Assert.That(BytesOf(outPkg, entry), Is.EqualTo(new byte[] { 0xDE, 0xAD, 0xBE, 0xEF }),
                "WE 自己封的 .tex 必须逐字节搬运，不能被我们再封一遍替掉");
        }

        [Test]
        public void KeepsSourceImagesOnlyWhenAsked()
        {
            var root = MakeProject();
            var outPkg = Mkdir("out.pkg");

            new LoosePackageBuilder().Build(root, outPkg, new LoosePackageOptions { KeepSourceImages = true });

            var names = Table(outPkg).Select(e => e.FullPath).ToList();
            Assert.That(names, Does.Contain("materials/has_tex.png"));
            Assert.That(names, Does.Contain("materials/has_tex.tex"));
            // 关键：同名 .tex 已在时绝不能再封一条重名条目把 WE 那份挤掉(条目序里 .png 排在 .tex 前)
            Assert.That(names.Count(n => n == "materials/has_tex.tex"), Is.EqualTo(1));
        }

        [Test]
        public void ShipsRawModelWhenThereIsNoMdl()
        {
            var root = Mkdir("proj2");
            Write(root, "scene.json", "{}");
            Write(root, "models/plain.obj", "v 0 0 0");

            var outPkg = Mkdir("out2.pkg");
            var report = new LoosePackageBuilder().Build(root, outPkg, new LoosePackageOptions());

            Assert.That(Table(outPkg).Select(e => e.FullPath), Is.EquivalentTo(new[] { "models/plain.obj", "scene.json" }));
            Assert.That(report.Warnings, Has.Some.Contains("没有 .mdl 版本"));
        }

        [Test]
        public void WarnsWhenProjectJsonIsMissing()
        {
            var root = Mkdir("proj3");
            Write(root, "scene.json", "{}");

            var report = new LoosePackageBuilder().Build(root, Mkdir("out3.pkg"), new LoosePackageOptions());

            Assert.That(report.Warnings, Has.Some.Contains("project.json"));
        }

        [Test]
        public void WarnsThatAVideoWallpaperHasNoPackageForm()
        {
            // 实测真实 WE 包里没有任何 .mp4 条目：视频壁纸的 mp4 是与 project.json 同级的散文件。
            // 打包仍然会成功，但必须说清楚这个包 WE 不会当壁纸用，否则用户拿到一个几百 MB 的废物。
            var root = Mkdir("videowp");
            Write(root, "project.json", "{\"file\":\"clip.mp4\",\"type\":\"video\"}");
            Write(root, "clip.mp4", new byte[] { 0, 0, 0, 0x20, 0x66, 0x74, 0x79, 0x70 });

            var report = new LoosePackageBuilder().Build(root, Mkdir("outvideo.pkg"), new LoosePackageOptions());

            Assert.That(report.Warnings, Has.Some.Contains("视频壁纸"));
        }

        [Test]
        public void DoesNotWarnForASceneWallpaper()
        {
            var root = MakeProject();
            var report = new LoosePackageBuilder().Build(root, Mkdir("outscene.pkg"), new LoosePackageOptions());

            Assert.That(report.Warnings, Is.Empty);
        }

        // ---------- 容器字节形状 ----------

        [Test]
        public void WritesAReadablePackageWithTheDefaultMagic()
        {
            var root = MakeProject();
            var outPkg = Mkdir("out.pkg");

            new LoosePackageBuilder().Build(root, outPkg, new LoosePackageOptions());

            AssertContainerIsValid(outPkg, "PKGV0018");
        }

        [Test]
        public void MagicIsOverridable()
        {
            var root = MakeProject();
            var outPkg = Mkdir("out.pkg");

            new LoosePackageBuilder().Build(root, outPkg, new LoosePackageOptions { Magic = "PKGV0023" });

            AssertContainerIsValid(outPkg, "PKGV0023");
        }

        [Test]
        public void NonAsciiEntryNamesRoundTrip()
        {
            // 条目名长度字段记的是 UTF-8 字节数；中文素材名一旦按字符数记账，整张条目表从这条起错位
            var root = Mkdir("洛茜");
            Write(root, "scene.json", "{}");
            Write(root, "materials/立绘 1.png", MakePng(4, 4));

            var outPkg = Mkdir("out4.pkg");
            new LoosePackageBuilder().Build(root, outPkg, new LoosePackageOptions());

            AssertContainerIsValid(outPkg, "PKGV0018");
            Assert.That(Table(outPkg).Select(e => e.FullPath), Does.Contain("materials/立绘 1.tex"));
        }

        [Test]
        public void EntryOrderIsDeterministic()
        {
            var root = MakeProject();
            var a = Mkdir("a.pkg");
            var b = Mkdir("b.pkg");

            new LoosePackageBuilder().Build(root, a, new LoosePackageOptions());
            new LoosePackageBuilder().Build(root, b, new LoosePackageOptions());

            Assert.That(File.ReadAllBytes(a), Is.EqualTo(File.ReadAllBytes(b)),
                "同一目录打两次必须得到同一份字节，否则回归没法比对");
        }

        // ---------- 直通 .tex ----------

        [Test]
        public void EncodedTexCarriesTheSourceImageBytesUnchanged()
        {
            var root = MakeProject();
            var png = File.ReadAllBytes(Path.Combine(root, "materials", "only_png.png"));
            var outPkg = Mkdir("out.pkg");

            new LoosePackageBuilder().Build(root, outPkg, new LoosePackageOptions());

            var entry = Table(outPkg).Single(e => e.FullPath == "materials/only_png.tex");
            var texBytes = BytesOf(outPkg, entry);

            using var reader = new BinaryReader(new MemoryStream(texBytes), Encoding.UTF8, true);
            var tex = TexReader.Default.ReadFrom(reader);

            Assert.That(tex.Header.Format, Is.EqualTo(TexFormat.RGBA8888));
            Assert.That(tex.ImagesContainer.ImageFormat, Is.EqualTo(FreeImageFormat.FIF_PNG));
            Assert.That(tex.ImagesContainer.Magic, Is.EqualTo("TEXB0004"));
            Assert.That(tex.Header.ImageWidth, Is.EqualTo(16));
            Assert.That(tex.Header.ImageHeight, Is.EqualTo(8));
            Assert.That(tex.ImagesContainer.Images.Count, Is.EqualTo(1));
            Assert.That(tex.FirstImage.Mipmaps.Count, Is.EqualTo(1), "直通纹理只发一级");
            Assert.That(tex.FirstImage.FirstMipmap.Bytes, Is.EqualTo(png), "载荷必须就是那张 PNG 的原字节");
            Assert.That(tex.FirstImage.FirstMipmap.Format, Is.EqualTo(MipmapFormat.ImagePNG));
        }

        [Test]
        public void EncodedTexTakesItsFlagsFromTheImportSidecar()
        {
            var root = Mkdir("proj4");
            Write(root, "scene.json", "{}");
            Write(root, "materials/a.png", MakePng(4, 4));
            Write(root, "materials/a.tex-json", "{\"nointerpolation\": \"true\", \"clampuvs\": true}");

            var outPkg = Mkdir("out5.pkg");
            new LoosePackageBuilder().Build(root, outPkg, new LoosePackageOptions());

            var entry = Table(outPkg).Single(e => e.FullPath == "materials/a.tex");
            using var reader = new BinaryReader(new MemoryStream(BytesOf(outPkg, entry)), Encoding.UTF8, true);
            var tex = TexReader.Default.ReadFrom(reader);

            // repkg 写的 sidecar 里 bool 是带引号的字符串，编辑器写的是真 bool —— 两种都要读得动
            Assert.That(tex.Header.Flags, Is.EqualTo(TexFlags.ClampUVs | TexFlags.NoInterpolation));
        }

        [Test]
        public void NoEncodeShipsTheImageAsItsOwnEntry()
        {
            var root = MakeProject();
            var outPkg = Mkdir("out.pkg");

            new LoosePackageBuilder().Build(root, outPkg, new LoosePackageOptions { EncodeImages = false });

            var names = Table(outPkg).Select(e => e.FullPath).ToList();
            Assert.That(names, Does.Contain("materials/only_png.png"));
            Assert.That(names, Does.Not.Contain("materials/only_png.tex"));
        }

        [Test]
        public void UnrecognizedImageFormatIsPackedVerbatimWithAWarning()
        {
            var root = Mkdir("proj5");
            Write(root, "scene.json", "{}");
            Write(root, "materials/anim.gif", new byte[] { 0x47, 0x49, 0x46, 8 });

            var outPkg = Mkdir("out6.pkg");
            var report = new LoosePackageBuilder().Build(root, outPkg, new LoosePackageOptions());

            Assert.That(Table(outPkg).Select(e => e.FullPath), Does.Contain("materials/anim.gif"));
            Assert.That(report.Warnings, Has.Some.Contains("找不到先例"));
        }

        // ---------- 命令行入口 ----------

        [Test]
        public void CliPacksOneProjectDirectoryIntoOutput()
        {
            var root = MakeProject();
            var outDir = Mkdir("outDir");

            Pack.Action(new PackOptions { Input = root, OutputDirectory = outDir, Name = "我的壁纸" });

            var produced = Path.Combine(outDir, "我的壁纸.pkg");
            Assert.That(File.Exists(produced), Is.True);
            AssertContainerIsValid(produced, "PKGV0018");
            // 工坊目录布局：.pkg 与 project.json/预览图同级
            Assert.That(File.Exists(Path.Combine(outDir, "project.json")), Is.True);
            Assert.That(File.Exists(Path.Combine(outDir, "preview.gif")), Is.True);
        }

        [Test]
        public void CliWritesNumberedFileInsteadOfOverwriting()
        {
            var root = MakeProject();
            var outDir = Mkdir("outDir");
            Directory.CreateDirectory(outDir);
            File.WriteAllText(Path.Combine(outDir, "proj.pkg"), "不是我们写的东西");

            Pack.Action(new PackOptions { Input = root, OutputDirectory = outDir });

            Assert.That(File.ReadAllText(Path.Combine(outDir, "proj.pkg")), Is.EqualTo("不是我们写的东西"),
                "同名原件一个字都不能动");
            Assert.That(File.Exists(Path.Combine(outDir, "proj_1.pkg")), Is.True);
        }

        [Test]
        public void CliPacksEachSubProjectOfAParentDirectory()
        {
            var parent = Mkdir("many");
            Write(parent, "one/project.json", "{\"title\":\"one\"}");
            Write(parent, "one/scene.json", "{}");
            Write(parent, "two/project.json", "{\"title\":\"two\"}");
            Write(parent, "two/scene.json", "{}");
            Write(parent, "notaproject", "x");
            Directory.CreateDirectory(Path.Combine(parent, "emptydir"));

            var outDir = Path.Combine(_dir, "outDirMany");
            Pack.Action(new PackOptions { Input = parent, OutputDirectory = outDir });

            Assert.That(File.Exists(Path.Combine(outDir, "one.pkg")), Is.True);
            Assert.That(File.Exists(Path.Combine(outDir, "two.pkg")), Is.True);

            // 两张壁纸共用一个输出目录时，同级那份 project.json 不能被后一张盖掉：
            // 盖完第一张的包就带着别人的标题/标签/属性了，而这两个文件同名不同内容。
            Assert.That(File.ReadAllText(Path.Combine(outDir, "project.json")), Is.EqualTo("{\"title\":\"one\"}"));
        }

        [Test]
        public void CliSaysNoWhenTheDirectoryIsNotAProject()
        {
            var root = Mkdir("nothing");
            Write(root, "readme.txt", "hi");
            var outDir = Path.Combine(_dir, "outDirNone");

            Pack.Action(new PackOptions { Input = root, OutputDirectory = outDir });

            Assert.That(Directory.Exists(outDir) && Directory.EnumerateFiles(outDir).Any(), Is.False,
                "认不出工程时不能留下半个包");
        }

        [Test]
        public void ProgressCallbackCountsUpToTheEntryTotal()
        {
            var root = MakeProject();
            var outPkg = Mkdir("out.pkg");
            var seen = new List<(int index, int total, string name)>();

            new LoosePackageBuilder().Build(root, outPkg, new LoosePackageOptions(),
                (index, total, name) => seen.Add((index, total, name)));

            Assert.That(seen, Is.Not.Empty);
            Assert.That(seen.Select(s => s.index), Is.EqualTo(Enumerable.Range(1, seen.Count)), "序号必须 1..N 连续");
            Assert.That(seen.All(s => s.total == seen.Count), Is.True, "分母在第一条就该已知且恒定");
            Assert.That(seen.Count, Is.EqualTo(Table(outPkg).Count), "每条条目都要回调一次");
            Assert.That(seen.Select(s => s.name), Is.Unique);
        }
    }
}
