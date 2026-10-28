using Windows.Graphics.Imaging;

namespace Canora.Services;

static partial class ArtworkColor
{
	private static async partial Task<(byte R, byte G, byte B)[]> PixelsAsync(byte[] bytes, double? frame)
	{
		using var stream = new MemoryStream(bytes).AsRandomAccessStream();
		var decoder = await BitmapDecoder.CreateAsync(stream);
		var (x, y, width, height) = Visible((int)decoder.PixelWidth, (int)decoder.PixelHeight, frame);
		// WIC prima scala e poi ritaglia (Bounds è nelle coordinate scalate): si scala tutta l'immagine così che la
		// parte visibile diventi Size×Size, e la si ritaglia da lì.
		double scaleX = Size / (double)width, scaleY = Size / (double)height;
		var scaledWidth = (uint)Math.Max(Size, Math.Round(decoder.PixelWidth * scaleX));
		var scaledHeight = (uint)Math.Max(Size, Math.Round(decoder.PixelHeight * scaleY));
		var transform = new BitmapTransform
		{
			ScaledWidth = scaledWidth,
			ScaledHeight = scaledHeight,
			Bounds = new BitmapBounds
			{
				X = Math.Min((uint)Math.Round(x * scaleX), scaledWidth - Size),
				Y = Math.Min((uint)Math.Round(y * scaleY), scaledHeight - Size),
				Width = Size,
				Height = Size,
			},
			InterpolationMode = BitmapInterpolationMode.Fant,
		};
		var data = await decoder.GetPixelDataAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Straight, transform,
			ExifOrientationMode.IgnoreExifOrientation, ColorManagementMode.DoNotColorManage);
		var bgra = data.DetachPixelData();
		var pixels = new (byte R, byte G, byte B)[Size * Size];
		for (var i = 0; i < pixels.Length; i++)
			pixels[i] = (bgra[i * 4 + 2], bgra[i * 4 + 1], bgra[i * 4]);
		return pixels;
	}
}
