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
    /// 目录前缀过滤测试:--onlypaths / --ignorepaths 按条目路径前缀匹配(含子文件夹),
    /// 解析前过滤;materials/masks 命中 materials/masks/foo,不命中 materials/masks_extra/foo。
    /// </summary>
    [TestFixture]
    public class PathFilterTests
    {
        private string _tempDir;
        private string _pkgPath;

        [SetUp]
        public void SetUp()
        {
            _tempDir = Path.Combine(Path.GetTempPath(), "repkg_path_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_tempDir);

            var package = new Package { Magic = "PKGV0005" };
            package.Entries.Add(new PackageEntry
            {
                Bytes = CreateRgbaTexBytes(), // 可转换出 png
                FullPath = "materials/a.tex"
            });
            package.Entries.Add(new PackageEntry
            {
                Bytes = CreateRgbaTexBytes(),
                FullPath = "materials/masks/b.tex"
            });
            package.Entries.Add(new PackageEntry
            {
                Bytes = Encoding.ASCII.GetBytes("effect json"),
                FullPath = "materials/effects/c.json"
            });
            package.Entries.Add(new PackageEntry
            {
                Bytes = Encoding.ASCII.GetBytes("effect json 2"),
                FullPath = "effects/d.json"
            });
            package.Entries.Add(new PackageEntry
            {
                Bytes = Encoding.ASCII.GetBytes("model json"),
                FullPath = "models/e.json"
            });
            package.Entries.Add(new PackageEntry
            {
                Bytes = Encoding.ASCII.GetBytes("boundary test"),
                FullPath = "materials_masks_extra/f.txt"
            });
            package.Entries.Add(new PackageEntry
            {
                Bytes = Encoding.ASCII.GetBytes("txt"),
                FullPath = "txt/g.txt"
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

        /// <summary>2x2 RGBA8888 全白 TEX,转换出 png</summary>
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
                                    Bytes = new byte[2 * 2 * 4].Select(b => (byte)255).ToArray(),
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

            return Directory.EnumerateFiles(outDir, "*", SearchOption.AllDirectories)
                .Select(f => f.Substring(outDir.Length).TrimStart('\\', '/').Replace('\\', '/'))
                .OrderBy(x => x, StringComparer.Ordinal)
                .ToList();
        }

        [Test]
        public void NoFilter_ExtractsEverything()
        {
            var files = ExtractAndList(Path.Combine(_tempDir, "o0"), _ => { });

            CollectionAssert.AreEqual(new[]
            {
                "effects/d.json",
                "materials/a.png",
                "materials/a.tex",
                "materials/a.tex-json",
                "materials/effects/c.json",
                "materials/masks/b.png",
                "materials/masks/b.tex",
                "materials/masks/b.tex-json",
                "materials_masks_extra/f.txt",
                "models/e.json",
                "txt/g.txt"
            }, files);
        }

        [Test]
        public void OnlyPathsMaterials_KeepsOnlyMaterialsTree()
        {
            var files = ExtractAndList(Path.Combine(_tempDir, "o1"),
                o => o.OnlyPaths = "materials");

            CollectionAssert.AreEqual(new[]
            {
                "materials/a.png",
                "materials/a.tex",
                "materials/a.tex-json",
                "materials/effects/c.json",
                "materials/masks/b.png",
                "materials/masks/b.tex",
                "materials/masks/b.tex-json"
            }, files);
        }

        [Test]
        public void OnlyPathsSubfolder_SupportsNestedPrefix()
        {
            var files = ExtractAndList(Path.Combine(_tempDir, "o2"),
                o => o.OnlyPaths = "materials/masks");

            CollectionAssert.AreEqual(new[]
            {
                "materials/masks/b.png",
                "materials/masks/b.tex",
                "materials/masks/b.tex-json"
            }, files);
        }

        [Test]
        public void OnlyPathsMultiple_CommaSeparated()
        {
            var files = ExtractAndList(Path.Combine(_tempDir, "o3"),
                o => o.OnlyPaths = "materials/masks,models");

            CollectionAssert.AreEqual(new[]
            {
                "materials/masks/b.png",
                "materials/masks/b.tex",
                "materials/masks/b.tex-json",
                "models/e.json"
            }, files);
        }

        [Test]
        public void IgnorePaths_ExcludesTreeButKeepsBoundary()
        {
            var files = ExtractAndList(Path.Combine(_tempDir, "o4"),
                o => o.IgnorePaths = "materials");

            // materials_masks_extra 不是 materials 的子文件夹,保留
            CollectionAssert.AreEqual(new[]
            {
                "effects/d.json",
                "materials_masks_extra/f.txt",
                "models/e.json",
                "txt/g.txt"
            }, files);
        }

        [Test]
        public void IgnorePathsSubfolder_ExcludesOnlyThatSubfolder()
        {
            var files = ExtractAndList(Path.Combine(_tempDir, "o5"),
                o => o.IgnorePaths = "materials/masks");

            CollectionAssert.AreEqual(new[]
            {
                "effects/d.json",
                "materials/a.png",
                "materials/a.tex",
                "materials/a.tex-json",
                "materials/effects/c.json",
                "materials_masks_extra/f.txt",
                "models/e.json",
                "txt/g.txt"
            }, files);
        }

        [Test]
        public void OnlyAndIgnore_Compose()
        {
            var files = ExtractAndList(Path.Combine(_tempDir, "o6"),
                o =>
                {
                    o.OnlyPaths = "materials";
                    o.IgnorePaths = "materials/masks";
                });

            CollectionAssert.AreEqual(new[]
            {
                "materials/a.png",
                "materials/a.tex",
                "materials/a.tex-json",
                "materials/effects/c.json"
            }, files);
        }

        [Test]
        public void BackslashInput_NormalizedToForwardSlash()
        {
            var files = ExtractAndList(Path.Combine(_tempDir, "o7"),
                o => o.OnlyPaths = @"materials\masks");

            CollectionAssert.AreEqual(new[]
            {
                "materials/masks/b.png",
                "materials/masks/b.tex",
                "materials/masks/b.tex-json"
            }, files);
        }

        [Test]
        public void TrailingSlashAndSpaces_Tolerated()
        {
            var files = ExtractAndList(Path.Combine(_tempDir, "o8"),
                o => o.OnlyPaths = " materials/masks/ , models ");

            CollectionAssert.AreEqual(new[]
            {
                "materials/masks/b.png",
                "materials/masks/b.tex",
                "materials/masks/b.tex-json",
                "models/e.json"
            }, files);
        }

        // ---------- paths-depth:限制前缀后路径段数 ----------

        /// <summary>构造深度测试专用 pkg:materials/a.tex、materials/masks/b.tex、materials/masks/deep/c.tex、sounds/x.mp3</summary>
        private string BuildDepthPkg()
        {
            var package = new Package { Magic = "PKGV0005" };
            package.Entries.Add(new PackageEntry
            {
                Bytes = CreateRgbaTexBytes(),
                FullPath = "materials/a.tex"
            });
            package.Entries.Add(new PackageEntry
            {
                Bytes = CreateRgbaTexBytes(),
                FullPath = "materials/masks/b.tex"
            });
            package.Entries.Add(new PackageEntry
            {
                Bytes = CreateRgbaTexBytes(),
                FullPath = "materials/masks/deep/c.tex"
            });
            package.Entries.Add(new PackageEntry
            {
                Bytes = Encoding.ASCII.GetBytes("audio"),
                FullPath = "sounds/x.mp3"
            });

            var pkgPath = Path.Combine(_tempDir, "depth.pkg");
            using (var fs = File.Create(pkgPath))
            using (var bw = new BinaryWriter(fs, Encoding.UTF8))
            {
                new PackageWriter().WriteTo(bw, package);
            }

            return pkgPath;
        }

        private List<string> ExtractDepthAndList(string pkgPath, string outDir, Action<ExtractOptions> configure)
        {
            Directory.CreateDirectory(outDir);
            var options = new ExtractOptions
            {
                Input = pkgPath,
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
        public void PathsDepth1_OnlyDirectChildren_SubfoldersExcluded()
        {
            var pkg = BuildDepthPkg();
            var files = ExtractDepthAndList(pkg, Path.Combine(_tempDir, "d1"),
                o =>
                {
                    o.OnlyPaths = "materials,sounds";
                    o.PathsDepth = 1;
                });

            // materials/a.* 和 sounds/x.mp3 保留;materials/masks 及更深层整体排除
            CollectionAssert.AreEqual(new[]
            {
                "materials/a.png",
                "materials/a.tex",
                "materials/a.tex-json",
                "sounds/x.mp3"
            }, files);
        }

        [Test]
        public void PathsDepth2_AllowsOneLevelOfSubfolders()
        {
            var pkg = BuildDepthPkg();
            var files = ExtractDepthAndList(pkg, Path.Combine(_tempDir, "d2"),
                o =>
                {
                    o.OnlyPaths = "materials";
                    o.PathsDepth = 2;
                });

            // 深度2:含 materials/masks/b.*,不含 materials/masks/deep/c.*
            CollectionAssert.AreEqual(new[]
            {
                "materials/a.png",
                "materials/a.tex",
                "materials/a.tex-json",
                "materials/masks/b.png",
                "materials/masks/b.tex",
                "materials/masks/b.tex-json"
            }, files);
        }

        [Test]
        public void PathsDepth0_Unlimited_KeepsSubfolders()
        {
            var pkg = BuildDepthPkg();
            var files = ExtractDepthAndList(pkg, Path.Combine(_tempDir, "d3"),
                o => o.OnlyPaths = "materials");

            CollectionAssert.AreEqual(new[]
            {
                "materials/a.png",
                "materials/a.tex",
                "materials/a.tex-json",
                "materials/masks/b.png",
                "materials/masks/b.tex",
                "materials/masks/b.tex-json",
                "materials/masks/deep/c.png",
                "materials/masks/deep/c.tex",
                "materials/masks/deep/c.tex-json"
            }, files);
        }
    }
}
