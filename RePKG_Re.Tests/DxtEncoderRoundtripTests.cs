using System;
using NUnit.Framework;
using RePKG_Re.Application.Texture;
using RePKG_Re.Application.Texture.Helpers;
using RePKG_Re.Core.Texture;

namespace RePKG_Re.Tests
{
    /// <summary>
    /// DxtEncoder 回环测试:编出来的块一律用自家解码器(DXT.cs)读回,断言
    ///   - 常数色逐像素精确(端点 565 量化内)
    ///   - 合成图案每格式 PSNR 达标
    ///   - BC3 alpha 误差 ≤ 半档(7-entry 步长 (max-min)/7)
    ///   - DXT1 半透明像素钉到索引 3 后解码为 (0,0,0,0)
    ///   - 非 4 倍数尺寸的块数与解码器行为对称
    /// 独立性边界:DXT.cs 与 DxtEncoder 是同一套约定的两面,这里钉的是"自洽 + 与官方
    /// 语义(3 色形态、保留档)一致",不宣称与第三方 BC 编码器同码流。
    /// </summary>
    [TestFixture]
    public class DxtEncoderRoundtripTests
    {
        // ---------- 原料 ----------

        /// <summary>
        /// 4 个象限各自纯色,颜色全取自 565 定点集(R/B 用 r&lt;&lt;3|r&gt;&gt;2、G 用 g&lt;&lt;2|g&gt;&gt;4 的原像),
        /// 于是"常数块 → 端点即该色"必须逐像素无损回到源图 —— 量化误差不该在这里出现。
        /// </summary>
        private static byte[] Checker(int w, int h)
        {
            var data = new byte[w * h * 4];
            for (var y = 0; y < h; y++)
            for (var x = 0; x < w; x++)
            {
                var i = (y * w + x) * 4;
                var qx = x < w / 2; var qy = y < h / 2;
                data[i] = (byte)(qx ? (qy ? 239 : 16) : (qy ? 0 : 255));
                data[i + 1] = (byte)(qx ? (qy ? 235 : 40) : (qy ? 255 : 0));
                data[i + 2] = (byte)(qx ? (qy ? 247 : 8) : (qy ? 132 : 115));
                data[i + 3] = 255;
            }
            return data;
        }

        /// <summary>水平渐变 + 正弦蓝通道(平滑内容,DXT 的主场)。</summary>
        private static byte[] Gradient(int w, int h)
        {
            var data = new byte[w * h * 4];
            for (var y = 0; y < h; y++)
            for (var x = 0; x < w; x++)
            {
                var i = (y * w + x) * 4;
                data[i] = (byte)(x * 255 / Math.Max(1, w - 1));
                data[i + 1] = (byte)(y * 255 / Math.Max(1, h - 1));
                data[i + 2] = (byte)(128 + 120 * Math.Sin(x * 0.35 + y * 0.1));
                data[i + 3] = 255;
            }
            return data;
        }

        private static byte[] Solid(byte r, byte g, byte b, byte a)
        {
            var data = new byte[16 * 16 * 4];
            for (var i = 0; i < 16 * 16; i++)
            {
                data[i * 4] = r; data[i * 4 + 1] = g; data[i * 4 + 2] = b; data[i * 4 + 3] = a;
            }
            return data;
        }

        private static DXTFlags FlagsFor(MipmapFormat f) =>
            f == MipmapFormat.CompressedDXT1 ? DXTFlags.DXT1 :
            f == MipmapFormat.CompressedDXT3 ? DXTFlags.DXT3 : DXTFlags.DXT5;

        private static byte[] Decode(int w, int h, byte[] packed, MipmapFormat target) =>
            DXT.DecompressImage(w, h, packed, FlagsFor(target));

        private static double PsnrRgb(byte[] a, byte[] b)
        {
            long se = 0; long n = 0;
            for (var i = 0; i < a.Length / 4; i++)
                for (var c = 0; c < 3; c++) { var d = a[i * 4 + c] - b[i * 4 + c]; se += d * d; n++; }
            var mse = (double)se / n;
            return mse == 0 ? double.PositiveInfinity : 10 * Math.Log10(255 * 255 / mse);
        }

