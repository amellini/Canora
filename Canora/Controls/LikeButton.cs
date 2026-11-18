using Canora.Models;
using Canora.Services;

namespace Canora.Controls;

// Il cuore dei "Mi piace": vuoto, oppure pieno nell'accento se il brano piace. Solo per i brani: con un episodio (podcast
// o video di un canale) si nasconde. Come NowPlayingBadge si iscrive a LibraryStore.LikesChanged solo mentre è a schermo,
// così le righe riciclate dalle liste non restano appese all'evento.
public sealed class LikeButton : Grid
{
	public static readonly BindableProperty TrackProperty = BindableProperty.Create(nameof(Track), typeof(Track), typeof(LikeButton),
		propertyChanged: (button, _, _) => ((LikeButton)button).Update());

	// Il colore del cuore vuoto (sull'hero del player quello del testo); senza, quello del testo dell'app.
	public static readonly BindableProperty ColorProperty = BindableProperty.Create(nameof(Color), typeof(Color), typeof(LikeButton),
		propertyChanged: (button, _, _) => ((LikeButton)button).Update());

	public Track? Track
	{
		get => (Track?)GetValue(TrackProperty);
		set => SetValue(TrackProperty, value);
	}

	public Color? Color
	{
		get => (Color?)GetValue(ColorProperty);
		set => SetValue(ColorProperty, value);
	}

	readonly Image icon = new() { WidthRequest = 24, HeightRequest = 24, Aspect = Aspect.AspectFit };

	// Lato del cuore: 24 nelle righe, 28 sull'hero del player come il "+".
	public double IconSize
	{
		get => icon.WidthRequest;
		set => icon.WidthRequest = icon.HeightRequest = value;
	}

	public LikeButton()
	{
		Add(icon);
		GestureRecognizers.Add(new TapGestureRecognizer { Command = new Command(Toggle) });
		Loaded += (_, _) =>
		{
			if (Store is { } store)
				store.LikesChanged += OnLikesChanged;
			Update();
		};
		Unloaded += (_, _) =>
		{
			if (Store is { } store)
				store.LikesChanged -= OnLikesChanged;
		};
	}

	// Lo store è unico (singleton), come il catalogo per TrackRow.
	static LibraryStore? Store => IPlatformApplication.Current?.Services.GetService<LibraryStore>();

	// Anche dal genere e dall'anno arrivati da Deezer, che possono arrivare fuori dal thread UI.
	void OnLikesChanged() => MainThread.BeginInvokeOnMainThread(Update);

	void Toggle()
	{
		if (Track is not { IsEpisode: false } track || Store is not { } store)
			return;
		var liked = store.ToggleLike(track);
		IPlatformApplication.Current?.Services.GetService<ViewModels.PlayerViewModel>()?.Notify(liked ? "Aggiunto ai Mi piace" : "Tolto dai Mi piace");
	}

	void Update()
	{
		var track = Track;
		IsVisible = track is { IsEpisode: false };
		var liked = track is not null && Store?.IsLiked(track) == true;
		icon.Source = liked ? "ic_heart_filled.png" : "ic_heart.png";
		// Prima via il colore fisso: un valore locale vince su una risorsa dinamica messa dopo, e sull'hero il cuore pieno
		// restava del colore del testo invece che nell'accento.
		icon.ClearValue(Tint.ColorProperty);
		if (!liked && Color is { } color)
			Tint.SetColor(icon, color);
		else
			icon.SetDynamicResource(Tint.ColorProperty, liked ? "Accent" : "TextPrimary");
		SemanticProperties.SetDescription(this, liked ? "Togli dai Mi piace" : "Mi piace");
	}
}
