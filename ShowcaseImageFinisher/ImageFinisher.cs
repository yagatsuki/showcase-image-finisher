using System.IO;
using System.Runtime.InteropServices;
using SkiaSharp;

namespace ShowcaseImageFinisher;

public enum OutputFormat { Png, Webp, JpegXl }

public static class OutputFormatInfo
{
    public static string Extension(this OutputFormat f) => f switch
    {
        OutputFormat.Png => ".png",
        OutputFormat.Webp => ".webp",
        OutputFormat.JpegXl => ".jxl",
        _ => ".png",
    };
}

/// <summary>Optional finishing touches applied while composing.</summary>
public sealed record FinishOptions
{
    /// <summary>Pull semi-transparent edge colors toward the subject
    /// color, removing the background-colored halo around the outline.</summary>
    public bool Defringe { get; init; }

    /// <summary>Trim to the largest connected subject only, ignoring
    /// other objects (logos, badges) that survived removal.</summary>
    public bool LargestSubjectOnly { get; init; }

    /// <summary>Transparent margin kept around the subject, in pixels.</summary>
    public int Padding { get; init; }

    /// <summary>Size-first (lossy) encoding instead of lossless:
    /// PNG is quantized to a 256-color palette,
    /// WebP uses lossy quality 90, JPEG XL uses distance 1.0.</summary>
    public bool SizePriority { get; init; }

    public static readonly FinishOptions Default = new();
}

/// <summary>
/// Decode (many formats via SkiaSharp), compose the cutout, trim the
/// transparent margins, and encode (PNG / WebP / JPEG XL, losslessly
/// by default or size-first in SizePriority mode).
/// The trim matches the trim-transparent-pixels tool: pixels with
/// alpha &lt;= threshold (default 8) count as transparent, which removes
/// the near-zero alpha noise background removal leaves behind.
/// </summary>
public static class ImageFinisher
{
    /// <summary>Decodes image bytes to unpremultiplied RGBA (w*h*4).</summary>
    public static (byte[] Rgba, int W, int H) DecodeRgba(byte[] bytes)
    {
        using var decoded = SKBitmap.Decode(bytes)
            ?? throw new InvalidDataException("対応していない画像形式、または壊れたファイルです");
        var (w, h) = (decoded.Width, decoded.Height);
        using var rgba = new SKBitmap(new SKImageInfo(w, h, SKColorType.Rgba8888, SKAlphaType.Unpremul));
        using (var canvas = new SKCanvas(rgba))
            canvas.DrawBitmap(decoded, 0, 0);
        var buf = new byte[w * h * 4];
        Marshal.Copy(rgba.GetPixels(), buf, 0, buf.Length);
        return (buf, w, h);
    }

    /// <summary>Flattens RGBA onto black and returns RGB triplets (w*h*3).</summary>
    public static byte[] FlattenRgb(byte[] rgba, int w, int h)
    {
        using var src = new SKBitmap();
        unsafe
        {
            fixed (byte* p = rgba)
                if (!src.InstallPixels(
                        new SKImageInfo(w, h, SKColorType.Rgba8888, SKAlphaType.Unpremul),
                        (IntPtr)p, w * 4))
                    throw new InvalidOperationException("画像バッファの取り込みに失敗しました");
        }
        using var flat = new SKBitmap(new SKImageInfo(w, h, SKColorType.Rgb888x, SKAlphaType.Opaque));
        using (var canvas = new SKCanvas(flat))
        {
            canvas.Clear(SKColors.Black);
            canvas.DrawBitmap(src, 0, 0);
        }
        var px = new byte[w * h * 4];
        Marshal.Copy(flat.GetPixels(), px, 0, px.Length);
        var rgb = new byte[w * h * 3];
        for (int i = 0, j = 0; i < px.Length; i += 4, j += 3)
        {
            rgb[j] = px[i]; rgb[j + 1] = px[i + 1]; rgb[j + 2] = px[i + 2];
        }
        return rgb;
    }

