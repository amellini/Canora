using System.Diagnostics;
using SQLite;

namespace Canora.Services;

// Un solo file e una sola connessione SQLite per tutta l'app (playlist, recenti, podcast).
// Ogni accesso passa dal lock, quindi letture all'avvio, scritture in coda e refresh dei feed in background
// non si sovrappongono mai sulla stessa connessione.
public sealed class AppDatabase
{
	readonly SQLiteConnection? connection;
	readonly Lock dbLock = new();
	readonly Lock queueGate = new();
	Task queue = Task.CompletedTask;

	// Non lancia mai: con SQLite in errore (disco pieno, file corrotto) un'eccezione qui sarebbe un crash a ogni
	// avvio. Senza connessione l'app funziona in memoria e non salva per questa sessione.
	public AppDatabase()
	{
		try
		{
			connection = new SQLiteConnection(Path.Combine(FileSystem.AppDataDirectory, "canora.db3"),
				SQLiteOpenFlags.ReadWrite | SQLiteOpenFlags.Create | SQLiteOpenFlags.FullMutex);
			connection.ExecuteScalar<string>("PRAGMA journal_mode=WAL");
		}
		catch (Exception ex)
		{
			Debug.WriteLine($"Database non disponibile, si lavora solo in memoria: {ex}");
		}
	}

	// Accesso sincrono (caricamento all'avvio, letture puntuali). Restituisce default se il database manca o fallisce.
	public T? Run<T>(Func<SQLiteConnection, T> work)
	{
		if (connection is null)
			return default;
		try
		{
			lock (dbLock)
				return work(connection);
		}
		catch (Exception ex)
		{
			Debug.WriteLine($"Accesso al database fallito: {ex}");
			return default;
		}
	}

	// Scrittura in coda seriale fuori dal thread UI: l'ordine è quello delle modifiche (un "rimuovi" seguito
	// subito da un "aggiungi" non può arrivare invertito). Un errore si logga e non ferma la coda.
	public void Write(Action<SQLiteConnection> change)
	{
		if (connection is null)
			return;
		lock (queueGate)
			queue = queue.ContinueWith(_ => Run(c =>
			{
				change(c);
				return true;
			}), TaskScheduler.Default);
	}
}
