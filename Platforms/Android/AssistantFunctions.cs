using Android.App;
using Android.Content;
using Canora.Models;
using Canora.ViewModels;
using Shiny.AppFunctions;
using YoutubeExplode;
using YoutubeExplode.Common;
using YoutubeExplode.Search;

namespace Canora.Services;

// Comandi per l'assistente del telefono (Gemini & co. via Android 16+ AppFunctions, con Shiny.AppFunctions):
//   "Riproduci <brano> da Can'ora", "Riproduci la mia playlist <nome> da Can'ora",
//   "Riproduci l'ultimo episodio di <podcast>".
// Girano in background, anche ad app chiusa: trovano cosa suonare, lo passano al player e portano l'app in primo
// piano. Quello che passa a context.Say e i messaggi d'errore l'assistente li dice all'utente.

public sealed record Playing(string Title, string Artist);

[AppFunction("play_song", Title = "Riproduci brano",
	Description = "Plays a song in Can'ora. Looks for it first among the songs in the user's playlists, otherwise plays the first YouTube result. Opens the app.")]
public sealed record PlaySong(
	[property: AppParameter(Title = "Brano", Description = "The song title, optionally with the artist, as the user said it")] string Song) : IAppFunction<Playing>;

[AppFunction("play_playlist", Title = "Riproduci playlist",
	Description = "Plays one of the user's own Can'ora playlists, found by name. Opens the app.")]
public sealed record PlayPlaylist(
	[property: AppParameter(Title = "Playlist", Description = "The playlist name as the user said it")] string Name) : IAppFunction<Playing>;

[AppFunction("play_latest_episode", Title = "Riproduci ultimo episodio",
	Description = "Plays the most recent unplayed episode of a podcast the user follows in Can'ora. Opens the app.")]
public sealed record PlayLatestEpisode(
	[property: AppParameter(Title = "Podcast", Description = "The podcast title as the user said it")] string Title) : IAppFunction<Playing>;

sealed class PlaySongHandler(LibraryStore library, YoutubeClient youtube, PlayerViewModel player) : IAppFunctionHandler<PlaySong, Playing>
{
	public async Task<Playing> Handle(PlaySong request, AppFunctionContext context, CancellationToken cancellationToken)
	{
		var query = Assistant.Clean(request.Song);
		var track = await MainThread.InvokeOnMainThreadAsync(() => library.SearchTracks(query).FirstOrDefault())
			?? await FirstOnYouTubeAsync(query, cancellationToken)
			?? throw Assistant.NotFound();
		return await Assistant.PlayAsync(context, player,
			() => player.PlayAsync([track], track, "IN RIPRODUZIONE DALLA RICERCA", $"«{query}» nei brani"),
			new(track.Title, track.Artist), $"Riproduco «{track.Title}» di {track.Artist}");
	}

	// Come il tab Cerca: solo video con durata (niente dirette), il primo che arriva.
	async Task<Track?> FirstOnYouTubeAsync(string query, CancellationToken cancellationToken)
	{
		await foreach (var batch in youtube.Search.GetResultBatchesAsync(query, SearchFilter.Video, cancellationToken))
			return batch.Items.OfType<VideoSearchResult>().FirstOrDefault(v => v.Duration is not null) is { } video ? Track.From(video) : null;
		return null;
	}
}

sealed class PlayPlaylistHandler(LibraryStore library, LibraryViewModel libraryView, PlayerViewModel player) : IAppFunctionHandler<PlayPlaylist, Playing>
{
	public async Task<Playing> Handle(PlayPlaylist request, AppFunctionContext context, CancellationToken cancellationToken)
	{
		var playlist = await MainThread.InvokeOnMainThreadAsync(() => Assistant.Find(library.Playlists, p => p.Name, request.Name))
			?? throw Assistant.NotFound();
		if (playlist.Tracks.Count == 0)
			throw new AppFunctionException(AppFunctionErrorCode.NotFound, $"La playlist «{playlist.Name}» è ancora vuota");
		return await Assistant.PlayAsync(context, player, () => libraryView.PlayPlaylistAsync(playlist),
			new(playlist.Name, playlist.TracksText), $"Riproduco la playlist «{playlist.Name}»");
	}
}

sealed class PlayLatestEpisodeHandler(PodcastStore store, PodcastService service, PodcastsViewModel podcasts, PlayerViewModel player)
	: IAppFunctionHandler<PlayLatestEpisode, Playing>
{
	public async Task<Playing> Handle(PlayLatestEpisode request, AppFunctionContext context, CancellationToken cancellationToken)
	{
		var podcast = await MainThread.InvokeOnMainThreadAsync(() => Assistant.Find(store.Followed, p => p.Title, request.Title))
			?? throw Assistant.NotFound();

		// Prima il feed aggiornato (l'episodio di stamattina potrebbe non essere ancora in cache); senza rete, la cache.
		List<Episode> episodes;
		try
		{
			var feed = await service.FetchAsync(podcast, cancellationToken);
			episodes = store.MergeFeed(podcast.Id, feed.Episodes);
		}
		catch (Exception) when (!cancellationToken.IsCancellationRequested)
		{
			episodes = store.LoadEpisodes(podcast.Id);
		}

		// Dal più recente: il primo non ascoltato (se è a metà, riprende da dove era rimasto).
		var episode = episodes.FirstOrDefault(e => !e.Listened)
			?? throw new AppFunctionException(AppFunctionErrorCode.NotFound, $"Hai già ascoltato tutti gli episodi di «{podcast.Title}»");
		return await Assistant.PlayAsync(context, player, () => podcasts.PlayEpisodeAsync(podcast, episode),
			new(episode.Title, podcast.Title), $"Riproduco «{episode.Title}» di {podcast.Title}");
	}
}

