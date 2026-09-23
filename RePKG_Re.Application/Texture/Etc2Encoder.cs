using System;

namespace RePKG_Re.Application.Texture
{
    /// <summary>
    /// ETC2 RGBA8(fmt5)编码器：16 字节/4x4 块 = 8 字节 EAC alpha + 8 字节 ETC2 color。
    ///
    /// 【位布局是测出来的，不是照抄规范的】下面每个常量与每处位位置都由 `%TEMP%\t2d\` 里的
    /// alpha-table.mjs / color-probe.mjs / color-probe2.mjs 逐位扫描一个独立参考解码器
    /// (texture2ddecoder-wasm)得到。规范记在脑子里会错——另一个 repkg 分支就栽在 byte3 的
    /// 位偏移和选择子转置上(症状：与参考实现相关性只有 0.796)。测得的事实：
    ///   alpha：byte0=codeword，byte1=[7:4]乘数 [3:0]表号，bytes2..7=48bit、每像素 3bit；
    ///          选择子序号 k 与像素的关系是 k = 4*x + y(列优先)，k=0 在最高位段；
    ///          最终 alpha = clamp(codeword + 乘数 × AlphaModifier[表][选择子])，乘数线性缩放实测成立。
    ///   color：bytes0..2 依次是 B/G/R；individual 下高 4 位=子块1、低 4 位=子块2(expand4)；
    ///          byte3 = [7:5]子块1表号 [4:2]子块2表号 [1]diff [0]flip；
    ///          差分下 base1 占 [7:3](5bit, expand5)、子块2 的增量占 [2:0](3bit 符号扩展)；
    ///          选择子每像素 2bit，LSB 面在 byte5(子块1)/byte4(子块2)，MSB 面在 byte7/byte6，
    ///          面内 bit = y + 4*(x - 子块左边界)。
    ///
    /// 【本版故意不发的东西，别当成已知】
    ///   - flip=1(横切子块)：位序只在 flip=0 下测过。WE 自己的块里 flip=1 极罕见(百万块里 191 个)。
    ///   - T/H/Planar：与 individual/diff 同体积(都是 8 字节/块)，只影响渐变画质；
    ///     缺它的直接后果实测是"平滑渐变块最大通道误差 17"，见 Etc2EncoderTests。
    /// </summary>
    public static class Etc2Encoder
    {
        public const int BlockByteCount = 16;

        /// <summary>ETC2 RGBA8 = 1 字节/像素。TEX 的 DecompressedBytesCount 按这个写，不是 w*h*4。</summary>
        public const int BytesPerPixel = 1;

        /// <summary>
        /// 乘数搜索上限 15。参考解码器把这个字段按无符号 0..15 单调处理，而规范写的是 4bit 有符号 ——
        /// 这条分歧用 WE 的真码流判掉的：它 42752 块(2636878454 的 7d2ONWk，金标准与真机都认过)里
        /// 有 8.1% 用了 8..15，而 WE 自己的导出在手机上正常 ⇒ 设备认这套读法。压到 0..7 的代价实测是
        /// 二值 alpha 块最大通道误差 86(可达域只有 ±105，够不到 0/255 两端)。
        /// </summary>
        const int MaxAlphaMultiplier = 15;

        /// <summary>16 张表 × 8 个选择子在乘数=1 时的实测偏移；真实偏移 = 乘数 × 本值。</summary>
        static readonly int[][] AlphaModifier =
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

        /// <summary>8 张表 × 4 个选择子值(选择子 = MSB&lt;&lt;1|LSB)的实测偏移。</summary>
        static readonly int[][] ColorModifier =
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

        static int Clamp(int v, int lo, int hi) => v < lo ? lo : v > hi ? hi : v;

        static int Clamp255(int v) => Clamp(v, 0, 255);

        static int Expand4(int v) => (v << 4) | v;

        static int Expand5(int v) => (v << 3) | (v >> 2);

        static int Quantize4(int v) => (Clamp255(v) * 15 + 127) / 255;

        /// <summary>必须四舍五入：截断会给每个差分基色留下约半个台阶(≈4/255)的系统性偏暗。</summary>
        static int Quantize5(int v) => (Clamp255(v) * 31 + 127) / 255;

