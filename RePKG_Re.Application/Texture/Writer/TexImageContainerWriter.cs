using System;
using System.IO;
using RePKG_Re.Application.Exceptions;
using RePKG_Re.Core.Texture;

namespace RePKG_Re.Application.Texture
{
    public class TexImageContainerWriter : ITexImageContainerWriter
    {
        private readonly ITexImageWriter _texImageWriter;

        public TexImageContainerWriter(ITexImageWriter texImageWriter)
        {
            _texImageWriter = texImageWriter;
        }

        public void WriteTo(BinaryWriter writer, ITexImageContainer imageContainer)
        {
            if (writer == null) throw new ArgumentNullException(nameof(writer));
            if (imageContainer == null) throw new ArgumentNullException(nameof(imageContainer));
            
            writer.WriteNString(imageContainer.Magic);
            writer.Write(imageContainer.Images.Count);

            // 尾部字段按魔数决定，不能按 ImageContainerVersion：读侧把 TEXB0004+非 MP4 降级成
            // Version3 的 mip 记录，但 Magic 仍是 "TEXB0004"。若按版本分支，读进来的包再写出去
            // 就少写一个 int32，整条 TEX 从 mip 起错位。
            switch (imageContainer.Magic)
            {
                case "TEXB0001":
                case "TEXB0002":
                    break;

                case "TEXB0003":
                    writer.Write((int) imageContainer.ImageFormat);
                    break;

                case "TEXB0004":
                    writer.Write((int) imageContainer.ImageFormat);
                    // isVideoMp4 不再单独存字段，读侧是从 ImageFormat==FIF_MP4 反推的，这里正向推回去
                    writer.Write(imageContainer.ImageFormat == FreeImageFormat.FIF_MP4 ? 1 : 0);
                    break;

                default:
                    throw new UnknownMagicException(nameof(TexImageContainerWriter), imageContainer.Magic);
            }
            
            foreach (var image in imageContainer.Images)
            {
                _texImageWriter.WriteTo(writer, imageContainer.ImageContainerVersion, image);
            }
        }
    }
}