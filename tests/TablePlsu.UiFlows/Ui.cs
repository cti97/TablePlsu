using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows.Automation;

namespace TablePlsu.UiFlows;

/// <summary>
/// Drives the app as a user would: real keyboard input through SendInput (so the app's PreviewKeyDown routing runs),
/// mouse clicks at element centres, and UI Automation only for finding elements and reading what is on screen.
/// </summary>
public sealed class Ui
{
    public Process Proc { get; }
    public Ui(Process proc) { Proc = proc; }

    // ---------- finding ----------

    public static Condition Id(string id) => new PropertyCondition(AutomationElement.AutomationIdProperty, id);
    public static Condition Named(string name) => new PropertyCondition(AutomationElement.NameProperty, name);
    public static Condition Class(string cls) => new PropertyCondition(AutomationElement.ClassNameProperty, cls);
    public static Condition Type(ControlType t) => new PropertyCondition(AutomationElement.ControlTypeProperty, t);
    public static Condition And(params Condition[] c) => new AndCondition(c);

    /// <summary>Top-level windows of the app process (main windows, palette, dialogs, popups).</summary>
    /// <remarks>
    /// Enumerates HWNDs with Win32 instead of walking the UIA desktop: on a shared desktop one hung window of another
    /// process makes every RootElement query stall for seconds.
    /// </remarks>
    public List<AutomationElement> Windows()
    {
        var handles = new List<IntPtr>();
        EnumWindows((h, _) =>
        {
            GetWindowThreadProcessId(h, out var pid);
            if (pid == Proc.Id && IsWindowVisible(h)) handles.Add(h);
            return true;
        }, IntPtr.Zero);
        var all = new List<AutomationElement>();
        foreach (var h in handles)
            if (Safe(() => AutomationElement.FromHandle(h)) is { } e) all.Add(e);
        return all;
    }

    delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);
    [DllImport("user32.dll")] static extern bool EnumWindows(EnumWindowsProc cb, IntPtr lParam);
    [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr hWnd);

    public List<AutomationElement> MainWindows() =>
        Windows().Where(w => Safe(() => w.Current.AutomationId) == "MainWindow").ToList();

    public static T? Safe<T>(Func<T> f) { try { return f(); } catch { return default; } }

    public static T? WaitFor<T>(Func<T?> probe, int ms = 10000) where T : class
    {
        var sw = Stopwatch.StartNew();
        do
        {
            var r = Safe(probe);
            if (r != null) return r;
            Thread.Sleep(100);
        } while (sw.ElapsedMilliseconds < ms);
        return null;
    }

    public static bool WaitTrue(Func<bool> probe, int ms = 10000)
    {
        var sw = Stopwatch.StartNew();
        do
        {
            if (Safe(probe)) return true;
            Thread.Sleep(100);
        } while (sw.ElapsedMilliseconds < ms);
        return false;
    }

    public static AutomationElement? First(AutomationElement root, Condition c) => Safe(() => root.FindFirst(TreeScope.Descendants, c));

    public static List<AutomationElement> All(AutomationElement root, Condition c) =>
        Safe(() => root.FindAll(TreeScope.Descendants, c).Cast<AutomationElement>().ToList()) ?? [];

    /// <summary>Visible (on-screen) elements only.</summary>
    public static List<AutomationElement> Visible(AutomationElement root, Condition c) =>
        All(root, c).Where(e => Safe(() => !e.Current.IsOffscreen && !e.Current.BoundingRectangle.IsEmpty)).ToList();

    public static AutomationElement? FirstVisible(AutomationElement root, Condition c) => Visible(root, c).FirstOrDefault();

    public static string Name(AutomationElement? e) => e == null ? "" : Safe(() => e.Current.Name) ?? "";

    public static string Value(AutomationElement? e)
    {
        if (e == null) return "";
        if (e.TryGetCurrentPattern(ValuePattern.Pattern, out var p)) return ((ValuePattern)p).Current.Value ?? "";
        if (e.TryGetCurrentPattern(TextPattern.Pattern, out var t)) return ((TextPattern)t).DocumentRange.GetText(-1);
        return Name(e);
    }

    public static bool HasFocus(AutomationElement? e) => e != null && Safe(() => e.Current.HasKeyboardFocus);

    public static bool IsSelected(AutomationElement e) =>
        Safe(() => e.TryGetCurrentPattern(SelectionItemPattern.Pattern, out var p) && ((SelectionItemPattern)p).Current.IsSelected);

    public static AutomationElement? Parent(AutomationElement e) => Safe(() => TreeWalker.ControlViewWalker.GetParent(e));

    public static AutomationElement? AncestorOfType(AutomationElement e, ControlType t)
    {
        var p = Parent(e);
        while (p != null && Safe(() => p.Current.ControlType) != t) p = Parent(p);
        return p;
    }

    public static string Dump(AutomationElement root, int maxDepth = 30)
    {
        var sb = new System.Text.StringBuilder();
        void Walk(AutomationElement e, int depth)
        {
            if (depth > maxDepth) return;
            var c = Safe(() => e.Current);
            sb.Append(new string(' ', depth * 2)).Append(Safe(() => c.ControlType.ProgrammaticName.Replace("ControlType.", "")))
              .Append(" id=").Append(Safe(() => c.AutomationId)).Append(" cls=").Append(Safe(() => c.ClassName))
              .Append(" name=\"").Append((Safe(() => c.Name) ?? "").Replace("\n", "\\n")).Append('"')
              .Append(Safe(() => c.IsOffscreen) ? " OFF" : "").AppendLine();
            var child = Safe(() => TreeWalker.ControlViewWalker.GetFirstChild(e));
            while (child != null)
            {
                Walk(child, depth + 1);
                child = Safe(() => TreeWalker.ControlViewWalker.GetNextSibling(child));
            }
        }
        Walk(root, 0);
        return sb.ToString();
    }

    // ---------- focus ----------

    [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
    [DllImport("user32.dll")] static extern bool BringWindowToTop(IntPtr hWnd);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);

    public static IntPtr Hwnd(AutomationElement w) => new(Safe(() => w.Current.NativeWindowHandle));

    /// <summary>The window input is meant for (other processes on this desktop may steal the foreground).</summary>
    public static AutomationElement? Target { get; private set; }

    public static void Activate(AutomationElement window)
    {
        Target = window;
        var h = Hwnd(window);
        if (h == IntPtr.Zero) return;
        if (GetForegroundWindow() == h) return;
        // An Alt tap lets a background process take the foreground (SetForegroundWindow rules).
        KeyRaw(0x12, false); KeyRaw(0x12, true);
        BringWindowToTop(h);
        SetForegroundWindow(h);
        WaitTrue(() => GetForegroundWindow() == h, 2000);
    }

    public bool IsForegroundOurs()
    {
        GetWindowThreadProcessId(GetForegroundWindow(), out var pid);
        return pid == Proc.Id;
    }

    /// <summary>Before sending input: if another process took the foreground, bring our target window back.</summary>
    void EnsureForeground()
    {
        if (IsForegroundOurs() || Target == null) return;
        Console.WriteLine("  (foreground was stolen; re-activating)");
        var h = Hwnd(Target);
        KeyRaw(0x12, false); KeyRaw(0x12, true);
        BringWindowToTop(h);
        SetForegroundWindow(h);
        WaitTrue(IsForegroundOurs, 2000);
    }

    // ---------- keyboard ----------

    [StructLayout(LayoutKind.Sequential)]
    struct INPUT { public uint type; public InputUnion u; }
    [StructLayout(LayoutKind.Explicit)]
    struct InputUnion { [FieldOffset(0)] public MOUSEINPUT mi; [FieldOffset(0)] public KEYBDINPUT ki; }
    [StructLayout(LayoutKind.Sequential)]
    struct KEYBDINPUT { public ushort wVk; public ushort wScan; public uint dwFlags; public uint time; public IntPtr dwExtraInfo; }
    [StructLayout(LayoutKind.Sequential)]
    struct MOUSEINPUT { public int dx; public int dy; public uint mouseData; public uint dwFlags; public uint time; public IntPtr dwExtraInfo; }
    [DllImport("user32.dll", SetLastError = true)] static extern uint SendInput(uint n, INPUT[] inputs, int size);
    [DllImport("user32.dll")] static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern short VkKeyScanW(char ch);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern uint MapVirtualKeyW(uint code, uint mapType);

    const uint KEYUP = 0x2, UNICODE = 0x4, EXTENDED = 0x1;

    static void Send(params INPUT[] inputs) => SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>());

    static INPUT KeyInput(ushort vk, bool up)
    {
        uint flags = up ? KEYUP : 0;
        if (vk is 0x21 or 0x22 or 0x23 or 0x24 or 0x25 or 0x26 or 0x27 or 0x28 or 0x2D or 0x2E) flags |= EXTENDED;
        return new INPUT { type = 1, u = new InputUnion { ki = new KEYBDINPUT { wVk = vk, dwFlags = flags } } };
    }

    static void KeyRaw(ushort vk, bool up) => Send(KeyInput(vk, up));

    static readonly Dictionary<string, ushort> Vk = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Ctrl"] = 0x11, ["Shift"] = 0x10, ["Alt"] = 0x12, ["Enter"] = 0x0D, ["Esc"] = 0x1B, ["Escape"] = 0x1B, ["Tab"] = 0x09,
        ["Space"] = 0x20, ["Back"] = 0x08, ["Backspace"] = 0x08, ["Delete"] = 0x2E, ["Del"] = 0x2E, ["Home"] = 0x24, ["End"] = 0x23,
        ["Left"] = 0x25, ["Up"] = 0x26, ["Right"] = 0x27, ["Down"] = 0x28, ["PageUp"] = 0x21, ["PageDown"] = 0x22,
        [","] = 0xBC, ["."] = 0xBE, ["/"] = 0xBF, ["`"] = 0xC0, ["["] = 0xDB, ["]"] = 0xDD, ["="] = 0xBB, ["-"] = 0xBD,
        ["F2"] = 0x71, ["F3"] = 0x72, ["F5"] = 0x74,
    };

    static ushort VkOf(string k)
    {
        if (Vk.TryGetValue(k, out var v)) return v;
        if (k.Length == 1 && char.IsLetterOrDigit(k[0])) return char.ToUpperInvariant(k[0]);
        throw new ArgumentException("Unknown key " + k);
    }

    /// <summary>Press a chord such as "Ctrl+Shift+K", "Enter" or "Ctrl+,". Several chords separated by spaces.</summary>
    public void Keys(string chords, int pauseMs = 120)
    {
        EnsureForeground();
        foreach (var chord in chords.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            // "Ctrl++" is not used; split on '+' but keep a trailing '+'-free last key.
            var parts = chord.Split('+');
            var vks = parts.Select(VkOf).ToArray();
            var list = new List<INPUT>();
            foreach (var v in vks) list.Add(KeyInput(v, false));
            foreach (var v in vks.Reverse()) list.Add(KeyInput(v, true));
            Send(list.ToArray());
            Thread.Sleep(pauseMs);
        }
    }

    /// <summary>Types text as Unicode key events (what a user's keyboard produces as TextInput). '\n' becomes Enter.</summary>
    public void Type(string text, int perCharMs = 8)
    {
        EnsureForeground();
        foreach (var ch in text)
        {
            if (ch == '\n') { Keys("Enter", 30); continue; }
            // Prefer the key a keyboard would send (virtual key + Shift), like a user typing; VK_PACKET unicode input
            // goes through the text services (TSF) differently. Fall back to unicode for characters with no key.
            var scan = VkKeyScanW(ch);
            // Dead keys (US-International ' " ` ~ ^) would combine with the next character: send those as unicode.
            if (scan != -1 && (scan & 0x600) == 0 && (MapVirtualKeyW((uint)(scan & 0xFF), 2) & 0x80000000) == 0)
            {
                var vk = (ushort)(scan & 0xFF);
                var shift = (scan & 0x100) != 0;
                var list = new List<INPUT>();
                if (shift) list.Add(KeyInput(0x10, false));
                list.Add(KeyInput(vk, false));
                list.Add(KeyInput(vk, true));
                if (shift) list.Add(KeyInput(0x10, true));
                Send(list.ToArray());
                if (perCharMs > 0) Thread.Sleep(perCharMs);
                continue;
            }
            Send(new INPUT { type = 1, u = new InputUnion { ki = new KEYBDINPUT { wScan = ch, dwFlags = UNICODE } } },
                 new INPUT { type = 1, u = new InputUnion { ki = new KEYBDINPUT { wScan = ch, dwFlags = UNICODE | KEYUP } } });
            if (perCharMs > 0) Thread.Sleep(perCharMs);
        }
        Thread.Sleep(80);
    }

    // ---------- mouse ----------

    const uint LEFTDOWN = 0x2, LEFTUP = 0x4;

    public static void ClickAt(int x, int y, bool dbl = false)
    {
        SetCursorPos(x, y);
        Thread.Sleep(40);
        for (int i = 0; i < (dbl ? 2 : 1); i++)
        {
            Send(new INPUT { type = 0, u = new InputUnion { mi = new MOUSEINPUT { dwFlags = LEFTDOWN } } },
                 new INPUT { type = 0, u = new InputUnion { mi = new MOUSEINPUT { dwFlags = LEFTUP } } });
            Thread.Sleep(dbl ? 30 : 60);
        }
        Thread.Sleep(150);
    }

    public static void Click(AutomationElement e, bool dbl = false)
    {
        var r = e.Current.BoundingRectangle;
        if (r.IsEmpty) throw new InvalidOperationException("Element has no bounds: " + Name(e));
        ClickAt((int)(r.Left + r.Width / 2), (int)(r.Top + r.Height / 2), dbl);
    }

    public static void Invoke(AutomationElement e) => ((InvokePattern)e.GetCurrentPattern(InvokePattern.Pattern)).Invoke();

    // ---------- screen ----------

    public static System.Drawing.Bitmap Capture(System.Windows.Rect r)
    {
        var bmp = new System.Drawing.Bitmap(Math.Max(1, (int)r.Width), Math.Max(1, (int)r.Height));
        using var g = System.Drawing.Graphics.FromImage(bmp);
        g.CopyFromScreen((int)r.Left, (int)r.Top, 0, 0, bmp.Size);
        return bmp;
    }

    /// <summary>Average brightness (0..255) of a rectangle on screen.</summary>
    public static double Brightness(System.Windows.Rect r)
    {
        using var bmp = Capture(r);
        double sum = 0; int n = 0;
        for (int y = 0; y < bmp.Height; y += 3)
            for (int x = 0; x < bmp.Width; x += 3)
            {
                var c = bmp.GetPixel(x, y);
                sum += (c.R + c.G + c.B) / 3.0; n++;
            }
        return n == 0 ? 0 : sum / n;
    }

    // ---------- clipboard ----------

    public static string ClipboardText()
    {
        string result = "";
        var t = new Thread(() => { try { result = System.Windows.Forms.Clipboard.GetText(); } catch { } });
        t.SetApartmentState(ApartmentState.STA);
        t.Start(); t.Join();
        return result;
    }

    public static void ClearClipboard()
    {
        var t = new Thread(() => { try { System.Windows.Forms.Clipboard.Clear(); } catch { } });
        t.SetApartmentState(ApartmentState.STA);
        t.Start(); t.Join();
    }
}
