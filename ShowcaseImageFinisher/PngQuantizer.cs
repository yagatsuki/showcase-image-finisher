using System.IO;
using System.IO.Compression;

namespace ShowcaseImageFinisher;

/// <summary>
/// TinyPNG-style lossy PNG compression, implemented in pure C# (no
/// native DLL): the RGBA image is quantized to a palette of at most
/// 256 colors (median-cut in premultiplied RGBA space, Floyd–Steinberg
/// dithering) and written as an indexed-color PNG (color type 3)
/// with per-row filter selection and zlib compression.
///
/// TinyPNG / pngquant do the same in principle: most of the saving
/// comes from storing 1 byte per pixel plus a small palette instead
/// of 4 bytes per pixel. When the source already has at most 256
/// distinct colors the palette is exact and nothing is lost.
/// </summary>
public static class PngQuantizer
{
    public const int MaxColors = 256;

    /// <summary>Quantizes RGBA and returns indexed-PNG bytes.</summary>
    public static byte[] EncodeQuantizedPng(byte[] rgba, int w, int h, int maxColors = MaxColors)
    {
        if (rgba.Length != w * h * 4)
            throw new ArgumentException("RGBA バッファのサイズが画像サイズと一致しません", nameof(rgba));
        maxColors = Math.Clamp(maxColors, 2, MaxColors);

        var (palette, indices) = Quantize(rgba, w, h, maxColors);
        return WriteIndexedPng(palette, indices, w, h);
    }

    // ---------- quantization ----------

    private readonly struct HistEntry
    {
        // Representative color of one histogram bin, premultiplied.
        public readonly float Pr, Pg, Pb, A;
        public readonly int Count;
        public HistEntry(float pr, float pg, float pb, float a, int count)
        { Pr = pr; Pg = pg; Pb = pb; A = a; Count = count; }
    }

    private sealed class Box
    {
        public required List<HistEntry> Entries;
        public int TotalCount;
    }

