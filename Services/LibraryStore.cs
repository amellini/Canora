using System.Collections.ObjectModel;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Canora.Models;
using SQLite;

namespace Canora.Services;

// Playlist e ascoltati di recente su SQLite (AppDatabase); le ObservableCollection sono la copia in memoria a cui si
// lega la UI, caricata all'avvio. Ogni modifica aggiorna la collection subito e accoda una singola istruzione SQL.
// Le ricerche recenti (10 stringhe) restano in Preferences.
public sealed partial class LibraryStore
{
	const int MaxRecent = 20;
	const int MaxSearches = 10;
	const int ListenDays = 30;

	readonly AppDatabase db;

	// Copia in memoria del tracker (vedi AddListen): le statistiche leggono da qui, sempre allineate anche mentre la
	// scrittura su SQLite è ancora in coda. Il lock perché il mix della settimana le legge da un thread del pool.
	readonly Dictionary<string, ListenEntity> listens = [];
	readonly Lock listensGate = new();

	// Un ascolto in più nel tracker (la Home aggiorna "Per te").
	public event Action? ListensChanged;

	// Sempre ordinate per ultimo ascolto (o creazione, se mai ascoltate): la più recente in cima.
	public ObservableCollection<Playlist> Playlists { get; } = [];
	public ObservableCollection<Track> Recent { get; } = [];
	public ObservableCollection<string> Searches { get; } = new(LoadSearches());

	public LibraryStore(AppDatabase db)
	{
		this.db = db;
		// Se il database non risponde le liste restano vuote: l'app parte comunque (vedi AppDatabase).
		var loaded = db.Run(c =>
		{
			c.CreateTable<PlaylistEntity>();
			c.CreateTable<PlaylistTrackEntity>();
			c.CreateTable<RecentEntity>();
			c.CreateTable<ListenEntity>();
			ImportOldPreferences(c);
			var tracks = c.Table<PlaylistTrackEntity>().OrderBy(t => t.Position).ToList().ToLookup(t => t.PlaylistId);
			var playlists = c.Table<PlaylistEntity>().ToList()
				.Select(p => new Playlist(p.Id, p.Name, [.. tracks[p.Id].Select(t => t.ToTrack())], p.Created, p.LastPlayed, p.PlayCount))
				.OrderByDescending(p => p.SortKey)
				.ToList();
			var recent = c.Table<RecentEntity>().OrderByDescending(r => r.PlayedAt).Take(MaxRecent).ToList().Select(r => r.ToTrack()).ToList();
			var month = DateTime.Now.AddDays(-ListenDays);
			return (playlists, recent, listens: c.Table<ListenEntity>().Where(l => l.LastPlayed >= month).ToList());
		});
		foreach (var p in loaded.playlists ?? [])
			Playlists.Add(p);
		foreach (var t in loaded.recent ?? [])
			Recent.Add(t);
		foreach (var l in loaded.listens ?? [])
			listens[l.TrackId] = l;
	}

	public Playlist CreatePlaylist(string name)
	{
		var playlist = Playlist.Create(name.Trim());
		Playlists.Insert(0, playlist);
		Write(db => db.Insert(new PlaylistEntity { Id = playlist.Id, Name = playlist.Name, Created = playlist.Created }));
		return playlist;
	}

	// false se il brano era già nella playlist.
	public bool AddToPlaylist(string playlistId, Track track)
	{
		var index = IndexOf(playlistId);
		if (index < 0 || Playlists[index].Tracks.Any(t => t.Id == track.Id))
			return false;
		var position = Playlists[index].Tracks.Count;
		Playlists[index] = Playlists[index] with { Tracks = [.. Playlists[index].Tracks, track] };
		Write(db => db.Insert(PlaylistTrackEntity.From(playlistId, position, track)));
		return true;
	}

	public void RemoveFromPlaylist(string playlistId, string trackId)
	{
		var index = IndexOf(playlistId);
		if (index < 0)
			return;
		Playlists[index] = Playlists[index] with { Tracks = [.. Playlists[index].Tracks.Where(t => t.Id != trackId)] };
		// I buchi in Position non contano: serve solo a ordinare.
		Write(db => db.Execute("DELETE FROM PlaylistTracks WHERE PlaylistId = ? AND TrackId = ?", playlistId, trackId));
	}