        /// <summary>
        /// 整幅编码。宽高必须是 4 的倍数——缩小公式 `ceil4(floor(÷N))` 天然满足，不缩的路径不走这里。
        /// 输入是直色 RGBA8、行优先(与物化器写像素的口径一致)。
        /// </summary>
        public static byte[] EncodeRgba8(int width, int height, byte[] rgba)
        {
            if (rgba == null) throw new ArgumentNullException(nameof(rgba));
            if (width <= 0 || height <= 0 || width % 4 != 0 || height % 4 != 0)
                throw new ArgumentException($"ETC2 需要 4 的倍数尺寸，当前 {width}x{height}");
            if (rgba.Length != width * height * 4)
                throw new ArgumentException($"RGBA8 长度 {rgba.Length} != {width}x{height}x4");

            var columns = width / 4;
            var output = new byte[columns * (height / 4) * BlockByteCount];
            var stride = width * 4;

            // 攒块用的缓冲必须在循环外分配一次：stackalloc 的内存要到方法返回才回收，
            // 放在循环里 = 每块再要 64 字节栈，一张 4 万块的图直接把栈打爆(实测 Stack overflow)。
            Span<byte> block = stackalloc byte[64];

            for (var by = 0; by < height; by += 4)
            for (var bx = 0; bx < width; bx += 4)
            {
                // 一个 4x4 块在整幅图里是"4 行、每行 16 字节、行间跨 width*4"，不是连续的 64 字节。
                // 直接 AsSpan(起点) 取 64 字节会把后面几行的开头像素串进来(错列的剪切窗口)：
                // 只有 bx=0 且 width=4 时恰好正确，所以单块往返测试全绿、整幅图却糊成马赛克。
                for (var y = 0; y < 4; y++)
                    rgba.AsSpan((by + y) * stride + bx * 4, 16).CopyTo(block.Slice(y * 16, 16));

                EncodeBlock(block, output.AsSpan((by / 4 * columns + bx / 4) * BlockByteCount, BlockByteCount));
            }

            return output;
        }

        /// <summary>编一个 4x4 块。src 行优先 16 像素 RGBA8(64 字节)，dst 为 16 字节。</summary>
        public static void EncodeBlock(ReadOnlySpan<byte> src, Span<byte> dst)
        {
            if (src.Length < 64) throw new ArgumentException("块输入需要 16 像素 RGBA8", nameof(src));
            if (dst.Length < BlockByteCount) throw new ArgumentException("块输出需要 16 字节", nameof(dst));

            EncodeAlpha(src, dst);
            EncodeColor(src, dst.Slice(8));
        }

        // ---- alpha：EAC ----

