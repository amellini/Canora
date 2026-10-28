using Canora.Services;

namespace Canora.Controls;

// Velatura sulla copertina di una riga in riproduzione, con tre barre da equalizzatore. Le righe (TrackRow,
// EpisodeRow) la mostrano e colorano il titolo con UpdateRow quando NowPlaying cambia: ci si iscrive solo mentre la
// riga è a schermo, così le righe riciclate dalle liste non restano appese all'evento statico.
public sealed class NowPlayingBadge : Grid
{
	public NowPlayingBadge()
	{
		BackgroundColor = Color.FromRgba(0, 0, 0, 0.55);
		var bars = new HorizontalStackLayout { Spacing = 3, HorizontalOptions = LayoutOptions.Center, VerticalOptions = LayoutOptions.Center };
		foreach (var height in (int[])[10, 18, 13])
		{
			var bar = new BoxView { WidthRequest = 4, HeightRequest = height, CornerRadius = 1, VerticalOptions = LayoutOptions.End };
			bar.SetDynamicResource(BoxView.ColorProperty, "Accent");
			bars.Add(bar);
		}
		Add(bars);
		InputTransparent = true;
	}

	// Collega una riga: id della voce, badge da mostrare, titolo da colorare.
	public static void Track(View row, Func<string?> id, NowPlayingBadge badge, Label title)
	{
		void Update()
		{
			var playing = NowPlaying.Is(id());
			badge.IsVisible = playing;
			// Sempre una risorsa dinamica: ClearValue non scollega quella dell'accento, e il titolo resterebbe colorato.
			// TextPrimary è il colore delle Label (lo stile RowTitle non ne mette uno).
			title.SetDynamicResource(Label.TextColorProperty, playing ? "Accent" : "TextPrimary");
		}
		row.Loaded += (_, _) => { NowPlaying.Changed += Update; Update(); };
		row.Unloaded += (_, _) => NowPlaying.Changed -= Update;
		row.BindingContextChanged += (_, _) => Update();
	}
}
