using System.Windows.Input;
using Canora.Services;
using Shiny.Maui.Controls.Images;
using Color = Microsoft.Maui.Graphics.Color;

namespace Canora.Controls;

// Immagine "hero" come nelle pagine web: tutta la larghezza, un terzo dell'altezza dello schermo (o tutto lo spazio
// disponibile con HeightRequest="-1"), titolo e
// sottotitolo sopra. Il testo è scuro sui toni chiari e chiaro su quelli scuri (luminosità misurata dove sta),
// con una velatura dello stesso tono che lo sostiene. I controlli aggiunti dentro in XAML (back, "+") stanno
// sopra e si colorano con TopColor (in alto) o TextColor (in basso).
// Con RenameCommand accanto al titolo compare una matita: il titolo diventa modificabile sul posto e alla
// conferma (Invio o tocco altrove) il comando riceve il nuovo nome.
public sealed class Hero : Grid
{
	const double PencilSize = 36;

	// ponytail: contrasto sulla copertina, non colori del tema (il tema cambia, la luminosità dell'immagine no).
	static readonly Color Light = Colors.White;
	static readonly Color Dark = Colors.Black;

	public static readonly BindableProperty SourceProperty = BindableProperty.Create(nameof(Source), typeof(string), typeof(Hero),
		propertyChanged: (b, _, _) => ((Hero)b).OnSourceChanged());
	public static readonly BindableProperty TitleProperty = BindableProperty.Create(nameof(Title), typeof(string), typeof(Hero));
	public static readonly BindableProperty SubtitleProperty = BindableProperty.Create(nameof(Subtitle), typeof(string), typeof(Hero));
	public static readonly BindableProperty TitleMarginProperty = BindableProperty.Create(nameof(TitleMargin), typeof(Thickness), typeof(Hero), new Thickness(18, 0, 18, 16),
		propertyChanged: (b, _, _) => ((Hero)b).UpdateTitleWidth());
	public static readonly BindableProperty RenameCommandProperty = BindableProperty.Create(nameof(RenameCommand), typeof(ICommand), typeof(Hero),
		propertyChanged: (b, _, _) => ((Hero)b).UpdateTitleWidth());
	public static readonly BindableProperty TextColorProperty = BindableProperty.Create(nameof(TextColor), typeof(Color), typeof(Hero), Light);
	public static readonly BindableProperty TopColorProperty = BindableProperty.Create(nameof(TopColor), typeof(Color), typeof(Hero), Light);

	public string? Source { get => (string?)GetValue(SourceProperty); set => SetValue(SourceProperty, value); }
	public string? Title { get => (string?)GetValue(TitleProperty); set => SetValue(TitleProperty, value); }
	public string? Subtitle { get => (string?)GetValue(SubtitleProperty); set => SetValue(SubtitleProperty, value); }
	public Thickness TitleMargin { get => (Thickness)GetValue(TitleMarginProperty); set => SetValue(TitleMarginProperty, value); }
	public ICommand? RenameCommand { get => (ICommand?)GetValue(RenameCommandProperty); set => SetValue(RenameCommandProperty, value); }
	public Color TextColor { get => (Color)GetValue(TextColorProperty); private set => SetValue(TextColorProperty, value); }
	public Color TopColor { get => (Color)GetValue(TopColorProperty); private set => SetValue(TopColorProperty, value); }

	// Tocco sul sottotitolo (nel player: l'artista, che apre la sua scheda).
	public event EventHandler? SubtitleTapped;

	readonly CoverImage picture = new() { Aspect = Aspect.AspectFill };
	readonly BoxView topShade = new();
	readonly BoxView bottomShade = new();
	readonly Label title = new() { FontFamily = "MontserratBold", FontSize = 26, CharacterSpacing = -0.8, MaxLines = 2, LineBreakMode = LineBreakMode.TailTruncation, VerticalOptions = LayoutOptions.Center };
	readonly Grid pencil = new() { WidthRequest = PencilSize, HeightRequest = PencilSize, VerticalOptions = LayoutOptions.Center };
	readonly Entry editor = new() { FontFamily = "MontserratBold", FontSize = 26, MaxLength = 60, ReturnType = ReturnType.Done };
	readonly HorizontalStackLayout titleRow = new();
	readonly VerticalStackLayout editorBox = new() { IsVisible = false };
	CancellationTokenSource? measuring;
	bool renaming; // EndRename in corso: Completed e poi Unfocused non devono rinominare due volte

