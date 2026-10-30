using System.Collections.Concurrent;
using CommunityToolkit.Maui.Core;
using CommunityToolkit.Maui.Views;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Canora.Controls;
using Canora.Models;
using Canora.Services;
using Canora.Theming;
using Canora.Views;
using Sentry;
using YoutubeExplode;
using YoutubeExplode.Videos.Streams;

namespace Canora.ViewModels;

public enum RepeatMode { Off, All, One }

// Stato del player condiviso da mini-player e Now Playing. Il MediaElement vive nella pagina radice
// (deve restare nel visual tree per tutta la vita dell'app) e viene agganciato con Attach.
public sealed partial class PlayerViewModel(YoutubeClient youtube, LibraryStore library, PodcastStore podcasts, CatalogService catalog, LyricsService lyricsService) : ObservableObject
{
	MediaElement? player;
	List<Track> queue = [];
	int index = -1;
	int failures;
	Stream? stream;
	CancellationTokenSource? loading;
	// Episodi: posizione da cui riprendere appena parte, e ultimo salvataggio dell'avanzamento.
	double resumeAt;
	DateTime progressSaved;
	// Segnato "Ascoltato" mentre suona: non se ne salva più l'avanzamento, altrimenti tornerebbe "in corso".
	bool markedListened;

	// Messaggi brevi per il toast della pagina radice.
	public event Action<string>? Message;

	// Per gli altri view model: un toast senza passare dalla pagina.
	public void Notify(string message) => Message?.Invoke(message);

	[ObservableProperty]
	[NotifyPropertyChangedFor(nameof(HasTrack), nameof(CanAddToPlaylist), nameof(IsEpisode))]
	public partial Track? Current { get; private set; }

	public bool HasTrack => Current is not null;

	partial void OnCurrentChanged(Track? value) => NowPlaying.Set(value?.Id);

	// Episodio in riproduzione: al posto di shuffle e ripeti, i salti di 15 secondi.
	public bool IsEpisode => Current is { IsEpisode: true };

	// Le playlist sono di brani: il "+" non si mostra per gli episodi di podcast.
	public bool CanAddToPlaylist => Current is { IsEpisode: false };

	// Intestazione del player come nel Figma: "PLAYING FROM SEARCH" / "“stay” in Songs".
	[ObservableProperty]
	public partial string QueueHeader { get; private set; } = "";

	[ObservableProperty]
	public partial string QueueTitle { get; private set; } = "";

	[ObservableProperty]
	public partial bool IsPlaying { get; private set; }

	[ObservableProperty]
	public partial bool IsLoading { get; private set; }

	[ObservableProperty]
	[NotifyPropertyChangedFor(nameof(PlayModeIcon), nameof(IsPlayModeOn))]
	public partial bool IsShuffle { get; private set; }

	[ObservableProperty]
	[NotifyPropertyChangedFor(nameof(IsRepeatOn), nameof(IsRepeatOne), nameof(IsPlayModeOn))]
	public partial RepeatMode Repeat { get; private set; }

	public bool IsRepeatOn => Repeat != RepeatMode.Off;
	public bool IsRepeatOne => Repeat == RepeatMode.One;

	// Pulsante unico del player per ripeti e casuale: l'icona dice la modalità, il colore se è attiva.
	public string PlayModeIcon => IsShuffle ? "ic_shuffle.png" : "ic_repeat.png";
	public bool IsPlayModeOn => IsShuffle || IsRepeatOn;

	// Secondi. TwoWay con lo Slider: mentre si trascina (IsSeeking) il player non lo sovrascrive.
	[ObservableProperty]
	[NotifyPropertyChangedFor(nameof(Progress), nameof(PositionText), nameof(ProgressBounds), nameof(ThumbBounds))]
	public partial double Position { get; set; }

	[ObservableProperty]
	[NotifyPropertyChangedFor(nameof(Progress), nameof(DurationText), nameof(SliderMaximum), nameof(ProgressBounds), nameof(ThumbBounds))]
	public partial double Duration { get; private set; }

	public double SliderMaximum => Math.Max(Duration, 1);
	public double Progress => Duration > 0 ? Math.Clamp(Position / Duration, 0, 1) : 0;
	public string PositionText => Format(Position);
	public string DurationText => Format(Duration);

