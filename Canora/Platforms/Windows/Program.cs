using Microsoft.UI.Dispatching;
using Velopack;

namespace Canora.WinUI;

// Il Main che WinUI genererebbe (vedi DISABLE_XAML_GENERATED_MAIN nel csproj), con Velopack in testa: durante
// installazione, aggiornamento e disinstallazione il Setup lancia l'app con i suoi argomenti, e Run li gestisce e
// chiude prima che parta la finestra. All'avvio applica anche un aggiornamento già scaricato (vedi AppUpdate).
// Lanciata dallo zip o da Visual Studio non fa niente.
public static class Program
{
	[STAThread]
	static void Main(string[] args)
	{
		VelopackApp.Build().Run();
		WinRT.ComWrappersSupport.InitializeComWrappers();
		Microsoft.UI.Xaml.Application.Start(_ =>
		{
			SynchronizationContext.SetSynchronizationContext(new DispatcherQueueSynchronizationContext(DispatcherQueue.GetForCurrentThread()));
			new App();
		});
	}
}
