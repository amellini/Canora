using Canora.Controls;
using Canora.Services;
using Velopack;
using Velopack.Sources;

namespace Canora.WinUI;

// Aggiornamento automatico per chi ha installato Can'ora con il Setup: all'avvio scarica in silenzio l'ultima release
// di GitHub (il pacchetto che release.yml pubblica accanto al Setup), poi propone di riavviare. Se non si riavvia,
// Velopack la applica al prossimo avvio (VelopackApp.Run in Program.cs). Lo zip e le build di sviluppo non sono
// installati: per loro resta l'invito a scaricare di UpdateCheck.
static class AppUpdate
{
	// false se l'app non è installata con il Setup.
	public static async Task<bool> TryAsync()
	{
		var manager = new UpdateManager(new GithubSource("https://github.com/amellini/Canora", null, false));
		if (!manager.IsInstalled)
			return false;
		try
		{
			if (await manager.CheckForUpdatesAsync() is not { } update)
				return true;
			await manager.DownloadUpdatesAsync(update);
			const string restart = "Riavvia ora";
			if (await ActionPopup.ShowAsync($"Can'ora {update.TargetFullRelease.Version} è pronta",
				"L'aggiornamento è già scaricato: riavvia ora, oppure parte da solo la prossima volta che apri Can'ora. Playlist, ascolti e podcast restano.",
				restart) != restart)
				return true;
			if (IPlatformApplication.Current?.Services.GetService<AppDatabase>() is { } db)
				await db.FlushAsync();
			manager.ApplyUpdatesAndRestart(update.TargetFullRelease);
		}
		catch (Exception ex)
		{
			// GitHub irraggiungibile o download interrotto: si riprova al prossimo avvio, non vale un errore.
			System.Diagnostics.Debug.WriteLine(ex);
		}
		return true;
	}
}
