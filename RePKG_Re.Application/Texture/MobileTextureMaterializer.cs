using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using RePKG_Re.Core.Texture;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace RePKG_Re.Application.Texture
{
    /// <summary>物化结果的动作。</summary>
    public enum MaterializeAction
    {
        /// <summary>本来就是原始像素/视频，原样搬运</summary>
        Copy,
        /// <summary>直通编码图已解码成 RGBA8</summary>
        Materialized,
        /// <summary>拒绝物化（超尺寸上限/解码失败），调用方应按 Copy 处理并上报</summary>
        Refused
    }

    public class MaterializeResult
    {
        public MaterializeAction Action { get; set; }
        /// <summary>要写进条目的字节（Refused 时等于原字节）</summary>
        public byte[] Bytes { get; set; }
        public string Reason { get; set; }
        /// <summary>物化了几条 image（帧）</summary>
        public int Images { get; set; }
        public int Width { get; set; }
        public int Height { get; set; }
    }

    /// <summary>
    /// 把 PC 包里"直通编码图"（TEX 容器 imageFormat != FIF_UNKNOWN，载荷是一段 PNG/JPEG 字节）
    /// 重写成移动端要的原始 RGBA8。移动端按 fmt0 ⇒ w*h*4 读载荷，喂编码图会整张纹理作废。
    ///
    /// 已真机/逐字节定死的口径，改动前先看这些证据：
    /// - alpha 是**直色**（非预乘）：17/17 条与 WE 移动导出逐字节相同，预乘每条差 8 万字节。
    ///   所以解码后原样写字节，不做任何 alpha/色彩变换。
    /// - 写出容器用 TEXB0004 + mipCnt=1 + 真实字节数；DXT/R8/RG88/原始像素/内嵌 mp4 一律不碰。
    /// - LZ4 逐条目择优：WE 对 180×180 小图标写 lz=0，只对 10.9MB 大图压。
    /// </summary>
    public class MobileTextureMaterializer
    {
        /// <summary>RGBA8 一像素 4 字节；单条 mip 的字节数上限沿用 TEX 读取侧那道闸</summary>
        private const int BytesPerPixel = 4;

        private readonly ITexReader _texReader = TexReader.Default;
        private readonly ITexWriter _texWriter = TexWriter.Default;
        private readonly ITexMipmapCompressor _compressor = new TexMipmapCompressor();

        public bool UseLz4 { get; set; } = true;

        public MaterializeResult Materialize(byte[] texBytes)
        {
            if (texBytes == null || texBytes.Length < 8)
                return Copy(texBytes, "too small to be a TEX");

            ITex tex;
            try
            {
                using var reader = new BinaryReader(new MemoryStream(texBytes), Encoding.UTF8);
                tex = _texReader.ReadFrom(reader);
            }
            catch (Exception e)
            {
                return Copy(texBytes, $"TEX 解析失败，原样搬运: {e.GetType().Name} {e.Message}");
            }

            var container = tex.ImagesContainer;
            if (container == null || container.Images.Count == 0)
                return Copy(texBytes, "无 image 容器");

            // 视频纹理的载荷是内嵌 mp4，解码成像素会把它直接写坏 —— 只按 flags 判，不看 ifmt/isVid
            if (tex.IsVideoTexture)
                return Copy(texBytes, "视频纹理，原样搬运");

            if (container.ImageFormat == FreeImageFormat.FIF_UNKNOWN)
                return Copy(texBytes, "已是原始像素/DX 块格式");

            var mip0 = container.Images[0].FirstMipmap;
            if (mip0 == null || mip0.Bytes == null || mip0.Bytes.Length == 0)
                return Copy(texBytes, "首 mip 无载荷");

            var output = new Tex
            {
                Magic1 = tex.Magic1,
                Magic2 = tex.Magic2,
                Header = new TexHeader
                {
                    Format = TexFormat.RGBA8888,
                    Flags = tex.Header.Flags,
                    UnkInt0 = tex.Header.UnkInt0
                },
                ImagesContainer = new TexImageContainer
                {
                    Magic = "TEXB0004",
                    // 读侧对 TEXB0004+非 MP4 会降级成 Version3 的 mip 记录，这里保持一致才能写出/读回闭合
                    ImageContainerVersion = TexImageContainerVersion.Version3,
                    ImageFormat = FreeImageFormat.FIF_UNKNOWN
                },
                FrameInfoContainer = tex.FrameInfoContainer
            };

            int width = 0, height = 0;
            foreach (var image in container.Images)
            {
                var source = image.FirstMipmap;
                if (source?.Bytes == null || source.Bytes.Length == 0)
                    return Copy(texBytes, "某帧无载荷，整条目放弃物化");

                var decoded = DecodeRgba(source.Bytes);
                if (decoded.Pixels == null)
                    return Copy(texBytes, $"解码失败: {decoded.Reason}");

                width = decoded.Width;
                height = decoded.Height;

                var needed = (long) width * height * BytesPerPixel;
                if (needed > Constants.MaximumMipmapByteCount)
                    return Refused($"{width}x{height} RGBA8 = {needed}B 超过上限 {Constants.MaximumMipmapByteCount}B");

                var mip = new TexMipmap
                {
                    Width = width,
                    Height = height,
                    Format = MipmapFormat.RGBA8888,
                    Bytes = decoded.Pixels,
                    DecompressedBytesCount = decoded.Pixels.Length,
                    IsLZ4Compressed = false
                };

                if (UseLz4)
                    CompressIfItPays(mip);

                output.ImagesContainer.Images.Add(new TexImage {Mipmaps = {mip}});
            }

            output.Header.TextureWidth = width;
            output.Header.TextureHeight = height;
            output.Header.ImageWidth = width;
            output.Header.ImageHeight = height;

            using var stream = new MemoryStream();
            using (var writer = new BinaryWriter(stream, Encoding.UTF8, true))
            {
                _texWriter.WriteTo(writer, output);
            }

            return new MaterializeResult
            {
                Action = MaterializeAction.Materialized,
                Bytes = stream.ToArray(),
                Images = output.ImagesContainer.Images.Count,
                Width = width,
                Height = height
            };
        }

        private readonly struct DecodeOutcome
        {
            public byte[] Pixels { get; }
            public int Width { get; }
            public int Height { get; }
            public string Reason { get; }

            public DecodeOutcome(byte[] pixels, int width, int height, string reason)
            {
                Pixels = pixels;
                Width = width;
                Height = height;
                Reason = reason;
            }
        }

        /// <summary>
        /// 解成直色 RGBA8。尺寸一律取解码结果，不信 TEX 头部记录 —— 头部的 tw/th 可能是块对齐后的值。
        /// </summary>
        private static DecodeOutcome DecodeRgba(byte[] encoded)
        {
            try
            {
                using var image = Image.Load<Rgba32>(encoded);
                var w = image.Width;
                var h = image.Height;
                if (w <= 0 || h <= 0)
                    return new DecodeOutcome(null, 0, 0, $"非法尺寸 {w}x{h}");

                var pixels = new byte[w * h * BytesPerPixel];
                image.CopyPixelDataTo(MemoryMarshal.Cast<byte, Rgba32>(pixels.AsSpan()));
                return new DecodeOutcome(pixels, w, h, null);
            }
            catch (Exception e)
            {
                return new DecodeOutcome(null, 0, 0, $"{e.GetType().Name} {e.Message}");
            }
        }

        /// <summary>LZ4 只在真的变小时采用；WE 对小图标就是不压，跟着它走。</summary>
        private void CompressIfItPays(TexMipmap mip)
        {
            var raw = mip.Bytes;

            try
            {
                _compressor.CompressMipmap(mip, MipmapFormat.RGBA8888, true);
            }
            catch (Exception)
            {
                Revert(mip, raw);
                return;
            }

            if (mip.Bytes == null || mip.Bytes.Length >= raw.Length)
                Revert(mip, raw);
        }

        private static void Revert(TexMipmap mip, byte[] raw)
        {
            mip.Bytes = raw;
            mip.IsLZ4Compressed = false;
            mip.DecompressedBytesCount = raw.Length;
        }

        private static MaterializeResult Copy(byte[] bytes, string reason)
            => new MaterializeResult {Action = MaterializeAction.Copy, Bytes = bytes, Reason = reason};

        private static MaterializeResult Refused(string reason)
            => new MaterializeResult {Action = MaterializeAction.Refused, Bytes = null, Reason = reason};
    }
}
