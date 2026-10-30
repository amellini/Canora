using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Canora.Models;
using Canora.Services;
using Canora.Theming;
using Sentry;

namespace Canora.ViewModels;

// Shade: la tinta del tema (Tile1…Tile6), letta quando la tile si crea. Image: la copertina su tutta la tile,
// come le hero (null finché Deezer non risponde: resta la tinta).
public sealed record Tile(string Title, int Shade, string Query, string? Image = null)
{
	public Color Color { get; } = Themes.Color($"Tile{Shade}");
}

public sealed partial class SearchViewModel : ObservableObject
{
	readonly PlayerViewModel player;
	readonly LibraryStore library;
	readonly CatalogService catalog;
	readonly PodcastService podcastService;

	public SearchViewModel(PlayerViewModel player, LibraryStore library, CatalogService catalog,
		PodcastService podcastService, PodcastsViewModel podcasts, CatalogViewModel catalogView)
	{
		this.player = player;
		this.library = library;
		this.catalog = catalog;
		this.podcastService = podcastService;
		Podcasts = podcasts;
		Catalog = catalogView;
		library.Searches.CollectionChanged += (_, _) => OnSearchesChanged();
		Themes.Changed += () =>
		{
			OnPropertyChanged(nameof(RecentTiles));
			OnPropertyChanged(nameof(BrowseTiles));
		};
	}

	CancellationTokenSource? typing;

	public PlayerViewModel Player => player;
	public LibraryStore Library => library;
	public PodcastsViewModel Podcasts { get; }
	public CatalogViewModel Catalog { get; }

	// --- Risultati raggruppati come Spotify: «Dalle tue playlist», Artisti, Canali, Album, Brani, Playlist, Podcast ---

	public ObservableCollection<CatalogArtist> Artists { get; } = [];
	public ObservableCollection<CatalogAlbum> Albums { get; } = [];
	public ObservableCollection<Track> Songs { get; } = [];
	public ObservableCollection<Podcast> PodcastResults { get; } = [];

	// Canali YouTube: si mostrano come gli artisti e si aprono (e si seguono) come i podcast.
	public ObservableCollection<Podcast> Channels { get; } = [];

	// Playlist di Deezer (per genere e per testo) e di YouTube; si aprono nella pagina album.
	public ObservableCollection<CatalogAlbum> CatalogPlaylists { get; } = [];

	// Pillole sotto la barra: tutte spente all'inizio, e allora si vede tutto; accenderne qualcuna restringe la
	// ricerca alle sole sezioni accese. "Brani" comprende anche «Dalle tue playlist».
	[ObservableProperty]
	public partial bool ShowArtists { get; private set; }

	[ObservableProperty]
	public partial bool ShowChannels { get; private set; }

	[ObservableProperty]
	public partial bool ShowAlbums { get; private set; }

	[ObservableProperty]
	public partial bool ShowSongs { get; private set; }

	[ObservableProperty]
	public partial bool ShowPodcasts { get; private set; }

	[ObservableProperty]
	public partial bool ShowPlaylists { get; private set; }

	bool AllSections => !(ShowArtists || ShowChannels || ShowAlbums || ShowSongs || ShowPodcasts || ShowPlaylists);
	bool IncludeArtists => AllSections || ShowArtists;
	bool IncludeChannels => AllSections || ShowChannels;
	bool IncludeAlbums => AllSections || ShowAlbums;
	bool IncludeSongs => AllSections || ShowSongs;
	bool IncludePodcasts => AllSections || ShowPodcasts;
	bool IncludePlaylists => AllSections || ShowPlaylists;

	[RelayCommand]
	void ToggleFilter(string filter)
	{
		switch (filter)
		{
			case "artists": ShowArtists = !ShowArtists; break;
			case "channels": ShowChannels = !ShowChannels; break;
			case "albums": ShowAlbums = !ShowAlbums; break;
			case "songs": ShowSongs = !ShowSongs; break;
			case "podcasts": ShowPodcasts = !ShowPodcasts; break;
			case "playlists": ShowPlaylists = !ShowPlaylists; break;
		}
		Refresh();
	}

	public bool ArtistsVisible => IncludeArtists && Artists.Count > 0;
	public bool ChannelsVisible => IncludeChannels && Channels.Count > 0;
	public bool AlbumsVisible => IncludeAlbums && Albums.Count > 0;
	public bool SongsVisible => IncludeSongs && Songs.Count > 0;
	public bool PodcastsVisible => IncludePodcasts && PodcastResults.Count > 0;
	public bool PlaylistsVisible => IncludePlaylists && CatalogPlaylists.Count > 0;
	public bool PlaylistMatchesVisible => IncludeSongs && HasPlaylistMatches;

	[ObservableProperty]
	public partial bool IsLoadingCatalog { get; private set; }

