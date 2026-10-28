using System.Collections.ObjectModel;
using Canora.Models;
using SQLite;

namespace Canora.Services;

// Podcast seguiti ed episodi su SQLite. Gli episodi sono una cache del feed più lo stato d'ascolto
// (posizione, ascoltato), che sopravvive ai refresh del feed e anche allo smettere di seguire.
public sealed class PodcastStore
{
	readonly AppDatabase db;

	// Ordinati per ultimo ascolto (o da quando si seguono): la Libreria li mescola alle playlist con lo stesso criterio.
	public ObservableCollection<Podcast> Followed { get; } = [];

	// Stato di un episodio cambiato (chiave, posizione, ascoltato, durata reale o 0): il dettaglio aperto aggiorna la riga.
	public event Action<string, double, bool, double>? EpisodeChanged;

	public PodcastStore(AppDatabase db)
	{
		this.db = db;
		var followed = db.Run(c =>
		{
			c.CreateTable<PodcastEntity>();
			c.CreateTable<EpisodeEntity>();
			return c.Table<PodcastEntity>().ToList().Select(p => p.ToPodcast()).OrderByDescending(p => p.SortKey).ToList();
		});
		foreach (var p in followed ?? [])
			Followed.Add(p);
	}

	public Podcast? Find(string podcastId) => Followed.FirstOrDefault(p => p.Id == podcastId);

	public bool IsFollowed(string podcastId) => Find(podcastId) is not null;

	// Si parte da "visto adesso": i nuovi episodi sono quelli che usciranno da qui in poi.
	public Podcast Follow(Podcast podcast)
	{
		var followed = podcast with { FollowedAt = DateTime.Now, LastOpened = DateTime.Now, NewCount = 0 };
		Followed.Insert(0, followed);
		db.Write(c => c.InsertOrReplace(PodcastEntity.From(followed)));
		return followed;
	}

	public void Unfollow(string podcastId)
	{
		if (Find(podcastId) is { } podcast)
			Followed.Remove(podcast);
		db.Write(c => c.Execute("DELETE FROM Podcasts WHERE Id = ?", podcastId));
	}

	// Aperto il dettaglio: da qui partono i "nuovi" della prossima volta.
	public void Opened(string podcastId) => Update(podcastId, p => p with { LastOpened = DateTime.Now, NewCount = 0 }, moveToTop: false);

	// Avviato un episodio: il podcast sale in cima alla Libreria.
	public void Played(string podcastId) => Update(podcastId, p => p with { LastPlayed = DateTime.Now }, moveToTop: true);

	// Dopo un refresh del feed: descrizione/copertina aggiornate e conteggi.
	public void Refreshed(string podcastId, string description, string? artwork, int episodeCount, int newCount) =>
		Update(podcastId, p => p with
		{
			Description = description,
			ArtworkUrl = string.IsNullOrWhiteSpace(artwork) ? p.ArtworkUrl : artwork,
			EpisodeCount = episodeCount,
			NewCount = newCount,
		}, moveToTop: false);

	void Update(string podcastId, Func<Podcast, Podcast> change, bool moveToTop)
	{
		var index = Followed.ToList().FindIndex(p => p.Id == podcastId);
		if (index < 0)
			return;
		var updated = change(Followed[index]);
		Followed[index] = updated;
		if (moveToTop && index > 0)
			Followed.Move(index, 0);
		db.Write(c => c.InsertOrReplace(PodcastEntity.From(updated)));
	}

	// Episodi salvati (cache dell'ultimo feed letto) con il loro stato, dal più recente.
	public List<Episode> LoadEpisodes(string podcastId) =>
		db.Run(c => c.Table<EpisodeEntity>().Where(e => e.PodcastId == podcastId).ToList())?
			.Select(e => e.ToEpisode()).OrderByDescending(e => e.Published).ToList() ?? [];

