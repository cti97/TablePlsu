using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;

namespace TablePlsu.App.Infrastructure;

/// <summary>
/// A notification-area icon via <c>Shell_NotifyIcon</c> (no WinForms: <c>UseWindowsForms</c> would pull a second UI
/// framework into the process and its global usings into every file). Left click/double click raises
/// <see cref="Activated"/>; right click opens <see cref="Menu"/> (a themed WPF <see cref="ContextMenu"/>) at the cursor.
/// The icon is re-added when Explorer restarts (<c>TaskbarCreated</c>).
/// </summary>
public sealed class TrayIcon : IDisposable
{
    private const int WM_APP = 0x8000;
    private const int CallbackMessage = WM_APP + 1;
    private const int WM_LBUTTONUP = 0x0202;
    private const int WM_LBUTTONDBLCLK = 0x0203;
    private const int WM_RBUTTONUP = 0x0205;
    private const int WM_CONTEXTMENU = 0x007B;
    private const int NIM_ADD = 0, NIM_MODIFY = 1, NIM_DELETE = 2;
    private const int NIF_MESSAGE = 1, NIF_ICON = 2, NIF_TIP = 4;

    private readonly HwndSource _window;
    private readonly int _taskbarCreated;
    private readonly IntPtr _icon;
    private readonly bool _ownsIcon;
    private string _tooltip;
    private bool _added;
    private bool _disposed;

    public TrayIcon(string tooltip)
    {
        _tooltip = tooltip;
        // A hidden zero-size popup (not a message-only window: those cannot become foreground, which the menu needs
        // so it closes when the user clicks elsewhere).
        _window = new HwndSource(new HwndSourceParameters("TablePlsuTray")
        {
            Width = 0,
            Height = 0,
            WindowStyle = unchecked((int)0x80000000), // WS_POPUP, not visible
            ExtendedWindowStyle = 0x00000080, // WS_EX_TOOLWINDOW: never in the taskbar / Alt+Tab
        });
        _window.AddHook(WndProc);
        _taskbarCreated = RegisterWindowMessage("TaskbarCreated");
        (_icon, _ownsIcon) = LoadAppIcon();
        Add();
    }

    /// <summary>Left click / double click on the icon.</summary>
    public event EventHandler? Activated;

    /// <summary>The right-click menu (built by the owner).</summary>
    public ContextMenu? Menu { get; set; }

    public string Tooltip
    {
        get => _tooltip;
        set
        {
            _tooltip = value;
            if (_added) Notify(NIM_MODIFY);
        }
    }

    private void Add() => _added = Notify(NIM_ADD);

    private bool Notify(int message)
    {
        var data = new NOTIFYICONDATAW
        {
            cbSize = Marshal.SizeOf<NOTIFYICONDATAW>(),
            hWnd = _window.Handle,
            uID = 1,
            uFlags = NIF_MESSAGE | NIF_ICON | NIF_TIP,
            uCallbackMessage = CallbackMessage,
            hIcon = _icon,
            szTip = _tooltip.Length > 127 ? _tooltip[..127] : _tooltip,
        };
        return Shell_NotifyIcon(message, ref data);
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == CallbackMessage)
        {
            switch ((int)lParam & 0xFFFF)
            {
                case WM_LBUTTONUP:
                case WM_LBUTTONDBLCLK:
                    Activated?.Invoke(this, EventArgs.Empty);
                    break;
                case WM_RBUTTONUP:
                case WM_CONTEXTMENU:
                    ShowMenu();
                    break;
            }
            handled = true;
        }
        else if (msg == _taskbarCreated && _taskbarCreated != 0)
        {
            Add(); // Explorer restarted: the old icon is gone.
        }
        return IntPtr.Zero;
    }

    private void ShowMenu()
    {
        if (Menu is not { } menu) return;
        SetForegroundWindow(_window.Handle);
        menu.Placement = PlacementMode.MousePoint;
        menu.StaysOpen = false;
        menu.IsOpen = true;
    }

    private static (IntPtr Icon, bool Owned) LoadAppIcon()
    {
        try
        {
            if (Environment.ProcessPath is { } exe && ExtractIconEx(exe, 0, IntPtr.Zero, out var small, 1) > 0 && small != IntPtr.Zero)
                return (small, true);
        }
        catch (Exception)
        {
        }
        return (LoadIcon(IntPtr.Zero, new IntPtr(32512)), false); // IDI_APPLICATION
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_added) Notify(NIM_DELETE);
        _added = false;
        if (Menu is not null) Menu.IsOpen = false;
        _window.RemoveHook(WndProc);
        _window.Dispose();
        if (_ownsIcon) DestroyIcon(_icon);
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NOTIFYICONDATAW
    {
        public int cbSize;
        public IntPtr hWnd;
        public int uID;
        public int uFlags;
        public int uCallbackMessage;
        public IntPtr hIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string szTip;
        public int dwState;
        public int dwStateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string szInfo;
        public int uTimeoutOrVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string szInfoTitle;
        public int dwInfoFlags;
        public Guid guidItem;
        public IntPtr hBalloonIcon;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, EntryPoint = "Shell_NotifyIconW")]
    private static extern bool Shell_NotifyIcon(int message, ref NOTIFYICONDATAW data);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, EntryPoint = "ExtractIconExW")]
    private static extern int ExtractIconEx(string file, int index, IntPtr large, out IntPtr small, int count);

    [DllImport("user32.dll", EntryPoint = "LoadIconW")]
    private static extern IntPtr LoadIcon(IntPtr instance, IntPtr name);

    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr icon);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "RegisterWindowMessageW")]
    private static extern int RegisterWindowMessage(string name);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hwnd);
}
