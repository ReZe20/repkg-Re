using System.IO;

namespace RePKG_Re.Core.Texture
{
    public interface ITexReader
    {
        /// <summary>
        /// readPixels=false 时只走过结构(头部/容器/mip 记录/帧信息)，像素载荷按长度跳过。
        /// onlyImage&gt;=0 时只给该下标的 image 装像素，其余照旧跳过。
        /// </summary>
        ITex ReadFrom(BinaryReader reader, bool readPixels = true, int onlyImage = -1);
    }
}