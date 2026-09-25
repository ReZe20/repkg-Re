using System;
using RePKG_Re.Core.Texture;

namespace RePKG_Re.Application.Texture.Helpers
{
    /// <summary>
    /// BC1/BC2/BC3(DXT1/DXT3/DXT5)块压缩器,cluster-fit 单遍版。
    ///
    /// 存在的理由:pack 之前只能封 PNG/JPEG 直通 blob,4K 一张约 33MB,而 WE 主流形态 DXT5 是
    /// 8MB/张 —— 没有块编码器就追不上编辑器(README pack 节"已知取舍"点名的那件事)。
    ///
    /// 【与自家解码器(DXT.cs)互镜像的约定,决定回环能否逐像素闭合】
    ///  - 索引位打包一律 LSB-first:颜色 4 字节里像素 i 占 bit 2i..2i+1;BC3 alpha 每 3 字节
    ///    装 8 个 3bit 索引(解码侧 value &gt;&gt; (3*j));BC2 alpha 偶像素在低 4 位。
    ///  - BC1:DXT3/DXT5 的颜色子块一律 4 色形态(c0&gt;c1)。DXT1 下形态由解码器的 "c0&lt;=c1 → 3 色 +
    ///    索引 3 = 透明黑" 规则决定:全不透明块写 4 色(白赚一档中间色);含 alpha&lt;128 像素的块
    ///    必须写 3 色,并把半透明像素钉到索引 3、不透明像素只在 0..2 里选 —— 把不透明像素给成
    ///    索引 3 就是画面上的洞,把半透明像素给成颜色档就是把该透的地方画成实心块。
    ///  - BC3 alpha:恒写 a0=max、a1=min —— a0&gt;a1 时解码器走 7-entry 插值表(表[0]/[1] 即两端点原值,
    ///    0/255 只有本身是端点时才精确,这正是取 min/max 做端点的原因);min==max 时写 a0==a1
    ///    并只用索引 0。绝不制造"a0&lt;a1 但索引&gt;=6"的 5-entry 歧义形态。
    ///
    /// 【质量与形态】端点取 RGB 主轴劈半聚类均值,索引取 4 代表色最近邻;不做多次拟合/排序穷举。
    /// 质量低于编辑器内置的 BC 编码器,但体量收益即时,且产物保证能被自家解码器读回 ——
    /// 生成物先过自家解码关,是 pack 的最低正确性底线(测试:DxtEncoderRoundtripTests)。
    /// 非 4 倍数尺寸:解码器按 4x4 块行推进并丢弃越界像素,编码器同构补边,块数与官方包一致。
    /// </summary>
    public static class DxtEncoder
    {
        /// <summary>把 RGBA8888 线性像素压成指定块格式(DXT1/DXT3/DXT5)。R8/RG88 等掩码格式不走这里。</summary>
        public static byte[] Compress(int width, int height, byte[] rgba, MipmapFormat target)
        {
            if (rgba == null) throw new ArgumentNullException(nameof(rgba));
            if (width <= 0 || height <= 0) throw new ArgumentException($"bad size {width}x{height}");
            long need = (long)width * height * 4;
            if (need > int.MaxValue) throw new ArgumentException($"image too large: {width}x{height}");
            if (rgba.Length < need) throw new ArgumentException($"pixel buffer shorter than {width}x{height} RGBA");

            if (target != MipmapFormat.CompressedDXT1 &&
                target != MipmapFormat.CompressedDXT3 &&
                target != MipmapFormat.CompressedDXT5)
                throw new ArgumentException($"not a block-compressed target: {target}");

            int blocksX = (width + 3) / 4;
            int blocksY = (height + 3) / 4;
            int bytesPerBlock = target == MipmapFormat.CompressedDXT1 ? 8 : 16;

            var data = new byte[blocksX * blocksY * bytesPerBlock];
            var px = new byte[16 * 4]; // 一个 4x4 块,行优先,越界补边(与解码器丢边对称)

            for (var by = 0; by < blocksY; by++)
            for (var bx = 0; bx < blocksX; bx++)
            {
                for (var py = 0; py < 4; py++)
                for (var pxx = 0; pxx < 4; pxx++)
                {
                    var sx = Math.Min(bx * 4 + pxx, width - 1);
                    var sy = Math.Min(by * 4 + py, height - 1);
                    var src = (sy * width + sx) * 4;
                    var dst = (py * 4 + pxx) * 4;
                    px[dst] = rgba[src];
                    px[dst + 1] = rgba[src + 1];
                    px[dst + 2] = rgba[src + 2];
                    px[dst + 3] = rgba[src + 3];
                }

                var o = (by * blocksX + bx) * bytesPerBlock;
                switch (target)
                {
                    case MipmapFormat.CompressedDXT1:
                        CompressColorBlock(px, data, o, true);
                        break;
                    case MipmapFormat.CompressedDXT3:
                        for (var pi = 0; pi < 16; pi++)
                        {
                            // BC2:4bit/像素,像素 pi 落在字节 pi/2 的 低(pi 偶)/高(pi 奇)4 位
                            data[o + (pi >> 1)] |= (byte)((px[pi * 4 + 3] >> 4) << (4 * (pi & 1)));
                        }
                        CompressColorBlock(px, data, o + 8, false);
                        break;
                    default: // DXT5
                        CompressAlphaBlock(px, data, o);
                        CompressColorBlock(px, data, o + 8, false);
                        break;
                }
            }

            return data;
        }

