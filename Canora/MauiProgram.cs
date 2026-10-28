using CommunityToolkit.Maui;
using Microsoft.Extensions.Logging;
using Canora.Controls;
using Canora.Services;
using Canora.ViewModels;
using YoutubeExplode;

namespace Canora;

public static class MauiProgram
{
	public static MauiApp CreateMauiApp()
	{
		var builder = MauiApp.CreateBuilder();
		builder
			.UseMauiApp<App>()
			.UseMauiCommunityToolkit()
#if ANDROID
			.UseShiny()
#endif
			// true = foreground service: riproduzione in background, notifica e controlli in lockscreen.
			.UseMauiCommunityToolkitMediaElement(isAndroidForegroundServiceEnabled: true)
			.ConfigureFonts(fonts =>
			{
				// Montserrat sostituisce Gotham (proprietario) del Figma; Poppins è quello del design.
				fonts.AddFont("Montserrat-Regular.ttf", "MontserratRegular");
				fonts.AddFont("Montserrat-Medium.ttf", "MontserratMedium");
				fonts.AddFont("Montserrat-Bold.ttf", "MontserratBold");
				fonts.AddFont("Poppins-Medium.ttf", "PoppinsMedium");
				fonts.AddFont("Poppins-SemiBold.ttf", "PoppinsSemiBold");
				fonts.AddFont("Poppins-Bold.ttf", "PoppinsBold");
			});

		Tint.Register();
#if ANDROID
		// La barra di ricerca del design è un rettangolo bianco pieno: niente sottolineatura dell'EditText.
		Microsoft.Maui.Handlers.EntryHandler.Mapper.AppendToMapping("NoUnderline", (handler, _) => handler.PlatformView.Background = null);
		// Confermato un campo dalla tastiera (Invio, Cerca, Avanti…) la tastiera si chiude, a meno che ci sia un altro campo
		// in cui passare. Android la chiude da sé solo con "Fine"; l'handler di MAUI (Completed, ReturnCommand) resta, questo
		// si aggiunge dopo.
		Microsoft.Maui.Handlers.EntryHandler.Mapper.AppendToMapping("HideKeyboardOnDone", (handler, _) =>
			handler.PlatformView.EditorAction += (_, _) =>
			{
				if (handler.PlatformView.FocusSearch(Android.Views.FocusSearchDirection.Forward) is not Android.Widget.EditText)
					MainActivity.HideKeyboard(handler.PlatformView);
			});
#elif WINDOWS
		// Lo stesso per il TextBox di WinUI: niente sfondo proprio (a riposo, al passaggio del mouse e col focus) né linea
		// d'accento: lo sfondo è quello del contenitore (gli stati visivi leggono queste risorse, non le proprietà).
		Microsoft.Maui.Handlers.EntryHandler.Mapper.AppendToMapping("NoUnderline", (handler, _) =>
		{
			var transparent = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Transparent);
			var resources = handler.PlatformView.Resources;
			resources["TextControlBackground"] = transparent;
			resources["TextControlBackgroundPointerOver"] = transparent;
			resources["TextControlBackgroundFocused"] = transparent;
			resources["TextControlBorderThemeThicknessFocused"] = new Microsoft.UI.Xaml.Thickness(0);
			handler.PlatformView.BorderThickness = new Microsoft.UI.Xaml.Thickness(0);
		});
		// I caroselli orizzontali (album, singoli, chip) in WinUI scorrono solo col touch: col mouse si trascinano (vedi
		// MouseDrag), e ci sono la barra (sottile, si allarga al passaggio) e Shift+rotella. La rotella semplice resta alla
		// pagina verticale, come nei browser.
		Microsoft.Maui.Handlers.ScrollViewHandler.Mapper.AppendToMapping(nameof(IScrollView.HorizontalScrollBarVisibility), (handler, view) =>
		{
			if (view.Orientation == ScrollOrientation.Horizontal)
				handler.PlatformView.HorizontalScrollBarVisibility = Microsoft.UI.Xaml.Controls.ScrollBarVisibility.Auto;
		});
		// Il ScrollViewer marca Handled lo Shift+rotella senza scorrere: serve handledEventsToo.
		Microsoft.Maui.Handlers.ScrollViewHandler.Mapper.AppendToMapping(nameof(IScrollView.Orientation), (handler, view) =>
		{
			handler.PlatformView.RemoveHandler(Microsoft.UI.Xaml.UIElement.PointerWheelChangedEvent, ShiftWheel);
			if (view.Orientation == ScrollOrientation.Horizontal)
				handler.PlatformView.AddHandler(Microsoft.UI.Xaml.UIElement.PointerWheelChangedEvent, ShiftWheel, true);
		});
#endif

		// Tutto singleton: una sola pagina radice che vive quanto l'app, e un solo player.
		builder.Services.AddSingleton(_ => new YoutubeClient(new HttpClient(new SkipStreamProbes())));
		builder.Services.AddSingleton<AppDatabase>();
		builder.Services.AddSingleton<LibraryStore>();
		builder.Services.AddSingleton<PodcastStore>();
		builder.Services.AddSingleton<PodcastService>();
		builder.Services.AddSingleton<CatalogService>();
		builder.Services.AddSingleton<LyricsService>();
		builder.Services.AddSingleton<PlayerViewModel>();
		builder.Services.AddSingleton<CatalogViewModel>();
		builder.Services.AddSingleton<SearchViewModel>();
		builder.Services.AddSingleton<PodcastsViewModel>();
		builder.Services.AddSingleton<LibraryViewModel>();
		builder.Services.AddSingleton<MainPage>();
#if ANDROID
		// Generato da Shiny.AppFunctions: le funzioni per l'assistente vocale (Platforms/Android/AssistantFunctions.cs).
		builder.Services.AddAppFunctions();
#endif

#if DEBUG
		builder.Logging.AddDebug();
#endif

		return builder.Build();
	}

#if WINDOWS
	static readonly Microsoft.UI.Xaml.Input.PointerEventHandler ShiftWheel = (sender, e) =>
	{
		var properties = e.GetCurrentPoint(null).Properties;
		if (!e.KeyModifiers.HasFlag(Windows.System.VirtualKeyModifiers.Shift) || properties.IsHorizontalMouseWheel)
			return;
		var scroller = (Microsoft.UI.Xaml.Controls.ScrollViewer)sender;
		scroller.ChangeView(scroller.HorizontalOffset - properties.MouseWheelDelta, null, null);
		e.Handled = true;
	};
#endif
}
