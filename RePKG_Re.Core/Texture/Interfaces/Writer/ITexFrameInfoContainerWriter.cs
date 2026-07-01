using System.IO;

namespace RePKG_Re.Core.Texture
{
    public interface ITexFrameInfoContainerWriter
    {
        void WriteTo(BinaryWriter writer, ITexFrameInfoContainer frameInfoContainer);
    }
}