	// Seek bar disegnata con un AbsoluteLayout proporzionale (traccia 4px + pallino 10px del Figma):
	// barra = larghezza proporzionale, pallino = X proporzionale (resta dentro i bordi da solo).
	public Rect ProgressBounds => new(0, 0.5, Progress, AbsoluteLayout.AutoSize);
	public Rect ThumbBounds => new(Progress, 0.5, AbsoluteLayout.AutoSize, AbsoluteLayout.AutoSize);

	// Colore dominante della copertina: sfondo del Now Playing (gradiente come nel Figma) e del mini-player.
	[ObservableProperty]
	[NotifyPropertyChangedFor(nameof(AccentDeep), nameof(AccentBackground), nameof(LyricsColor))]
	public partial Color Accent { get; private set; } = Themes.Color("PlayerAccent");

	public Color AccentDeep => Accent.WithLuminosity(Accent.GetLuminosity() * 0.6f);

	// Sfondo del karaoke: il colore della copertina, scurito quanto basta perché il testo bianco si legga sempre.
	public Color LyricsColor => Accent.WithLuminosity(Math.Min(Accent.GetLuminosity(), 0.3f));

	// Karaoke: testo del brano in corso (LRCLIB) e riga cantata in base a Position. LyricsMessage è quello che il
	// pannello mostra senza testo (ricerca in corso o testo non trovato).
	[ObservableProperty]
	[NotifyPropertyChangedFor(nameof(HasLyrics))]
	public partial Lyrics? Lyrics { get; private set; }

	public bool HasLyrics => Lyrics is not null;

	[ObservableProperty]
	public partial int LyricIndex { get; private set; } = -1;

	[ObservableProperty]
	public partial string LyricsMessage { get; private set; } = "";

	[ObservableProperty]
	public partial bool IsLyricsOpen { get; private set; }

	// Lucchetto del karaoke: tiene acceso lo schermo mentre si canta; chiuso il testo, torna il blocco normale.
	[ObservableProperty]
	public partial bool IsScreenAwake { get; private set; }

	partial void OnIsScreenAwakeChanged(bool value) => DeviceDisplay.KeepScreenOn = value;

	partial void OnIsLyricsOpenChanged(bool value)
	{
		if (!value)
			IsScreenAwake = false;
	}

	// Volume dell'app, 0…1: lo slider del player su Windows (su Android c'è quello di sistema). Resta fra un avvio e
	// l'altro.
	[ObservableProperty]
	public partial double Volume { get; set; } = Preferences.Default.Get(nameof(Volume), 1.0);

	partial void OnVolumeChanged(double value)
	{
		player?.Volume = value;
		Preferences.Default.Set(nameof(Volume), value);
	}

	// Music Player Screen: #5e5a4f 7% → #35322d 66% → #1d1c1a 112%, con il primo colore preso dalla copertina
	// e il fondo (PlayerEnd) dal tema.
	public Brush AccentBackground => new LinearGradientBrush(
	[
		new GradientStop(Accent, 0.07f),
		new GradientStop(AccentDeep, 0.66f),
		new GradientStop(Themes.Color("PlayerEnd"), 1f),
	], new Point(0, 0), new Point(0, 1));

	bool isSeeking;

	// Metodi d'istanza e non lambda: MediaElement tiene gli handler in un WeakEventManager, quindi una lambda
	// che cattura variabili locali vive in una closure che nessuno referenzia e sparisce al primo GC
	// (gli eventi smettono di arrivare in silenzio). Il target qui è il view model, singleton, sempre vivo.
	public void Attach(MediaElement element)
	{
		player = element;
		element.Volume = Volume;
		element.StateChanged += OnStateChanged;
		element.PositionChanged += OnPositionChanged;
		element.MediaEnded += OnMediaEnded;
		element.MediaFailed += OnMediaFailed;
		element.HandlerChanged += OnPlayerReady;
		podcasts.EpisodeChanged += OnEpisodeChanged;
		// Il fondo del gradiente viene dal tema (qui e non nel costruttore: la classe non ne ha uno con corpo).
		Themes.Changed += () => OnPropertyChanged(nameof(AccentBackground));
	}

	void OnPlayerReady(object? sender, EventArgs e)
	{
		if (player?.Handler is null || pending is not { } request)
			return;
		pending = null;
		if (DateTime.Now - request.At < PendingTimeout)
			_ = request.Play();
	}

