using System.Collections.Concurrent;
using System.Globalization;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Canora.Models;
using YoutubeExplode;
using YoutubeExplode.Common;
using YoutubeExplode.Search;

namespace Canora.Services;

// Ricerca stile Spotify: artisti, album e brani dal catalogo pubblico di Deezer (gratuito, senza chiave), solo
// metadati. L'audio resta YouTube: MatchAsync trova il video con la stessa durata del brano del catalogo.
public sealed class CatalogService
{
	static readonly HttpClient Http = new() { BaseAddress = new Uri("https://api.deezer.com/"), Timeout = TimeSpan.FromSeconds(15) };
	static readonly HttpClient ListenBrainz = new() { BaseAddress = new Uri("https://api.listenbrainz.org/"), Timeout = TimeSpan.FromSeconds(20) };

	// Token dell'account ListenBrainz (Impostazioni): facoltativo, aggiunge le sue proposte al mix della settimana.
	public const string ListenBrainzTokenKey = "ListenBrainzToken";

	readonly YoutubeClient youtube;
	readonly AppDatabase db;
	readonly LibraryStore library;

	// Id del catalogo → id del video, in memoria e su SQLite: un brano già abbinato (anche in una playlist, dove i
	// brani del catalogo restano "dz:") riparte subito, senza rifare la ricerca su YouTube.
	readonly ConcurrentDictionary<string, string> matches = new();

	// Ricerche su YouTube in corso, per brano: l'abbinamento anticipato dei risultati e il tocco sullo stesso brano
	// ne fanno una sola, e il tocco aspetta quella già partita.
	readonly ConcurrentDictionary<string, Task<string>> searching = new();

	public CatalogService(YoutubeClient youtube, AppDatabase db, LibraryStore library)
	{
		this.youtube = youtube;
		this.db = db;
		this.library = library;
		db.Run(c => c.CreateTable<CatalogMatchEntity>());
		NowPlaying.Matched = KnownVideo;
	}

	public sealed record Results(List<CatalogArtist> Artists, List<CatalogAlbum> Albums, List<Track> Tracks, List<CatalogAlbum> Playlists);

	public sealed record ArtistPage(CatalogArtist Artist, List<Track> Top, List<CatalogAlbum> Albums, List<CatalogAlbum> Singles);

	public sealed record AlbumPage(CatalogAlbum Album, List<Track> Tracks);

	public async Task<Results> SearchAsync(string query, CancellationToken token)
	{
		var q = Uri.EscapeDataString(query.Trim());
		var artists = SearchArtistsAsync(query, 8, token);
		var albums = GetAsync(DeezerJson.Default.DzListDzAlbum, $"search/album?q={q}&limit=10", token);
		var tracks = GetAsync(DeezerJson.Default.DzListDzTrack, $"search/track?q={q}&limit=6", token);
		var playlists = PlaylistsAsync(query.Trim(), token);
		await Task.WhenAll(artists, albums, tracks, playlists);
		return new(
			await artists,
			[.. Distinct((await albums).Data).Select(a => ToAlbum(a, a.Artist?.Name)).Select(a => a with { Caption = $"{a.TypeText} · {a.Artist}" })],
			[.. (await tracks).Data.Select(t => ToTrack(t, t.Album?.CoverBig))],
			await playlists);
	}

	// Anche dalla presentazione, per gli artisti che non sono fra quelli del genere.
	public async Task<List<CatalogArtist>> SearchArtistsAsync(string query, int limit, CancellationToken token) =>
		[.. (await GetAsync(DeezerJson.Default.DzListDzArtist, $"search/artist?q={Uri.EscapeDataString(query.Trim())}&limit={limit}", token)).Data.Select(ToArtist)];

