using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Newtonsoft.Json;
using NUnit.Framework;
using RePKG_Re.Application.Package;
using RePKG_Re.Application.Texture;
using RePKG_Re.Command;
using RePKG_Re.Core.Package;
using RePKG_Re.Core.Texture;

namespace RePKG_Re.Tests
{
    /// <summary>
    /// batch 命令测试:manifest 解析、多壁纸并行提取、坏包不中断、线程数一致性、空格路径。
    /// 事件通过 Console.SetOut 捕获;exit 0 语义在命令行冒烟中验证(Environment.Exit 不可在进程内测)。
    /// </summary>
    [TestFixture]
    public class BatchTests
    {
        private string _tempDir;

        [SetUp]
        public void SetUp()
        {
            _tempDir = Path.Combine(Path.GetTempPath(), "repkg_batch_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_tempDir);
        }

        [TearDown]
        public void TearDown()
        {
            try { Directory.Delete(_tempDir, true); } catch { }
        }

        // ---------- 夹具 ----------

        private static void WritePkg(string path, params (string fullPath, byte[] bytes)[] entries)
        {
            var package = new Package { Magic = "PKGV0005" };
            foreach (var (fullPath, bytes) in entries)
                package.Entries.Add(new PackageEntry { Bytes = bytes, FullPath = fullPath });

            using (var fs = File.Create(path))
            using (var bw = new BinaryWriter(fs, Encoding.UTF8))
                new PackageWriter().WriteTo(bw, package);
        }

        /// <summary>构造一个 2x2 RGBA8888 的最小合法 TEX(转换后应为 .png)</summary>
        private static byte[] RgbaTexBytes()
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

        private string WriteManifest(string name, List<(string id, string input, string output)> wallpapers,
            int threads = 8, object options = null)
        {
            var path = Path.Combine(_tempDir, name);
            var json = JsonConvert.SerializeObject(new
            {
                threads,
                wallpapers = wallpapers.Select(w => new { id = w.id, input = w.input, output = w.output }).ToList(),
                options = options ?? new { overwrite = true }
            });
            File.WriteAllText(path, json);
            return path;
        }

        private static List<dynamic> RunBatchAndCapture(string manifestPath, int threads = 0)
        {
            var sw = new StringWriter();
            var original = Console.Out;
            Console.SetOut(sw);
            try
            {
                Batch.Action(new BatchOptions { Manifest = manifestPath, Threads = threads });
            }
            finally
            {
                Console.SetOut(original);
            }

            return sw.ToString()
                .Split('\n')
                .Where(l => l.StartsWith("{"))
                .Select(l => JsonConvert.DeserializeObject<dynamic>(l))
                .ToList();
        }

        private static List<string> ListFiles(string dir)
            => Directory.Exists(dir)
                ? Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories)
                    .Select(p => p.Substring(dir.Length + 1).Replace('\\', '/'))
                    .OrderBy(p => p, StringComparer.Ordinal)
                    .ToList()
                : new List<string>();

        // ---------- 用例 ----------

        [Test]
        public void Manifest_Load_And_OptionsMapping()
        {
            var wpDir = Path.Combine(_tempDir, "wp");
            Directory.CreateDirectory(wpDir);
            var manifestPath = WriteManifest("m.json",
                new List<(string, string, string)> { ("0", wpDir, Path.Combine(_tempDir, "out")) },
                threads: 4,
                options: new
                {
                    overwrite = true,
                    onlypaths = new[] { "materials", "sounds" },
                    pathsDepth = 1,
                    keepSubfolderStructure = true,
                    filterEffectImages = 85,
                    outputOnlyExts = new[] { "png", "mp4" }
                });

            var manifest = BatchManifest.Load(manifestPath);
            Assert.That(manifest.Threads, Is.EqualTo(4));
            Assert.That(manifest.Wallpapers, Has.Count.EqualTo(1));
            Assert.That(manifest.Wallpapers[0].Id, Is.EqualTo("0"));

            var opts = manifest.ToExtractOptions();
            Assert.That(opts.OnlyPaths, Is.EqualTo("materials,sounds"));
            Assert.That(opts.PathsDepth, Is.EqualTo(1));
            Assert.That(opts.SingleDir, Is.True); // keepSubfolderStructure → -s
            Assert.That(opts.FilterEffectImages, Is.EqualTo(85.0));
            Assert.That(opts.OutputOnlyExts, Is.EqualTo("png,mp4"));
            Assert.That(opts.Overwrite, Is.True);
        }

        [Test]
        public void Batch_Extracts_MultipleWallpapers_With_DoneEvents()
        {
            var wp1 = Path.Combine(_tempDir, "wp one");
            var wp2 = Path.Combine(_tempDir, "wp two");
            var out1 = Path.Combine(_tempDir, "out1");
            var out2 = Path.Combine(_tempDir, "out2");
            Directory.CreateDirectory(wp1);
            Directory.CreateDirectory(wp2);
            WritePkg(Path.Combine(wp1, "a.pkg"),
                ("txt/foo.txt", Encoding.ASCII.GetBytes("hello")),
                ("tex/scene.tex", RgbaTexBytes()));
            WritePkg(Path.Combine(wp2, "b.pkg"),
                ("img/bar.png", Encoding.ASCII.GetBytes("fake png")));

            var manifestPath = WriteManifest("m.json",
                new List<(string, string, string)>
                {
                    ("A", wp1, out1),
                    ("B", wp2, out2)
                }, threads: 8);

            var events = RunBatchAndCapture(manifestPath);

            Assert.That(events.Any(e => e.type == "batch" && e.action == "done"), Is.True);
            var dones = events.Where(e => e.type == "wallpaper" && e.action == "done").Select(e => (string)e.id).ToList();
            Assert.That(dones, Is.EquivalentTo(new[] { "A", "B" }));
            // 无错误事件
            Assert.That(events.Any(e => e.type == "error"), Is.False);

            // 输出文件:wp1 的 tex 转换出 png + tex-json,raw 保留
            Assert.That(ListFiles(out1),
                Is.EquivalentTo(new[] { "tex/scene.png", "tex/scene.tex", "tex/scene.tex-json", "txt/foo.txt" }));
            Assert.That(ListFiles(out2), Is.EquivalentTo(new[] { "img/bar.png" }));
        }