        static void EncodeAlpha(ReadOnlySpan<byte> src, Span<byte> dst)
        {
            int min = 255, max = 0, sum = 0;
            for (var p = 0; p < 16; p++)
            {
                var a = src[p * 4 + 3];
                if (a < min) min = a;
                if (a > max) max = a;
                sum += a;
            }

            // 恒定 alpha 是绝对多数(WE 真码流里 99.4% 的块就是 FF 00×7)，单列一条精确快速路径
            if (min == max)
            {
                dst[0] = (byte) min;
                dst[1] = 0;
                dst.Slice(2, 6).Clear();
                return;
            }

            Span<byte> sel = stackalloc byte[16];
            Span<byte> bestSel = stackalloc byte[16];
            var bestScore = long.MaxValue;
            var bestCode = (sum + 8) / 16;
            var bestTable = 0;
            var bestMult = 1;
            var range = max - min;

            // 乘数全搜 16 档 × 16 张表太贵，所以按"该表乘数=1 能覆盖多大跨度"估出临界值 n，
            // 只试 {n, n+1, 2n+1, 15}。最后那档 15 不能省：alpha 只要跨到 0/255 两端，
            // 超出范围的部分会被钳位"免费"吃掉，于是大乘数反而能精确命中二值 alpha。
            Span<int> mults = stackalloc int[4];
            for (var table = 0; table < AlphaModifier.Length; table++)
            {
                var row = AlphaModifier[table];
                var lo = 255;
                var hi = -255;
                for (var s = 0; s < 8; s++)
                {
                    if (row[s] < lo) lo = row[s];
                    if (row[s] > hi) hi = row[s];
                }

                var reach = hi - lo;
                if (reach <= 0) continue;
                var n = Math.Max(1, (range + reach - 1) / reach);
                var count = 0;
                for (var i = 0; i < 4; i++)
                {
                    var candidate = i == 0 ? n : i == 1 ? n + 1 : i == 2 ? 2 * n + 1 : MaxAlphaMultiplier;
                    candidate = Clamp(candidate, 1, MaxAlphaMultiplier);
                    if (mults.Slice(0, count).IndexOf(candidate) >= 0) continue;
                    mults[count++] = candidate;
                }

                for (var mi = 0; mi < count; mi++)
                {
                    var mult = mults[mi];
                    // 先按当前码字定选择子，再用选好的偏移反解码字，来回两轮就够收敛
                    var code = Clamp255((min + max) / 2);
                    var score = 0L;
                    for (var iter = 0; iter < 2; iter++)
                    {
                        score = 0;
                        var reflow = 0;
                        for (var p = 0; p < 16; p++)
                        {
                            var a = src[p * 4 + 3];
                            var local = 0;
                            var localErr = int.MaxValue;
                            for (var s = 0; s < 8; s++)
                            {
                                var d = Clamp255(code + mult * row[s]) - a;
                                var e = d * d;
                                if (e >= localErr) continue;
                                localErr = e;
                                local = s;
                            }

                            sel[p] = (byte) local;
                            score += localErr;
                            reflow += a - mult * row[local];
                        }

                        var fitted = Clamp(reflow / 16, 0, 255);
                        if (fitted == code) break;
                        code = fitted;
                    }

                    if (score >= bestScore) continue;
                    bestScore = score;
                    bestCode = code;
                    bestTable = table;
                    bestMult = mult;
                    sel.CopyTo(bestSel);
                    if (score != 0) goto write;
                }
            }

            write:
            dst[0] = (byte) bestCode;
            dst[1] = (byte) ((bestMult << 4) | (bestTable & 0xF));
            dst.Slice(2, 6).Clear();
            // 把 bytes2..7 当成一个 48bit 大端域：选择子 k=0 占最高 3 位(即 byte2 的 bit7..5)，
            // 之后每个选择子往低位排 3 位。金标准块 f3 10 ff ff ff ff fb ff 就是这么反推出来的。
            for (var p = 0; p < 16; p++)
            {
                var x = p % 4;
                var y = p / 4;
                var start = 3 * (4 * x + y);                // 从最高位起算的偏移
                for (var b = 0; b < 3; b++)
                {
                    var t = start + b;
                    var bit = (bestSel[p] >> (2 - b)) & 1;  // 选择子的 MSB 先写
                    dst[2 + t / 8] |= (byte) (bit << (7 - t % 8));
                }
            }
        }

        // ---- color：individual + differential，flip 恒 0 ----

        static void EncodeColor(ReadOnlySpan<byte> src, Span<byte> dst)
        {
            Span<int> w = stackalloc int[16];
            for (var p = 0; p < 16; p++) w[p] = src[p * 4 + 3];

            Span<int> indQ1 = stackalloc int[3];
            Span<int> indQ2 = stackalloc int[3];
            Span<int> indT = stackalloc int[2];
            Span<byte> indSel = stackalloc byte[16];
            Span<int> difBase = stackalloc int[3];
            Span<int> difDelta = stackalloc int[3];
            Span<int> difT = stackalloc int[2];
            Span<byte> difSel = stackalloc byte[16];

            var indScore = FitIndividual(src, w, indQ1, indQ2, indT, indSel);
            var difScore = FitDifferential(src, w, difBase, difDelta, difT, difSel);

            // 通道落位是 R→字节 0、B→字节 2。反过来的话解出来红蓝整片互换 —— 这条是用
            // "同一张 DXT5 素材：WE 的 fmt5 vs 我方的 fmt5，都喂给同一个第三方解码器"量出来的：
            // WE 的与源图逐通道对得上（R/B 平均误差 1.3），我方旧落位差 15/16 而 G 只差 1.2，
            // 是教科书级的通道镜像签名。灰度素材（之前真机验过那张）R==B，这个错看不出来，
            // 所以必须由彩色的 背景.tex 来判。
            dst.Slice(0, 8).Clear();
            if (difScore <= indScore)
            {
                for (var c = 0; c < 3; c++)
                    dst[c] = (byte) (((difBase[c] & 31) << 3) | (difDelta[c] & 7));
                dst[3] = (byte) (0x02 | ((difT[0] & 7) << 5) | ((difT[1] & 7) << 2));
                WriteColorSelectors(difSel, dst);
            }
            else
            {
                for (var c = 0; c < 3; c++)
                    dst[c] = (byte) (((indQ1[c] & 15) << 4) | (indQ2[c] & 15));
                dst[3] = (byte) (((indT[0] & 7) << 5) | ((indT[1] & 7) << 2));
                WriteColorSelectors(indSel, dst);
            }
        }