        /// <summary>
        /// BC3 alpha 子块:a0=max、a1=min(a0&gt;a1 → 解码器恒走 7-entry 表,表[0]/[1] 即两端点原值);
        /// min==max 时写 a0==a1(解码器走 5-entry 退化表)并只用索引 0。
        /// 注意 7-entry 模式没有 0/255 保留档(那是 5-entry 的表[6]/[7]),所以极端值只有当它
        /// 本身是端点时才精确 —— 这正是取 min/max 做端点的原因。
        /// </summary>
        private static void CompressAlphaBlock(byte[] px, byte[] outBlocks, int o)
        {
            byte min = 255, max = 0;
            for (var i = 0; i < 16; i++)
            {
                var a = px[i * 4 + 3];
                if (a < min) min = a;
                if (a > max) max = a;
            }

            Span<byte> table = stackalloc byte[8];
            byte a0, a1;
            int limit;
            if (min == max)
            {
                a0 = max; a1 = max;
                limit = 1; // 5-entry 退化表:索引 >=2 的插值槽不可靠,只用端点
                for (var i = 0; i < 8; i++) table[i] = max;
            }
            else
            {
                a0 = max; a1 = min; // a0>a1 → 7-entry
                table[0] = a0;
                table[1] = a1;
                for (var i = 1; i < 7; i++)
                    table[i + 1] = (byte)(((7 - i) * a0 + i * a1) / 7);
                limit = 8;
            }

            Span<byte> indices = stackalloc byte[16];
            for (var i = 0; i < 16; i++)
            {
                var a = px[i * 4 + 3];
                byte best = 0; var bestErr = int.MaxValue;
                for (byte t = 0; t < limit; t++)
                {
                    var err = Math.Abs(a - table[t]);
                    if (err < bestErr) { bestErr = err; best = t; }
                }
                indices[i] = best;
            }

            outBlocks[o] = a0;
            outBlocks[o + 1] = a1;
            // 48bit:每 3 字节装 8 个 3bit 索引,LSB-first(镜像解码器 value &gt;&gt; (3*j))
            for (var chunk = 0; chunk < 2; chunk++)
            {
                ulong v = 0;
                for (var j = 0; j < 8; j++)
                    v |= (ulong)indices[chunk * 8 + j] << (3 * j);
                for (var b = 0; b < 3; b++) outBlocks[o + 2 + chunk * 3 + b] = (byte)((v >> (8 * b)) & 0xFF);
            }
        }