	// Playlist: se la ricerca è un genere di Deezer ("rock", "metal", "jazz"…) prima le sue playlist in classifica,
	// poi quelle che Deezer trova per testo ("rock anni 90"), poi quelle di YouTube. Un errore toglie solo le
	// playlist, non il resto della ricerca.
	async Task<List<CatalogAlbum>> PlaylistsAsync(string query, CancellationToken token)
	{
		try
		{
			var genre = await GenreAsync(query);
			var charts = genre is { } id
				? GetAsync(DeezerJson.Default.DzListDzPlaylist, $"chart/{id}/playlists?limit=10", token)
				: Task.FromResult(new DzList<DzPlaylist>());
			var found = GetAsync(DeezerJson.Default.DzListDzPlaylist, $"search/playlist?q={Uri.EscapeDataString(query)}&limit=10", token);
			var videos = YouTubePlaylistsAsync(query, token);
			await Task.WhenAll(charts, found, videos);
			return [.. (await charts).Data.Concat((await found).Data).DistinctBy(p => p.Id).Select(ToPlaylist), .. await videos];
		}
		catch (Exception ex) when (ex is not OperationCanceledException)
		{
			Sentry.SentrySdk.CaptureException(ex);
			return [];
		}
	}

	// Copertina per una tile di ricerca ("Sfoglia tutto" e recenti): quella della prima playlist di Deezer per la query.
	public async Task<string?> CoverForAsync(string query, CancellationToken token) =>
		(await GetAsync(DeezerJson.Default.DzListDzPlaylist, $"search/playlist?q={Uri.EscapeDataString(query)}&limit=1", token))
			.Data.FirstOrDefault()?.PictureBig;

	// La prima pagina di playlist di YouTube (ne bastano 8).
	async Task<List<CatalogAlbum>> YouTubePlaylistsAsync(string query, CancellationToken token)
	{
		await foreach (var batch in youtube.Search.GetResultBatchesAsync(query, SearchFilter.Playlist, token))
			return [.. batch.Items.OfType<PlaylistSearchResult>().Take(8).Select(p =>
			{
				var owner = p.Author?.ChannelTitle ?? "YouTube";
				var cover = p.Thumbnails.MaxBy(t => t.Resolution.Area)?.Url ?? "";
				return new CatalogAlbum(0, p.Title, owner, cover, cover, null, "playlist") { YouTubeId = p.Id.Value, Caption = $"YouTube · {owner}" };
			})];
		return [];
	}

	// I generi di Deezer cambiano di rado: una lettura per sessione. Un nome composto ("Rap/Hip Hop", "Soul & Funk")
	// risponde anche alle sue parti.
	static Task<DzList<DzGenre>>? genres;

	static async Task<long?> GenreAsync(string query)
	{
		var all = await (genres ??= GetAsync(DeezerJson.Default.DzListDzGenre, "genre", CancellationToken.None));
		var match = all.Data.FirstOrDefault(g => g.Id != 0 && (g.Name.Equals(query, StringComparison.OrdinalIgnoreCase)
			|| g.Name.Split(['/', '&'], StringSplitOptions.TrimEntries).Contains(query, StringComparer.OrdinalIgnoreCase)));
		return match?.Id;
	}

	// Pagina artista come su Spotify: i più ascoltati, poi gli album e i singoli (dal più recente).
	public async Task<ArtistPage> ArtistAsync(long id, CancellationToken token)
	{
		var artist = GetAsync(DeezerJson.Default.DzArtist, $"artist/{id}", token);
		var top = GetAsync(DeezerJson.Default.DzListDzTrack, $"artist/{id}/top?limit=10", token);
		var releases = GetAsync(DeezerJson.Default.DzListDzAlbum, $"artist/{id}/albums?limit=100", token);
		await Task.WhenAll(artist, top, releases);
		var all = Distinct((await releases).Data).OrderByDescending(a => a.ReleaseDate).ToList();
		var name = (await artist).Name;
		CatalogAlbum Card(DzAlbum a)
		{
			var album = ToAlbum(a, name);
			return album with { Caption = $"{album.Year} · {album.TypeText}" };
		}
		return new(
			ToArtist(await artist),
			[.. (await top).Data.Select(t => ToTrack(t, t.Album?.CoverBig))],
			[.. all.Where(a => a.RecordType is "album" or "compile").Select(Card)],
			[.. all.Where(a => a.RecordType is "single" or "ep").Select(Card)]);
	}