	void OnEpisodeChanged(string key, double position, bool listened, double duration)
	{
		if (key == Current?.Id)
			markedListened = listened;
	}

	// Stato, fine ed errore: su Windows MediaElement li solleva dal thread di MediaPlayer, e da lì LoadAsync (brano
	// successivo) imposterebbe Source sul MediaPlayerElement fuori dal thread UI: RPC_E_WRONG_THREAD (0x8001010E), il
	// catch lo prende per un brano rotto e Fail salta avanti finché si arrende. Sul thread UI (Android) gira subito.
	void OnStateChanged(object? sender, MediaStateChangedEventArgs e) => MainThread.BeginInvokeOnMainThread(() =>
	{
		IsPlaying = e.NewState == MediaElementState.Playing;
		IsLoading = e.NewState is MediaElementState.Opening or MediaElementState.Buffering;
		if (IsPlaying)
		{
			failures = 0;
			// Partito il brano: ora, senza togliergli banda, si preparano i prossimi.
			if (prefetchedFor != index && loading is { } cts)
			{
				prefetchedFor = index;
				_ = PrefetchAsync(cts.Token);
			}
			// Episodio già iniziato: si riprende da dove lo si era lasciato.
			if (resumeAt > 0 && player is not null)
			{
				_ = player.SeekTo(TimeSpan.FromSeconds(resumeAt));
				resumeAt = 0;
			}
		}
		else if (e.NewState == MediaElementState.Paused)
			SaveEpisodeProgress();
	});

	void OnPositionChanged(object? sender, MediaPositionChangedEventArgs e)
	{
		if (player?.Duration > TimeSpan.Zero)
			Duration = player.Duration.TotalSeconds;
		if (!isSeeking)
			Position = e.Position.TotalSeconds;
		UpdateLyric();
		CountListen();
		if (Current is { IsEpisode: true } && DateTime.Now - progressSaved > TimeSpan.FromSeconds(10))
			SaveEpisodeProgress();
	}

	void OnMediaEnded(object? sender, EventArgs e) => MainThread.BeginInvokeOnMainThread(() =>
	{
		if (Current is { IsEpisode: true } episode)
			podcasts.SetListened(episode.Id, true);
		_ = AdvanceAsync(manual: false);
	});

	// Un brano di musica conta come ascoltato dopo 30 secondi (metà, se dura meno di un minuto), come gli scrobble di
	// Last.fm: va nel tracker del mese e, se è il primo ascoltato della playlist in coda, vale un ascolto della playlist.
	// ponytail: guarda la posizione, quindi anche un salto in avanti oltre i 30 s conta come ascolto.
	string? queueSource;
	bool sourceCounted;
	bool listenCounted;

	void CountListen()
	{
		if (listenCounted || IsLoading || Current is not { IsEpisode: false } track || Duration <= 0 || Position < Math.Min(30, Duration / 2))
			return;
		listenCounted = true;
		library.AddListen(track);
		if (queueSource is { } playlist && !sourceCounted)
		{
			sourceCounted = true;
			library.MarkPlayed(playlist);
		}
	}

	// Avanzamento dell'episodio in corso; oltre il 97% (sigle, saluti finali) conta come ascoltato.
	void SaveEpisodeProgress()
	{
		if (Current is not { IsEpisode: true } episode || Position <= 0 || resumeAt > 0 || markedListened)
			return;
		progressSaved = DateTime.Now;
		var listened = Duration > 0 && Position / Duration > 0.97;
		podcasts.SaveProgress(episode.Id, listened ? 0 : Position, listened, Duration);
	}

	void OnMediaFailed(object? sender, MediaFailedEventArgs e) => MainThread.BeginInvokeOnMainThread(() =>
	{
		SentrySdk.CaptureMessage($"MediaFailed: {e.ErrorMessage}", SentryLevel.Error);
		IsLoading = false;
		Fail();
	});

	// La coda è una copia della lista da cui si è toccato il brano (risultati, playlist, recenti).
	// Riproduzione chiesta prima che esista il player (app avviata dall'assistente vocale, vedi AssistantFunctions):
	// parte appena il MediaElement ha il suo handler. Scade, per non far partire musica a sorpresa molto dopo.
	(Func<Task> Play, DateTime At)? pending;
	public static readonly TimeSpan PendingTimeout = TimeSpan.FromMinutes(2);

