using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Canora.Controls;
using Canora.Models;
using Canora.Services;
using Sentry;

namespace Canora.ViewModels;

// Pagine Artista e Album del catalogo, sopra il tab corrente come il dettaglio podcast (vedi MainPage).
// Al massimo due livelli: un artista e, sopra, uno dei suoi album; Back toglie l'ultimo.
public sealed partial class CatalogViewModel(CatalogService catalog, PlayerViewModel player, LibraryStore library) : ObservableObject
{
	CancellationTokenSource? artistLoad;
	CancellationTokenSource? albumLoad;

	public PlayerViewModel Player => player;

	[ObservableProperty]
	[NotifyPropertyChangedFor(nameof(IsArtistVisible), nameof(IsOpen))]
	public partial CatalogArtist? Artist { get; private set; }

	[ObservableProperty]
	[NotifyPropertyChangedFor(nameof(IsArtistVisible), nameof(IsAlbumVisible), nameof(IsOpen))]
	public partial CatalogAlbum? Album { get; private set; }

	public bool IsArtistVisible => Artist is not null && Album is null;
	public bool IsAlbumVisible => Album is not null;
	public bool IsOpen => Artist is not null || Album is not null;

	public ObservableCollection<Track> TopTracks { get; } = [];
	public ObservableCollection<CatalogAlbum> Albums { get; } = [];
	public ObservableCollection<CatalogAlbum> Singles { get; } = [];
	public ObservableCollection<Track> AlbumTracks { get; } = [];

	[ObservableProperty]
	[NotifyPropertyChangedFor(nameof(HasAlbums), nameof(HasSingles))]
	public partial bool IsLoadingArtist { get; private set; }

	[ObservableProperty]
	public partial bool IsLoadingAlbum { get; private set; }

	[ObservableProperty]
	public partial bool LoadFailed { get; private set; }

	public bool HasAlbums => Albums.Count > 0;
	public bool HasSingles => Singles.Count > 0;

	// Quante volte hai ascoltato i brani dell'album, nell'hero; niente finché è zero.
	public string? AlbumPlaysText => AlbumTracks.Sum(library.Plays) switch
	{
		0 => null,
		1 => "1 ascolto",
		var plays => $"{plays} ascolti",
	};

	void OnListensChanged() => MainThread.BeginInvokeOnMainThread(() => OnPropertyChanged(nameof(AlbumPlaysText)));

	[RelayCommand]
	async Task OpenArtist(CatalogArtist artist)
	{
		albumLoad?.Cancel();
		artistLoad?.Cancel();
		var cts = artistLoad = new CancellationTokenSource();
		Album = null;
		Artist = artist;
		TopTracks.Clear();
		Albums.Clear();
		Singles.Clear();
		LoadFailed = false;
		IsLoadingArtist = true;
		try
		{
			var page = await Task.Run(() => catalog.ArtistAsync(artist.Id, cts.Token), cts.Token);
			if (cts.IsCancellationRequested)
				return;
			Artist = page.Artist with { Name = string.IsNullOrEmpty(page.Artist.Name) ? artist.Name : page.Artist.Name };
			foreach (var track in page.Top)
				TopTracks.Add(track);
			foreach (var album in page.Albums)
				Albums.Add(album);
			foreach (var single in page.Singles)
				Singles.Add(single);
		}
		catch (OperationCanceledException)
		{
		}
		catch (Exception ex)
		{
			SentrySdk.CaptureException(ex);
			LoadFailed = true;
		}
		finally
		{
			if (cts == artistLoad)
				IsLoadingArtist = false;
		}
	}

	[RelayCommand]
	async Task OpenAlbum(CatalogAlbum album)
	{
		albumLoad?.Cancel();
		var cts = albumLoad = new CancellationTokenSource();
		Album = album;
		AlbumTracks.Clear();
		OnPropertyChanged(nameof(AlbumPlaysText));
		// Il numero sale anche con l'album aperto. -= prima: il view model è uno solo, ci si iscrive una volta.
		library.ListensChanged -= OnListensChanged;
		library.ListensChanged += OnListensChanged;
		LoadFailed = false;
		IsLoadingAlbum = true;
		try
		{
			var page = await Task.Run(() => catalog.AlbumAsync(album, cts.Token), cts.Token);
			if (cts.IsCancellationRequested)
				return;
			Album = page.Album;
			foreach (var track in page.Tracks)
				AlbumTracks.Add(track);
			OnPropertyChanged(nameof(AlbumPlaysText));
		}
		catch (OperationCanceledException)
		{
		}
		catch (Exception ex)
		{
			SentrySdk.CaptureException(ex);
			LoadFailed = true;
		}
		finally
		{
			if (cts == albumLoad)
				IsLoadingAlbum = false;
		}
	}

