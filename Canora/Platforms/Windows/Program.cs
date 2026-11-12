using Microsoft.UI.Dispatching;
using Microsoft.Windows.AppLifecycle;
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

		// Una sola istanza: la prima registra la chiave, le altre le passano l'attivazione (vedi MauiProgram, che porta la
		// finestra in primo piano) ed escono. Prima del segnalino di avvio: una seconda istanza non deve toccarlo.
		var instance = AppInstance.FindOrRegisterForKey("com.mellosofy.canora");
		if (!instance.IsCurrent)
		{
			var activation = AppInstance.GetCurrent().GetActivatedEventArgs();
			Task.Run(() => instance.RedirectActivationToAsync(activation).AsTask()).GetAwaiter().GetResult();
			return;
		}

		// Segnalino di avvio: resta sul disco solo se il processo muore nei primi secondi. Trovarlo vuol dire che
		// l'avvio precedente è andato in crash, e una versione che va in crash all'avvio non vive abbastanza da
		// scaricare l'aggiornamento che la correggerebbe (è successo con la 1.4.0): lo si fa qui, senza finestra.
		var starting = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Canora", "starting");
		try
		{
			if (File.Exists(starting))
				AppUpdate.Recover();
			Directory.CreateDirectory(Path.GetDirectoryName(starting)!);
			File.WriteAllText(starting, "");
			_ = Task.Delay(TimeSpan.FromSeconds(30)).ContinueWith(_ => Clear(starting));
			// Chiusura regolare prima dei 30 secondi: Application.Start non ritorna, l'app esce dal processo.
			AppDomain.CurrentDomain.ProcessExit += (_, _) => Clear(starting);
		}
		catch (Exception)
		{
			// Disco o rete: la protezione non deve mai impedire l'avvio.
		}

		WinRT.ComWrappersSupport.InitializeComWrappers();
		Microsoft.UI.Xaml.Application.Start(_ =>
		{
			SynchronizationContext.SetSynchronizationContext(new DispatcherQueueSynchronizationContext(DispatcherQueue.GetForCurrentThread()));
			new App();
		});
	}

	static void Clear(string path)
	{
		try
		{
			File.Delete(path);
		}
		catch (Exception)
		{
		}
	}
}
