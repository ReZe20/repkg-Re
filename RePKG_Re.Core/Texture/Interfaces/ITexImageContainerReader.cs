using System.IO;

namespace RePKG_Re.Core.Texture
{
    public interface ITexImageContainerReader
    {
        ITexImageContainer ReadFrom(BinaryReader reader, TexFormat texFormat);
    }
}