using Canora.Theming;
using Canora.ViewModels;

namespace Canora.Views;

// Si apre modale sopra la pagina radice (che resta viva, MediaElement compreso).
public partial class SettingsPage : ContentPage
{
	public SettingsPage(LibraryViewModel library)
	{
		InitializeComponent();
		BindingContext = library;
		ThemeList.SelectedItem = Themes.Current;
		// Il tema in uso può stare oltre il bordo destro: lo si porta in vista.
		ThemeList.Loaded += (_, _) => ThemeList.ScrollTo(Themes.Current, position: ScrollToPosition.Center, animate: false);
	}

	// Il tema si applica al tocco: DynamicResource ricolora anche questa pagina e quella sotto.
	void OnThemeSelected(object? sender, SelectionChangedEventArgs e)
	{
		if (e.CurrentSelection.FirstOrDefault() is Theme theme && theme != Themes.Current)
			Themes.Apply(theme);
	}

	protected override bool OnBackButtonPressed()
	{
		_ = Navigation.PopModalAsync();
		return true;
	}

	void OnCloseTapped(object? sender, TappedEventArgs e) => _ = Navigation.PopModalAsync();

	void OnProfileTapped(object? sender, TappedEventArgs e) => _ = OnboardingPage.ShowAsync(Navigation);
}
