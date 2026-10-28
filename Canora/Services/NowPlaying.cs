using Canora.Models;

namespace Canora.Services;

// Quale brano o episodio suona, per evidenziarne le righe ovunque compaiano (ricerca, artista, album, playlist,
// podcast). Un brano del catalogo ha due id: quello "dz:" delle righe del catalogo e quello del video YouTube
// abbinato, con cui compare nei recenti e nelle playlist. Il player pubblica quelli che conosce; una riga "dz:" di un
// brano partito dal video (dai recenti) si riconosce con l'abbinamento salvato dal catalogo.
public static class NowPlaying
{
	static HashSet<string> ids = [];

	// Solo dal thread UI (lo aggiorna il player, lo leggono le righe).
	public static event Action? Changed;

	// Id del catalogo → video già abbinato (CatalogService.KnownVideo).
	// ponytail: una lettura (memoria, poi SQLite) per riga a schermo a ogni cambio di brano; poche decine, si regge.
	public static Func<string, string?>? Matched { get; set; }

	public static bool Is(string? id) =>
		id is not null && (ids.Contains(id)
			|| id.StartsWith(Track.CatalogPrefix, StringComparison.Ordinal) && Matched?.Invoke(id) is { } video && ids.Contains(video));

	public static void Set(params string?[] playing)
	{
		ids = [.. playing.OfType<string>()];
		Changed?.Invoke();
	}
}
