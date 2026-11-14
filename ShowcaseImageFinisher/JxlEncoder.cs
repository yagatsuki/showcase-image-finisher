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

    // jxl.dll is resolved from the exe's own folder by the assembly-wide
    // resolver in NativeDllResolver (.NET allows only one per assembly).

    public static bool IsAvailable => Diagnose().StartsWith("利用できます", StringComparison.Ordinal);

    /// <summary>Human-readable diagnosis of the jxl.dll situation.</summary>
    public static string Diagnose()
    {
        var dir = AppContext.BaseDirectory;
        var dll = Path.Combine(dir, "jxl.dll");
        if (!File.Exists(dll))
            return $"jxl.dll が exe のフォルダにありません（{dir}）";
        var arch = PeArchitecture(dll);
        var process = System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture;
        if (arch is not null && arch != process.ToString())
            return $"jxl.dll が {arch} 版のため読み込めません（このアプリは {process} 版）。" +
                   $"公式リリースの {process} 版パッケージから取り直してください";
        try
        {
            var version = JxlEncoderVersion();
            return $"利用できます（libjxl {version / 1_000_000}.{version / 1000 % 1000}.{version % 1000}）";
        }
        catch (Exception ex)
        {
            var missing = new[] { "jxl_threads.dll", "jxl_cms.dll", "brotlicommon.dll", "brotlidec.dll", "brotlienc.dll" }
                .Where(f => !File.Exists(Path.Combine(dir, f))).ToList();
            var extra = missing.Count > 0 ? $"、依存 DLL の不足: {string.Join(", ", missing)}" : "";
            return $"jxl.dll はありますが読み込めませんでした: {ex.GetType().Name}: {ex.Message}{extra}" +
                   "（VC++ 再頒布可能ランタイムの不足でもこの状態になります）";
        }
    }

    /// <summary>Reads the PE header of a DLL and returns "X64" / "X86" / "Arm64".</summary>
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

    public static byte[] EncodeRgbaLossless(byte[] rgba, int w, int h) =>
        EncodeRgba(rgba, w, h, lossless: true);

    /// <summary>
    /// Encodes RGBA as JPEG XL. In lossy mode a frame distance of 1.0
    /// is used (roughly quality 90: visually near-lossless, several
    /// times smaller than lossless for photos — the JPEG XL analogue
    /// of the size-first trade-off).
    /// </summary>
    public static byte[] EncodeRgba(byte[] rgba, int w, int h, bool lossless)
    {
        var enc = JxlEncoderCreate(IntPtr.Zero);
        if (enc == IntPtr.Zero)
            throw new InvalidOperationException("JPEG XL エンコーダの作成に失敗しました");
        void CheckEnc(int status, string api)
        {
            if (status != 0)
                throw new InvalidOperationException(
                    $"JPEG XL エンコードに失敗しました: {api} (status={status}, 詳細コード={JxlEncoderGetError(enc)})");
        }
        try
        {
            var info = new JxlBasicInfo { Padding = new byte[100] };
            JxlEncoderInitBasicInfo(ref info);
            info.Xsize = (uint)w;
            info.Ysize = (uint)h;
            info.BitsPerSample = 8;
            info.ExponentBitsPerSample = 0;
            info.UsesOriginalProfile = lossless ? 1 : 0; // 1 is required for true lossless
            info.NumColorChannels = 3;
            info.NumExtraChannels = 1;
            info.AlphaBits = 8;
            info.AlphaExponentBits = 0;
            info.AlphaPremultiplied = 0;
            CheckEnc(JxlEncoderSetBasicInfo(enc, ref info), "JxlEncoderSetBasicInfo");

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
            CheckEnc(JxlEncoderSetColorEncoding(enc, ref color), "JxlEncoderSetColorEncoding");

            var settings = JxlEncoderFrameSettingsCreate(enc, IntPtr.Zero);
            if (settings == IntPtr.Zero)
                throw new InvalidOperationException("JPEG XL フレーム設定の作成に失敗しました");
            CheckEnc(JxlEncoderSetFrameLossless(settings, lossless ? 1 : 0), "JxlEncoderSetFrameLossless");
            if (!lossless)
                CheckEnc(JxlEncoderSetFrameDistance(settings, 1.0f), "JxlEncoderSetFrameDistance");

            var format = new JxlPixelFormat
            {
                NumChannels = 4,
                DataType = 2, // JXL_TYPE_UINT8 (0 is JXL_TYPE_FLOAT!)
                Endianness = 0, // JXL_NATIVE_ENDIAN
                Align = UIntPtr.Zero,
            };
            unsafe
            {
                fixed (byte* p = rgba)
                    CheckEnc(JxlEncoderAddImageFrame(settings, ref format, (IntPtr)p,
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
    private static extern int JxlEncoderGetError(IntPtr enc);

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
    private static extern int JxlEncoderSetFrameDistance(IntPtr frameSettings, float distance);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    private static extern int JxlEncoderAddImageFrame(IntPtr frameSettings,
        ref JxlPixelFormat pixelFormat, IntPtr buffer, UIntPtr size);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    private static extern void JxlEncoderCloseInput(IntPtr enc);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    private static extern int JxlEncoderProcessOutput(IntPtr enc,
        ref IntPtr nextOut, ref UIntPtr availOut);
}
