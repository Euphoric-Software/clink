using Clink.Screenshots.Imaging;

namespace Clink.Screenshots.Editing;

/// <summary>
/// The adjustment stage of the base picture, in the order of the design (docs A3): <see cref="Sharpen"/> first (the unsharp mask of the
/// BASE, amount = Sharpness / 100, sigma 1.5, about 150 ms at 4K), then <see cref="Lut"/> (brightness and contrast, one lookup table,
/// cheap: about 10 ms at 4K) over the sharpened base. The sharpened base depends on (base, sharpness) only, so the composer caches it and
/// a brightness or contrast drag is one LUT pass over the cached sharpened base: it never loses the sharpening and never sharpens again.
/// <see cref="Full"/> is both, and is what the export uses, so the saved picture equals the settled preview. Pure; the input images are
/// never changed.
/// </summary>
internal static class Adjuster
{
    /// <summary>Brightness and contrast of <paramref name="src"/> (sharpness ignored). The input itself when both are 0 (no copy).</summary>
    public static BgraImage Lut(AdjustmentValues v, BgraImage src)
    {
        if (v.Brightness == 0 && v.Contrast == 0) return src;
        var dst = new BgraImage(src.Width, src.Height);
        LutInto(v, src, dst);
        return dst;
    }

    /// <summary>Brightness and contrast of <paramref name="src"/> into <paramref name="dst"/> (the same size, never <paramref name="src"/>): the whole picture is written.</summary>
    public static void LutInto(AdjustmentValues v, BgraImage src, BgraImage dst)
    {
        if (ReferenceEquals(src, dst)) throw new ArgumentException("dst must not be src", nameof(dst));
        Adjustments.Apply(src, dst, src.Bounds, Adjustments.BuildLut(v.Brightness, v.Contrast));
    }

    /// <summary>The unsharp mask (amount = <paramref name="sharpness"/> / 100, sigma 1.5) of the base picture. The input itself when the sharpness is 0.</summary>
    public static BgraImage Sharpen(int sharpness, BgraImage baseImage)
    {
        if (sharpness <= 0) return baseImage;
        var dst = new BgraImage(baseImage.Width, baseImage.Height);
        Adjustments.Unsharp(baseImage, dst, baseImage.Bounds, sharpness / 100.0);
        return dst;
    }

    /// <summary>Both stages, sharpen then LUT. The input itself for the identity.</summary>
    public static BgraImage Full(AdjustmentValues v, BgraImage src) => Lut(v, Sharpen(v.Sharpness, src));
}
