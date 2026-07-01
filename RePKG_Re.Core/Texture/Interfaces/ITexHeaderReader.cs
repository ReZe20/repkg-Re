using System.IO;

namespace RePKG_Re.Core.Texture
{
    public interface ITexHeaderReader
    {
        ITexHeader ReadFrom(BinaryReader reader);
    }
}