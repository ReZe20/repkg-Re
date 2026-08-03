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

namespace RePKG_Re.Tests
{
    /// <summary>
    /// 输出层扩展名过滤测试：--output-ignoreexts / --output-onlyexts 按"输出文件"的扩展名
    /// 判断是否写出（TEX 条目照常转换，转换图按转换后格式如 .png 参与判断）。
    /// 旧 -i/-e 保持解析前过滤语义不变。
    /// </summary>
    [TestFixture]
    public class OutputFilterTests
    {
        private string _tempDir;
        private string _pkgPath;

        [SetUp]
        public void SetUp()
        {
            _tempDir = Path.Combine(Path.GetTempPath(), "repkg_filter_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_tempDir);

            var package = new Package { Magic = "PKGV0005" };
            package.Entries.Add(new PackageEntry
            {
                Bytes = Encoding.ASCII.GetBytes("hello text"),
                FullPath = "txt/foo.txt"
            });
            package.Entries.Add(new PackageEntry
            {
                Bytes = Encoding.ASCII.GetBytes("fake png"),
                FullPath = "img/bar.png"
            });
            package.Entries.Add(new PackageEntry
            {
                Bytes = CreateRgbaTexBytes(),
                FullPath = "tex/scene.tex"
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

        /// <summary>构造一个 2x2 RGBA8888 的最小合法 TEX(转换后应为 .png)</summary>
        private static byte[] CreateRgbaTexBytes()
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
                                    Bytes = new byte[2 * 2 * 4],
                                    Width = 2,
                                    Height = 2,
                                    Format = MipmapFormat.RGBA8888,
                                    IsLZ4Compressed = false,
                                    DecompressedBytesCount = 2 * 2 * 4
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

            var files = Directory.EnumerateFiles(outDir, "*", SearchOption.AllDirectories)
                .Select(f => f.Substring(outDir.Length).TrimStart('\\', '/').Replace('\\', '/'))
                .OrderBy(x => x, StringComparer.Ordinal)
                .ToList();

            Console.WriteLine("FILES[" + files.Count + "]: " + string.Join(", ", files));
            return files;
        }

        [Test]
        public void Baseline_ExtractsAllIncludingRawTexTexJsonAndConvertedImage()
        {
            var files = ExtractAndList(Path.Combine(_tempDir, "o1"), _ => { });

            CollectionAssert.AreEqual(new[]
            {
                "img/bar.png",
                "tex/scene.png",
                "tex/scene.tex",
                "tex/scene.tex-json",
                "txt/foo.txt"
            }, files);
        }

        [Test]
        public void OutputIgnoreTxt_RemovesTxtButKeepsTexAndConvertedImage()
        {
            var files = ExtractAndList(Path.Combine(_tempDir, "o2"),
                o => o.OutputIgnoreExts = "txt");

            CollectionAssert.AreEqual(new[]
            {
                "img/bar.png",
                "tex/scene.png",
                "tex/scene.tex",
                "tex/scene.tex-json"
            }, files);
        }

        [Test]
        public void OutputIgnoreTex_RemovesRawTexButKeepsConvertedImage()
        {
            var files = ExtractAndList(Path.Combine(_tempDir, "o3"),
                o => o.OutputIgnoreExts = "tex");

            CollectionAssert.AreEqual(new[]
            {
                "img/bar.png",
                "tex/scene.png",
                "tex/scene.tex-json",
                "txt/foo.txt"
            }, files);
        }

        [Test]
        public void OutputOnlyPng_KeepsPngEntriesAndConvertedImages_DropsRawTexTexJsonAndOthers()
        {
            var files = ExtractAndList(Path.Combine(_tempDir, "o4"),
                o => o.OutputOnlyExts = "png");

            // 核心新行为：转换出的 png 按 ".png" 保留，raw .tex / .tex-json / .txt 全部过滤
            CollectionAssert.AreEqual(new[]
            {
                "img/bar.png",
                "tex/scene.png"
            }, files);
        }

        [Test]
        public void OnlyTexImages_KeepsConvertedImageAndTexJson_DropsRawTex()
        {
            var files = ExtractAndList(Path.Combine(_tempDir, "o5"),
                o => o.OnlyTexImages = true);

            CollectionAssert.AreEqual(new[]
            {
                "img/bar.png",
                "tex/scene.png",
                "tex/scene.tex-json",
                "txt/foo.txt"
            }, files);
        }

        [Test]
        public void OutputIgnoreAndOnlyCombined_BothApply()
        {
            var files = ExtractAndList(Path.Combine(_tempDir, "o6"),
                o => { o.OutputIgnoreExts = "txt"; o.OutputOnlyExts = "png"; });

            CollectionAssert.AreEqual(new[]
            {
                "img/bar.png",
                "tex/scene.png"
            }, files);
        }

        [Test]
        public void LazyMode_BaselineMatchesNonLazy()
        {
            var files = ExtractAndList(Path.Combine(_tempDir, "o7"),
                o => o.Lazy = true);

            CollectionAssert.AreEqual(new[]
            {
                "img/bar.png",
                "tex/scene.png",
                "tex/scene.tex",
                "tex/scene.tex-json",
                "txt/foo.txt"
            }, files);
        }

        [Test]
        public void LazyMode_OutputOnlyPng_AppliesPreSkipWithoutReadingBytes()
        {
            var files = ExtractAndList(Path.Combine(_tempDir, "o8"),
                o => { o.OutputOnlyExts = "png"; o.Lazy = true; });

            CollectionAssert.AreEqual(new[]
            {
                "img/bar.png",
                "tex/scene.png"
            }, files);
        }

        // === 旧 -i/-e 语义回归：解析前过滤保持不变 ===

        [Test]
        public void LegacyIgnoreExts_FiltersBeforeParsing()
        {
            var files = ExtractAndList(Path.Combine(_tempDir, "o9"),
                o => o.IgnoreExts = "txt");

            // txt 条目不被解析；tex 条目照常 raw+转换+json
            CollectionAssert.AreEqual(new[]
            {
                "img/bar.png",
                "tex/scene.png",
                "tex/scene.tex",
                "tex/scene.tex-json"
            }, files);
        }

        [Test]
        public void LegacyOnlyExts_Tex_OnlyTexEntriesParsed_WithConversion()
        {
            var files = ExtractAndList(Path.Combine(_tempDir, "o10"),
                o => o.OnlyExts = "tex");

            // 只有 .tex 条目被解析；raw .tex 写出，且默认转换出 png + tex-json
            CollectionAssert.AreEqual(new[]
            {
                "tex/scene.png",
                "tex/scene.tex",
                "tex/scene.tex-json"
            }, files);
        }

        [Test]
        public void OutputOnlyTex_KeepsOnlyRawTex_NoConvertedImageNoJson()
        {
            var files = ExtractAndList(Path.Combine(_tempDir, "o11"),
                o => o.OutputOnlyExts = "tex");

            // 输出层白名单 tex：raw .tex 保留；转换图(png)与 tex-json(json)不在白名单被滤
            CollectionAssert.AreEqual(new[]
            {
                "tex/scene.tex"
            }, files);
        }
    }
}
