// Polyfill: netstandard2.0 has no IsExternalInit, which the compiler requires for
// `init`-only properties and positional records. Declaring it here lets the engine use
// modern immutable types while still targeting the older framework Civil 3D may host.
namespace System.Runtime.CompilerServices
{
    using System.ComponentModel;

    [EditorBrowsable(EditorBrowsableState.Never)]
    internal static class IsExternalInit { }
}
