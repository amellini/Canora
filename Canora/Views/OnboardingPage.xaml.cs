using Canora.Services;
using Canora.ViewModels;

namespace Canora.Views;

// Presentazione (OnboardingViewModel): modale sopra la pagina radice al primo avvio, poi dalle Impostazioni.
public partial class OnboardingPage : ContentPage
{
	readonly OnboardingViewModel model;

	OnboardingPage(OnboardingViewModel model)
	{
		InitializeComponent();
		BindingContext = this.model = model;
		// Maiuscola a ogni parola ("Anna Maria"), senza suggerimenti né correttore.
		NameEntry.Keyboard = Keyboard.Create(KeyboardFlags.CapitalizeWord);
		model.Finished += () => _ = Navigation.PopModalAsync();
	}

	// animated false al primo avvio: la presentazione c'è già quando compare l'app, senza la Home che scorre sotto.
	public static Task ShowAsync(INavigation navigation, bool animated = true) =>
		navigation.PushModalAsync(new OnboardingPage(new OnboardingViewModel(IPlatformApplication.Current!.Services.GetRequiredService<CatalogService>())), animated);

	protected override bool OnBackButtonPressed()
	{
		model.Back();
		return true;
	}
}
