using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Canora.Services;

// Nuove versioni su GitHub: all'avvio si legge l'ultima release (API pubblica di GitHub, senza chiave; bozze e
// pre-release non contano) e, se è più recente di questa, la Home invita a scaricarla. Si confronta solo
// major.minor.patch: su Windows AppInfo aggiunge l'ApplicationVersion come quarto numero.
static class UpdateCheck
{
	// La pagina della release più recente, con le novità e i file per Android e Windows. Fissa e non quella della
	// risposta: l'app apre solo indirizzi suoi.
	public const string ReleasesUrl = "https://github.com/amellini/Canora/releases/latest";
	const string LatestApi = "https://api.github.com/repos/amellini/Canora/releases/latest";
	const string SkippedKey = "SkippedRelease";

	static readonly HttpClient Http = CreateClient();

	static HttpClient CreateClient()
	{
		var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
		http.DefaultRequestHeaders.UserAgent.ParseAdd("Canora/1.0"); // l'API di GitHub rifiuta le richieste senza
		return http;
	}

	// La release da proporre, o null: nessuna più recente, saltata, o GitHub irraggiungibile (si riprova al prossimo
	// avvio: non vale un errore).
	public static async Task<GitHubRelease?> NewerAsync()
	{
		try
		{
			var latest = await Http.GetFromJsonAsync(LatestApi, GitHubJson.Default.GitHubRelease);
			return latest is not null && Parse(latest.TagName) > Parse(AppInfo.Current.VersionString)
				&& Preferences.Default.Get(SkippedKey, "") != latest.TagName ? latest : null;
		}
		catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
		{
			return null;
		}
	}

	// "Salta questa versione": se ne riparla alla prossima.
	public static void Skip(GitHubRelease release) => Preferences.Default.Set(SkippedKey, release.TagName);

	// "v1.2.3" o "1.2.3.10203" → 1.2.3. Un tag che non è una versione vale 0.0: mai più recente.
	static Version Parse(string text) =>
		Version.TryParse(text.TrimStart('v'), out var v) ? new Version(v.Major, v.Minor, Math.Max(v.Build, 0)) : new Version();
}

// Risposta di /releases/latest (solo il campo usato).
sealed class GitHubRelease
{
	public string TagName { get; set; } = "";
}

// Serializzazione generata a compile time come per Deezer e LRCLIB: regge il trimming di Release.
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower)]
[JsonSerializable(typeof(GitHubRelease))]
sealed partial class GitHubJson : JsonSerializerContext;
