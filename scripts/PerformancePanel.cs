using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Web.Script.Serialization;
using System.Windows.Forms;

// A process can have a visible helper or console window before its title window.
public static class VictoryWindow {
    private delegate bool Visit(IntPtr window, IntPtr state);
    [DllImport("user32.dll")] private static extern bool EnumWindows(Visit visit, IntPtr state);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window, out uint process);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr window);
    [DllImport("user32.dll", CharSet=CharSet.Unicode)] private static extern int GetWindowText(IntPtr window, StringBuilder text, int capacity);
    [DllImport("user32.dll", CharSet=CharSet.Unicode)] private static extern IntPtr GetProp(IntPtr window, string name);
    public static string Caption(IntPtr window) {
        var text = new StringBuilder(512);
        if (window != IntPtr.Zero) GetWindowText(window, text, text.Capacity);
        return text.ToString();
    }
    public static IntPtr Find(int process) {
        IntPtr title = IntPtr.Zero, hooked = IntPtr.Zero;
        EnumWindows(delegate(IntPtr window, IntPtr state) {
            uint owner;
            GetWindowThreadProcessId(window, out owner);
            if (owner != process || !IsWindowVisible(window)) return true;
            if (Caption(window).IndexOf(" | VIctory ", StringComparison.OrdinalIgnoreCase) >= 0) {
                title = window; return false;
            }
            if (GetProp(window, "victory.wndproc") != IntPtr.Zero) hooked = window;
            return true;
        }, IntPtr.Zero);
        return title != IntPtr.Zero ? title : hooked;
    }
}

internal sealed class CpuInfo { public string Name { get; set; } }
internal sealed class Recording {
    public CpuInfo[] cpu { get; set; }
    public string runtimeAdapter { get; set; }
    public double osVisibleMemoryMiB { get; set; }
}
public sealed class GamePanel : Form {
    [StructLayout(LayoutKind.Sequential)] private struct Rect { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr window, out Rect rect);
    [DllImport("user32.dll")] private static extern IntPtr SetWindowLongPtr(IntPtr window, int index, IntPtr value);
    [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr window);
    [DllImport("user32.dll", CharSet=CharSet.Unicode)] private static extern IntPtr GetProp(IntPtr window, string key);
    [DllImport("user32.dll")] private static extern uint GetDpiForWindow(IntPtr window);
    [DllImport("user32.dll")] private static extern bool SetProcessDpiAwarenessContext(IntPtr context);