	public Hero()
	{
		HeightRequest = ScreenHeight / 3;
		var cover = new BoxView(); // finché l'immagine non arriva (o se manca)
		cover.SetDynamicResource(BoxView.FillProperty, "PlaylistCover");
		Add(cover);
		Add(picture);
		Add(topShade);
		Add(bottomShade);

		var subtitle = new Label { FontFamily = "MontserratMedium", FontSize = 16, Opacity = 0.85, LineBreakMode = LineBreakMode.TailTruncation, HorizontalOptions = LayoutOptions.Start };
		var subtitleTap = new TapGestureRecognizer();
		subtitleTap.Tapped += (_, e) => SubtitleTapped?.Invoke(this, e);
		subtitle.GestureRecognizers.Add(subtitleTap);
		title.SetBinding(Label.TextProperty, static (Hero h) => h.Title, source: this);
		title.SetBinding(Label.TextColorProperty, static (Hero h) => h.TextColor, source: this);
		subtitle.SetBinding(Label.TextProperty, static (Hero h) => h.Subtitle, source: this);
		subtitle.SetBinding(Label.TextColorProperty, static (Hero h) => h.TextColor, source: this);

		// Matita subito dopo il testo del titolo (il titolo ha una larghezza massima, vedi UpdateTitleWidth).
		var pencilIcon = new Image { Source = "ic_edit.png", WidthRequest = 22, HeightRequest = 22 };
		pencilIcon.SetBinding(Tint.ColorProperty, static (Hero h) => h.TextColor, source: this);
		pencil.Add(pencilIcon);
		pencil.GestureRecognizers.Add(new TapGestureRecognizer { Command = new Command(BeginRename) });
		SemanticProperties.SetDescription(pencil, "Rinomina");
		titleRow.Add(title);
		titleRow.Add(pencil);

		// In modifica: il campo prende il posto del titolo, con una riga sotto che dice "qui si scrive"
		// (le Entry dell'app non hanno la sottolineatura di Android, vedi MauiProgram).
		editor.SetBinding(Entry.TextColorProperty, static (Hero h) => h.TextColor, source: this);
		editor.Completed += (_, _) => EndRename();
		editor.Unfocused += (_, _) => EndRename();
		var underline = new BoxView { HeightRequest = 2 };
		underline.SetBinding(BoxView.ColorProperty, static (Hero h) => h.TextColor, source: this);
		editorBox.Add(editor);
		editorBox.Add(underline);

		var texts = new VerticalStackLayout { Spacing = 4, VerticalOptions = LayoutOptions.End, Children = { titleRow, editorBox, subtitle } };
		texts.SetBinding(MarginProperty, static (Hero h) => h.TitleMargin, source: this);
		Add(texts);
		Shade(topLight: false, bottomLight: false);
		UpdateTitleWidth();
	}

	// Il titolo va a capo entro lo spazio lasciato dalla matita; senza comando di rinomina la matita non c'è.
	void UpdateTitleWidth()
	{
		pencil.IsVisible = RenameCommand is not null;
		var available = ScreenWidth - TitleMargin.Left - TitleMargin.Right;
		title.MaximumWidthRequest = available - (pencil.IsVisible ? PencilSize : 0);
	}

	async void BeginRename()
	{
		editor.Text = Title;
		titleRow.IsVisible = false;
		editorBox.IsVisible = true;
		editor.Focus();
		editor.CursorPosition = 0;
		editor.SelectionLength = editor.Text?.Length ?? 0;
		await editor.ShowSoftInputAsync(CancellationToken.None);
	}

