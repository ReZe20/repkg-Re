using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Memory;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing.Processors.Quantization;

namespace RePKG_Re.Application.Texture
{
    /// <summary>
    /// 单帧量化结果:调色板(RGB 字节)+ 像素索引 + 透明信息。
    /// 由 GifWriter.QuantizeFrame 产出(可并行),随后由 GifWriter.WriteQuantizedFrame
    /// 按帧顺序写入 GIF 文件。纯数据对象,不持有任何共享状态。
    /// </summary>
    public sealed class QuantizedFrame
    {
        public QuantizedFrame(int width, int height, int delayCentiseconds, byte[] indices,
            byte[] rgbPalette, int nbits, bool hasTransparent, byte transparentIndex)
        {
            Width = width;
            Height = height;
            DelayCentiseconds = delayCentiseconds;
            Indices = indices;
            RgbPalette = rgbPalette;
            Nbits = nbits;
            HasTransparent = hasTransparent;
            TransparentIndex = transparentIndex;
        }

        public int Width { get; }
        public int Height { get; }
        public int DelayCentiseconds { get; }
        public byte[] Indices { get; }
        public byte[] RgbPalette { get; }
        public int Nbits { get; }
        public bool HasTransparent { get; }
        public byte TransparentIndex { get; }
    }

    /// <summary>
    /// 逐帧流式 GIF89a 写入器(容器 + LZW 自实现,量化复用 ImageSharp OctreeQuantizer 256 色)。
    /// 与 ImageSharp GifEncoder 的关键区别:帧逐个写入、像素用完即弃,
    /// 内存峰值 = 单帧位图 + 单帧量化结果;ImageSharp 需要把全部帧先驻留进 Image 对象
    /// (N 帧 = N × 全尺寸位图),大动画(如 8K 140 帧)可差一个数量级。
    /// 线程模型:QuantizeFrame 线程安全、可并行调用(每帧独立量化器,无共享状态);
    /// WriteQuantizedFrame 必须由单个线程按帧顺序调用(内部 LZW 表不并发)。
    /// 多 pkg 并行时两者都通过 .NET 全局线程池调度,不会出现线程数爆炸。
    /// </summary>
    public sealed class GifWriter : IDisposable
    {
        private readonly BinaryWriter _writer;
        private bool _finished;

        // LZW 字典:key = (prefix << 8) | suffix,value = 码字 + 1(0 = 空)。
        // 单数组随机读(4MB)缓存友好;每帧 4MB memset 代价很小。
        // 只在 WriteQuantizedFrame(顺序写入)中使用,不并发。
        private readonly int[] _lzwTable = new int[1 << 20];
        private readonly List<byte> _lzwOut = new List<byte>();

        public GifWriter(Stream stream, int logicalWidth, int logicalHeight)
        {
            _writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true);
            _writer.Write(Encoding.ASCII.GetBytes("GIF89a"));
            _writer.Write((ushort)logicalWidth);
            _writer.Write((ushort)logicalHeight);
            _writer.Write((byte)0x00); // packed:无全局色表(每帧局部色表)
            _writer.Write((byte)0x00); // 背景色索引
            _writer.Write((byte)0x00); // 像素宽高比
        }

        /// <summary>
        /// 量化一帧(线程安全,可并行)。调用方负责在帧 Image 存活期间调用,
        /// 返回的 QuantizedFrame 是独立数据,与帧 Image 无引用关系。
        /// </summary>
        public QuantizedFrame QuantizeFrame<TPixel>(Image<TPixel> frame, int delayCentiseconds)
            where TPixel : unmanaged, IPixel<TPixel>
        {
            var quantizer = new OctreeQuantizer(new QuantizerOptions { MaxColors = 256 });
            using (var q = quantizer.CreatePixelSpecificQuantizer<TPixel>(Configuration.Default))
            {
                var rootFrame = frame.Frames.RootFrame;
                q.AddPaletteColors(new Buffer2DRegion<TPixel>(rootFrame.PixelBuffer));

                using (var indexed = q.QuantizeFrame(rootFrame, new Rectangle(0, 0, frame.Width, frame.Height)))
                {
                    var palette = indexed.Palette;
                    int palSize = NextPow2(Math.Max(2, palette.Length));
                    int nbits = 0;
                    for (int v = palSize; v > 1; v >>= 1) nbits++;

                    // 透明检测:仅当像素类型带 alpha 通道时扫描(动画纹理多为 R8/RG88 无 alpha,直接跳过)
                    byte transparentIndex = 0;
                    bool hasTransparent = false;
                    if (frame.PixelType.AlphaRepresentation != PixelAlphaRepresentation.None)
                    {
                        for (int y = 0; y < frame.Height && !hasTransparent; y++)
                        {
                            var row = rootFrame.PixelBuffer.DangerousGetRowSpan(y);
                            for (int x = 0; x < frame.Width; x++)
                            {
                                var px = new Rgba32();
                                row[x].ToRgba32(ref px);
                                if (px.A < 128)
                                {
                                    transparentIndex = q.GetQuantizedColor(row[x], out _);
                                    hasTransparent = true;
                                    break;
                                }
                            }
                        }
                    }

                    // 像素索引与调色板独立拷贝(IndexedImageFrame 释放后仍有效)
                    int pixelCount = frame.Width * frame.Height;
                    var indices = new byte[pixelCount];
                    for (int y = 0; y < frame.Height; y++)
                        indexed.DangerousGetRowSpan(y)
                            .CopyTo(new Span<byte>(indices, y * frame.Width, frame.Width));

                    var rgb = new byte[palSize * 3];
                    for (int i = 0; i < palSize; i++)
                    {
                        var c = i < palette.Length ? palette.Span[i] : palette.Span[0];
                        var rgba = new Rgba32();
                        c.ToRgba32(ref rgba);
                        rgb[i * 3] = rgba.R;
                        rgb[i * 3 + 1] = rgba.G;
                        rgb[i * 3 + 2] = rgba.B;
                    }

                    return new QuantizedFrame(frame.Width, frame.Height, delayCentiseconds, indices,
                        rgb, nbits, hasTransparent, transparentIndex);
                }
            }
        }

