using System.IO;

namespace RePKG_Re.Core.Texture
{
    public interface ITexImageReader
    {
        ITexImage ReadFrom(
            BinaryReader reader,
            ITexImageContainer container,
            TexFormat texFormat);
    }
}