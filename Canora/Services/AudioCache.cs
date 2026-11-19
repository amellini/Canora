using System.Security.Cryptography;
using System.Text;
using Sentry;

namespace Canora.Services;

// Cache dell'audio ascoltato: il brano (o l'episodio breve) che parte dalla rete si scarica intanto su file, così
// riascoltarlo, o riprenderlo ore dopo una pausa, non aspetta la rete. Un file per brano nella cartella cache dell'app
// (Android la può svuotare da sé se manca spazio), con il nome dato dall'hash della chiave (id del video YouTube, URL
// dell'audio per gli episodi del feed). MRU senza database: l'ultimo ascolto è la data di modifica del file, toccata a
// ogni riproduzione. Chi non si ascolta da 15 giorni se ne va, e oltre 500 MB escono i meno recenti.
public static class AudioCache
{
	const long MaxBytes = 500L * 1024 * 1024;
	static readonly TimeSpan MaxAge = TimeSpan.FromDays(15);

	// Oltre questa durata si ascolta solo in streaming: un episodio di mezz'ora sono ~15-70 MB (Spreaker dà l'originale,
	// anche a 320 kbps), un mix di ore ne prenderebbe una bella fetta.
	public static readonly TimeSpan MaxDuration = TimeSpan.FromMinutes(30);

	static readonly string Folder = Path.Combine(FileSystem.CacheDirectory, "audio");

	// Il file che il player ha caricato (nome, null se suona dalla rete): né "Svuota cache" né Trim lo cancellano. Su
	// Windows Media Foundation lascia cancellarlo, ma poi il brano in pausa non riprende più e si salta al successivo.
	public static string? InUse { get; set; }

	// Il file completo del brano, se c'è; conta come appena ascoltato.
	public static string? Find(string key)
	{
		try
		{
			if (!Directory.Exists(Folder))
				return null;
			var file = Directory.EnumerateFiles(Folder, Name(key) + ".*").FirstOrDefault(f => !IsPartial(f));
			if (file is not null)
				File.SetLastWriteTimeUtc(file, DateTime.UtcNow);
			return file;
		}
		catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
		{
			return null;
		}
	}

	// Scarica il brano su un file temporaneo e lo mette in cache solo se arriva tutto: annullato (cambio brano) o
	// fallito, il pezzo scaricato si butta. fetch scrive il file che riceve; gira su un thread del pool (su Android
	// chiudere le risposte HTTP sul thread UI è un errore).
	public static async Task SaveAsync(string key, string extension, Func<string, CancellationToken, Task> fetch, CancellationToken token)
	{
		var name = Name(key);
		var part = Path.Combine(Folder, $"{name}.{Guid.NewGuid():N}.part");
		try
		{
			Directory.CreateDirectory(Folder);
			await Task.Run(() => fetch(part, token), token);
			File.Move(part, Path.Combine(Folder, $"{name}.{extension}"), overwrite: true);
			Trim();
		}
		catch (Exception ex)
		{
			Delete(part);
			if (ex is not OperationCanceledException)
				SentrySdk.Logger.LogWarning("Cache audio non salvata: {0}", ex.Message);
		}
	}

	// Via chi non si ascolta da 15 giorni, poi i meno recenti finché si sta nei 500 MB; e i download rimasti a metà
	// (app chiusa mentre scaricava), riconoscibili perché non si scrivono da un'ora. Il file nel player resta (InUse).
	public static void Trim()
	{
		try
		{
			if (!Directory.Exists(Folder))
				return;
			var now = DateTime.UtcNow;
			long total = 0;
			foreach (var file in new DirectoryInfo(Folder).GetFiles().OrderByDescending(f => f.LastWriteTimeUtc))
			{
				var stale = IsPartial(file.Name)
					? now - file.LastWriteTimeUtc > TimeSpan.FromHours(1)
					: now - file.LastWriteTimeUtc > MaxAge || (total += file.Length) > MaxBytes;
				if (stale)
					Delete(file.FullName);
			}
		}
		catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
		{
		}
	}

	public static long Size
	{
		get
		{
			try
			{
				return Directory.Exists(Folder) ? new DirectoryInfo(Folder).GetFiles().Sum(f => f.Length) : 0;
			}
			catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
			{
				return 0;
			}
		}
	}

	public static void Clear()
	{
		try
		{
			if (Directory.Exists(Folder))
				foreach (var file in Directory.GetFiles(Folder))
					Delete(file);
		}
		catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
		{
		}
	}

	// Hash e non la chiave: gli URL non sono nomi di file, e gli id YouTube distinguono maiuscole e minuscole (Windows no).
	static string Name(string key) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key)), 0, 16);

	static bool IsPartial(string path) => path.EndsWith(".part", StringComparison.Ordinal);

	static void Delete(string path)
	{
		if (Path.GetFileName(path) == InUse)
			return;
		try
		{
			File.Delete(path);
		}
		catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
		{
		}
	}
}
