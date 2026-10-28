using Android.App;
using Android.Content.PM;
using Android.Views;
using Android.Views.InputMethods;
using Android.Widget;

namespace Canora;

// SingleTask + ResizeableActivity: richiesti da MediaElement per la notifica/servizio in background
// (un tap sulla notifica riporta all'istanza esistente invece di crearne una seconda).
[Activity(Theme = "@style/Maui.SplashTheme", MainLauncher = true, ResizeableActivity = true, LaunchMode = LaunchMode.SingleTask, ConfigurationChanges = ConfigChanges.ScreenSize | ConfigChanges.Orientation | ConfigChanges.UiMode | ConfigChanges.ScreenLayout | ConfigChanges.SmallestScreenSize | ConfigChanges.Density)]
public class MainActivity : MauiAppCompatActivity
{
	protected override void OnResume()
	{
		base.OnResume();
		Services.Assistant.Dismiss();
	}

	// Un tocco fuori dal campo in modifica (un risultato della ricerca, un tab…) chiude la tastiera e toglie il focus.
	// ContentPage.HideSoftInputOnTapped qui non scatta: la pagina radice non passa dalla navigazione di MAUI.
	public override bool DispatchTouchEvent(MotionEvent? e)
	{
		if (e?.Action == MotionEventActions.Down && CurrentFocus is EditText field)
		{
			var bounds = new Android.Graphics.Rect();
			field.GetGlobalVisibleRect(bounds);
			if (!bounds.Contains((int)e.RawX, (int)e.RawY))
				HideKeyboard(field);
		}
		return base.DispatchTouchEvent(e);
	}

	// Anche da MauiProgram, quando si conferma un campo dalla tastiera.
	public static void HideKeyboard(EditText field)
	{
		((InputMethodManager?)field.Context?.GetSystemService(InputMethodService))?.HideSoftInputFromWindow(field.WindowToken, HideSoftInputFlags.None);
		field.ClearFocus();
	}
}
