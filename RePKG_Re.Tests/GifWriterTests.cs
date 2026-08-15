using System;
using System.IO;
using NUnit.Framework;
using RePKG_Re.Application.Texture;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Gif;
using SixLabors.ImageSharp.PixelFormats;

namespace RePKG_Re.Tests
{
    /// <summary>
    /// 自研逐帧流式 GIF 写入器(GifWriter)测试:
    /// 多帧动画往返(帧数/尺寸/内容/延时) + 与 ImageSharp GifEncoder 输出逐像素一致。
    /// </summary>
    [TestFixture]
    public class GifWriterTests
    {
        /// <summary>4x4 红色帧(左上角 1 个透明像素)。</summary>
        private static Image<Rgba32> CreateRedFrame()
        {
            var img = new Image<Rgba32>(4, 4);
            for (int y = 0; y < 4; y++)
            for (int x = 0; x < 4; x++)
                img[x, y] = new Rgba32(255, 0, 0, 255);
            img[0, 0] = new Rgba32(0, 0, 0, 0); // 透明像素
            return img;
        }

        /// <summary>4x4 蓝色帧。</summary>
        private static Image<Rgba32> CreateBlueFrame()
        {
            var img = new Image<Rgba32>(4, 4);
            for (int y = 0; y < 4; y++)
            for (int x = 0; x < 4; x++)
                img[x, y] = new Rgba32(0, 0, 255, 255);
            return img;
        }

        /// <summary>红+蓝两帧的 Image(用于 ImageSharp 参考编码)。</summary>
        private static Image<Rgba32> CreateTwoFrameImage()
        {
            var img = CreateRedFrame();
            using (var frame2 = img.Frames.CreateFrame())
            {
                for (int y = 0; y < 4; y++)
                for (int x = 0; x < 4; x++)
                    frame2[x, y] = new Rgba32(0, 0, 255, 255);
            }
            return img;
        }

        private static byte[] EncodeWithGifWriter()
        {
            using (var ms = new MemoryStream())
            using (var red = CreateRedFrame())
            using (var blue = CreateBlueFrame())
            {
                using (var writer = new GifWriter(ms, 4, 4))
                {
                    writer.WriteFrame(red, 10);
                    writer.WriteFrame(blue, 10);
                    writer.Finish();
                }
                return ms.ToArray();
            }
        }

        [Test]
        public void TwoFrames_RoundTrip_FramesContentAndDelayCorrect()
        {
            var gif = EncodeWithGifWriter();

            using (var decoded = Image.Load<Rgba32>(gif))
            {
                Assert.AreEqual(4, decoded.Width);
                Assert.AreEqual(4, decoded.Height);
                Assert.AreEqual(2, decoded.Frames.Count, "帧数应为 2");

                // 帧延时
                var meta0 = decoded.Frames.RootFrame.Metadata.GetFormatMetadata(GifFormat.Instance);
                Assert.AreEqual(10, meta0.FrameDelay, "帧延时(1/100s)");

                // 帧0 红色(透明角在 ImageSharp 解码时映射为透明黑)
                var f0 = decoded.Frames.RootFrame;
                Assert.AreEqual(new Rgba32(255, 0, 0, 255), f0[3, 3], "帧0 应为红色");
                Assert.AreEqual(new Rgba32(0, 0, 0, 0), f0[0, 0], "帧0 透明角应保持透明");

                // 帧1 蓝色
                var f1 = decoded.Frames[1];
                Assert.AreEqual(new Rgba32(0, 0, 255, 255), f1[0, 0], "帧1 应为蓝色");
            }
        }

        [Test]
        public void Output_MatchesImageSharp_PixelWise()
        {
            var mine = EncodeWithGifWriter();

            // 参考:ImageSharp 对单帧分别编码(2.1.13 的 CreateFrame 多帧构造有 NRE,绕开)
            byte[] refRed, refBlue;
            using (var red = CreateRedFrame())
            using (var ms = new MemoryStream())
            {
                red.SaveAsGif(ms, new GifEncoder { ColorTableMode = GifColorTableMode.Local });
                refRed = ms.ToArray();
            }
            using (var blue = CreateBlueFrame())
            using (var ms = new MemoryStream())
            {
                blue.SaveAsGif(ms, new GifEncoder { ColorTableMode = GifColorTableMode.Local });
                refBlue = ms.ToArray();
            }

            using (var a = Image.Load<Rgba32>(mine))
            using (var r = Image.Load<Rgba32>(refRed))
            using (var b = Image.Load<Rgba32>(refBlue))
            {
                Assert.AreEqual(2, a.Frames.Count);
                for (int y = 0; y < a.Height; y++)
                for (int x = 0; x < a.Width; x++)
                {
                    Assert.AreEqual(r.Frames.RootFrame[x, y], a.Frames[0][x, y],
                        $"帧0 像素不一致 ({x},{y})");
                    Assert.AreEqual(b.Frames.RootFrame[x, y], a.Frames[1][x, y],
                        $"帧1 像素不一致 ({x},{y})");
                }
            }
        }
    }
}