	public void RenamePlaylist(string playlistId, string name)
	{
		var index = IndexOf(playlistId);
		if (index < 0)
			return;
		Playlists[index] = Playlists[index] with { Name = name.Trim() };
		var renamed = Playlists[index].Name;
		Write(db => db.Execute("UPDATE Playlists SET Name = ? WHERE Id = ?", renamed, playlistId));
	}

	public void DeletePlaylist(string playlistId)
	{
		var index = IndexOf(playlistId);
		if (index < 0)
			return;
		Playlists.RemoveAt(index);
		Write(db => db.RunInTransaction(() =>
		{
			db.Execute("DELETE FROM PlaylistTracks WHERE PlaylistId = ?", playlistId);
			db.Execute("DELETE FROM Playlists WHERE Id = ?", playlistId);
		}));
	}

	// Un ascolto = uno o più brani ascoltati di fila partendo dalla playlist (lo decide il player); la playlist sale
	// in cima alla Libreria.
	public void MarkPlayed(string playlistId)
	{
		var index = IndexOf(playlistId);
		if (index < 0)
			return;
		var played = Playlists[index] with { LastPlayed = DateTime.Now, PlayCount = Playlists[index].PlayCount + 1 };
		Playlists[index] = played;
		Playlists.Move(index, 0);
		Write(db => db.Execute("UPDATE Playlists SET LastPlayed = ?, PlayCount = ? WHERE Id = ?", played.LastPlayed, played.PlayCount, playlistId));
	}

	public void AddRecent(Track track)
	{
		var existing = Recent.FirstOrDefault(t => t.Id == track.Id);
		if (existing is not null)
			Recent.Remove(existing);
		Recent.Insert(0, track);
		while (Recent.Count > MaxRecent)
			Recent.RemoveAt(Recent.Count - 1);
		var played = DateTime.Now;
		Write(db =>
		{
			db.InsertOrReplace(RecentEntity.From(track, played));
			db.Execute("DELETE FROM RecentTracks WHERE TrackId NOT IN (SELECT TrackId FROM RecentTracks ORDER BY PlayedAt DESC LIMIT ?)", MaxRecent);
		});
	}

	// Tracker degli ascolti di musica dell'ultimo mese: una riga per brano (ascoltato 20 volte = una riga con Plays 20),
	// e via le righe non riascoltate da più di ListenDays giorni. Base per le proposte in base agli ascolti.
	// ponytail: Plays conta dal primo ascolto ancora in finestra, non è una finestra mobile esatta (un brano ascoltato
	// ogni settimana si porta dietro anche gli ascolti di più di un mese fa); una tabella di eventi se serve esattezza.
	public void AddListen(Track track)
	{
		var now = DateTime.Now;
		lock (listensGate)
		{
			listens[track.Id] = listens.TryGetValue(track.Id, out var known)
				? known with { Title = track.Title, Artist = track.Artist, ArtworkUrl = track.ArtworkUrl, Plays = known.Plays + 1, LastPlayed = now }
				: new ListenEntity { TrackId = track.Id, Title = track.Title, Artist = track.Artist, DurationTicks = track.Duration?.Ticks, ArtworkUrl = track.ArtworkUrl, Plays = 1, FirstPlayed = now, LastPlayed = now };
			foreach (var stale in listens.Values.Where(l => l.LastPlayed < now.AddDays(-ListenDays)).ToList())
				listens.Remove(stale.TrackId);
		}
		ListensChanged?.Invoke();
		Write(db =>
		{
			db.Execute("""
				INSERT INTO Listens (TrackId, Title, Artist, DurationTicks, ArtworkUrl, Plays, FirstPlayed, LastPlayed)
				VALUES (?, ?, ?, ?, ?, 1, ?, ?)
				ON CONFLICT(TrackId) DO UPDATE SET Plays = Plays + 1, LastPlayed = excluded.LastPlayed,
					Title = excluded.Title, Artist = excluded.Artist, ArtworkUrl = excluded.ArtworkUrl
				""", track.Id, track.Title, track.Artist, track.Duration?.Ticks, track.ArtworkUrl, now, now);
			db.Execute("DELETE FROM Listens WHERE LastPlayed < ?", now.AddDays(-ListenDays));
		});
	}

	// Gli artisti più ascoltati negli ultimi giorni (somma degli ascolti dei loro brani), il nome come l'ultimo brano.
	public List<(string Artist, int Plays)> TopArtists(int days, int count)
	{
		var since = DateTime.Now.AddDays(-days);
		lock (listensGate)
			return [.. listens.Values.Where(l => l.LastPlayed >= since && l.Artist.Length > 0)
				.GroupBy(l => l.Artist.Trim().ToLowerInvariant())
				.Select(g => (Artist: g.MaxBy(l => l.LastPlayed)!.Artist.Trim(), Plays: g.Sum(l => l.Plays)))
				.OrderByDescending(a => a.Plays)
				.Take(count)];
	}

