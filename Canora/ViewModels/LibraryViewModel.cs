using System.Collections.ObjectModel;
using System.Collections.Specialized;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Canora.Controls;
using Canora.Models;
using Canora.Services;
using Canora.Views;

namespace Canora.ViewModels;

// Home e Libreria: playlist (elenco + dettaglio aperto) e ascoltati di recente.
public sealed partial class LibraryViewModel : ObservableObject
{
	const string RecentTitle = "Ascoltati di recente";

	readonly PlayerViewModel player;
	readonly LibraryStore library;
	readonly PodcastStore podcastStore;

	public LibraryViewModel(PlayerViewModel player, LibraryStore library, PodcastStore podcastStore, PodcastsViewModel podcasts, CatalogViewModel catalog, CatalogService catalogService)
	{
		this.player = player;
		this.library = library;
		this.podcastStore = podcastStore;
		this.catalog = catalog;
		this.catalogService = catalogService;
		Podcasts = podcasts;
		library.ListensChanged += () => MainThread.BeginInvokeOnMainThread(RebuildForYou);
		library.LikesChanged += () => MainThread.BeginInvokeOnMainThread(RebuildForYou);
		library.Playlists.CollectionChanged += OnPlaylistsChanged;
		library.Recent.CollectionChanged += (_, _) => Notify(nameof(HasRecent), nameof(IsEmpty), nameof(HasAny));
		podcastStore.Followed.CollectionChanged += (_, _) => RebuildItems();
		RebuildItems();
		RebuildForYou();
	}

	public PlayerViewModel Player => player;
	public LibraryStore Library => library;
	public PodcastsViewModel Podcasts { get; }

	// Elenco della Libreria: playlist e podcast seguiti insieme, per ultimo ascolto.
	// ponytail: ricostruito per intero a ogni modifica (decine di righe); diff mirati se diventa lento.
	public ObservableCollection<object> Items { get; } = [];

	void RebuildItems()
	{
		Items.Clear();
		foreach (var item in library.Playlists.Select(p => (Key: p.SortKey, Item: (object)p))
			.Concat(podcastStore.Followed.Select(p => (Key: p.SortKey, Item: (object)p)))
			.OrderByDescending(x => x.Key))
			Items.Add(item.Item);
	}

	// Col nome della presentazione (Profile): "Buonasera, Andrea". Niente "Bentornato/a": il genere non lo chiediamo.
	public string Greeting
	{
		get
		{
			var greeting = DateTime.Now.Hour switch
			{
				< 5 => "Buonanotte",
				< 12 => "Buongiorno",
				< 18 => "Buon pomeriggio",
				_ => "Buonasera",
			};
			return Profile.Name is { Length: > 0 } name ? $"{greeting}, {name}" : greeting;
		}
	}

	// Playlist aperta nel tab Libreria (null = elenco). La pagina radice ci passa al tab quando diventa non null.
	[ObservableProperty]
	[NotifyPropertyChangedFor(nameof(IsDetailOpen), nameof(IsListOpen))]
	public partial Playlist? Selected { get; private set; }

	public bool IsDetailOpen => Selected is not null;
	public bool IsListOpen => Selected is null;

	// "Per te" in Home: la scelta della settimana, i "Mi piace" e i loro simili, poi 4 compilation, una per genere della
	// presentazione e, se i generi non bastano, una per artista più ascoltato nel mese. Qui solo le card: i brani li fa
	// CatalogService quando si aprono. Le pillole sopra cambiano le card (vedi SelectTag).
	readonly CatalogViewModel catalog;
	readonly CatalogService catalogService;

	public ObservableCollection<CatalogAlbum> ForYou { get; } = [];

	// Le card della pillola "Tutti": ci sono appena c'è un gusto da cui partire, e con loro la sezione e le pillole.
	List<CatalogAlbum> allCards = [];
	public bool HasForYou => allCards.Count > 0;

	// Copertina dei generi senza artisti scelti: quella della loro playlist su Deezer, chiesta una volta.
	readonly Dictionary<string, string> genreCovers = [];

