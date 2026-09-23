using System.IO;

namespace RePKG_Re.Core.Texture
{
    public interface ITexImageReader
    {
        ITexImage ReadFrom(
            BinaryReader reader,
            ITexImageContainer container,
            TexFormat texFormat);

        /// <summary>解压单个 mipmap(供容器层批量并行解压使用)。</summary>
        void DecompressMipmap(ITexMipmap mipmap);

        /// <summary>是否在读取时立即解压 mipmap(容器层可临时关闭以批量并行解压)。</summary>
        bool DecompressMipmapBytes { get; set; }

        /// <summary>false = 只走过条目结构、按记录里的长度跳过像素载荷。用于"这条目我根本不碰"的预判定。</summary>
        bool ReadMipmapBytes { get; set; }
    }
}