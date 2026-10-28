using Canora.Theming;

namespace Canora;

public partial class App : Application
{
	readonly IServiceProvider services;

	public App(IServiceProvider services)
	{
		InitializeComponent();
		// Il tema va unito alle risorse prima che si crei qualunque vista (anche i view model ne leggono i colori).
		Themes.ApplySaved();
		// L'interfaccia è disegnata solo scura (tutti i temi sono scuri).
		UserAppTheme = AppTheme.Dark;
		this.services = services;
	}

	// MainPage si risolve qui e non nel costruttore: stili e tema esistono solo dopo InitializeComponent.
	protected override Window CreateWindow(IActivationState? activationState)
	{
		var window = new Window(services.GetRequiredService<MainPage>()) { StatusBarTheme = StatusBarTheme.Dark };
#if WINDOWS
		// Su desktop si apre sempre massimizzata; rimpicciolendola non scende sotto una larghezza da telefono.
		window.Title = "Can'ora";
		// Barra del titolo nei colori del tema (risorse dinamiche: segue anche il cambio tema), con il logo dell'app.
		var titleBar = new TitleBar { Title = "Can'ora", Icon = "logo.png", HeightRequest = 32 };
		titleBar.SetDynamicResource(VisualElement.BackgroundColorProperty, "Background");
		titleBar.SetDynamicResource(TitleBar.ForegroundColorProperty, "TextPrimary");
		window.TitleBar = titleBar;
		window.MinimumWidth = 360;
		window.MinimumHeight = 600;
		window.Created += (_, _) => ((window.Handler?.PlatformView as Microsoft.UI.Xaml.Window)?.AppWindow.Presenter
			as Microsoft.UI.Windowing.OverlappedPresenter)?.Maximize();
		// Precedente, play/pausa e successivo nell'anteprima dell'icona sulla taskbar.
		window.Created += (_, _) =>
		{
			if (window.Handler?.PlatformView is Microsoft.UI.Xaml.Window native)
			{
				Taskbar.Attach(WinRT.Interop.WindowNative.GetWindowHandle(native), services.GetRequiredService<ViewModels.PlayerViewModel>());
				MouseDrag.Attach(native);
			}
		};
#endif
		return window;
	}
}
