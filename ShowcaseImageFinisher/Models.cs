using System.IO;
using System.Net.Http;
using System.Security.Cryptography;

namespace ShowcaseImageFinisher;

/// <summary>
/// Background-removal model definition. The preprocessing values mirror
/// rembg exactly (shared normalize: resize, divide by the image max,
/// (x - mean) / std, CHW), verified against rembg's own masks.
/// </summary>
public sealed record ModelDef(
    string Id, string Display, string FileName, string Url, string Md5,
    int InputSize, float[] Mean, float[] Std)
{
    public static readonly ModelDef IsNet = new(
        "isnet-general-use", "isnet-general-use（高品質・重め）",
        "isnet-general-use.onnx",
        "https://github.com/danielgatis/rembg/releases/download/v0.0.0/isnet-general-use.onnx",
        "fc16ebd8b0c10d971d3513d564d01e29", 1024,
        new[] { 0.5f, 0.5f, 0.5f }, new[] { 1.0f, 1.0f, 1.0f });

    public static readonly ModelDef U2Net = new(
        "u2net", "u2net（標準）",
        "u2net.onnx",
        "https://github.com/danielgatis/rembg/releases/download/v0.0.0/u2net.onnx",
        "60024c5c889badc19c04ad937298a77b", 320,
        new[] { 0.485f, 0.456f, 0.406f }, new[] { 0.229f, 0.224f, 0.225f });

    public static readonly ModelDef U2NetP = new(
        "u2netp", "u2netp（高速・軽量）",
        "u2netp.onnx",
        "https://github.com/danielgatis/rembg/releases/download/v0.0.0/u2netp.onnx",
        "8e83ca70e441ab06c318d82300c84806", 320,
        new[] { 0.485f, 0.456f, 0.406f }, new[] { 0.229f, 0.224f, 0.225f });

    public static readonly ModelDef[] All = { IsNet, U2Net, U2NetP };
}

public static class ModelStore
{
    public static string ModelDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "ShowcaseImageFinisher", "models");

    public static string PathOf(ModelDef def) => Path.Combine(ModelDir, def.FileName);

    public static bool Exists(ModelDef def) => File.Exists(PathOf(def));

    /// <summary>Downloads the model on first use and verifies its MD5.</summary>
    public static async Task<string> EnsureAsync(
        ModelDef def, IProgress<double>? progress, CancellationToken ct)
    {
        var path = PathOf(def);
        if (File.Exists(path) && Md5Of(path).Equals(def.Md5, StringComparison.OrdinalIgnoreCase))
            return path;

        Directory.CreateDirectory(ModelDir);
        var tmp = path + ".download";
        using var http = new HttpClient();
        using var resp = await http.GetAsync(def.Url, HttpCompletionOption.ResponseHeadersRead, ct);
        resp.EnsureSuccessStatusCode();
        var total = resp.Content.Headers.ContentLength ?? -1;
        await using (var src = await resp.Content.ReadAsStreamAsync(ct))
        await using (var dst = File.Create(tmp))
        {
            var buf = new byte[81920];
            long done = 0;
            int n;
            while ((n = await src.ReadAsync(buf, ct)) > 0)
            {
                await dst.WriteAsync(buf.AsMemory(0, n), ct);
                done += n;
                if (total > 0) progress?.Report(done * 100.0 / total);
            }
        }
        if (!Md5Of(tmp).Equals(def.Md5, StringComparison.OrdinalIgnoreCase))
        {
            File.Delete(tmp);
            throw new InvalidDataException($"モデルの検証に失敗しました: {def.FileName}");
        }
        File.Move(tmp, path, overwrite: true);
        return path;
    }

    private static string Md5Of(string path)
    {
        using var md5 = MD5.Create();
        using var s = File.OpenRead(path);
        return Convert.ToHexString(md5.ComputeHash(s));
    }
}
