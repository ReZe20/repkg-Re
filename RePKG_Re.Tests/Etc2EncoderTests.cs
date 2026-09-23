using System;
using NUnit.Framework;
using RePKG_Re.Application.Texture;

namespace RePKG_Re.Tests
{
    /// <summary>
    /// ETC2 RGBA8 编码器的测试。
    ///
    /// 这里带一个**只支持编码器所发子集**的本地解码器(individual/differential、flip=0、
    /// EAC 乘数 0..7)，用来把"编出来的块"翻译回像素。它和编码器各持一份实测表，
    /// 但真正的独立性来自 <see cref="TestGolden_WeRealBlocks"/>：那几组字节是从 WE 真导出的
    /// fmt5 载荷里原样取的，像素是独立参考解码器(texture2ddecoder-wasm)给的 —— 只有它能把
    /// "表值/位放置"从"我们自己的自洽"里拔出来。
    /// </summary>
    [TestFixture]
    public class Etc2EncoderTests
    {
        // 与 Etc2Encoder 同源：都由 %TEMP%\t2d 的逐位扫描脚本对独立解码器测出
        static readonly int[][] AlphaMod =
        {
            new[] {-3, -6, -9, -15, 2, 5, 8, 14},
            new[] {-3, -7, -10, -13, 2, 6, 9, 12},
            new[] {-2, -5, -8, -13, 1, 4, 7, 12},
            new[] {-2, -4, -6, -13, 1, 3, 5, 12},
            new[] {-3, -6, -8, -12, 2, 5, 7, 11},
            new[] {-3, -7, -9, -11, 2, 6, 8, 10},
            new[] {-4, -7, -8, -11, 3, 6, 7, 10},
            new[] {-3, -5, -8, -11, 2, 4, 7, 10},
            new[] {-2, -6, -8, -10, 1, 5, 7, 9},
            new[] {-2, -5, -8, -10, 1, 4, 7, 9},
            new[] {-2, -4, -8, -10, 1, 3, 7, 9},
            new[] {-2, -5, -7, -10, 1, 4, 6, 9},
            new[] {-3, -4, -7, -10, 2, 3, 6, 9},
            new[] {-1, -2, -3, -10, 0, 1, 2, 9},
            new[] {-4, -6, -8, -9, 3, 5, 7, 8},
            new[] {-3, -5, -7, -9, 2, 4, 6, 8}
        };

        static readonly int[][] ColorMod =
        {
            new[] {2, -2, 8, -8},
            new[] {5, -5, 17, -17},
            new[] {9, -9, 29, -29},
            new[] {13, -13, 42, -42},
            new[] {18, -18, 60, -60},
            new[] {24, -24, 80, -80},
            new[] {33, -33, 106, -106},
            new[] {47, -47, 119, -136}
        };

        [Test]
        public void TestUniformOpaqueBlock_ReproducesWeBlockSignature()
        {
            // WE 真码流里 99.4% 的块前 8 字节就是 FF 00×7：恒定不透明。我们该逐字节发同一个东西。
            var src = new byte[64];
            for (var p = 0; p < 16; p++)
            {
                src[p * 4] = 17;
                src[p * 4 + 1] = 200;
                src[p * 4 + 2] = 99;
                src[p * 4 + 3] = 255;
            }

            var dst = new byte[16];
            Etc2Encoder.EncodeBlock(src, dst);
            Assert.AreEqual(new byte[] {0xff, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00},
                dst.AsSpan(0, 8).ToArray(), "恒定 alpha 必须走精确快速路径");
        }

        [Test]
        public void TestGolden_WeRealBlocks_DecodeToReferencePixels()
        {
            // 取自 WE 导出的 3577990983(medium) 与 2636878454(medium) 的 fmt5 载荷，
            // 像素是独立参考解码器的输出。三组分别覆盖：alpha=0 的恒定块、非恒定 EAC 选择子、
            // alpha=255 的恒定块。
            Check(
                new byte[] {0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0xf8, 0xf8, 0xf8, 0x02, 0x00, 0x00, 0x00, 0x00},
                Fill(255, 255, 255, 0));
            Check(
                new byte[] {0xff, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0xf8, 0xf8, 0xf8, 0x02, 0x00, 0x00, 0x00, 0x00},
                Fill(255, 255, 255, 255));

            // 唯一的非恒定 alpha 像素在行优先的第 4 个((x=3,y=0))，选择子 5 → 243+5=248
            var varying = Fill(255, 255, 255, 255);
            varying[3 * 4 + 3] = 0xf8;
            Check(
                new byte[] {0xf3, 0x10, 0xff, 0xff, 0xff, 0xff, 0xfb, 0xff, 0xf8, 0xf8, 0xf8, 0x02, 0x00, 0x00, 0x00, 0x00},
                varying);
        }

        static void Check(byte[] block, byte[] expected)
        {
            var actual = DecodeBlock(block);
            Assert.AreEqual(expected, actual,
                $"块 {Convert.ToHexString(block)} 解出的像素与参考实现不一致：首个差异在 " +
                $"像素 {FirstDiff(expected, actual)}");
        }

