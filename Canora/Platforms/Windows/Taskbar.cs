using System.Runtime.InteropServices;
using Canora.Services;
using Canora.ViewModels;
using Windows.Graphics.Imaging;

namespace Canora;

// L'anteprima dell'icona sulla taskbar, come Spotify: la copertina del brano al posto della miniatura della finestra,
// con precedente, play/pausa e successivo sotto.
// I pulsanti sono di ITaskbarList3 (ThumbBarAddButtons); i clic arrivano alla finestra come WM_COMMAND/THBN_CLICKED,
// presi con una subclass della finestra. Le icone sono .ico bianchi in Taskbar\ accanto all'exe.
// La copertina è una miniatura "iconica" di DWM: con DWMWA_FORCE_ICONIC_REPRESENTATION DWM non usa la finestra, e
// all'hover manda WM_DWMSENDICONICTHUMBNAIL con la misura massima; la bitmap la accetta solo in risposta a quel
// messaggio (prima dà E_INVALIDARG). Sulla build 26340 Insider di Windows 11 la rifiutava sempre, dalla 26340.9882 va.
static class Taskbar
{
	const uint Previous = 0, PlayPause = 1, Next = 2;
	const uint WM_COMMAND = 0x0111, THBN_CLICKED = 0x1800;
	const uint WM_DWMSENDICONICTHUMBNAIL = 0x0323, WM_DWMSENDICONICLIVEPREVIEWBITMAP = 0x0326;
	const int DWMWA_FORCE_ICONIC_REPRESENTATION = 7, DWMWA_EXTENDED_FRAME_BOUNDS = 9, DWMWA_HAS_ICONIC_BITMAP = 10;
	const uint CoverMaxSide = 512; // DWM chiede circa 80-200 px: margine per i monitor ad alta densità

