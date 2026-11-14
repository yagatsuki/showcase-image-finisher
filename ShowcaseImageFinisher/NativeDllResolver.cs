using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace ShowcaseImageFinisher;

/// <summary>
/// One shared DLL import resolver for the whole assembly (.NET allows
/// only one per assembly, so per-class resolvers would collide).
/// Native libraries that ship as loose DLLs next to the exe —
/// libjxl (jxl.dll) and libimagequant (libimagequant.dll) — are
/// resolved strictly from the exe's own folder, regardless of the
/// process working directory or PATH. Returning IntPtr.Zero for
/// anything else keeps the default probing for all other libraries.
/// </summary>
internal static class NativeDllResolver
{
    [ModuleInitializer]
    internal static void Init()
    {
        NativeLibrary.SetDllImportResolver(typeof(NativeDllResolver).Assembly, Resolve);
    }

    private static IntPtr Resolve(string libraryName, System.Reflection.Assembly assembly, DllImportSearchPath? searchPath)
    {
        string? file = libraryName switch
        {
            "jxl" => "jxl.dll",
            "libimagequant" => "libimagequant.dll",
            _ => null,
        };
        if (file is null) return IntPtr.Zero;
        var candidate = Path.Combine(AppContext.BaseDirectory, file);
        return File.Exists(candidate) && NativeLibrary.TryLoad(candidate, out var handle)
            ? handle : IntPtr.Zero;
    }
}
