using System.IO;

namespace RePKG_Re.Core.Package.Interfaces
{
    public interface IPackageReader
    {
        Package ReadFrom(BinaryReader reader);
    }
}