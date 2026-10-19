using System.Diagnostics;
using System.Drawing.Drawing2D;
using MojaveDesktopRadio.Engine;
using MojaveDesktopRadio.Fx;
using MojaveDesktopRadio.Media;

namespace MojaveDesktopRadio.Tray;

/// <summary>Notification-area UI. All engine calls are posted; the UI thread never blocks on audio or IPC.</summary>
internal sealed class TrayContext : ApplicationContext
{
    private readonly BridgeEngine _engine;
    private readonly SynchronizationContext _ui;
    private readonly NotifyIcon _icon;
    private readonly ToolStripMenuItem _playerItem = new() { Enabled = false };
    private readonly ToolStripMenuItem _trackItem = new() { Enabled = false };
    private readonly ToolStripMenuItem _gameItem = new() { Enabled = false };
    private readonly ToolStripMenuItem _fxItem = new() { Enabled = false };
    private readonly ToolStripMenuItem _desktopFxItem = new("Desktop FX (without the game)") { CheckOnClick = true };
    private readonly ToolStripMenuItem _presetMenu = new("Desktop FX preset");
    private readonly ToolStripMenuItem _playerMenu = new("Desktop player");
    private readonly Icon _appIcon;
    private bool _rendering;

    public TrayContext(BridgeEngine engine)
    {
        _engine = engine;
        _ui = SynchronizationContext.Current ?? new WindowsFormsSynchronizationContext();
        _appIcon = CreateIcon();

        var menu = new ContextMenuStrip();
        menu.Items.Add(new ToolStripMenuItem("Mojave Desktop Radio") { Enabled = false, Font = new Font(SystemFonts.MenuFont ?? Control.DefaultFont, FontStyle.Bold) });
        menu.Items.Add(_playerItem);
        menu.Items.Add(_trackItem);
        menu.Items.Add(_gameItem);
        menu.Items.Add(_fxItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Play / Pause", null, (_, _) => _engine.SendMediaCommand(MediaCommand.Toggle));
        menu.Items.Add("Next track", null, (_, _) => _engine.SendMediaCommand(MediaCommand.Next));
        menu.Items.Add("Previous track", null, (_, _) => _engine.SendMediaCommand(MediaCommand.Previous));
        menu.Items.Add(new ToolStripSeparator());

        _desktopFxItem.CheckedChanged += (_, _) =>
        {
            if (!_rendering) _engine.SetDesktopFx(_desktopFxItem.Checked);
        };
        menu.Items.Add(_desktopFxItem);
        foreach (var preset in Enum.GetValues<RadioPreset>())
        {
            var item = new ToolStripMenuItem(PresetName(preset)) { Tag = preset };
            item.Click += (_, _) => _engine.SetDesktopPreset(preset);
            _presetMenu.DropDownItems.Add(item);
        }

        menu.Items.Add(_presetMenu);

        // Optional: other players than Spotify. While the game runs, MCM > Desktop Player decides.
        foreach (var profile in PlayerProfiles.BuiltIn)
            _playerMenu.DropDownItems.Add(PlayerItem(profile.Key, profile.Tested ? profile.DisplayName : profile.DisplayName + " (untested)"));
        _playerMenu.DropDownItems.Add(PlayerItem(PlayerProfiles.CustomKey, "Custom (settings.json)"));
        menu.Items.Add(_playerMenu);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Open log folder", null, (_, _) => OpenPath(AppPaths.LogDir));
        menu.Items.Add("Open settings.json", null, (_, _) => OpenPath(AppPaths.SettingsFile));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Exit (restores audio routing)", null, (_, _) => ExitThread());

        _icon = new NotifyIcon
        {
            Icon = _appIcon,
            Text = "Mojave Desktop Radio",
            ContextMenuStrip = menu,
            Visible = true,
        };
        _icon.MouseClick += (_, e) =>
        {
            if (e.Button == MouseButtons.Left) _engine.SendMediaCommand(MediaCommand.Toggle);
        };

        _engine.StatusChanged += s => _ui.Post(_ => Render(s), null);
        _engine.ExitRequested += () => _ui.Post(_ => ExitThread(), null);
        Microsoft.Win32.SystemEvents.SessionEnding += OnSessionEnding;

        try { Render(_engine.GetStatus()); } catch { /* first status arrives with the next tick */ }
    }

    private void OnSessionEnding(object? sender, Microsoft.Win32.SessionEndingEventArgs e)
    {
        // Windows logoff / shutdown: restore audio routing before we get killed.
        _engine.Dispose();
    }

    private void Render(EngineStatus s)
    {
        _playerItem.Text = $"{s.Player.DisplayName}: " + s.Media.Status switch
        {
            PlayerStatus.NotRunning => "not running",
            PlayerStatus.NoSession => "open, no media session yet (press play once)",
            PlayerStatus.Playing => "playing",
            PlayerStatus.Paused => "paused",
            PlayerStatus.Stopped => "stopped",
            _ => "…",
        };
        _trackItem.Text = s.Media.HasSession && s.Media.Title.Length > 0
            ? Truncate($"♪ {s.Media.Artist} – {s.Media.Title}", 70)
            : "♪ —";
        _gameItem.Text = s.GameConnected
            ? $"Fallout: connected, station {(s.StationActive ? "ON" : "off")}"
            : "Fallout: not connected";
        _fxItem.Text = $"FX: {PresetName(s.Fx.EffectivePreset)} — {RouteText(s)}";

        if (_desktopFxItem.Checked != s.DesktopFx)
        {
            _rendering = true;
            _desktopFxItem.Checked = s.DesktopFx;
            _rendering = false;
        }

        foreach (ToolStripMenuItem item in _presetMenu.DropDownItems)
            item.Checked = item.Tag is RadioPreset p && p == _engine.Settings.DesktopFx.Preset;

        foreach (ToolStripMenuItem item in _playerMenu.DropDownItems)
            item.Checked = item.Tag is string key && key == s.Player.Key;
        _playerMenu.Enabled = !s.GameConnected;
        _playerMenu.Text = s.GameConnected ? "Desktop player (set in MCM while the game runs)" : "Desktop player";

        var tip = s.Media.IsPlaying && s.Media.Title.Length > 0 ? $"MDR: {s.Media.Artist} – {s.Media.Title}" : "Mojave Desktop Radio";
        _icon.Text = Truncate(tip, 127);
    }

    private ToolStripMenuItem PlayerItem(string key, string text)
    {
        var item = new ToolStripMenuItem(text) { Tag = key };
        item.Click += (_, _) => _engine.SelectPlayer(key);
        return item;
    }

    private static string RouteText(EngineStatus s) => s.Route switch
    {
        RouteState.Off => "direct",
        RouteState.Pending => $"routing… ({s.RouteDetail})",
        RouteState.Routed => $"via {s.RouteDevice} → {s.OutputDevice}",
        RouteState.Manual => $"manual route via {s.RouteDevice}",
        RouteState.Unavailable => $"unavailable: {s.RouteDetail}",
        _ => "",
    };

    private static string PresetName(RadioPreset p) => p switch
    {
        RadioPreset.Clean => "Clean",
        RadioPreset.Mojave => "Mojave Radio",
        RadioPreset.Wasteland => "Wasteland Radio",
        RadioPreset.Damaged => "Damaged Radio",
        _ => p.ToString(),
    };

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..(max - 1)] + "…";