	static readonly uint TaskbarButtonCreated = RegisterWindowMessage("TaskbarButtonCreated");
	static readonly SubclassProc Proc = WndProc; // in un campo: Windows lo chiama per tutta la vita della finestra
	static ITaskbarList3? taskbar;
	static PlayerViewModel? player;
	static IntPtr window, previousIcon, playIcon, pauseIcon, nextIcon;
	static bool added;
	// Copertina del brano in BGRA premoltiplicato, quadrata (coverSide×coverSide); null = anteprima della finestra.
	static byte[]? cover;
	static int coverSide;
	static string? coverUrl;
	static CancellationTokenSource? coverLoad;

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
			if (e.PropertyName is nameof(PlayerViewModel.Current))
				_ = LoadCoverAsync(player.Current?.ThumbUrl);
		};
		Show(); // il pulsante della taskbar di solito esiste già: TaskbarButtonCreated arriva solo se Explorer lo ricrea
		_ = LoadCoverAsync(player.Current?.ThumbUrl); // il brano ripreso all'avvio può essere già lì
	}

	// Cambio brano: la copertina nuova sostituisce la vecchia quando è pronta (niente lampo dell'anteprima della
	// finestra nel mezzo); senza brano, o se non si scarica, torna l'anteprima della finestra.
	static async Task LoadCoverAsync(string? url)
	{
		if (url == coverUrl)
			return;
		coverUrl = url;
		coverLoad?.Cancel();
		var load = coverLoad = new CancellationTokenSource();
		if (url is null)
		{
			SetCover(null, 0);
			return;
		}
		try
		{
			var bytes = await ArtworkColor.BytesAsync(url, load.Token);
			var (pixels, side) = await Task.Run(() => DecodeAsync(bytes), load.Token);
			if (!load.IsCancellationRequested)
				SetCover(pixels, side);
		}
		catch (Exception ex) when (ex is not OutOfMemoryException)
		{
			if (!load.IsCancellationRequested)
				SetCover(null, 0);
		}
	}

	// Il quadrato centrale della copertina (come l'AspectFill del mini-player), al massimo CoverMaxSide di lato.
	static async Task<(byte[] Pixels, int Side)> DecodeAsync(byte[] bytes)
	{
		using var stream = new MemoryStream(bytes).AsRandomAccessStream();
		var decoder = await BitmapDecoder.CreateAsync(stream);
		var square = Math.Min(decoder.PixelWidth, decoder.PixelHeight);
		var side = Math.Min(square, CoverMaxSide);
		// WIC prima scala e poi ritaglia (Bounds è nelle coordinate scalate).
		var scale = side / (double)square;
		var scaledWidth = Math.Max(side, (uint)Math.Round(decoder.PixelWidth * scale));
		var scaledHeight = Math.Max(side, (uint)Math.Round(decoder.PixelHeight * scale));
		var transform = new BitmapTransform
		{
			ScaledWidth = scaledWidth,
			ScaledHeight = scaledHeight,
			Bounds = new BitmapBounds { X = (scaledWidth - side) / 2, Y = (scaledHeight - side) / 2, Width = side, Height = side },
			InterpolationMode = BitmapInterpolationMode.Fant,
		};
		var data = await decoder.GetPixelDataAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied, transform,
			ExifOrientationMode.IgnoreExifOrientation, ColorManagementMode.DoNotColorManage);
		return (data.DetachPixelData(), (int)side);
	}

	static void SetCover(byte[]? pixels, int side)
	{
		(cover, coverSide) = (pixels, side);
		var on = pixels is null ? 0 : 1;
		DwmSetWindowAttribute(window, DWMWA_FORCE_ICONIC_REPRESENTATION, ref on, sizeof(int));
		DwmSetWindowAttribute(window, DWMWA_HAS_ICONIC_BITMAP, ref on, sizeof(int));
		if (pixels is not null)
			DwmInvalidateIconicBitmaps(window); // al prossimo hover DWM chiede la copertina nuova
	}

	// Risposta a WM_DWMSENDICONICTHUMBNAIL: la copertina ridotta al lato più corto che DWM concede (mai ingrandita).
	static void SendCover(IntPtr hwnd, int maxWidth, int maxHeight)
	{
		if (cover is null)
			return;
		var side = Math.Min(coverSide, Math.Min(maxWidth, maxHeight));
		var pixels = Shrink(cover, coverSide, side);
		var bitmap = CreateBitmap(side, side, out var bits);
		if (bitmap == IntPtr.Zero)
			return;
		Marshal.Copy(pixels, 0, bits, pixels.Length);
		var result = DwmSetIconicThumbnail(hwnd, bitmap, 0);
		if (result != 0)
			System.Diagnostics.Debug.WriteLine($"DwmSetIconicThumbnail {side}x{side}: 0x{result:X8}");
		DeleteObject(bitmap);
	}

	// Risposta a WM_DWMSENDICONICLIVEPREVIEWBITMAP. Con la copertina forzata, anche la peek (il mouse sulla miniatura,
	// che mostra la finestra in grande) DWM la chiede all'app, e senza risposta mostra uno spinner su fondo bianco.
	// Le si dà la finestra com'è: PrintWindow con PW_RENDERFULLCONTENT cattura anche i contenuti WinUI.
	static void SendPeek(IntPtr hwnd)
	{
		IntPtr bitmap;
		// Ridotta a icona non c'è niente da catturare: un pixel trasparente, e la peek non mostra nulla, come per le
		// altre finestre ridotte. Senza risposta DWM mostrerebbe lo spinner, e DWMWA_DISALLOW_PEEK non lo evita.
		if (IsIconic(hwnd))
		{
			bitmap = CreateBitmap(1, 1, out _); // DIB appena creata: pixel a zero, trasparente
			var none = new POINT();
			DwmSetIconicLivePreviewBitmap(hwnd, bitmap, ref none, 0);
			DeleteObject(bitmap);
			return;
		}
		if (!GetClientRect(hwnd, out var client)
			|| DwmGetWindowAttribute(hwnd, DWMWA_EXTENDED_FRAME_BOUNDS, out var frame, Marshal.SizeOf<RECT>()) != 0)
			return;
		int width = client.Right, height = client.Bottom;
		bitmap = CreateBitmap(width, height, out var bits);
		if (bitmap == IntPtr.Zero)
			return;
		var dc = CreateCompatibleDC(IntPtr.Zero);
		var previous = SelectObject(dc, bitmap);
		var printed = PrintWindow(hwnd, dc, 1 | 2 /* PW_CLIENTONLY | PW_RENDERFULLCONTENT */);
		SelectObject(dc, previous);
		DeleteDC(dc);
		if (printed)
		{
			// PrintWindow lascia a zero il canale alfa, e per DWM la finestra sarebbe trasparente: opaca.
			var pixels = new int[width * height];
			Marshal.Copy(bits, pixels, 0, pixels.Length);
			for (var i = 0; i < pixels.Length; i++)
				pixels[i] |= unchecked((int)0xFF000000);
			Marshal.Copy(pixels, 0, bits, pixels.Length);
			// Dove sta l'area client rispetto all'angolo visibile della finestra: lì DWM disegna la bitmap. Non
			// rispetto a GetWindowRect, che conta anche i bordi invisibili (-8,-8 da massimizzata).
			var origin = new POINT();
			ClientToScreen(hwnd, ref origin);
			var offset = new POINT { X = origin.X - frame.Left, Y = origin.Y - frame.Top };
			var result = DwmSetIconicLivePreviewBitmap(hwnd, bitmap, ref offset, 0);
			if (result != 0)
				System.Diagnostics.Debug.WriteLine($"DwmSetIconicLivePreviewBitmap {width}x{height}: 0x{result:X8}");
		}
		DeleteObject(bitmap);
	}

	// DIB a 32 bit dall'alto in basso; i pixel si scrivono in `bits`. Va liberata con DeleteObject.
	static IntPtr CreateBitmap(int width, int height, out IntPtr bits)
	{
		var header = new BITMAPINFOHEADER { biSize = Marshal.SizeOf<BITMAPINFOHEADER>(), biWidth = width, biHeight = -height, biPlanes = 1, biBitCount = 32 };
		return CreateDIBSection(IntPtr.Zero, ref header, 0 /* DIB_RGB_COLORS */, out bits, IntPtr.Zero, 0);
	}

	// Riduzione per media: ogni pixel d'arrivo è la media dei pixel di partenza del suo riquadro.
	static byte[] Shrink(byte[] source, int from, int to)
	{
		if (from == to)
			return source;
		var result = new byte[to * to * 4];
		var scale = from / (double)to;
		for (var y = 0; y < to; y++)
		{
			int top = (int)(y * scale), bottom = Math.Max(top + 1, (int)((y + 1) * scale));
			for (var x = 0; x < to; x++)
			{
				int left = (int)(x * scale), right = Math.Max(left + 1, (int)((x + 1) * scale));
				int b = 0, g = 0, r = 0, a = 0;
				for (var sy = top; sy < bottom; sy++)
					for (var sx = left; sx < right; sx++)
					{
						var i = (sy * from + sx) * 4;
						(b, g, r, a) = (b + source[i], g + source[i + 1], r + source[i + 2], a + source[i + 3]);
					}
				var count = (bottom - top) * (right - left);
				var o = (y * to + x) * 4;
				(result[o], result[o + 1], result[o + 2], result[o + 3]) = ((byte)(b / count), (byte)(g / count), (byte)(r / count), (byte)(a / count));
			}
		}
		return result;
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
		if (msg == WM_DWMSENDICONICTHUMBNAIL)
		{
			SendCover(hwnd, (int)(lParam.ToInt64() >> 16 & 0xFFFF), (int)(lParam.ToInt64() & 0xFFFF));
			return IntPtr.Zero;
		}
		if (msg == WM_DWMSENDICONICLIVEPREVIEWBITMAP)
		{
			SendPeek(hwnd);
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

	[DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
	[DllImport("dwmapi.dll")] static extern int DwmGetWindowAttribute(IntPtr hwnd, int attribute, out RECT value, int size);
	[DllImport("dwmapi.dll")] static extern int DwmSetIconicThumbnail(IntPtr hwnd, IntPtr bitmap, uint flags);
	[DllImport("dwmapi.dll")] static extern int DwmSetIconicLivePreviewBitmap(IntPtr hwnd, IntPtr bitmap, ref POINT client, uint flags);
	[DllImport("dwmapi.dll")] static extern int DwmInvalidateIconicBitmaps(IntPtr hwnd);
	[DllImport("gdi32.dll")] static extern IntPtr CreateDIBSection(IntPtr hdc, ref BITMAPINFOHEADER info, uint usage, out IntPtr bits, IntPtr section, uint offset);
	[DllImport("gdi32.dll")] static extern IntPtr CreateCompatibleDC(IntPtr hdc);
	[DllImport("gdi32.dll")] static extern IntPtr SelectObject(IntPtr hdc, IntPtr handle);
	[DllImport("gdi32.dll")] static extern bool DeleteDC(IntPtr hdc);
	[DllImport("gdi32.dll")] static extern bool DeleteObject(IntPtr handle);
	[DllImport("user32.dll")] static extern bool PrintWindow(IntPtr hwnd, IntPtr hdc, uint flags);
	[DllImport("user32.dll")] static extern bool IsIconic(IntPtr hwnd);
	[DllImport("user32.dll")] static extern bool GetClientRect(IntPtr hwnd, out RECT rect);
	[DllImport("user32.dll")] static extern bool ClientToScreen(IntPtr hwnd, ref POINT point);

	[StructLayout(LayoutKind.Sequential)]
	struct POINT
	{
		public int X, Y;
	}

	[StructLayout(LayoutKind.Sequential)]
	struct RECT
	{
		public int Left, Top, Right, Bottom;
	}

	[StructLayout(LayoutKind.Sequential)]
	struct BITMAPINFOHEADER
	{
		public int biSize;
		public int biWidth;
		public int biHeight;
		public short biPlanes;
		public short biBitCount;
		public int biCompression;
		public int biSizeImage;
		public int biXPelsPerMeter;
		public int biYPelsPerMeter;
		public int biClrUsed;
		public int biClrImportant;
	}

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
