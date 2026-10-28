using Microsoft.Maui.Layouts;
using Canora.Controls;
using Canora.ViewModels;

namespace Canora.Views;

// Overlay a tutta pagina sopra i tab: sale dal basso, si chiude col chevron, con il Back o trascinandolo giù.
public partial class NowPlayingView : ContentView
{
	CancellationTokenSource? animation;
	double panStart;

	public NowPlayingView()
	{
		InitializeComponent();
#if WINDOWS || MACCATALYST
		// Desktop: controlli più piccoli sotto la copertina da bordo a bordo (le misure del Figma sono per il pollice).
		foreach (View button in Buttons)
			(button.WidthRequest, button.HeightRequest) = (button.WidthRequest * DesktopScale, button.HeightRequest * DesktopScale);
		Buttons.HeightRequest *= DesktopScale;

		// Copertina (e karaoke) da bordo a bordo anche sotto barra, tempi, pulsanti e volume, come in landscape sul
		// telefono: la velatura in basso tiene leggibili i controlli su una copertina chiara, e titolo e "+" salgono
		// sopra i controlli, dove l'hero ne regola da sé il contrasto.
		foreach (var cover in new View[] { PlayerHero, Karaoke, LandscapeShade })
		{
			Place(cover, 0, 5, 0, 2);
			cover.Margin = new Thickness(-20, -10, -20, -28); // annulla il Padding della griglia
		}
		LandscapeShade.IsVisible = true;
		SideShade.IsVisible = false;
		BottomShade.HeightRequest = DesktopControls + 40;
		Karaoke.Padding = new Thickness(0, 0, 0, DesktopControls);
		PlayerHero.TitleMargin = new Thickness(20, 0, 64, DesktopControls + 18);
		AddButton.Margin = new Thickness(0, 0, 10, DesktopControls + 12);
		Rail.SetDynamicResource(BoxView.ColorProperty, "ProgressTrack"); // SeekRail, scuro, sparisce sulla velatura
		LinkStyle.Apply(RowArtistLabel);
		LinkStyle.Apply(KaraokeArtistLabel);
#endif
	}

	// Tocco sull'artista (copertina, riga dei controlli o karaoke): si chiude il player e si apre la sua scheda, come dalle
	// righe brano. Gli episodi no: il loro "artista" è il podcast, che nel catalogo musicale non c'è.
	void OnArtistTapped(object? sender, EventArgs e)
	{
		if (BindingContext is not PlayerViewModel { Current: { IsEpisode: false } track })
			return;
		IPlatformApplication.Current?.Services.GetService<CatalogViewModel>()?.OpenArtistOf(track);
		_ = HideAsync();
	}

#if WINDOWS || MACCATALYST
	// Titolo e artista nella riga dei controlli quando fra il bordo sinistro e i pulsanti centrati c'è posto: allora
	// la copertina non li ripete. In una finestra stretta restano sulla copertina, come prima; il volume, a destra,
	// si accorcia o sparisce.
	void UpdateRowTitle(double width)
	{
		// La fila dei pulsanti è larga 360, o tutta la larghezza se la finestra è più stretta.
		var inner = width - Root.Padding.HorizontalThickness;
		Buttons.WidthRequest = Math.Min(DesktopButtons, inner - Buttons.Margin.HorizontalThickness);
		var room = (inner - DesktopButtons) / 2 - RowTitle.Margin.Left - 24;
		// Il "+" (32 + 12 di spazio) segue il testo: titolo e artista si tagliano prima.
		RowTitleLabel.MaximumWidthRequest = RowArtistLabel.MaximumWidthRequest = Math.Max(room - 44, 0);
		// Lo stesso spazio a destra per il volume: lo slider si accorcia fino a 60, sotto sparisce (icona e margini: 40).
		VolumeSlider.WidthRequest = Math.Clamp(room - 40, 0, 140);
		VolumeBar.IsVisible = VolumeSlider.WidthRequest >= 60;
		var fits = room >= MinRowTitle;
		if (fits == RowTitle.IsVisible)
			return;
		RowTitle.IsVisible = fits;
		if (fits)
		{
			PlayerHero.RemoveBinding(Hero.TitleProperty);
			PlayerHero.RemoveBinding(Hero.SubtitleProperty);
			PlayerHero.Title = PlayerHero.Subtitle = null;
			AddButton.RemoveBinding(IsVisibleProperty); // il "+" passa accanto al titolo nella riga
			AddButton.IsVisible = false;
		}
		else
		{
			PlayerHero.SetBinding(Hero.TitleProperty, static (PlayerViewModel p) => p.Current?.Title);
			PlayerHero.SetBinding(Hero.SubtitleProperty, static (PlayerViewModel p) => p.Current?.Artist);
			AddButton.SetBinding(IsVisibleProperty, static (PlayerViewModel p) => p.CanAddToPlaylist);
		}
	}

