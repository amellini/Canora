using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Canora.Models;

namespace Canora.Services;

// Chi ascolta, dalla presentazione (Views/OnboardingPage): il nome per il saluto della Home, i generi e gli artisti
// preferiti, base per le proposte anche prima che ci siano ascolti. In Preferences, come le ricerche recenti.
public static class Profile
{
	public static string Name => Preferences.Default.Get("ProfileName", "");

	public static List<string> Genres => Load("ProfileGenres", AppJson.Default.ListString);

	public static List<CatalogArtist> Artists => Load("ProfileArtists", AppJson.Default.ListCatalogArtist);

	// Il genere in cui è stato scelto ogni artista (id → genere), per le compilation di "Per te". Chiave a parte: le scelte
	// salvate prima non l'hanno, e i loro artisti restano senza genere.
	public static Dictionary<long, string> ArtistGenres
	{
		get
		{
			try
			{
				var json = Preferences.Default.Get("ProfileArtistGenres", "");
				return json.Length == 0 ? [] : JsonSerializer.Deserialize(json, AppJson.Default.DictionaryInt64String) ?? [];
			}
			catch (JsonException)
			{
				return [];
			}
		}
	}

	// Vero anche se saltata: la presentazione si mostra solo al primo avvio, poi si rifà dalle Impostazioni.
	public static bool IsOnboarded => Preferences.Default.Get("Onboarded", false);

	public static void Save(string name, List<string> genres, List<CatalogArtist> artists, Dictionary<long, string> artistGenres)
	{
		Preferences.Default.Set("ProfileName", name.Trim());
		Preferences.Default.Set("ProfileGenres", JsonSerializer.Serialize(genres, AppJson.Default.ListString));
		Preferences.Default.Set("ProfileArtists", JsonSerializer.Serialize(artists, AppJson.Default.ListCatalogArtist));
		Preferences.Default.Set("ProfileArtistGenres", JsonSerializer.Serialize(artistGenres, AppJson.Default.DictionaryInt64String));
		Preferences.Default.Set("Onboarded", true);
	}

	static List<T> Load<T>(string key, JsonTypeInfo<List<T>> type)
	{
		try
		{
			var json = Preferences.Default.Get(key, "");
			return json.Length == 0 ? [] : JsonSerializer.Deserialize(json, type) ?? [];
		}
		catch (JsonException)
		{
			return [];
		}
	}
}
