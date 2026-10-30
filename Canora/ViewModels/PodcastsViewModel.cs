using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Canora.Controls;
using Canora.Models;
using Canora.Services;
using Sentry;

namespace Canora.ViewModels;

// Tab Podcast (ricerca per titolo + seguiti) e dettaglio di un podcast, aperto anche dalla Libreria.
public sealed partial class PodcastsViewModel : ObservableObject
{
	readonly PodcastService service;
	readonly PodcastStore store;
	readonly PlayerViewModel player;
	CancellationTokenSource? typing;
	CancellationTokenSource? opening;
	DateTime refreshed;

	public PodcastsViewModel(PodcastService service, PodcastStore store, PlayerViewModel player)
	{
		this.service = service;
		this.store = store;
		this.player = player;
		store.EpisodeChanged += OnEpisodeChanged;
		store.Followed.CollectionChanged += (_, _) =>
		{
			OnPropertyChanged(nameof(HasFollowed));
			OnPropertyChanged(nameof(NoFollowed));
		};
	}

	public PodcastStore Store => store;
	public PlayerViewModel Player => player;
	public bool HasFollowed => store.Followed.Count > 0;
	public bool NoFollowed => !HasFollowed;

	// --- Ricerca ---

	public ObservableCollection<Podcast> Results { get; } = [];

	[ObservableProperty]
	[NotifyPropertyChangedFor(nameof(IsIdle), nameof(HasQuery))]
	public partial string Query { get; set; } = "";

	public bool IsIdle => string.IsNullOrWhiteSpace(Query);
	public bool HasQuery => !IsIdle;

	[ObservableProperty]
	public partial bool IsSearching { get; private set; }

	[ObservableProperty]
	public partial bool SearchFailed { get; private set; }

	[ObservableProperty]
	public partial bool NoResults { get; private set; }

	partial void OnQueryChanged(string value) => _ = SearchAsync(value, debounce: true);

	[RelayCommand]
	Task Submit() => SearchAsync(Query, debounce: false);

	async Task SearchAsync(string query, bool debounce)
	{
		typing?.Cancel();
		var cts = typing = new CancellationTokenSource();
		Results.Clear();
		SearchFailed = NoResults = false;
		if (string.IsNullOrWhiteSpace(query))
		{
			IsSearching = false;
			return;
		}
		try
		{
			if (debounce)
				await Task.Delay(500, cts.Token);
			IsSearching = true;
			var found = await Task.Run(() => service.SearchAsync(query, cts.Token), cts.Token);
			if (cts.IsCancellationRequested)
				return;
			foreach (var podcast in found)
				Results.Add(podcast);
			NoResults = found.Count == 0;
		}
		catch (OperationCanceledException)
		{
		}
		catch (Exception ex)
		{
			SentrySdk.CaptureException(ex);
			SearchFailed = true;
		}
		finally
		{
			if (cts == typing)
				IsSearching = false;
		}
	}

	// --- Dettaglio ---

	// Podcast aperto (null = nessun dettaglio). La pagina radice lo mostra sopra il tab corrente.
	[ObservableProperty]
	[NotifyPropertyChangedFor(nameof(IsDetailOpen), nameof(IsSelectedFollowed))]
	public partial Podcast? Selected { get; private set; }

	public bool IsDetailOpen => Selected is not null;
	public bool IsSelectedFollowed => Selected is { } p && store.IsFollowed(p.Id);

	public ObservableCollection<Episode> Episodes { get; } = [];

	[ObservableProperty]
	public partial bool IsLoadingEpisodes { get; private set; }

	[ObservableProperty]
	public partial bool FeedFailed { get; private set; }

	// Apre il podcast: subito gli episodi salvati, poi il feed aggiornato. "Nuovi" = usciti dopo l'ultima apertura;
	// il riferimento si sposta a adesso, quindi la volta dopo non lo saranno più.
	[RelayCommand]
	async Task Open(Podcast podcast)
	{
		opening?.Cancel();
		var cts = opening = new CancellationTokenSource();
		var followed = store.Find(podcast.Id);
		Selected = followed ?? podcast;
		var since = followed?.LastOpened;
		FeedFailed = false;
		Show(store.LoadEpisodes(podcast.Id), since);
		if (followed is not null)
			store.Opened(podcast.Id);

		IsLoadingEpisodes = true;
		try
		{
			var feed = await Task.Run(() => service.FetchAsync(podcast, cts.Token), cts.Token);
			var merged = await Task.Run(() => store.MergeFeed(podcast.Id, feed.Episodes), cts.Token);
			if (cts.IsCancellationRequested)
				return;
			Show(merged, since);
			var count = EpisodeCount(podcast, feed, merged);
			Selected = (store.Find(podcast.Id) ?? Selected!) with
			{
				Description = feed.Description,
				ArtworkUrl = string.IsNullOrWhiteSpace(feed.ImageUrl) ? Selected!.ArtworkUrl : feed.ImageUrl,
				EpisodeCount = count,
			};
			if (followed is not null)
				store.Refreshed(podcast.Id, feed.Description, feed.ImageUrl, count, newCount: 0);
		}
		catch (OperationCanceledException)
		{
		}
		catch (Exception ex)
		{
			SentrySdk.CaptureException(ex);
			FeedFailed = Episodes.Count == 0;
		}
		finally
		{
			if (cts == opening)
				IsLoadingEpisodes = false;
		}
	}

