using SkiaSharp;

namespace RouteBaseWeb.Services.Photos;

/// <summary>
/// Produces a photo's rendition set from its original bytes with SkiaSharp: a <c>thumb</c> (~256px) + a
/// <c>preview</c> (chosen max-edge) JPEG, a ThumbHash placeholder, and the actual stored dimensions.
/// SkiaSharp (a native <c>.so</c>), not ImageSharp: ImageSharp 4.x carries a build-time license-key gate.
/// Pure transform over bytes (no DB/FS) so it's unit-testable. Memory-conscious (§12): decodes
/// <b>downsampled</b> via SKCodec so a huge original never materializes at full resolution, and disposes
/// every intermediate bitmap promptly — peak ≈ one frame.
/// </summary>
public sealed class PhotoDerivativeService
{
    public sealed record Derivatives(byte[] Thumb, byte[] Preview, byte[] ThumbHash, int Width, int Height);

    /// <summary>Decode-buffer ceiling (~320 MB RGBA at W×H×4). Guards the PNG pixel-bomb: Skia's PNG codec
    /// can't scale, so a small hostile PNG decodes at full resolution and OOM-kills the box.</summary>
    public const int MaxDecodePixels = 80_000_000;

    private const int ThumbMaxEdge = 256;
    private const int ThumbHashMaxEdge = 100;      // ThumbHash requires ≤100×100 input
    private const int ThumbQuality = 75;
    private const int PreviewQuality = 80;

    private static readonly SKSamplingOptions Sampling = new(SKFilterMode.Linear, SKMipmapMode.Linear);

    /// <summary>Decode → EXIF-orient → thumb + preview + ThumbHash. Throws on an undecodable image
    /// (caller maps to a <c>failed</c> import status).</summary>
    public Derivatives Create(byte[] original, int previewMaxEdge, int maxDecodePixels = MaxDecodePixels)
    {
        using var data = SKData.CreateCopy(original);
        using var codec = SKCodec.Create(data)
            ?? throw new InvalidDataException("Image is not decodable.");

        var origin = codec.EncodedOrigin;
        int maxEdge = Math.Max(codec.Info.Width, codec.Info.Height);

        // Downsampled decode: pick the largest power-of-2 sample whose result still covers previewMaxEdge,
        // so the preview stays sharp but we never decode the full-res frame into RAM.
        int sample = 1;
        while (maxEdge / (sample * 2) >= previewMaxEdge) sample *= 2;
        var scaled = codec.GetScaledDimensions(1f / sample);

        // Pixel-bomb guard: PNG can't native-scale, so `scaled` may be the full dims. Reject before any
        // bitmap alloc. (long) cast avoids int overflow past ~46k². Caller maps this to a failed import.
        if ((long)scaled.Width * scaled.Height > maxDecodePixels)
            throw new InvalidDataException(
                $"Image too large to process ({codec.Info.Width}x{codec.Info.Height}).");

        var decodeInfo = new SKImageInfo(scaled.Width, scaled.Height, SKColorType.Rgba8888, SKAlphaType.Unpremul);

        using var decoded = SKBitmap.Decode(codec, decodeInfo)
            ?? throw new InvalidDataException("Image decode failed.");

        // For TopLeft, `oriented` IS `decoded` and both usings dispose the same object — safe:
        // SKObject.Dispose is handle-guarded and idempotent. The price of leak-proof ownership;
        // the old transfer-ownership shape leaked `decoded` (~320 MB RGBA worst case) whenever
        // ApplyOrientation threw, and its allocations throw first exactly under memory pressure.
        using var oriented = ApplyOrientation(decoded, origin);

        using var previewBmp = ResizeToMaxEdge(oriented, previewMaxEdge);
        using var thumbBmp   = ResizeToMaxEdge(oriented, ThumbMaxEdge);
        using var hashBmp    = ResizeToMaxEdge(oriented, ThumbHashMaxEdge);

        byte[] preview = EncodeJpeg(previewBmp, PreviewQuality);
        byte[] thumb   = EncodeJpeg(thumbBmp, ThumbQuality);
        byte[] hash    = ThumbHash.Encode(hashBmp.Width, hashBmp.Height, hashBmp.GetPixelSpan());

        return new Derivatives(thumb, preview, hash, previewBmp.Width, previewBmp.Height);
    }

    // Resize preserving aspect so the longest edge == maxEdge (never upscales). Always returns a fresh
    // Rgba8888 bitmap (caller disposes), so it never aliases the input.
    private static SKBitmap ResizeToMaxEdge(SKBitmap src, int maxEdge)
    {
        double scale = Math.Min(1.0, (double)maxEdge / Math.Max(src.Width, src.Height));
        int w = Math.Max(1, (int)Math.Round(src.Width * scale));
        int h = Math.Max(1, (int)Math.Round(src.Height * scale));
        var info = new SKImageInfo(w, h, SKColorType.Rgba8888, SKAlphaType.Unpremul);
        return src.Resize(info, Sampling) ?? throw new InvalidDataException("Resize failed.");
    }

    private static byte[] EncodeJpeg(SKBitmap bmp, int quality)
    {
        using var img = SKImage.FromBitmap(bmp);
        using var data = img.Encode(SKEncodedImageFormat.Jpeg, quality);
        return data.ToArray();
    }

    // Applies EXIF orientation to the pixels. Handles the realistic camera set (1/2/3/4/6/8) precisely;
    // the mirror-and-rotate pair (5/7) — which cameras effectively never emit — falls back to the rotation
    // component (correct dims + rotation, drops the rare mirror). Never disposes `src` (the caller owns
    // it); for TopLeft the return IS `src`. Disposes its own `dst` if the canvas work throws.
    private static SKBitmap ApplyOrientation(SKBitmap src, SKEncodedOrigin origin)
    {
        if (origin is SKEncodedOrigin.TopLeft or SKEncodedOrigin.Default)
            return src;

        bool swap = origin is SKEncodedOrigin.LeftTop or SKEncodedOrigin.RightTop
                            or SKEncodedOrigin.RightBottom or SKEncodedOrigin.LeftBottom;
        int w = src.Width, h = src.Height;
        var info = new SKImageInfo(swap ? h : w, swap ? w : h, SKColorType.Rgba8888, SKAlphaType.Unpremul);
        var dst = new SKBitmap(info);
        try
        {
            using var canvas = new SKCanvas(dst);
            switch (origin)
            {
                case SKEncodedOrigin.TopRight:                       // 2 mirror horizontal
                    canvas.Scale(-1, 1, w / 2f, h / 2f);
                    break;
                case SKEncodedOrigin.BottomRight:                    // 3 rotate 180
                    canvas.RotateDegrees(180, w / 2f, h / 2f);
                    break;
                case SKEncodedOrigin.BottomLeft:                     // 4 mirror vertical
                    canvas.Scale(1, -1, w / 2f, h / 2f);
                    break;
                case SKEncodedOrigin.RightTop:                       // 6 rotate 90 CW
                case SKEncodedOrigin.RightBottom:                    // 7 → rotation part
                    canvas.Translate(info.Width, 0);
                    canvas.RotateDegrees(90);
                    break;
                case SKEncodedOrigin.LeftBottom:                     // 8 rotate 270 CW
                case SKEncodedOrigin.LeftTop:                        // 5 → rotation part
                    canvas.Translate(0, info.Height);
                    canvas.RotateDegrees(270);
                    break;
            }
            canvas.DrawBitmap(src, 0, 0);
        }
        catch
        {
            dst.Dispose();
            throw;
        }
        return dst;
    }
}