	// Il brano più ascoltato negli ultimi giorni (la sua copertina fa da copertina al mix).
	public string? TopArtwork(int days)
	{
		var since = DateTime.Now.AddDays(-days);
		lock (listensGate)
			return listens.Values.Where(l => l.LastPlayed >= since).MaxBy(l => l.Plays)?.ArtworkUrl;
	}

	// La copertina del brano più ascoltato nel mese di un artista (la sua compilation in "Per te").
	public string? ArtistArtwork(string artist)
	{
		var key = artist.Trim().ToLowerInvariant();
		lock (listensGate)
			return listens.Values.Where(l => l.Artist.Trim().ToLowerInvariant() == key).MaxBy(l => l.Plays)?.ArtworkUrl;
	}

	// I brani ascoltati nel mese, per chiave artista|titolo (gli id non servono: Deezer e YouTube ne hanno di diversi).
	public HashSet<string> ListenedKeys()
	{
		lock (listensGate)
			return [.. listens.Values.Select(l => ListenKey(l.Artist, l.Title))];
	}

	// "Fire (Remastered 2011)" e "fire" sono lo stesso brano: minuscole, senza parentesi né punteggiatura.
	public static string ListenKey(string artist, string title) =>
		$"{Simplify(artist)}|{Simplify(title)}";

	static string Simplify(string text) => NotLetters().Replace(Brackets().Replace(text.ToLowerInvariant(), ""), "");

	[GeneratedRegex(@"[\(\[][^\)\]]*[\)\]]")]
	private static partial Regex Brackets();

	[GeneratedRegex(@"[^\p{L}\p{N}]")]
	private static partial Regex NotLetters();

	public void ClearRecent()
	{
		Recent.Clear();
		Write(db => db.DeleteAll<RecentEntity>());
	}

	public void AddSearch(string query)
	{
		query = query.Trim();
		if (query.Length == 0)
			return;
		var existing = Searches.FirstOrDefault(s => string.Equals(s, query, StringComparison.OrdinalIgnoreCase));
		if (existing is not null)
			Searches.Remove(existing);
		Searches.Insert(0, query);
		while (Searches.Count > MaxSearches)
			Searches.RemoveAt(Searches.Count - 1);
		SaveSearches();
	}

	public void ClearSearches()
	{
		Searches.Clear();
		SaveSearches();
	}

	// Brani delle playlist che corrispondono a una ricerca (tab Cerca e assistente vocale), playlist ascoltate
	// più di recente prima.
	public IEnumerable<Track> SearchTracks(string query) =>
		Playlists.SelectMany(p => p.Tracks).DistinctBy(t => t.Id).Where(t => Matches($"{t.Title} {t.Artist}", query));

	// Ogni parola cercata deve comparire nel testo, ignorando maiuscole e accenti ("beyonce" → "Beyoncé").
	public static bool Matches(string text, string query)
	{
		var words = Fold(query).Split(' ', StringSplitOptions.RemoveEmptyEntries);
		var folded = Fold(text);
		return words.Length > 0 && words.All(folded.Contains);
	}

	static string Fold(string text) =>
		string.Concat(text.Normalize(NormalizationForm.FormD).Where(c => CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark))
			.ToLowerInvariant();

	int IndexOf(string playlistId)
	{
		for (var i = 0; i < Playlists.Count; i++)
			if (Playlists[i].Id == playlistId)
				return i;
		return -1;
	}

	void Write(Action<SQLiteConnection> change) => db.Write(change);

	// ponytail: migrazione una tantum dalla versione che teneva i recenti in Preferences; togliere quando
	// nessun dispositivo ha più la build vecchia.
	static void ImportOldPreferences(SQLiteConnection db)
	{
		var json = Preferences.Default.Get("Recent", "");
		if (json.Length > 0 && db.Table<RecentEntity>().Count() == 0)
		{
			try
			{
				var old = JsonSerializer.Deserialize(json, AppJson.Default.ListTrack) ?? [];
				var now = DateTime.Now;
				db.InsertAll(old.Select((t, i) => RecentEntity.From(t, now.AddSeconds(-i))));
			}
			catch (JsonException)
			{
			}
		}
		Preferences.Default.Remove("Recent");
		Preferences.Default.Remove("Liked");
	}