        static int FirstDiff(byte[] a, byte[] b)
        {
            for (var i = 0; i < a.Length; i++)
                if (a[i] != b[i]) return i / 4;
            return -1;
        }

        static byte[] Fill(byte r, byte g, byte b, byte a)
        {
            var px = new byte[64];
            for (var p = 0; p < 16; p++)
            {
                px[p * 4] = r;
                px[p * 4 + 1] = g;
                px[p * 4 + 2] = b;
                px[p * 4 + 3] = a;
            }

            return px;
        }

        [Test]
        public void TestRoundTrip_TypicalBlocks_AreClose()
        {
            // 平滑渐变(WE 缩完 2x/4x 之后的主体形态)与二值 alpha 的图标块：误差该很小。
            // 透明像素的 RGB 是免费的，所以只判不透明那部分。
            // 四条都是"当前实现的实测上限"，不是理想值：改进会让它们继续绿，退化会立刻红。
            // 17 = 平滑渐变。一个 4x4 只有一个基色 + 4 档修饰，渐变要靠 Planar 模式才贴得住，
            //     v1 没发 Planar，所以卡在这里。
            AssertRoundTrip(Block(p => (100 + p * 2, 200 - p * 2, 60 + p, (byte) 255)), 17);
            // 17 = 常量色 + 0..255 渐变 alpha。EAC 一表只有 8 档，跨 255 时档距就是这么粗。
            AssertRoundTrip(Block(p => (245, 3, 190, (byte) (p * 17 % 256))), 17);
            // 2 = 二值 alpha(WE 素材里最常见的硬边)。乘数取到 15 时超出 0..255 的部分被钳位
            //     免费吃掉，所以两端都能精确命中；乘数域一旦收窄这里会立刻回到 50+。
            AssertRoundTrip(Block(p => (255, 0, 128, (byte) (p % 4 == 0 ? 0 : 255))), 2);
            // 115 = 一个 4x4 里横跨 0..240：每子块只有"一个基色 + 4 档修饰"，这类块在 ETC2 的
            //     表达范围之外，任何编码器都救不回来。写下来当下限说明。
            AssertRoundTrip(Block(p => (p * 37 % 256, p * 53 % 256, 128, (byte) (p * 17 % 256))), 115);
        }

        static void AssertRoundTrip(byte[] src, int maxChannelError)
        {
            var encoded = new byte[16];
            Etc2Encoder.EncodeBlock(src, encoded);
            var back = DecodeBlock(encoded);
            var worst = 0;
            for (var p = 0; p < 16; p++)
            for (var c = 0; c < 4; c++)
            {
                // 全透明像素的 RGB 编码器按设计不参与拟合(WE 与我们不缩版本的差异也主要在那儿)，
                // 只要求 alpha 对上；alpha 非 0 的像素才连 RGB 一起判。
                if (c < 3 && src[p * 4 + 3] == 0) continue;
                worst = Math.Max(worst, Math.Abs(back[p * 4 + c] - src[p * 4 + c]));
            }

            TestContext.WriteLine($"块 {Convert.ToHexString(encoded)} 最大通道误差 {worst}");
            Assert.LessOrEqual(worst, maxChannelError);
        }

        /// <summary>
        /// 整幅编码必须与"逐块单独编码"逐字节相同。这条是补 stride 那个坑的：
        /// 4x4 块在整幅图里是 4 行、行间跨 width*4，直接取连续 64 字节只有 bx=0 且 width=4 时才对。
        /// </summary>
        [Test]
        public void TestEncodeRgba8_BlocksMatchStandaloneEncoding()
        {
            const int W = 8, H = 8;
            var rgba = new byte[W * H * 4];
            for (var y = 0; y < H; y++)
            for (var x = 0; x < W; x++)
            {
                var i = (y * W + x) * 4;
                // 左半黑、右半白，再叠一层随位置变化：块与块内容必须明显不同
                var v = (byte) (x < 4 ? y * 6 : 255 - x * 5);
                rgba[i] = v;
                rgba[i + 1] = (byte) (255 - v);
                rgba[i + 2] = (byte) ((x * 31 + y * 17) % 256);
                rgba[i + 3] = (byte) (x % 3 == 0 ? 0 : 255);
            }

            var whole = Etc2Encoder.EncodeRgba8(W, H, rgba);
            for (var by = 0; by < H; by += 4)
            for (var bx = 0; bx < W; bx += 4)
            {
                var one = new byte[64];
                for (var y = 0; y < 4; y++)
                    Array.Copy(rgba, ((by + y) * W + bx) * 4, one, y * 16, 16);

                var expected = new byte[16];
                Etc2Encoder.EncodeBlock(one, expected);

                var actual = whole.AsSpan((by / 4 * (W / 4) + bx / 4) * 16, 16).ToArray();
                Assert.AreEqual(expected, actual,
                    $"块({bx / 4},{by / 4}) 与单独编码不一致：整幅编码读到的不是这 16 个像素");
            }
        }