    private static (byte[] Palette, byte[] Indices) Quantize(byte[] rgba, int w, int h, int maxColors)
    {
        var n = w * h;
        var hasTransparent = false;
        var exact = new Dictionary<uint, int>();
        for (var i = 0; i < n; i++)
        {
            var a = rgba[i * 4 + 3];
            if (a == 0) { hasTransparent = true; continue; }
            var key = (uint)(rgba[i * 4] << 24 | rgba[i * 4 + 1] << 16 | rgba[i * 4 + 2] << 8 | a);
            exact[key] = exact.GetValueOrDefault(key) + 1;
        }

        var palette = new List<(byte R, byte G, byte B, byte A)>();
        if (hasTransparent) palette.Add((0, 0, 0, 0));
        var target = maxColors - palette.Count;

        if (exact.Count <= target)
        {
            // Already few enough colors: the palette is exact (lossless).
            var indexOf = new Dictionary<uint, byte>();
            foreach (var key in exact.Keys.OrderByDescending(k => exact[k]))
            {
                indexOf[key] = (byte)palette.Count;
                palette.Add(((byte)(key >> 24), (byte)(key >> 16), (byte)(key >> 8), (byte)key));
            }
            var exactIndices = new byte[n];
            for (var i = 0; i < n; i++)
            {
                if (rgba[i * 4 + 3] == 0) { exactIndices[i] = 0; continue; }
                var key = (uint)(rgba[i * 4] << 24 | rgba[i * 4 + 1] << 16 | rgba[i * 4 + 2] << 8 | rgba[i * 4 + 3]);
                exactIndices[i] = indexOf[key];
            }
            return (PaletteBytes(palette), exactIndices);
        }

        // Histogram with 5 bits per channel (premultiplied RGB + alpha).
        // Each bin keeps count-weighted sums so its representative color
        // is the bin's true average, not the bin corner.
        var bins = new Dictionary<uint, (long Sr, long Sg, long Sb, long Sa, int Count)>();
        for (var i = 0; i < n; i++)
        {
            var a = rgba[i * 4 + 3];
            if (a == 0) continue;
            var af = a / 255f;
            var pr = (int)Math.Round(rgba[i * 4] * af);
            var pg = (int)Math.Round(rgba[i * 4 + 1] * af);
            var pb = (int)Math.Round(rgba[i * 4 + 2] * af);
            var key = (uint)((pr >> 3) << 15 | (pg >> 3) << 10 | (pb >> 3) << 5 | (a >> 3));
            var b = bins.GetValueOrDefault(key);
            bins[key] = (b.Sr + pr, b.Sg + pg, b.Sb + pb, b.Sa + a, b.Count + 1);
        }
        var entries = new List<HistEntry>(bins.Count);
        foreach (var b in bins.Values)
            entries.Add(new HistEntry(
                (float)b.Sr / b.Count, (float)b.Sg / b.Count,
                (float)b.Sb / b.Count, (float)b.Sa / b.Count, b.Count));

        // Median-cut: repeatedly split the box whose colors spread the
        // most (range weighted by population) along its widest channel.
        var boxes = new List<Box>
        { new() { Entries = entries, TotalCount = entries.Sum(e => e.Count) } };
        while (boxes.Count < target)
        {
            Box? best = null;
            var bestScore = -1.0;
            var bestChannel = 0;
            foreach (var box in boxes)
            {
                if (box.Entries.Count < 2) continue;
                var (range, channel) = WidestChannel(box.Entries);
                var score = range * Math.Sqrt(box.TotalCount);
                if (score > bestScore) { bestScore = score; best = box; bestChannel = channel; }
            }
            if (best is null) break;
            var sorted = best.Entries.OrderBy(e => ChannelValue(e, bestChannel)).ToList();
            var half = best.TotalCount / 2;
            var acc = 0;
            var splitAt = 1;
            for (var i = 0; i < sorted.Count - 1; i++)
            {
                acc += sorted[i].Count;
                if (acc >= half) { splitAt = i + 1; break; }
            }
            var lo = sorted.Take(splitAt).ToList();
            var hi = sorted.Skip(splitAt).ToList();
            best.Entries = lo;
            best.TotalCount = lo.Sum(e => e.Count);
            boxes.Add(new Box { Entries = hi, TotalCount = hi.Sum(e => e.Count) });
        }

        foreach (var box in boxes)
        {
            double sr = 0, sg = 0, sb = 0, sa = 0;
            var count = 0;
            foreach (var e in box.Entries)
            { sr += e.Pr * e.Count; sg += e.Pg * e.Count; sb += e.Pb * e.Count; sa += e.A * e.Count; count += e.Count; }
            var avgA = sa / count;
            byte r = 0, g = 0, b = 0;
            if (avgA > 0.5)
            {
                var f = 255.0 / avgA;
                r = (byte)Math.Clamp(Math.Round(sr / count * f), 0, 255);
                g = (byte)Math.Clamp(Math.Round(sg / count * f), 0, 255);
                b = (byte)Math.Clamp(Math.Round(sb / count * f), 0, 255);
            }
            palette.Add((r, g, b, (byte)Math.Clamp(Math.Round(avgA), 0, 255)));
        }

        return (PaletteBytes(palette), MapWithDithering(rgba, w, h, palette, hasTransparent));
    }

    private static (float Range, int Channel) WidestChannel(List<HistEntry> entries)
    {
        float min0 = 255, min1 = 255, min2 = 255, min3 = 255;
        float max0 = 0, max1 = 0, max2 = 0, max3 = 0;
        foreach (var e in entries)
        {
            if (e.Pr < min0) min0 = e.Pr; if (e.Pr > max0) max0 = e.Pr;
            if (e.Pg < min1) min1 = e.Pg; if (e.Pg > max1) max1 = e.Pg;
            if (e.Pb < min2) min2 = e.Pb; if (e.Pb > max2) max2 = e.Pb;
            if (e.A < min3) min3 = e.A; if (e.A > max3) max3 = e.A;
        }
        // Alpha is weighted heavily so boxes rarely mix opaque pixels
        // with semi-transparent edge pixels (a mixed box would average
        // to alpha < 255 and make solid pixels translucent).
        var ranges = new[] { max0 - min0, max1 - min1, max2 - min2, (max3 - min3) * 4f };
        var ch = 0;
        for (var i = 1; i < 4; i++) if (ranges[i] > ranges[ch]) ch = i;
        return (ranges[ch], ch);
    }

    private static float ChannelValue(HistEntry e, int ch) => ch switch
    { 0 => e.Pr, 1 => e.Pg, 2 => e.Pb, _ => e.A * 4f };

    private static byte[] PaletteBytes(List<(byte R, byte G, byte B, byte A)> palette)
    {
        var bytes = new byte[palette.Count * 4];
        for (var i = 0; i < palette.Count; i++)
        {
            bytes[i * 4] = palette[i].R; bytes[i * 4 + 1] = palette[i].G;
            bytes[i * 4 + 2] = palette[i].B; bytes[i * 4 + 3] = palette[i].A;
        }
        return bytes;
    }

