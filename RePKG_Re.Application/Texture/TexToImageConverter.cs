using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using RePKG_Re.Core.Texture;
using RePKG_Re.Application.Texture.Helpers;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Gif;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace RePKG_Re.Application.Texture
{
    public class TexToImageConverter
    {
        /// <summary>
        /// 转换 TEX 为图片字节(内存版,成品整份驻留)。
        /// 需要把编码结果直接写入文件时用 <see cref="ConvertToImage(ITex, Stream, double)"/>
        /// 流式重载,避免大图/GIF 的成品字节再占一份内存。
        /// </summary>
        public ImageResult ConvertToImage(ITex tex, double effectThresholdPercent = 0)
        {
            return ConvertToImage(tex, null, effectThresholdPercent);
        }

        /// <summary>
        /// 转换 TEX 为图片,编码结果直接写入 outputStream(null = 返回字节)。
        /// 流式模式:返回的 ImageResult.Stream 指向调用方提供的流(由调用方关闭),Bytes 为 null。
        /// </summary>
        public ImageResult ConvertToImage(ITex tex, Stream outputStream, double effectThresholdPercent = 0)
        {
            if (tex == null) throw new ArgumentNullException(nameof(tex));

            if (tex.IsGif)
                return ConvertToGif(tex, outputStream);

            var sourceMipmap = tex.FirstImage.FirstMipmap;

            if (tex.IsVideoTexture)
            {
                if (sourceMipmap.Bytes.Length < 12)
                {
                    throw new InvalidOperationException("Expected mp4 magic header");
                }

                var mp4magic = Encoding.ASCII.GetString(sourceMipmap.Bytes, 4, 8);

                if (!mp4magic.Equals("ftypisom", StringComparison.OrdinalIgnoreCase)
                    && !mp4magic.Equals("ftypmsnv", StringComparison.OrdinalIgnoreCase)
                    && !mp4magic.Equals("ftypmp42", StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException("Expected mp4 magic header");
                }

                // 流式模式必须把 mp4 字节写入 outputStream(与下方非 raw 分支同坑:
                // 2026-08-23 实测视频纹理 TEX 转出 mp4 空文件)
                if (outputStream != null && sourceMipmap.Bytes != null)
                    outputStream.Write(sourceMipmap.Bytes, 0, sourceMipmap.Bytes.Length);

                return new ImageResult
                {
                    Bytes = sourceMipmap.Bytes,
                    Format = MipmapFormat.VideoMp4
                };
            }

            var format = sourceMipmap.Format;

            if (format.IsCompressed())
                throw new InvalidOperationException("Raw mipmap format must be uncompressed");

            if (format.IsRawFormat())
            {
                // 零拷贝:WrapMemory 直接包装解压出的像素字节,不再复制一份进 ImageSharp 缓冲
                // (8K 图每张省 ~230MB 拷贝)。fixed 块保证指针在图像生命周期内有效。
                // 注意:WrapMemory 图像禁止原地 Mutate(会抛 InvalidMemoryOperationException),
                // 需要改尺寸时一律 Clone(输出与原 LoadPixelData+Mutate 路径一致)。
                unsafe
                {
                    fixed (byte* p = sourceMipmap.Bytes)
                    {
                        using (var image = ImageFromRawFormat(format, p, sourceMipmap.Width, sourceMipmap.Height))
                        {
                            Image toEncode = image;
                            try
                            {
                                if (sourceMipmap.Width != tex.Header.ImageWidth ||
                                    sourceMipmap.Height != tex.Header.ImageHeight)
                                    toEncode = image.Clone(
                                        x => x.Crop(tex.Header.ImageWidth, tex.Header.ImageHeight));

                                // 效果图分析:编码前直接采样内存位图,零额外解码
                                double? transparentRatio = null;
                                double? blackRatio = null;
                                if (effectThresholdPercent > 0)
                                {
                                    EffectImageDetector.IsEffectImage(toEncode, effectThresholdPercent,
                                        out var trans, out var black);
                                    transparentRatio = trans;
                                    blackRatio = black;
                                }

                                if (outputStream != null)
                                {
                                    toEncode.SaveAsPng(outputStream);

                                    return new ImageResult
                                    {
                                        Stream = outputStream,
                                        Format = MipmapFormat.ImagePNG,
                                        TransparentRatio = transparentRatio,
                                        BlackRatio = blackRatio
                                    };
                                }

                                using (var memoryStream = new MemoryStream())
                                {
                                    toEncode.SaveAsPng(memoryStream);

                                    return new ImageResult
                                    {
                                        Bytes = memoryStream.ToArray(),
                                        Format = MipmapFormat.ImagePNG,
                                        TransparentRatio = transparentRatio,
                                        BlackRatio = blackRatio
                                    };
                                }
                            }
                            finally
                            {
                                if (toEncode != image)
                                    toEncode.Dispose();
                            }
                        }
                    }
                }
            }

            // 非 raw 格式(PNG/JPEG 等已编码图,容器 FIF 直接映射):字节原样返回。
            // 流式模式必须把字节写入 outputStream,否则调用方(ConvertToImageAndSave)
            // 创建了文件流却没内容 → 0 字节空文件(2026-08-23 实测 rgba8888+png 容器 TEX 全空)
            if (outputStream != null && sourceMipmap.Bytes != null)
                outputStream.Write(sourceMipmap.Bytes, 0, sourceMipmap.Bytes.Length);

            return new ImageResult
            {
                Bytes = sourceMipmap.Bytes,
                Format = format
            };
        }

        public MipmapFormat GetConvertedFormat(ITex tex)
        {
            if (tex == null) throw new ArgumentNullException(nameof(tex));

            // 动画纹理:转换输出为 GIF(与 ConvertToImage 的 IsGif 分支保持一致,
            // 且优先级相同——IsGif 先于 IsVideoTexture,避免内容与扩展名不一致)
            if (tex.IsGif)
            {
                return MipmapFormat.ImageGIF;
            }

            if (tex.IsVideoTexture)
            {
                return MipmapFormat.VideoMp4;
            }

            var format = tex.FirstImage.FirstMipmap.Format;

            if (format.IsCompressed())
                throw new InvalidOperationException("Raw mipmap format must be uncompressed");

            return format.IsRawFormat() ? MipmapFormat.ImagePNG : format;
        }

        private static unsafe ImageResult ConvertToGif(ITex tex, Stream outputStream)
        {
            var frameFormat = tex.FirstImage.FirstMipmap.Format;

            if (!frameFormat.IsRawFormat())
                throw new InvalidOperationException(
                    "Only raw mipmap formats are supported right now while converting gif");

            var target = outputStream ?? (Stream)new MemoryStream();
            try
            {
                using (var writer = new GifWriter(target, tex.FrameInfoContainer.GifWidth,
                    tex.FrameInfoContainer.GifHeight))
                {
                    // 按源图像分组逐张处理:同一源图的所有帧一起裁剪,处理完立即释放。
                    // 峰值内存 = 1 张全尺寸源图 + 一批量化结果(每帧 4~6MB)。
                    var framesByImage = tex.FrameInfoContainer.Frames
                        .Select((frameInfo, index) => new { frameInfo, index })
                        .GroupBy(x => x.frameInfo.ImageId)
                        .OrderBy(g => g.Key);

                    // 量化并行度:帧量化是纯 CPU 任务,丢进 .NET 全局线程池
                    // (多 pkg 并行时线程池自动分核,不会线程数爆炸)。
                    // 实测 16 核机器:全核(16)比半核(8)只快 ~1s 但内存 +170MB,
                    // 半核是耗时/内存平衡点。
                    var batchSize = Environment.ProcessorCount / 2;
                    if (batchSize < 1) batchSize = 1;
                    if (batchSize > 8) batchSize = 8;

                    Image source = null;
                    try
                    {
                        foreach (var group in framesByImage)
                        {
                            if (source != null)
                            {
                                source.Dispose();
                                source = null;
                            }

                            var mipmap = tex.ImagesContainer.Images[group.Key].FirstMipmap;

                            fixed (byte* p = mipmap.Bytes)
                            {
                                source = ImageFromRawFormat(frameFormat, p, mipmap.Width, mipmap.Height);

                                // 分块:每批 batchSize 帧并行量化(Clone + 量化,只读共享源图),
                                // 然后按原帧顺序写入——GIF 帧顺序严格不变。
                                var frames = group.ToList();
                                for (int start = 0; start < frames.Count; start += batchSize)
                                {
                                    int count = Math.Min(batchSize, frames.Count - start);
                                    var tasks = new Task<QuantizedFrame>[count];
                                    for (int j = 0; j < count; j++)
                                    {
                                        var x = frames[start + j];
                                        tasks[j] = Task.Run(() =>
                                        {
                                            var frameInfo = x.frameInfo;
                                            var width = frameInfo.Width != 0 ? frameInfo.Width : frameInfo.HeightX;
                                            var height = frameInfo.Height != 0 ? frameInfo.Height : frameInfo.WidthY;
                                            var xPos = Math.Min(frameInfo.X, frameInfo.X + width);
                                            var yPos = Math.Min(frameInfo.Y, frameInfo.Y + height);
                                            var rotationAngle =
                                                -(Math.Atan2(Math.Sign(height), Math.Sign(width)) - Math.PI / 4);

                                            using (var frame = source.Clone(
                                                context => context
                                                    .Crop(new Rectangle((int)xPos, (int)yPos,
                                                        (int)Math.Abs(width), (int)Math.Abs(height)))
                                                    .Rotate((float)Math.Round(rotationAngle * 180 / Math.PI))))
                                            {
                                                var delay = (int)Math.Round(frameInfo.Frametime * 100.0f);
                                                return QuantizeFrameTyped(writer, frame, delay, frameFormat);
                                            }
                                        });
                                    }

                                    Task.WhenAll(tasks).GetAwaiter().GetResult();
                                    foreach (var t in tasks)
                                        writer.WriteQuantizedFrame(t.Result);
                                }
                            }
                        }
                    }
                    finally
                    {
                        source?.Dispose();
                    }
                }

                if (outputStream != null)
                {
                    return new ImageResult
                    {
                        Stream = outputStream,
                        Format = MipmapFormat.ImageGIF
                    };
                }

                return new ImageResult
                {
                    Bytes = ((MemoryStream)target).ToArray(),
                    Format = MipmapFormat.ImageGIF
                };
            }
            finally
            {
                if (outputStream == null)
                    target.Dispose();
            }
        }

        /// <summary>按帧原始格式分派到 GifWriter 的泛型 QuantizeFrame(线程安全)。</summary>
        private static QuantizedFrame QuantizeFrameTyped(GifWriter writer, Image frame, int delay,
            MipmapFormat format)
        {
            switch (format)
            {
                case MipmapFormat.R8:
                    return writer.QuantizeFrame((Image<L8>)frame, delay);
                case MipmapFormat.RG88:
                    return writer.QuantizeFrame((Image<RG88>)frame, delay);
                case MipmapFormat.RGBA8888:
                    return writer.QuantizeFrame((Image<Rgba32>)frame, delay);
                default:
                    throw new InvalidOperationException($"Mipmap format: {format} is not supported");
            }
        }

        /// <summary>
        /// 从原始像素字节创建位图(bytes 非 null = 零拷贝 WrapMemory 包装,不复制像素;
        /// 调用方必须保证指针在返回图像生命周期内有效,用 fixed 块包裹)。
        /// bytes 为 null 时创建空位图(ConvertToGif 的目标画布)。
        /// </summary>
        private static unsafe Image ImageFromRawFormat(MipmapFormat format, byte* bytes, int width, int height)
        {
            switch (format)
            {
                case MipmapFormat.R8:
                    return bytes == null
                        ? new Image<L8>(width, height)
                        : Image.WrapMemory<L8>((void*)bytes, width, height);

                case MipmapFormat.RG88:
                    return bytes == null
                        ? new Image<RG88>(width, height)
                        : Image.WrapMemory<RG88>((void*)bytes, width, height);

                case MipmapFormat.RGBA8888:
                    return bytes == null
                        ? new Image<Rgba32>(width, height)
                        : Image.WrapMemory<Rgba32>((void*)bytes, width, height);

                default:
                    throw new InvalidOperationException($"Mipmap format: {format} is not supported");
            }
        }
    }

    public class ImageResult
    {
        /// <summary>编码产物字节(流式模式为 null)</summary>
        public byte[] Bytes { get; set; }

        /// <summary>流式编码输出:指向调用方提供的流,由调用方负责关闭(字节模式为 null)</summary>
        public Stream Stream { get; set; }

        public MipmapFormat Format { get; set; }

        /// <summary>效果图分析结果:透明像素占比(null = 未分析)</summary>
        public double? TransparentRatio { get; set; }

        /// <summary>效果图分析结果:黑色像素占比(null = 未分析)</summary>
        public double? BlackRatio { get; set; }
    }
}