	// L'assistente vocale aspetta questo per dire se la riproduzione è partita.
	public bool IsPending => pending is not null;

	// source: la playlist della Libreria da cui parte la coda, per contarne gli ascolti (vedi CountListen). Ripartire
	// dalla stessa playlist non azzera il conteggio della sessione; un'altra coda sì.
	public Task PlayAsync(IEnumerable<Track> tracks, Track track, string queueHeader, string queueTitle, string? source = null)
	{
		if (player?.Handler is null)
		{
			pending = (() => PlayAsync(tracks, track, queueHeader, queueTitle, source), DateTime.Now);
			return Task.CompletedTask;
		}
		if (source is null || source != queueSource)
			(queueSource, sourceCounted) = (source, false);
		queue = [.. tracks];
		QueueHeader = queueHeader;
		QueueTitle = queueTitle;
		var i = queue.FindIndex(t => t.Id == track.Id);
		if (i < 0)
		{
			queue.Insert(0, track);
			i = 0;
		}
		failures = 0;
		shuffleNext = null;
		prefetchedFor = -1;
		return LoadAsync(i, picked: true);
	}

	// picked: brano scelto dall'utente (PlayAsync), non arrivato dalla coda: solo questi vanno negli "Ascoltati di recente".
	async Task LoadAsync(int i, bool picked = false)
	{
		if (player is null || i < 0 || i >= queue.Count)
			return;

		SaveEpisodeProgress(); // l'episodio che si sta lasciando riparte da qui la prossima volta
		markedListened = false;
		listenCounted = false;
		loading?.Cancel();
		var cts = loading = new CancellationTokenSource();
		index = i;
		var track = Current = queue[i];
		Position = 0;
		Duration = track.Duration?.TotalSeconds ?? 0;
		IsLoading = true;
		resumeAt = track.IsEpisode ? podcasts.GetPosition(track.Id) : 0;
		_ = UpdateAccentAsync(track, cts.Token);
		Lyrics = null;
		LyricIndex = -1;
		LyricsMessage = track.IsEpisode ? "Nessun testo per gli episodi" : "Cerco il testo…";

		try
		{
			// Video di un canale YouTube seguito come podcast: episodio per posizione e "ascoltato", audio da YouTube.
			var episodeVideo = track.AudioUrl is { } episodeAudio ? Episode.VideoIdOf(episodeAudio) : null;
			if (track.AudioUrl is { } audioUrl && episodeVideo is null)
			{
				// Episodio: audio diretto dal feed, niente YoutubeExplode.
				var uri = await Task.Run(() => PodcastService.ResolveAudioAsync(audioUrl, cts.Token), cts.Token);
				if (cts.IsCancellationRequested)
					return;
				player.MetadataTitle = track.Title;
				player.MetadataArtist = track.Artist;
				player.MetadataArtworkUrl = track.ThumbUrl;
				player.ShouldLoopPlayback = false; // negli episodi il pulsante ripeti non c'è
				player.Source = MediaSource.FromUri(uri);
				(stream, var old) = (null, stream);
				if (old is not null)
					_ = Task.Run(old.Dispose);
				return;
			}

			// Brano del catalogo (ricerca, artista, album): prima il video YouTube con la stessa durata. In coda e nei
			// recenti va quello abbinato, così riparte e si aggiunge alle playlist come un brano YouTube qualsiasi.
			if (track.IsCatalog)
			{
				var fromCatalog = track.Id;
				track = await Task.Run(() => catalog.MatchAsync(track, cts.Token), cts.Token);
				if (cts.IsCancellationRequested)
					return;
				queue[i] = track;
				Current = track;
				NowPlaying.Set(fromCatalog, track.Id); // le righe del catalogo restano evidenziate anche dopo l'abbinamento
			}
			if (episodeVideo is null)
			{
				if (picked)
					library.AddRecent(track); // "Ascoltati di recente" è per i brani
				_ = LoadLyricsAsync(track, cts.Token); // titolo, artista e durata del catalogo quando ci sono: li cerca meglio
			}
			var videoId = episodeVideo ?? track.Id;

			// Su un thread del pool: altrimenti le continuation di YoutubeExplode tornano sul thread UI e Android
			// rifiuta la chiusura delle risposte HTTP lì (NetworkOnMainThreadException).
#if WINDOWS || MACCATALYST
			// Media Foundation suona Opus/WebM solo con l'estensione "Web Media Extensions" e AVFoundation (Mac) per
			// niente: AAC/MP4 c'è sempre. E l'URL e non lo stream: dietro AsRandomAccessStream di MediaElement uno Stream
			// .NET resta in Opening, mentre l'URL il player di sistema lo scarica da sé con richieste Range, seek compreso.
			var url = await Task.Run(async () =>
			{
				var manifest = await ManifestAsync(videoId).WaitAsync(cts.Token);
				var audio = manifest.GetAudioOnlyStreams().Where(s => s.Container == Container.Mp4).TryGetWithHighestBitrate()
					?? manifest.GetAudioOnlyStreams().GetWithHighestBitrate();
				return audio.Url;
			}, cts.Token);
			Stream? next = null;
			if (cts.IsCancellationRequested)
				return;
#else
			var next = await Task.Run(async () =>
			{
				var manifest = await ManifestAsync(videoId).WaitAsync(cts.Token);
				var audio = manifest.GetAudioOnlyStreams().GetWithHighestBitrate();
				// GetAsync e non audio.Url: lo stream di YoutubeExplode scarica a segmenti con Range (anti-throttling)
				// ed è seekable, quindi ExoPlayer può fare seek senza riscaricare tutto.
				return new SafeStream(await youtube.Videos.Streams.GetAsync(audio, cts.Token));
			}, cts.Token);
			if (cts.IsCancellationRequested)
			{
				_ = Task.Run(next.Dispose);
				return;
			}
#endif
			// I metadata vanno impostati prima di Source: li legge quando prepara la notifica.
			player.MetadataTitle = track.Title;
			player.MetadataArtist = track.Artist;
			player.MetadataArtworkUrl = track.ThumbUrl;
			player.ShouldLoopPlayback = episodeVideo is null && Repeat == RepeatMode.One; // negli episodi il ripeti non c'è
#if WINDOWS || MACCATALYST
			player.Source = MediaSource.FromUri(url);
#else
			player.Source = MediaSource.FromStream(next);
#endif
			(stream, var previous) = (next, stream);
			if (previous is not null)
				_ = Task.Run(previous.Dispose); // chiude connessioni HTTP: mai sul thread UI
		}
		catch (OperationCanceledException) when (cts.IsCancellationRequested)
		{
		}
		catch (Exception ex)
		{
			SentrySdk.CaptureException(ex);
			IsLoading = false;
			Fail();
		}
	}

