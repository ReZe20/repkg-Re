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
    /// 动画纹理(GIF 标志)输出扩展名测试:
    /// 转换输出应为 .gif(内容也是 GIF),而非曾错误使用的 .png 扩展名;
    /// 输出层过滤按 .gif 参与判断。
    /// </summary>
    [TestFixture]
    public class GifExtensionTests
    {
        private string _tempDir;
        private string _pkgPath;

        [SetUp]
        public void SetUp()
        {
            _tempDir = Path.Combine(Path.GetTempPath(), "repkg_gif_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_tempDir);

            var package = new Package { Magic = "PKGV0005" };
            package.Entries.Add(new PackageEntry
            {
                Bytes = Encoding.ASCII.GetBytes("fake png"),
                FullPath = "img/bar.png"
            });
            package.Entries.Add(new PackageEntry
            {
                Bytes = CreateAnimatedTexBytes(),
                FullPath = "tex/anim.tex"
            });
            package.Entries.Add(new PackageEntry
            {
                Bytes = CreateStaticTexBytes(),
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

        /// <summary>构造一个 2 帧 4x4 RGBA8888 的动画 TEX(带 IsGif 标志,转换后应为 .gif)</summary>
        private static byte[] CreateAnimatedTexBytes()
        {
            var redFrame = new byte[4 * 4 * 4];
            var blueFrame = new byte[4 * 4 * 4];
            for (var i = 0; i < 4 * 4; i++)
            {
                redFrame[i * 4 + 0] = 255; redFrame[i * 4 + 3] = 255;      // 红
                blueFrame[i * 4 + 2] = 255; blueFrame[i * 4 + 3] = 255;     // 蓝
            }

            var tex = new Tex
            {
                Magic1 = "TEXV0005",
                Magic2 = "TEXI0001",
                Header = new TexHeader
                {
                    Format = TexFormat.RGBA8888,
                    Flags = TexFlags.IsGif,
                    TextureWidth = 4,
                    TextureHeight = 4,
                    ImageWidth = 4,
                    ImageHeight = 4,
                    UnkInt0 = 0
                },
                ImagesContainer = new TexImageContainer
                {
                    Magic = "TEXB0002",
                    ImageContainerVersion = TexImageContainerVersion.Version2,
                    Images =
                    {
                        CreateRawImage(redFrame),
                        CreateRawImage(blueFrame)
                    }
                },
                FrameInfoContainer = new TexFrameInfoContainer
                {
                    Magic = "TEXS0001",
                    Frames =
                    {
                        new TexFrameInfo { ImageId = 0, Frametime = 0.1f, X = 0, Y = 0, Width = 4, Height = 4 },
                        new TexFrameInfo { ImageId = 1, Frametime = 0.1f, X = 0, Y = 0, Width = 4, Height = 4 }
                    }
                }
            };

            using (var ms = new MemoryStream())
            using (var bw = new BinaryWriter(ms, Encoding.UTF8))
            {
                TexWriter.Default.WriteTo(bw, tex);
                return ms.ToArray();
            }
        }

        private static TexImage CreateRawImage(byte[] bytes)
        {
            return new TexImage
            {
                Mipmaps =
                {
                    new TexMipmap
                    {
                        Bytes = bytes,
                        Width = 4,
                        Height = 4,
                        Format = MipmapFormat.RGBA8888,
                        IsLZ4Compressed = false,
                        DecompressedBytesCount = bytes.Length
                    }
                }
            };
        }

        /// <summary>构造一个 2x2 RGBA8888 静态 TEX(转换后应为 .png)</summary>
        private static byte[] CreateStaticTexBytes()
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

            return Directory.EnumerateFiles(outDir, "*", SearchOption.AllDirectories)
                .Select(f => f.Substring(outDir.Length).TrimStart('\\', '/').Replace('\\', '/'))
                .OrderBy(x => x, StringComparer.Ordinal)
                .ToList();
        }

        [Test]
        public void GetConvertedFormat_AnimatedTex_ReturnsGif()
        {
            var tex = new Tex
            {
                Header = new TexHeader { Flags = TexFlags.IsGif },
                ImagesContainer = new TexImageContainer
                {
                    Images =
                    {
                        new TexImage
                        {
                            Mipmaps = { new TexMipmap { Format = MipmapFormat.RGBA8888 } }
                        }
                    }
                }
            };

            Assert.AreEqual(MipmapFormat.ImageGIF, new TexToImageConverter().GetConvertedFormat(tex));
        }

        [Test]
        public void AnimatedTex_OutputsGifFileWithGifContent_NotPng()
        {
            var files = ExtractAndList(Path.Combine(_tempDir, "o1"), _ => { });

            CollectionAssert.AreEqual(new[]
            {
                "img/bar.png",
                "tex/anim.gif",
                "tex/anim.tex",
                "tex/anim.tex-json",
                "tex/scene.png",
                "tex/scene.tex",
                "tex/scene.tex-json"
            }, files);

            // 内容必须是真正的 GIF(修复前是 GIF 内容 + .png 名字)
            var header = new byte[6];
            using (var fs = File.OpenRead(Path.Combine(_tempDir, "o1", "tex", "anim.gif")))
                fs.Read(header, 0, 6);
            var magic = Encoding.ASCII.GetString(header);
            Assert.IsTrue(magic == "GIF89a" || magic == "GIF87a",
                $"expected GIF magic, got: {magic}");
        }

        [Test]
        public void OutputOnlyPng_FiltersOutAnimatedGif_KeepsStaticPng()
        {
            var files = ExtractAndList(Path.Combine(_tempDir, "o2"),
                o => o.OutputOnlyExts = "png");

            // 动画纹理按 .gif 参与输出层过滤:不在 png 白名单 → 整条目不写转换图
            CollectionAssert.AreEqual(new[]
            {
                "img/bar.png",
                "tex/scene.png"
            }, files);
        }

        [Test]
        public void OutputOnlyGif_KeepsAnimatedGif_DropsStaticPng()
        {
            var files = ExtractAndList(Path.Combine(_tempDir, "o3"),
                o => o.OutputOnlyExts = "gif");

            CollectionAssert.AreEqual(new[]
            {
                "tex/anim.gif"
            }, files);
        }
    }
}
