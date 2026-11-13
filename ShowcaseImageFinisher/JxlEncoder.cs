using System.IO;
using System.Runtime.InteropServices;

namespace ShowcaseImageFinisher;

/// <summary>
/// Lossless JPEG XL encoding through libjxl (jxl.dll), via P/Invoke.
/// The struct layouts and function signatures follow libjxl 0.11's
/// public headers (jxl/encode.h, jxl/codestream_header.h,
/// jxl/color_encoding.h, jxl/types.h). Lossless mode is enabled with
/// JxlEncoderSetFrameLossless, as the 0.11 API prescribes.
///
/// jxl.dll is NOT bundled: place it next to the exe (from the official
/// libjxl releases, https://github.com/libjxl/libjxl/releases).
/// When the DLL is missing, encoding fails with a clear message and
/// PNG / WebP remain available.
/// </summary>
public static class JxlEncoder
{
    private const string Dll = "jxl";

    static JxlEncoder()
    {
        // Resolve jxl.dll strictly from the exe's own folder, regardless of
        // the process working directory or PATH.
        NativeLibrary.SetDllImportResolver(typeof(JxlEncoder).Assembly, (name, asm, path) =>
        {
            if (!name.Equals(Dll, StringComparison.OrdinalIgnoreCase)) return IntPtr.Zero;
            var candidate = Path.Combine(AppContext.BaseDirectory, "jxl.dll");
            return File.Exists(candidate) && NativeLibrary.TryLoad(candidate, out var handle)
                ? handle : IntPtr.Zero;
        });
    }

    public static bool IsAvailable => Diagnose().StartsWith("利用できます", StringComparison.Ordinal);

    /// <summary>Human-readable diagnosis of the jxl.dll situation.</summary>
    public static string Diagnose()
    {
        var dir = AppContext.BaseDirectory;
        var dll = Path.Combine(dir, "jxl.dll");
        var threads = Path.Combine(dir, "jxl_threads.dll");
        if (!File.Exists(dll))
            return $"jxl.dll が exe のフォルダにありません（{dir}）";
        try
        {
            var version = JxlEncoderVersion();
            return $"利用できます（libjxl {version / 1_000_000}.{version / 1000 % 1000}.{version % 1000}）";
        }
        catch (Exception ex)
        {
            var extra = File.Exists(threads) ? "" : "、jxl_threads.dll もありません";
            return $"jxl.dll はありますが読み込めませんでした: {ex.GetType().Name}: {ex.Message}{extra}" +
                   "（VC++ 再頒布可能ランタイムの不足でもこの状態になります）";
        }
    }

    public static byte[] EncodeRgbaLossless(byte[] rgba, int w, int h)
    {
        var enc = JxlEncoderCreate(IntPtr.Zero);
        if (enc == IntPtr.Zero)
            throw new InvalidOperationException("JPEG XL エンコーダの作成に失敗しました");
        try
        {
            var info = new JxlBasicInfo { Padding = new byte[100] };
            JxlEncoderInitBasicInfo(ref info);
            info.Xsize = (uint)w;
            info.Ysize = (uint)h;
            info.BitsPerSample = 8;
            info.ExponentBitsPerSample = 0;
            info.UsesOriginalProfile = 1; // required for true lossless
            info.NumColorChannels = 3;
            info.NumExtraChannels = 1;
            info.AlphaBits = 8;
            info.AlphaExponentBits = 0;
            info.AlphaPremultiplied = 0;
            Check(JxlEncoderSetBasicInfo(enc, ref info), "JxlEncoderSetBasicInfo");

            var color = new JxlColorEncoding
            {
                WhitePointXy = new double[2],
                PrimariesRedXy = new double[2],
                PrimariesGreenXy = new double[2],
                PrimariesBlueXy = new double[2],
                ColorSpace = 0,        // JXL_COLOR_SPACE_RGB
                WhitePoint = 1,        // JXL_WHITE_POINT_D65
                Primaries = 1,         // JXL_PRIMARIES_SRGB
                TransferFunction = 13, // JXL_TRANSFER_FUNCTION_SRGB
                RenderingIntent = 0,   // JXL_RENDERING_INTENT_PERCEPTUAL
            };
            Check(JxlEncoderSetColorEncoding(enc, ref color), "JxlEncoderSetColorEncoding");

            var settings = JxlEncoderFrameSettingsCreate(enc, IntPtr.Zero);
            if (settings == IntPtr.Zero)
                throw new InvalidOperationException("JPEG XL フレーム設定の作成に失敗しました");
            Check(JxlEncoderSetFrameLossless(settings, 1), "JxlEncoderSetFrameLossless");

            var format = new JxlPixelFormat
            {
                NumChannels = 4,
                DataType = 0, // JXL_TYPE_UINT8
                Endianness = 0, // JXL_NATIVE_ENDIAN
                Align = UIntPtr.Zero,
            };
            unsafe
            {
                fixed (byte* p = rgba)
                    Check(JxlEncoderAddImageFrame(settings, ref format, (IntPtr)p,
                        (UIntPtr)rgba.Length), "JxlEncoderAddImageFrame");
            }
            JxlEncoderCloseInput(enc);

            using var ms = new MemoryStream();
            var chunk = new byte[1 << 20];
            unsafe
            {
                fixed (byte* p = chunk)
                {
                    var next = (IntPtr)p;
                    var avail = (UIntPtr)chunk.Length;
                    while (true)
                    {
                        var status = JxlEncoderProcessOutput(enc, ref next, ref avail);
                        var written = chunk.Length - (int)avail;
                        if (written > 0) ms.Write(chunk, 0, written);
                        if (status == 0) break; // JXL_ENC_SUCCESS
                        if (status != 2) // JXL_ENC_NEED_MORE_OUTPUT
                            throw new InvalidOperationException(
                                $"JPEG XL の出力に失敗しました (status={status})");
                        next = (IntPtr)p;
                        avail = (UIntPtr)chunk.Length;
                    }
                }
            }
            return ms.ToArray();
        }
        catch (DllNotFoundException)
        {
            throw new InvalidOperationException(
                "jxl.dll が見つかりません。libjxl 公式リリースの jxl.dll を exe と同じフォルダに置いてください");
        }
        catch (EntryPointNotFoundException)
        {
            throw new InvalidOperationException(
                "jxl.dll の版が古く、必要な関数が見つかりません。libjxl 0.11 以降を使ってください");
        }
        finally
        {
            JxlEncoderDestroy(enc);
        }
    }