        /// <summary>
        /// 按帧顺序写入一个量化结果(仅限单线程顺序调用,内部 LZW 表不并发)。
        /// </summary>
        public void WriteQuantizedFrame(QuantizedFrame qf)
        {
            if (_finished) throw new InvalidOperationException("GifWriter already finished");

            // Graphic Control Extension(disposal=1 帧覆盖)
            _writer.Write((byte)0x21);
            _writer.Write((byte)0xF9);
            _writer.Write((byte)0x04);
            _writer.Write((byte)(0x04 | (qf.HasTransparent ? 0x01 : 0x00)));
            _writer.Write((ushort)qf.DelayCentiseconds);
            _writer.Write(qf.HasTransparent ? qf.TransparentIndex : (byte)0);
            _writer.Write((byte)0x00);

            // Image Descriptor + Local Color Table
            _writer.Write((byte)0x2C);
            _writer.Write((ushort)0);
            _writer.Write((ushort)0);
            _writer.Write((ushort)qf.Width);
            _writer.Write((ushort)qf.Height);
            _writer.Write((byte)(0x80 | (qf.Nbits - 1)));

            _writer.Write(qf.RgbPalette);

            WriteLzw(qf.Indices, qf.Width * qf.Height, qf.Nbits);
        }

        /// <summary>顺序单帧入口(内部 = 量化 + 写),供简单场景与测试使用。</summary>
        public void WriteFrame<TPixel>(Image<TPixel> frame, int delayCentiseconds)
            where TPixel : unmanaged, IPixel<TPixel>
        {
            WriteQuantizedFrame(QuantizeFrame(frame, delayCentiseconds));
        }

        /// <summary>写完所有帧后调用:写 trailer。Dispose 也会自动调用。</summary>
        public void Finish()
        {
            if (_finished) return;
            _finished = true;
            _writer.Write((byte)0x3B);
            _writer.Flush();
        }

        public void Dispose()
        {
            Finish();
            _writer.Dispose();
        }

        private void WriteLzw(byte[] indices, int pixelCount, int minCodeSize)
        {
            int clearCode = 1 << minCodeSize;
            int endCode = clearCode + 1;
            int codeSize = minCodeSize + 1;
            int nextCode = endCode + 1;
            int maxCode = (1 << codeSize) - 1;

            _lzwOut.Clear();
            if (_lzwOut.Capacity < pixelCount)
                _lzwOut.Capacity = pixelCount;
            Array.Clear(_lzwTable, 0, _lzwTable.Length);
            int bitBuf = 0, bitCnt = 0;

            void WriteCode(int code)
            {
                bitBuf |= code << bitCnt;
                bitCnt += codeSize;
                while (bitCnt >= 8)
                {
                    _lzwOut.Add((byte)(bitBuf & 0xFF));
                    bitBuf >>= 8;
                    bitCnt -= 8;
                }
            }

            void FlushBits()
            {
                while (bitCnt > 0)
                {
                    _lzwOut.Add((byte)(bitBuf & 0xFF));
                    bitBuf >>= 8;
                    bitCnt -= 8;
                }
            }

            int Find(int cur, int k)
            {
                return _lzwTable[(cur << 8) | k] - 1;
            }

            WriteCode(clearCode);
            int code = indices[0];
            for (int i = 1; i < indices.Length; i++)
            {
                int k = indices[i];
                int f = Find(code, k);
                if (f >= 0)
                {
                    code = f;
                }
                else
                {
                    WriteCode(code);

                    _lzwTable[(code << 8) | k] = nextCode + 1;
                    nextCode++;
                    code = k;

                    // 码宽切换与解码端同步:编码端字典比解码端多 1 条
                    // (解码端对 clear 后第一个码不创建条目,编码端第一个未找到即分配),
                    // 因此加宽条件晚 1 个条目:nextCode > maxCode + 1。
                    if (codeSize < 12 && nextCode > maxCode + 1)
                    {
                        codeSize++;
                        maxCode = (1 << codeSize) - 1;
                    }
                    else if (codeSize == 12 && nextCode > maxCode)
                    {
                        // 12 位字典满(nextCode = 4096):输出 clear 并重置
                        WriteCode(clearCode);
                        Array.Clear(_lzwTable, 0, _lzwTable.Length);
                        nextCode = endCode + 1;
                        codeSize = minCodeSize + 1;
                        maxCode = (1 << codeSize) - 1;
                    }
                }
            }
            WriteCode(code);
            WriteCode(endCode);
            FlushBits();

            // 子块输出:minCodeSize + 数据子块(每块 ≤ 255 字节) + 终止 0
            _writer.Write((byte)minCodeSize);
            int pos = 0;
            while (pos < _lzwOut.Count)
            {
                int n = Math.Min(255, _lzwOut.Count - pos);
                _writer.Write((byte)n);
                for (int i = 0; i < n; i++) _writer.Write(_lzwOut[pos + i]);
                pos += n;
            }
            _writer.Write((byte)0);
        }

        private static int NextPow2(int n)
        {
            int p = 1;
            while (p < n) p <<= 1;
            return p;
        }
    }
}