        [Test]
        public void Batch_Threads1_And_Threads8_Produce_Identical_Output()
        {
            var wp = Path.Combine(_tempDir, "wp");
            Directory.CreateDirectory(wp);
            WritePkg(Path.Combine(wp, "a.pkg"),
                ("txt/a.txt", Encoding.ASCII.GetBytes("aaa")),
                ("txt/b.txt", Encoding.ASCII.GetBytes("bbb")),
                ("tex/scene.tex", RgbaTexBytes()),
                ("videos/clip.mp4", Encoding.ASCII.GetBytes("mp4")));

            var out1 = Path.Combine(_tempDir, "out1");
            var out8 = Path.Combine(_tempDir, "out8");
            var m1 = WriteManifest("m1.json", new List<(string, string, string)> { ("0", wp, out1) });
            var m8 = WriteManifest("m8.json", new List<(string, string, string)> { ("0", wp, out8) });

            RunBatchAndCapture(m1, threads: 1);
            RunBatchAndCapture(m8, threads: 8);

            var files1 = ListFiles(out1);
            var files8 = ListFiles(out8);
            Assert.That(files8, Is.EqualTo(files1), "threads=1 与 threads=8 输出文件集合不一致");

            foreach (var f in files1)
            {
                var b1 = File.ReadAllBytes(Path.Combine(out1, f));
                var b8 = File.ReadAllBytes(Path.Combine(out8, f));
                Assert.That(b8, Is.EqualTo(b1), $"文件内容不一致: {f}");
            }
        }

        [Test]
        public void Batch_Continues_After_Corrupt_Pkg()
        {
            var wpGood = Path.Combine(_tempDir, "good");
            var wpBad = Path.Combine(_tempDir, "bad");
            var outGood = Path.Combine(_tempDir, "outGood");
            var outBad = Path.Combine(_tempDir, "outBad");
            Directory.CreateDirectory(wpGood);
            Directory.CreateDirectory(wpBad);
            WritePkg(Path.Combine(wpGood, "a.pkg"), ("txt/ok.txt", Encoding.ASCII.GetBytes("ok")));
            File.WriteAllBytes(Path.Combine(wpBad, "bad.pkg"), new byte[] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10 });

            var manifestPath = WriteManifest("m.json",
                new List<(string, string, string)>
                {
                    ("GOOD", wpGood, outGood),
                    ("BAD", wpBad, outBad)
                }, threads: 4);

            var events = RunBatchAndCapture(manifestPath);

            // 坏包报 error,不中断;好壁纸照常完成
            Assert.That(events.Any(e => e.type == "error" && e.id == "BAD"), Is.True);
            Assert.That(events.Any(e => e.type == "error" && e.id == "GOOD"), Is.False);
            Assert.That(events.Any(e => e.type == "wallpaper" && e.action == "done" && e.id == "GOOD"), Is.True);
            Assert.That(events.Any(e => e.type == "wallpaper" && e.action == "done" && e.id == "BAD"), Is.True);
            Assert.That(events.Any(e => e.type == "batch" && e.action == "done"), Is.True);
            Assert.That(ListFiles(outGood), Is.EquivalentTo(new[] { "txt/ok.txt" }));
        }

        [Test]
        public void Batch_Handles_Spaces_In_Paths()
        {
            var wp = Path.Combine(_tempDir, "wallpaper with spaces 壁纸");
            var outDir = Path.Combine(_tempDir, "out with spaces");
            Directory.CreateDirectory(wp);
            WritePkg(Path.Combine(wp, "spaced.pkg"), ("txt/a.txt", Encoding.ASCII.GetBytes("x")));

            var manifestPath = WriteManifest("m.json",
                new List<(string, string, string)> { ("0", wp, outDir) }, threads: 2);

            var events = RunBatchAndCapture(manifestPath);
            Assert.That(events.Any(e => e.type == "error"), Is.False);
            Assert.That(ListFiles(outDir), Is.EquivalentTo(new[] { "txt/a.txt" }));
        }

        [Test]
        public void Batch_Entry_Events_Pos_Are_Contiguous_Serial()
        {
            var wp = Path.Combine(_tempDir, "wp");
            var outDir = Path.Combine(_tempDir, "out");
            Directory.CreateDirectory(wp);
            WritePkg(Path.Combine(wp, "a.pkg"),
                ("txt/a.txt", Encoding.ASCII.GetBytes("a")),
                ("txt/b.txt", Encoding.ASCII.GetBytes("b")),
                ("img/c.png", Encoding.ASCII.GetBytes("c")));

            var manifestPath = WriteManifest("m.json",
                new List<(string, string, string)> { ("0", wp, outDir) }, threads: 1);

            var events = RunBatchAndCapture(manifestPath, threads: 1);
            var entryEvents = events.Where(e => e.type == "entry").Select(e => (int)e.pos).ToList();
            Assert.That(entryEvents, Is.EqualTo(new[] { 1, 2, 3 }));
            var start = events.First(e => e.type == "wallpaper");
            Assert.That((int)start.total_entries, Is.EqualTo(3));
        }
    }
}