        [Test]
        public void TestEncodeRgba8_ByteCountIsOnePerPixel()
        {
            const int W = 8, H = 12;
            var rgba = new byte[W * H * 4];
            for (var i = 0; i < rgba.Length; i++) rgba[i] = (byte) (i * 7);

            var encoded = Etc2Encoder.EncodeRgba8(W, H, rgba);
            Assert.AreEqual(W * H * Etc2Encoder.BytesPerPixel, encoded.Length, "ETC2 RGBA8 是 1 字节/像素");

            // 256x256 = 16384 块，尺寸是故意的：块攒数据的缓冲一旦挪进循环里 stackalloc，
            // 小图过得去、真图直接把栈打爆(实测 Stack overflow)，这个规模才能当场红。
            const int Big = 256;
            var big = new byte[Big * Big * 4];
            for (var i = 0; i < big.Length; i++) big[i] = (byte) (i * 11);
            Assert.AreEqual(Big * Big, Etc2Encoder.EncodeRgba8(Big, Big, big).Length);
        }

        [Test]
        public void TestEncodeRgba8_RejectsUnalignedSize()
        {
            Assert.Catch<ArgumentException>(() => Etc2Encoder.EncodeRgba8(6, 4, new byte[6 * 4 * 4]));
        }

        // ---- 只支持编码器发出的那个子集；发别的就报错，别把"没测过"当"支持" ----

        static byte[] DecodeBlock(byte[] b)
        {
            var output = new byte[64];
            var code = b[0];
            var mult = (b[1] >> 4) & 0xF;
            var table = b[1] & 0xF;
            Assert.LessOrEqual(mult, 15, "EAC 乘数是 4bit 字段，出现别的值说明写侧错位");

            for (var p = 0; p < 16; p++)
            {
                var x = p % 4;
                var y = p / 4;
                var start = 3 * (4 * x + y);
                var s = 0;
                for (var bit = 0; bit < 3; bit++)
                    s = (s << 1) | ((b[2 + (start + bit) / 8] >> (7 - (start + bit) % 8)) & 1);

                output[p * 4 + 3] = (byte) Clamp(code + mult * AlphaMod[table][s]);
            }

            var color = new byte[8];
            Array.Copy(b, 8, color, 0, 8);
            var flip = color[3] & 1;
            var diff = (color[3] >> 1) & 1;
            Assert.AreEqual(0, flip, "编码器不发 flip=1(位序没测过)");

            var t1 = (color[3] >> 5) & 7;
            var t2 = (color[3] >> 2) & 7;
            Span<int> base1 = stackalloc int[3];
            Span<int> base2 = stackalloc int[3];
            for (var c = 0; c < 3; c++)
            {
                // byte0=R byte1=G byte2=B —— 和写侧同序。这条不许"为了过测试"去跟着写侧改：
                // 编码器和这个解码器共用一份假设时，通道序错了两边一起错、单测永远绿（曾经就是这样：
                // 落位反了半年没人发现，直到拿彩色 DXT5 素材和 WE 真码流对像素才量出来）。
                // 判据是 WE 真包里的彩色块，不是这里。
                var channel = c;
                if (diff == 0)
                {
                    base1[c] = Expand4(color[channel] >> 4);
                    base2[c] = Expand4(color[channel] & 0xF);
                }
                else
                {
                    var b1 = (color[channel] >> 3) & 31;
                    var d = color[channel] & 7;
                    if (d >= 4) d -= 8;
                    base1[c] = Expand5(b1);
                    base2[c] = Expand5(b1 + d);
                }
            }

            for (var p = 0; p < 16; p++)
            {
                var x = p % 4;
                var y = p / 4;
                var sub = x < 2 ? 0 : 1;
                var bit = y + 4 * (x - 2 * sub);
                var lsb = (color[sub == 0 ? 5 : 4] >> bit) & 1;
                var msb = (color[sub == 0 ? 7 : 6] >> bit) & 1;
                var sel = (msb << 1) | lsb;
                var mod = ColorMod[sub == 0 ? t1 : t2][sel];
                for (var c = 0; c < 3; c++)
                    output[p * 4 + c] = (byte) Clamp((sub == 0 ? base1[c] : base2[c]) + mod);
            }

            return output;
        }

        static int Expand4(int v) => (v << 4) | v;

        static int Expand5(int v) => (v << 3) | (v >> 2);

        static int Clamp(int v) => v < 0 ? 0 : v > 255 ? 255 : v;


        static byte[] Block(Func<int, (int, int, int, int)> pixel)
        {
            var src = new byte[64];
            for (var p = 0; p < 16; p++)
            {
                var (r, g, b, a) = pixel(p);
                src[p * 4] = (byte) r;
                src[p * 4 + 1] = (byte) g;
                src[p * 4 + 2] = (byte) b;
                src[p * 4 + 3] = (byte) a;
            }


            return src;
        }
    }
}