        // ---------- 常数色:必须逐像素精确 ----------

        [TestCase(MipmapFormat.CompressedDXT1)]
        [TestCase(MipmapFormat.CompressedDXT3)]
        [TestCase(MipmapFormat.CompressedDXT5)]
        public void SolidColor_RoundtripsExactly(MipmapFormat target)
        {
            // (132,65,99) 是 565 的定点值:r=132 → 16<<3|16>>2,G=65 → 16<<2|16>>4,B=99 → 12<<3|12>>2
            // 取定点值才能要求逐像素相等,否则端点量化本身就有 ≤8 的通道误差
            var rgba = Solid(132, 65, 99, 255);
            var packed = DxtEncoder.Compress(16, 16, rgba, target);
            var back = Decode(16, 16, packed, target);

            for (var i = 0; i < 16 * 16; i++)
            {
                Assert.That(back[i * 4], Is.EqualTo(132), $"R px{i}");
                Assert.That(back[i * 4 + 1], Is.EqualTo(65), $"G px{i}");
                Assert.That(back[i * 4 + 2], Is.EqualTo(99), $"B px{i}");
            }
            if (target != MipmapFormat.CompressedDXT3)
                for (var i = 0; i < 16 * 16; i++)
                    Assert.That(back[i * 4 + 3], Is.EqualTo(255), $"A px{i}");
            else
                Assert.That(back[3], Is.EqualTo(255)); // 255>>4=15 → 15|15<<4=255,精确
        }

        // ---------- 尺寸与块数 ----------

        [TestCase(MipmapFormat.CompressedDXT1, 8)]
        [TestCase(MipmapFormat.CompressedDXT3, 16)]
        [TestCase(MipmapFormat.CompressedDXT5, 16)]
        public void OddSize_BlockCount_MatchesDecoder(MipmapFormat target, int bytesPerBlock)
        {
            // 6x6 → ceil(6/4)^2 = 4 块;解码器同样按 4x4 块行推进并丢弃越界像素
            var rgba = Solid(10, 65, 90, 255);
            var packed = DxtEncoder.Compress(6, 6, rgba, target);
            Assert.That(packed.Length, Is.EqualTo(((6 + 3) / 4) * ((6 + 3) / 4) * bytesPerBlock));

            var back = Decode(6, 6, packed, target);
            Assert.That(back.Length, Is.EqualTo(6 * 6 * 4));
            // 常数色 + 补边:所有采样像素都应是端点色(G=65 是 565 定点值,逐像素相等)
            for (var y = 0; y < 6; y++)
            for (var x = 0; x < 6; x++)
            {
                Assert.That(back[(y * 6 + x) * 4 + 1], Is.EqualTo(65), $"px {x},{y}");
            }
        }

        [Test]
        public void ShortPixelBuffer_Throws()
        {
            Assert.Throws<ArgumentException>(() =>
                DxtEncoder.Compress(8, 8, new byte[8 * 8 * 4 - 4], MipmapFormat.CompressedDXT5));
            Assert.Throws<ArgumentException>(() =>
                DxtEncoder.Compress(8, 8, new byte[8 * 8 * 4], MipmapFormat.R8));
        }

        // ---------- PSNR:合成图案 ----------

        [Test]
        public void Checker_Rgb_DecodesBackExactly()
        {
            const int w = 64, h = 64;
            var rgba = Checker(w, h);
            // 每块纯色 → 端点即块色,且色值全在 565 定点集上:RGB 三通道必须逐像素相等。
            // 这条断言比 PSNR 阈值强得多,任何"端点写反/索引错位/形态选错"都会在这里炸。
            foreach (var target in new[] { MipmapFormat.CompressedDXT1, MipmapFormat.CompressedDXT3, MipmapFormat.CompressedDXT5 })
            {
                var back = Decode(w, h, DxtEncoder.Compress(w, h, rgba, target), target);
                for (var i = 0; i < w * h; i++)
                    for (var c = 0; c < 3; c++)
                        Assert.That(back[i * 4 + c], Is.EqualTo(rgba[i * 4 + c]), $"{target} px{i} c{c}");
                if (target != MipmapFormat.CompressedDXT3)
                    for (var i = 0; i < w * h; i++)
                        Assert.That(back[i * 4 + 3], Is.EqualTo(255), $"{target} px{i} alpha");
            }
        }

