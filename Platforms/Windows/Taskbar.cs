using System.Runtime.InteropServices;
using Canora.ViewModels;

namespace Canora;

// Precedente, play/pausa e successivo nell'anteprima dell'icona sulla taskbar, come Spotify. I pulsanti sono di
// ITaskbarList3 (ThumbBarAddButtons); i clic arrivano alla finestra come WM_COMMAND/THBN_CLICKED, presi con una
// subclass della finestra. Le icone sono .ico bianchi in Taskbar\ accanto all'exe.
// La copertina al posto della miniatura della finestra (DwmSetIconicThumbnail) qui non si può: su Windows 11 build
// 26340 DWM rifiuta ogni bitmap con E_INVALIDARG, anche da una finestra Windows Forms, e l'anteprima resta uno spinner.
static class Taskbar
{
	const uint Previous = 0, PlayPause = 1, Next = 2;
	const uint WM_COMMAND = 0x0111, THBN_CLICKED = 0x1800;

	static readonly uint TaskbarButtonCreated = RegisterWindowMessage("TaskbarButtonCreated");
	static readonly SubclassProc Proc = WndProc; // in un campo: Windows lo chiama per tutta la vita della finestra
	static ITaskbarList3? taskbar;
	static PlayerViewModel? player;
	static IntPtr window, previousIcon, playIcon, pauseIcon, nextIcon;
	static bool added;

	public static void Attach(IntPtr hwnd, PlayerViewModel viewModel)
	{
		(window, player) = (hwnd, viewModel);
		var size = GetSystemMetricsForDpi(49 /* SM_CXSMICON */, GetDpiForWindow(hwnd));
		IntPtr Icon(string name) => LoadImage(IntPtr.Zero, Path.Combine(AppContext.BaseDirectory, "Taskbar", name + ".ico"), 1 /* IMAGE_ICON */, size, size, 0x10 /* LR_LOADFROMFILE */);
		(previousIcon, playIcon, pauseIcon, nextIcon) = (Icon("previous"), Icon("play"), Icon("pause"), Icon("next"));
		SetWindowSubclass(hwnd, Proc, 1, 0);
		player.PropertyChanged += (_, e) =>
		{
			if (e.PropertyName is nameof(PlayerViewModel.IsPlaying) or nameof(PlayerViewModel.HasTrack))
				Show();
		};
		Show(); // il pulsante della taskbar di solito esiste già: TaskbarButtonCreated arriva solo se Explorer lo ricrea
	}

	// Aggiunti una volta per pulsante della taskbar, poi solo aggiornati (play ↔ pausa, attivi solo con un brano).
	static void Show()
	{
		if (player is null)
			return;
		try
		{
			if (taskbar is null)
			{
				taskbar = (ITaskbarList3)new TaskbarList();
				taskbar.HrInit();
			}
			var flags = player.HasTrack ? 0u : 1u; // THBF_ENABLED / THBF_DISABLED
			THUMBBUTTON[] buttons =
			[
				Button(Previous, previousIcon, "Precedente", flags),
				player.IsPlaying ? Button(PlayPause, pauseIcon, "Pausa", flags) : Button(PlayPause, playIcon, "Riproduci", flags),
				Button(Next, nextIcon, "Successivo", flags),
			];
			if (added)
				taskbar.ThumbBarUpdateButtons(window, (uint)buttons.Length, buttons);
			else
				added = taskbar.ThumbBarAddButtons(window, (uint)buttons.Length, buttons) == 0;
		}
		catch (Exception ex) when (ex is COMException or InvalidCastException)
		{
			System.Diagnostics.Debug.WriteLine(ex); // taskbar non pronta: si riprova al prossimo cambio o a TaskbarButtonCreated
		}
	}

	static THUMBBUTTON Button(uint id, IntPtr icon, string tip, uint flags) =>
		new() { dwMask = 0x2 | 0x4 | 0x8 /* THB_ICON | THB_TOOLTIP | THB_FLAGS */, iId = id, hIcon = icon, szTip = tip, dwFlags = flags };

	static IntPtr WndProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam, UIntPtr id, UIntPtr data)
	{
		if (msg == WM_COMMAND && (wParam.ToInt64() >> 16 & 0xFFFF) == THBN_CLICKED && player is not null)
		{
			switch ((uint)(wParam.ToInt64() & 0xFFFF))
			{
				case Previous: player.PreviousCommand.Execute(null); break;
				case PlayPause: player.TogglePlayCommand.Execute(null); break;
				case Next: player.NextCommand.Execute(null); break;
			}
			return IntPtr.Zero;
		}
		if (msg == TaskbarButtonCreated)
		{
			added = false; // Explorer riavviato: pulsante nuovo, i pulsanti vanno aggiunti di nuovo
			Show();
		}
		return DefSubclassProc(hwnd, msg, wParam, lParam);
	}

	delegate IntPtr SubclassProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam, UIntPtr id, UIntPtr data);

	[DllImport("comctl32.dll")] static extern bool SetWindowSubclass(IntPtr hwnd, SubclassProc proc, nuint id, nuint data);
	[DllImport("comctl32.dll")] static extern IntPtr DefSubclassProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);
	[DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern uint RegisterWindowMessage(string name);
	[DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern IntPtr LoadImage(IntPtr instance, string name, uint type, int cx, int cy, uint load);
	[DllImport("user32.dll")] static extern int GetSystemMetricsForDpi(int index, uint dpi);
	[DllImport("user32.dll")] static extern uint GetDpiForWindow(IntPtr hwnd);

	[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
	struct THUMBBUTTON
	{
		public uint dwMask;
		public uint iId;
		public uint iBitmap;
		public IntPtr hIcon;
		[MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string szTip;
		public uint dwFlags;
	}

	[ComImport, Guid("56FDF344-FD6D-11d0-958A-006097C9A090"), ClassInterface(ClassInterfaceType.None)]
	class TaskbarList;

	// L'ordine dei metodi è quello della vtable (ITaskbarList, ITaskbarList2, ITaskbarList3): non va cambiato.
	[ComImport, Guid("ea1afb91-9e28-4b86-90e9-9e9f8a5eefaf"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
	interface ITaskbarList3
	{
		void HrInit();
		void AddTab(IntPtr hwnd);
		void DeleteTab(IntPtr hwnd);
		void ActivateTab(IntPtr hwnd);
		void SetActiveAlt(IntPtr hwnd);
		void MarkFullscreenWindow(IntPtr hwnd, [MarshalAs(UnmanagedType.Bool)] bool fullscreen);
		void SetProgressValue(IntPtr hwnd, ulong completed, ulong total);
		void SetProgressState(IntPtr hwnd, int flags);
		void RegisterTab(IntPtr tab, IntPtr hwnd);
		void UnregisterTab(IntPtr tab);
		void SetTabOrder(IntPtr tab, IntPtr insertBefore);
		void SetTabActive(IntPtr tab, IntPtr hwnd, uint reserved);
		[PreserveSig] int ThumbBarAddButtons(IntPtr hwnd, uint count, [MarshalAs(UnmanagedType.LPArray)] THUMBBUTTON[] buttons);
		[PreserveSig] int ThumbBarUpdateButtons(IntPtr hwnd, uint count, [MarshalAs(UnmanagedType.LPArray)] THUMBBUTTON[] buttons);
	}
}