	// Manifest degli stream YouTube per video: è la parte lenta del cambio brano (2-5 s, contro ~0,2 s per aprire lo
	// stream e leggerne l'inizio), quindi si chiede in anticipo per i prossimi della coda e si riusa. La richiesta non
	// si annulla con chi la aspetta: il risultato serve comunque. Gli URL degli stream scadono dopo ~6 ore.
	// ponytail: dopo un'ora si richiede; se mai un URL scaduto prima, si scarterebbe al primo errore di GetAsync.
	readonly ConcurrentDictionary<string, (Task<StreamManifest> Manifest, DateTime At)> manifests = new();

	Task<StreamManifest> ManifestAsync(string videoId)
	{
		if (manifests.TryGetValue(videoId, out var cached) && DateTime.Now - cached.At < TimeSpan.FromHours(1) && !cached.Manifest.IsFaulted && !cached.Manifest.IsCanceled)
			return cached.Manifest;
		foreach (var old in manifests.Where(m => DateTime.Now - m.Value.At >= TimeSpan.FromHours(1)))
			manifests.TryRemove(old.Key, out _);
		var fetch = Task.Run(() => youtube.Videos.Streams.GetManifestAsync(videoId).AsTask());
		manifests[videoId] = (fetch, DateTime.Now);
		return fetch;
	}

	// Il brano su cui si sono già preparati i successivi, e il prossimo del casuale, scelto prima per poterlo preparare.
	int prefetchedFor = -1;
	int? shuffleNext;

