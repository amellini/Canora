using System.Globalization;

namespace Canora.Models;

// Podcast trovato con la ricerca o seguito. Id = collectionId di Apple Podcasts, oppure "yt:" + id per un canale
// YouTube, che si apre e si segue come un podcast: gli episodi sono i suoi video (vedi PodcastService).
// Immutabile come Playlist: ogni modifica sostituisce il record nella collection.
public sealed record Podcast(string Id, string FeedUrl, string Title, string Author, string ArtworkUrl, string Description,
	DateTime FollowedAt, DateTime? LastOpened, DateTime? LastPlayed, int NewCount, int EpisodeCount)
{
	const string YouTubePrefix = "yt:";

	public static Podcast FromChannel(string channelId, string title, string avatarUrl) =>
		new(YouTubePrefix + channelId, $"https://www.youtube.com/channel/{channelId}", title, "YouTube", avatarUrl, "", default, null, null, 0, 0);

	public bool IsYouTube => Id.StartsWith(YouTubePrefix, StringComparison.Ordinal);

	public string ChannelId => Id[YouTubePrefix.Length..];

	// Badge accanto al titolo, in Libreria e nel dettaglio.
	public string Kind => IsYouTube ? "CANALE" : "PODCAST";

	// Ordine in Libreria, insieme alle playlist: ultimo ascolto, altrimenti quando si è iniziato a seguirlo.
	public DateTime SortKey => LastPlayed ?? FollowedAt;

	public bool HasNew => NewCount > 0;

	// Seconda riga in Libreria: i nuovi episodi se ce ne sono, altrimenti l'autore.
	public string Subtitle => (NewCount, IsYouTube) switch
	{
		(0, _) => Author,
		(1, true) => "1 nuovo video",
		(_, true) => $"{NewCount} nuovi video",
		(1, false) => "1 nuovo episodio",
		_ => $"{NewCount} nuovi episodi",
	};

	// Un canale appena trovato non sa quanti video ha finché non li si legge: niente "0 video" nell'attesa.
	public string EpisodesText => IsYouTube ? EpisodeCount > 0 ? $"{EpisodeCount} video" : "" : EpisodeCount == 1 ? "1 episodio" : $"{EpisodeCount} episodi";

	// I canali non hanno descrizione: senza, il dettaglio non lascia il suo spazio vuoto.
	public bool HasDescription => !string.IsNullOrWhiteSpace(Description);
}

// Episodio letto dal feed, con lo stato d'ascolto salvato. IsNew si calcola all'apertura del podcast:
// uscito dopo l'ultima volta che lo si era aperto.
public sealed record Episode(string Key, string PodcastId, string Title, string AudioUrl, DateTime Published, TimeSpan? Duration,
	string? ImageUrl, double Position, bool Listened)
{
	static readonly CultureInfo Italian = CultureInfo.GetCultureInfo("it-IT");

	// Video di un canale YouTube: l'audio arriva da YouTube come per i brani, la posizione si salva come per gli episodi.
	const string YouTubeAudioPrefix = "youtube:";

	public static string YouTubeAudio(string videoId) => YouTubeAudioPrefix + videoId;

	public static string? VideoIdOf(string audioUrl) =>
		audioUrl.StartsWith(YouTubeAudioPrefix, StringComparison.Ordinal) ? audioUrl[YouTubeAudioPrefix.Length..] : null;

	// Published inventata per tenere l'ordine (video di un canale più vecchi di quelli nel feed RSS): non si mostra.
	public bool DateUnknown { get; init; }

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
			var date = Published == DateTime.MinValue || DateUnknown ? "" : Published.ToString(Published.Year == DateTime.Now.Year ? "d MMM" : "d MMM yyyy", Italian);
			return Duration is not { } d ? date : date.Length > 0 ? $"{date} · {Format(d)}" : Format(d);
		}
	}

	string Remaining => Duration is { } d ? Format(TimeSpan.FromSeconds(Math.Max(d.TotalSeconds - Position, 0))).ToUpperInvariant() : "";

	static string Format(TimeSpan d) => d.TotalHours >= 1 ? $"{(int)d.TotalHours} h {d.Minutes} min" : $"{Math.Max(1, (int)Math.Round(d.TotalMinutes))} min";

	public Track ToTrack(Podcast podcast) =>
		new(Key, Title, podcast.Title, Duration, ImageUrl ?? podcast.ArtworkUrl) { AudioUrl = AudioUrl };
}