    /// <summary>
    /// Applies the mask as the new alpha (multiplied by any existing
    /// alpha), cleans near-zero alpha noise, and trims to the content.
    /// </summary>
    public static (byte[] Rgba, int W, int H) ComposeAndTrim(
        byte[] rgba, int w, int h, byte[] mask, int threshold = 8,
        FinishOptions? options = null)
    {
        options ??= FinishOptions.Default;
        var a = (byte[])rgba.Clone();
        for (var i = 0; i < w * h; i++)
        {
            var alpha = mask[i] * a[i * 4 + 3] / 255;
            a[i * 4 + 3] = (byte)(alpha <= threshold ? 0 : alpha);
        }
        if (options.Defringe) DefringeEdges(a, w, h);
        // The bounding box counts only majority-opaque pixels as content.
        // Background removers leave faint residue (alpha up to ~60) where
        // other objects used to be (logos, badges); counting it would hold
        // the box open to the image edge even though nothing is visible.
        const int contentThreshold = 128;
        var (minX, minY, maxX, maxY) = (w, h, -1, -1);
        for (var y = 0; y < h; y++)
        for (var x = 0; x < w; x++)
        {
            if (a[(y * w + x) * 4 + 3] <= contentThreshold) continue;
            if (x < minX) minX = x;
            if (x > maxX) maxX = x;
            if (y < minY) minY = y;
            if (y > maxY) maxY = y;
        }
        if (maxX < 0) throw new InvalidDataException("背景除去の結果、画像全体が透明になりました");
        if (options.LargestSubjectOnly)
            (minX, minY, maxX, maxY) = LargestComponentBox(a, w, h, contentThreshold);
        if (options.Padding > 0)
        {
            minX = Math.Max(0, minX - options.Padding);
            minY = Math.Max(0, minY - options.Padding);
            maxX = Math.Min(w - 1, maxX + options.Padding);
            maxY = Math.Min(h - 1, maxY + options.Padding);
        }
        var (nw, nh) = (maxX - minX + 1, maxY - minY + 1);
        if (nw == w && nh == h) return (a, w, h);
        var cropped = new byte[nw * nh * 4];
        for (var y = 0; y < nh; y++)
            Buffer.BlockCopy(a, ((minY + y) * w + minX) * 4, cropped, y * nw * 4, nw * 4);
        return (cropped, nw, nh);
    }

    private static readonly (int Dx, int Dy)[] Neighbors = { (-1, 0), (1, 0), (0, -1), (0, 1) };

    /// <summary>
    /// Decontaminates semi-transparent edge pixels: their color is pulled
    /// toward the nearest opaque neighbor (the subject's own color), which
    /// removes the halo of background color around the outline.
    /// </summary>
    private static void DefringeEdges(byte[] a, int w, int h)
    {
        var src = (byte[])a.Clone();
        for (var y = 0; y < h; y++)
        for (var x = 0; x < w; x++)
        {
            var i = (y * w + x) * 4;
            var alpha = src[i + 3];
            if (alpha == 0 || alpha == 255) continue;
            foreach (var (dx, dy) in Neighbors)
            {
                var nx = x + dx; var ny = y + dy;
                if (nx < 0 || ny < 0 || nx >= w || ny >= h) continue;
                var j = (ny * w + nx) * 4;
                if (src[j + 3] < 250) continue;
                a[i] = (byte)(src[i] * 0.35 + src[j] * 0.65);
                a[i + 1] = (byte)(src[i + 1] * 0.35 + src[j + 1] * 0.65);
                a[i + 2] = (byte)(src[i + 2] * 0.35 + src[j + 2] * 0.65);
                break;
            }
        }
    }

    /// <summary>
    /// Bounding box of the largest connected component of content pixels
    /// (union-find labeling, then per-component statistics).
    /// </summary>
    private static (int MinX, int MinY, int MaxX, int MaxY) LargestComponentBox(
        byte[] a, int w, int h, int contentThreshold)
    {
        var labels = new int[w * h];
        var parent = new List<int> { 0 };
        int Find(int v)
        {
            while (parent[v] != v) { parent[v] = parent[parent[v]]; v = parent[v]; }
            return v;
        }
        for (var y = 0; y < h; y++)
        for (var x = 0; x < w; x++)
        {
            var idx = y * w + x;
            if (a[idx * 4 + 3] <= contentThreshold) continue;
            var left = x > 0 ? labels[idx - 1] : 0;
            var top = y > 0 ? labels[idx - w] : 0;
            if (left == 0 && top == 0)
            {
                labels[idx] = parent.Count;
                parent.Add(parent.Count);
            }
            else if (left != 0 && top != 0)
            {
                var rl = Find(left); var rt = Find(top);
                if (rl != rt) parent[Math.Max(rl, rt)] = Math.Min(rl, rt);
                labels[idx] = Math.Min(rl, rt);
            }
            else labels[idx] = left != 0 ? left : top;
        }
        var stats = new Dictionary<int, (int Count, int MinX, int MinY, int MaxX, int MaxY)>();
        for (var y = 0; y < h; y++)
        for (var x = 0; x < w; x++)
        {
            var l = labels[y * w + x];
            if (l == 0) continue;
            var r = Find(l);
            if (!stats.TryGetValue(r, out var s)) s = (0, w, h, -1, -1);
            s.Count++;
            if (x < s.MinX) s.MinX = x;
            if (x > s.MaxX) s.MaxX = x;
            if (y < s.MinY) s.MinY = y;
            if (y > s.MaxY) s.MaxY = y;
            stats[r] = s;
        }
        var best = stats.Values.MaxBy(s => s.Count);
        return (best.MinX, best.MinY, best.MaxX, best.MaxY);
    }