	public bool NoResults => HasQuery && !IsLoadingCatalog
		&& !(ArtistsVisible || ChannelsVisible || AlbumsVisible || SongsVisible || PodcastsVisible || PlaylistsVisible || PlaylistMatchesVisible);

	void Refresh()
	{
		OnPropertyChanged(nameof(ArtistsVisible));
		OnPropertyChanged(nameof(ChannelsVisible));
		OnPropertyChanged(nameof(AlbumsVisible));
		OnPropertyChanged(nameof(SongsVisible));
		OnPropertyChanged(nameof(PodcastsVisible));
		OnPropertyChanged(nameof(PlaylistsVisible));
		OnPropertyChanged(nameof(PlaylistMatchesVisible));
		OnPropertyChanged(nameof(NoResults));
	}

	// Un tocco su un risultato è una ricerca "usata": fra le recenti finisce il nome di ciò che si è toccato e non il
	// testo scritto, che può essere a metà ("rage a" → "Rage Against the Machine"), come su Spotify.
	[RelayCommand]
	Task PlaySong(Track track)
	{
		library.AddSearch(track.Title);
		return player.PlayAsync(Songs, track, "IN RIPRODUZIONE DALLA RICERCA", $"«{Query.Trim()}» nei brani");
	}

	[RelayCommand]
	void OpenArtist(CatalogArtist artist)
	{
		library.AddSearch(artist.Name);
		Catalog.OpenArtistCommand.Execute(artist);
	}

	[RelayCommand]
	void OpenAlbum(CatalogAlbum album)
	{
		library.AddSearch(album.Title);
		Catalog.OpenAlbumCommand.Execute(album);
	}

	[RelayCommand]
	void OpenPodcast(Podcast podcast)
	{
		library.AddSearch(podcast.Title);
		Podcasts.OpenCommand.Execute(podcast);
	}

	// Un brano del catalogo suona dopo averne trovato il video su YouTube (~0,5 s, solo la prima volta: poi è salvato).
	// I primi dei risultati si abbinano subito in background, uno alla volta (~26 KB l'uno), così il tocco parte senza
	// quell'attesa; una nuova ricerca ferma il resto. ponytail: i primi PrematchCount, tutti se si toccano spesso gli ultimi.
	const int PrematchCount = 4;

	async Task PrematchAsync(IEnumerable<Track> tracks, CancellationToken token)
	{
		foreach (var track in tracks.Take(PrematchCount))
		{
			try
			{
				await catalog.MatchAsync(track, token);
			}
			catch (OperationCanceledException)
			{
				return;
			}
			catch (Exception ex)
			{
				SentrySdk.Logger.LogWarning("Video YouTube non trovato: {0}", ex.Message); // al tocco lo dirà il player
			}
		}
	}

	// Catalogo, podcast e canali in parallelo: ogni sezione compare appena arriva, un errore ne toglie solo una.
	async Task LoadCatalogAsync(string query, CancellationToken token)
	{
		IsLoadingCatalog = true;
		var music = Task.Run(() => catalog.SearchAsync(query, token), token);
		var shows = Task.Run(() => podcastService.SearchAsync(query, token), token);
		var channels = Task.Run(() => podcastService.SearchChannelsAsync(query, token), token);
		await Task.WhenAll(
			Section(music, found =>
			{
				found.Artists.ForEach(Artists.Add);
				found.Albums.ForEach(Albums.Add);
				found.Tracks.ForEach(Songs.Add);
				found.Playlists.ForEach(CatalogPlaylists.Add);
				_ = PrematchAsync(found.Tracks, token);
			}),
			Section(shows, found => found.Take(4).ToList().ForEach(PodcastResults.Add)),
			Section(channels, found => found.ForEach(Channels.Add)));
		if (!token.IsCancellationRequested)
		{
			IsLoadingCatalog = false;
			Refresh();
		}

		async Task Section<T>(Task<T> loading, Action<T> show)
		{
			try
			{
				var found = await loading;
				if (token.IsCancellationRequested)
					return;
				show(found);
				Refresh();
			}
			catch (Exception ex)
			{
				if (!token.IsCancellationRequested)
					SentrySdk.CaptureException(ex);
			}
		}
	}

	// Brani delle playlist che corrispondono alla query: intestazione «Dalle tue playlist» sopra i risultati.
	public ObservableCollection<Track> PlaylistMatches { get; } = [];

	[ObservableProperty]
	public partial bool HasPlaylistMatches { get; private set; }

	[ObservableProperty]
	[NotifyPropertyChangedFor(nameof(IsIdle), nameof(HasQuery))]
	public partial string Query { get; set; } = "";

	// Campo vuoto: si mostrano le tile (ricerche recenti e "Sfoglia tutto") al posto dei risultati.
	public bool IsIdle => string.IsNullOrWhiteSpace(Query);
	public bool HasQuery => !IsIdle;