    // Floyd–Steinberg dithering in premultiplied space; the error of
    // each pixel is pushed to its not-yet-visited neighbours.
    private static byte[] MapWithDithering(
        byte[] rgba, int w, int h, List<(byte R, byte G, byte B, byte A)> palette,
        bool hasTransparent)
    {
        var n = w * h;
        // Premultiplied palette (float, 0..255 scale).
        var pal = new float[palette.Count * 4];
        for (var i = 0; i < palette.Count; i++)
        {
            var af = palette[i].A / 255f;
            pal[i * 4] = palette[i].R * af; pal[i * 4 + 1] = palette[i].G * af;
            pal[i * 4 + 2] = palette[i].B * af; pal[i * 4 + 3] = palette[i].A;
        }
        var buf = new float[n * 4];
        for (var i = 0; i < n; i++)
        {
            var af = rgba[i * 4 + 3] / 255f;
            buf[i * 4] = rgba[i * 4] * af; buf[i * 4 + 1] = rgba[i * 4 + 1] * af;
            buf[i * 4 + 2] = rgba[i * 4 + 2] * af; buf[i * 4 + 3] = rgba[i * 4 + 3];
        }
        var indices = new byte[n];
        var cache = new Dictionary<uint, byte>();
        for (var y = 0; y < h; y++)
        for (var x = 0; x < w; x++)
        {
            var i = y * w + x;
            var o = i * 4;
            // Originally fully transparent pixels stay transparent and
            // absorb any dither error instead of spreading a halo.
            if (hasTransparent && rgba[o + 3] == 0) { indices[i] = 0; continue; }
            var pr = Math.Clamp(buf[o], 0, 255);
            var pg = Math.Clamp(buf[o + 1], 0, 255);
            var pb = Math.Clamp(buf[o + 2], 0, 255);
            var pa = Math.Clamp(buf[o + 3], 0, 255);
            byte best;
            if (hasTransparent && pa <= 0.5f) best = 0; // palette[0] is the transparent entry
            else
            {
                var key = (uint)((int)Math.Round(pr) << 24 | (int)Math.Round(pg) << 16
                                 | (int)Math.Round(pb) << 8 | (int)Math.Round(pa));
                if (!cache.TryGetValue(key, out best))
                {
                    var bestDist = double.MaxValue;
                    for (var p = 0; p < palette.Count; p++)
                    {
                        var dr = pr - pal[p * 4]; var dg = pg - pal[p * 4 + 1];
                        var db = pb - pal[p * 4 + 2]; var da = (pa - pal[p * 4 + 3]) * 2;
                        var d = dr * dr + dg * dg + db * db + da * da;
                        if (d < bestDist) { bestDist = d; best = (byte)p; }
                    }
                    cache[key] = best;
                }
            }
            indices[i] = best;
            var er = pr - pal[best * 4]; var eg = pg - pal[best * 4 + 1];
            var eb = pb - pal[best * 4 + 2]; var ea = pa - pal[best * 4 + 3];
            if (er == 0 && eg == 0 && eb == 0 && ea == 0) continue;
            Spread(buf, w, h, x + 1, y, er, eg, eb, ea, 7 / 16f);
            Spread(buf, w, h, x - 1, y + 1, er, eg, eb, ea, 3 / 16f);
            Spread(buf, w, h, x, y + 1, er, eg, eb, ea, 5 / 16f);
            Spread(buf, w, h, x + 1, y + 1, er, eg, eb, ea, 1 / 16f);
        }
        return indices;
    }

    private static void Spread(float[] buf, int w, int h, int x, int y,
        float er, float eg, float eb, float ea, float f)
    {
        if (x < 0 || y < 0 || x >= w || y >= h) return;
        var o = (y * w + x) * 4;
        buf[o] += er * f; buf[o + 1] += eg * f; buf[o + 2] += eb * f; buf[o + 3] += ea * f;
    }

    // ---------- indexed PNG writer ----------

    private static byte[] WriteIndexedPng(byte[] palette, byte[] indices, int w, int h)
    {
        var colors = palette.Length / 4;
        using var ms = new MemoryStream();
        ms.Write(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }, 0, 8);

        var ihdr = new byte[13];
        WriteBe32(ihdr, 0, w); WriteBe32(ihdr, 4, h);
        ihdr[8] = 8; // bit depth
        ihdr[9] = 3; // color type 3: indexed
        WriteChunk(ms, "IHDR", ihdr);

        var plte = new byte[colors * 3];
        for (var i = 0; i < colors; i++)
        {
            plte[i * 3] = palette[i * 4]; plte[i * 3 + 1] = palette[i * 4 + 1]; plte[i * 3 + 2] = palette[i * 4 + 2];
        }
        WriteChunk(ms, "PLTE", plte);

