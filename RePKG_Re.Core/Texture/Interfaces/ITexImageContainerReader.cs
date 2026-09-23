using System.IO;

namespace RePKG_Re.Core.Texture
{
    public interface ITexImageContainerReader
    {
        /// <summary>
        /// readPixels=false 时只读结构:mip 记录照读(尺寸/LZ4 标记/载荷长度都拿得到),像素载荷按长度跳过。
        /// onlyImage&gt;=0 时只有这个下标的 image 装像素,其余跳过 —— 一张图集多条 image 时用它把峰值内存压到单张的量级。
        /// </summary>
        ITexImageContainer ReadFrom(
            BinaryReader reader,
            TexFormat texFormat,
            bool readPixels = true,
            int onlyImage = -1);
    }
}