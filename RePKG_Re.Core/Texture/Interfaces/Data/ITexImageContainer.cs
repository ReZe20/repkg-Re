using System.Collections.Generic;

namespace RePKG_Re.Core.Texture
{
    public interface ITexImageContainer
    {
        string Magic { get; set; }
        FreeImageFormat ImageFormat { get; set; }
        IList<ITexImage> Images { get; }
        TexImageContainerVersion ImageContainerVersion { get; set; }
    }
}