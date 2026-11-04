// ==================================================
// TrayIcon.cs - タスクトレイ常駐 (アイコン・プロファイル切替メニュー・LED の光り方メニュー・通知バルーン)
// Dependencies: Windows Forms NotifyIcon / .NET 8 (MIT License)
// See CREDITS.txt for full license details.
// ==================================================

using System.Runtime.InteropServices;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using Drawing = System.Drawing;
using WinForms = System.Windows.Forms;

namespace ProFanController.UI;

public sealed class TrayIcon : IDisposable
{
    private readonly WinForms.NotifyIcon _icon;
    private readonly WinForms.ToolStripMenuItem _pauseItem;
    private readonly WinForms.ToolStripMenuItem _widgetItem;
    private readonly WinForms.ToolStripMenuItem _profileMenu;
    private readonly Drawing.Icon _appIcon;
    private readonly Action<string> _selectProfile;
    private readonly Action<string> _command;
    private readonly WinForms.ToolStripMenuItem _ledMenu;
    private readonly WinForms.ToolStripMenuItem _musicMenu;
    private readonly WinForms.ToolStripMenuItem _ledToggle;
    /// <summary>「LED の光り方」の各項目と、その光り方（チェックを付けるため）</summary>
    private readonly List<(WinForms.ToolStripMenuItem Item, string Mode)> _ledModeItems = new();
    /// <summary>「音楽連動の光り方」の各項目と、その光り方</summary>
    private readonly List<(WinForms.ToolStripMenuItem Item, string Style)> _musicItems = new();

    /// <summary>
    /// いまの LED の設定を返す（メニューを開くたびに読んで、選ばれている項目にチェックを付ける）。
    /// 設定しなければ、メイン画面が表示している設定を読む
    /// </summary>
    public Func<RgbConfig?>? LightingState { get; set; }

    public TrayIcon(Action open, Action togglePause, Action toggleWidget, Action<string> selectProfile, Action<string> command, Action exit)
    {
        _appIcon = CreateAppIcon();
        _selectProfile = selectProfile;
        _command = command;

        var menu = new WinForms.ContextMenuStrip();
        menu.Items.Add(new WinForms.ToolStripMenuItem("ProFanController を開く", null, (_, _) => open()) { Font = new Drawing.Font(menu.Font, Drawing.FontStyle.Bold) });
        _profileMenu = new WinForms.ToolStripMenuItem("プロファイル");
        menu.Items.Add(_profileMenu);
        var quick = new WinForms.ToolStripMenuItem("ワンタッチ操作");
        foreach (var (text, cmd) in new[]
        {
            ("ブースト（全開）5 分", "boost:5"), ("ブースト（全開）15 分", "boost:15"),
            ("静音 30 分", "quiet:30"), ("静音 1 時間", "quiet:60"), ("静音 2 時間", "quiet:120"),
            ("解除", "cancel"),
        })
        {
            quick.DropDownItems.Add(new WinForms.ToolStripMenuItem(text, null, (_, _) => command(cmd)));
        }
        menu.Items.Add(quick);
        // LED: いまの光り方にチェックを付ける（メニューを開くたびに RefreshLightingMenu で更新）。
        // 選んだときは画面と同じ入口（App.ExecuteCommand）を通すので、「元に戻す」の履歴には残らない
        _ledMenu = new WinForms.ToolStripMenuItem("LED の光り方");
        foreach (var mode in new[] { RgbModes.Temperature, RgbModes.Rainbow, RgbModes.RainbowStatic, RgbModes.Breathing, RgbModes.Static, RgbModes.Music })
        {
            var item = new WinForms.ToolStripMenuItem(ModeLabel(mode), null, (_, _) => command($"led:{mode}"));
            _ledModeItems.Add((item, mode));
            _ledMenu.DropDownItems.Add(item);
        }
        _ledMenu.DropDownItems.Add(new WinForms.ToolStripSeparator());
        _ledToggle = new WinForms.ToolStripMenuItem("LED 連携", null, (_, _) => ToggleLighting());
        _ledMenu.DropDownItems.Add(_ledToggle);
        menu.Items.Add(_ledMenu);

        _musicMenu = new WinForms.ToolStripMenuItem("音楽連動の光り方");
        foreach (var style in MusicStyles.All)
        {
            var (label, description) = MusicTuning.Describe(style);
            var item = new WinForms.ToolStripMenuItem(label, null, (_, _) => command($"music:{style}")) { ToolTipText = description };
            _musicItems.Add((item, style));
            _musicMenu.DropDownItems.Add(item);
        }
        _musicMenu.DropDown.ShowItemToolTips = true; // 項目に乗せたとき説明を出す
        menu.Items.Add(_musicMenu);
        menu.Opening += (_, _) => RefreshLightingMenu();
        _widgetItem = new WinForms.ToolStripMenuItem("ミニウィジェットを表示", null, (_, _) => toggleWidget());
        menu.Items.Add(_widgetItem);
        _pauseItem = new WinForms.ToolStripMenuItem("一時停止（BIOS に戻す）", null, (_, _) => togglePause());
        menu.Items.Add(_pauseItem);
        menu.Items.Add(new WinForms.ToolStripSeparator());
        menu.Items.Add(new WinForms.ToolStripMenuItem("終了（BIOS 制御に戻す）", null, (_, _) => exit()));

        _icon = new WinForms.NotifyIcon
        {
            Icon = _appIcon,
            Text = "ProFanController",
            ContextMenuStrip = menu,
            Visible = true,
        };
        _icon.MouseClick += (_, e) =>
        {
            if (e.Button == WinForms.MouseButtons.Left) open();
        };
    }