    private static void OpenPath(string path)
    {
        try
        {
            AppPaths.EnsureCreated();
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Log.Warn($"could not open {path}: {ex.Message}");
        }
    }

    /// <summary>Draws a small Pip-Boy green radio icon (no binary assets needed).</summary>
    private static Icon CreateIcon()
    {
        using var bmp = new Bitmap(32, 32);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);
            var green = Color.FromArgb(255, 26, 255, 128);
            using var pen = new Pen(green, 2.4f);
            using var brush = new SolidBrush(Color.FromArgb(255, 10, 40, 20));
            using var fill = new SolidBrush(green);
            g.DrawLine(pen, 9, 11, 22, 3);                 // antenna
            g.FillRectangle(brush, 3, 11, 26, 17);
            g.DrawRectangle(pen, 3, 11, 26, 17);           // body
            g.FillEllipse(fill, 18, 15, 8, 8);             // speaker
            for (var y = 15; y <= 23; y += 4) g.DrawLine(pen, 7, y, 14, y); // grille
        }

        var h = bmp.GetHicon();
        using var tmp = Icon.FromHandle(h);
        var icon = (Icon)tmp.Clone();
        DestroyIcon(h);
        return icon;
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr handle);

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            Microsoft.Win32.SystemEvents.SessionEnding -= OnSessionEnding;
            _icon.Visible = false;
            _icon.Dispose();
            _appIcon.Dispose();
        }

        base.Dispose(disposing);
    }
}