	// Unisce il feed appena letto alla cache: aggiorna titoli/URL, non tocca posizione e "ascoltato".
	public List<Episode> MergeFeed(string podcastId, List<Episode> fromFeed)
	{
		db.Run(c =>
		{
			c.RunInTransaction(() =>
			{
				foreach (var e in fromFeed)
					c.Execute("""
						INSERT INTO Episodes (Key, PodcastId, Title, AudioUrl, Published, DurationTicks, ImageUrl, Position, Listened)
						VALUES (?, ?, ?, ?, ?, ?, ?, 0, 0)
						ON CONFLICT(Key) DO UPDATE SET Title = excluded.Title, AudioUrl = excluded.AudioUrl,
							Published = excluded.Published, DurationTicks = excluded.DurationTicks, ImageUrl = excluded.ImageUrl
						""", e.Key, e.PodcastId, e.Title, e.AudioUrl, e.Published, e.Duration?.Ticks, e.ImageUrl);
			});
			return true;
		});
		return LoadEpisodes(podcastId);
	}

	public double GetPosition(string episodeKey) =>
		db.Run(c => c.Table<EpisodeEntity>().Where(e => e.Key == episodeKey).Select(e => e.Listened ? 0 : e.Position).FirstOrDefault());

	// duration: quella reale letta dal player (0 = sconosciuta). I feed dichiarano spesso durate sbagliate o
	// nessuna, quindi appena la si conosce sostituisce quella del feed.
	public void SaveProgress(string episodeKey, double position, bool listened, double duration = 0)
	{
		var ticks = duration > 0 ? TimeSpan.FromSeconds(duration).Ticks : (long?)null;
		db.Write(c => c.Execute("UPDATE Episodes SET Position = ?, Listened = ?, DurationTicks = COALESCE(?, DurationTicks) WHERE Key = ?",
			position, listened, ticks, episodeKey));
		EpisodeChanged?.Invoke(episodeKey, position, listened, duration);
	}

	// Segnare come ascoltato azzera la posizione: riascoltandolo si riparte dall'inizio.
	public void SetListened(string episodeKey, bool listened) => SaveProgress(episodeKey, 0, listened);

	public void SetAllListened(string podcastId)
	{
		db.Write(c => c.Execute("UPDATE Episodes SET Position = 0, Listened = 1 WHERE PodcastId = ?", podcastId));
	}
}

[Table("Podcasts")]
sealed class PodcastEntity
{
	[PrimaryKey]
	public string Id { get; set; } = "";
	public string FeedUrl { get; set; } = "";
	public string Title { get; set; } = "";
	public string Author { get; set; } = "";
	public string ArtworkUrl { get; set; } = "";
	public string Description { get; set; } = "";
	public DateTime FollowedAt { get; set; }
	public DateTime? LastOpened { get; set; }
	public DateTime? LastPlayed { get; set; }
	public int NewCount { get; set; }
	public int EpisodeCount { get; set; }

	public static PodcastEntity From(Podcast p) => new()
	{
		Id = p.Id,
		FeedUrl = p.FeedUrl,
		Title = p.Title,
		Author = p.Author,
		ArtworkUrl = p.ArtworkUrl,
		Description = p.Description,
		FollowedAt = p.FollowedAt,
		LastOpened = p.LastOpened,
		LastPlayed = p.LastPlayed,
		NewCount = p.NewCount,
		EpisodeCount = p.EpisodeCount,
	};

	public Podcast ToPodcast() => new(Id, FeedUrl, Title, Author, ArtworkUrl, Description, FollowedAt, LastOpened, LastPlayed, NewCount, EpisodeCount);
}

[Table("Episodes")]
sealed class EpisodeEntity
{
	[PrimaryKey]
	public string Key { get; set; } = "";
	[Indexed]
	public string PodcastId { get; set; } = "";
	public string Title { get; set; } = "";
	public string AudioUrl { get; set; } = "";
	public DateTime Published { get; set; }
	public long? DurationTicks { get; set; }
	public string? ImageUrl { get; set; }
	public double Position { get; set; }
	public bool Listened { get; set; }

	public Episode ToEpisode() => new(Key, PodcastId, Title, AudioUrl, Published,
		DurationTicks is { } ticks ? TimeSpan.FromTicks(ticks) : null, ImageUrl, Position, Listened);
}
