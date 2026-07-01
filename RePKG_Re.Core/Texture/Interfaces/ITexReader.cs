using System.IO;

namespace RePKG_Re.Core.Texture
{
    public interface ITexReader
    {
        ITex ReadFrom(BinaryReader reader);
    }
}