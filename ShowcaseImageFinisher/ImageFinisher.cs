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

/// <summary>
/// Decode (many formats via SkiaSharp), compose the cutout, trim the
/// transparent margins, and encode losslessly (PNG / WebP / JPEG XL).
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
                src.InstallPixels(
                    new SKImageInfo(w, h, SKColorType.Rgba8888, SKAlphaType.Unpremul),
                    (IntPtr)p, w * 4);
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
        byte[] rgba, int w, int h, byte[] mask, int threshold = 8)
    {
        var a = (byte[])rgba.Clone();
        for (var i = 0; i < w * h; i++)
        {
            var alpha = mask[i] * a[i * 4 + 3] / 255;
            a[i * 4 + 3] = (byte)(alpha <= threshold ? 0 : alpha);
        }
        var (minX, minY, maxX, maxY) = (w, h, -1, -1);
        for (var y = 0; y < h; y++)
        for (var x = 0; x < w; x++)
        {
            if (a[(y * w + x) * 4 + 3] == 0) continue;
            if (x < minX) minX = x;
            if (x > maxX) maxX = x;
            if (y < minY) minY = y;
            if (y > maxY) maxY = y;
        }
        if (maxX < 0) throw new InvalidDataException("背景除去の結果、画像全体が透明になりました");
        var (nw, nh) = (maxX - minX + 1, maxY - minY + 1);
        if (nw == w && nh == h) return (a, w, h);
        var cropped = new byte[nw * nh * 4];
        for (var y = 0; y < nh; y++)
            Buffer.BlockCopy(a, ((minY + y) * w + minX) * 4, cropped, y * nw * 4, nw * 4);
        return (cropped, nw, nh);
    }

    /// <summary>Encodes trimmed RGBA in the chosen lossless format.</summary>
    public static byte[] Encode(byte[] rgba, int w, int h, OutputFormat format)
    {
        if (format == OutputFormat.JpegXl)
            return JxlEncoder.EncodeRgbaLossless(rgba, w, h);

        using var bmp = new SKBitmap();
        unsafe
        {
            fixed (byte* p = rgba)
                bmp.InstallPixels(
                    new SKImageInfo(w, h, SKColorType.Rgba8888, SKAlphaType.Unpremul),
                    (IntPtr)p, w * 4);
        }
        using var pixmap = bmp.PeekPixels();
        using var ms = new MemoryStream();
        var ok = format == OutputFormat.Webp
            ? pixmap.Encode(ms, new SKWebpEncoderOptions(SKWebpEncoderCompression.Lossless, 100))
            : pixmap.Encode(ms, SKEncodedImageFormat.Png, 100);
        if (!ok) throw new InvalidOperationException("画像のエンコードに失敗しました");
        return ms.ToArray();
    }

    /// <summary>Full pipeline for one file. Returns output size info.</summary>
    public static (int W, int H, long Bytes) ProcessFile(
        string inputPath, string outputPath, ModelDef def, string modelPath,
        OutputFormat format)
    {
        var bytes = File.ReadAllBytes(inputPath);
        var (rgba, w, h) = DecodeRgba(bytes);
        var mask = BgRemover.PredictMask(def, modelPath, FlattenRgb(rgba, w, h), w, h);
        var (cut, cw, ch) = ComposeAndTrim(rgba, w, h, mask);
        var encoded = Encode(cut, cw, ch, format);
        File.WriteAllBytes(outputPath, encoded);
        return (cw, ch, encoded.LongLength);
    }
}
