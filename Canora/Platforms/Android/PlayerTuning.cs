using System.Runtime.CompilerServices;
using Android.Content;
using Android.OS;
using Android.Views;
using AndroidX.Media3.Common;
using AndroidX.Media3.ExoPlayer;
using AndroidX.Media3.Session;
using AndroidX.Media3.UI;
using CommunityToolkit.Maui.Core.Handlers;
using CommunityToolkit.Maui.Core.Views;
using CommunityToolkit.Maui.Views;
using Canora.ViewModels;

namespace Canora;

// MediaElement 10 crea l'ExoPlayer con le impostazioni di default: niente audio focus (suona sopra le altre app
// e le chiamate), niente pausa quando si staccano le cuffie, niente wake lock di rete a schermo spento.
// Si recupera il player dalla PlayerView interna e lo si configura come un player musicale.
static class PlayerTuning
{
	static IExoPlayer? tuned;

	public static void Apply(MediaElement element, Func<RepeatMode, bool> repeatChanged, Action next, Action previous)
	{
		if (element.Handler?.PlatformView is not ViewGroup root || Find(root)?.Player is not IExoPlayer exo || exo == tuned)
			return;
		tuned = exo;
		exo.AddListener(new RepeatGuard(exo, repeatChanged));
		if (element.Handler is MediaElementHandler handler && GetManager(handler) is { } manager && Session(manager) is { } session)
			session.Player = new QueueControls(exo, next, previous);

		var attributes = new AudioAttributes.Builder()
			.SetUsage(C.UsageMedia)!
			.SetContentType(C.AudioContentTypeMusic)!
			.Build()!;
		exo.SetAudioAttributes(attributes, true);
		exo.SetHandleAudioBecomingNoisy(true);
		exo.SetWakeMode(C.WakeModeNetwork);
	}

	// MediaElement avvia il suo servizio in primo piano (startForegroundService + bind) appena l'handler mappa Source,
	// anche vuoto, e lo ferma (stop + unbind) quando l'handler si stacca. startForeground però lo chiama solo in OnCreate,
	// che gira sul thread UI dopo quello che è già in coda: se l'handler si stacca prima (Android 17, finito un
	// aggiornamento, rilancia l'app con clear-task sopra l'activity appena aperta e la MainPage passa alla nuova), Android
	// abbatte un servizio non ancora in primo piano e chiude l'app con ForegroundServiceDidNotStartInTimeException.
	// Riprodotto col doppio avvio "am start" + "am start -f 0x10008000" a 0,2-0,3 s. Un bind nostro lo tiene in vita finché
	// non si connette (OnCreate, e con lui startForeground, è passato), poi molla: da lì lo stop del toolkit non fa danni.
	// Va chiamato su HandlerChanged, nello stesso giro in cui il toolkit avvia il servizio.
	public static void HoldService(MediaElement element)
	{
		if (element.Handler is null)
			return;
		var context = Android.App.Application.Context;
		// Il servizio del toolkit è internal: lo si raggiunge col nome Java che dichiara.
		var intent = new Intent().SetClassName(context, "communityToolkit.maui.media.services");
		context.BindService(intent, new ServiceHold(), Bind.AutoCreate);
	}

	sealed class ServiceHold : Java.Lang.Object, IServiceConnection
	{
		public void OnServiceConnected(ComponentName? name, IBinder? service) => Android.App.Application.Context.UnbindService(this);
		public void OnServiceDisconnected(ComponentName? name) { }
	}

	static PlayerView? Find(ViewGroup group)
	{
		for (var i = 0; i < group.ChildCount; i++)
		{
			var child = group.GetChildAt(i);
			if (child is PlayerView view)
				return view;
			if (child is ViewGroup nested && Find(nested) is { } found)
				return found;
		}
		return null;
	}

	// La sessione è privata nel toolkit; UnsafeAccessor, a differenza della reflection, regge il trimming.
	[UnsafeAccessor(UnsafeAccessorKind.Method, Name = "get_MediaManager")]
	static extern MediaManager? GetManager(MediaElementHandler handler);

	[UnsafeAccessor(UnsafeAccessorKind.Field, Name = "session")]
	static extern ref MediaSession? Session(MediaManager manager);

	// ExoPlayer contiene solo il brano corrente (la coda è dell'app): senza un successivo la MediaSession dichiara
	// "avanti" non disponibile e lo scarta (volante, Bluetooth, notifica), e "indietro" riavvolge e basta.
	// La sessione vede questo involucro, che li offre sempre e li passa ai comandi dell'app.
	sealed class QueueControls(IPlayer player, Action next, Action previous) : ForwardingPlayer(player)
	{
		static readonly int[] Queue =
		[
			InterfaceConsts.CommandSeekToNext, InterfaceConsts.CommandSeekToNextMediaItem,
			InterfaceConsts.CommandSeekToPrevious, InterfaceConsts.CommandSeekToPreviousMediaItem,
		];

		public override PlayerCommands? AvailableCommands =>
			new PlayerCommands.Builder().AddAll(base.AvailableCommands)!.AddAll(Queue)!.Build();

		public override bool IsCommandAvailable(int command) => Queue.Contains(command) || base.IsCommandAvailable(command);

		public override void SeekToNext() => next();
		public override void SeekToNextMediaItem() => next();
		public override void SeekToPrevious() => previous();
		public override void SeekToPreviousMediaItem() => previous();
	}

	// La MediaSession del toolkit accetta il ripeti dai controller esterni (Bluetooth, controlli di sistema, Assistente)
	// e MediaElement lo ignora (OnRepeatModeChanged vuoto): con ExoPlayer in ripetizione il brano gira all'infinito
	// senza MediaEnded, col ripeti dell'app spento. Lo decide l'app: ExoPlayer ripete solo con "ripeti brano".
	sealed class RepeatGuard(IExoPlayer exo, Func<RepeatMode, bool> changed) : Java.Lang.Object, IPlayerListener
	{
		// Player.REPEAT_MODE_OFF / ONE / ALL
		const int Off = 0, One = 1, All = 2;

		public void OnRepeatModeChanged(int repeatMode)
		{
			var loop = changed(repeatMode switch { One => RepeatMode.One, All => RepeatMode.All, _ => RepeatMode.Off });
			var wanted = loop ? One : Off;
			if (((IPlayer)exo).RepeatMode != wanted)
				((IPlayer)exo).RepeatMode = wanted;
		}
	}
}