    private static void Check(int status, string api)
    {
        if (status != 0)
            throw new InvalidOperationException($"JPEG XL エンコードに失敗しました: {api} (status={status})");
    }

    // ---- libjxl structs (layouts per the 0.11 public headers) ----

    [StructLayout(LayoutKind.Sequential)]
    private struct JxlPreviewHeader
    {
        public uint Xsize;
        public uint Ysize;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JxlAnimationHeader
    {
        public uint TpsNumerator;
        public uint TpsDenominator;
        public uint NumLoops;
        public int HaveTimecodes; // JxlBool
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JxlBasicInfo
    {
        public int HaveContainer; // JxlBool
        public uint Xsize;
        public uint Ysize;
        public uint BitsPerSample;
        public uint ExponentBitsPerSample;
        public float IntensityTarget;
        public float MinNits;
        public int RelativeToMaxDisplay; // JxlBool
        public float LinearBelow;
        public int UsesOriginalProfile; // JxlBool
        public int HavePreview; // JxlBool
        public int HaveAnimation; // JxlBool
        public int Orientation; // JxlOrientation
        public uint NumColorChannels;
        public uint NumExtraChannels;
        public uint AlphaBits;
        public uint AlphaExponentBits;
        public int AlphaPremultiplied; // JxlBool
        public JxlPreviewHeader Preview;
        public JxlAnimationHeader Animation;
        public uint IntrinsicXsize;
        public uint IntrinsicYsize;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 100)]
        public byte[] Padding;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JxlColorEncoding
    {
        public int ColorSpace; // JxlColorSpace
        public int WhitePoint; // JxlWhitePoint
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 2)]
        public double[] WhitePointXy;
        public int Primaries; // JxlPrimaries
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 2)]
        public double[] PrimariesRedXy;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 2)]
        public double[] PrimariesGreenXy;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 2)]
        public double[] PrimariesBlueXy;
        public int TransferFunction; // JxlTransferFunction
        public double Gamma;
        public int RenderingIntent; // JxlRenderingIntent
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JxlPixelFormat
    {
        public uint NumChannels;
        public int DataType; // JxlDataType
        public int Endianness; // JxlEndianness
        public UIntPtr Align;
    }

    // ---- libjxl functions ----

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    private static extern uint JxlEncoderVersion();

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr JxlEncoderCreate(IntPtr memoryManager);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    private static extern void JxlEncoderDestroy(IntPtr enc);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    private static extern void JxlEncoderInitBasicInfo(ref JxlBasicInfo info);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    private static extern int JxlEncoderSetBasicInfo(IntPtr enc, ref JxlBasicInfo info);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    private static extern int JxlEncoderSetColorEncoding(IntPtr enc, ref JxlColorEncoding color);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr JxlEncoderFrameSettingsCreate(IntPtr enc, IntPtr source);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    private static extern int JxlEncoderSetFrameLossless(IntPtr frameSettings, int lossless);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    private static extern int JxlEncoderAddImageFrame(IntPtr frameSettings,
        ref JxlPixelFormat pixelFormat, IntPtr buffer, UIntPtr size);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    private static extern void JxlEncoderCloseInput(IntPtr enc);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    private static extern int JxlEncoderProcessOutput(IntPtr enc,
        ref IntPtr nextOut, ref UIntPtr availOut);
}
