using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
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
            var json = JsonSerializer.Serialize(new
            {
                threads,
                wallpapers = wallpapers.Select(w => new { id = w.id, input = w.input, output = w.output }).ToList(),
                options = options ?? new { overwrite = true }
            });
            File.WriteAllText(path, json);
            return path;
        }

        /// <summary>
        /// batch 事件行的测试侧读取器。事件协议是前端契约的一部分,字段名以 Batch.cs 的发射格式为准。
        /// </summary>
        private sealed class Ev
        {
            public string Type { get; }
            public string Action { get; }
            public string Id { get; }
            public int Pos { get; }
            public int TotalEntries { get; }

            private Ev(JsonElement e)
            {
                Type = Str(e, "type");
                Action = Str(e, "action");
                Id = Str(e, "id");
                Pos = Int(e, "pos");
                TotalEntries = Int(e, "total_entries");
            }

            public static Ev Parse(string json)
            {
                using var doc = JsonDocument.Parse(json);
                return new Ev(doc.RootElement.Clone());
            }

            private static string Str(JsonElement e, string name)
                => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

            private static int Int(JsonElement e, string name)
                => e.TryGetProperty(name, out var v) && v.TryGetInt32(out var n) ? n : 0;
        }

        private static List<Ev> RunBatchAndCapture(string manifestPath, int threads = 0)
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
                .Select(l => Ev.Parse(l))
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

        /// <summary>
        /// README「Manifest schema」那张表的守门用例:顶层键 + 21 个 options 键一次填满,逐字段断言映射结果,
        /// 并断言 manifest 无法表达的那几个必须保持关闭。表里任何一格与代码分叉(改名、漏映射、
        /// 默认值变了),这里就红。
        /// </summary>
        [Test]
        public void ManifestSchema_EveryDocumentedKey_IsMapped()
        {
            var path = Path.Combine(_tempDir, "full.json");
            // 手写 JSON 而不是序列化匿名对象:测试里出现的键名就是线上格式
            File.WriteAllText(path, @"{
  ""threads"": 3,
  ""wallpapers"": [ { ""id"": ""W1"", ""input"": ""C:/in"", ""output"": ""C:/out"", ""outputName"": ""My Wallpaper"" } ],
  ""options"": {
    ""overwrite"": true,
    ""onlypaths"": [ ""materials"", ""sounds/ambient"" ],
    ""ignorepaths"": [ ""effects"" ],
    ""pathsDepth"": 2,
    ""onlyexts"": [ ""png"" ],
    ""ignoreexts"": [ ""mp4"" ],
    ""outputOnlyExts"": [ ""png"", ""json"" ],
    ""outputIgnoreExts"": [ ""dat"" ],
    ""keepSubfolderStructure"": true,
    ""noTexConvert"": true,
    ""onlyTexImages"": true,
    ""filterEffectImages"": 85,
    ""mpkgMagic"": ""PKGM0016"",
    ""keepAudio"": true,
    ""noLz4"": true,
    ""mpkgReduction"": 2,
    ""mpkgEtc2"": true,
    ""mpkgNoShaderCompat"": true,
    ""pkgMagic"": ""PKGV0023"",
    ""noDematerialize"": true,
    ""keepReductionKey"": true
  }
}");

            var manifest = BatchManifest.Load(path);
            Assert.That(manifest.Threads, Is.EqualTo(3));
            Assert.That(manifest.Wallpapers[0].Id, Is.EqualTo("W1"));
            Assert.That(manifest.Wallpapers[0].OutputName, Is.EqualTo("My Wallpaper"));

            var o = manifest.ToExtractOptions();
            Assert.That(o.Overwrite, Is.True);
            Assert.That(o.OnlyPaths, Is.EqualTo("materials,sounds/ambient"));
            Assert.That(o.IgnorePaths, Is.EqualTo("effects"));
            Assert.That(o.PathsDepth, Is.EqualTo(2));
            Assert.That(o.OnlyExts, Is.EqualTo("png"));
            Assert.That(o.IgnoreExts, Is.EqualTo("mp4"));
            Assert.That(o.OutputOnlyExts, Is.EqualTo("png,json"));
            Assert.That(o.OutputIgnoreExts, Is.EqualTo("dat"));
            // 表里标明"名字与行为相反"的那一格:true 把条目路径压平,等价 -s/--singledir
            Assert.That(o.SingleDir, Is.True);
            Assert.That(o.NoTexConvert, Is.True);
            Assert.That(o.OnlyTexImages, Is.True);
            Assert.That(o.FilterEffectImages, Is.EqualTo(85.0));

            // mpkg 那三个键的取反关系最容易写反:keepAudio → !DropAudio,noLz4 → !UseLz4
            var mobile = manifest.ToMobileOptions();
            Assert.That(mobile.Magic, Is.EqualTo("PKGM0016"));
            Assert.That(mobile.DropAudio, Is.False);
            Assert.That(mobile.UseLz4, Is.False);
            Assert.That(mobile.Reduction, Is.EqualTo(2));
            Assert.That(mobile.EncodeEtc2, Is.True);
            Assert.That(mobile.ShaderCompat, Is.False);   // mpkgNoShaderCompat: true → 关

            // 编码只在缩过之后才有意义:÷1 又开编码是配错了,要报出来而不是静默发 fmt0
            manifest.Options.MpkgReduction = 1;
            Assert.Throws<ArgumentException>(() => manifest.ToMobileOptions());

            // mode:pkg(逆向)那三个键同样钉住:两个取反键最容易被写反
            var pc = manifest.ToPcOptions();
            Assert.That(pc.Magic, Is.EqualTo("PKGV0023"));
            Assert.That(pc.Dematerialize, Is.False);      // noDematerialize: true → 关
            Assert.That(pc.ClearTextureReduction, Is.False); // keepReductionKey: true → 不清

            // manifest 表达不了的选项:batch 自己接管,映射结果必须是关闭/空
            Assert.That(o.Lazy, Is.False);            // 执行器本就按需读取条目
            Assert.That(o.OutputDirectory, Is.Empty); // 输出目录按每张壁纸单独给
            Assert.That(o.TexDirectory, Is.False);
            Assert.That(o.Recursive, Is.False);
            Assert.That(o.UseName, Is.False);
            Assert.That(o.CopyProject, Is.False);
            Assert.That(o.MaxEntrySize, Is.Zero);
            Assert.That(o.MinEntrySize, Is.Zero);
        }

        /// <summary>兼容改写必须由 manifest 默认开着：漏配键的后果是一块白，不是包变大。</summary>
        [Test]
        public void MpkgNoShaderCompat_Absent_ShaderCompat_StaysOn()
        {
            var path = Path.Combine(_tempDir, "mpkg_defaults.json");
            File.WriteAllText(path, @"{
  ""mode"": ""mpkg"",
  ""wallpapers"": [ { ""id"": ""A"", ""input"": ""C:/in"", ""output"": ""C:/out"" } ]
}");

            var mobile = BatchManifest.Load(path).ToMobileOptions();
            Assert.That(mobile.ShaderCompat, Is.True);
            Assert.That(mobile.Reduction, Is.EqualTo(1));
            Assert.That(mobile.EncodeEtc2, Is.False);
            Assert.That(mobile.DropAudio, Is.True);
        }

        /// <summary>表头那句「manifest 键按不区分大小写匹配」的守门用例。</summary>
        [Test]
        public void Manifest_Keys_Are_CaseInsensitive()
        {
            var path = Path.Combine(_tempDir, "case.json");
            File.WriteAllText(path, @"{
  ""Threads"": 2,
  ""Wallpapers"": [ { ""Id"": ""A"", ""Input"": ""C:/in"", ""Output"": ""C:/out"", ""OutputName"": ""N"" } ],
  ""Options"": { ""PathsDepth"": 1, ""OnlyPaths"": [ ""materials"" ], ""KeepSubfolderStructure"": true }
}");

            var manifest = BatchManifest.Load(path);
            Assert.That(manifest.Threads, Is.EqualTo(2));
            Assert.That(manifest.Wallpapers[0].Id, Is.EqualTo("A"));
            Assert.That(manifest.Wallpapers[0].OutputName, Is.EqualTo("N"));

            var o = manifest.ToExtractOptions();
            Assert.That(o.PathsDepth, Is.EqualTo(1));
            Assert.That(o.OnlyPaths, Is.EqualTo("materials"));
            Assert.That(o.SingleDir, Is.True);
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

            Assert.That(events.Any(e => e.Type == "batch" && e.Action == "done"), Is.True);
            var dones = events.Where(e => e.Type == "wallpaper" && e.Action == "done").Select(e => e.Id).ToList();
            Assert.That(dones, Is.EquivalentTo(new[] { "A", "B" }));
            // 无错误事件
            Assert.That(events.Any(e => e.Type == "error"), Is.False);

            // 输出文件:wp1 的 tex 转换出 png + tex-json,raw 保留
            Assert.That(ListFiles(out1),
                Is.EquivalentTo(new[] { "tex/scene.png", "tex/scene.tex", "tex/scene.tex-json", "txt/foo.txt" }));
            Assert.That(ListFiles(out2), Is.EquivalentTo(new[] { "img/bar.png" }));
        }

        [Test]
        public void Batch_Extracts_FileInput_SinglePkg()
        {
            // input 直接指向单个 .pkg 文件(文件兼容模式):只拆该文件,不做目录枚举
            var pkgPath = Path.Combine(_tempDir, "single.pkg");
            WritePkg(pkgPath,
                ("txt/foo.txt", Encoding.ASCII.GetBytes("hello")),
                ("tex/scene.tex", RgbaTexBytes()));

            var outDir = Path.Combine(_tempDir, "out");
            var manifestPath = WriteManifest("m.json",
                new List<(string, string, string)> { ("0", pkgPath, outDir) }, threads: 4);

            var events = RunBatchAndCapture(manifestPath);

            Assert.That(events.Any(e => e.Type == "batch" && e.Action == "done"), Is.True);
            Assert.That(events.Any(e => e.Type == "error"), Is.False);
            Assert.That(ListFiles(outDir),
                Is.EquivalentTo(new[] { "tex/scene.png", "tex/scene.tex", "tex/scene.tex-json", "txt/foo.txt" }));
        }

        [Test]
        public void Batch_Mixed_FileAndDirInput()
        {
            // 文件与目录混合:文件条目只拆自身;目录条目枚举目录内所有 pkg
            var pkgFile = Path.Combine(_tempDir, "one.pkg");
            WritePkg(pkgFile, ("txt/a.txt", Encoding.ASCII.GetBytes("aaa")));

            var dir = Path.Combine(_tempDir, "wpd");
            Directory.CreateDirectory(dir);
            WritePkg(Path.Combine(dir, "two.pkg"), ("txt/b.txt", Encoding.ASCII.GetBytes("bbb")));

            var out1 = Path.Combine(_tempDir, "out1");
            var out2 = Path.Combine(_tempDir, "out2");
            var manifestPath = WriteManifest("m.json",
                new List<(string, string, string)>
                {
                    ("F", pkgFile, out1),
                    ("D", dir, out2)
                }, threads: 4);

            var events = RunBatchAndCapture(manifestPath);

            var dones = events.Where(e => e.Type == "wallpaper" && e.Action == "done")
                .Select(e => e.Id).ToList();
            Assert.That(dones, Is.EquivalentTo(new[] { "F", "D" }));
            Assert.That(events.Any(e => e.Type == "error"), Is.False);
            Assert.That(ListFiles(out1), Is.EquivalentTo(new[] { "txt/a.txt" }));
            Assert.That(ListFiles(out2), Is.EquivalentTo(new[] { "txt/b.txt" }));
        }

        /// <summary>
        /// mode=mpkg 的 wallpapers[].outputName:文件名主干由调用方给(壁纸标题或工坊 ID),
        /// repkg 负责清洗非法文件名字符;一个壁纸拆出多个包时缀上源包名,免得几个包写进同一个文件。
        /// </summary>
        [Test]
        public void Batch_Mpkg_RespectsOutputName_And_DisambiguatesMultiplePackages()
        {
            var one = Path.Combine(_tempDir, "wp1");
            var many = Path.Combine(_tempDir, "wp2");
            Directory.CreateDirectory(one);
            Directory.CreateDirectory(many);
            WritePkg(Path.Combine(one, "scene.pkg"), ("scene.json", Encoding.ASCII.GetBytes("{}")));
            WritePkg(Path.Combine(many, "scene.pkg"), ("scene.json", Encoding.ASCII.GetBytes("{}")));
            WritePkg(Path.Combine(many, "extra.pkg"), ("scene.json", Encoding.ASCII.GetBytes("{}")));

            var outOne = Path.Combine(_tempDir, "o1");
            var outMany = Path.Combine(_tempDir, "o2");
            var manifestPath = Path.Combine(_tempDir, "mpkg-name.json");
            File.WriteAllText(manifestPath, JsonSerializer.Serialize(new
            {
                mode = "mpkg",
                threads = 1,
                wallpapers = new object[]
                {
                    new { id = "1", input = one, output = outOne, outputName = "2636878454" },
                    new { id = "2", input = many, output = outMany, outputName = "A:B?C" }
                }
            }));

            var events = RunBatchAndCapture(manifestPath);
            Assert.That(events.Exists(e => e.Type == "error"), Is.False);
            Assert.That(ListFiles(outOne), Is.EquivalentTo(new[] { "2636878454.mpkg" }));
            Assert.That(ListFiles(outMany), Is.EquivalentTo(new[] { "A_B_C_scene.mpkg", "A_B_C_extra.mpkg" }));
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
            Assert.That(events.Any(e => e.Type == "error" && e.Id == "BAD"), Is.True);
            Assert.That(events.Any(e => e.Type == "error" && e.Id == "GOOD"), Is.False);
            Assert.That(events.Any(e => e.Type == "wallpaper" && e.Action == "done" && e.Id == "GOOD"), Is.True);
            Assert.That(events.Any(e => e.Type == "wallpaper" && e.Action == "done" && e.Id == "BAD"), Is.True);
            Assert.That(events.Any(e => e.Type == "batch" && e.Action == "done"), Is.True);
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
            Assert.That(events.Any(e => e.Type == "error"), Is.False);
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
            var entryEvents = events.Where(e => e.Type == "entry").Select(e => e.Pos).ToList();
            Assert.That(entryEvents, Is.EqualTo(new[] { 1, 2, 3 }));
            var start = events.First(e => e.Type == "wallpaper");
            Assert.That(start.TotalEntries, Is.EqualTo(3));
        }

        [Test]
        public void MemoryGate_Acquire_Release_And_Bypass()
        {
            // 小预订:系统可用内存充足,必然通过(Start 内同步首采样,availPhys 立即可用)
            var gate = new MemoryGate(safetyRatio: 0.7, pollIntervalMs: 100, maxRetries: 5, retryDelayMs: 1);
            gate.Start();
            try
            {
                Assert.That(gate.TryAcquire(1024 * 1024), Is.True);
                gate.Release(1024 * 1024);

                // 超大预订(10TB):超过任何真实预算,重试耗尽后放行(返回 false,不预订)
                // 放行语义 = 退化无闸行为,保证不因闸而死锁
                Assert.That(gate.TryAcquire(10L * 1024 * 1024 * 1024 * 1024), Is.False);
            }
            finally
            {
                gate.Stop();
            }
        }
    }
}
