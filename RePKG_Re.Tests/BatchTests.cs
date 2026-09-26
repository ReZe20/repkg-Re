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
using SixLabors.ImageSharp;

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
            public string Msg { get; }

            private Ev(JsonElement e)
            {
                Type = Str(e, "type");
                Action = Str(e, "action");
                Id = Str(e, "id");
                Pos = Int(e, "pos");
                TotalEntries = Int(e, "total_entries");
                Msg = Str(e, "msg");
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
            Assert.That(opts.SingleDir, Is.True); // 旧键 keepSubfolderStructure 仍可读(兼容) → -s
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
    ""singleDir"": true,
    ""noTexConvert"": true,
    ""onlyTexImages"": true,
    ""filterEffectImages"": 85,
    ""mpkgMagic"": ""PKGM0016"",
    ""preset"": ""4x"",
    ""keepAudio"": true,
    ""noLz4"": true,
    ""mpkgReduction"": 2,
    ""mpkgEtc2"": true,
    ""mpkgNoShaderCompat"": true,
    ""mpkgNoDematerialize"": true,
    ""mpkgShrinkDx"": true,
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
            // 平铺开关:true 把条目路径压平,等价 -s/--singledir(旧键 keepSubfolderStructure 名字相反,已弃用)
            Assert.That(o.SingleDir, Is.True);
            Assert.That(o.NoTexConvert, Is.True);
            Assert.That(o.OnlyTexImages, Is.True);
            Assert.That(o.FilterEffectImages, Is.EqualTo(85.0));

            // mpkg 那三个键的取反关系最容易写反:keepAudio → !DropAudio,noLz4 → !UseLz4
            var mobile = manifest.ToMobileOptions();
            Assert.That(mobile.Magic, Is.EqualTo("PKGM0016"));
            Assert.That(mobile.DropAudio, Is.False);
            Assert.That(mobile.UseLz4, Is.False);
            Assert.That(mobile.Reduction, Is.EqualTo(2));      // 同时写了 preset:4x —— 显式键赢
            Assert.That(mobile.EncodeEtc2, Is.True);
            Assert.That(mobile.ShaderCompat, Is.False);   // mpkgNoShaderCompat: true → 关
            // 正向的关物化开关与逆向的 noDematerialize 同义不同键(两套键读自同一个 options 块),
            // 前缀必须留在这条断言里:写成 noDematerialize 会被读成逆向那条,mode:mpkg 下静悄悄不生效。
            Assert.That(mobile.Dematerialize, Is.False);  // mpkgNoDematerialize: true → 关
            Assert.That(mobile.ShrinkDx, Is.True);

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

        /// <summary>一个 mpkg 键都不写时的默认面必须是"真机验过的那版行为"：
        /// 兼容改写开着（漏配的后果是一块白，不是包变大），物化开着、DXT 不动手。</summary>
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
            // 这两条是"产物与真机验过的那版逐字节相同"的默认面:新键一旦默认改成动手的那边,
            // 没写任何选项的调用方(WE Tool 的默认档)就会开始拿到不一样的字节。
            Assert.That(mobile.Dematerialize, Is.True);
            Assert.That(mobile.ShrinkDx, Is.False);
        }

        /// <summary>
        /// wallpapers[].options 的覆盖语义:写了才覆盖、没写逐键回落全局、整段缺省等于没有条目级这回事。
        /// 逐键而不是整段,是因为前端只想改一张壁纸的档位时不该被迫重抄其余五个键。
        /// </summary>
        [Test]
        public void Manifest_PerWallpaperOptions_OverrideGlobalAndFallBack()
        {
            var path = Path.Combine(_tempDir, "peritem.json");
            File.WriteAllText(path, @"{
  ""mode"": ""mpkg"",
  ""wallpapers"": [
    { ""id"": ""A"", ""input"": ""C:/a"", ""output"": ""C:/out"" },
    { ""id"": ""B"", ""input"": ""C:/b"", ""output"": ""C:/out"",
      ""options"": { ""mpkgReduction"": 4, ""mpkgEtc2"": false, ""mpkgShrinkDx"": false, ""keepAudio"": true, ""mpkgMagic"": ""PKGM0016"" } },
    { ""id"": ""C"", ""input"": ""C:/c"", ""output"": ""C:/out"",
      ""options"": { ""mpkgNoShaderCompat"": true, ""mpkgNoDematerialize"": true } }
  ],
  ""options"": { ""overwrite"": true, ""mpkgReduction"": 2, ""mpkgEtc2"": true, ""mpkgShrinkDx"": true, ""mpkgMagic"": ""PKGM0019"" }
}");

            var manifest = BatchManifest.Load(path);

            var a = manifest.ToMobileOptions(manifest.Wallpapers[0]);
            Assert.That(a.Reduction, Is.EqualTo(2));
            Assert.That(a.EncodeEtc2, Is.True);
            Assert.That(a.Magic, Is.EqualTo("PKGM0019"));
            Assert.That(a.DropAudio, Is.True);        // 全局 keepAudio 缺省 false → 丢音频
            Assert.That(a.ShaderCompat, Is.True);
            Assert.That(a.Dematerialize, Is.True);    // 全局没写那条 → 照常物化
            Assert.That(a.ShrinkDx, Is.True);         // 全局写了 → 没覆盖的条目跟着

            var b = manifest.ToMobileOptions(manifest.Wallpapers[1]);
            Assert.That(b.Reduction, Is.EqualTo(4));   // 写了的覆盖
            Assert.That(b.EncodeEtc2, Is.False);
            Assert.That(b.ShrinkDx, Is.False);         // 全局开着,这条按住不动:于是 DXT 又回到逐字节照搬
            Assert.That(b.Magic, Is.EqualTo("PKGM0016"));
            Assert.That(b.DropAudio, Is.False);        // keepAudio:true → 留音频
            Assert.That(b.UseLz4, Is.True);            // 没写的仍取全局

            var c = manifest.ToMobileOptions(manifest.Wallpapers[2]);
            Assert.That(c.ShaderCompat, Is.False);     // 只写一个键
            Assert.That(c.Reduction, Is.EqualTo(2));   // 其余照旧
            Assert.That(c.Dematerialize, Is.False);    // 条目级关物化
            Assert.That(c.ShrinkDx, Is.True);          // 同一条里没写的键仍回落全局
        }

        /// <summary>
        /// 条目级能凑出全局看不见的非法组合:全局编码关着、某行自己开编码但解析后的除数是 1。
        /// 走的是解析器而不是 Load —— Load 里那条校验会 Environment.Exit(1),进程内测不了。
        /// </summary>
        [Test]
        public void Manifest_PerWallpaperOptions_InvalidCombo_ThrowsNamingTheWallpaper()
        {
            var manifest = new BatchManifest
            {
                Mode = "mpkg",
                Wallpapers =
                [
                    new BatchWallpaper
                    {
                        Id = "BAD", Input = "C:/a", Output = "C:/out",
                        Options = new BatchWallpaperOptions { MpkgReduction = 1, MpkgEtc2 = true }
                    }
                ],
                Options = new BatchOptionsModel { MpkgReduction = 2, MpkgEtc2 = false }
            };

            var ex = Assert.Throws<ArgumentException>(() => manifest.ToMobileOptions(manifest.Wallpapers[0]));
            Assert.That(ex!.Message, Does.Contain("BAD"));        // 必须点出是哪条壁纸,否则前端无从定位
            Assert.That(manifest.ToMobileOptions().EncodeEtc2, Is.False); // 全局口径本身仍然合法
        }

        /// <summary>
        /// 三档预设的等价性闸门:preset 展开出来的那一格必须与手写显式键逐字段相同。
        /// 这条之所以是硬闸门 —— "÷1 发 RGBA8、2× 起发 fmt5"替掉的是前端各自维护的那份派生,
        /// 表一旦和当年的派生分叉,所有真机验过的产物形态一起作废。
        /// </summary>
        [Test]
        [TestCase("1x", 1, false)]
        [TestCase("2x", 2, true)]
        [TestCase("4x", 4, true)]
        [TestCase("2X", 2, true)]   // 大小写不敏感
        [TestCase("4×", 4, true)]   // 界面里的乘号原样贴进清单也得认
        public void Manifest_MpkgPreset_ExpandsToTheExplicitPair(string preset, int reduction, bool etc2)
        {
            var byPreset = BatchManifest.Load(WriteMpkgOptions("p_" + preset.Replace('×', 'x') + ".json",
                "{ \"preset\": \"" + preset + "\" }")).ToMobileOptions();
            var byKeys = BatchManifest.Load(WriteMpkgOptions("k_" + preset.Replace('×', 'x') + ".json",
                "{ \"mpkgReduction\": " + reduction + ", \"mpkgEtc2\": " + (etc2 ? "true" : "false") + " }")).ToMobileOptions();

            Assert.That(byPreset.Reduction, Is.EqualTo(reduction), preset + " 的除数");
            Assert.That(byPreset.EncodeEtc2, Is.EqualTo(etc2), preset + " 的 ETC2");
            Assert.That(byPreset.Reduction, Is.EqualTo(byKeys.Reduction));
            Assert.That(byPreset.EncodeEtc2, Is.EqualTo(byKeys.EncodeEtc2));
        }

        /// <summary>预设只填"没写的格子",显式键永远压它 —— 否则自定义模式没法表达"2× 档但不要 ETC2"。</summary>
        [Test]
        public void Manifest_MpkgPreset_ExplicitKeysWin()
        {
            var shrunkOnly = BatchManifest.Load(WriteMpkgOptions("preset_plus_divisor.json",
                "{ \"preset\": \"4x\", \"mpkgReduction\": 2 }")).ToMobileOptions();
            Assert.That(shrunkOnly.Reduction, Is.EqualTo(2), "写了除数就以它为准,预设只补 ETC2 那格");
            Assert.That(shrunkOnly.EncodeEtc2, Is.True);

            // "缩而不编":UI 摆不出这个组合,手搓清单要能做(它是"÷2 但仍发 RGBA8"的对照包)
            var reducedNoCodec = BatchManifest.Load(WriteMpkgOptions("preset_plus_etc2.json",
                "{ \"preset\": \"2x\", \"mpkgEtc2\": false }")).ToMobileOptions();
            Assert.That(reducedNoCodec.Reduction, Is.EqualTo(2));
            Assert.That(reducedNoCodec.EncodeEtc2, Is.False);
        }

        /// <summary>
        /// 未知档位名不许当成"没填"退回 1× —— 那会把一整批包按原始尺寸发出去。
        /// 测的是两个真实抛点(档位表本身、条目级 preset 的解析处),不能拿 Load 测:
        /// 清单内容错误在 Load 内部就是"打印 + Environment.Exit(1)",进程内测不了(见类注释)。
        /// </summary>
        [Test]
        public void Manifest_MpkgPreset_UnknownName_Throws()
        {
            Assert.Throws<ArgumentException>(() => MpkgPresets.Require("8x", out _, out _, "options.preset"));

            var manifest = new BatchManifest
            {
                Mode = "mpkg",
                Wallpapers =
                [
                    new BatchWallpaper
                    {
                        Id = "BAD", Input = "C:/a", Output = "C:/out",
                        Options = new BatchWallpaperOptions { Preset = "8x" }
                    }
                ],
                Options = new BatchOptionsModel()
            };

            var ex = Assert.Throws<ArgumentException>(() => manifest.ToMobileOptions(manifest.Wallpapers[0]));
            Assert.That(ex!.Message, Does.Contain("8x"));
            Assert.That(ex.Message, Does.Contain("1x/2x/4x"));
            Assert.That(ex.Message, Does.Contain("BAD"));    // 条目级必须点出是哪条壁纸
        }

        /// <summary>条目级预设与全局预设、以及本条显式键的优先级。</summary>
        [Test]
        public void Manifest_PerWallpaperPreset_OverridesGlobal_AndYieldsToExplicitKeys()
        {
            var path = Path.Combine(_tempDir, "peritem-preset.json");
            File.WriteAllText(path, @"{
  ""mode"": ""mpkg"",
  ""wallpapers"": [
    { ""id"": ""A"", ""input"": ""C:/a"", ""output"": ""C:/out"" },
    { ""id"": ""B"", ""input"": ""C:/b"", ""output"": ""C:/out"", ""options"": { ""preset"": ""4x"" } },
    { ""id"": ""C"", ""input"": ""C:/c"", ""output"": ""C:/out"",
      ""options"": { ""preset"": ""2x"", ""mpkgEtc2"": false } }
  ],
  ""options"": { ""preset"": ""1x"" }
}");

            var manifest = BatchManifest.Load(path);

            var a = manifest.ToMobileOptions(manifest.Wallpapers[0]);
            Assert.That(a.Reduction, Is.EqualTo(1));       // 全局 1x
            Assert.That(a.EncodeEtc2, Is.False);

            var b = manifest.ToMobileOptions(manifest.Wallpapers[1]);
            Assert.That(b.Reduction, Is.EqualTo(4));       // 条目预设盖过全局预设
            Assert.That(b.EncodeEtc2, Is.True);

            var c = manifest.ToMobileOptions(manifest.Wallpapers[2]);
            Assert.That(c.Reduction, Is.EqualTo(2));       // 条目预设
            Assert.That(c.EncodeEtc2, Is.False);           // 条目显式键盖过条目预设
        }

        /// <summary>端到端:一批里两条各用不同 preset,产出的 scene.json 各写自己的除数,全局那档只兜底。</summary>
        [Test]
        public void Batch_Mpkg_PerWallpaperPreset_AppliesPerEntry()
        {
            var keep = Path.Combine(_tempDir, "wpKeep");
            var quarter = Path.Combine(_tempDir, "wpQuarter");
            Directory.CreateDirectory(keep);
            Directory.CreateDirectory(quarter);
            var scene = Encoding.UTF8.GetBytes("{\"general\":{\"version\":26}}");
            WritePkg(Path.Combine(keep, "scene.pkg"), ("scene.json", scene));
            WritePkg(Path.Combine(quarter, "scene.pkg"), ("scene.json", scene));

            var output = Path.Combine(_tempDir, "oPreset");
            var manifestPath = Path.Combine(_tempDir, "mpkg-preset.json");
            File.WriteAllText(manifestPath, JsonSerializer.Serialize(new
            {
                mode = "mpkg",
                threads = 1,
                wallpapers = new object[]
                {
                    new { id = "1", input = keep, output, outputName = "keep", options = new { preset = "1x" } },
                    new { id = "2", input = quarter, output, outputName = "quarter", options = new { preset = "4x" } }
                },
                options = new { overwrite = true, preset = "2x" }
            }));

            var events = RunBatchAndCapture(manifestPath);
            Assert.That(events.Exists(e => e.Type == "error"), Is.False);

            Assert.That(SceneText(Path.Combine(output, "keep.mpkg")), Does.Not.Contain("texturereduction"));
            Assert.That(SceneText(Path.Combine(output, "quarter.mpkg")), Does.Contain("\"texturereduction\" : 4"));
        }

        /// <summary>写一份只有单条壁纸、options 段原样给定的 mpkg 清单。</summary>
        private string WriteMpkgOptions(string name, string optionsJson)
        {
            var path = Path.Combine(_tempDir, name);
            File.WriteAllText(path, @"{
  ""mode"": ""mpkg"",
  ""wallpapers"": [ { ""id"": ""A"", ""input"": ""C:/in"", ""output"": ""C:/out"" } ],
  ""options"": " + optionsJson + @"
}");
            return path;
        }

        /// <summary>
        /// mode:inspect 读的是同一套档位键，但不写文件，所以清单里没有 output 也算合法 ——
        /// 逼调用方编一个用不到的目录，只会让人以为探测会往那里写东西。
        /// </summary>
        [Test]
        public void Manifest_Inspect_NeedsNoOutput_AndSharesTheTierKeys()
        {
            var path = Path.Combine(_tempDir, "inspect.json");
            File.WriteAllText(path, @"{
  ""mode"": ""inspect"",
  ""wallpapers"": [
    { ""id"": ""A"", ""input"": ""C:/a.pkg"" },
    { ""id"": ""B"", ""input"": ""C:/b.pkg"", ""options"": { ""preset"": ""4x"", ""mpkgEtc2"": false } }
  ],
  ""options"": { ""preset"": ""2x"" }
}");

            var manifest = BatchManifest.Load(path);
            Assert.That(manifest.IsInspect, Is.True);

            var a = manifest.ToMobileOptions(manifest.Wallpapers[0]);
            Assert.That(a.Reduction, Is.EqualTo(2));
            Assert.That(a.EncodeEtc2, Is.True);

            // 条目预设填格、显式键压过预设：与 mode:mpkg 完全同一份优先级，探测读数才对得上转换产物
            var b = manifest.ToMobileOptions(manifest.Wallpapers[1]);
            Assert.That(b.Reduction, Is.EqualTo(4));
            Assert.That(b.EncodeEtc2, Is.False);
        }

        /// <summary>表头那句「manifest 键按不区分大小写匹配」的守门用例。</summary>
        [Test]
        public void Manifest_Keys_Are_CaseInsensitive()
        {
            var path = Path.Combine(_tempDir, "case.json");
            File.WriteAllText(path, @"{
  ""Threads"": 2,
  ""Wallpapers"": [ { ""Id"": ""A"", ""Input"": ""C:/in"", ""Output"": ""C:/out"", ""OutputName"": ""N"" } ],
  ""Options"": { ""PathsDepth"": 1, ""OnlyPaths"": [ ""materials"" ], ""SingleDir"": true }
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

        /// <summary>
        /// 平铺开关的两条兼容语义:旧键单独给仍然生效;两键同时给时正名键 singleDir 获胜。
        /// </summary>
        [Test]
        public void SingleDir_LegacyKey_StillWorks_ModernKeyWins()
        {
            var legacyOnly = Path.Combine(_tempDir, "legacy.json");
            File.WriteAllText(legacyOnly, @"{
  ""wallpapers"": [ { ""id"": ""A"", ""input"": ""C:/in"", ""output"": ""C:/out"" } ],
  ""options"": { ""keepSubfolderStructure"": true }
}");
            Assert.That(BatchManifest.Load(legacyOnly).ToExtractOptions().SingleDir, Is.True);

            var both = Path.Combine(_tempDir, "both.json");
            File.WriteAllText(both, @"{
  ""wallpapers"": [ { ""id"": ""A"", ""input"": ""C:/in"", ""output"": ""C:/out"" } ],
  ""options"": { ""keepSubfolderStructure"": true, ""singleDir"": false }
}");
            Assert.That(BatchManifest.Load(both).ToExtractOptions().SingleDir, Is.False);
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

        /// <summary>
        /// 一批里两张壁纸各用各的档:条目级 options 必须真的走到转换器,而不是只在清单里改了个数。
        /// 这是前端只跑一批的前提 —— 否则它还得按档把队列切成多批。
        /// </summary>
        [Test]
        public void Batch_Mpkg_PerWallpaperReduction_AppliesPerEntry()
        {
            var keep = Path.Combine(_tempDir, "wpKeep");
            var quarter = Path.Combine(_tempDir, "wpQuarter");
            Directory.CreateDirectory(keep);
            Directory.CreateDirectory(quarter);
            // general 块至少要有一个成员:空块时 SetTextureReduction 报"读不到成员"而不写键
            var scene = Encoding.UTF8.GetBytes("{\"general\":{\"version\":26}}");
            WritePkg(Path.Combine(keep, "scene.pkg"), ("scene.json", scene));
            WritePkg(Path.Combine(quarter, "scene.pkg"), ("scene.json", scene));

            var output = Path.Combine(_tempDir, "oPerItem");
            var manifestPath = Path.Combine(_tempDir, "mpkg-peritem.json");
            File.WriteAllText(manifestPath, JsonSerializer.Serialize(new
            {
                mode = "mpkg",
                threads = 1,
                wallpapers = new object[]
                {
                    new { id = "1", input = keep, output, outputName = "keep", options = new { mpkgReduction = 1 } },
                    new { id = "2", input = quarter, output, outputName = "quarter", options = new { mpkgReduction = 4 } }
                },
                options = new { overwrite = true, mpkgReduction = 2 }
            }));

            var events = RunBatchAndCapture(manifestPath);
            Assert.That(events.Exists(e => e.Type == "error"), Is.False);

            // 除数 1 那条不写键;除数 4 那条写的是自己的 4,不是全局的 2
            Assert.That(SceneText(Path.Combine(output, "keep.mpkg")), Does.Not.Contain("texturereduction"));
            Assert.That(SceneText(Path.Combine(output, "quarter.mpkg")), Does.Contain("\"texturereduction\" : 4"));
        }

        /// <summary>读出产包里 scene.json 的文本(走 PackageReader,所以条目压没压都能读)。</summary>
        private static string SceneText(string packagePath)
        {
            using var fs = File.OpenRead(packagePath);
            using var br = new BinaryReader(fs, Encoding.UTF8, true);
            var pkg = new PackageReader { ReadEntryBytes = true }.ReadFrom(br);
            var entry = pkg.Entries.FirstOrDefault(e => e.FullPath == "scene.json");
            return entry?.Bytes is null ? "" : Encoding.UTF8.GetString(entry.Bytes);
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

        /// <summary>
        /// mode=pack 走的是 batch 的进程协议：一个工程目录 = 一条 wallpaper 事件流，
        /// start 带条目总数、条目事件按 id 打点、认不出工程只发 error 不炸整批。
        /// 这三条前端都依赖(进度、按 id 路由、崩溃重启后的跳过)，所以按事件断言而不是只看产物。
        /// </summary>
        [Test]
        public void Batch_Pack_EmitsSameProtocolAsExtract_And_WritesLooseSiblings()
        {
            var project = Path.Combine(_tempDir, "mywp");
            Directory.CreateDirectory(Path.Combine(project, "materials"));
            File.WriteAllText(Path.Combine(project, "project.json"), "{\"title\":\"mywp\",\"preview\":\"preview.gif\"}");
            File.WriteAllText(Path.Combine(project, "scene.json"), "{}");
            File.WriteAllBytes(Path.Combine(project, "preview.gif"), new byte[] { 1, 2, 3 });
            File.WriteAllBytes(Path.Combine(project, "materials", "a.png"), Png(4, 4));

            var other = Path.Combine(_tempDir, "notawallpaper");
            Directory.CreateDirectory(other);
            File.WriteAllText(Path.Combine(other, "readme.txt"), "x");

            var output = Path.Combine(_tempDir, "packed");
            var manifestPath = Path.Combine(_tempDir, "pack.json");
            File.WriteAllText(manifestPath, JsonSerializer.Serialize(new
            {
                mode = "pack",
                threads = 1,
                wallpapers = new object[]
                {
                    new { id = "1", input = project, output, outputName = "My WP :D" },
                    new { id = "2", input = other, output }
                },
                options = new { overwrite = false, pkgMagic = "PKGV0023" }
            }));

            var events = RunBatchAndCapture(manifestPath);

            var starts = events.FindAll(e => e.Type == "wallpaper" && e.Action == "start");
            Assert.That(starts, Has.Count.EqualTo(1), "认不出来的那个不该发 start");
            Assert.That(starts[0].TotalEntries, Is.EqualTo(2), "scene.json + materials/a.tex");

            Assert.That(events.FindAll(e => e.Type == "entry"), Has.Count.EqualTo(2));
            var errors = events.FindAll(e => e.Type == "error");
            Assert.That(errors, Has.Count.EqualTo(1));
            Assert.That(errors[0].Msg, Contains.Substring("No wallpaper project"));
            Assert.That(events.FindAll(e => e.Type == "wallpaper" && e.Action == "done"), Has.Count.EqualTo(2),
                "失败的那条也要 done，否则前端的队列会卡住");

            Assert.That(ListFiles(output), Is.EquivalentTo(new[] { "My WP _D.pkg", "preview.gif", "project.json" }),
                "包与同级 loose 元数据一起落在输出目录");

            using (var stream = File.OpenRead(Path.Combine(output, "My WP _D.pkg")))
            using (var reader = new BinaryReader(stream, Encoding.UTF8, true))
            {
                var package = new PackageReader { ReadEntryBytes = false }.ReadFrom(reader);
                Assert.That(package.Magic, Is.EqualTo("PKGV0023"));
                Assert.That(package.Entries.Select(e => e.FullPath),
                    Is.EquivalentTo(new[] { "materials/a.tex", "scene.json" }),
                    "project.json 与 preview.gif 只留在包外；源图变成了 .tex");
            }
        }

        /// <summary>4x4 全不透明 PNG，够测试用。</summary>
        private static byte[] Png(int w, int h)
        {
            using var image = new SixLabors.ImageSharp.Image<SixLabors.ImageSharp.PixelFormats.Rgba32>(w, h);
            using var ms = new MemoryStream();
            image.SaveAsPng(ms);
            return ms.ToArray();
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
