using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using NUnit.Framework;
using RePKG_Re.Application.Package;
using RePKG_Re.Application.Texture;
using RePKG_Re.Command;
using RePKG_Re.Core.Package;
using RePKG_Re.Core.Texture;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace RePKG_Re.Tests
{
    /// <summary>
    /// pack --dxt 的三层契约：
    ///   1. DxtTexBuilder 产物必须能被 TexReader.Default 完整读回(容器/mip 链/LZ4/DXT 解码),
    ///      且 mip0 解码像素与源图比 PSNR 达标 —— "生成物先过自家解码关"的底线。
    ///   2. LoosePackageBuilder 侧的开关语义:默认仍是直通、--dxt 才换形态、编不动原样进包并留痕、
    ///      EncodeImages=false 时 DXT 也不做。
    ///   3. ParseDxt 的接受/拒绝集(命令行 --dxt 与 manifest packDxt 共用同一条解析)。
    /// 与 DxtEncoderRoundtripTests 的分工:那边钉块级位布局,这边钉容器与调用链。
    /// Load 里"非法清单 → exit 1"的进程语义按仓库既有约定不在进程内测(Environment.Exit 测不了)。
    /// </summary>
    [TestFixture]
    public class DxtTexBuilderTests
    {
        private string _dir;

        [SetUp]
        public void SetUp()
        {
            _dir = Path.Combine(Path.GetTempPath(), "repkg-dxt-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_dir);
        }

        [TearDown]
        public void TearDown()
        {
            try { if (Directory.Exists(_dir)) Directory.Delete(_dir, true); } catch { }
        }

        // ---------- 原料 ----------

        /// <summary>四象限(两纯色两渐变):硬边 + 平滑区都有,还带跨行重复块给 LZ4 吃。</summary>
        private static byte[] MakePng(int w = 64, int h = 64)
        {
            using var image = new Image<Rgba32>(w, h);
            for (var y = 0; y < h; y++)
            for (var x = 0; x < w; x++)
            {
                var qx = x < w / 2; var qy = y < h / 2;
                byte r, g, b;
                if (qx == qy) { r = (byte)(x * 4 % 256); g = 40; b = 200; }
                else { r = 20; g = (byte)(y * 4 % 256); b = (byte)((x + y) * 2 % 256); }
                image[x, y] = new Rgba32(r, g, b, 255);
            }
            using var stream = new MemoryStream();
            image.SaveAsPng(stream);
            return stream.ToArray();
        }

        /// <summary>全平图:每个 DXT 块字节相同,LZ4 收益无悬念 —— 用它钉"LZ4 择优"真的会压。</summary>
        private static byte[] MakeFlatPng(int w, int h)
        {
            using var image = new Image<Rgba32>(w, h, new Rgba32(120, 120, 120, 255));
            using var stream = new MemoryStream();
            image.SaveAsPng(stream);
            return stream.ToArray();
        }

        /// <summary>两帧 GIF,用仓库自带的 GifWriter 造(ImageSharp 2.1.13 的多帧 SaveAsGif
        /// 在这个环境里会 NRE,GifWriterTests 也因此才存在)。</summary>
        private static byte[] MakeAnimatedGif()
        {
            using var red = new Image<Rgba32>(32, 32, new Rgba32(255, 0, 0, 255));
            using var blue = new Image<Rgba32>(32, 32, new Rgba32(0, 0, 255, 255));
            using var ms = new MemoryStream();
            using (var writer = new GifWriter(ms, 32, 32))
            {
                writer.WriteFrame(red, 10);
                writer.WriteFrame(blue, 10);
                writer.Finish();
            }
            return ms.ToArray();
        }

        private static Tex ReadBack(byte[] texBytes)
        {
            using var br = new BinaryReader(new MemoryStream(texBytes), Encoding.UTF8);
            return (Tex)TexReader.Default.ReadFrom(br);
        }

        /// <summary>只读 mip 记录(readPixels=false):lz4 标志与 DecompressedBytesCount 保留,载荷被 Seek 掉。
        /// 这是唯一能"看见 lz4 标志"的公开读法 —— TexImageContainerReader 对 readPixels=true 一律解压。</summary>
        private static Tex ReadRecords(byte[] texBytes)
        {
            using var br = new BinaryReader(new MemoryStream(texBytes), Encoding.UTF8);
            return (Tex)TexReader.Default.ReadFrom(br, readPixels: false);
        }

        private static double PsnrRgb(byte[] a, byte[] b)
        {
            long se = 0; long n = 0;
            var pixels = Math.Min(a.Length, b.Length) / 4;
            for (var i = 0; i < pixels; i++)
                for (var c = 0; c < 3; c++) { var d = a[i * 4 + c] - b[i * 4 + c]; se += d * d; n++; }
            var mse = (double)se / n;
            return mse == 0 ? double.PositiveInfinity : 10 * Math.Log10(255 * 255 / mse);
        }

        // ---------- 1. 产物可被自家读侧完整读回 ----------

        [TestCase(TexFormat.DXT1)]
        [TestCase(TexFormat.DXT3)]
        [TestCase(TexFormat.DXT5)]
        public void BuiltTex_ReadsBack_WithFullMipChain(TexFormat format)
        {
            var bytes = DxtTexBuilder.Build("a.png", MakePng(), format, TexFlags.ClampUVs);
            var tex = ReadBack(bytes);

            Assert.That(tex.Header.Format, Is.EqualTo(format));
            Assert.That(tex.Header.Flags, Is.EqualTo(TexFlags.ClampUVs));
            Assert.That(tex.ImagesContainer.Magic, Is.EqualTo("TEXB0002"));
            Assert.That(tex.ImagesContainer.ImageContainerVersion, Is.EqualTo(TexImageContainerVersion.Version2));
            Assert.That(tex.Header.TextureWidth, Is.EqualTo(64));
            Assert.That(tex.ImagesContainer.Images, Has.Count.EqualTo(1));

            // 64 → 32 → 16 → 8 → 4:止于 4x4(DXT 最小完整块)
            var mips = tex.FirstImage.Mipmaps;
            Assert.That(mips.Select(m => m.Width + "x" + m.Height),
                Is.EqualTo(new[] { "64x64", "32x32", "16x16", "8x8", "4x4" }));
            Assert.That(mips.All(m => m.Format == MipmapFormat.RGBA8888), Is.True,
                "TexReader.Default 应把每级 DXT 解成 RGBA8888");
            Assert.That(mips[0].Bytes.Length, Is.EqualTo(64 * 64 * 4));
            Assert.That(mips[4].Bytes.Length, Is.EqualTo(4 * 4 * 4));
        }

        [Test]
        public void BuiltTex_Mip0_MatchesSource_Psnr()
        {
            var png = MakePng();
            using var source = Image.Load<Rgba32>(png);
            var srcPixels = new byte[64 * 64 * 4];
            source.CopyPixelDataTo(MemoryMarshal.Cast<byte, Rgba32>(srcPixels.AsSpan()));

            var tex = ReadBack(DxtTexBuilder.Build("a.png", png, TexFormat.DXT5, TexFlags.None));
            var psnr = PsnrRgb(srcPixels, tex.FirstImage.FirstMipmap.Bytes);
            Assert.That(psnr, Is.GreaterThan(25), $"mip0 PSNR {psnr:F1}dB");
        }

        [Test]
        public void BuiltTex_Lz4Pays_OnFlatContent_AndChainReadsBack()
        {
            var bytes = DxtTexBuilder.Build("a.png", MakeFlatPng(128, 128), TexFormat.DXT5, TexFlags.None);

            // 全平 128x128 的 DXT5 块字节 16KB 高度重复,LZ4 必须有净收益,否则择优判据形同虚设
            var records = ReadRecords(bytes);
            var mips = records.FirstImage.Mipmaps;
            Assert.That(mips, Has.Count.EqualTo(6), "128 → 64 → 32 → 16 → 8 → 4 共 6 级");
            Assert.That(mips.All(m => m.IsLZ4Compressed), Is.True, "平坦内容每一级都该压得过");
            Assert.That(mips.All(m => m.Bytes == null), Is.True, "readPixels=false 不该装载荷");
            Assert.That(mips[0].DecompressedBytesCount, Is.EqualTo(128 * 128), "mip0 lz4 前的块字节数");
            Assert.That(mips[1].DecompressedBytesCount, Is.EqualTo(64 * 64));
            Assert.That(mips[5].DecompressedBytesCount, Is.EqualTo(16));
            // 整个文件的块字节链原样是 21840B,压完的包必须明显小于它
            Assert.That(bytes.Length, Is.LessThan(21840 / 4), "LZ4 没有净收益就是这个开关的失败");

            // 全链读回:逐级 lz4 解压 + DXT 解码后像素尺寸正确
            var tex = ReadBack(bytes);
            Assert.That(tex.FirstImage.Mipmaps[0].Bytes.Length, Is.EqualTo(128 * 128 * 4));
            Assert.That(tex.FirstImage.Mipmaps[5].Bytes.Length, Is.EqualTo(4 * 4 * 4));
        }

        [Test]
        public void OddSize_NonPowerOf_Two_ChainTerminates_At_FourByFour()
        {
            var tex = ReadBack(DxtTexBuilder.Build("a.png", MakePng(50, 34), TexFormat.DXT5, TexFlags.None));
            var sizes = tex.FirstImage.Mipmaps.Select(m => m.Width + "x" + m.Height).ToArray();

            Assert.That(sizes[0], Is.EqualTo("50x34"));
            Assert.That(sizes, Is.EqualTo(new[] { "50x34", "25x17", "12x8", "6x4", "4x4" }),
                "减半链一律 Math.Max(4, w/2),末级恒为 4x4");
            Assert.That(tex.FirstImage.Mipmaps.All(m => m.Format == MipmapFormat.RGBA8888), Is.True);
        }

        // ---------- 2. 拒绝项 ----------

        [Test]
        public void Refuses_MissingArgs()
        {
            Assert.Throws<ArgumentNullException>(() =>
                DxtTexBuilder.Build(null, MakePng(), TexFormat.DXT5, TexFlags.None));
            Assert.Throws<ArgumentException>(() =>
                DxtTexBuilder.Build("a.png", Array.Empty<byte>(), TexFormat.DXT5, TexFlags.None));
            Assert.Throws<ArgumentException>(() =>
                DxtTexBuilder.Build("a.png", MakePng(), TexFormat.RGBA8888, TexFlags.None),
                "只有 BC1/BC2/BC3 有编码器");
        }

        [Test]
        public void Refuses_TinyImage()
        {
            Assert.That(
                Assert.Throws<ArgumentException>(() =>
                    DxtTexBuilder.Build("a.png", MakePng(2, 2), TexFormat.DXT5, TexFlags.None)).Message,
                Does.Contain("4x4"));
        }

        [Test]
        public void Refuses_GifAndVideoFlags()
        {
            Assert.Throws<ArgumentException>(() =>
                DxtTexBuilder.Build("a.png", MakePng(), TexFormat.DXT5, TexFlags.IsGif));
            Assert.Throws<ArgumentException>(() =>
                DxtTexBuilder.Build("a.png", MakePng(), TexFormat.DXT5, TexFlags.IsVideoTexture));
        }

        [Test]
        public void Refuses_MultiFrameGif()
        {
            var gif = MakeAnimatedGif();
            using var probe = Image.Load<Rgba32>(gif);
            Assert.That(probe.Frames.Count, Is.GreaterThan(1), "前置:语料必须是多帧 GIF");

            Assert.That(
                Assert.Throws<ArgumentException>(() =>
                    DxtTexBuilder.Build("a.gif", gif, TexFormat.DXT5, TexFlags.None)).Message,
                Does.Contain("多帧"));
        }

        [Test]
        public void Refuses_UnreadableBytes()
        {
            Assert.Catch(() =>
                DxtTexBuilder.Build("a.png", new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 }, TexFormat.DXT5, TexFlags.None));
        }

        // ---------- 3. pack 侧解析(--dxt 与 manifest packDxt 共用) ----------

        [TestCase("dxt1", TexFormat.DXT1)]
        [TestCase("DXT5", TexFormat.DXT5)]
        [TestCase(" dxt3 ", TexFormat.DXT3)]
        [TestCase("1", TexFormat.DXT1)]
        [TestCase("5", TexFormat.DXT5)]
        [TestCase("", null)]
        [TestCase(null, null)]
        public void ParseDxt_AcceptsDocumentedForms(string value, TexFormat? expected)
        {
            Assert.That(PackOptions.ParseDxt(value), Is.EqualTo(expected));
        }

        [TestCase("dxt2")]
        [TestCase("etc2")]
        [TestCase("rgba8888")]
        public void ParseDxt_RejectsUnknown(string value)
        {
            Assert.Throws<ArgumentException>(() => PackOptions.ParseDxt(value));
        }

        // ---------- 4. pack 集成:默认直通,开关才换形态 ----------

        private string Mkdir(string rel)
        {
            var path = Path.Combine(new[] { _dir }.Concat(rel.Split('/')).ToArray());
            Directory.CreateDirectory(path);
            return path;
        }

        private string MakeProject()
        {
            var root = Mkdir("proj");
            File.WriteAllText(Path.Combine(root, "project.json"),
                "{\"file\":\"scene.json\",\"preview\":\"preview.gif\",\"type\":\"scene\"}");
            File.WriteAllText(Path.Combine(root, "scene.json"), "{\"general\":{}}");
            File.WriteAllBytes(Path.Combine(root, "preview.gif"), new byte[] { 1, 2, 3, 4 });
            File.WriteAllBytes(Of(root, "materials/bg.png"), MakePng(64, 64));
            File.WriteAllBytes(Of(root, "materials/flat.png"), MakePng(32, 32));
            return root;
        }

        private static string Of(string root, string relative)
        {
            var path = Path.Combine(new[] { root }.Concat(relative.Split('/')).ToArray());
            Directory.CreateDirectory(Path.GetDirectoryName(path) ?? path);
            return path;
        }

        private static Package ReadPkg(string path)
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            using var br = new BinaryReader(fs, Encoding.UTF8);
            return new PackageReader { ReadEntryBytes = true }.ReadFrom(br);
        }

        private static byte[] Entry(Package pkg, string relative) =>
            pkg.Entries.Single(e => e.FullPath.Replace('\\', '/') == relative).Bytes;

        [Test]
        public void Pack_Default_StaysPassthrough()
        {
            var root = MakeProject();
            var outPkg = Path.Combine(_dir, "default.pkg");
            var report = new LoosePackageBuilder().Build(root, outPkg, new LoosePackageOptions());

            Assert.That(report.Encoded, Is.EqualTo(2));
            var tex = ReadBack(Entry(ReadPkg(outPkg), "materials/bg.tex"));
            Assert.That(tex.Header.Format, Is.EqualTo(TexFormat.RGBA8888), "默认必须还是直通 PNG blob");
            Assert.That(tex.ImagesContainer.Magic, Is.EqualTo("TEXB0004"));
        }

        [Test]
        public void Pack_DxtFlag_SwitchesFormat_AndShrinksPackage()
        {
            // 尺寸收益只在"PNG 压不动"的内容上可断言:合成平滑小图的 PNG 本来就只有几百字节,
            // DXT5(w*h 起)反而更大 —— 那条 4K 33MB→8MB 的账是照片级内容的,这里用噪声图模拟。
            var root = Mkdir("proj-noise");
            File.WriteAllText(Path.Combine(root, "project.json"), "{\"file\":\"scene.json\",\"type\":\"scene\"}");
            File.WriteAllText(Path.Combine(root, "scene.json"), "{\"general\":{}}");
            File.WriteAllBytes(Of(root, "noise.png"), MakeNoisyPng(256, 256));

            var passthroughPkg = Path.Combine(_dir, "pt.pkg");
            var dxtPkg = Path.Combine(_dir, "dxt.pkg");
            new LoosePackageBuilder().Build(root, passthroughPkg, new LoosePackageOptions());
            var report = new LoosePackageBuilder().Build(root, dxtPkg,
                new LoosePackageOptions { EncodeDxtFormat = TexFormat.DXT5 });

            Assert.That(report.Encoded, Is.EqualTo(1));

            var tex = ReadBack(Entry(ReadPkg(dxtPkg), "noise.tex"));
            Assert.That(tex.Header.Format, Is.EqualTo(TexFormat.DXT5));
            Assert.That(tex.ImagesContainer.Magic, Is.EqualTo("TEXB0002"));
            Assert.That(tex.FirstImage.Mipmaps, Has.Count.EqualTo(7), "256 → 4x4");

            Assert.That(new FileInfo(dxtPkg).Length,
                Is.LessThan(new FileInfo(passthroughPkg).Length),
                "噪声内容上 DXT 包必须小于 PNG 直通包,这是这个开关存在的理由");
        }

        /// <summary>随机噪声:PNG 救不了它(接近 w*h*4),DXT5 恒 1 字节/像素 —— 体量对比只有在这种内容上成立。</summary>
        private static byte[] MakeNoisyPng(int w, int h)
        {
            using var image = new Image<Rgba32>(w, h);
            var rnd = new Random(5);
            var px = new Rgba32[w * h];
            for (var i = 0; i < px.Length; i++)
                px[i] = new Rgba32((byte)rnd.Next(256), (byte)rnd.Next(256), (byte)rnd.Next(256), 255);
            image.CopyPixelDataTo(px);
            using var ms = new MemoryStream();
            image.SaveAsPng(ms);
            return ms.ToArray();
        }

        [Test]
        public void Pack_DxtFlag_AnimatedGif_ShipsLooseWithWarning()
        {
            var root = MakeProject();
            File.WriteAllBytes(Of(root, "materials/anim.gif"), MakeAnimatedGif());

            var outPkg = Path.Combine(_dir, "mixed.pkg");
            var report = new LoosePackageBuilder().Build(root, outPkg,
                new LoosePackageOptions { EncodeDxtFormat = TexFormat.DXT5 });

            var pkg = ReadPkg(outPkg);
            Assert.That(pkg.Entries.Any(e => e.FullPath.Replace('\\', '/').EndsWith("anim.gif")), Is.True,
                "编不动的 GIF 原样进包");
            Assert.That(pkg.Entries.Any(e => e.Extension == ".tex" && e.FullPath.Contains("anim")), Is.False,
                "GIF 直通在真实包里无先例,不该有 anim.tex");
            Assert.That(report.Warnings.Any(s => s.Contains("anim.gif")), Is.True,
                "回落必须进 Warnings(不是 Notes),否则'整包都是 DXT'的预期落空了都没人知道");
        }

        [Test]
        public void Pack_DxtFlag_SmallPngFailingDxt_FallsBackToPassthrough()
        {
            // 2x2 PNG:DXT 拒(不够一个完整块),直通收(PNG blob 不挑尺寸)——
            // 回落要在 Notes 留痕,产物必须还是 TEXB0004 直通形态。
            var root = Mkdir("proj");
            File.WriteAllText(Path.Combine(root, "project.json"), "{\"file\":\"scene.json\",\"type\":\"scene\"}");
            File.WriteAllText(Path.Combine(root, "scene.json"), "{\"general\":{}}");
            File.WriteAllBytes(Of(root, "tiny.png"), MakePng(2, 2));

            var outPkg = Path.Combine(_dir, "fallback.pkg");
            var report = new LoosePackageBuilder().Build(root, outPkg,
                new LoosePackageOptions { EncodeDxtFormat = TexFormat.DXT5 });

            var tex = ReadBack(Entry(ReadPkg(outPkg), "tiny.tex"));
            Assert.That(tex.ImagesContainer.Magic, Is.EqualTo("TEXB0004"), "DXT 拒收后必须还有直通那条路");
            Assert.That(tex.Header.Format, Is.EqualTo(TexFormat.RGBA8888));
            Assert.That(report.Notes.Any(s => s.Contains("tiny.png") && s.Contains("回落")), Is.True,
                "回落动作要在 Notes 里看得见");
            Assert.That(report.Warnings, Is.Empty, "回落不是错误,不该进 Warnings 通道");
        }

        [Test]
        public void Pack_NoEncodeWins_OverDxt()
        {
            var root = MakeProject();
            var outPkg = Path.Combine(_dir, "off.pkg");
            var report = new LoosePackageBuilder().Build(root, outPkg, new LoosePackageOptions
            {
                EncodeImages = false,
                EncodeDxtFormat = TexFormat.DXT5
            });

            Assert.That(report.Encoded, Is.EqualTo(0));
            var pkg = ReadPkg(outPkg);
            Assert.That(pkg.Entries.Any(e => e.FullPath.EndsWith("bg.png")), Is.True,
                "关了编码就不该封任何 .tex,DXT 也在'编码'之列");
            Assert.That(pkg.Entries.Any(e => e.Extension == ".tex"), Is.False);
        }

        // ---------- 5. manifest 键 ----------

        private string WriteManifest(string optionsJson)
        {
            var path = Path.Combine(_dir, "m-" + Guid.NewGuid().ToString("N") + ".json");
            File.WriteAllText(path,
                "{\"mode\":\"pack\",\"wallpapers\":[{\"id\":\"1\",\"input\":\"x\",\"output\":\"y\"}]" +
                (optionsJson == null ? "" : ",\"options\":" + optionsJson) + "}");
            return path;
        }

        [Test]
        public void Manifest_PackDxt_RoundTripsIntoBuilderOptions()
        {
            var loaded = BatchManifest.Load(WriteManifest("{\"packDxt\":\"dxt5\"}"));
            Assert.That(loaded.Options.PackDxt, Is.EqualTo(TexFormat.DXT5));
            Assert.That(loaded.ToPackOptions().ToBuilderOptions().EncodeDxtFormat, Is.EqualTo(TexFormat.DXT5));
        }

        [Test]
        public void Manifest_NoPackDxt_StaysNull()
        {
            Assert.That(BatchManifest.Load(WriteManifest("{}")).Options?.PackDxt, Is.Null);
            Assert.That(BatchManifest.Load(WriteManifest(null)).Options?.PackDxt, Is.Null);
        }

        [Test]
        public void Manifest_PackDxt_SmalltalkAliases()
        {
            Assert.That(BatchManifest.Load(WriteManifest("{\"packDxt\":\"DXT1\"}")).Options.PackDxt,
                Is.EqualTo(TexFormat.DXT1));
            Assert.That(BatchManifest.Load(WriteManifest("{\"packDxt\":\"3\"}")).Options.PackDxt,
                Is.EqualTo(TexFormat.DXT3));
        }
    }
}
