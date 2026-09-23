using System;
using System.IO;
using System.Text;
using NUnit.Framework;
using RePKG_Re.Application.Package;
using RePKG_Re.Core.Package;
using RePKG_Re.Core.Package.Interfaces;

namespace RePKG_Re.Tests
{
    /// <summary>
    /// GLSL → GLSL ES 兼容改写的规则用例：每条改写规则一个正例，每类"必须不动"的上下文一个反例，
    /// 再加三条硬不变式（只插 ".0"、幂等、行尾原样）。判不准就留原样是设计意图，所以反例和正例一样重要 ——
    /// 少改一层只是手机上一块白，多改会把现在正常的着色器弄白。
    /// </summary>
    [TestFixture]
    public class ShaderCompatPatcherTests
    {
        private string _dir;

        [SetUp]
        public void SetUp() => _dir = Path.Combine(Path.GetTempPath(), "repkg-compat-" + Guid.NewGuid().ToString("N"));

        [TearDown]
        public void TearDown()
        {
            try { if (Directory.Exists(_dir)) Directory.Delete(_dir, true); } catch { }
        }

        /// <summary>断言改写后的整段文本，并顺带钉住"长度差 = 2×字面量数"这条不变式。</summary>
        private static void AssertPatch(string source, string expected)
        {
            var r = ShaderCompatPatcher.Patch(source);
            Assert.AreEqual(expected, r.Text);
            Assert.AreEqual(expected.Length - source.Length, r.LiteralsChanged * 2, "长度差必须是 2×补上的字面量数");
            AssertInsertOnly(source, r.Text, r.LiteralsChanged);
            AssertIdempotent(r.Text);
        }

        private static void AssertUnchanged(string source)
        {
            var r = ShaderCompatPatcher.Patch(source);
            Assert.AreEqual(source, r.Text);
            Assert.IsFalse(r.Changed);
            Assert.AreEqual(0, r.LiteralsChanged);
        }

        private static void AssertInsertOnly(string source, string result, int literals)
        {
            var ia = 0;
            var ib = 0;
            var inserted = 0;
            while (ia < source.Length)
            {
                if (ib < result.Length && source[ia] == result[ib]) { ia++; ib++; continue; }
                if (ib + 1 < result.Length && result[ib] == '.' && result[ib + 1] == '0'
                    && ia > 0 && source[ia - 1] >= '0' && source[ia - 1] <= '9')
                {
                    ib += 2;
                    inserted++;
                    continue;
                }
                Assert.Fail($"第 {ia} 字符处不是\"插入 .0\"：改写破坏了原文");
            }
            Assert.AreEqual(source.Length + literals * 2, result.Length, "尾部多出内容");
            Assert.AreEqual(literals, inserted, "插入次数必须等于记录的字面量数");
        }

        private static void AssertIdempotent(string patched)
        {
            var again = ShaderCompatPatcher.Patch(patched);
            Assert.IsFalse(again.Changed, "已经补过 .0 的文本不能被再改一次");
            Assert.AreEqual(patched, again.Text);
        }

        // ---- 改写规则 ----

        [Test]
        public void FloatDeclaration_Init_GetsDotZero()
        {
            AssertPatch(
                "void main() {\n    float volume = 2;\n}\n",
                "void main() {\n    float volume = 2.0;\n}\n");
        }

        [Test]
        public void FloatAssignment_FromEarlierDeclaration_GetsDotZero()
        {
            AssertPatch(
                "float remap;\nvoid main() {\n    remap = 3;\n}\n",
                "float remap;\nvoid main() {\n    remap = 3.0;\n}\n");
        }

        [Test]
        public void ArithmeticWithFloatOperand_GetsDotZero()
        {
            AssertPatch(
                "float gain;\nvoid main() {\n    gain = gain * 2;\n}\n",
                "float gain;\nvoid main() {\n    gain = gain * 2.0;\n}\n");
        }