        /// <summary>
        /// BC1 颜色子块。isDxt1=true 时才存在形态选择问题:解码器用"打包值 a&lt;=b"决定走 3 色 +
        /// 索引 3 = 透明黑,所以
        ///   - 全不透明块 → 4 色形态(c0&gt;c1),白赚一个中间色;
        ///   - 含半透明像素的块 → 只能 3 色形态(c0&lt;=c1),用少一档颜色换索引 3 的透明语义,
        ///     不透明像素只在 0..2 里选(3 色形态下索引 3 解码为 (0,0,0,0),给错就是画面上的洞);
        ///   - 两个端点在 565 下同值时,写不出 c0&gt;c1,一律退回 3 色形态(同样只用 0..2)。
        /// DXT3/DXT5 的颜色子块解码器恒按 4 色表解读(isDxt1=false 不看端点大小),一律写 c0&gt;c1。
        /// </summary>
        private static void CompressColorBlock(byte[] px, byte[] outBlocks, int o, bool isDxt1)
        {
            Span<int> mn = stackalloc int[3] { 255, 255, 255 };
            Span<int> mx = stackalloc int[3] { 0, 0, 0 };
            var anyTranslucent = false;
            for (var i = 0; i < 16; i++)
            {
                if (isDxt1 && px[i * 4 + 3] < 128) anyTranslucent = true;
                for (var c = 0; c < 3; c++)
                {
                    var v = px[i * 4 + c];
                    if (v < mn[c]) mn[c] = v;
                    if (v > mx[c]) mx[c] = v;
                }
            }

            if (mn[0] == mx[0] && mn[1] == mx[1] && mn[2] == mx[2])
            {
                // 常数色:端点同值 → 解码器走 3 色形态,索引 0 就是该色;
                // 半透明像素改钉索引 3(透明黑),否则会把"该透的地方"画成实心块
                var e = Pack565((byte)mx[0], (byte)mx[1], (byte)mx[2]);
                WriteEndpoints(e, e, outBlocks, o);
                var cbits = 0u;
                if (anyTranslucent)
                    for (var i = 0; i < 16; i++)
                        if (px[i * 4 + 3] < 128) cbits |= 3u << (2 * i);
                for (var b = 0; b < 4; b++) outBlocks[o + 4 + b] = (byte)((cbits >> (8 * b)) & 0xFF);
                return;
            }

            int dom = 0;
            if (mx[1] - mn[1] > mx[dom] - mn[dom]) dom = 1;
            if (mx[2] - mn[2] > mx[dom] - mn[dom]) dom = 2;

            Span<int> order = stackalloc int[16];
            for (var i = 0; i < 16; i++) order[i] = i;
            SortByDominantAxis(px, order, dom, mn[dom], mx[dom]);

            var eA = Pack565FromMean(px, order, 0, 8);   // 低半簇均值
            var eB = Pack565FromMean(px, order, 8, 16);  // 高半簇均值

            // 端点按打包值排序:解码器判形态用的就是 565 原值(a<=b),不是展开后的 888
            var asc = eA <= eB;
            int low = asc ? eA : eB;
            int high = asc ? eB : eA;

            var threeColor = isDxt1 && (anyTranslucent || high == low);

            Span<uint> table = stackalloc uint[4];
            int search;
            if (threeColor)
            {
                // 3 色:c0<=c1,表 = {c0, c1, half};索引 3 归透明黑,不参与颜色搜索
                WriteEndpoints(low, high, outBlocks, o);
                table[0] = RgbFrom565(low);
                table[1] = RgbFrom565(high);
                table[2] = Half(table[0], table[1]);
                search = 3;
            }
            else
            {
                // 4 色:c0>c1,表 = {c0, c1, 2/3, 1/3}
                WriteEndpoints(high, low, outBlocks, o);
                table[0] = RgbFrom565(high);
                table[1] = RgbFrom565(low);
                table[2] = TwoThirds(table[0], table[1]);
                table[3] = TwoThirds(table[1], table[0]);
                search = 4;
            }

            var bits = 0u;
            for (var i = 0; i < 16; i++)
            {
                byte best;
                if (anyTranslucent && px[i * 4 + 3] < 128)
                {
                    best = 3; // 只在 3 色形态下可达:透明像素钉到索引 3,解码为 (0,0,0,0)
                }
                else
                {
                    best = 0;
                    var pr = px[i * 4]; var pg = px[i * 4 + 1]; var pb = px[i * 4 + 2];
                    var bestErr = long.MaxValue;
                    for (byte t = 0; t < search; t++)
                    {
                        var dr = pr - (int)((table[t] >> 16) & 0xFF);
                        var dg = pg - (int)((table[t] >> 8) & 0xFF);
                        var db = pb - (int)(table[t] & 0xFF);
                        var err = (long)dr * dr + dg * dg + db * db;
                        if (err < bestErr) { bestErr = err; best = t; }
                    }
                }
                bits |= (uint)best << (2 * i);
            }
            for (var b = 0; b < 4; b++) outBlocks[o + 4 + b] = (byte)((bits >> (8 * b)) & 0xFF);
        }

