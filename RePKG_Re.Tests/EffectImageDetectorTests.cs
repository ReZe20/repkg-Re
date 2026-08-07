using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
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
    /// 效果图剔除测试:--filter-effect-images 按"转换图透明/黑色占比 ≥ 阈值"跳过整条目。
    /// 单元:EffectImageDetector 判定;集成:Extract.Action 输出文件集合。
    /// </summary>
    [TestFixture]
    public class EffectImageDetectorTests
    {
        // ---------- 单元:判定算法 ----------

        private static Image<Rgba32> CreateImage(int width, int height, Func<int, int, Rgba32> pixel)
        {
            var image = new Image<Rgba32>(width, height);
            image.ProcessPixelRows(accessor =>
            {
                for (int y = 0; y < accessor.Height; y++)
                {
                    var row = accessor.GetRowSpan(y);
                    for (int x = 0; x < row.Length; x++)
                        row[x] = pixel(x, y);
                }
            });
            return image;
        }

        [Test]
        public void AllTransparent_EffectAt85()
        {
            using (var image = CreateImage(256, 256, (x, y) => new Rgba32(0, 0, 0, 0)))
            {
                var hit = EffectImageDetector.IsEffectImage(image, 0.85, out var tr, out var br);
                Assert.IsTrue(hit);
                Assert.AreEqual(1.0, tr, 0.001);
                Assert.AreEqual(0.0, br, 0.001);
            }
        }

        [Test]
        public void AllBlack_EffectAt85()
        {
            using (var image = CreateImage(256, 256, (x, y) => new Rgba32(0, 0, 0, 255)))
            {
                var hit = EffectImageDetector.IsEffectImage(image, 0.85, out var tr, out var br);
                Assert.IsTrue(hit);
                Assert.AreEqual(0.0, tr, 0.001);
                Assert.AreEqual(1.0, br, 0.001);
            }
        }

        [Test]
        public void TransparentBlack_CountsAsTransparent_NotBlack()
        {
            // RGBA 全 0:透明黑像素只计入透明占比
            using (var image = CreateImage(64, 64, (x, y) => new Rgba32(0, 0, 0, 0)))
            {
                var hit = EffectImageDetector.IsEffectImage(image, 0.85, out var tr, out var br);
                Assert.IsTrue(hit);
                Assert.AreEqual(1.0, tr, 0.001);
                Assert.AreEqual(0.0, br, 0.001);
            }
        }

        [Test]
        public void AllWhite_NotEffect()
        {
            using (var image = CreateImage(256, 256, (x, y) => new Rgba32(255, 255, 255, 255)))
            {
                var hit = EffectImageDetector.IsEffectImage(image, 0.85, out var tr, out var br);
                Assert.IsFalse(hit);
                Assert.AreEqual(0.0, tr, 0.001);
                Assert.AreEqual(0.0, br, 0.001);
            }
        }

        [Test]
        public void HalfTransparent_HitAt40_NotAt85()
        {
            // 上半透明、下半白:透明占比 = 0.5
            using (var image = CreateImage(256, 256,
                (x, y) => y < 128 ? new Rgba32(0, 0, 0, 0) : new Rgba32(255, 255, 255, 255)))
            {
                Assert.IsFalse(EffectImageDetector.IsEffectImage(image, 0.85, out _, out _));
                Assert.IsTrue(EffectImageDetector.IsEffectImage(image, 0.40, out var tr, out _));
                Assert.AreEqual(0.5, tr, 0.001);
            }
        }

        [Test]
        public void NinetyPercentBlack_HitAt85()
        {
            using (var image = CreateImage(100, 100,
                (x, y) => x < 90 ? new Rgba32(0, 0, 0, 255) : new Rgba32(255, 255, 255, 255)))
            {
                var hit = EffectImageDetector.IsEffectImage(image, 0.85, out var tr, out var br);
                Assert.IsTrue(hit);
                Assert.GreaterOrEqual(br, 0.85);
                Assert.AreEqual(0.0, tr, 0.001);
            }
        }

        [Test]
        public void NoiseImage_NotEffect_EarlyExitKeepsRatiosCorrect()
        {
            var rnd = new Random(42);
            using (var image = CreateImage(512, 512,
                (x, y) => new Rgba32((byte)rnd.Next(256), (byte)rnd.Next(256), (byte)rnd.Next(256), 255)))
            {
                var hit = EffectImageDetector.IsEffectImage(image, 0.85, out var tr, out var br);
                Assert.IsFalse(hit);
                Assert.Less(tr, 0.01);
                Assert.Less(br, 0.01);
            }
        }

        // ---------- 集成:Extract.Action ----------

        private string _tempDir;
        private string _pkgPath;

        [SetUp]
        public void SetUp()
        {
            _tempDir = Path.Combine(Path.GetTempPath(), "repkg_effect_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_tempDir);

            var package = new Package { Magic = "PKGV0005" };
            package.Entries.Add(new PackageEntry
            {
                Bytes = CreateRgbaTexBytes(255), // 全白:正常纹理
                FullPath = "tex/normal.tex"
            });
            package.Entries.Add(new PackageEntry
            {
                Bytes = CreateRgbaTexBytes(0), // 全透明:效果图
                FullPath = "tex/transparent.tex"
            });
            package.Entries.Add(new PackageEntry
            {
                Bytes = CreateRgbaTexBytesWithBlackAlpha(), // 不透明全黑:效果图
                FullPath = "tex/black.tex"
            });
            package.Entries.Add(new PackageEntry
            {
                Bytes = Encoding.ASCII.GetBytes("hello text"),
                FullPath = "txt/foo.txt"
            });

            _pkgPath = Path.Combine(_tempDir, "test.pkg");
            using (var fs = File.Create(_pkgPath))
            using (var bw = new BinaryWriter(fs, Encoding.UTF8))
            {
                new PackageWriter().WriteTo(bw, package);
            }
        }

        [TearDown]
        public void TearDown()
        {
            try { Directory.Delete(_tempDir, true); } catch { }
        }

        /// <summary>构造 2x2 RGBA8888 最小合法 TEX,像素值 = byte(全同色)</summary>
        private static byte[] CreateRgbaTexBytes(byte value)
        {
            var pixels = new byte[2 * 2 * 4];
            for (int i = 0; i < pixels.Length; i++)
                pixels[i] = value;
            return BuildTexBytes(pixels);
        }

        /// <summary>构造 2x2 不透明全黑 TEX(alpha=255, RGB=0)</summary>
        private static byte[] CreateRgbaTexBytesWithBlackAlpha()
        {
            var pixels = new byte[2 * 2 * 4];
            for (int i = 3; i < pixels.Length; i += 4)
                pixels[i] = 255;
            return BuildTexBytes(pixels);
        }

        private static byte[] BuildTexBytes(byte[] pixels)
        {
            var tex = new Tex
            {
                Magic1 = "TEXV0005",
                Magic2 = "TEXI0001",
                Header = new TexHeader
                {
                    Format = TexFormat.RGBA8888,
                    Flags = 0,
                    TextureWidth = 2,
                    TextureHeight = 2,
                    ImageWidth = 2,
                    ImageHeight = 2,
                    UnkInt0 = 0
                },
                ImagesContainer = new TexImageContainer
                {
                    Magic = "TEXB0002",
                    ImageContainerVersion = TexImageContainerVersion.Version2,
                    Images =
                    {
                        new TexImage
                        {
                            Mipmaps =
                            {
                                new TexMipmap
                                {
                                    Bytes = pixels,
                                    Width = 2,
                                    Height = 2,
                                    Format = MipmapFormat.RGBA8888,
                                    IsLZ4Compressed = false,
                                    DecompressedBytesCount = pixels.Length
                                }
                            }
                        }
                    }
                },
                FrameInfoContainer = new TexFrameInfoContainer { Magic = "TEXS0001" }
            };

            using (var ms = new MemoryStream())
            using (var bw = new BinaryWriter(ms, Encoding.UTF8))
            {
                TexWriter.Default.WriteTo(bw, tex);
                return ms.ToArray();
            }
        }

        private List<string> ExtractAndList(string outDir, Action<ExtractOptions> configure)
        {
            Directory.CreateDirectory(outDir);
            var options = new ExtractOptions
            {
                Input = _pkgPath,
                OutputDirectory = outDir
            };
            configure(options);
            Extract.Action(options);

            return Directory.EnumerateFiles(outDir, "*", SearchOption.AllDirectories)
                .Select(f => f.Substring(outDir.Length).TrimStart('\\', '/').Replace('\\', '/'))
                .OrderBy(x => x, StringComparer.Ordinal)
                .ToList();
        }

        [Test]
        public void NoFilter_ExtractsEverything()
        {
            var files = ExtractAndList(Path.Combine(_tempDir, "o1"), _ => { });

            CollectionAssert.AreEqual(new[]
            {
                "tex/black.png",
                "tex/black.tex",
                "tex/black.tex-json",
                "tex/normal.png",
                "tex/normal.tex",
                "tex/normal.tex-json",
                "tex/transparent.png",
                "tex/transparent.tex",
                "tex/transparent.tex-json",
                "txt/foo.txt"
            }, files);
        }

        [Test]
        public void Filter85_SkipsWholeEntriesForEffectImages()
        {
            var files = ExtractAndList(Path.Combine(_tempDir, "o2"),
                o => o.FilterEffectImages = 85);

            // 效果图条目整条目消失(raw .tex / 转换图 / .tex-json 都不输出),正常条目不受影响
            CollectionAssert.AreEqual(new[]
            {
                "tex/normal.png",
                "tex/normal.tex",
                "tex/normal.tex-json",
                "txt/foo.txt"
            }, files);
        }

        [Test]
        public void Filter75_StillSkipsWholeEntriesForEffectImages()
        {
            var files = ExtractAndList(Path.Combine(_tempDir, "o3"),
                o => o.FilterEffectImages = 75);

            CollectionAssert.AreEqual(new[]
            {
                "tex/normal.png",
                "tex/normal.tex",
                "tex/normal.tex-json",
                "txt/foo.txt"
            }, files);
        }
    }
}
