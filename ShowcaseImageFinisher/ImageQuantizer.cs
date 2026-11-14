using System.IO;
using System.Runtime.InteropServices;

namespace ShowcaseImageFinisher;

/// <summary>
/// High-quality palette quantization through libimagequant
/// (libimagequant.dll), via P/Invoke. The function signatures and the liq_palette layout
/// follow the library's public header (verified against 2.18.0 and
/// 4.4.1, whose C API is identical for the calls used here).
///
/// The DLL is NOT bundled: place libimagequant.dll next to the exe
/// (e.g. from the MSYS2 package mingw-w64-x86_64-libimagequant).
/// When the DLL is missing, callers fall back to the built-in
/// quantizer in <see cref="PngQuantizer"/>.
/// License note: libimagequant is GPLv3 (or commercial).
/// </summary>
public static class ImageQuantizer
{
    private const string Dll = "libimagequant";

    private static readonly Lazy<bool> Available = new(() =>
        Diagnose().StartsWith("利用できます", StringComparison.Ordinal));

    public static bool IsAvailable => Available.Value;

    /// <summary>Human-readable diagnosis of the DLL situation.</summary>
    public static string Diagnose()
    {
        var dir = AppContext.BaseDirectory;
        var dll = Path.Combine(dir, "libimagequant.dll");
        if (!File.Exists(dll))
            return $"libimagequant.dll が exe のフォルダにありません（{dir}）";
        var arch = PeArchitecture(dll);
        var process = RuntimeInformation.ProcessArchitecture;
        if (arch is not null && arch != process.ToString())
            return $"libimagequant.dll が {arch} 版のため読み込めません（このアプリは {process} 版）";
        try
        {
            var v = LiqVersion();
            return $"利用できます（libimagequant {v / 10000}.{v / 100 % 100}.{v % 100}）";
        }
        catch (Exception ex)
        {
            return $"libimagequant.dll はありますが読み込めませんでした: {ex.GetType().Name}: {ex.Message}";
        }
    }

    private static string? PeArchitecture(string path)
    {
        try
        {
            using var fs = File.OpenRead(path);
            using var br = new BinaryReader(fs);
            fs.Seek(0x3C, SeekOrigin.Begin);
            var peOffset = br.ReadInt32();
            fs.Seek(peOffset + 4, SeekOrigin.Begin);
            return br.ReadUInt16() switch
            {
                0x8664 => "X64",
                0x14C => "X86",
                0xAA64 => "Arm64",
                _ => null,
            };
        }
        catch { return null; }
    }

    /// <summary>Quantizes RGBA with libimagequant and returns indexed-PNG bytes.</summary>
    public static byte[] EncodeQuantizedPng(byte[] rgba, int w, int h, int maxColors = 256,
        int minPosterization = 0, float ditheringLevel = 1.0f)
    {
        var attr = LiqAttrCreate();
        if (attr == IntPtr.Zero)
            throw new InvalidOperationException("libimagequant の初期化に失敗しました");
        IntPtr image = IntPtr.Zero, result = IntPtr.Zero;
        var pin = GCHandle.Alloc(rgba, GCHandleType.Pinned);
        try
        {
            Check(LiqSetMaxColors(attr, maxColors), "liq_set_max_colors");
            Check(LiqSetQuality(attr, 0, 100), "liq_set_quality");
            if (minPosterization > 0)
                Check(LiqSetMinPosterization(attr, minPosterization), "liq_set_min_posterization");
            image = LiqImageCreateRgba(attr, pin.AddrOfPinnedObject(), w, h, 0);
            if (image == IntPtr.Zero)
                throw new InvalidOperationException("libimagequant の画像作成に失敗しました");
            Check(LiqImageQuantize(image, attr, out result), "liq_image_quantize");
            Check(LiqSetDitheringLevel(result, ditheringLevel), "liq_set_dithering_level");

            var palettePtr = LiqGetPalette(result);
            if (palettePtr == IntPtr.Zero)
                throw new InvalidOperationException("libimagequant のパレット取得に失敗しました");
            var count = Marshal.ReadInt32(palettePtr);
            if (count is < 1 or > 256)
                throw new InvalidOperationException($"libimagequant のパレット数が不正です: {count}");
            // liq_palette: uint count, then liq_color entries[256] (r,g,b,a each).
            var palette = new byte[count * 4];
            Marshal.Copy(palettePtr + 4, palette, 0, palette.Length);

            var indices = new byte[w * h];
            var indicesPin = GCHandle.Alloc(indices, GCHandleType.Pinned);
            try
            {
                Check(LiqWriteRemappedImage(result, image,
                    indicesPin.AddrOfPinnedObject(), (UIntPtr)indices.Length),
                    "liq_write_remapped_image");
            }
            finally { indicesPin.Free(); }

            return PngQuantizer.EncodeIndexedPng(palette, indices, w, h);
        }
        finally
        {
            if (result != IntPtr.Zero) LiqResultDestroy(result);
            if (image != IntPtr.Zero) LiqImageDestroy(image);
            LiqAttrDestroy(attr);
            pin.Free();
        }
    }

    private static void Check(int error, string api)
    {
        if (error != 0)
            throw new InvalidOperationException($"libimagequant に失敗しました: {api} (liq_error={error})");
    }

    // ---- libimagequant functions (C API, cdecl) ----

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl, EntryPoint = "liq_version")]
    private static extern int LiqVersion();

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl, EntryPoint = "liq_attr_create")]
    private static extern IntPtr LiqAttrCreate();

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl, EntryPoint = "liq_attr_destroy")]
    private static extern void LiqAttrDestroy(IntPtr attr);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl, EntryPoint = "liq_set_max_colors")]
    private static extern int LiqSetMaxColors(IntPtr attr, int colors);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl, EntryPoint = "liq_set_quality")]
    private static extern int LiqSetQuality(IntPtr attr, int minimum, int maximum);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl, EntryPoint = "liq_set_min_posterization")]
    private static extern int LiqSetMinPosterization(IntPtr attr, int bits);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl, EntryPoint = "liq_image_create_rgba")]
    private static extern IntPtr LiqImageCreateRgba(IntPtr attr, IntPtr bitmap, int width, int height, double gamma);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl, EntryPoint = "liq_image_quantize")]
    private static extern int LiqImageQuantize(IntPtr image, IntPtr attr, out IntPtr result);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl, EntryPoint = "liq_image_destroy")]
    private static extern void LiqImageDestroy(IntPtr image);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl, EntryPoint = "liq_set_dithering_level")]
    private static extern int LiqSetDitheringLevel(IntPtr result, float level);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl, EntryPoint = "liq_get_palette")]
    private static extern IntPtr LiqGetPalette(IntPtr result);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl, EntryPoint = "liq_write_remapped_image")]
    private static extern int LiqWriteRemappedImage(IntPtr result, IntPtr image, IntPtr buffer, UIntPtr bufferSize);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl, EntryPoint = "liq_result_destroy")]
    private static extern void LiqResultDestroy(IntPtr result);
}