        // tRNS: alpha per palette entry, trailing opaque entries omitted.
        var trnsLen = colors;
        while (trnsLen > 0 && palette[(trnsLen - 1) * 4 + 3] == 255) trnsLen--;
        if (trnsLen > 0)
        {
            var trns = new byte[trnsLen];
            for (var i = 0; i < trnsLen; i++) trns[i] = palette[i * 4 + 3];
            WriteChunk(ms, "tRNS", trns);
        }

        var raw = ApplyFilters(indices, w, h);
        using var idat = new MemoryStream();
        using (var zlib = new ZLibStream(idat, CompressionLevel.Optimal, leaveOpen: true))
            zlib.Write(raw, 0, raw.Length);
        WriteChunk(ms, "IDAT", idat.ToArray());
        WriteChunk(ms, "IEND", Array.Empty<byte>());
        return ms.ToArray();
    }

    // Picks, per row, the filter (None/Sub/Up/Average/Paeth) whose
    // output has the smallest sum of absolute signed bytes — the
    // standard libpng heuristic. bpp is 1 for indexed data.
    private static byte[] ApplyFilters(byte[] indices, int w, int h)
    {
        var raw = new byte[h * (w + 1)];
        var candidates = new byte[5][];
        for (var f = 0; f < 5; f++) candidates[f] = new byte[w];
        for (var y = 0; y < h; y++)
        {
            var row = y * w;
            var prev = y > 0 ? row - w : -1;
            var bestFilter = 0;
            var bestScore = long.MaxValue;
            for (var f = 0; f < 5; f++)
            {
                long score = 0;
                for (var x = 0; x < w; x++)
                {
                    var cur = indices[row + x];
                    var left = x > 0 ? indices[row + x - 1] : (byte)0;
                    var up = prev >= 0 ? indices[prev + x] : (byte)0;
                    var upLeft = prev >= 0 && x > 0 ? indices[prev + x - 1] : (byte)0;
                    var v = f switch
                    {
                        0 => cur,
                        1 => (byte)(cur - left),
                        2 => (byte)(cur - up),
                        3 => (byte)(cur - (left + up) / 2),
                        _ => (byte)(cur - Paeth(left, up, upLeft)),
                    };
                    candidates[f][x] = v;
                    score += Math.Abs((int)(sbyte)v); // (sbyte)(-128) has no positive sbyte
                }
                if (score < bestScore) { bestScore = score; bestFilter = f; }
            }
            var outRow = y * (w + 1);
            raw[outRow] = (byte)bestFilter;
            Array.Copy(candidates[bestFilter], 0, raw, outRow + 1, w);
        }
        return raw;
    }

    private static int Paeth(byte a, byte b, byte c)
    {
        var p = a + b - c;
        var pa = Math.Abs(p - a); var pb = Math.Abs(p - b); var pc = Math.Abs(p - c);
        return pa <= pb && pa <= pc ? a : pb <= pc ? b : c;
    }

    private static void WriteChunk(Stream stream, string type, byte[] data)
    {
        var len = new byte[4];
        WriteBe32(len, 0, data.Length);
        stream.Write(len, 0, 4);
        var typeBytes = System.Text.Encoding.ASCII.GetBytes(type);
        stream.Write(typeBytes, 0, 4);
        stream.Write(data, 0, data.Length);
        var crcData = new byte[4 + data.Length];
        Array.Copy(typeBytes, crcData, 4);
        Array.Copy(data, 0, crcData, 4, data.Length);
        var crc = new byte[4];
        WriteBe32(crc, 0, (int)Crc32(crcData));
        stream.Write(crc, 0, 4);
    }

    private static void WriteBe32(byte[] buf, int offset, int value)
    {
        buf[offset] = (byte)(value >> 24); buf[offset + 1] = (byte)(value >> 16);
        buf[offset + 2] = (byte)(value >> 8); buf[offset + 3] = (byte)value;
    }

    private static readonly uint[] CrcTable = BuildCrcTable();

    private static uint[] BuildCrcTable()
    {
        var table = new uint[256];
        for (uint i = 0; i < 256; i++)
        {
            var c = i;
            for (var k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
            table[i] = c;
        }
        return table;
    }

    private static uint Crc32(byte[] data)
    {
        var c = 0xFFFFFFFFu;
        foreach (var b in data) c = CrcTable[(c ^ b) & 0xFF] ^ (c >> 8);
        return c ^ 0xFFFFFFFFu;
    }
}