	// Album di Deezer, oppure una playlist (Deezer o YouTube) mostrata nella stessa pagina. Le playlist si fermano a
	// 200 brani: più di così non si scorrono comunque.
	public async Task<AlbumPage> AlbumAsync(CatalogAlbum album, CancellationToken token)
	{
		if (album.Mix is { } mix)
			return new(album, await mix(token));
		if (album.YouTubeId is { } list)
		{
			List<Track> videos = [];
			await foreach (var video in youtube.Playlists.GetVideosAsync(list, token))
			{
				videos.Add(Track.From(video));
				if (videos.Count == 200)
					break;
			}
			return new(album, videos);
		}
		if (album.IsPlaylist)
		{
			var tracks = await GetAsync(DeezerJson.Default.DzListDzTrack, $"playlist/{album.Id}/tracks?limit=200", token);
			return new(album, [.. tracks.Data.Select(t => ToTrack(t, t.Album?.CoverBig))]);
		}
		var full = await GetAsync(DeezerJson.Default.DzAlbum, $"album/{album.Id}", token);
		return new(ToAlbum(full, full.Artist?.Name), [.. (full.Tracks?.Data ?? []).Select(t => ToTrack(t, full.CoverBig, full))]);
	}

	// --- "Per te" in Home (LibraryViewModel): la scelta della settimana e le compilation, fatte quando si aprono ---

	const string WeeklyPickKey = "WeeklyPick";
	const string WeeklyPickWeekKey = "WeeklyPickWeek";

	// Da dove parte la scelta della settimana: gli artisti più ascoltati negli ultimi 7 giorni, completati con quelli della
	// presentazione (Profile), 4 in tutto. Plays pesa LB Radio: 1 per quelli della presentazione.
	public List<(CatalogArtist Artist, int Plays)> WeeklySeeds() =>
	[
		.. library.TopArtists(7, 4).Select(a => (new CatalogArtist(0, a.Artist, "", "", 0), a.Plays))
			.Concat(Profile.Artists.Select(a => (a, 1)))
			.DistinctBy(s => s.Item1.Name.Trim().ToLowerInvariant())
			.Take(4),
	];

	// Scelta della settimana: musica nuova dalle radio di Deezer degli artisti di partenza e, con il token, da LB Radio;
	// niente brani già ascoltati nel mese né di quegli artisti o di quelli della presentazione. Resta la stessa fino a
	// domenica: la prima apertura della settimana la fa e la salva.
	public async Task<List<Track>> WeeklyPickAsync(CancellationToken token)
	{
		var week = $"{ISOWeek.GetYear(DateTime.Now)}-{ISOWeek.GetWeekOfYear(DateTime.Now)}";
		if (Preferences.Default.Get(WeeklyPickWeekKey, "") == week)
		{
			var json = Preferences.Default.Get(WeeklyPickKey, "");
			if (json.Length > 0 && JsonSerializer.Deserialize(json, AppJson.Default.ListTrack) is { Count: > 0 } saved)
				return saved;
		}
		var seeds = WeeklySeeds();
		HashSet<string> known = [.. seeds.Select(s => s.Artist.Name).Concat(Profile.Artists.Select(a => a.Name)).Select(n => n.Trim().ToLowerInvariant())];
		var pick = await BlendAsync(
			[.. seeds.Select(s => (Func<Task<List<Track>>>)(() => RadioAsync(s.Artist, token))), () => ListenBrainzAsync([.. seeds.Select(s => (s.Artist.Name, s.Plays))], token)],
			library.ListenedKeys(),
			track => !known.Contains(track.Artist.Trim().ToLowerInvariant()));
		if (pick.Count > 0)
		{
			Preferences.Default.Set(WeeklyPickKey, JsonSerializer.Serialize(pick, AppJson.Default.ListTrack));
			Preferences.Default.Set(WeeklyPickWeekKey, week);
		}
		return pick;
	}

