using Shiny.Maui.Controls.Images;
using Color = Microsoft.Maui.Graphics.Color;

namespace Canora.Services;

// Colore dominante di una copertina, per i gradienti di mini-player e Now Playing, e luminosità delle sue fasce per il
// testo delle hero. Qui solo i conti su una miniatura Size×Size: decodifica e ridimensionamento sono di ogni
// piattaforma (Platforms/*/ArtworkColor.cs, con le API grafiche native).
static partial class ArtworkColor
{
	const int Size = 24;

	public static async Task<Color> FromUrlAsync(string url, CancellationToken token)
	{
		var bytes = await BytesAsync(url, token);
		return Dominant(await Task.Run(() => PixelsAsync(bytes, null), token));
	}

	// Luminosità (0 nero … 1 bianco) delle fasce in alto (back) e in basso (titolo) della parte visibile
	// dell'immagine in un riquadro di proporzioni `frame` (larghezza/altezza) con AspectFill.
	public static async Task<(double Top, double Bottom)> LightnessAsync(string url, double frame, CancellationToken token)
	{
		var bytes = await BytesAsync(url, token);
		return Lightness(await Task.Run(() => PixelsAsync(bytes, frame), token));
	}

	// Dalla cache di ShinyImage: la copertina che si vede e quella che si misura sono un solo download (e una
	// richiesta sola anche quando partono insieme). Le immagini grandi la cache le tiene solo su disco.
	static async Task<byte[]> BytesAsync(string url, CancellationToken token)
	{
		var images = IPlatformApplication.Current!.Services.GetRequiredService<IImageService>();
		var result = await images.GetAsync(new ImageRequest(url), null, token);
		if (!result.Success)
			throw result.Error ?? new IOException($"Copertina non scaricata: {url}");
		return result.Bytes ?? await File.ReadAllBytesAsync(result.FilePath!, token);
	}

	// Miniatura Size×Size riga per riga: di tutta l'immagine (frame null) o della sola parte Visible.
	private static partial Task<(byte R, byte G, byte B)[]> PixelsAsync(byte[] bytes, double? frame);

	// AspectFill: resta la parte centrale, tagliata ai lati (immagine più larga) o sopra e sotto (più alta).
	static (int X, int Y, int Width, int Height) Visible(int width, int height, double? frame)
	{
		if (frame is not { } ratio)
			return (0, 0, width, height);
		var wider = width / (double)height > ratio;
		int visibleW = wider ? (int)(height * ratio) : width, visibleH = wider ? height : (int)(width / ratio);
		return ((width - visibleW) / 2, (height - visibleH) / 2, visibleW, visibleH);
	}

	static (double Top, double Bottom) Lightness((byte R, byte G, byte B)[] pixels)
	{
		double Band(int fromRow, int toRow)
		{
			double sum = 0;
			for (var i = fromRow * Size; i < toRow * Size; i++)
				sum += (0.299 * pixels[i].R + 0.587 * pixels[i].G + 0.114 * pixels[i].B) / 255;
			return sum / ((toRow - fromRow) * Size);
		}

		// Il back occupa il primo quarto, titolo e sottotitolo grosso modo l'ultimo 40%.
		return (Band(0, 6), Band(14, 24));
	}

	// ponytail: media dei pixel pesata per saturazione sulla miniatura, poi portata in una fascia
	// di luminosità in cui il testo bianco resta leggibile. Palette di AndroidX se servisse più fedeltà.
	static Color Dominant((byte R, byte G, byte B)[] pixels)
	{
		double r = 0, g = 0, b = 0, weight = 0;
		foreach (var c in pixels)
		{
			int max = Math.Max(c.R, Math.Max(c.G, c.B)), min = Math.Min(c.R, Math.Min(c.G, c.B));
			var w = 0.05 + (max == 0 ? 0 : (max - min) / (double)max) * (max / 255.0);
			r += c.R * w;
			g += c.G * w;
			b += c.B * w;
			weight += w;
		}

		var average = Color.FromRgb((int)(r / weight), (int)(g / weight), (int)(b / weight));
		average.ToHsl(out var h, out var s, out var l);
		return Color.FromHsla(h, Math.Clamp(s * 1.2, 0.2, 0.7), Math.Clamp(l, 0.22, 0.38));
	}
}