        [Test]
        public void Gradient_Psnr_PerFormat()
        {
            const int w = 128, h = 64;
            var rgba = Gradient(w, h);
            // 25dB 是"看得见内容、没有明显崩坏"的底线;实测远高于此,留大余量防平台差异
            foreach (var target in new[] { MipmapFormat.CompressedDXT1, MipmapFormat.CompressedDXT3, MipmapFormat.CompressedDXT5 })
            {
                var back = Decode(w, h, DxtEncoder.Compress(w, h, rgba, target), target);
                var psnr = PsnrRgb(rgba, back);
                Assert.That(psnr, Is.GreaterThan(25), $"{target} 渐变阈值, got {psnr:F1}dB");
            }
        }

        // ---------- BC3 alpha:7-entry 精度 ----------

        [Test]
        public void Dxt5Alpha_ErrorWithinHalfStep()
        {
            const int w = 64, h = 64;
            var rgba = new byte[w * h * 4];
            var rnd = new Random(11);
            for (var i = 0; i < w * h; i++)
            {
                rgba[i * 4] = (byte)rnd.Next(256);
                rgba[i * 4 + 1] = (byte)rnd.Next(256);
                rgba[i * 4 + 2] = (byte)rnd.Next(256);
                // 把 alpha 限制在 32..223:步长 ≤ (223-32)/7 ≈ 27,半档 ≤ 13.5 → 断言 ≤ 14。
                // 全 0..255 随机块的 min/max 撑满时步长 ~32,半档 16,同一条公式,取 14 只是留安全边距。
                rgba[i * 4 + 3] = (byte)(32 + rnd.Next(192));
            }
            var back = Decode(w, h, DxtEncoder.Compress(w, h, rgba, MipmapFormat.CompressedDXT5), MipmapFormat.CompressedDXT5);
            for (var i = 0; i < w * h; i++)
                Assert.That(Math.Abs(rgba[i * 4 + 3] - back[i * 4 + 3]), Is.LessThanOrEqualTo(14), $"px {i}");
        }

        [Test]
        public void Dxt3Alpha_NibblePlacement()
        {
            const int w = 4, h = 4;
            var rgba = new byte[w * h * 4];
            var nib = new byte[16];
            for (var i = 0; i < 16; i++)
            {
                rgba[i * 4] = 5; rgba[i * 4 + 1] = 5; rgba[i * 4 + 2] = 5;
                // 覆盖 0..15 全部 4bit 码字:编码器只做 a>>4 截断,不四舍五入
                nib[i] = (byte)i;
                rgba[i * 4 + 3] = (byte)(i << 4);
            }
            var packed = DxtEncoder.Compress(w, h, rgba, MipmapFormat.CompressedDXT3);

            // 结构断言:像素 pi 的 4bit alpha 落在字节 pi>>1 的低(pi 偶)/高(pi 奇)4 位
            for (var pi = 0; pi < 16; pi++)
            {
                var b = packed[pi >> 1];
                Assert.That((pi & 1) == 0 ? (b & 0x0F) : (b >> 4), Is.EqualTo(nib[pi]), $"nibble px{pi}");
            }
            var back = Decode(w, h, packed, MipmapFormat.CompressedDXT3);
            for (var pi = 0; pi < 16; pi++)
                Assert.That(back[pi * 4 + 3], Is.EqualTo((byte)((nib[pi] << 4) | nib[pi])), $"alpha px{pi}");
        }

        // ---------- DXT1 透明语义 ----------