        [Test]
        public void GenericBuiltin_AnyFloatSibling_PatchesAllIntegers()
        {
            AssertPatch(
                "varying float v;\nvoid main() {\n    float x = clamp(v, 0, 1);\n}\n",
                "varying float v;\nvoid main() {\n    float x = clamp(v, 0.0, 1.0);\n}\n");
        }

        [Test]
        public void GenericBuiltin_WithNoFloatSibling_Stays()
        {
            // 全整型实参的 min 在 ES 里是合法重载，不能动
            AssertUnchanged("void main() {\n    int m = min(0, 1);\n}\n");
        }

        [Test]
        public void LodSampler_ThirdArgument_IsFloat()
        {
            AssertPatch(
                "uniform sampler2D t;\nvarying vec2 uv;\nvoid main() {\n    vec4 c = texture2DLod(t, uv, 0);\n}\n",
                "uniform sampler2D t;\nvarying vec2 uv;\nvoid main() {\n    vec4 c = texture2DLod(t, uv, 0.0);\n}\n");
        }

        [Test]
        public void UserFunction_FloatParameter_PatchesIntArgument()
        {
            AssertPatch(
                "float remap_x(float volume, int mode) {\n    return volume;\n}\n" +
                "void main() {\n    float r = remap_x(1, 2);\n}\n",
                "float remap_x(float volume, int mode) {\n    return volume;\n}\n" +
                "void main() {\n    float r = remap_x(1.0, 2);\n}\n");
        }

        [Test]
        public void UserFunction_UnknownReturnType_DoesNotLeakIntoDeclaration()
        {
            // 类型读不出来就留原样：这是"少改不会更错"那条取舍的正面用例
            AssertUnchanged("void main() {\n    float v = unknown_fn(4);\n}\n");
        }

        // ---- 必须不动的上下文 ----

        [Test]
        public void IntDeclaration_And_ArraySize_StaysInteger()
        {
            AssertUnchanged("int mode = 2;\nvec4 samples[4];\n");
        }

        [Test]
        public void ForLoopWithIntCounter_Stays()
        {
            AssertUnchanged("void main() {\n    for (int i = 0; i < 10; i++) { }\n}\n");
        }

        [Test]
        public void ConstructorArguments_Stay()
        {
            // 真机 A/B 证过 ES 收整型实参，WE 自己的移动导出也没动这里
            AssertUnchanged("void main() {\n    vec3 c = vec3(0, 1, 2);\n    gl_FragColor = vec4(0);\n}\n");
        }

        [Test]
        public void Comments_And_Preprocessor_Stays()
        {
            AssertUnchanged(
                "// float x = 2;\n" +
                "#define LIMIT 4\n" +
                "/* int y = 7; */\n" +
                "float z = 1.5;   // 3\n");
        }

        [Test]
        public void AlreadyFloat_NumericForms_Stay()
        {
            AssertUnchanged(
                "float a = 1e-6;\nfloat b = 1.5;\nfloat c = .5;\nfloat d = 2.0;\nfloat e = 10e2;\n");
        }

        [Test]
        public void ArraySubscript_Stays()
        {
            AssertUnchanged("int w[8];\nvoid main() {\n    int a = w[2];\n}\n");
        }

        // ---- 结构不变式 ----

        [Test]
        public void CarriageReturns_ArePreserved()
        {
            const string source = "void main() {\r\n    float volume = 2;\r\n}\r\n";
            var r = ShaderCompatPatcher.Patch(source);
            Assert.AreEqual("void main() {\r\n    float volume = 2.0;\r\n}\r\n", r.Text);
            Assert.AreEqual(Count(source, "\r\n"), Count(r.Text, "\r\n"), "行尾不能被规范化");
        }

        [Test]
        public void MixedLineEndings_ArePreservedPerLine()
        {
            const string source = "float a;\r\nfloat b = 1;\nfloat c = 2;\r\n";
            var r = ShaderCompatPatcher.Patch(source);
            Assert.AreEqual("float a;\r\nfloat b = 1.0;\nfloat c = 2.0;\r\n", r.Text);
        }