        static void WriteColorSelectors(ReadOnlySpan<byte> sel, Span<byte> dst)
        {
            for (var p = 0; p < 16; p++)
            {
                var x = p % 4;
                var y = p / 4;
                var sub = x < 2 ? 0 : 1;
                var bit = y + 4 * (x - 2 * sub);
                dst[sub == 0 ? 5 : 4] |= (byte) ((sel[p] & 1) << bit);
                dst[sub == 0 ? 7 : 6] |= (byte) (((sel[p] >> 1) & 1) << bit);
            }
        }

        /// <summary>
        /// individual 模式：两个子块的基色与表号互不影响，所以按子块分开拟合、误差相加。
        /// 基色候选两档：按 alpha 加权的均值、极值中点；各自配 8 张表挑最省的。
        /// </summary>
        static long FitIndividual(
            ReadOnlySpan<byte> src, ReadOnlySpan<int> w, Span<int> q1, Span<int> q2, Span<int> table,
            Span<byte> sel)
        {
            var total = 0L;
            Span<int> target = stackalloc int[3];
            Span<int> base8 = stackalloc int[3];
            Span<int> q = stackalloc int[3];
            Span<int> bestQ = stackalloc int[3];
            Span<byte> candSel = stackalloc byte[16];

            for (var sub = 0; sub < 2; sub++)
            {
                var best = long.MaxValue;
                var bestTable = 0;
                for (var candidate = 0; candidate < 2; candidate++)
                {
                    SubBlockTarget(src, w, sub, candidate, target);
                    for (var c = 0; c < 3; c++)
                    {
                        q[c] = Quantize4(target[c]);
                        base8[c] = Expand4(q[c]);
                    }

                    var err = FitTables(src, w, sub, base8, candSel, out var t);
                    if (err >= best) continue;
                    best = err;
                    bestTable = t;
                    for (var c = 0; c < 3; c++) bestQ[c] = q[c];
                    for (var p = 0; p < 16; p++)
                        if ((p % 4 < 2 ? 0 : 1) == sub) sel[p] = candSel[p];
                }

                for (var c = 0; c < 3; c++) (sub == 0 ? q1 : q2)[c] = bestQ[c];
                table[sub] = bestTable;
                total += best;
            }

            return total;
        }

        /// <summary>
        /// 差分模式：base1 取子块1 的加权均值量化到 5bit，子块2 用 3bit 有符号增量表达(压进 -4..3)。
        /// 基色定下来之后，两个子块的表号依旧互相独立，所以还是分开拟合。
        /// </summary>
        static long FitDifferential(
            ReadOnlySpan<byte> src, ReadOnlySpan<int> w, Span<int> base1, Span<int> delta, Span<int> table,
            Span<byte> sel)
        {
            Span<int> t1 = stackalloc int[3];
            Span<int> t2 = stackalloc int[3];
            SubBlockTarget(src, w, 0, 0, t1);
            SubBlockTarget(src, w, 1, 0, t2);

            for (var c = 0; c < 3; c++)
            {
                base1[c] = Quantize5(t1[c]);
                delta[c] = Clamp(Quantize5(t2[c]) - base1[c], -4, 3);
                delta[c] = Clamp(delta[c], -base1[c], 31 - base1[c]);
            }

            Span<int> b1 = stackalloc int[3] {Expand5(base1[0]), Expand5(base1[1]), Expand5(base1[2])};
            Span<int> b2 = stackalloc int[3]
            {
                Expand5(base1[0] + delta[0]), Expand5(base1[1] + delta[1]), Expand5(base1[2] + delta[2])
            };

            Span<byte> candSel = stackalloc byte[16];
            var total = 0L;
            for (var sub = 0; sub < 2; sub++)
            {
                var err = FitTables(src, w, sub, sub == 0 ? b1 : b2, candSel, out var t);
                table[sub] = t;
                total += err;
                for (var p = 0; p < 16; p++)
                    if ((p % 4 < 2 ? 0 : 1) == sub) sel[p] = candSel[p];
            }

            return total;
        }

