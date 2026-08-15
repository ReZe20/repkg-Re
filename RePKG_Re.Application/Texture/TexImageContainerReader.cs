using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using RePKG_Re.Application.Exceptions;
using RePKG_Re.Core.Texture;

namespace RePKG_Re.Application.Texture
{
    public class TexImageContainerReader : ITexImageContainerReader
    {
        private readonly ITexImageReader _texImageReader;

        public TexImageContainerReader(ITexImageReader texImageReader)
        {
            _texImageReader = texImageReader;
        }

        public ITexImageContainer ReadFrom(BinaryReader reader, TexFormat texFormat)
        {
            if (reader == null) throw new ArgumentNullException(nameof(reader));

            if (!texFormat.IsValid())
                throw new EnumNotValidException<TexFormat>(texFormat);

            var container = new TexImageContainer
            {
                Magic = reader.ReadNString(maxLength: 16)
            };

            var imageCount = reader.ReadInt32();

            if (imageCount > Constants.MaximumImageCount)
                throw new UnsafeTexException(
                    $"Image count exceeds limit: {imageCount}/{Constants.MaximumImageCount}");

            switch (container.Magic)
            {
                case "TEXB0001":
                case "TEXB0002":
                    break;
                case "TEXB0003":
                    container.ImageFormat = (FreeImageFormat) reader.ReadInt32();
                    break;
                case "TEXB0004":
                    var format = (FreeImageFormat)reader.ReadInt32();
                    var isVideoMp4 = reader.ReadInt32() == 1;
                    if (format == FreeImageFormat.FIF_UNKNOWN)
                    {
                        if (isVideoMp4)
                        {
                            format = FreeImageFormat.FIF_MP4;
                        }
                    }
                    container.ImageFormat = format;
                    break;
                default:
                    throw new UnknownMagicException(nameof(TexImageContainerReader), container.Magic);
            }

            int version = Convert.ToInt32(container.Magic.Substring(4));
            container.ImageContainerVersion = (TexImageContainerVersion)version;

            if(container.ImageContainerVersion == TexImageContainerVersion.Version4
                && container.ImageFormat != FreeImageFormat.FIF_MP4)
            {
                container.ImageContainerVersion = TexImageContainerVersion.Version3;
            }
            
            if (!container.ImageFormat.IsValid())
                throw new EnumNotValidException<FreeImageFormat>(container.ImageFormat);

            // 先顺序读取全部 image(BinaryReader 流位置依赖,必须串行),
            // 读完后再并行解压 mipmap(限流 2:8K 大图解压结果 116MB/张,避免同时驻留过多)。
            _texImageReader.DecompressMipmapBytes = false;
            try
            {
                for (var i = 0; i < imageCount; i++)
                {
                    container.Images.Add(_texImageReader.ReadFrom(reader, container, texFormat));
                }
            }
            finally
            {
                _texImageReader.DecompressMipmapBytes = true;
            }

            // 并行解压(DXT 解压是纯 CPU 大头,多张源图互不依赖;
            // 任务进 .NET 全局线程池,多 pkg 并行时自动分核)
            var mipmaps = new List<ITexMipmap>();
            foreach (var image in container.Images)
                mipmaps.AddRange(image.Mipmaps);

            if (mipmaps.Count > 1)
            {
                using (var semaphore = new SemaphoreSlim(2))
                {
                    var tasks = new List<Task>(mipmaps.Count);
                    foreach (var mipmap in mipmaps)
                    {
                        semaphore.Wait();
                        tasks.Add(Task.Run(() =>
                        {
                            try
                            {
                                _texImageReader.DecompressMipmap(mipmap);
                            }
                            finally
                            {
                                semaphore.Release();
                            }
                        }));
                    }
                    Task.WhenAll(tasks).GetAwaiter().GetResult();
                }
            }
            else if (mipmaps.Count == 1)
            {
                _texImageReader.DecompressMipmap(mipmaps[0]);
            }

            return container;
        }
    }
}