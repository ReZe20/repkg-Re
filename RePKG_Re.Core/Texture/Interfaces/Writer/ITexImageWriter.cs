using System.IO;

namespace RePKG_Re.Core.Texture
{
    public interface ITexImageWriter
    {
        void WriteTo(BinaryWriter writer, TexImageContainerVersion containerVersion, ITexImage image);
    }
}