	// I prossimi due della coda (uno col casuale): video abbinato (brani del catalogo, ~1 s la prima volta) e manifest,
	// uno alla volta. Lo stream audio no: si apre al cambio. Un altro cambio di brano annulla quello che resta.
	async Task PrefetchAsync(CancellationToken token)
	{
		foreach (var track in Upcoming().Select(k => queue[k]).ToList())
		{
			try
			{
				if (track.IsEpisode)
					continue;
				var video = await Task.Run(() => catalog.MatchAsync(track, token), token);
				await ManifestAsync(video.Id).WaitAsync(token);
			}
			catch (OperationCanceledException)
			{
				return;
			}
			catch (Exception ex)
			{
				SentrySdk.Logger.LogWarning("Precaricamento non riuscito: {0}", ex.Message); // al cambio si riprova da capo
			}
		}
	}

	IEnumerable<int> Upcoming()
	{
		if (IsShuffle && queue.Count > 1)
		{
			yield return shuffleNext ??= RandomOther();
			yield break;
		}
		for (var step = 1; step <= 2 && step < queue.Count; step++)
		{
			var k = index + step;
			if (k >= queue.Count)
			{
				if (Repeat != RepeatMode.All)
					yield break;
				k -= queue.Count;
			}
			yield return k;
		}
	}

	int RandomOther()
	{
		int next;
		do
			next = Random.Shared.Next(queue.Count);
		while (next == index);
		return next;
	}

	// Brano non riproducibile (rimosso, con restrizioni d'età, rete assente…): avvisa e passa al successivo,
	// ma non all'infinito se a fallire è la rete.
	void Fail()
	{
		Message?.Invoke(Current is null ? "Impossibile riprodurre il brano" : $"Impossibile riprodurre «{Current.Title}»");
		if (++failures < 3)
			_ = AdvanceAsync(manual: false);
	}

	async Task UpdateAccentAsync(Track track, CancellationToken token)
	{
		try
		{
			Accent = await ArtworkColor.FromUrlAsync(track.ThumbUrl, token);
		}
		catch (Exception ex) when (ex is not OutOfMemoryException)
		{
			// Copertina non scaricabile: resta il colore del brano precedente, non vale un errore.
		}
	}

	async Task LoadLyricsAsync(Track track, CancellationToken token)
	{
		try
		{
			var lyrics = await Task.Run(() => lyricsService.GetAsync(track, token), token);
			if (token.IsCancellationRequested)
				return;
			Lyrics = lyrics;
			LyricsMessage = lyrics is null ? "Testo non disponibile per questo brano" : "";
			UpdateLyric();
		}
		catch (Exception ex) when (ex is not OutOfMemoryException)
		{
			// LRCLIB irraggiungibile o brano cambiato nel frattempo: il karaoke non vale un errore.
			if (!token.IsCancellationRequested)
				LyricsMessage = "Testo non disponibile per questo brano";
		}
	}

	// Riga in corso: l'ultima già iniziata, accesa con un attimo d'anticipo (come Spotify) per leggerla mentre parte.
	// ponytail: tempi di LRCLIB sull'audio ufficiale; un video con intro più lunga resta sfasato, servirebbe un offset.
	void UpdateLyric()
	{
		if (Lyrics is not { IsSynced: true, Lines: var lines })
			return;
		var now = TimeSpan.FromSeconds(Position + 0.3);
		var i = lines.Count - 1;
		while (i >= 0 && lines[i].At > now)
			i--;
		if (i == LyricIndex)
			return;
		LyricIndex = i;
		for (var j = 0; j < lines.Count; j++)
		{
			lines[j].IsCurrent = j == i;
			lines[j].IsSung = j < i;
		}
	}

	[RelayCommand]
	void ToggleLyrics() => IsLyricsOpen = !IsLyricsOpen;

	[RelayCommand]
	void ToggleScreenAwake() => IsScreenAwake = !IsScreenAwake;

	// Tocco su una riga del karaoke: il brano riparte da lì.
	[RelayCommand]
	Task SeekToLyric(LyricLine line)
	{
		if (player is null || line.At is not { } at)
			return Task.CompletedTask;
		Position = at.TotalSeconds;
		UpdateLyric();
		return player.SeekTo(at);
	}

