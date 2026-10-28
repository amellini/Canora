using System.Globalization;

namespace Canora.Models;

// Podcast trovato con la ricerca o seguito. Id = collectionId di Apple Podcasts.
// Immutabile come Playlist: ogni modifica sostituisce il record nella collection.
public sealed record Podcast(string Id, string FeedUrl, string Title, string Author, string ArtworkUrl, string Description,
	DateTime FollowedAt, DateTime? LastOpened, DateTime? LastPlayed, int NewCount, int EpisodeCount)
{
	// Ordine in Libreria, insieme alle playlist: ultimo ascolto, altrimenti quando si è iniziato a seguirlo.
	public DateTime SortKey => LastPlayed ?? FollowedAt;

	public bool HasNew => NewCount > 0;

	// Seconda riga in Libreria: i nuovi episodi se ce ne sono, altrimenti l'autore.
	public string Subtitle => NewCount switch
	{
		0 => Author,
		1 => "1 nuovo episodio",
		_ => $"{NewCount} nuovi episodi",
	};

	public string EpisodesText => EpisodeCount == 1 ? "1 episodio" : $"{EpisodeCount} episodi";
}

// Episodio letto dal feed, con lo stato d'ascolto salvato. IsNew si calcola all'apertura del podcast:
// uscito dopo l'ultima volta che lo si era aperto.
public sealed record Episode(string Key, string PodcastId, string Title, string AudioUrl, DateTime Published, TimeSpan? Duration,
	string? ImageUrl, double Position, bool Listened)
{
	static readonly CultureInfo Italian = CultureInfo.GetCultureInfo("it-IT");

	public bool IsNew { get; init; }

	public bool InProgress => !Listened && Position > 5;

	public double Progress => Duration is { TotalSeconds: > 0 } d ? Math.Clamp(Position / d.TotalSeconds, 0, 1) : 0;

	// Badge della riga: stessa forma del badge durata delle righe brano. "Ascoltato" lo dice già il pulsante pieno.
	public string Badge => Listened ? ""
		: InProgress ? $"{Remaining} RIMASTI"
		: IsNew ? "NUOVO"
		: "";

	public bool HasBadge => Badge.Length > 0;

	public string Info
	{
		get
		{
			var date = Published == DateTime.MinValue ? "" : Published.ToString(Published.Year == DateTime.Now.Year ? "d MMM" : "d MMM yyyy", Italian);
			return Duration is { } d ? $"{date} · {Format(d)}" : date;
		}
	}

	string Remaining => Duration is { } d ? Format(TimeSpan.FromSeconds(Math.Max(d.TotalSeconds - Position, 0))).ToUpperInvariant() : "";

	static string Format(TimeSpan d) => d.TotalHours >= 1 ? $"{(int)d.TotalHours} h {d.Minutes} min" : $"{Math.Max(1, (int)Math.Round(d.TotalMinutes))} min";

	public Track ToTrack(Podcast podcast) =>
		new(Key, Title, podcast.Title, Duration, ImageUrl ?? podcast.ArtworkUrl) { AudioUrl = AudioUrl };
}