        [Test]
        public void Counters_SplitLinesAndLiterals()
        {
            var r = ShaderCompatPatcher.Patch("varying float b;\nvoid main() {\n    float a = clamp(b, 0, 1);\n}\n");
            Assert.AreEqual(1, r.LinesChanged, "一行两个实参也只算一行");
            Assert.AreEqual(2, r.LiteralsChanged);
        }

        [Test]
        public void PathFilter_OnlyCoversFragAndVert()
        {
            Assert.IsTrue(ShaderCompatPatcher.IsShaderPath("shaders/post.frag"));
            Assert.IsTrue(ShaderCompatPatcher.IsShaderPath("SHADER/BLEED.VERT"));
            Assert.IsFalse(ShaderCompatPatcher.IsShaderPath("shaders/common.glsl"));
            Assert.IsFalse(ShaderCompatPatcher.IsShaderPath("scene.json"));
            Assert.IsFalse(ShaderCompatPatcher.IsShaderPath(null));
        }

        // ---- 转换器接线 ----

        [Test]
        public void TestConvert_ShaderCompat_PatchesAndReports()
        {
            var pkg = WritePackage("scene.pkg",
                ("shaders/audio.frag", Frag("float volume = 2;\n")),
                ("shaders/clean.vert", Frag("float keep = 1.5;\n")));
            var target = Path.Combine(_dir, "out", "scene.mpkg");
            Directory.CreateDirectory(Path.GetDirectoryName(target));

            var report = new MobilePackageConverter().Convert(pkg, target, new MobilePackageOptions());

            Assert.AreEqual(1, report.ShadersRewritten);
            Assert.AreEqual(1, report.ShaderLiterals);
            // 例行改写不是错误：Warnings 必须空着，否则一次成功转化会在前端计入 ErrorCount
            Assert.AreEqual(0, report.Warnings.Count);
            Assert.AreEqual(1, report.Rewrites.Count);
            StringAssert.Contains("1 行/1 处", report.Rewrites[0]);
            var entries = ReadEntries(target);
            Assert.AreEqual("float volume = 2.0;\n", entries["shaders/audio.frag"]);
            Assert.AreEqual("float keep = 1.5;\n", entries["shaders/clean.vert"], "无需改写的着色器一个字节都不能动");
        }

        [Test]
        public void TestConvert_ShaderCompatOff_LeavesShadersByteIdentical()
        {
            var body = "float volume = 2;\n";
            var pkg = WritePackage("scene.pkg", ("shaders/audio.frag", Frag(body)));
            var target = Path.Combine(_dir, "out", "scene.mpkg");
            Directory.CreateDirectory(Path.GetDirectoryName(target));

            var report = new MobilePackageConverter().Convert(
                pkg, target, new MobilePackageOptions {ShaderCompat = false});

            Assert.AreEqual(0, report.ShadersRewritten);
            Assert.AreEqual(0, report.Warnings.Count);
            Assert.AreEqual(body, ReadEntries(target)["shaders/audio.frag"]);
        }

        [Test]
        public void TestConvert_NonShaderSource_IsNotRewritten()
        {
            var pkg = WritePackage("scene.pkg",
                ("shaders/shared.glsl", Frag("float volume = 2;\n")),
                ("shaders/notes.txt", Frag("float volume = 2;\n")));
            var target = Path.Combine(_dir, "out", "scene.mpkg");
            Directory.CreateDirectory(Path.GetDirectoryName(target));

            var report = new MobilePackageConverter().Convert(pkg, target, new MobilePackageOptions());

            Assert.AreEqual(0, report.ShadersRewritten);
            var entries = ReadEntries(target);
            Assert.AreEqual("float volume = 2;\n", entries["shaders/shared.glsl"]);
            Assert.AreEqual("float volume = 2;\n", entries["shaders/notes.txt"]);
        }

