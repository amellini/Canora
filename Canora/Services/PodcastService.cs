using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Canora.Models;
using YoutubeExplode;
using YoutubeExplode.Common;

namespace Canora.Services;

// Rete dei podcast: ricerca per titolo sull'iTunes Search API (gratuita, senza chiave, catalogo di Apple Podcasts,
// restituisce l'URL del feed RSS) e lettura del feed. Anche i canali YouTube, che l'app tratta come podcast.
public sealed partial class PodcastService(YoutubeClient youtube)
{
	const int MaxEpisodes = 300;
	static readonly XNamespace Itunes = "http://www.itunes.com/dtds/podcast-1.0.dtd";
	static readonly XNamespace Atom = "http://www.w3.org/2005/Atom";
	static readonly XNamespace YouTubeXml = "http://www.youtube.com/xml/schemas/2015";

	static readonly HttpClient Http = CreateClient();

	static HttpClient CreateClient()
	{
		var http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
		// Alcuni host di feed rifiutano le richieste senza User-Agent.
		http.DefaultRequestHeaders.UserAgent.ParseAdd("Canora/1.0 (Android)");
		return http;
	}

	public async Task<List<Podcast>> SearchAsync(string title, CancellationToken token)
	{
		var url = $"https://itunes.apple.com/search?media=podcast&entity=podcast&country=IT&limit=30&term={Uri.EscapeDataString(title.Trim())}";
		var response = await Http.GetFromJsonAsync(url, PodcastJson.Default.ITunesResponse, token);
		return [.. (response?.Results ?? [])
			.Where(r => !string.IsNullOrWhiteSpace(r.FeedUrl))
			.Select(r => new Podcast(r.CollectionId.ToString(CultureInfo.InvariantCulture), r.FeedUrl!, r.CollectionName ?? "", r.ArtistName ?? "",
				r.ArtworkUrl600 ?? r.ArtworkUrl100 ?? "", "", default, null, null, 0, r.TrackCount))];
	}

	public sealed record Feed(string Description, string? ImageUrl, List<Episode> Episodes);

	// Gli episodi escono senza stato d'ascolto: lo aggiunge PodcastStore unendoli a quelli salvati.
	// latest: bastano gli ultimi (conteggio dei nuovi, assistente); conta solo per i canali YouTube, che costano una
	// richiesta ogni 100 video, mentre un feed RSS arriva comunque intero.
	public Task<Feed> FetchAsync(Podcast podcast, CancellationToken token, bool latest = false) =>
		podcast.IsYouTube ? FetchChannelAsync(podcast, latest ? 100 : 200, token) : FetchFeedAsync(podcast, token);

	async Task<Feed> FetchFeedAsync(Podcast podcast, CancellationToken token)
	{
		await using var stream = await Http.GetStreamAsync(podcast.FeedUrl, token);
		var doc = await XDocument.LoadAsync(stream, LoadOptions.None, token);
		var channel = doc.Root?.Element("channel") ?? throw new InvalidDataException("Feed RSS senza <channel>");

		var description = StripHtml((string?)channel.Element(Itunes + "summary") ?? (string?)channel.Element("description") ?? "");
		var image = (string?)channel.Element(Itunes + "image")?.Attribute("href") ?? (string?)channel.Element("image")?.Element("url");

		var episodes = new List<Episode>();
		foreach (var item in channel.Elements("item"))
		{
			var audio = (string?)item.Element("enclosure")?.Attribute("url");
			if (string.IsNullOrWhiteSpace(audio))
				continue; // niente audio (post testuali, trailer video): non è un episodio ascoltabile
			var guid = (string?)item.Element("guid") ?? audio;
			episodes.Add(new Episode(
				$"{podcast.Id}|{guid}",
				podcast.Id,
				((string?)item.Element("title") ?? "").Trim(),
				audio.Trim(),
				ParseDate((string?)item.Element("pubDate")),
				ParseDuration((string?)item.Element(Itunes + "duration")),
				(string?)item.Element(Itunes + "image")?.Attribute("href"),
				0,
				false));
			if (episodes.Count == MaxEpisodes)
				break; // i feed storici arrivano a migliaia di puntate: le più recenti bastano
		}
		return new Feed(description, image, [.. episodes.OrderByDescending(e => e.Published)]);
	}

