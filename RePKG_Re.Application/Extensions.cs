using System;
using System.IO;
using System.Text;
using RePKG_Re.Application.Exceptions;

namespace RePKG_Re.Application
{
    internal static class Extensions
    {
        public static string ReadNString(this BinaryReader reader, int maxLength = -1)
        {
            if (reader == null) throw new ArgumentNullException(nameof(reader));
            
            var builder = new StringBuilder(maxLength <= 0 ? 16 : maxLength);
            var c = reader.ReadChar();

            while (c != '\0' && (maxLength == -1 || builder.Length < maxLength))
            {
                builder.Append(c);
                c = reader.ReadChar();
            }

            return builder.ToString();
        }

        public static void WriteNString(this BinaryWriter writer, string input)
        {
            if (writer == null) throw new ArgumentNullException(nameof(writer));
            if (input == null) throw new ArgumentNullException(nameof(input));

            writer.Write(Encoding.UTF8.GetBytes(input));
            writer.Write((byte) 0);
        }

        public static string ReadStringI32Size(this BinaryReader reader, int maxLength = -1)
        {
            if (reader == null) throw new ArgumentNullException(nameof(reader));

            var size = reader.ReadInt32();

            if (size < 0)
                throw new UnsafePkgException($"Size cannot be negative: {size}");

            // 只能报错，不能 Math.Min 截断：少读的字节会留在流里被当成后续字段，
            // 于是整张条目表从这条起错位，offset/length 变成垃圾值而且不抛异常
            if (maxLength > -1 && size > maxLength)
                throw new UnsafePkgException($"String size {size} exceeds the limit of {maxLength}");

            var bytes = reader.ReadBytes(size);

            return Encoding.UTF8.GetString(bytes);
        }

        public static void WriteStringI32Size(this BinaryWriter writer, string input)
        {
            if (writer == null) throw new ArgumentNullException(nameof(writer));
            if (input == null) throw new ArgumentNullException(nameof(input));

            // 长度字段必须是 UTF-8 字节数：string.Length 数的是 UTF-16 code unit，
            // 非 ASCII 名字（中文素材名）会让读侧少读，剩余字节被当成后续字段，整张条目表错位
            writer.Write(Encoding.UTF8.GetByteCount(input));
            writer.Write(Encoding.UTF8.GetBytes(input));
        }
    }
}