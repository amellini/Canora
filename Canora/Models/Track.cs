using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using YoutubeExplode.Videos;

namespace Canora.Models;

public sealed partial record Track(string Id, string Title, string Artist, TimeSpan? Duration, string ArtworkUrl)
{
	// Episodio di podcast: audio diretto dal feed invece che da YouTube (Id = chiave dell'episodio).
	public string? AudioUrl { get; init; }

	// Album e id di Deezer, per "Album · Artista" sotto il titolo e per aprirne le schede. Solo i brani del catalogo li
	// hanno; un video YouTube ha l'artista per nome (la scheda si cerca per nome) e nessun album.
	public string? Album { get; init; }
	public long? AlbumId { get; init; }
	public long? ArtistId { get; init; }

	[JsonIgnore]
	public bool HasAlbum => !string.IsNullOrEmpty(Album);

	[JsonIgnore]
	public bool IsEpisode => AudioUrl is not null;

	// Brano del catalogo non ancora abbinato a un video YouTube (lo fa il player quando parte).
	public const string CatalogPrefix = "dz:";

	[JsonIgnore]
	public bool IsCatalog => Id.StartsWith(CatalogPrefix, StringComparison.Ordinal);

	// Video YouTube: mqdefault, 16:9 senza bande nere, esiste per ogni video a differenza di maxresdefault.
	// Episodi (anche i video dei canali, il cui Id è la chiave dell'episodio) e brani del catalogo: la loro copertina.
	[JsonIgnore]
	public string ThumbUrl => !IsEpisode && ArtworkUrl.Contains("ytimg.com", StringComparison.Ordinal) ? $"https://i.ytimg.com/vi/{Id}/mqdefault.jpg" : ArtworkUrl;

	[JsonIgnore]
	public string DurationText => Duration is not { } d ? "" : d.TotalHours >= 1 ? d.ToString(@"h\:mm\:ss") : d.ToString(@"m\:ss");

	// ponytail: euristica sui titoli musicali di YouTube ("Artista - Brano (Official Video)"); se non c'è il trattino
	// l'artista è il canale, ripulito dai suffissi " - Topic" / "VEVO" dei canali generati. Alcuni canali ufficiali
	// scrivono al contrario ("In The End - Linkin Park"): se dopo il trattino c'è il nome del canale, è l'artista.
	public static Track From(IVideo video)
	{
		var title = Noise().Replace(video.Title, "").Trim();
		var artist = video.Author.ChannelTitle.Replace(" - Topic", "").Replace("VEVO", "").Trim();
		var split = Dash().Match(title);
		if (split.Success && split.Index > 0)
		{
			var (left, right) = (title[..split.Index].Trim(), title[(split.Index + split.Length)..].Trim());
			(artist, title) = right.Equals(artist, StringComparison.OrdinalIgnoreCase) ? (right, left) : (left, right);
		}
		var artwork = video.Thumbnails.MaxBy(t => t.Resolution.Area)?.Url ?? $"https://i.ytimg.com/vi/{video.Id}/hqdefault.jpg";
		return new(video.Id.Value, title, artist, video.Duration, artwork);
	}

	[GeneratedRegex(@"\s*[\(\[][^\)\]]*\b(official|video|audio|lyrics?|testo|visuali[sz]er|hd|4k|remaster(ed)?)\b[^\)\]]*[\)\]]", RegexOptions.IgnoreCase)]
	private static partial Regex Noise();

	[GeneratedRegex(@"\s+[-–—]\s+")]
	private static partial Regex Dash();
}

// Serializzazione generata a compile time: niente reflection, regge il trimming di Release.
// List<Track> serve solo a importare i recenti della versione precedente (in Preferences).
[JsonSerializable(typeof(List<Track>))]
[JsonSerializable(typeof(List<string>))]
[JsonSerializable(typeof(List<CatalogArtist>))]
[JsonSerializable(typeof(Dictionary<long, string>))]
public sealed partial class AppJson : JsonSerializerContext;