	Task AdvanceAsync(bool manual)
	{
		if (queue.Count == 0)
			return Task.CompletedTask;
		if (IsShuffle && queue.Count > 1)
		{
			// Quello già scelto (e preparato) da Upcoming, se c'è.
			var next = shuffleNext is { } chosen && chosen != index && chosen < queue.Count ? chosen : RandomOther();
			shuffleNext = null;
			return LoadAsync(next);
		}
		if (index + 1 < queue.Count)
			return LoadAsync(index + 1);
		if (Repeat == RepeatMode.All && !IsEpisode)
			return LoadAsync(0);
		if (!manual)
			Position = 0; // fine coda: si ferma qui, come Spotify senza ripetizione
		return Task.CompletedTask;
	}

	[RelayCommand]
	void TogglePlay()
	{
		if (player is null || Current is null)
			return;
		if (player.CurrentState == MediaElementState.Playing)
			player.Pause();
		else if (player.Source is null || player.CurrentState is MediaElementState.Failed or MediaElementState.None)
			_ = LoadAsync(index);
		else
			player.Play();
	}

	[RelayCommand]
	Task Next() => AdvanceAsync(manual: true);

	// Come Spotify: dopo 3 secondi "indietro" riavvolge il brano, prima torna al precedente.
	[RelayCommand]
	async Task Previous()
	{
		if (player is null)
			return;
		if (Position > 3 || index <= 0)
			await player.SeekTo(TimeSpan.Zero);
		else
			await LoadAsync(index - 1);
	}

	[RelayCommand]
	Task SkipBack() => SkipAsync(-15);

	[RelayCommand]
	Task SkipForward() => SkipAsync(15);

	Task SkipAsync(double seconds)
	{
		if (player is null || Current is null)
			return Task.CompletedTask;
		var target = Math.Max(Position + seconds, 0);
		if (Duration > 0)
			target = Math.Min(target, Duration);
		Position = target; // la barra si sposta subito, senza aspettare il PositionChanged
		return player.SeekTo(TimeSpan.FromSeconds(target));
	}

	[RelayCommand]
	void ToggleShuffle() => IsShuffle = !IsShuffle;

	// Ripeti cambiato su ExoPlayer, da noi (ShouldLoopPlayback) o da un controller esterno (vedi PlayerTuning):
	// "ripeti brano" e "ripeti tutto" diventano lo stato dell'app, visibile nel player; uno spegnimento conta solo se
	// stava ripetendo il brano (lo spegnimento che segue "ripeti tutto" è il nostro). Il valore restituito dice se
	// ExoPlayer deve ripetere il brano: la coda, anche con "ripeti tutto", la gestisce AdvanceAsync.
	public bool OnPlayerRepeatModeChanged(RepeatMode mode)
	{
		if (mode != RepeatMode.Off)
			Repeat = mode;
		else if (Repeat == RepeatMode.One && !IsEpisode)
			Repeat = RepeatMode.Off;
		var loop = Repeat == RepeatMode.One && !IsEpisode;
		player?.ShouldLoopPlayback = loop;
		return loop;
	}

	// Normale → ripeti (la coda) → casuale → normale. Una ripetizione del brano arrivata da fuori (cuffie, notifica)
	// conta come "ripeti" e il tocco passa al casuale.
	[RelayCommand]
	void CyclePlayMode()
	{
		(IsShuffle, Repeat) = (IsShuffle, Repeat) switch
		{
			(false, RepeatMode.Off) => (false, RepeatMode.All),
			(false, _) => (true, RepeatMode.Off),
			_ => (false, RepeatMode.Off),
		};
		player?.ShouldLoopPlayback = false;
	}

	[RelayCommand]
	void SeekStarted() => isSeeking = true;

	[RelayCommand]
	async Task SeekCompleted()
	{
		if (player is not null)
			await player.SeekTo(TimeSpan.FromSeconds(Position));
		isSeeking = false;
	}

	// Pulsante "+": pagina di scelta della playlist (con creazione), poi si torna dov'eri.
	// Senza parametro aggiunge il brano in riproduzione.
	[RelayCommand]
	Task AddToPlaylist(Track? track)
	{
		track ??= Current;
		return track is null || track.IsEpisode ? Task.CompletedTask : AddToPlaylistAsync([track]);
	}