    private readonly Process game;
    private readonly string folder;
    private readonly System.Windows.Forms.Timer clock = new System.Windows.Forms.Timer();
    private readonly Stopwatch uptime = Stopwatch.StartNew();
    private readonly CultureInfo invariant = CultureInfo.InvariantCulture;
    private readonly Font titleFont, labelFont, valueFont, modelFont;
    private Recording machine;
    private readonly Dictionary<string, double> sample = new Dictionary<string, double>();
    private string fps = "…";
    private long nextRead;
    private float scale = 1;
    private static readonly Color Accent = Color.FromArgb(115, 220, 199);
    private static readonly Color Fore = Color.FromArgb(243, 247, 250);
    private static readonly Color Muted = Color.FromArgb(188, 200, 211);
    private const int LogicalWidth = 342, StatsHeight = 226;
    private bool pcControls;
    private bool captured, driving;
    private long inputFlags;
    // Each Xbox input has one permanent row. The two columns show the actual
    // keyboard aliases and title-dependent actions in both input layouts.
    private sealed class Binding {
        public readonly string Pad, Foot, Vehicle, FootAction, VehicleAction;
        public readonly int Flag;
        public Binding(string pad, int flag, string foot, string vehicle, string footAction, string vehicleAction) {
            Pad = pad; Flag = flag; Foot = foot; Vehicle = vehicle; FootAction = footAction; VehicleAction = vehicleAction;
        }
    }
    private static readonly Binding[] Bindings = {
        new Binding("L direction", 1 << 18, "WASD / numpad", "A, D / numpad", "Move", "Steer / left stick"),
        new Binding("LS / L3 click", 0x1000, "Shift", "Shift / E", "Click / sprint", "Click / horn"),
        new Binding("R direction", 1 << 19, "Mouse / IJKL", "Mouse / IJKL", "Look", "Look"),
        new Binding("RS / R3 click", 0x2000, "C / H", "C", "Click / rear view", "Click / rear view"),
        new Binding("LB", 0x400, "Tab", "Tab / right mouse", "Equipment wheel", "Vehicle weapon"),
        new Binding("RB", 0x800, "Q", "Q / left mouse", "Cover", "Vehicle fire"),
        new Binding("LT", 1 << 16, "Right mouse / Z", "S / Z", "Aim", "Brake / reverse"),
        new Binding("RT", 1 << 17, "Left mouse", "W", "Fire", "Accelerate"),
        new Binding("A", 4, "Space / Enter", "Space / Enter", "Jump / confirm", "Handbrake / confirm"),
        new Binding("B", 8, "Ctrl / B / Bksp", "Ctrl / B / H / Bksp", "Crouch / back", "Back / headlights"),
        new Binding("X", 16, "R / X", "R / X", "Reload / melee", "Context action"),
        new Binding("Y", 32, "E / F / Y", "F / Y", "Interact / enter", "Leave vehicle"),
        new Binding("D-pad", 0x3c0, "↑ ↓ ← →", "↑ ↓ ← →", "Directional buttons", "Directional buttons"),
        new Binding("View", 2, "M", "M", "Map", "Map"),
        new Binding("Menu", 1, "Esc", "Esc", "Pause", "Pause")
    };
    private const int RowHeight = 32, GuideHeader = 51, GuideFooter = 86;
    private GamePanel(Process game, string folder) {
        this.game = game; this.folder = folder;
        FormBorderStyle = FormBorderStyle.None; ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual; AutoScaleMode = AutoScaleMode.None;
        DoubleBuffered = true; Text = "VIctory runtime monitor"; AccessibleName = Text;
        BackColor = Color.FromArgb(23, 32, 40);
        FontFamily family = SystemFonts.MessageBoxFont.FontFamily;
        titleFont = new Font(family, 15, FontStyle.Bold, GraphicsUnit.Pixel);
        labelFont = new Font(family, 13, FontStyle.Regular, GraphicsUnit.Pixel);
        valueFont = new Font(family, 14, FontStyle.Bold, GraphicsUnit.Pixel);
        modelFont = new Font(family, 12, FontStyle.Regular, GraphicsUnit.Pixel);
        clock.Interval = 200; clock.Tick += delegate { UpdatePanel(); };
    }
    protected override bool ShowWithoutActivation { get { return true; } }
    protected override CreateParams CreateParams {
        get { var cp = base.CreateParams; cp.ExStyle |= 0x08000000 | 0x80 | 0x20; return cp; }
    }
    protected override void WndProc(ref Message m) {
        if (m.Msg == 0x84) { m.Result = new IntPtr(-1); return; }
        if (m.Msg == 0x21) { m.Result = new IntPtr(3); return; }
        base.WndProc(ref m);
    }
    protected override void OnLoad(EventArgs e) {
        base.OnLoad(e);
        SetWindowLongPtr(Handle, -8, VictoryWindow.Find(game.Id));
        UpdatePanel(); clock.Start();
    }