	// Un feed RSS li ha tutti; di un canale YouTube si leggono solo gli ultimi, quindi contano quelli salvati.
	static int EpisodeCount(Podcast podcast, PodcastService.Feed feed, List<Episode> merged) =>
		podcast.IsYouTube ? merged.Count : feed.Episodes.Count;

	// Gli episodi senza immagine propria mostrano la copertina del podcast.
	void Show(List<Episode> episodes, DateTime? since)
	{
		var artwork = Selected?.ArtworkUrl;
		Episodes.Clear();
		foreach (var e in episodes)
			Episodes.Add(e with
			{
				ImageUrl = e.ImageUrl ?? artwork,
				IsNew = since is { } s && e.Published > s && !e.DateUnknown && !e.Listened && e.Position == 0,
			});
	}

	[RelayCommand]
	public void Close()
	{
		opening?.Cancel();
		Selected = null;
		Episodes.Clear();
	}

	// Un solo pulsante: "Segui" / "Smetti di seguire". Smettere non perde nulla (gli episodi restano segnati)
	// e si torna a seguire con un tocco, quindi niente conferma.
	[RelayCommand]
	void ToggleFollow()
	{
		if (Selected is not { } podcast)
			return;
		if (store.IsFollowed(podcast.Id))
		{
			store.Unfollow(podcast.Id);
			player.Notify($"Non segui più «{podcast.Title}»");
		}
		else
		{
			Selected = store.Follow(podcast);
			player.Notify($"Ora segui «{podcast.Title}»");
		}
		OnPropertyChanged(nameof(IsSelectedFollowed));
	}

	[RelayCommand]
	Task PlayEpisode(Episode episode) => Selected is { } podcast ? PlayEpisodeAsync(podcast, episode) : Task.CompletedTask;

	// Anche dall'assistente vocale, senza aprire il dettaglio.
	public Task PlayEpisodeAsync(Podcast podcast, Episode episode)
	{
		if (store.IsFollowed(podcast.Id))
			store.Played(podcast.Id);
		return player.PlayAsync([episode.ToTrack(podcast)], episode.ToTrack(podcast), "IN RIPRODUZIONE DAL PODCAST", podcast.Title);
	}

	// Play verde: l'episodio in corso, altrimenti il più recente non ascoltato, altrimenti l'ultimo uscito.
	[RelayCommand]
	Task PlayLatest()
	{
		var next = Episodes.FirstOrDefault(e => e.InProgress) ?? Episodes.FirstOrDefault(e => !e.Listened) ?? Episodes.FirstOrDefault();
		return next is null ? Task.CompletedTask : PlayEpisode(next);
	}

	// Pulsante della riga: "Da ascoltare" ⇄ "Ascoltato".
	[RelayCommand]
	void ToggleListened(Episode episode) => store.SetListened(episode.Key, !episode.Listened);

	[RelayCommand]
	async Task PodcastMenu()
	{
		const string allListened = "Tutto ascoltato";
		if (Selected is not { } podcast || await ActionPopup.ShowAsync(podcast.Title, null, allListened) != allListened)
			return;
		store.SetAllListened(podcast.Id);
		for (var i = 0; i < Episodes.Count; i++)
			Episodes[i] = Episodes[i] with { Listened = true, Position = 0, IsNew = false };
	}

	// Avanzamento salvato dal player o "ascoltato" cambiato: si aggiorna la riga se il podcast è aperto.
	void OnEpisodeChanged(string key, double position, bool listened, double duration) => MainThread.BeginInvokeOnMainThread(() =>
	{
		for (var i = 0; i < Episodes.Count; i++)
			if (Episodes[i].Key == key)
			{
				Episodes[i] = Episodes[i] with
				{
					Position = position,
					Listened = listened,
					IsNew = Episodes[i].IsNew && !listened && position == 0,
					Duration = duration > 0 ? TimeSpan.FromSeconds(duration) : Episodes[i].Duration,
				};
				return;
			}
	});

	// Controlla i feed seguiti per i nuovi episodi (conteggio in Libreria). Al più ogni 15 minuti.
	public async Task RefreshFollowedAsync()
	{
		if (DateTime.Now - refreshed < TimeSpan.FromMinutes(15))
			return;
		refreshed = DateTime.Now;
		foreach (var podcast in store.Followed.ToList())
		{
			try
			{
				var feed = await Task.Run(() => service.FetchAsync(podcast, CancellationToken.None, latest: true));
				var merged = await Task.Run(() => store.MergeFeed(podcast.Id, feed.Episodes));
				var since = (store.Find(podcast.Id) ?? podcast).LastOpened ?? podcast.FollowedAt;
				// ponytail: un canale dà la data vera ai suoi ultimi 15 video e 15 dirette (RSS); una diretta programmata
				// prima dell'ultima apertura e trasmessa dopo non conta come nuova.
				store.Refreshed(podcast.Id, feed.Description, feed.ImageUrl, EpisodeCount(podcast, feed, merged),
					feed.Episodes.Count(e => e.Published > since && !e.DateUnknown));
			}
			catch (Exception ex)
			{
				SentrySdk.Logger.LogWarning("Feed non aggiornato ({0}): {1}", podcast.Title, ex.Message);
			}
		}
	}
}
