using System.IO;

namespace RePKG_Re.Core.Texture
{
    public interface ITexHeaderWriter
    {
        void WriteTo(BinaryWriter writer, ITexHeader header);
    }
}