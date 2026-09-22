using System;

namespace RePKG_Re.Application.Exceptions
{
    /// <summary>
    /// Thrown on unorganic pkg header values.
    /// For example a name length field that no legal package could contain
    /// </summary>
    public class UnsafePkgException : Exception
    {
        public UnsafePkgException(string reason) : base($"Unsafe PKG detected, reason: {reason}")
        {}
    }
}
