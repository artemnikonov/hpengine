// Compiler shim for `record`/`init` under the frontend profile.
//
// Core is kept buildable from source under netstandard2.1 + C#9. Positional `record` types are C#9 and legal, but
// their `init` setters need the marker type System.Runtime.CompilerServices.IsExternalInit, which netstandard2.1
// lacks. Headless (latest C#) gets it from the BCL, so the mismatch shows only on the frontend profile.
// Needed because the mesh/animation catalog records in Core are positional records.
//
// The type is internal: it stays out of the public surface and does not conflict if another assembly declares
// the same shim.
namespace System.Runtime.CompilerServices
{
    internal static class IsExternalInit { }
}
