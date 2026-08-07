using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace RePKG_Re.Application.Texture
{
    /// <summary>
    /// 效果图判定:转换图透明占比或黑色占比达到阈值即视为"效果图"
    /// (粒子/光效/黑底纹理等,场景壁纸中大量存在,通常不是用户需要的素材)。
    /// 采样 + 早退,单张开销 <1ms~几 ms。
    /// </summary>
    public static class EffectImageDetector
    {
        /// <summary>alpha 低于此值视为透明像素</summary>
        public const byte TransparentAlphaThreshold = 32;

        /// <summary>不透明像素 RGB 均低于此值视为黑色像素</summary>
        public const byte NearBlackThreshold = 32;

        /// <summary>采样步长(每 4x4 取 1 像素)</summary>
        public const int SampleStride = 4;

        /// <param name="thresholdPercent">阈值(0-1,如 0.85 = 透明或黑色占比 ≥ 85%)</param>
        /// <returns>true = 命中效果图。占比为近似值(采样);命中时扫描完整以获得准确日志值。</returns>
        public static bool IsEffectImage(Image image, double thresholdPercent,
            out double transparentRatio, out double blackRatio)
        {
            // RGBA8888 是 WE 纹理最常见的格式,走零拷贝快路径
            if (image is Image<Rgba32> rgba)
                return AnalyzeCore(rgba, thresholdPercent, out transparentRatio, out blackRatio);

            // 其它格式(L8/La16 等)转成 RGBA 再分析
            using (var converted = image.CloneAs<Rgba32>())
                return AnalyzeCore(converted, thresholdPercent, out transparentRatio, out blackRatio);
        }

        private static bool AnalyzeCore(Image<Rgba32> image, double thresholdPercent,
            out double transparentRatio, out double blackRatio)
        {
            transparentRatio = 0;
            blackRatio = 0;

            long total = ((image.Width + SampleStride - 1) / SampleStride)
                       * ((image.Height + SampleStride - 1) / SampleStride);
            if (total == 0)
                return false;

            long needed = (long)System.Math.Ceiling(total * thresholdPercent);
            long transparent = 0;
            long black = 0;
            long remaining = total;
            bool hit = false;

            image.ProcessPixelRows(accessor =>
            {
                for (int y = 0; y < accessor.Height; y += SampleStride)
                {
                    var row = accessor.GetRowSpan(y);
                    for (int x = 0; x < row.Length; x += SampleStride)
                    {
                        remaining--;
                        ref var p = ref row[x];
                        if (p.A < TransparentAlphaThreshold)
                            transparent++;
                        else if (p.R < NearBlackThreshold && p.G < NearBlackThreshold && p.B < NearBlackThreshold)
                            black++;

                        if (hit)
                            continue; // 已命中:继续扫完以获得准确占比(用于日志)

                        if (transparent >= needed || black >= needed)
                        {
                            hit = true;
                        }
                        else if (transparent + remaining < needed && black + remaining < needed)
                        {
                            // 早退:剩余像素即使全部计入,两个占比都到不了阈值
                            return;
                        }
                    }
                }
            });

            transparentRatio = (double)transparent / total;
            blackRatio = (double)black / total;
            return hit;
        }
    }
}