	// Invio o tocco altrove confermano; un nome vuoto o uguale lascia tutto com'era.
	// Tastiera e focus si tolgono prima di nascondere il campo: altrimenti Android lascia la tastiera aperta
	// e sposta il focus sul primo controllo toccabile che trova (il cestino, evidenziato in grigio).
	async void EndRename()
	{
		if (!editorBox.IsVisible || renaming)
			return;
		renaming = true;
		await editor.HideSoftInputAsync(CancellationToken.None);
		editor.Unfocus();
		editorBox.IsVisible = false;
		titleRow.IsVisible = true;
		renaming = false;
		var name = editor.Text?.Trim();
		if (!string.IsNullOrEmpty(name) && name != Title)
			RenameCommand?.Execute(name);
	}

	static double ScreenHeight => DeviceDisplay.MainDisplayInfo.Height / DeviceDisplay.MainDisplayInfo.Density;
	static double ScreenWidth => DeviceDisplay.MainDisplayInfo.Width / DeviceDisplay.MainDisplayInfo.Density;

	void OnSourceChanged()
	{
		var url = Source;
		picture.Uri = string.IsNullOrWhiteSpace(url) ? null : url;
		MeasureLightness();
	}

	// HeightRequest="-1" in XAML (player espanso): l'hero riempie lo spazio e la luminosità va misurata sull'altezza
	// vera, quindi di nuovo a ogni cambio di dimensione.
	protected override void OnSizeAllocated(double width, double height)
	{
		base.OnSizeAllocated(width, height);
		if (HeightRequest < 0 && height > 0 && Math.Abs(height - measuredHeight) > 1)
			MeasureLightness();
	}

	double measuredHeight;

	// L'immagine riempie la cella che riceve ma non chiede spazio: una copertina quadrata in AspectFill chiederebbe
	// un'altezza pari alla larghezza, e con HeightRequest="-1" (player espanso) nella finestra larga di Windows la Grid
	// del player la distribuiva sulle righe dell'hero, spingendo barra, tempi e pulsanti fuori dallo schermo.
	// ShinyImage: la stessa cache da cui ArtworkColor misura la luminosità, quindi un download solo.
	sealed class CoverImage : ShinyImage
	{
		// La misura della piattaforma serve comunque: un'Image di WinUI mai misurata può non disegnarsi.
		protected override Size MeasureOverride(double widthConstraint, double heightConstraint)
		{
			base.MeasureOverride(widthConstraint, heightConstraint);
			return Size.Zero;
		}
	}

	async void MeasureLightness()
	{
		measuring?.Cancel();
		var cts = measuring = new CancellationTokenSource();
		var url = Source;
		if (string.IsNullOrWhiteSpace(url))
		{
			Shade(topLight: false, bottomLight: false);
			return;
		}
		var height = measuredHeight = HeightRequest > 0 ? HeightRequest : Height;
		if (height <= 0)
			return; // non ancora disposto: ci pensa OnSizeAllocated
		try
		{
			var (top, bottom) = await ArtworkColor.LightnessAsync(url, ScreenWidth / height, cts.Token);
			if (!cts.IsCancellationRequested)
				Shade(topLight: top > 0.6, bottomLight: bottom > 0.6);
		}
		catch (Exception ex) when (ex is not OutOfMemoryException)
		{
			// Immagine non misurabile: resta il testo chiaro su velatura scura, leggibile quasi ovunque.
		}
	}

	// Sfondo chiaro → testo scuro con velatura chiara, e viceversa.
	void Shade(bool topLight, bool bottomLight)
	{
		TopColor = topLight ? Dark : Light;
		TextColor = bottomLight ? Dark : Light;
		var top = topLight ? Light : Colors.Black;
		var bottom = bottomLight ? Light : Colors.Black;
		topShade.Background = new LinearGradientBrush([new(top.WithAlpha(0.45f), 0), new(top.WithAlpha(0), 0.3f)], new Point(0, 0), new Point(0, 1));
		bottomShade.Background = new LinearGradientBrush([new(bottom.WithAlpha(0), 0.4f), new(bottom.WithAlpha(0.65f), 1)], new Point(0, 0), new Point(0, 1));
	}
}