	void RebuildForYou()
	{
		List<CatalogAlbum> cards = [];
		var seeds = catalogService.WeeklySeeds();
		if (seeds.Count > 0)
		{
			var cover = library.TopArtwork(7) ?? seeds.Select(s => s.Artist.Picture).FirstOrDefault(p => p.Length > 0) ?? "";
			// Titolo e didascalia corti: nella card da 173 non stanno di più.
			cards.Add(Mix("Scelta settimanale", "Artisti da scoprire", cover, catalogService.WeeklyPickAsync));
		}
		if (library.LikedCount is var liked and > 0)
		{
			var cover = library.LikedArtwork() ?? "";
			cards.Add(new CatalogAlbum(0, "I tuoi Mi piace", liked == 1 ? "1 brano" : $"{liked} brani", "", cover, null, "mix")
				{ Mix = _ => Task.FromResult(library.LikedTracks()), IsLikes = true });
			cards.Add(Mix("Simili ai Mi piace", "Dai brani che ami", cover, catalogService.LikedMixAsync));
		}
		var favorites = Profile.Artists;
		var genreOf = Profile.ArtistGenres;
		var mixes = 0;
		foreach (var genre in Profile.Genres.Take(4))
		{
			var artists = favorites.Where(a => genreOf.GetValueOrDefault(a.Id) == genre).ToList();
			var cover = artists.Select(a => a.Picture).FirstOrDefault(p => p.Length > 0) ?? GenreCover(genre);
			cards.Add(Mix($"Mix {genre}", artists.Count > 0 ? From([.. artists.Select(a => a.Name)]) : "I classici del genere", cover,
				token => catalogService.GenreMixAsync(genre, artists, token)));
			mixes++;
		}
		foreach (var (artist, _) in library.TopArtists(30, 4 - mixes))
			cards.Add(Mix($"Mix {artist}", $"{artist} e artisti simili", library.ArtistArtwork(artist) ?? "", token => catalogService.ArtistMixAsync(artist, token)));

		allCards = cards;
		Notify(nameof(HasForYou), nameof(IsEmpty), nameof(HasAny));
		RebuildTags();
		if (Tags.FirstOrDefault(t => t.IsSelected) is null or { Kind: ForYouTagKind.All })
			Show(cards);
	}

	static CatalogAlbum Mix(string title, string caption, string cover, Func<CancellationToken, Task<List<Track>>> tracks) =>
		new(0, title, caption, cover, cover, null, "mix") { Mix = tracks };

	void Show(List<CatalogAlbum> cards)
	{
		// Tornando sulla Home le card di solito sono le stesse: niente da rifare (né copertine da ricaricare).
		if (cards.Select(Key).SequenceEqual(ForYou.Select(Key)))
			return;
		ForYou.Clear();
		foreach (var card in cards)
			ForYou.Add(card);

		static (string, string, string) Key(CatalogAlbum a) => (a.Title, a.Artist, a.Cover);
	}

	// Pillole: "Tutti", "Nuove uscite", poi i generi (prima quelli dei "Mi piace", poi della presentazione, 5 in tutto) e
	// 3 decenni (quelli dei "Mi piace", completati con anni 90, 80 e 2000), in ordine di tempo.
	public ObservableCollection<ForYouTag> Tags { get; } = [];

	// Mentre arrivano le card di una pillola, e il messaggio se non ne ha nessuna.
	[ObservableProperty]
	public partial bool IsForYouLoading { get; private set; }

	[ObservableProperty]
	[NotifyPropertyChangedFor(nameof(HasForYouMessage))]
	public partial string ForYouMessage { get; private set; } = "";

	public bool HasForYouMessage => ForYouMessage.Length > 0;

	CancellationTokenSource? tagLoading;

