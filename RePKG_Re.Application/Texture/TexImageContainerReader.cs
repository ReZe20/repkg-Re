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

        public ITexImageContainer ReadFrom(
            BinaryReader reader,
            TexFormat texFormat,
            bool readPixels = true,
            int onlyImage = -1)
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
            // 读完后再并行解压 mipmap(解压是纯 CPU 大头,多张源图互不依赖)。
            _texImageReader.DecompressMipmapBytes = false;
            try
            {
                for (var i = 0; i < imageCount; i++)
                {
                    // onlyImage 挑中哪张就只给哪张装像素:一张图集五条 7680×7560 的 DXT 全解码驻留就是 1.1GB,
                    // 逐张读能把峰值压到单张的量级。没挑中的那张只走 mip 记录,载荷按长度 Seek 掉。
                    _texImageReader.ReadMipmapBytes = readPixels && (onlyImage < 0 || i == onlyImage);
                    container.Images.Add(_texImageReader.ReadFrom(reader, container, texFormat));
                }
            }
            finally
            {
                _texImageReader.DecompressMipmapBytes = true;
                _texImageReader.ReadMipmapBytes = true;
            }

            // readPixels=false 时上面读到的只有 mip 记录(尺寸/载荷长度),像素是 null:
            // 既没解压也就没有"释放压缩块"这一步可做,直接交回给调用方判方向。
            if (!readPixels)
                return container;

            // 并行解压(DXT 解压是纯 CPU 大头,多张源图互不依赖;
            // 任务进 .NET 全局线程池,多 pkg 并行时自动分核)
            var mipmaps = new List<ITexMipmap>();
            foreach (var image in container.Images)
            foreach (var mip in image.Mipmaps)
            {
                if (mip.Bytes == null) continue;   // onlyImage 没挑中的那些:没有像素也就没有要解压的东西
                mipmaps.Add(mip);
            }

            if (mipmaps.Count > 1)
            {
                // 解压并行度按核数自适应。原先写死 2,在 8K 图集文件上把解压锁死在 2 路
                // (实测 read 1.13s);改 8 路后 0.44s(-61%)。上限 8 与 GIF 帧并行度保持一致,
                // 避免与 batch 的多个 worker 叠加后过度超订。
                // 内存代价实测很小(8K 图集:峰值 +125MB):解压结果本来就要全部驻留,
                // 提高并行只多出同时在场的临时缓冲。
                var decompressParallelism = Environment.ProcessorCount / 2;
                if (decompressParallelism < 2) decompressParallelism = 2;
                if (decompressParallelism > 8) decompressParallelism = 8;
                if (decompressParallelism > mipmaps.Count) decompressParallelism = mipmaps.Count;

                using (var semaphore = new SemaphoreSlim(decompressParallelism))
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