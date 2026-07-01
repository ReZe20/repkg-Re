using System.IO;

namespace RePKG_Re.Core.Texture
{
    public interface ITexFrameInfoContainerReader
    {
        ITexFrameInfoContainer ReadFrom(BinaryReader reader);
    }
}