	void RebuildTags()
	{
		List<ForYouTag> tags =
		[
			new("Tutti", ForYouTagKind.All),
			new("Nuove uscite", ForYouTagKind.NewReleases),
			.. library.LikedGenres().Select(g => new ForYouTag(g.Name, ForYouTagKind.Genre) { GenreId = g.Id })
				.Concat(Profile.Genres.Select(g => new ForYouTag(g, ForYouTagKind.Genre)))
				.DistinctBy(t => t.Name.ToLowerInvariant())
				.Take(5),
			.. library.LikedDecades().Concat([1990, 1980, 2000]).Distinct().Take(3).Order()
				.Select(d => new ForYouTag(d < 2000 ? $"Anni {d % 100:00}" : $"Anni {d}", ForYouTagKind.Decade) { Decade = d }),
		];
		if (tags.Select(t => (t.Name, t.GenreId)).SequenceEqual(Tags.Select(t => (t.Name, t.GenreId))))
			return;
		var selected = Tags.FirstOrDefault(t => t.IsSelected)?.Name;
		Tags.Clear();
		foreach (var tag in tags)
			Tags.Add(tag);
		// La pillola accesa resta accesa; se non c'è più (un genere tolto dai "Mi piace") si torna a "Tutti".
		var keep = Tags.FirstOrDefault(t => t.Name == selected) ?? Tags[0];
		keep.IsSelected = true;
		if (keep.Kind == ForYouTagKind.All && selected is not null and not "Tutti")
			_ = SelectTag(keep);
	}

	[RelayCommand]
	async Task SelectTag(ForYouTag tag)
	{
		foreach (var t in Tags)
			t.IsSelected = t == tag;
		tagLoading?.Cancel();
		ForYouMessage = "";
		if (tag.Kind == ForYouTagKind.All)
		{
			IsForYouLoading = false;
			Show(allCards);
			return;
		}
		var loading = tagLoading = new CancellationTokenSource();
		Show([]);
		IsForYouLoading = true;
		List<CatalogAlbum> cards;
		try
		{
			cards = await TagCardsAsync(tag, loading.Token);
		}
		catch (OperationCanceledException)
		{
			return; // un'altra pillola: ci pensa lei
		}
		catch (Exception ex)
		{
			Sentry.SentrySdk.CaptureException(ex);
			cards = [];
		}
		if (loading.IsCancellationRequested)
			return;
		IsForYouLoading = false;
		Show(cards);
		ForYouMessage = cards.Count > 0 ? ""
			: tag.Kind == ForYouTagKind.NewReleases ? "Nessuna uscita negli ultimi mesi dai tuoi artisti"
			: "Nessuna proposta, per ora";
	}

	async Task<List<CatalogAlbum>> TagCardsAsync(ForYouTag tag, CancellationToken token)
	{
		switch (tag.Kind)
		{
			case ForYouTagKind.NewReleases:
				return await catalogService.NewReleasesAsync(token);
			case ForYouTagKind.Genre:
			{
				// Il mix del genere parte dagli artisti dei "Mi piace" di quel genere e da quelli scelti nella presentazione.
				var genreOf = Profile.ArtistGenres;
				List<CatalogArtist> artists =
				[
					.. (tag.GenreId is { } id ? library.LikedArtists(4, id).Select(a => a.Artist) : [])
						.Concat(Profile.Artists.Where(a => genreOf.GetValueOrDefault(a.Id) == tag.Name))
						.DistinctBy(a => a.Name.ToLowerInvariant())
						.Take(4),
				];
				var playlists = await catalogService.GenrePlaylistsAsync(tag.Name, tag.GenreId, token);
				var cover = (tag.GenreId is { } g ? library.LikedArtwork(g) : null)
					?? artists.Select(a => a.Picture).FirstOrDefault(p => p.Length > 0)
					?? playlists.FirstOrDefault()?.Cover ?? "";
				var mix = Mix($"Mix {tag.Name}", artists.Count > 0 ? From([.. artists.Select(a => a.Name)]) : "I classici del genere", cover,
					t => catalogService.GenreMixAsync(tag.Name, artists, t));
				return [mix, .. playlists];
			}
			case ForYouTagKind.Decade:
			{
				var genres = Tags.Where(t => t.Kind == ForYouTagKind.Genre).Select(t => t.Name).Take(3);
				var playlists = await catalogService.DecadePlaylistsAsync(tag.Decade, genres, token);
				// In testa i brani dei "Mi piace" usciti in quel decennio, se ce ne sono.
				var mine = library.LikedTracks(tag.Decade);
				if (mine.Count == 0)
					return playlists;
				return [Mix($"I tuoi {tag.Name.ToLowerInvariant()}", mine.Count == 1 ? "1 brano che ami" : $"{mine.Count} brani che ami", mine[0].ArtworkUrl, _ => Task.FromResult(mine)), .. playlists];
			}
			default:
				return allCards;
		}
	}

