using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using CommunityToolkit.Mvvm.ComponentModel;
using Canora.Models;

namespace Canora.Services;

// Testi per il karaoke da LRCLIB (lrclib.net: gratuito, senza chiave), sincronizzati riga per riga in formato LRC
// ("[mm:ss.xx] riga"). Prima la ricerca esatta per artista, titolo e durata (±2 s), poi quella libera col brano
// di durata più vicina. Senza versione sincronizzata resta il testo semplice, mostrato senza seguire la musica.
public sealed partial class LyricsService
{
	static readonly HttpClient Http = CreateClient();

	// Per brano: anche "nessun testo", così riaprire il player non rifà la ricerca.
	readonly ConcurrentDictionary<string, Lyrics?> cache = new();

	static HttpClient CreateClient()
	{
		var http = new HttpClient { BaseAddress = new Uri("https://lrclib.net/api/"), Timeout = TimeSpan.FromSeconds(15) };
		http.DefaultRequestHeaders.UserAgent.ParseAdd("Canora/1.0"); // LRCLIB chiede di identificare l'app
		return http;
	}

	public async Task<Lyrics?> GetAsync(Track track, CancellationToken token)
	{
		if (cache.TryGetValue(track.Id, out var known))
			return known;
		var seconds = track.Duration?.TotalSeconds ?? 0;
		var artist = Uri.EscapeDataString(track.Artist);
		var title = Uri.EscapeDataString(track.Title);
		var found = seconds > 0 ? await GetExactAsync($"get?artist_name={artist}&track_name={title}&duration={Math.Round(seconds)}", token) : null;
		if (found is not { SyncedLyrics: not null })
		{
			var results = await Http.GetFromJsonAsync($"search?artist_name={artist}&track_name={title}", LrcLibJson.Default.ListLrcLibTrack, token) ?? [];
			found = results.Where(r => r.SyncedLyrics is not null).MinBy(r => seconds > 0 ? Math.Abs(r.Duration - seconds) : 0)
				?? found
				?? results.FirstOrDefault(r => r.PlainLyrics is not null || r.Instrumental);
		}
		var lyrics = found is null ? null : Parse(found);
		cache[track.Id] = lyrics;
		return lyrics;
	}

	static async Task<LrcLibTrack?> GetExactAsync(string path, CancellationToken token)
	{
		using var response = await Http.GetAsync(path, token);
		if (response.StatusCode == HttpStatusCode.NotFound)
			return null;
		response.EnsureSuccessStatusCode();
		return await response.Content.ReadFromJsonAsync(LrcLibJson.Default.LrcLibTrack, token);
	}

	static Lyrics? Parse(LrcLibTrack found)
	{
		if (found.Instrumental)
			return new([new LyricLine(null, "Brano strumentale")], IsSynced: false);
		if (found.SyncedLyrics is { } synced)
		{
			// Una riga può avere più tempi ("[00:12.00][01:40.00] ritornello"); le righe vuote sono pause strumentali.
			var lines = new List<LyricLine>();
			foreach (var raw in synced.Split('\n'))
			{
				// Solo i tempi in testa alla riga: quelli dentro il testo (rari) restano testo.
				var tags = new List<Match>();
				for (var tag = Tag().Match(raw); tag.Success && tag.Index == (tags.Count == 0 ? 0 : tags[^1].Index + tags[^1].Length); tag = tag.NextMatch())
					tags.Add(tag);
				if (tags.Count == 0)
					continue;
				var text = raw[(tags[^1].Index + tags[^1].Length)..].Trim();
				foreach (var tag in tags)
				{
					var at = TimeSpan.FromMinutes(int.Parse(tag.Groups[1].Value, CultureInfo.InvariantCulture))
						+ TimeSpan.FromSeconds(double.Parse(tag.Groups[2].Value, CultureInfo.InvariantCulture));
					lines.Add(new LyricLine(at, text.Length == 0 ? "♪" : text));
				}
			}
			if (lines.Count > 0)
				return new([.. lines.OrderBy(l => l.At)], IsSynced: true);
		}
		if (string.IsNullOrWhiteSpace(found.PlainLyrics))
			return null;
		return new([.. found.PlainLyrics.Split('\n').Select(l => new LyricLine(null, l.Trim()))], IsSynced: false);
	}

	[GeneratedRegex(@"\[(\d+):(\d+(?:\.\d+)?)\]")]
	private static partial Regex Tag();
}

public sealed record Lyrics(IReadOnlyList<LyricLine> Lines, bool IsSynced);

// Una riga del testo: At è quando inizia (null nel testo non sincronizzato). IsCurrent e IsSung li aggiorna il
// player man mano che il brano avanza, e la vista karaoke li colora.
public sealed partial class LyricLine(TimeSpan? at, string text) : ObservableObject
{
	public TimeSpan? At { get; } = at;
	public string Text { get; } = text;

	[ObservableProperty]
	public partial bool IsCurrent { get; set; }

	// Il testo non sincronizzato non segue la musica: tutto in chiaro, come le righe già cantate.
	[ObservableProperty]
	public partial bool IsSung { get; set; } = at is null;
}

public sealed class LrcLibTrack
{
	public double Duration { get; set; }
	public bool Instrumental { get; set; }
	public string? PlainLyrics { get; set; }
	public string? SyncedLyrics { get; set; }
}

// Serializzazione generata a compile time come per Deezer: regge il trimming di Release.
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(LrcLibTrack))]
[JsonSerializable(typeof(List<LrcLibTrack>))]
sealed partial class LrcLibJson : JsonSerializerContext;