static class Assistant
{
	// Parole di collegamento che l'assistente lascia nella richiesta ("Houdini di Dua Lipa"): nei titoli non ci sono.
	static readonly HashSet<string> Fillers = new(["di", "del", "della", "dei", "degli", "by"], StringComparer.OrdinalIgnoreCase);

	public static string Clean(string text) =>
		string.Join(' ', text.Split(' ', StringSplitOptions.RemoveEmptyEntries).Where(w => !Fillers.Contains(w)));

	public static AppFunctionException NotFound() => new(AppFunctionErrorCode.NotFound, "Mi dispiace, non l'ho trovata");

	// Il nome detto può essere uguale, contenuto ("allenamento" → "Allenamento estivo") o contenere quello vero
	// ("la mia playlist allenamento" → "Allenamento"). Nell'ordine della lista: ascoltati di recente prima.
	public static T? Find<T>(IEnumerable<T> items, Func<T, string> name, string said) where T : class
	{
		var list = items.ToList();
		return list.FirstOrDefault(i => string.Equals(name(i), said.Trim(), StringComparison.OrdinalIgnoreCase))
			?? list.FirstOrDefault(i => LibraryStore.Matches(name(i), said))
			?? list.FirstOrDefault(i => LibraryStore.Matches(said, name(i)));
	}

	// Il player vive nella pagina radice: ad app chiusa la richiesta resta in attesa finché la pagina non nasce
	// (vedi PlayerViewModel.PlayAsync), quindi si apre l'app e si aspetta qualche secondo che parta davvero.
	// Android 17 però non lascia aprire un'app dal background ("Background activity launch blocked"): allora una
	// notifica, perché il tocco su una notifica è un avvio permesso, e la richiesta parte all'apertura.
	public static async Task<Playing> PlayAsync(AppFunctionContext context, PlayerViewModel player, Func<Task> play, Playing result, string said)
	{
		await MainThread.InvokeOnMainThreadAsync(() => { _ = play(); });
		OpenApp();
		for (var i = 0; i < 50 && player.IsPending; i++)
			await Task.Delay(100);
		if (!player.IsPending)
			context.Say(said);
		else if (Notify(result.Title))
			context.Say($"Tocca la notifica di Can'ora per ascoltare «{result.Title}»");
		else
			throw new AppFunctionException(AppFunctionErrorCode.Denied, $"Apri Can'ora per ascoltare «{result.Title}»");
		return result;
	}

	// SingleTask: riporta in primo piano l'istanza esistente, o la crea.
	static void OpenApp()
	{
		var app = Android.App.Application.Context;
		app.StartActivity(LaunchIntent(app));
	}

	static Intent LaunchIntent(Context app)
	{
		var intent = app.PackageManager!.GetLaunchIntentForPackage(app.PackageName!)!;
		intent.AddFlags(ActivityFlags.NewTask | ActivityFlags.ReorderToFront);
		return intent;
	}

	const string Channel = "assistant";
	const int NotificationId = 4242;

	// Heads-up (importanza alta), così si vede sopra l'assistente. Scade insieme alla richiesta in attesa.
	// false se le notifiche sono negate: il permesso si chiede all'apertura dell'app (MainPage.OnAppearing).
	static bool Notify(string title)
	{
		var app = Android.App.Application.Context;
		var manager = (NotificationManager)app.GetSystemService(Context.NotificationService)!;
		if (!manager.AreNotificationsEnabled())
			return false;
		manager.CreateNotificationChannel(new NotificationChannel(Channel, "Comandi vocali", NotificationImportance.High));
		var open = PendingIntent.GetActivity(app, 0, LaunchIntent(app), PendingIntentFlags.Immutable | PendingIntentFlags.UpdateCurrent);
		var notification = new Notification.Builder(app, Channel)
			.SetSmallIcon(Resource.Drawable.ic_play_circle)
			.SetContentTitle("Can'ora")
			.SetContentText($"Tocca per ascoltare «{title}»")
			.SetContentIntent(open)
			.SetAutoCancel(true)
			.SetTimeoutAfter((long)PlayerViewModel.PendingTimeout.TotalMilliseconds)
			.Build();
		manager.Notify(NotificationId, notification);
		return true;
	}

	// App aperta (dalla notifica o in un altro modo): la notifica non serve più. Da MainActivity.OnResume.
	public static void Dismiss() =>
		((NotificationManager)Android.App.Application.Context.GetSystemService(Context.NotificationService)!).Cancel(NotificationId);
}