	static string From(List<string> names) => names switch
	{
		[var a] => $"Da {a}",
		[var a, var b] => $"Da {a} e {b}",
		[var a, var b, ..] => $"Da {a}, {b} e altri",
		_ => "",
	};

	string GenreCover(string genre)
	{
		if (genreCovers.TryGetValue(genre, out var cover))
			return cover;
		genreCovers[genre] = "";
		_ = FetchAsync();
		return "";

		async Task FetchAsync()
		{
			try
			{
				genreCovers[genre] = await catalogService.CoverForAsync(CatalogService.GenreQuery(genre), CancellationToken.None) ?? "";
				RebuildForYou();
			}
			catch (Exception ex)
			{
				Sentry.SentrySdk.Logger.LogWarning("Copertina del genere {0} non caricata: {1}", genre, ex.Message);
			}
		}
	}

	[RelayCommand]
	void OpenForYou(CatalogAlbum mix) => catalog.OpenAlbumCommand.Execute(mix);

	// Impostazioni: token di ListenBrainz (facoltativo, vuoto = solo Deezer).
	public string ListenBrainzToken
	{
		get => Preferences.Default.Get(CatalogService.ListenBrainzTokenKey, "");
		set => Preferences.Default.Set(CatalogService.ListenBrainzTokenKey, value?.Trim() ?? "");
	}

	public bool HasPlaylists => library.Playlists.Count > 0;
	public bool HasRecent => library.Recent.Count > 0;
	public bool IsEmpty => !HasPlaylists && !HasRecent && !HasForYou;
	public bool HasAny => !IsEmpty;

	// I record sono immutabili: dopo una modifica la playlist aperta va ripresa per Id (null se eliminata).
	void OnPlaylistsChanged(object? sender, NotifyCollectionChangedEventArgs e)
	{
		if (Selected is { } open)
			Selected = library.Playlists.FirstOrDefault(p => p.Id == open.Id);
		Notify(nameof(HasPlaylists), nameof(IsEmpty), nameof(HasAny));
		RebuildItems();
	}

	// La pagina radice lo chiama quando si torna sul tab Home, così il saluto segue l'ora; nome e "Per te" seguono anche
	// la presentazione appena rifatta.
	public void Refresh()
	{
		OnPropertyChanged(nameof(Greeting));
		RebuildForYou();
	}

	void Notify(params string[] names)
	{
		foreach (var name in names)
			OnPropertyChanged(name);
	}

	[RelayCommand]
	void OpenPlaylist(Playlist playlist) => Selected = playlist;

	[RelayCommand]
	public void ClosePlaylist() => Selected = null;

	[RelayCommand]
	Task PlayRecent(Track track) => track.IsEpisode
		? player.PlayAsync([track], track, "IN RIPRODUZIONE DAL PODCAST", track.Artist) // l'episodio da solo, come dal dettaglio del podcast
		: player.PlayAsync(library.Recent.Where(t => !t.IsEpisode), track, "IN RIPRODUZIONE DALLA LIBRERIA", RecentTitle); // la coda resta di soli brani

	[RelayCommand]
	Task PlayFromPlaylist(Track track) => Selected is { } playlist ? Play(playlist, track) : Task.CompletedTask;

	// Bottone verde della Playlist Screen: con lo shuffle attivo parte da un brano a caso, come Spotify.
	[RelayCommand]
	Task PlayPlaylist(Playlist? playlist) => (playlist ?? Selected) is { } p ? PlayPlaylistAsync(p) : Task.CompletedTask;

	// Anche dall'assistente vocale.
	public Task PlayPlaylistAsync(Playlist playlist) => playlist.Tracks.Count == 0
		? Task.CompletedTask
		: Play(playlist, playlist.Tracks[player.IsShuffle ? Random.Shared.Next(playlist.Tracks.Count) : 0]);

