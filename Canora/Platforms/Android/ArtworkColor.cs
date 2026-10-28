using Android.Graphics;

namespace Canora.Services;

static partial class ArtworkColor
{
	private static partial Task<(byte R, byte G, byte B)[]> PixelsAsync(byte[] bytes, double? frame)
	{
		using var full = BitmapFactory.DecodeByteArray(bytes, 0, bytes.Length) ?? throw new InvalidDataException("Copertina non decodificabile");
		var (x, y, width, height) = Visible(full.Width, full.Height, frame);
		using var visible = Bitmap.CreateBitmap(full, x, y, width, height);
		using var small = Bitmap.CreateScaledBitmap(visible, Size, Size, true);
		var argb = new int[Size * Size];
		small.GetPixels(argb, 0, Size, 0, 0, Size, Size);
		return Task.FromResult(argb.Select(p => ((byte)(p >> 16), (byte)(p >> 8), (byte)p)).ToArray());
	}
}