	const double MinRowTitle = 160;
	const double DesktopButtons = 360; // la fila dei pulsanti centrata (WidthRequest su desktop nello XAML)
#endif

	const double DesktopScale = 0.75;
	const double DesktopControls = 152; // barra (32 + 10), tempi (8 + 17), pulsanti (76 × 0,75) e Padding inferiore 28

	// Karaoke: la riga in corso resta al centro della lista, anche quando si apre il testo a brano iniziato.
	protected override void OnBindingContextChanged()
	{
		base.OnBindingContextChanged();
		if (BindingContext is PlayerViewModel player)
			player.PropertyChanged += OnPlayerChanged; // view e view model vivono quanto l'app
	}

	void OnPlayerChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
	{
		if (e.PropertyName is nameof(PlayerViewModel.LyricIndex) or nameof(PlayerViewModel.IsLyricsOpen)
			&& sender is PlayerViewModel { IsLyricsOpen: true, LyricIndex: >= 0 and var line })
			LyricsList.ScrollTo(line, position: ScrollToPosition.Center, animate: true);
	}

	// Landscape: copertina (o karaoke) su tutto il pannello, con sopra a destra la colonna dei pulsanti rimpiccioliti
	// a passo costante e in basso barra e tempi.
	// Titolo, "+" e righe del karaoke si tengono fuori da colonna e barra; il play è più grande degli altri.
	// In landscape copertina e karaoke vanno anche sotto status bar e fascia della fotocamera: la griglia resta nella
	// safe area (MAUI somma gli inset al Padding e ne ripara i layout figli) e solo loro due ne escono con margini
	// negativi pari a Padding + inset; il loro contenuto (titolo, "+", righe) si riprende gli inset a mano.
	protected override void OnSizeAllocated(double width, double height)
	{
		base.OnSizeAllocated(width, height);
#if WINDOWS || MACCATALYST
		UpdateRowTitle(width);
#endif
		// Su desktop sempre la disposizione verticale: la finestra del desktop è quasi sempre più larga che alta, ma i
		// controlli vanno sotto la copertina (in colonna a destra uscivano dallo schermo, con barra e tempi tagliati).
		var landscape = width > height && DeviceInfo.Idiom != DeviceIdiom.Desktop;
		if (landscape == this.landscape)
			return;
		this.landscape = landscape;
		var span = landscape ? 1 : 2;
		Place(SeekBar, 2, 1, 0, span);
		Place(Times, 3, 1, 0, span);
		Place(Buttons, landscape ? 0 : 4, landscape ? 4 : 1, landscape ? 1 : 0, span);
		SeekBar.Margin = new Thickness(17, landscape ? 16 : 32, 15, 0);
		var inset = landscape ? SystemInsets() : default;
#if ANDROID
		// Su Android la safe area è padding nativo della griglia, che di suo ritaglia i figli: senza questo copertina
		// e karaoke si fermerebbero al bordo della safe area nonostante i margini negativi.
		(Root.Handler?.PlatformView as Android.Views.ViewGroup)?.SetClipToPadding(false);
#endif
		Root.Padding = new Thickness(20, 10, 20, landscape ? 12 : 28);
		var edge = new Thickness(20 + inset.Left, 10 + inset.Top, 20 + inset.Right, (landscape ? 12 : 0) + inset.Bottom);
		foreach (var cover in new View[] { PlayerHero, Karaoke, LandscapeShade })
		{
			Place(cover, 0, landscape ? 5 : 2, 0, landscape ? 2 : 1);
			cover.Margin = new Thickness(-edge.Left, -edge.Top, -edge.Right, -edge.Bottom);
		}
		LandscapeShade.IsVisible = landscape;
		Karaoke.Padding = landscape ? new Thickness(inset.Left, inset.Top, ControlsWidth + inset.Right, BarHeight + inset.Bottom) : 0;
		PlayerHero.TitleMargin = landscape ? new Thickness(edge.Left, 0, ControlsWidth + inset.Right + 64, BarHeight + inset.Bottom + 18) : new Thickness(20, 0, 64, 18);
		AddButton.Margin = landscape ? new Thickness(0, 0, ControlsWidth + inset.Right + 10, BarHeight + inset.Bottom + 12) : new Thickness(0, 0, 10, 12);

		var scale = landscape ? LandscapeScale : 1 / LandscapeScale;
		foreach (View button in Buttons)
			if (button != PlayButton)
				(button.WidthRequest, button.HeightRequest) = (button.WidthRequest * scale, button.HeightRequest * scale);
		Buttons.Direction = landscape ? FlexDirection.Column : FlexDirection.Row;
		Buttons.JustifyContent = FlexJustify.SpaceBetween; // in colonna: primo in cima, ultimo in fondo, il resto fra loro
		// Larghezza fissa: in una colonna Auto il FlexLayout verticale si misura stretto e il play strabordava.
		(Buttons.WidthRequest, Buttons.HeightRequest) = landscape ? (LandscapePlay, -1) : (-1, 76);
		Buttons.Margin = landscape ? 0 : new Thickness(15, 0, 15, 0);
		PlayButton.WidthRequest = PlayButton.HeightRequest = landscape ? LandscapePlay : 76;
	}

