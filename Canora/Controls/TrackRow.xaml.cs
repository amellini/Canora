using System.Windows.Input;

namespace Canora.Controls;

public partial class TrackRow : ContentView
{
	// Comando del menu ⋮ (Mi piace / Aggiungi in coda), riceve il Track della riga.
	public static readonly BindableProperty MenuCommandProperty = BindableProperty.Create(nameof(MenuCommand), typeof(ICommand), typeof(TrackRow));

	public ICommand? MenuCommand
	{
		get => (ICommand?)GetValue(MenuCommandProperty);
		set => SetValue(MenuCommandProperty, value);
	}

	public TrackRow()
	{
		InitializeComponent();
#if WINDOWS
		Texts.Remove(MobileAlbumLabel);
		LinkStyle.Apply(AlbumLabel);
		LinkStyle.Apply(ArtistLabel);
#else
		// Telefono: tre righe, quindi riga più alta e copertina più grande; l'album sta sulla sua riga.
		Line.Remove(AlbumLabel);
		Line.Remove(DotLabel);
		RowGrid.HeightRequest = 84;
		RowGrid.ColumnDefinitions[0].Width = 76;
		Cover.WidthRequest = Cover.HeightRequest = 76;
		Texts.Spacing = 2;
#endif
		NowPlayingBadge.Track(this, () => (BindingContext as Models.Track)?.Id, Playing, TitleLabel);
		BindingContextChanged += (_, _) => FitLine(); // righe riciclate: testi nuovi, stessa larghezza
	}

	void OnLineSizeChanged(object? sender, EventArgs e) => FitLine();

	// Album e artista interi se ci stanno; se no l'artista ha al massimo metà della riga (o quel che l'album lascia) e
	// l'album il resto, entrambi coi puntini. Un FlexLayout con Shrink non basta: su Android l'artista usciva dalla riga.
	void FitLine()
	{
		var width = Line.Width;
		if (width <= 0)
			return;
		AlbumLabel.MaximumWidthRequest = ArtistLabel.MaximumWidthRequest = double.PositiveInfinity;
		var artist = ArtistLabel.Measure(double.PositiveInfinity, double.PositiveInfinity).Width;
		if (AlbumLabel.Parent is null || !AlbumLabel.IsVisible) // telefono (album sulla sua riga) o video senza album
		{
			ArtistLabel.MaximumWidthRequest = width;
			return;
		}
		var album = AlbumLabel.Measure(double.PositiveInfinity, double.PositiveInfinity).Width;
		var room = width - DotLabel.Measure(double.PositiveInfinity, double.PositiveInfinity).Width;
		if (album + artist <= room)
			return;
		var artistWidth = Math.Min(artist, Math.Max(room - album, room / 2));
		ArtistLabel.MaximumWidthRequest = artistWidth;
		AlbumLabel.MaximumWidthRequest = room - artistWidth;
	}

	// Album e artista aprono la loro scheda in qualunque lista; il catalogo è unico (singleton), come il player.
	static ViewModels.CatalogViewModel? Catalog => IPlatformApplication.Current?.Services.GetService<ViewModels.CatalogViewModel>();

	void OnAlbumTapped(object? sender, TappedEventArgs e)
	{
		if (BindingContext is Models.Track track)
			Catalog?.OpenAlbumOf(track);
	}

	void OnArtistTapped(object? sender, TappedEventArgs e)
	{
		if (BindingContext is Models.Track track)
			Catalog?.OpenArtistOf(track);
	}
}