	static List<string> LoadSearches()
	{
		try
		{
			var json = Preferences.Default.Get(nameof(Searches), "");
			var saved = json.Length == 0 ? [] : JsonSerializer.Deserialize(json, AppJson.Default.ListString) ?? [];
			// Le ricerche salvate a metà quando un tocco salvava il testo scritto ("rage a" accanto a "Rage Against the
			// Machine"): via quelle che sono l'inizio di un'altra. Si salva al prossimo AddSearch.
			// ponytail: a ogni avvio, quindi cade anche una ricerca breve voluta ("pop" accanto a "pop hits").
			return [.. saved.Where(s => !saved.Any(o => o.Length > s.Length && o.StartsWith(s, StringComparison.OrdinalIgnoreCase)))];
		}
		catch (JsonException)
		{
			return [];
		}
	}

	void SaveSearches() => Preferences.Default.Set(nameof(Searches), JsonSerializer.Serialize(Searches.ToList(), AppJson.Default.ListString));
}

// Righe SQLite: sqlite-net vuole classi con costruttore vuoto e proprietà scrivibili, quindi niente record.
[Table("Playlists")]
sealed class PlaylistEntity
{
	[PrimaryKey]
	public string Id { get; set; } = "";
	public string Name { get; set; } = "";
	public DateTime Created { get; set; }
	public DateTime? LastPlayed { get; set; }
	public int PlayCount { get; set; }
}

[Table("PlaylistTracks")]
sealed class PlaylistTrackEntity
{
	[PrimaryKey, AutoIncrement]
	public int RowId { get; set; }
	[Indexed]
	public string PlaylistId { get; set; } = "";
	public int Position { get; set; }
	public string TrackId { get; set; } = "";
	public string Title { get; set; } = "";
	public string Artist { get; set; } = "";
	public long? DurationTicks { get; set; }
	public string ArtworkUrl { get; set; } = "";
	public string? Album { get; set; }
	public long? AlbumId { get; set; }
	public long? ArtistId { get; set; }

	public static PlaylistTrackEntity From(string playlistId, int position, Track t) => new()
	{
		PlaylistId = playlistId,
		Position = position,
		TrackId = t.Id,
		Title = t.Title,
		Artist = t.Artist,
		DurationTicks = t.Duration?.Ticks,
		ArtworkUrl = t.ArtworkUrl,
		Album = t.Album,
		AlbumId = t.AlbumId,
		ArtistId = t.ArtistId,
	};

	public Track ToTrack() => new(TrackId, Title, Artist, DurationTicks is { } ticks ? TimeSpan.FromTicks(ticks) : null, ArtworkUrl)
		{ Album = Album, AlbumId = AlbumId, ArtistId = ArtistId };
}

[Table("Listens")]
sealed record ListenEntity
{
	[PrimaryKey]
	public string TrackId { get; set; } = "";
	public string Title { get; set; } = "";
	public string Artist { get; set; } = "";
	public long? DurationTicks { get; set; }
	public string ArtworkUrl { get; set; } = "";
	public int Plays { get; set; }
	public DateTime FirstPlayed { get; set; }
	[Indexed]
	public DateTime LastPlayed { get; set; }

}

[Table("RecentTracks")]
sealed class RecentEntity
{
	[PrimaryKey]
	public string TrackId { get; set; } = "";
	public string Title { get; set; } = "";
	public string Artist { get; set; } = "";
	public long? DurationTicks { get; set; }
	public string ArtworkUrl { get; set; } = "";
	public string? Album { get; set; }
	public long? AlbumId { get; set; }
	public long? ArtistId { get; set; }
	[Indexed]
	public DateTime PlayedAt { get; set; }

	public static RecentEntity From(Track t, DateTime playedAt) => new()
	{
		TrackId = t.Id,
		Title = t.Title,
		Artist = t.Artist,
		DurationTicks = t.Duration?.Ticks,
		ArtworkUrl = t.ArtworkUrl,
		Album = t.Album,
		AlbumId = t.AlbumId,
		ArtistId = t.ArtistId,
		PlayedAt = playedAt,
	};

	public Track ToTrack() => new(TrackId, Title, Artist, DurationTicks is { } ticks ? TimeSpan.FromTicks(ticks) : null, ArtworkUrl)
		{ Album = Album, AlbumId = AlbumId, ArtistId = ArtistId };
}
