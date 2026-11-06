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
				// MAUI 11 dà a ogni pagina modale (i popup, la scelta della playlist) un suo WindowRootView agganciato al
				// LayoutUpdated della TitleBar. Chiudendo con una modale aperta, il suo smontaggio rifà il layout quando la
				// finestra di sistema non c'è più: InputNonClientPointerSource.GetForWindowId lancia e il processo termina.
				// Si chiudono prima le modali, a finestra viva, poi la finestra.
				native.AppWindow.Closing += async (_, args) =>
				{
					if (window.Page is not { Navigation.ModalStack.Count: > 0 } page)
						return;
					args.Cancel = true;
					try
					{
						while (page.Navigation.ModalStack.Count > 0)
							await page.Navigation.PopModalAsync(animated: false);
					}
					finally
					{
						native.Close();
					}
				};
			}
		};
#elif MACCATALYST
		// Come su Windows non scende sotto una larghezza da telefono; successivo e precedente dai tasti multimediali.
		window.Title = "Can'ora";
		window.MinimumWidth = 360;
		window.MinimumHeight = 600;
		RemoteCommands.Attach(services.GetRequiredService<ViewModels.PlayerViewModel>());
#endif
		return window;
	}
}
