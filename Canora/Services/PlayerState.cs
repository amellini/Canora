using System.Text.Json;
using Canora.Models;
using SQLite;
using Sentry;

namespace Canora.Services;

// Cosa suonava quando l'app si è chiusa (anche uccisa da Android in background): coda, brano e punto, per rimettere il
// mini-player com'era, in pausa, e far ripartire da lì con un tocco. Una riga sola. Per gli episodi l'avanzamento vero
// resta in PodcastStore (Episodes.Position), che all'apertura vince su quello qui.
public sealed class PlayerState
{
	readonly AppDatabase db;

	public PlayerState(AppDatabase db)
	{
		this.db = db;
		db.Run(c => c.CreateTable<PlayerStateEntity>());
	}

	// Duration: quella reale letta dal player (0 = sconosciuta): quella del brano in coda manca spesso (feed senza durate).
	public sealed record Saved(List<Track> Queue, int Index, string Header, string Title, string? Source, double Position, double Duration);

	// ponytail: la coda si riscrive intera a ogni cambio di brano (al più qualche centinaio di righe di JSON); una tabella
	// con una riga per brano servirebbe solo con code enormi.
	public void Save(List<Track> queue, int index, string header, string title, string? source, double position, double duration)
	{
		var json = JsonSerializer.Serialize(queue, AppJson.Default.ListTrack); // qui, sul thread del player: la coda cambia
		db.Write(c => c.InsertOrReplace(new PlayerStateEntity
		{
			Id = 1,
			Queue = json,
			QueueIndex = index,
			Header = header,
			Title = title,
			Source = source,
			Position = position,
			Duration = duration,
		}));
	}

	public void SavePosition(double position, double duration) =>
		db.Write(c => c.Execute("UPDATE PlayerState SET Position = ?, Duration = ? WHERE Id = 1", position, duration));

	public Saved? Load()
	{
		if (db.Run(c => c.Find<PlayerStateEntity>(1)) is not { } row)
			return null;
		try
		{
			return JsonSerializer.Deserialize(row.Queue, AppJson.Default.ListTrack) is { } queue && row.QueueIndex >= 0 && row.QueueIndex < queue.Count
				? new Saved(queue, row.QueueIndex, row.Header, row.Title, row.Source, row.Position, row.Duration)
				: null;
		}
		catch (JsonException ex)
		{
			SentrySdk.CaptureException(ex); // JSON rovinato: si riparte senza mini-player, non vale un crash
			return null;
		}
	}
}

[Table("PlayerState")]
sealed class PlayerStateEntity
{
	[PrimaryKey]
	public int Id { get; set; }
	public string Queue { get; set; } = "";
	public int QueueIndex { get; set; }
	public string Header { get; set; } = "";
	public string Title { get; set; } = "";
	public string? Source { get; set; }
	public double Position { get; set; }
	public double Duration { get; set; } // colonna aggiunta da CreateTable ai database esistenti
}