        /// <summary>子块的目标色：candidate 0 = 按 alpha 加权均值(全透明退回普通均值)，1 = 极值中点。</summary>
        static void SubBlockTarget(ReadOnlySpan<byte> src, ReadOnlySpan<int> w, int sub, int candidate, Span<int> outTarget)
        {
            // stackalloc 不清零(C# 只在带初始化器时才写值)：这三个累加器少一个 {0} 就会把栈残值
            // 当成像素累加进基色，症状是"全黑块的基色算出 136"——同输入同输出，看着完全确定，
            // 所以自洽的往返测试抓不到，只有拿独立解码器比像素才会露出来。
            Span<int> sum = stackalloc int[3];
            Span<int> wsum = stackalloc int[3];
            Span<int> mn = stackalloc int[3] {255, 255, 255};
            Span<int> mx = stackalloc int[3];
            sum.Clear();
            wsum.Clear();
            mx.Clear();
            var weight = 0;
            var count = 0;

            for (var p = 0; p < 16; p++)
            {
                if ((p % 4 < 2 ? 0 : 1) != sub) continue;
                count++;
                for (var c = 0; c < 3; c++)
                {
                    var v = src[p * 4 + c];
                    sum[c] += v;
                    wsum[c] += v * w[p];
                    if (v < mn[c]) mn[c] = v;
                    if (v > mx[c]) mx[c] = v;
                }

                weight += w[p];
            }

            for (var c = 0; c < 3; c++)
            {
                if (candidate == 1)
                {
                    outTarget[c] = (mn[c] + mx[c]) / 2;
                    continue;
                }

                outTarget[c] = weight > 0 ? Clamp255(wsum[c] / weight) : sum[c] / count;
            }
        }

        /// <summary>基色已定，只挑这个子块的表号与每像素选择子；base8 是 8bit 展开值。</summary>
        static long FitTables(
            ReadOnlySpan<byte> src, ReadOnlySpan<int> w, int sub, ReadOnlySpan<int> base8, Span<byte> sel,
            out int bestTable)
        {
            bestTable = 0;
            var best = long.MaxValue;
            Span<byte> pick = stackalloc byte[16];
            for (var t = 0; t < 8; t++)
            {
                var row = ColorModifier[t];
                var err = 0L;
                for (var p = 0; p < 16; p++)
                {
                    if ((p % 4 < 2 ? 0 : 1) != sub) continue;
                    var local = 0;
                    var localErr = int.MaxValue;
                    for (var s = 0; s < 4; s++)
                    {
                        var d0 = Clamp255(base8[0] + row[s]) - src[p * 4];
                        var d1 = Clamp255(base8[1] + row[s]) - src[p * 4 + 1];
                        var d2 = Clamp255(base8[2] + row[s]) - src[p * 4 + 2];
                        var e = d0 * d0 + d1 * d1 + d2 * d2;
                        if (e >= localErr) continue;
                        localErr = e;
                        local = s;
                    }

                    pick[p] = (byte) local;
                    err += (long) localErr * (w[p] > 0 ? w[p] : 1);
                }

                // 选择子必须和表号一起换：只记表号、留下最后一次尝试的选择子，解出来就是错色
                if (err >= best) continue;
                best = err;
                bestTable = t;
                pick.CopyTo(sel);
            }

            return best;
        }
    }
}
