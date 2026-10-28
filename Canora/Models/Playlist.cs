namespace Canora.Models;

// Immutabile: ogni modifica (brano aggiunto, ascolto) produce un nuovo record che sostituisce il vecchio
// nella ObservableCollection, così le righe legate si aggiornano senza INotifyPropertyChanged.
public sealed record Playlist(string Id, string Name, IReadOnlyList<Track> Tracks, DateTime Created, DateTime? LastPlayed, int PlayCount)
{
	public static Playlist Create(string name) => new(Guid.NewGuid().ToString("N"), name, [], DateTime.Now, null, 0);

	// Ordine della Libreria: ultimo ascolto, e per quelle mai ascoltate la data di creazione.
	public DateTime SortKey => LastPlayed ?? Created;

	public string? Cover => Tracks.FirstOrDefault()?.ArtworkUrl;

	public bool HasCover => Tracks.Count > 0;

	public bool NoCover => Tracks.Count == 0;

	public string TracksText => Tracks.Count == 1 ? "1 brano" : $"{Tracks.Count} brani";

	public string DurationText
	{
		get
		{
			var total = TimeSpan.FromTicks(Tracks.Sum(t => t.Duration?.Ticks ?? 0));
			return total.TotalHours >= 1 ? $"{(int)total.TotalHours} h {total.Minutes} min" : $"{(int)Math.Round(total.TotalMinutes)} min";
		}
	}

	public string PlaysText => PlayCount == 1 ? "1 ascolto" : $"{PlayCount} ascolti";

	// "12 brani · 45 min": seconda riga delle righe playlist e del dettaglio.
	public string Summary => Tracks.Count == 0 ? "Ancora nessun brano" : $"{TracksText} · {DurationText}";
}
