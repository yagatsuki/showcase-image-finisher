using System.Runtime.InteropServices;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using SkiaSharp;

namespace ShowcaseImageFinisher;

/// <summary>
/// Background removal with the same ONNX models and the same
/// preprocessing as rembg (danielgatis/rembg), reimplemented in C#:
/// resize (to the model's input size) -> divide by the image's max value
/// -> (x - mean) / std per channel -> CHW tensor. The predicted mask is
/// min-max normalized, resized back to the original size, and used as
/// the alpha channel.
/// </summary>
public static class BgRemover
{
    private static readonly Dictionary<string, InferenceSession> Sessions = new();
    private static readonly SKSamplingOptions Sampling =
        new(SKCubicResampler.CatmullRom);

    public static InferenceSession GetSession(string modelPath)
    {
        if (!Sessions.TryGetValue(modelPath, out var session))
        {
            session = new InferenceSession(modelPath, new SessionOptions());
            Sessions[modelPath] = session;
        }
        return session;
    }

    /// <summary>
    /// Runs inference. flatRgb is the source flattened onto black
    /// (R,G,B triplets, w*h*3). Returns the mask as w*h bytes.
    /// </summary>
    public static byte[] PredictMask(ModelDef def, string modelPath,
        byte[] flatRgb, int w, int h)
    {
        var size = def.InputSize;
        var small = ResizeRgb(flatRgb, w, h, size, size);

        // rembg normalize: divide by the max value over the whole image,
        // then (x - mean) / std per channel, CHW layout.
        byte max = 0;
        foreach (var b in small) if (b > max) max = b;
        var denom = Math.Max((float)max, 1e-6f);
        var plane = size * size;
        var data = new float[3 * plane];
        for (var c = 0; c < 3; c++)
        for (var i = 0; i < plane; i++)
            data[c * plane + i] = (small[i * 3 + c] / denom - def.Mean[c]) / def.Std[c];

        var tensor = new DenseTensor<float>(data, new[] { 1, 3, size, size });
        var session = GetSession(modelPath);
        var input = NamedOnnxValue.CreateFromTensor(session.InputMetadata.Keys.First(), tensor);
        using var results = session.Run(new[] { input });
        var output = results.First().AsTensor<float>();
        if (output.Dimensions.Length != 4)
            throw new InvalidOperationException(
                $"モデルの出力形式が想定と異なります (shape: {string.Join("x", output.Dimensions.ToArray())})");

        // Post-process like rembg: min-max normalize channel 0, scale to
        // bytes, then resize the mask back to the original size.
        var mn = float.MaxValue;
        var mx = float.MinValue;
        foreach (var v in output) { if (v < mn) mn = v; if (v > mx) mx = v; }
        var range = Math.Max(mx - mn, 1e-6f);
        var maskSmall = new byte[plane];
        for (var i = 0; i < plane; i++)
        {
            var v = Math.Clamp((output[0, 0, i / size, i % size] - mn) / range, 0f, 1f);
            maskSmall[i] = (byte)(v * 255f);
        }
        return ResizeGray(maskSmall, size, size, w, h);
    }

    private static byte[] ResizeRgb(byte[] rgb, int w, int h, int nw, int nh)
    {
        using var src = new SKBitmap();
        unsafe
        {
            fixed (byte* p = rgb)
                if (!src.InstallPixels(
                        new SKImageInfo(w, h, SKColorType.Rgb888x, SKAlphaType.Opaque),
                        (IntPtr)p, w * 3))
                    throw new InvalidOperationException("画像バッファの取り込みに失敗しました");
        }
        using var dst = src.Resize(
                new SKImageInfo(nw, nh, SKColorType.Rgb888x, SKAlphaType.Opaque), Sampling)
            ?? throw new InvalidOperationException("画像のリサイズに失敗しました");
        var outBytes = new byte[nw * nh * 3];
        Marshal.Copy(dst.GetPixels(), outBytes, 0, outBytes.Length);
        return outBytes;
    }

    private static byte[] ResizeGray(byte[] gray, int w, int h, int nw, int nh)
    {
        using var src = new SKBitmap();
        unsafe
        {
            fixed (byte* p = gray)
                if (!src.InstallPixels(
                        new SKImageInfo(w, h, SKColorType.Gray8, SKAlphaType.Opaque),
                        (IntPtr)p, w))
                    throw new InvalidOperationException("マスクバッファの取り込みに失敗しました");
        }
        using var dst = src.Resize(
                new SKImageInfo(nw, nh, SKColorType.Gray8, SKAlphaType.Opaque), Sampling)
            ?? throw new InvalidOperationException("マスクのリサイズに失敗しました");
        var outBytes = new byte[nw * nh];
        Marshal.Copy(dst.GetPixels(), outBytes, 0, outBytes.Length);
        return outBytes;
    }
}
