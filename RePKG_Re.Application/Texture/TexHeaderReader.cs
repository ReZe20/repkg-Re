using System;
using System.IO;
using RePKG_Re.Application.Exceptions;
using RePKG_Re.Core.Texture;

namespace RePKG_Re.Application.Texture
{
    public class TexHeaderReader : ITexHeaderReader
    {
        public ITexHeader ReadFrom(BinaryReader reader)
        {
            if (reader == null) throw new ArgumentNullException(nameof(reader));
            
            var header = new TexHeader
            {
                Format = (TexFormat) reader.ReadInt32(),
                Flags = (TexFlags) reader.ReadInt32(),
                TextureWidth = reader.ReadInt32(),
                TextureHeight = reader.ReadInt32(),
                ImageWidth = reader.ReadInt32(),
                ImageHeight = reader.ReadInt32(),
                UnkInt0 = reader.ReadUInt32()
            };

            if (!header.Format.IsValid())
                throw new EnumNotValidException<TexFormat>(header.Format);
            
            return header;
        }
    }
}