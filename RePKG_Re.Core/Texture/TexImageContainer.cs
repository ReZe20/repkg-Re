using System.Collections.Generic;

namespace RePKG_Re.Core.Texture
{
    public class TexImageContainer : ITexImageContainer
    {
        public string Magic { get; set; }
        public FreeImageFormat ImageFormat { get; set; } = FreeImageFormat.FIF_UNKNOWN;
        public IList<ITexImage> Images { get; } = new List<ITexImage>();
        
        public TexImageContainerVersion ImageContainerVersion { get; set; }
    }
}