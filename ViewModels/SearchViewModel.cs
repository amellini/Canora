using System.Collections.ObjectModel;
using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Canora.Models;
using Canora.Services;
using Canora.Theming;
using YoutubeExplode;
using YoutubeExplode.Common;
using YoutubeExplode.Search;

namespace Canora.ViewModels;

// Shade: la tinta del tema (Tile1…Tile6), letta quando la tile si crea. Image: la copertina su tutta la tile,
// come le hero (null finché Deezer non risponde: resta la tinta).
public sealed record Tile(string Title, int Shade, string Query, string? Image = null)
{
	public Color Color { get; } = Themes.Color($"Tile{Shade}");
}

public sealed partial class SearchViewModel : ObservableObject
{
	readonly YoutubeClient youtube;
	readonly PlayerViewModel player;
	readonly LibraryStore library;
	readonly CatalogService catalog;
	readonly PodcastService podcastService;

	public SearchViewModel(YoutubeClient youtube, PlayerViewModel player, LibraryStore library, CatalogService catalog,
		PodcastService podcastService, PodcastsViewModel podcasts, CatalogViewModel catalogView)
	{
		this.youtube = youtube;
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

	// Una pagina di risultati per richiesta; l'enumeratore resta aperto per lo scroll infinito.
	IAsyncEnumerator<Batch<ISearchResult>>? pages;
	CancellationTokenSource? typing;

	public PlayerViewModel Player => player;
	public LibraryStore Library => library;
	public PodcastsViewModel Podcasts { get; }
	public CatalogViewModel Catalog { get; }

	// --- Risultati raggruppati come Spotify: «Dalle tue playlist», Artisti, Album, Brani, Playlist, Podcast, poi YouTube ---

	public ObservableCollection<CatalogArtist> Artists { get; } = [];
	public ObservableCollection<CatalogAlbum> Albums { get; } = [];
	public ObservableCollection<Track> Songs { get; } = [];
	public ObservableCollection<Podcast> PodcastResults { get; } = [];

	// Playlist di Deezer (per genere e per testo) e di YouTube; si aprono nella pagina album.
	public ObservableCollection<CatalogAlbum> CatalogPlaylists { get; } = [];

	// Pillole sotto la barra: tutte spente all'inizio, e allora si vede tutto; accenderne qualcuna restringe la
	// ricerca alle sole sezioni accese. "Brani" comprende anche «Dalle tue playlist» e i video di YouTube.
	// Ogni pillola cambia anche le altre sezioni (accendere la prima nasconde tutte le altre), quindi tutte
	// avvisano VisibleResults.
	[ObservableProperty]
	[NotifyPropertyChangedFor(nameof(VisibleResults))]
	public partial bool ShowArtists { get; private set; }

	[ObservableProperty]
	[NotifyPropertyChangedFor(nameof(VisibleResults))]
	public partial bool ShowAlbums { get; private set; }

	[ObservableProperty]
	[NotifyPropertyChangedFor(nameof(VisibleResults))]
	public partial bool ShowSongs { get; private set; }

	[ObservableProperty]
	[NotifyPropertyChangedFor(nameof(VisibleResults))]
	public partial bool ShowPodcasts { get; private set; }

	[ObservableProperty]
	[NotifyPropertyChangedFor(nameof(VisibleResults))]
	public partial bool ShowPlaylists { get; private set; }

	bool AllSections => !(ShowArtists || ShowAlbums || ShowSongs || ShowPodcasts || ShowPlaylists);
	bool IncludeArtists => AllSections || ShowArtists;
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
			case "albums": ShowAlbums = !ShowAlbums; break;
			case "songs": ShowSongs = !ShowSongs; break;
			case "podcasts": ShowPodcasts = !ShowPodcasts; break;
			case "playlists": ShowPlaylists = !ShowPlaylists; break;
		}
		Refresh();
	}

	public bool ArtistsVisible => IncludeArtists && Artists.Count > 0;
	public bool AlbumsVisible => IncludeAlbums && Albums.Count > 0;
	public bool SongsVisible => IncludeSongs && Songs.Count > 0;
	public bool PodcastsVisible => IncludePodcasts && PodcastResults.Count > 0;
	public bool PlaylistsVisible => IncludePlaylists && CatalogPlaylists.Count > 0;
	public bool PlaylistMatchesVisible => IncludeSongs && HasPlaylistMatches;
	public bool YouTubeVisible => IncludeSongs && Results.Count > 0;

	// I video di YouTube sono gli elementi della lista (scroll infinito): se i brani sono esclusi, lista vuota.
	public IList<Track> VisibleResults => IncludeSongs ? Results : [];

	[ObservableProperty]
	public partial bool IsLoadingCatalog { get; private set; }

	public bool NoResults => HasQuery && !IsBusy && !IsLoadingCatalog && !HasError
		&& !(ArtistsVisible || AlbumsVisible || SongsVisible || PodcastsVisible || PlaylistsVisible || PlaylistMatchesVisible || YouTubeVisible);

	void Refresh()
	{
		OnPropertyChanged(nameof(ArtistsVisible));
		OnPropertyChanged(nameof(AlbumsVisible));
		OnPropertyChanged(nameof(SongsVisible));
		OnPropertyChanged(nameof(PodcastsVisible));
		OnPropertyChanged(nameof(PlaylistsVisible));
		OnPropertyChanged(nameof(PlaylistMatchesVisible));
		OnPropertyChanged(nameof(YouTubeVisible));
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
				Debug.WriteLine(ex); // senza video su YouTube: al tocco lo dirà il player
			}
		}
	}

	// Catalogo e podcast in parallelo a YouTube: ogni sezione compare appena arriva, un errore ne toglie solo una.
	async Task LoadCatalogAsync(string query, CancellationToken token)
	{
		IsLoadingCatalog = true;
		var music = Task.Run(() => catalog.SearchAsync(query, token), token);
		var shows = Task.Run(() => podcastService.SearchAsync(query, token), token);
		try
		{
			var found = await music;
			if (token.IsCancellationRequested)
				return;
			found.Artists.ForEach(Artists.Add);
			found.Albums.ForEach(Albums.Add);
			found.Tracks.ForEach(Songs.Add);
			found.Playlists.ForEach(CatalogPlaylists.Add);
			Refresh();
			_ = PrematchAsync(found.Tracks, token);
		}
		catch (Exception ex) when (!token.IsCancellationRequested)
		{
			Debug.WriteLine(ex);
		}
		catch (OperationCanceledException)
		{
		}
		try
		{
			var found = await shows;
			if (token.IsCancellationRequested)
				return;
			foreach (var podcast in found.Take(4))
				PodcastResults.Add(podcast);
		}
		catch (Exception ex) when (!token.IsCancellationRequested)
		{
			Debug.WriteLine(ex);
		}
		catch (OperationCanceledException)
		{
		}
		finally
		{
			if (!token.IsCancellationRequested)
			{
				IsLoadingCatalog = false;
				Refresh();
			}
		}
	}

	// Brani delle playlist che corrispondono alla query: intestazione «Dalle tue playlist» sopra i risultati.
	public ObservableCollection<Track> PlaylistMatches { get; } = [];

	[ObservableProperty]
	public partial bool HasPlaylistMatches { get; private set; }

	// Risultati di YouTube (senza i brani già fra PlaylistMatches).
	public ObservableCollection<Track> Results { get; } = [];

	[ObservableProperty]
	[NotifyPropertyChangedFor(nameof(IsIdle), nameof(HasQuery))]
	public partial string Query { get; set; } = "";

	// Campo vuoto: si mostrano le tile (ricerche recenti e "Sfoglia tutto") al posto dei risultati.
	public bool IsIdle => string.IsNullOrWhiteSpace(Query);
	public bool HasQuery => !IsIdle;

	[ObservableProperty]
	public partial bool IsBusy { get; private set; }

	[ObservableProperty]
	public partial bool HasError { get; private set; }

	partial void OnQueryChanged(string value) => _ = DebounceAsync(value);

	async Task DebounceAsync(string query)
	{
		typing?.Cancel();
		var cts = typing = new CancellationTokenSource();
		try
		{
			await Task.Delay(450, cts.Token);
			await StartAsync(query, cts.Token);
		}
		catch (OperationCanceledException)
		{
		}
	}

	// Invio sulla tastiera: cerca subito e salva fra le recenti.
	[RelayCommand]
	async Task Submit()
	{
		typing?.Cancel();
		var cts = typing = new CancellationTokenSource();
		library.AddSearch(Query);
		await StartAsync(Query, cts.Token);
	}

	[RelayCommand]
	void UseRecent(string query) => Query = query;

	[RelayCommand]
	void ClearRecent() => library.ClearSearches();

	[RelayCommand]
	Task Retry() => StartAsync(Query, (typing = new CancellationTokenSource()).Token);

	[RelayCommand]
	async Task LoadMore()
	{
		if (pages is { } current && !IsBusy)
			await LoadPageAsync(current);
	}

	[RelayCommand]
	Task Play(Track track)
	{
		library.AddSearch(track.Title);
		// La coda segue l'ordine a schermo: prima i brani delle playlist, poi YouTube.
		return player.PlayAsync([.. PlaylistMatches, .. Results], track, "IN RIPRODUZIONE DALLA RICERCA", $"«{Query.Trim()}» nei brani");
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

	Task StartAsync(string query, CancellationToken token)
	{
		Results.Clear();
		PlaylistMatches.Clear();
		Artists.Clear();
		Albums.Clear();
		Songs.Clear();
		PodcastResults.Clear();
		CatalogPlaylists.Clear();
		HasError = false;
		pages = null;
		if (string.IsNullOrWhiteSpace(query))
		{
			HasPlaylistMatches = false;
			IsLoadingCatalog = false;
			// Campo svuotato: la prossima ricerca riparte senza filtri, cioè con tutte le sezioni.
			ShowArtists = ShowAlbums = ShowSongs = ShowPodcasts = ShowPlaylists = false;
			Refresh();
			return Task.CompletedTask;
		}
		// In testa i brani delle playlist che corrispondono: subito, senza rete, e spesso sono proprio quelli
		// che si cercano. YouTube poi aggiunge il resto, senza ripeterli.
		foreach (var track in library.SearchTracks(query))
			PlaylistMatches.Add(track);
		HasPlaylistMatches = PlaylistMatches.Count > 0;
		_ = LoadCatalogAsync(query, token);
		pages = youtube.Search.GetResultBatchesAsync(query.Trim(), SearchFilter.Video, token).GetAsyncEnumerator(token);
		return LoadPageAsync(pages);
	}

	async Task LoadPageAsync(IAsyncEnumerator<Batch<ISearchResult>> current)
	{
		IsBusy = true;
		try
		{
			var more = await current.MoveNextAsync();
			if (current != pages)
				return; // nel frattempo è partita un'altra ricerca: questi risultati sono vecchi
			if (!more)
			{
				pages = null;
				return;
			}
			// ponytail: le dirette non hanno durata né un audio-only utile, quindi si scartano.
			// Salta anche i brani già in testa perché presenti nelle playlist.
			foreach (var video in current.Current.Items.OfType<VideoSearchResult>())
				if (video.Duration is not null && !PlaylistMatches.Any(m => m.Id == video.Id.Value) && !Results.Any(r => r.Id == video.Id.Value))
					Results.Add(Track.From(video));
		}
		catch (OperationCanceledException)
		{
		}
		catch (Exception ex) when (current == pages)
		{
			Debug.WriteLine(ex);
			HasError = true;
			pages = null;
		}
		finally
		{
			if (current == pages || pages is null)
			{
				IsBusy = false;
				Refresh();
			}
		}
	}
}