	// Gli enclosure passano spesso per redirect di tracciamento (podtrac, chartable…) anche da http a https,
	// che ExoPlayer non segue di default: si risolve qui l'URL finale e lo si porta su https.
	public static async Task<Uri> ResolveAudioAsync(string url, CancellationToken token)
	{
		using var response = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, token);
		var final = response.RequestMessage?.RequestUri ?? new Uri(url);
		return final.Scheme == Uri.UriSchemeHttp ? new UriBuilder(final) { Scheme = Uri.UriSchemeHttps, Port = -1 }.Uri : final;
	}

	// --- Canali YouTube ---

	public async Task<List<Podcast>> SearchChannelsAsync(string query, CancellationToken token)
	{
		var found = new List<Podcast>();
		await foreach (var channel in youtube.Search.GetChannelsAsync(query.Trim(), token))
		{
			// "Artista - Topic" li genera YouTube per la musica: niente video propri da seguire.
			if (channel.Title.EndsWith(" - Topic", StringComparison.Ordinal))
				continue;
			found.Add(Podcast.FromChannel(channel.Id.Value, channel.Title, Avatar(channel.Thumbnails)));
			if (found.Count == 6)
				break;
		}
		return found;
	}

	// Avatar della ricerca: URL senza schema ("//yt3.ggpht.com/…=s176-c-…") e piccolo; la hero lo vuole grande.
	static string Avatar(IReadOnlyList<Thumbnail> thumbnails)
	{
		var url = thumbnails.MaxBy(t => t.Resolution.Area)?.Url ?? "";
		return AvatarSize().Replace(url.StartsWith("//", StringComparison.Ordinal) ? "https:" + url : url, "=s800$1");
	}

	// I video del canale come episodi: gli ultimi `count` caricamenti (playlist UU…, in ordine) meno gli Shorts (UUSH…:
	// gli Short fra gli ultimi N caricamenti sono per forza fra gli ultimi N Short) e meno le dirette in corso o
	// programmate, che non hanno ancora una durata. YoutubeExplode non dà le date: quelle vere vengono dai feed RSS
	// (ultimi 15 video e 15 dirette), le altre servono solo all'ordine e non si mostrano (DateUnknown).
	async Task<Feed> FetchChannelAsync(Podcast channel, int count, CancellationToken token)
	{
		var rest = channel.ChannelId[2..]; // UC… → UU…, UUSH…, UULF…, UULV…
		var uploads = youtube.Playlists.GetVideosAsync("UU" + rest, token).CollectAsync(count).AsTask();
		var shorts = ShortIdsAsync("UUSH" + rest, count, token);
		var dates = Task.WhenAll(RssDatesAsync("UULF" + rest, token), RssDatesAsync("UULV" + rest, token));
		await Task.WhenAll(uploads, shorts, dates);

		var known = dates.Result.SelectMany(d => d).ToDictionary();
		var videos = uploads.Result.Where(v => v.Duration is not null && !shorts.Result.Contains(v.Id.Value)).ToList();
		var published = FillDates([.. videos.Select(v => known.TryGetValue(v.Id.Value, out var date) ? date : (DateTime?)null)]);
		var episodes = videos.Select((v, i) => new Episode($"{channel.Id}|{v.Id}", channel.Id, v.Title, Episode.YouTubeAudio(v.Id.Value),
			published[i], v.Duration, $"https://i.ytimg.com/vi/{v.Id}/mqdefault.jpg", 0, false) { DateUnknown = !known.ContainsKey(v.Id.Value) });
		return new Feed("", null, [.. episodes]);
	}

	async Task<HashSet<string>> ShortIdsAsync(string playlistId, int count, CancellationToken token)
	{
		try
		{
			return [.. (await youtube.Playlists.GetVideosAsync(playlistId, token).CollectAsync(count)).Select(v => v.Id.Value)];
		}
		catch (Exception ex) when (ex is not OperationCanceledException)
		{
			return []; // canale senza Shorts: la playlist non esiste
		}
	}

	static async Task<Dictionary<string, DateTime>> RssDatesAsync(string playlistId, CancellationToken token)
	{
		try
		{
			await using var stream = await Http.GetStreamAsync($"https://www.youtube.com/feeds/videos.xml?playlist_id={playlistId}", token);
			var doc = await XDocument.LoadAsync(stream, LoadOptions.None, token);
			return doc.Root!.Elements(Atom + "entry").ToDictionary(e => (string)e.Element(YouTubeXml + "videoId")!, e => ((DateTimeOffset)e.Element(Atom + "published")!).LocalDateTime);
		}
		catch (HttpRequestException)
		{
			return []; // canale senza dirette (o senza video normali): niente feed
		}
	}

	// Date mancanti, dal più recente: a metà fra la precedente e la prossima nota, così l'ordine regge; dopo l'ultima
	// nota, un minuto prima della precedente.
	// ponytail: una data vera già salvata e poi uscita dal feed resta quella (PodcastStore), mentre le vicine si
	// ricalcolano senza saperla; può scambiarsi di posto con una vicina, nient'altro.
	static DateTime[] FillDates(DateTime?[] known)
	{
		var dates = new DateTime[known.Length];
		var newer = DateTime.Now;
		for (var i = 0; i < known.Length; i++)
		{
			var older = known.Skip(i + 1).FirstOrDefault(d => d is not null);
			dates[i] = newer = known[i] ?? (older is { } o && o < newer ? newer - (newer - o) / 2 : newer.AddMinutes(-1));
		}
		return dates;
	}

	// RFC 822 dei feed, con tutte le sue varianti: "Wed, 02 Oct 2024 13:00:00 GMT", "+0000", "EDT"...
	// Si toglie il giorno della settimana, si traducono i fusi con nome e si mettono i due punti all'offset.
	static DateTime ParseDate(string? text)
	{
		if (string.IsNullOrWhiteSpace(text))
			return DateTime.MinValue;
		var s = text.Trim();
		if (s.IndexOf(',') is var comma and >= 0)
			s = s[(comma + 1)..].Trim();
		s = NamedZone().Replace(s, m => m.Value switch
		{
			"EST" => "-05:00",
			"EDT" => "-04:00",
			"CST" => "-06:00",
			"CDT" => "-05:00",
			"MST" => "-07:00",
			"MDT" => "-06:00",
			"PST" => "-08:00",
			"PDT" => "-07:00",
			_ => "+00:00", // GMT, UT, UTC, Z
		});
		s = NumericZone().Replace(s, "$1$2:$3");
		return DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces, out var date)
			? date.LocalDateTime
			: DateTime.MinValue;
	}

	// itunes:duration: secondi ("3600") oppure "h:mm:ss" / "mm:ss".
	static TimeSpan? ParseDuration(string? text)
	{
		if (string.IsNullOrWhiteSpace(text))
			return null;
		var seconds = 0;
		foreach (var part in text.Trim().Split(':'))
		{
			if (!int.TryParse(part, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value))
				return null;
			seconds = seconds * 60 + value;
		}
		return seconds > 0 ? TimeSpan.FromSeconds(seconds) : null;
	}

	static string StripHtml(string html) => Whitespace().Replace(System.Net.WebUtility.HtmlDecode(Tag().Replace(html, " ")), " ").Trim();

	[GeneratedRegex(@"\b(GMT|UTC|UT|Z|EST|EDT|CST|CDT|MST|MDT|PST|PDT)\b")]
	private static partial Regex NamedZone();

	[GeneratedRegex(@"([+-])(\d{2})(\d{2})$")]
	private static partial Regex NumericZone();

	[GeneratedRegex("<[^>]+>")]
	private static partial Regex Tag();

	[GeneratedRegex(@"\s+")]
	private static partial Regex Whitespace();

	[GeneratedRegex(@"=s\d+(-|$)")]
	private static partial Regex AvatarSize();
}

// Risposta dell'iTunes Search API (solo i campi usati). Deserializzazione generata: regge il trimming.
public sealed record ITunesResponse(List<ITunesPodcast> Results);

public sealed record ITunesPodcast(long CollectionId, string? CollectionName, string? ArtistName, string? FeedUrl,
	string? ArtworkUrl600, string? ArtworkUrl100, int TrackCount);

[JsonSourceGenerationOptions(PropertyNameCaseInsensitive = true)]
[JsonSerializable(typeof(ITunesResponse))]
public sealed partial class PodcastJson : JsonSerializerContext;