    /// <summary>Lossy WebP quality used in size-priority mode.</summary>
    public const float WebpLossyQuality = 90f;

    /// <summary>Encodes trimmed RGBA in the chosen format, losslessly
    /// by default or size-first when <see cref="FinishOptions.SizePriority"/>
    /// is set.</summary>
    public static byte[] Encode(byte[] rgba, int w, int h, OutputFormat format,
        FinishOptions? options = null)
    {
        var sizePriority = options?.SizePriority == true;
        if (format == OutputFormat.Png && sizePriority)
        {
            // Prefer libimagequant (higher quality) when its DLL
            // sits next to the exe; any failure falls back to the
            // built-in quantizer so the batch never breaks over it.
            // Parameters measured on real background-removed cutouts:
            // the palette cap is 192, not 256 (at 256 the dithered
            // indices compress slightly worse than the built-in
            // output; at 192 it is smaller and lower-error), and the
            // dithering level is 0.5, which shrank files a further
            // 3-7% at unchanged error (full dithering is index noise
            // that deflate cannot match).
            if (ImageQuantizer.IsAvailable)
            {
                try
                {
                    return ImageQuantizer.EncodeQuantizedPng(rgba, w, h,
                        maxColors: 192, ditheringLevel: 0.5f);
                }
                catch { /* fall through to the built-in quantizer */ }
            }
            return PngQuantizer.EncodeQuantizedPng(rgba, w, h);
        }
        if (format == OutputFormat.JpegXl)
            return JxlEncoder.EncodeRgba(rgba, w, h, lossless: !sizePriority);

        using var bmp = new SKBitmap();
        unsafe
        {
            fixed (byte* p = rgba)
                if (!bmp.InstallPixels(
                        new SKImageInfo(w, h, SKColorType.Rgba8888, SKAlphaType.Unpremul),
                        (IntPtr)p, w * 4))
                    throw new InvalidOperationException("画像バッファの取り込みに失敗しました");
        }
        using var pixmap = bmp.PeekPixels()
            ?? throw new InvalidOperationException("画像ピクセルの取得に失敗しました");
        using var ms = new MemoryStream();
        var ok = format == OutputFormat.Webp
            ? pixmap.Encode(ms, sizePriority
                ? new SKWebpEncoderOptions(SKWebpEncoderCompression.Lossy, WebpLossyQuality)
                : new SKWebpEncoderOptions(SKWebpEncoderCompression.Lossless, 100))
            : pixmap.Encode(ms, SKEncodedImageFormat.Png, 100);
        if (!ok) throw new InvalidOperationException("画像のエンコードに失敗しました");
        return ms.ToArray();
    }

    /// <summary>Full pipeline for one file. Returns output size info.</summary>
    public static (int W, int H, long Bytes) ProcessFile(
        string inputPath, string outputPath, ModelDef def, string modelPath,
        OutputFormat format, FinishOptions? options = null)
    {
        var bytes = File.ReadAllBytes(inputPath);
        var (rgba, w, h) = DecodeRgba(bytes);
        var mask = BgRemover.PredictMask(def, modelPath, FlattenRgb(rgba, w, h), w, h);
        var (cut, cw, ch) = ComposeAndTrim(rgba, w, h, mask, 8, options);
        var encoded = Encode(cut, cw, ch, format, options);
        File.WriteAllBytes(outputPath, encoded);
        return (cw, ch, encoded.LongLength);
    }
}
