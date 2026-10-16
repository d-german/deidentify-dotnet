using System.Runtime.InteropServices;
using SkiaSharp;

namespace Deidentify.Imaging;

internal static class BgraBitmap
{
    public static SKBitmap Create(byte[] pixels, int width, int height)
    {
        var bitmap = new SKBitmap(new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Premul));
        Marshal.Copy(pixels, 0, bitmap.GetPixels(), pixels.Length);
        return bitmap;
    }
}