	// Compilation di un genere: le radio dei suoi artisti della presentazione (senza, quelle dei primi 3 della playlist del
	// genere) alternate ai brani della playlist stessa, in ordine sparso. Rifatta a ogni apertura.
	public async Task<List<Track>> GenreMixAsync(string genre, List<CatalogArtist> artists, CancellationToken token)
	{
		var tracks = await GenreTracksAsync(GenreQuery(genre), token);
		var seeds = artists.Count > 0 ? artists.Take(4) : tracks.Select(t => t.Artist).OfType<DzArtist>().Where(a => a.Id != VariousArtists).DistinctBy(a => a.Id).Take(3).Select(ToArtist);
		List<Track> playlist = [.. tracks.Select(t => ToTrack(t, t.Album?.CoverBig)).OrderBy(_ => Random.Shared.Next())];
		return await BlendAsync([.. seeds.Select(a => (Func<Task<List<Track>>>)(() => RadioAsync(a, token))), () => Task.FromResult(playlist)], [], _ => true);
	}

	// Compilation di un artista degli ascolti, quando i generi non bastano a farne 4: la sua radio.
	public Task<List<Track>> ArtistMixAsync(string artist, CancellationToken token) =>
		BlendAsync([() => RadioAsync(new CatalogArtist(0, artist, "", "", 0), token)], [], _ => true);

	// Le fonti si alternano un brano a testa fino a 40, senza doppioni né brani in seen (gli ascoltati, per la scelta della
	// settimana); keep scarta il resto. Ogni fonte che fallisce manca e basta.
	// ponytail: alternanza alla pari, senza pesare gli artisti per ascolti; pesi se il mix sbilancia troppo poco.
	static async Task<List<Track>> BlendAsync(IEnumerable<Func<Task<List<Track>>>> sources, HashSet<string> seen, Func<Track, bool> keep)
	{
		var lists = await Task.WhenAll(sources.Select(Safe));
		List<Track> mix = [];
		for (var i = 0; mix.Count < 40 && lists.Any(s => i < s.Count); i++)
			foreach (var track in lists.Where(s => i < s.Count).Select(s => s[i]))
				if (mix.Count < 40 && keep(track) && seen.Add(LibraryStore.ListenKey(track.Artist, track.Title)))
					mix.Add(track);
		return mix;

		static async Task<List<Track>> Safe(Func<Task<List<Track>>> source)
		{
			try
			{
				return await source();
			}
			catch (Exception ex) when (ex is not OperationCanceledException)
			{
				Sentry.SentrySdk.CaptureException(ex);
				return [];
			}
		}
	}

	// L'artista di Deezer per nome (dei video YouTube si sa solo quello). Fra gli omonimi quello con più fan: per "Muse"
	// la band, non il primo risultato.
	public async Task<CatalogArtist?> FindArtistAsync(string name, CancellationToken token)
	{
		var found = await GetAsync(DeezerJson.Default.DzListDzArtist, $"search/artist?q={Uri.EscapeDataString(name)}&limit=10", token);
		var artist = found.Data.Where(a => a.Name.Equals(name, StringComparison.OrdinalIgnoreCase)).MaxBy(a => a.NbFan) ?? found.Data.FirstOrDefault();
		return artist is null ? null : ToArtist(artist);
	}

	// I generi della presentazione e la ricerca di playlist su Deezer che ne dà artisti e brani: la prima trovata è di
	// solito una "Essentials" dei redattori di Deezer. Un genere cercato ha per ricerca il suo nome (GenreQuery).
	public static readonly (string Name, string Query)[] Genres =
	[
		("Pop", "pop hits"),
		("Pop italiano", "pop italiano"),
		("Rock", "rock essentials"),
		("Rock italiano", "rock italiano"),
		("Cantautori", "cantautori italiani"),
		("Indie", "indie essentials"),
		("Indie italiano", "indie italiano"),
		("Rap italiano", "rap italiano"),
		("Hip-Hop", "hip hop essentials"),
		("R&B", "r&b essentials"),
		("Soul & Funk", "soul funk"),
		("Metal", "metal essentials"),
		("Punk", "punk"),
		("Dance", "dance"),
		("Elettronica", "electronic essentials"),
		("Jazz", "jazz essentials"),
		("Blues", "blues essentials"),
		("Classica", "classical essentials"),
		("Colonne sonore", "epic soundtracks"),
		("Lo-Fi", "lofi"),
		("Reggae", "reggae essentials"),
		("Latin", "latin hits"),
		("K-pop", "k-pop"),
	];

