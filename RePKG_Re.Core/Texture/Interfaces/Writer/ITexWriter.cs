using System.IO;

namespace RePKG_Re.Core.Texture
{
    public interface ITexWriter
    {
        void WriteTo(BinaryWriter writer, ITex tex);
    }
}