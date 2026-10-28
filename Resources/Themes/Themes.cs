namespace Canora.Theming;

public partial class ClassicTheme : ResourceDictionary
{
	public ClassicTheme() => InitializeComponent();
}

public partial class CobaltSkyTheme : ResourceDictionary
{
	public CobaltSkyTheme() => InitializeComponent();
}

public partial class LagunaTheme : ResourceDictionary
{
	public LagunaTheme() => InitializeComponent();
}

public partial class WarmAutumnGlowTheme : ResourceDictionary
{
	public WarmAutumnGlowTheme() => InitializeComponent();
}

public partial class RusticCharmTheme : ResourceDictionary
{
	public RusticCharmTheme() => InitializeComponent();
}

// Un tema = un ResourceDictionary con le stesse chiavi, unito alle risorse dell'app al posto del precedente.
// Le viste usano DynamicResource e si ricolorano da sole; i colori calcolati in C# (gradiente del player,
// tile della ricerca) si aggiornano con Changed.
public sealed class Theme(string key, string name, ResourceDictionary resources)
{
	public string Key => key;
	public string Name => name;
	public ResourceDictionary Resources => resources;

	// Miniatura della Home nella scelta del tema
	public Brush Preview => (Brush)resources["HomeBackground"];
	public Brush Fade => (Brush)resources["BottomFade"];
	public Brush Cover => (Brush)resources["PlaylistCover"];
	public Color Accent => (Color)resources["Accent"];
	public Color Text => (Color)resources["TextPrimary"];
	public Color Muted => (Color)resources["TextSecondary"];
	public Color Nav => (Color)resources["NavInactive"];
	public Color Player => (Color)resources["PlayerAccent"];
}

public static class Themes
{
	public static IReadOnlyList<Theme> All { get; } =
	[
		new("Classic", "Classico", new ClassicTheme()),
		new("CobaltSky", "Cobalt sky", new CobaltSkyTheme()),
		new("Laguna", "Laguna", new LagunaTheme()),
		new("WarmAutumnGlow", "Warm Autumn Glow", new WarmAutumnGlowTheme()),
		new("RusticCharm", "Rustic Charm", new RusticCharmTheme()),
	];

	public static Theme Current { get; private set; } = All[0];

	public static event Action? Changed;

	// Da App, prima di creare la pagina radice: il tema salvato, o il primo.
	public static void ApplySaved() =>
		Apply(All.FirstOrDefault(t => t.Key == Preferences.Default.Get(nameof(Theme), "")) ?? All[0]);

	public static void Apply(Theme theme)
	{
		var merged = Application.Current!.Resources.MergedDictionaries;
		merged.Remove(Current.Resources);
		merged.Add(theme.Resources);
		Current = theme;
		Preferences.Default.Set(nameof(Theme), theme.Key);
		Changed?.Invoke();
	}

	// Per il codice: il colore del tema attivo.
	public static Color Color(string key) => (Color)Application.Current!.Resources[key];
}