        private static void SortByDominantAxis(byte[] px, Span<int> order, int dom, int min, int max)
        {
            var range = Math.Max(1, max - min);
            Span<int> key = stackalloc int[16];
            for (var i = 0; i < 16; i++)
                key[order[i]] = (px[order[i] * 4 + dom] - min) * 255 / range;

            for (var i = 1; i < 16; i++)
            {
                var cur = order[i];
                var j = i - 1;
                while (j >= 0 && key[order[j]] > key[cur])
                {
                    order[j + 1] = order[j];
                    j--;
                }
                order[j + 1] = cur;
            }
        }

        private static int Pack565FromMean(byte[] px, ReadOnlySpan<int> order, int from, int to)
        {
            var r = 0; var g = 0; var b = 0; var n = Math.Max(1, to - from);
            for (var i = from; i < to; i++)
            {
                r += px[order[i] * 4];
                g += px[order[i] * 4 + 1];
                b += px[order[i] * 4 + 2];
            }
            return Pack565((byte)(r / n), (byte)(g / n), (byte)(b / n));
        }

        private static int Pack565(byte r, byte g, byte b) =>
            (r >> 3 << 11) | (g >> 2 << 5) | (b >> 3);

        /// <summary>565 → 8888,展开方式与解码器 Unpack565 完全一致(r&lt;&lt;3 | r&gt;&gt;2)。</summary>
        private static uint RgbFrom565(int v)
        {
            int r = (v >> 11) & 0x1F, g = (v >> 5) & 0x3F, b = v & 0x1F;
            return (uint)(((r << 3) | (r >> 2)) << 16 | ((g << 2) | (g >> 4)) << 8 | ((b << 3) | (b >> 2)));
        }

        /// <summary>midpoint,截断除法 —— 与解码器 (c+d)/2 互镜像。</summary>
        private static uint Half(uint c, uint d)
        {
            var r = ((c >> 16 & 0xFF) + (d >> 16 & 0xFF)) / 2;
            var g = ((c >> 8 & 0xFF) + (d >> 8 & 0xFF)) / 2;
            var b = ((c & 0xFF) + (d & 0xFF)) / 2;
            return (uint)(r << 16 | g << 8 | b);
        }

        /// <summary>2/3 混合,截断除法 —— 与解码器 (2*c+d)/3 互镜像。</summary>
        private static uint TwoThirds(uint c, uint d)
        {
            var r = (2 * (int)(c >> 16 & 0xFF) + (d >> 16 & 0xFF)) / 3;
            var g = (2 * (int)(c >> 8 & 0xFF) + (d >> 8 & 0xFF)) / 3;
            var b = (2 * (int)(c & 0xFF) + (d & 0xFF)) / 3;
            return (uint)(r << 16 | g << 8 | b);
        }

        private static void WriteEndpoints(int c0, int c1, byte[] outBlocks, int o)
        {
            outBlocks[o] = (byte)(c0 & 0xFF);
            outBlocks[o + 1] = (byte)((c0 >> 8) & 0xFF);
            outBlocks[o + 2] = (byte)(c1 & 0xFF);
            outBlocks[o + 3] = (byte)((c1 >> 8) & 0xFF);
        }
    }
}