    // Read the final complete record while the recorder is appending to the file.
    private static string LastRecord(string file) {
        using (var s = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete)) {
            int size = (int)Math.Min(4096L, s.Length);
            s.Seek(-size, SeekOrigin.End);
            using (var reader = new StreamReader(s, Encoding.UTF8)) {
                string tail = reader.ReadToEnd();
                int end = tail.LastIndexOf('\n');
                if (end < 1) return null;
                int start = tail.LastIndexOf('\n', end - 1) + 1;
                string line = tail.Substring(start, end - start).TrimEnd('\r');
                return line.Length > 0 && Char.IsDigit(line[0]) ? line : null;
            }
        }
    }
    private static bool Fresh(string time, int seconds) {
        DateTimeOffset stamp;
        return DateTimeOffset.TryParse(time, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out stamp)
            && Math.Abs((DateTimeOffset.UtcNow - stamp).TotalSeconds) <= seconds;
    }
    private void ReadData() {
        try {
            using (var f = new FileStream(Path.Combine(folder, "hardware.json"), FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            using (var r = new StreamReader(f, Encoding.UTF8))
                machine = new JavaScriptSerializer().Deserialize<Recording>(r.ReadToEnd());
        } catch (IOException) {} catch (ArgumentException) {} catch (InvalidOperationException) {}
        sample.Clear();
        try {
            string path = Path.Combine(folder, "samples.csv"), record = LastRecord(path);
            if (record != null) {
                string[] values = record.Split(',');
                if (values.Length > 1 && values[1] == game.Id.ToString(invariant) && Fresh(values[0], 10)) {
                    using (var f = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                    using (var r = new StreamReader(f)) {
                        string[] names = (r.ReadLine() ?? "").Split(',');
                        for (int i = 2; i < Math.Min(names.Length, values.Length); ++i) {
                            double v;
                            if (Double.TryParse(values[i], NumberStyles.Float, invariant, out v)) sample[names[i]] = v;
                        }
                    }
                }
            }
        } catch (IOException) {}
        fps = "…";
        try {
            string row = LastRecord(Path.Combine(folder, "frames.csv"));
            if (row != null) {
                var fields = row.Split(','); double val;
                if (fields.Length >= 2 && Fresh(fields[0], 12) && Double.TryParse(fields[1], NumberStyles.Float, invariant, out val))
                    fps = val.ToString("0.0", invariant);
            }
        } catch (IOException) {}
    }
    private void UpdatePanel() {
        game.Refresh();
        if (game.HasExited || !File.Exists(Path.Combine(folder, "active"))) { Close(); return; }
        IntPtr h = VictoryWindow.Find(game.Id);
        Rect r;
        if (h == IntPtr.Zero || !GetWindowRect(h, out r) || IsIconic(h)) { if (Visible) Hide(); return; }
        var area = Screen.FromHandle(h).WorkingArea;
        // These properties are present on builds with the optional PC controls.
        // Keep both columns visible when the player releases the mouse with F6.
        pcControls |= GetProp(h, "victory.pc_controls") != IntPtr.Zero
            || GetProp(h, "victory.mouse_look") != IntPtr.Zero || GetProp(h, "victory.pc_buttons") != IntPtr.Zero;
        int height = StatsHeight;
        if (pcControls) {
            bool hasMouse = GetProp(h, "victory.mouse_look") != IntPtr.Zero;
            long flags = Math.Max(0, GetProp(h, "victory.pc_buttons").ToInt64() - 1);
            bool car = GetProp(h, "victory.driving_keys") != IntPtr.Zero;
            if (captured != hasMouse || inputFlags != flags || driving != car) Invalidate();
            captured = hasMouse; inputFlags = flags; driving = car;
            height += GuideHeader + Bindings.Length * RowHeight + GuideFooter;
        }
        scale = Math.Min(Math.Max(1f, GetDpiForWindow(Handle) / 96f), (area.Height - 12f) / height);
        int w = (int)Math.Ceiling(LogicalWidth * scale), ht = (int)Math.Ceiling(height * scale), pad = (int)(10 * scale);
        int x = area.Right - r.Right >= w + pad ? r.Right + pad : r.Right - w - pad;
        int y = Math.Min(Math.Max(r.Top + 2 * pad, area.Top), area.Bottom - ht);
        Rectangle next = new Rectangle(Math.Max(area.Left, Math.Min(x, area.Right - w)), y, w, ht);
        if (Bounds != next) Bounds = next;
        if (uptime.ElapsedMilliseconds >= nextRead) {
            ReadData(); nextRead = uptime.ElapsedMilliseconds + 900; Invalidate();
        }
        if (!Visible && IsHandleCreated && uptime.ElapsedMilliseconds > 500) Show();
    }
    private string Number(string key, double divisor, string format, string unit) {
        double n; return sample.TryGetValue(key, out n) ? (n / divisor).ToString(format, invariant) + unit : "…";
    }
    private void TextLine(Graphics g, string text, Font f, Color c, float x, float y, float w, bool right) {
        using (var b = new SolidBrush(c))
        using (var fmt = new StringFormat(StringFormat.GenericTypographic)) {
            fmt.Trimming = StringTrimming.EllipsisCharacter; fmt.FormatFlags |= StringFormatFlags.NoWrap;
            if (right) fmt.Alignment = StringAlignment.Far;
            g.DrawString(text, f, b, new RectangleF(x, y, w, 22), fmt);
        }
    }
    private void Usage(Graphics g, int y, string name, string model, string key) {
        TextLine(g, name, labelFont, Fore, 14, y, 133, false);
        TextLine(g, Number(key, 1, "0", "%"), valueFont, Accent, LogicalWidth - 93, y - 1, 79, true);
        TextLine(g, model, modelFont, Muted, 14, y + 19, LogicalWidth - 28, false);
        using (var bg = new SolidBrush(Color.FromArgb(60, 74, 84))) g.FillRectangle(bg, 14, y + 38, LogicalWidth - 28, 3);
        double n;
        if (sample.TryGetValue(key, out n)) using (var b = new SolidBrush(Accent))
            g.FillRectangle(b, 14, y + 38, (float)((LogicalWidth - 28) / 100.0 * Math.Min(100, Math.Max(0,n))), 3);
    }
    protected override void OnPaint(PaintEventArgs e) {
        base.OnPaint(e); Graphics g = e.Graphics; g.ScaleTransform(scale, scale);
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
        string cpu = machine != null && machine.cpu != null && machine.cpu.Length > 0 && machine.cpu[0].Name != null
            ? machine.cpu[0].Name.Replace("(R)", "").Replace("(TM)", "").Trim() : "Reading CPU model";
        string gpu = machine != null && !String.IsNullOrWhiteSpace(machine.runtimeAdapter) ? machine.runtimeAdapter : "Waiting for a 3D engine";
        TextLine(g, "Performance", titleFont, Fore, 14, 9, 110, false);
        TextLine(g, fps + " FPS", valueFont, Accent, LogicalWidth - 128, 10, 114, true);
        Usage(g, 40, "Game CPU", cpu, "game_cpu_pct");
        Usage(g, 91, "Game GPU (3D)", gpu, "gpu_3d_pct");
        TextLine(g, "Working set", labelFont, Fore, 14, 144, 96, false);
        TextLine(g, Number("game_working_set_mib", 1024, "0.0", " GiB"), valueFont, Fore, LogicalWidth - 150, 142, 136, true);
        TextLine(g, "Dedicated GPU", labelFont, Fore, 14, 170, 96, false);
        TextLine(g, Number("gpu_dedicated_mib", 1024, "0.0", " GiB"), valueFont, Fore, LogicalWidth - 150, 168, 136, true);
        string total = machine != null && machine.osVisibleMemoryMiB > 0 ? (machine.osVisibleMemoryMiB / 1024).ToString("0.0", invariant) : "…";
        TextLine(g, "System RAM", labelFont, Muted, 14, 197, 85, false);
        TextLine(g, Number("system_ram_used_mib", 1024, "0.0", "") + " / " + total + " GiB", modelFont, Muted, LogicalWidth - 180, 198, 166, true);
        if (!pcControls) return;
        using (var pen = new Pen(Color.FromArgb(60, 74, 84))) g.DrawLine(pen, 14, StatsHeight, LogicalWidth - 14, StatsHeight);
        TextLine(g, "Controls", titleFont, Fore, 14, StatsHeight + 5, 95, false);
        TextLine(g, driving ? "Driving / F5 to switch" : "On foot / F5 to switch", modelFont, Accent, 110, StatsHeight + 7, 218, true);
        TextLine(g, "Xbox prompt", modelFont, Muted, 14, StatsHeight + 29, 84, false);
        TextLine(g, "On foot", labelFont, driving ? Muted : Accent, 103, StatsHeight + 28, 100, false);
        TextLine(g, "Driving / boat", labelFont, driving ? Accent : Muted, 216, StatsHeight + 28, 112, false);
        int y = StatsHeight + GuideHeader;
        foreach (Binding binding in Bindings) {
            bool active = (inputFlags & binding.Flag) != 0;
            TextLine(g, binding.Pad, modelFont, active ? Accent : Fore, 14, y, 87, false);
            TextLine(g, binding.Foot, labelFont, active && !driving ? Accent : Fore, 103, y - 1, 111, false);
            TextLine(g, binding.Vehicle, labelFont, active && driving ? Accent : Fore, 216, y - 1, 119, false);
            TextLine(g, binding.FootAction, modelFont, Muted, 103, y + 14, 112, false);
            TextLine(g, binding.VehicleAction, modelFont, Muted, 216, y + 14, 119, false);
            y += RowHeight;
        }
        TextLine(g, "Hold Space for a hold-A prompt.", labelFont, Fore, 14, y + 1, 310, false);
        TextLine(g, "Left stick: numpad 8 / 4 / 2 / 6 with Num Lock.", modelFont, Muted, 14, y + 21, 317, false);
        TextLine(g, "Both stick clicks: Shift+C. Both shoulders: Tab+Q.", modelFont, Muted, 14, y + 38, 317, false);
        TextLine(g, captured ? "F6 free mouse" : "F6 capture mouse", modelFont, Accent, 14, y + 57, 112, false);
        TextLine(g, "Actions follow the current title prompt.", modelFont, Muted, 110, y + 57, 218, true);

    }
    protected override void Dispose(bool disposing) {
        if (disposing) { clock.Dispose(); titleFont.Dispose(); labelFont.Dispose(); valueFont.Dispose(); modelFont.Dispose(); }
        base.Dispose(disposing);
    }
    public static int Run(int pid, string folder) {
        if (pid <= 0 || !Directory.Exists(folder)) return 2;
        try {
            bool first;
            using (var guard = new Mutex(true, @"Local\VIctory.PerformancePanel." + pid, out first)) {
                if (!first) return 0;
                using (var proc = Process.GetProcessById(pid)) {
                    for (int i = 0; i < 240 && !proc.HasExited && VictoryWindow.Find(proc.Id) == IntPtr.Zero; ++i) {
                        Thread.Sleep(250); proc.Refresh();
                    }
                    if (proc.HasExited || VictoryWindow.Find(proc.Id) == IntPtr.Zero) return 0;
                    SetProcessDpiAwarenessContext(new IntPtr(-4));
                    Application.EnableVisualStyles(); Application.SetCompatibleTextRenderingDefault(false);
                    using (var form = new GamePanel(proc, Path.GetFullPath(folder))) Application.Run(form);
                }
            }
            return 0;
        } catch (ArgumentException) { return 2; } catch (InvalidOperationException) { return 1; }
    }
}