	// Freccia indietro e Back di Android: prima l'album, poi l'artista.
	[RelayCommand]
	public void Back()
	{
		if (Album is not null)
		{
			albumLoad?.Cancel();
			Album = null;
		}
		else
		{
			artistLoad?.Cancel();
			Artist = null;
		}
	}

	// Un tocco sulla nav chiude tutto, come per il dettaglio podcast.
	public void Close()
	{
		albumLoad?.Cancel();
		artistLoad?.Cancel();
		Album = null;
		Artist = null;
	}

	// Tocco su album o artista sotto il titolo di una riga brano, da qualunque lista: la loro scheda. Un video YouTube
	// ha solo il nome dell'artista: la scheda si cerca su Deezer per nome.
	public void OpenAlbumOf(Track track)
	{
		if (track.AlbumId is { } id)
			_ = OpenAlbum(new CatalogAlbum(id, track.Album ?? "", track.Artist, track.ArtworkUrl, track.ArtworkUrl, null, "album"));
	}

	public async void OpenArtistOf(Track track)
	{
		if (track.ArtistId is { } id)
		{
			_ = OpenArtist(new CatalogArtist(id, track.Artist, "", "", 0));
			return;
		}
		try
		{
			if (await Task.Run(() => catalog.FindArtistAsync(track.Artist, CancellationToken.None)) is { } artist)
				_ = OpenArtist(artist);
			else
				player.Notify($"«{track.Artist}» non è nel catalogo");
		}
		catch (Exception ex)
		{
			SentrySdk.CaptureException(ex);
			player.Notify("Impossibile aprire l'artista. Controlla la connessione.");
		}
	}

	// "+" della pagina album: tutta la tracklist in una playlist (scelta o nuova).
	[RelayCommand]
	Task AddAlbumToPlaylist() => player.AddToPlaylistAsync([.. AlbumTracks]);

	// "+" della pagina artista: i più ascoltati in una playlist nuova col nome dell'artista, senza chiedere. Se quel nome
	// c'è già, la si crea come dalla Libreria, con il primo nome libero ("Queen 2") da confermare o cambiare.
	[RelayCommand]
	async Task AddTopToPlaylist()
	{
		if (Artist is not { } artist || TopTracks.Count == 0)
			return;
		bool Exists(string name) => library.Playlists.Any(p => string.Equals(p.Name, name, StringComparison.CurrentCultureIgnoreCase));
		var name = artist.Name;
		if (Exists(name))
		{
			var free = Enumerable.Range(2, int.MaxValue - 2).Select(n => $"{artist.Name} {n}").First(n => !Exists(n));
			if (await ActionPopup.PromptAsync("Nuova playlist", free, "Crea") is not { } chosen)
				return;
			name = chosen;
		}
		var playlist = library.CreatePlaylist(name);
		foreach (var track in TopTracks)
			library.AddToPlaylist(playlist.Id, track);
		player.Notify($"Creata la playlist «{playlist.Name}»");
	}

	// Play verde e righe: come Spotify, la coda è l'elenco della pagina; con lo shuffle il play parte a caso.
	[RelayCommand]
	Task PlayTop(Track? track) => Play(TopTracks, track, "IN RIPRODUZIONE DALL'ARTISTA", Artist?.Name);

	[RelayCommand]
	Task PlayAlbum(Track? track) => Play(AlbumTracks, track, Album?.IsPlaylist == true ? "IN RIPRODUZIONE DALLA PLAYLIST" : "IN RIPRODUZIONE DALL'ALBUM", Album?.Title);

	Task Play(IList<Track> tracks, Track? track, string header, string? title)
	{
		if (tracks.Count == 0)
			return Task.CompletedTask;
		track ??= tracks[player.IsShuffle ? Random.Shared.Next(tracks.Count) : 0];
		return player.PlayAsync(tracks, track, header, title ?? "");
	}
}