    /// <summary>光り方の表示名（画面の「光り方」と同じ言葉）</summary>
    private static string ModeLabel(string mode) => mode switch
    {
        RgbModes.Temperature => "温度",
        RgbModes.Rainbow => "レインボー（流れる）",
        RgbModes.RainbowStatic => "レインボー（固定）",
        RgbModes.Breathing => "呼吸",
        RgbModes.Static => "固定色",
        RgbModes.Music => "音楽連動",
        _ => mode,
    };

    /// <summary>いまの LED の設定（LightingState がなければ、メイン画面から読む）</summary>
    private RgbConfig? CurrentLighting()
    {
        try
        {
            if (LightingState is { } state) return state();
            if (System.Windows.Application.Current is { } app)
            {
                foreach (var w in app.Windows)
                {
                    if (w is MainWindow main) return main.LightingConfig;
                }
            }
        }
        catch (Exception ex)
        {
            Logger.Warn($"トレイ: LED の設定を読めません: {ex.Message}");
        }
        return null;
    }

    /// <summary>メニューを開く直前に、いまの光り方・オンオフにチェックを付ける</summary>
    private void RefreshLightingMenu()
    {
        var cfg = CurrentLighting();
        bool enabled = cfg?.Enabled == true;
        foreach (var (item, mode) in _ledModeItems) item.Checked = enabled && cfg!.Mode == mode;
        _ledToggle.Checked = enabled;
        _ledToggle.Text = enabled ? "LED 連携をオフにする" : "LED 連携をオンにする";
        _ledMenu.Text = cfg is null ? "LED の光り方" : enabled ? $"LED の光り方: {ModeLabel(cfg.Mode)}" : "LED の光り方: オフ";

        bool music = enabled && cfg!.Mode == RgbModes.Music;
        bool reduceFlashing = cfg?.Music?.ReduceFlashing ?? true;
        foreach (var (item, style) in _musicItems)
        {
            item.Checked = music && cfg!.MusicStyle == style;
            // ストロボは「激しい点滅を抑える」をオフにしたときだけ選べる（画面の設定と同じ）
            if (style == MusicStyles.Strobe)
            {
                item.Enabled = !reduceFlashing;
                item.ToolTipText = reduceFlashing
                    ? "激しく点滅します。ライティング画面で「激しい点滅を抑える」をオフにすると選べます"
                    : MusicTuning.Describe(style).Description;
            }
        }
    }