	public static string GenreQuery(string genre) => Genres.FirstOrDefault(g => g.Name == genre).Query ?? genre;

	// Gli artisti di un genere per la presentazione (OnboardingViewModel), nell'ordine dei brani della sua playlist.
	// genre/{id}/artists non serve: dà la stessa classifica (quella del paese) per ogni genere.
	public async Task<List<CatalogArtist>> GenreArtistsAsync(string query, CancellationToken token) =>
		[.. (await GenreTracksAsync(query, token)).Select(t => t.Artist).OfType<DzArtist>().Where(a => a.Id != VariousArtists).DistinctBy(a => a.Id).Take(30).Select(ToArtist)];

	// I brani della prima playlist di Deezer per la ricerca del genere.
	async Task<List<DzTrack>> GenreTracksAsync(string query, CancellationToken token)
	{
		var found = await GetAsync(DeezerJson.Default.DzListDzPlaylist, $"search/playlist?q={Uri.EscapeDataString(query)}&limit=1", token);
		if (found.Data.FirstOrDefault() is not { } playlist)
			return [];
		return (await GetAsync(DeezerJson.Default.DzListDzTrack, $"playlist/{playlist.Id}/tracks?limit=100", token)).Data;
	}

	// "Various Artists", l'artista delle compilation su Deezer.
	const long VariousArtists = 5080;

	// La radio di Deezer di un artista: per id, o per nome se non c'è (gli artisti degli ascolti, anche da YouTube).
	async Task<List<Track>> RadioAsync(CatalogArtist artist, CancellationToken token)
	{
		if (artist.Id == 0 && await FindArtistAsync(artist.Name, token) is { } found)
			artist = found;
		if (artist.Id == 0)
			return [];
		var radio = await GetAsync(DeezerJson.Default.DzListDzTrack, $"artist/{artist.Id}/radio?limit=25", token);
		return [.. radio.Data.Select(t => ToTrack(t, t.Album?.CoverBig))];
	}

	// LB Radio (playlist JSPF) con un prompt dagli artisti della settimana, "artist:(Muse):3 artist:(Placebo):1": il
	// peso sono gli ascolti. I brani arrivano senza video: come quelli di Deezer li abbina MatchAsync ("dz:mb:<mbid>").
	async Task<List<Track>> ListenBrainzAsync(List<(string Artist, int Plays)> artists, CancellationToken token)
	{
		var key = Preferences.Default.Get(ListenBrainzTokenKey, "");
		if (key.Length == 0 || artists.Count == 0)
			return [];
		var prompt = string.Join(' ', artists.Select(a => $"artist:({a.Artist.Replace("(", "").Replace(")", "")}):{Math.Clamp(a.Plays, 1, 5)}"));
		using var request = new HttpRequestMessage(HttpMethod.Get, $"1/explore/lb-radio?prompt={Uri.EscapeDataString(prompt)}&mode=easy");
		request.Headers.Authorization = new AuthenticationHeaderValue("Token", key);
		using var response = await ListenBrainz.SendAsync(request, token);
		response.EnsureSuccessStatusCode();
		using var json = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(token), cancellationToken: token);
		if (!json.RootElement.TryGetProperty("payload", out var payload) || !payload.TryGetProperty("jspf", out var jspf)
			|| !jspf.TryGetProperty("playlist", out var playlist) || !playlist.TryGetProperty("track", out var tracks))
			return [];
		List<Track> result = [];
		foreach (var t in tracks.EnumerateArray())
		{
			var title = Text(t, "title");
			var creator = Text(t, "creator");
			if (title.Length == 0 || creator.Length == 0)
				continue;
			var identifier = t.TryGetProperty("identifier", out var id) ? id.ValueKind == JsonValueKind.Array ? id.EnumerateArray().FirstOrDefault().GetString() : id.GetString() : null;
			var mbid = identifier?.Split('/').LastOrDefault() ?? $"{creator}-{title}";
			TimeSpan? duration = t.TryGetProperty("duration", out var ms) && ms.TryGetInt64(out var millis) ? TimeSpan.FromMilliseconds(millis) : null;
			result.Add(new($"{Track.CatalogPrefix}mb:{mbid}", title, creator, duration, CoverArt(t)));
		}
		return result;

