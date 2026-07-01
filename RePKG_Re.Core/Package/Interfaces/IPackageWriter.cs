using System.IO;

namespace RePKG_Re.Core.Package.Interfaces
{
    public interface IPackageWriter
    {
        void WriteTo(BinaryWriter writer, Package package);
    }
}