	// Gli ascolti della playlist li conta il player: uno per sessione, appena un suo brano è ascoltato davvero.
	Task Play(Playlist playlist, Track track) =>
		player.PlayAsync(playlist.Tracks, track, "IN RIPRODUZIONE DALLA PLAYLIST", playlist.Name, playlist.Id);

	[RelayCommand]
	async Task CreatePlaylist()
	{
		if (await ActionPopup.PromptAsync("Nuova playlist", $"La mia playlist n. {library.Playlists.Count + 1}", "Crea") is { } name)
			Selected = library.CreatePlaylist(name);
	}

	// Long-press sulle righe dell'elenco. Nel dettaglio ci sono la matita nell'hero e il cestino.
	[RelayCommand]
	async Task PlaylistMenu(Playlist playlist)
	{
		const string rename = "Rinomina";
		const string delete = "Elimina playlist";
		switch (await ActionPopup.ShowAsync(playlist.Name, playlist.Summary, rename, delete))
		{
			case rename when await ActionPopup.PromptAsync("Rinomina playlist", playlist.Name, "Salva") is { } name:
				library.RenamePlaylist(playlist.Id, name);
				break;
			case delete:
				await ConfirmDeleteAsync(playlist);
				break;
		}
	}

	// Matita nell'hero del dettaglio: il nome arriva già confermato e non vuoto.
	[RelayCommand]
	void Rename(string name)
	{
		if (Selected is { } playlist)
			library.RenamePlaylist(playlist.Id, name);
	}

	// Cestino del dettaglio. Eliminata la playlist aperta, il dettaglio si chiude da sé (vedi OnPlaylistsChanged).
	[RelayCommand]
	Task DeletePlaylist() => Selected is { } playlist ? ConfirmDeleteAsync(playlist) : Task.CompletedTask;

	async Task ConfirmDeleteAsync(Playlist playlist)
	{
		if (await ActionPopup.ShowAsync($"Eliminare «{playlist.Name}»?", "I brani restano su YouTube, si perde solo la playlist.", "Elimina") != "Elimina")
			return;
		library.DeletePlaylist(playlist.Id);
		player.Notify($"«{playlist.Name}» eliminata");
	}

	// Menu dei brani nel dettaglio: quello del player più "Rimuovi da questa playlist".
	[RelayCommand]
	Task TrackMenu(Track track)
	{
		if (Selected is not { } playlist)
			return player.ShowTrackMenuAsync(track);
		return player.ShowTrackMenuAsync(track, playlist.Name, () => library.RemoveFromPlaylist(playlist.Id, track.Id));
	}

	// Icona impostazioni della Home (su Windows anche in fondo alla barra laterale): pagina a sé (Views/SettingsPage).
	[RelayCommand]
	Task Settings() =>
		Application.Current?.Windows.FirstOrDefault()?.Page is { } root ? root.Navigation.PushModalAsync(new SettingsPage(this)) : Task.CompletedTask;

	// Dalle impostazioni: il popup di conferma è anche il riscontro (il toast resterebbe sotto la pagina modale).
	[RelayCommand]
	async Task ClearRecent()
	{
		if (await ActionPopup.ShowAsync("Cancellare gli ascoltati di recente?", null, "Cancella") == "Cancella")
			library.ClearRecent();
	}

	[RelayCommand]
	async Task ClearSearches()
	{
		if (await ActionPopup.ShowAsync("Cancellare le ricerche recenti?", null, "Cancella") == "Cancella")
			library.ClearSearches();
	}

	// Spazio della cache audio (vedi AudioCache): si legge a ogni apertura delle Impostazioni (la pagina è nuova ogni volta).
	public string CacheText => $"Ora occupa {AudioCache.Size / 1048576d:0} MB su 500.";

	[RelayCommand]
	async Task ClearCache()
	{
		if (await ActionPopup.ShowAsync("Svuotare la cache?", "I brani salvati si riscaricano al prossimo ascolto.", "Svuota") != "Svuota")
			return;
		await Task.Run(AudioCache.Clear);
		OnPropertyChanged(nameof(CacheText));
	}
}
