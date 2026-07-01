using System.IO;

namespace RePKG_Re.Core.Texture
{
    public interface ITexImageContainerWriter
    {
        void WriteTo(BinaryWriter writer, ITexImageContainer imageContainer);
    }
}