	partial void OnQueryChanged(string value) => _ = DebounceAsync(value);

	async Task DebounceAsync(string query)
	{
		typing?.Cancel();
		var cts = typing = new CancellationTokenSource();
		try
		{
			await Task.Delay(450, cts.Token);
			Start(query, cts.Token);
		}
		catch (OperationCanceledException)
		{
		}
	}

	// Invio sulla tastiera: cerca subito e salva fra le recenti.
	[RelayCommand]
	void Submit()
	{
		typing?.Cancel();
		var cts = typing = new CancellationTokenSource();
		library.AddSearch(Query);
		Start(Query, cts.Token);
	}

	[RelayCommand]
	void UseRecent(string query) => Query = query;

	[RelayCommand]
	void ClearRecent() => library.ClearSearches();

	// Brano di «Dalle tue playlist»: la coda è quell'elenco.
	[RelayCommand]
	Task Play(Track track)
	{
		library.AddSearch(track.Title);
		return player.PlayAsync(PlaylistMatches, track, "IN RIPRODUZIONE DALLA RICERCA", $"«{Query.Trim()}» nelle tue playlist");
	}

	// Search Screen del Figma: "Your top genres" → le ultime ricerche, "Browse all" → ricerche pronte.
	// Stessi colori e stessa forma delle tile del design.
	public IEnumerable<Tile> RecentTiles => library.Searches.Take(4).Select((query, i) => new Tile(query, RecentShades[i % RecentShades.Length], query, ImageFor(query)));

	public bool HasRecentSearches => library.Searches.Count > 0;

	// Ricreate a ogni lettura (8 record): così prendono i colori del tema attivo e le copertine arrivate nel frattempo.
	public IReadOnlyList<Tile> BrowseTiles => [.. Browse.Select(t => new Tile(t.Title, t.Shade, t.Query, ImageFor(t.Query)))];

	static readonly (string Title, int Shade, string Query)[] Browse =
	[
		("Pop", 1, "pop hits"),
		("Rap italiano", 2, "rap italiano"),
		("Hit del momento", 3, "hit del momento"),
		("Nuove uscite", 4, "nuove uscite"),
		("Classifiche", 1, "top 50 italia"),
		("Live", 5, "live concerto"),
		("Lo-fi", 5, "lofi hip hop"),
		("Relax", 6, "musica rilassante"),
	];

	static readonly int[] RecentShades = [1, 2, 6, 3];

	// Copertine delle tile per query, chieste a Deezer una volta per sessione alla prima lettura; arrivata una,
	// le tile si ricreano. Solo dal thread UI (le letture vengono dai binding).
	// ponytail: in memoria, al riavvio si richiedono; su SQLite se mai pesassero all'avvio.
	readonly Dictionary<string, string?> tileImages = [];

	string? ImageFor(string query)
	{
		if (tileImages.TryGetValue(query, out var image))
			return image;
		tileImages[query] = null; // richiesta in corso
		_ = LoadTileImageAsync(query);
		return null;
	}

	async Task LoadTileImageAsync(string query)
	{
		try
		{
			tileImages[query] = await Task.Run(() => catalog.CoverForAsync(query, CancellationToken.None));
		}
		catch (Exception ex) when (ex is not OutOfMemoryException)
		{
			tileImages.Remove(query); // senza rete: si riprova alla prossima lettura
			return;
		}
		OnPropertyChanged(nameof(BrowseTiles));
		OnPropertyChanged(nameof(RecentTiles));
	}

	void OnSearchesChanged()
	{
		OnPropertyChanged(nameof(RecentTiles));
		OnPropertyChanged(nameof(HasRecentSearches));
	}

	void Start(string query, CancellationToken token)
	{
		PlaylistMatches.Clear();
		Artists.Clear();
		Channels.Clear();
		Albums.Clear();
		Songs.Clear();
		PodcastResults.Clear();
		CatalogPlaylists.Clear();
		if (string.IsNullOrWhiteSpace(query))
		{
			HasPlaylistMatches = false;
			IsLoadingCatalog = false;
			// Campo svuotato: la prossima ricerca riparte senza filtri, cioè con tutte le sezioni.
			ShowArtists = ShowChannels = ShowAlbums = ShowSongs = ShowPodcasts = ShowPlaylists = false;
			Refresh();
			return;
		}
		// In testa i brani delle playlist che corrispondono: subito, senza rete, e spesso sono proprio quelli
		// che si cercano.
		foreach (var track in library.SearchTracks(query))
			PlaylistMatches.Add(track);
		HasPlaylistMatches = PlaylistMatches.Count > 0;
		_ = LoadCatalogAsync(query, token);
		Refresh();
	}
}