		static string Text(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

		// La copertina dal Cover Art Archive, se LB Radio dice quale (estensione MusicBrainz del JSPF).
		static string CoverArt(JsonElement t) =>
			t.TryGetProperty("extension", out var ext)
			&& ext.TryGetProperty("https://musicbrainz.org/doc/jspf#track", out var mb)
			&& mb.TryGetProperty("additional_metadata", out var meta)
			&& meta.TryGetProperty("caa_release_mbid", out var release) && release.ValueKind == JsonValueKind.String
			&& meta.TryGetProperty("caa_id", out var caa) && caa.TryGetInt64(out var caaId)
				? $"https://coverartarchive.org/release/{release.GetString()}/{caaId}-250.jpg"
				: "";
	}

	// Il video già abbinato a un brano del catalogo (in memoria o su SQLite), senza cercarlo.
	public string? KnownVideo(string catalogId)
	{
		if (matches.TryGetValue(catalogId, out var known))
			return known;
		if (db.Run(c => c.Find<CatalogMatchEntity>(catalogId))?.VideoId is not { } saved)
			return null;
		return matches[catalogId] = saved;
	}

	// Il video YouTube del brano del catalogo: si cerca "artista titolo" e si prende quello con la stessa durata
	// (±3 s), preferendo i canali "- Topic" di YouTube Music (audio ufficiale, niente video con intro o finali
	// allungati). Altrimenti il più vicino entro 15 s, altrimenti il primo risultato.
	public async Task<Track> MatchAsync(Track track, CancellationToken token)
	{
		if (!track.IsCatalog)
			return track;
		if (KnownVideo(track.Id) is { } known)
			return track with { Id = known };
		// La ricerca condivisa non si annulla con chi l'ha chiesta (è breve e il risultato resta): ognuno smette solo di
		// aspettarla. Task.Run: sempre asincrona, così il finally non può togliere la voce prima che GetOrAdd la metta.
		var search = searching.GetOrAdd(track.Id, _ => Task.Run(() => FindVideoAsync(track)));
		return track with { Id = await search.WaitAsync(token) };
	}

	async Task<string> FindVideoAsync(Track track)
	{
		try
		{
			var target = track.Duration?.TotalSeconds ?? 0;
			List<VideoSearchResult> videos = [];
			await foreach (var batch in youtube.Search.GetResultBatchesAsync($"{track.Artist} {track.Title}", SearchFilter.Video))
			{
				videos.AddRange(batch.Items.OfType<VideoSearchResult>().Where(v => v.Duration is not null));
				break; // la prima pagina basta: il brano giusto, se c'è, sta lì
			}
			double Gap(VideoSearchResult v) => Math.Abs(v.Duration!.Value.TotalSeconds - target);
			var best = videos.Where(v => Gap(v) <= 3).OrderByDescending(v => v.Author.ChannelTitle.EndsWith("- Topic", StringComparison.Ordinal)).FirstOrDefault()
				?? videos.Where(v => Gap(v) <= 15).MinBy(Gap)
				?? videos.FirstOrDefault()
				?? throw new InvalidOperationException($"Nessun video per «{track.Artist} {track.Title}»");
			var match = new CatalogMatchEntity { CatalogId = track.Id, VideoId = best.Id.Value };
			matches[track.Id] = match.VideoId;
			db.Write(c => c.InsertOrReplace(match));
			return match.VideoId;
		}
		finally
		{
			searching.TryRemove(track.Id, out _); // abbinato (ormai in matches) o fallito: si riprova la volta dopo
		}
	}