        [Test]
        public void Dxt1_TranslucentPixels_DecodeToTransparentBlack()
        {
            const int w = 8, h = 8;
            var rgba = new byte[w * h * 4];
            for (var i = 0; i < w * h; i++)
            {
                var y = i / w;
                rgba[i * 4] = 200; rgba[i * 4 + 1] = 30; rgba[i * 4 + 2] = 90;
                rgba[i * 4 + 3] = y >= h / 2 ? (byte)255 : (byte)0; // 下半不透明,上半全透明
            }
            var back = Decode(w, h, DxtEncoder.Compress(w, h, rgba, MipmapFormat.CompressedDXT1), MipmapFormat.CompressedDXT1);
            for (var y = 0; y < h / 2; y++)
            for (var x = 0; x < w; x++)
            {
                var i = (y * w + x) * 4;
                Assert.That(back[i + 3], Is.EqualTo(0), $"px {x},{y} alpha");
                Assert.That(back[i] | back[i + 1] | back[i + 2], Is.EqualTo(0), $"px {x},{y} 应为 (0,0,0,0)");
            }
            // 不透明半区:该块走 4 色形态,颜色端点来自不透明像素簇,主色应在索引 0/1/2 里 → 误差 ≤ 16
            for (var y = h / 2; y < h; y++)
            for (var x = 0; x < w; x++)
            {
                var i = (y * w + x) * 4;
                Assert.That(back[i + 3], Is.EqualTo(255));
                Assert.That(Math.Abs(back[i] - 200), Is.LessThanOrEqualTo(16));
            }
        }

        // ---------- 走完整 TEX 管线(与 pack 产物同构) ----------

        [Test]
        public void Dxt5_Mip_Roundtrips_Through_TexReaderPipeline()
        {
            const int w = 16, h = 16;
            var rgba = Checker(w, h);
            var packed = DxtEncoder.Compress(w, h, rgba, MipmapFormat.CompressedDXT5);

            var tex = new Tex
            {
                Magic1 = "TEXV0005",
                Magic2 = "TEXI0001",
                Header = new TexHeader
                {
                    Format = TexFormat.DXT5,
                    Flags = TexFlags.ClampUVs,
                    TextureWidth = w, TextureHeight = h, ImageWidth = w, ImageHeight = h,
                    UnkInt0 = 0
                },
                ImagesContainer = new TexImageContainer
                {
                    Magic = "TEXB0003",
                    ImageContainerVersion = TexImageContainerVersion.Version3,
                    ImageFormat = FreeImageFormat.FIF_UNKNOWN
                }
            };
            tex.ImagesContainer.Images.Add(new TexImage
            {
                Mipmaps =
                {
                    new TexMipmap
                    {
                        Width = w, Height = h,
                        Format = MipmapFormat.CompressedDXT5,
                        Bytes = packed,
                        DecompressedBytesCount = packed.Length,
                        IsLZ4Compressed = false
                    }
                }
            });

            using var ms = new System.IO.MemoryStream();
            using (var bw = new System.IO.BinaryWriter(ms, System.Text.Encoding.UTF8, true))
                TexWriter.Default.WriteTo(bw, tex);

            // LZ4 全开管线:mip 记录 → LZ4(无)→ DXT 解码 → RGBA8888
            using var br = new System.IO.BinaryReader(new System.IO.MemoryStream(ms.ToArray()), System.Text.Encoding.UTF8);
            var back = (Tex)TexReader.Default.ReadFrom(br);

            Assert.That(back.Header.Format, Is.EqualTo(TexFormat.DXT5));
            var mip = back.FirstImage.FirstMipmap;
            Assert.That(mip.Format, Is.EqualTo(MipmapFormat.RGBA8888), "解码管线应把 DXT5 展开成 RGBA");
            Assert.That(mip.Bytes.Length, Is.EqualTo(w * h * 4));
            for (var i = 0; i < w * h; i++)
                for (var c = 0; c < 3; c++)
                    Assert.That(Math.Abs(mip.Bytes[i * 4 + c] - rgba[i * 4 + c]), Is.LessThanOrEqualTo(16), $"px{i} c{c}");
        }
    }
}