    /// <summary>LED 連携のオン・オフを切り替える（押した時点の設定を読み直してから決める）</summary>
    private void ToggleLighting()
        => _command(CurrentLighting()?.Enabled == true ? "led:off" : "led:on");

    public void SetPaused(bool paused)
        => _pauseItem.Text = paused ? "制御を再開する" : "一時停止（BIOS に戻す）";

    public void SetWidgetVisible(bool visible) => _widgetItem.Checked = visible;

    /// <summary>プロファイル一覧を作り直す。reason は普段と違うプロファイルを使っている理由（例: 自動: game）</summary>
    public void SetProfiles(IEnumerable<string> names, string manual, string effective, string? reason)
    {
        _profileMenu.DropDownItems.Clear();
        foreach (var name in names)
        {
            var item = new WinForms.ToolStripMenuItem(name, null, (_, _) => _selectProfile(name)) { Checked = name == manual };
            if (reason is not null && name == effective && name != manual) item.Text += $"（{reason}）";
            _profileMenu.DropDownItems.Add(item);
        }
        _profileMenu.Text = $"プロファイル: {effective}";
    }

    /// <summary>マウスを乗せたときの表示（Windows の制限で 127 文字まで）</summary>
    public void SetTooltip(string text)
        => _icon.Text = text.Length > 120 ? text[..120] : text;

    public void ShowBalloon(string title, string message, AlertLevel level)
    {
        var icon = level switch
        {
            AlertLevel.Danger => WinForms.ToolTipIcon.Error,
            AlertLevel.Warning => WinForms.ToolTipIcon.Warning,
            _ => WinForms.ToolTipIcon.Info,
        };
        _icon.ShowBalloonTip(5000, title, message, icon);
    }

    /// <summary>ウィンドウのタイトルバー用に、同じアイコンを WPF 形式で返す</summary>
    public System.Windows.Media.ImageSource CreateWindowIcon()
        => Imaging.CreateBitmapSourceFromHIcon(_appIcon.Handle, System.Windows.Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());

    /// <summary>アイコンファイルを同梱しなくて済むよう、グラデーションのファン図形をその場で描く</summary>
    private static Drawing.Icon CreateAppIcon()
    {
        using var bmp = new Drawing.Bitmap(64, 64);
        using (var g = Drawing.Graphics.FromImage(bmp))
        {
            g.SmoothingMode = Drawing.Drawing2D.SmoothingMode.AntiAlias;
            g.Clear(Drawing.Color.Transparent);
            using var bg = new Drawing.Drawing2D.LinearGradientBrush(
                new Drawing.Rectangle(0, 0, 64, 64), Drawing.Color.FromArgb(0x7C, 0x5C, 0xFF), Drawing.Color.FromArgb(0x22, 0xC7, 0xFF), 45f);
            g.FillEllipse(bg, 2, 2, 60, 60);

            // 羽根 3 枚
            using var white = new Drawing.SolidBrush(Drawing.Color.White);
            for (int i = 0; i < 3; i++)
            {
                var state = g.Save();
                g.TranslateTransform(32, 32);
                g.RotateTransform(i * 120);
                g.FillEllipse(white, -7, -26, 14, 24);
                g.Restore(state);
            }
            using var hub = new Drawing.SolidBrush(Drawing.Color.FromArgb(0x10, 0x11, 0x1A));
            g.FillEllipse(hub, 25, 25, 14, 14);
        }

        IntPtr hIcon = bmp.GetHicon();
        try
        {
            // FromHandle は所有権を持たないので、コピーを作ってから元のハンドルを解放する
            return (Drawing.Icon)Drawing.Icon.FromHandle(hIcon).Clone();
        }
        finally
        {
            DestroyIcon(hIcon);
        }
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyIcon(IntPtr hIcon);

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.Dispose();
        _appIcon.Dispose();
    }
}
