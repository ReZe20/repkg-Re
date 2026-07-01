using System.Collections.Generic;

namespace RePKG_Re.Core.Texture
{
    public interface ITexFrameInfoContainer
    {
        string Magic { get; set; }
        IList<ITexFrameInfo> Frames { get; }
        int GifWidth { get; set; }
        int GifHeight { get; set; }
    }
}