	static async Task<T> GetAsync<T>(System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> type, string path, CancellationToken token) where T : new() =>
		await Http.GetFromJsonAsync(path, type, token) ?? new T(); // Deezer risponde 200 anche agli errori, senza "data"

	// Deezer ripete lo stesso album in più edizioni (deluxe, ristampe con data diversa): basta la prima.
	static IEnumerable<DzAlbum> Distinct(IEnumerable<DzAlbum> albums) => albums.DistinctBy(a => a.Title.Trim().ToLowerInvariant());

	static CatalogArtist ToArtist(DzArtist a) => new(a.Id, a.Name, a.PictureBig ?? "", a.PictureXl ?? a.PictureBig ?? "", a.NbFan);

	static CatalogAlbum ToAlbum(DzAlbum a, string? artist) =>
		new(a.Id, a.Title, artist ?? "", a.CoverBig ?? "", a.CoverXl ?? a.CoverBig ?? "", a.ReleaseDate, a.RecordType ?? "album");

	static CatalogAlbum ToPlaylist(DzPlaylist p)
	{
		var owner = p.User?.Name ?? "Deezer";
		return new(p.Id, p.Title, owner, p.PictureBig ?? "", p.PictureXl ?? p.PictureBig ?? "", null, "playlist") { Caption = $"Playlist · {owner}" };
	}

	// album: quello della pagina album, i cui brani arrivano senza il proprio.
	static Track ToTrack(DzTrack t, string? cover, DzAlbum? album = null) =>
		new($"{Track.CatalogPrefix}{t.Id}", t.Title, t.Artist?.Name ?? "", TimeSpan.FromSeconds(t.Duration), cover ?? "")
		{
			Album = (album ?? t.Album)?.Title,
			AlbumId = (album ?? t.Album)?.Id,
			ArtistId = (t.Artist ?? album?.Artist)?.Id,
		};
}

[SQLite.Table("CatalogMatches")]
sealed class CatalogMatchEntity
{
	[SQLite.PrimaryKey]
	public string CatalogId { get; set; } = "";
	public string VideoId { get; set; } = "";
}

// Le risposte di Deezer (snake_case), con serializzazione generata a compile time: regge il trimming di Release.
public sealed class DzList<T>
{
	public List<T> Data { get; set; } = [];
}

public sealed class DzArtist
{
	public long Id { get; set; }
	public string Name { get; set; } = "";
	public string? PictureBig { get; set; }
	public string? PictureXl { get; set; }
	public int NbFan { get; set; }
}

public sealed class DzAlbum
{
	public long Id { get; set; }
	public string Title { get; set; } = "";
	public string? CoverBig { get; set; }
	public string? CoverXl { get; set; }
	public string? ReleaseDate { get; set; }
	public string? RecordType { get; set; }
	public DzArtist? Artist { get; set; }
	public DzList<DzTrack>? Tracks { get; set; }
}

public sealed class DzTrack
{
	public long Id { get; set; }
	public string Title { get; set; } = "";
	public int Duration { get; set; }
	public DzArtist? Artist { get; set; }
	public DzAlbum? Album { get; set; }
}

public sealed class DzPlaylist
{
	public long Id { get; set; }
	public string Title { get; set; } = "";
	public string? PictureBig { get; set; }
	public string? PictureXl { get; set; }
	public DzArtist? User { get; set; }
}

public sealed class DzGenre
{
	public long Id { get; set; }
	public string Name { get; set; } = "";
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower)]
[JsonSerializable(typeof(DzList<DzArtist>))]
[JsonSerializable(typeof(DzList<DzAlbum>))]
[JsonSerializable(typeof(DzList<DzTrack>))]
[JsonSerializable(typeof(DzList<DzPlaylist>))]
[JsonSerializable(typeof(DzList<DzGenre>))]
[JsonSerializable(typeof(DzArtist))]
[JsonSerializable(typeof(DzAlbum))]
sealed partial class DeezerJson : JsonSerializerContext;