	const double LandscapeScale = 0.8;
	const double LandscapePlay = 80;
	const double ControlsWidth = LandscapePlay + 20; // colonna + Padding destro
	const double BarHeight = 72; // barra, tempi e Padding inferiore
	bool landscape;

	// Status bar, barra di navigazione e fotocamera, in unità MAUI.
	// ponytail: letti al cambio di orientamento; ruotando di 180° in landscape la fotocamera cambia lato senza
	// che la dimensione cambi, e gli inset restano quelli di prima finché non si torna in portrait.
	static Thickness SystemInsets()
	{
#if !ANDROID
		return default; // finestra su desktop: niente status bar né fotocamera
#else
		var decor = Platform.CurrentActivity?.Window?.DecorView;
		var insets = decor is null ? null : AndroidX.Core.View.ViewCompat.GetRootWindowInsets(decor)
			?.GetInsets(AndroidX.Core.View.WindowInsetsCompat.Type.SystemBars() | AndroidX.Core.View.WindowInsetsCompat.Type.DisplayCutout());
		if (insets is null)
			return default;
		var density = DeviceDisplay.MainDisplayInfo.Density;
		return new Thickness(insets.Left / density, insets.Top / density, insets.Right / density, insets.Bottom / density);
#endif
	}

	static void Place(View view, int row, int rowSpan, int column, int columnSpan)
	{
		Grid.SetRow(view, row);
		Grid.SetRowSpan(view, rowSpan);
		Grid.SetColumn(view, column);
		Grid.SetColumnSpan(view, columnSpan);
	}

	public async Task ShowAsync()
	{
		var token = Restart();
		TranslationY = Parent is VisualElement parent ? parent.Height : 2000;
		IsVisible = true;
		await this.TranslateToAsync(0, 0, 300, Easing.CubicOut, token);
	}

	public async Task HideAsync()
	{
		// TranslateToAsync restituisce true se l'animazione è stata annullata: in quel caso il pannello resta.
		if (!await this.TranslateToAsync(0, Height, 240, Easing.CubicIn, Restart()))
			IsVisible = false;
	}

	// MAUI 11: le animazioni *Async accettano un CancellationToken, così un'apertura interrompe una chiusura in corso.
	CancellationToken Restart()
	{
		animation?.Cancel();
		animation = new CancellationTokenSource();
		return animation.Token;
	}

	void OnCloseTapped(object? sender, TappedEventArgs e) => _ = HideAsync();

	void OnPanUpdated(object? sender, PanUpdatedEventArgs e)
	{
		switch (e.StatusType)
		{
			case GestureStatus.Started:
				Restart();
				panStart = TranslationY;
				break;
			case GestureStatus.Running:
				TranslationY = Math.Max(0, panStart + e.TotalY);
				break;
			case GestureStatus.Completed or GestureStatus.Canceled:
				_ = TranslationY > Height * 0.2 ? HideAsync() : this.TranslateToAsync(0, 0, 200, Easing.CubicOut, Restart());
				break;
		}
	}
}
