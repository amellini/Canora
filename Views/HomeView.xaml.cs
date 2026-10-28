namespace Canora.Views;

public partial class HomeView : ContentView
{
	// La pagina radice passa al tab Cerca (cronologia ricerche, stato vuoto).
	public event EventHandler? SearchRequested;

	public HomeView() => InitializeComponent();

	void OnSearchTapped(object? sender, TappedEventArgs e) => SearchRequested?.Invoke(this, EventArgs.Empty);
}