        /// <summary>
        /// 真机验过的那张壁纸（蕾塞 3577990983）就是规则集的账本：它的全部 .frag/.vert 改完之后必须是
        /// "7 个文件 / 51 行 / 68 处"，这一格与 S10 那次真机判读是同一个数字。语料不在本机就跳过。
        /// </summary>
        [Test]
        public void RealWallpaper_CorpusTotals_MatchDeviceValidatedCounts()
        {
            var file = Environment.GetEnvironmentVariable("REPKG_SHADER_CORPUS")
                ?? @"D:/mpkg/3577990983.pkg";
            if (!File.Exists(file))
            {
                Assert.Ignore($"缺少测试语料 {file}（真机验过的那张壁纸的 PC 包，放进来即启用）");
                return;
            }

            Package package;
            using (var stream = File.OpenRead(file))
            using (var reader = new BinaryReader(stream, Encoding.UTF8, true))
                package = new PackageReader().ReadFrom(reader);

            var files = 0;
            var lines = 0;
            var literals = 0;
            foreach (var e in package.Entries)
            {
                if (!ShaderCompatPatcher.IsShaderPath(e.FullPath)) continue;
                var r = ShaderCompatPatcher.Patch(Encoding.UTF8.GetString(e.Bytes));
                AssertInsertOnly(Encoding.UTF8.GetString(e.Bytes), r.Text, r.LiteralsChanged);
                AssertIdempotent(r.Text);
                if (!r.Changed) continue;
                files++;
                lines += r.LinesChanged;
                literals += r.LiteralsChanged;
            }

            Assert.AreEqual(7, files, "改到几个着色器文件");
            Assert.AreEqual(51, lines, "改到几行");
            Assert.AreEqual(68, literals, "补了几个字面量");
        }

        /// <summary>放弃改写才是错误：那条必须进 Warnings（前端计入 ErrorCount），并且一个字节都不写出去。</summary>
        [Test]
        public void TestConvert_CompatRefusedByBadUtf8_GoesToWarningsNotRewrites()
        {
            var raw = new byte[14];
            Encoding.UTF8.GetBytes("float v = 2;").CopyTo(raw, 0);
            raw[12] = 0xFF;                       // 非法 UTF-8：往返会多出字节，长度差不再等于 2×字面量数
            raw[13] = (byte) '\n';

            var pkg = WritePackage("scene.pkg", ("shaders/bad.frag", raw));
            var target = Path.Combine(_dir, "out", "scene.mpkg");
            Directory.CreateDirectory(Path.GetDirectoryName(target));

            var report = new MobilePackageConverter().Convert(pkg, target, new MobilePackageOptions());

            Assert.AreEqual(0, report.ShadersRewritten);
            Assert.AreEqual(0, report.Rewrites.Count);
            Assert.AreEqual(1, report.Warnings.Count);
            StringAssert.Contains("放弃兼容改写", report.Warnings[0]);
            Assert.AreEqual(raw, ReadEntryBytes(target, "shaders/bad.frag"), "放弃改写就必须把原字节原样写出去");
        }

        private static byte[] Frag(string body) => Encoding.UTF8.GetBytes(body);

        private string WritePackage(string name, params (string Path, byte[] Bytes)[] entries)
        {
            var package = new Package {Magic = "PKGV0023"};
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

        /// <summary>按条目名取回文本载荷。</summary>
        private static System.Collections.Generic.Dictionary<string, string> ReadEntries(string file)
        {
            var map = new System.Collections.Generic.Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var e in ReadRaw(file).Entries)
                map[e.FullPath] = Encoding.UTF8.GetString(e.Bytes);
            return map;
        }

        private static byte[] ReadEntryBytes(string file, string name)
        {
            foreach (var e in ReadRaw(file).Entries)
                if (e.FullPath == name) return e.Bytes;
            Assert.Fail($"输出包里没有 {name}");
            return null;
        }

        private static Package ReadRaw(string file)
        {
            using var stream = File.OpenRead(file);
            using var reader = new BinaryReader(stream, Encoding.UTF8, true);
            return new PackageReader().ReadFrom(reader);
        }

        private static int Count(string haystack, string needle)
        {
            var n = 0;
            for (var i = haystack.IndexOf(needle, StringComparison.Ordinal);
                 i >= 0;
                 i = haystack.IndexOf(needle, i + 1, StringComparison.Ordinal))
                n++;
            return n;
        }
    }
}