	// Un brano o un album intero. I brani del catalogo entrano così come sono, senza aspettare YouTube: il video lo
	// trova LoadAsync quando partono, e CatalogService ricorda l'abbinamento.
	public async Task AddToPlaylistAsync(IReadOnlyList<Track> tracks)
	{
		if (tracks.Count == 0 || RootPage is not { } page)
			return;
		if (await PlaylistPickerPage.PickAsync(page.Navigation, library) is not { } playlist)
			return;
		var added = 0;
		foreach (var track in tracks)
			if (library.AddToPlaylist(playlist.Id, track))
				added++;
		Message?.Invoke((tracks.Count, added) switch
		{
			(1, 1) => $"Aggiunto a «{playlist.Name}»",
			(1, _) => $"«{tracks[0].Title}» è già in «{playlist.Name}»",
			var (all, n) when n == all => $"{n} brani aggiunti a «{playlist.Name}»",
			var (all, n) => $"{n} brani aggiunti a «{playlist.Name}», {all - n} c'erano già",
		});
	}

	// Menu ⋮ / long-press sulle righe brano.
	[RelayCommand]
	Task TrackMenu(Track track) => ShowTrackMenuAsync(track);

	// removeFrom: nome della playlist aperta, per offrire anche "Rimuovi da questa playlist".
	public async Task ShowTrackMenuAsync(Track track, string? removeFrom = null, Action? remove = null)
	{
		const string add = "Aggiungi a playlist";
		const string enqueue = "Aggiungi in coda";
		var removeLabel = removeFrom is null ? null : $"Rimuovi da «{removeFrom}»";
		// Gli episodi non vanno nelle playlist (come il "+" nascosto nel player).
		List<string> actions = track.IsEpisode ? [enqueue] : [add, enqueue];
		if (removeLabel is not null)
			actions.Add(removeLabel);
		var choice = await ActionPopup.ShowAsync(track.Title, track.Artist, [.. actions]);
		if (choice == add)
			await AddToPlaylist(track);
		else if (choice == enqueue)
			await Enqueue(track);
		else if (choice is not null && choice == removeLabel)
		{
			remove?.Invoke();
			Message?.Invoke($"Rimosso da «{removeFrom}»");
		}
	}

	async Task Enqueue(Track track)
	{
		if (Current is null)
		{
			await PlayAsync([track], track, "IN RIPRODUZIONE DALLA", "Coda");
			return;
		}
		queue.Insert(index + 1, track);
		Message?.Invoke("Aggiunto in coda");
	}

	static Page? RootPage => Application.Current?.Windows.FirstOrDefault()?.Page;

	static string Format(double seconds)
	{
		var t = TimeSpan.FromSeconds(Math.Max(seconds, 0));
		return t.TotalHours >= 1 ? t.ToString(@"h\:mm\:ss") : t.ToString(@"m\:ss");
	}
}

// ExoPlayer legge lo stream da un thread suo attraverso JNI: un'eccezione .NET che risale da Read
// arriva a Java come Error e il Loader di ExoPlayer la rilancia, facendo crashare l'app. Qui ogni errore
// di rete viene ritentato e, se persiste (o lo stream è stato chiuso al cambio brano), diventa fine stream.
file sealed class SafeStream(Stream inner) : Stream
{
	volatile bool disposed;

	public override int Read(byte[] buffer, int offset, int count)
	{
		for (var attempt = 1; ; attempt++)
		{
			try
			{
				return disposed ? 0 : inner.Read(buffer, offset, count);
			}
			catch (Exception) when (!disposed && attempt < 5)
			{
				Thread.Sleep(attempt * 1000); // siamo sul thread di caricamento di ExoPlayer, non sulla UI
			}
			catch (Exception ex)
			{
				SentrySdk.CaptureException(ex); // stream interrotto anche dopo i tentativi
				return 0;
			}
		}
	}

	public override bool CanRead => true;
	public override bool CanSeek => inner.CanSeek;
	public override bool CanWrite => false;
	public override long Length => inner.Length;
	public override long Position { get => inner.Position; set => inner.Position = value; }
	public override long Seek(long offset, SeekOrigin origin) => inner.Seek(offset, origin);
	public override void Flush() { }
	public override void SetLength(long value) => throw new NotSupportedException();
	public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

	protected override void Dispose(bool disposing)
	{
		disposed = true;
		if (disposing)
			inner.Dispose();
		base.Dispose(disposing);
	}
}
