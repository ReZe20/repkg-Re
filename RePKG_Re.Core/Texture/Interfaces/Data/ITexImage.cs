using System.Collections.Generic;

namespace RePKG_Re.Core.Texture
{
    public interface ITexImage
    {
        IList<ITexMipmap> Mipmaps { get; }
        ITexMipmap FirstMipmap { get; }
    };
}