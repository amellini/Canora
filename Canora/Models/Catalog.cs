using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Canora.Models;

// Catalogo musicale (Deezer), solo per sfogliare artisti e album come su Spotify. I brani si suonano da YouTube:
// Track con Id "dz:<id>" che il player abbina al video con la stessa durata quando parte (CatalogService.MatchAsync).

public sealed record CatalogArtist(long Id, string Name, string Picture, string PictureLarge, int Fans)
{
	static readonly CultureInfo Italian = CultureInfo.GetCultureInfo("it-IT");

	public string FansText => Fans == 1 ? "1 fan" : $"{Fans.ToString("N0", Italian)} fan";
}

// Anche le playlist (Type "playlist") passano da qui: Deezer con Id, YouTube con YouTubeId; Artist è chi la cura.
// Type "mix" sono le proposte di "Per te" in Home: i brani li fa Mix quando si aprono (CatalogService).
public sealed record CatalogAlbum(long Id, string Title, string Artist, string Cover, string CoverLarge, string? Released, string Type)
{
	public string? YouTubeId { get; init; }

	public Func<CancellationToken, Task<List<Track>>>? Mix { get; init; }

	// La card dei "Mi piace" in "Per te": al posto della copertina, il cuore sul riquadro.
	public bool IsLikes { get; init; }

	public bool IsPlaylist => Type is "playlist" or "mix";

	// Sotto la copertina: "Album · Incubus" nei risultati di ricerca, "2001 · Album" nella pagina dell'artista.
	public string Caption { get; init; } = "";

	public string Year => Released is { Length: >= 4 } date ? date[..4] : "";

	public string TypeText => Type switch
	{
		"single" => "Singolo",
		"ep" => "EP",
		"compile" => "Raccolta",
		"playlist" => "Playlist",
		"mix" => "Mix",
		_ => "Album",
	};

	public string AlbumLine => string.IsNullOrEmpty(Year) ? Artist : $"{Artist} · {Year}";
}

public enum ForYouTagKind { All, NewReleases, Genre, Decade }

// Una pillola di "Per te" in Home (LibraryViewModel): quella accesa decide le card sotto. GenreId solo per i generi di
// Deezer (dai "Mi piace"), Decade per i decenni (1990 = anni 90).
public sealed partial class ForYouTag(string name, ForYouTagKind kind) : ObservableObject
{
	public string Name { get; } = name;
	public ForYouTagKind Kind { get; } = kind;
	public long? GenreId { get; init; }
	public int Decade { get; init; }

	[ObservableProperty]
	public partial bool IsSelected { get; set; }
}
