using CoreGraphics;
using Foundation;
using UIKit;

namespace Canora.Services;

static partial class ArtworkColor
{
	private static partial Task<(byte R, byte G, byte B)[]> PixelsAsync(byte[] bytes, double? frame)
	{
		using var full = UIImage.LoadFromData(NSData.FromArray(bytes))?.CGImage ?? throw new InvalidDataException("Copertina non decodificabile");
		var (x, y, width, height) = Visible((int)full.Width, (int)full.Height, frame);
		// Il ritaglio di CGImage ha l'origine in alto a sinistra, come Visible; il contesto Size×Size scala mentre disegna e
		// tiene in memoria la riga in alto per prima.
		using var visible = full.WithImageInRect(new CGRect(x, y, width, height)) ?? full;
		var rgba = new byte[Size * Size * 4];
		using (var space = CGColorSpace.CreateDeviceRGB())
		using (var context = new CGBitmapContext(rgba, Size, Size, 8, Size * 4, space, CGImageAlphaInfo.PremultipliedLast))
		{
			context.InterpolationQuality = CGInterpolationQuality.High;
			context.DrawImage(new CGRect(0, 0, Size, Size), visible);
		}
		var pixels = new (byte R, byte G, byte B)[Size * Size];
		for (var i = 0; i < pixels.Length; i++)
			pixels[i] = (rgba[i * 4], rgba[i * 4 + 1], rgba[i * 4 + 2]);
		return Task.FromResult(pixels);
	}
}
