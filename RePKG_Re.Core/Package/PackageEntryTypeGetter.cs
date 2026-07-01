using System.IO;
using RePKG_Re.Core.Package.Enums;

namespace RePKG_Re.Core.Package
{
    public static class PackageEntryTypeGetter
    {
        public static EntryType GetFromFileName(string path)
        {
            var extension = Path.GetExtension(path);

            if (string.IsNullOrWhiteSpace(extension))
                return EntryType.Binary;

            switch (extension.ToLower())
            {
                case ".tex":
                    return EntryType.Tex;

                default:
                    return EntryType.Binary;
            }
        }
    }
}