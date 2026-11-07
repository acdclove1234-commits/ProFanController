// ==================================================
// ThemeManager.cs - 見た目の切り替え (ダーク／ライト／Windows に合わせる・アクセント色 64 色と Windows の色・
//                   フォント・文字の大きさ)。画面の色の資源をまとめて入れ替える
// Dependencies: WPF / .NET 8 (MIT License)
// See CREDITS.txt for full license details.
// ==================================================

using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using Microsoft.Win32;

namespace ProFanController.UI;

/// <summary>テーマ 1 つ分の色（画面の色の資源と、グラフなどコードで描く部品の色）</summary>
public sealed record ThemeColors(
    bool IsLight,
    Color Bg, Color Sidebar, Color Card, Color CardHover, Color Border, Color Input, Color Track,
    Color Text, Color SubText, Color Muted, Color Accent, Color Accent2, Color Ok, Color Warn, Color Danger,
    Color Row, Color Inset, Color Inset2, Color ChartBg, Color ChartGrid, Color ChartLabel,
    Color Overlay, Color Line, Color WidgetBg, Color Scrim, Color TipBg)
{
    public static readonly ThemeColors Dark = new(false,
        Hex("#0B0C12"), Hex("#10111A"), Hex("#161824"), Hex("#1D2030"), Hex("#252838"), Hex("#1D2030"), Hex("#2A2E42"),
        Hex("#ECEEF6"), Hex("#8B91A8"), Hex("#5B6178"), Hex("#7C5CFF"), Hex("#22C7FF"), Hex("#34D399"), Hex("#FBBF24"), Hex("#F87171"),
        Hex("#10121A"), Hex("#0D0F16"), Hex("#0B0D14"), Hex("#12141F"), Hex("#232636"), Hex("#6B718A"),
        Hex("#0FFFFFFF"), Hex("#22FFFFFF"), Hex("#E6101119"), Hex("#CC161824"), Hex("#F01D2030"));

    public static readonly ThemeColors Light = new(true,
        Hex("#F4F5F9"), Hex("#EBEDF3"), Hex("#FFFFFF"), Hex("#F1F2F7"), Hex("#DDE0E9"), Hex("#F1F2F7"), Hex("#D6D9E4"),
        Hex("#151722"), Hex("#4D5470"), Hex("#7C8299"), Hex("#6D4AFF"), Hex("#0891B2"), Hex("#059669"), Hex("#B7791F"), Hex("#DC2626"),
        Hex("#F7F8FB"), Hex("#F2F3F7"), Hex("#EDEFF4"), Hex("#FAFBFD"), Hex("#E3E6EE"), Hex("#7C8299"),
        Hex("#0A000000"), Hex("#18000000"), Hex("#F0FFFFFF"), Hex("#CCFFFFFF"), Hex("#F0FFFFFF"));

    public static Color Hex(string hex) => (Color)ColorConverter.ConvertFromString(hex);
}

/// <summary>グラフなど、コードで描く部品が使う今のテーマの色（ThemeManager.Apply で入れ替わる）</summary>
public static class ThemePalette
{
    public static ThemeColors Colors { get; private set; } = ThemeColors.Dark;
    public static Brush ChartBg { get; private set; } = Frozen(ThemeColors.Dark.ChartBg);
    public static Pen ChartGrid { get; private set; } = FrozenPen(ThemeColors.Dark.ChartGrid);
    public static Brush ChartLabel { get; private set; } = Frozen(ThemeColors.Dark.ChartLabel);
    public static Brush Text { get; private set; } = Frozen(ThemeColors.Dark.Text);
    public static Brush SubText { get; private set; } = Frozen(ThemeColors.Dark.SubText);
    public static Brush Track { get; private set; } = Frozen(ThemeColors.Dark.Track);
    public static Brush TipBg { get; private set; } = Frozen(ThemeColors.Dark.TipBg);
    public static Brush Input { get; private set; } = Frozen(ThemeColors.Dark.Input);
    public static Brush Card { get; private set; } = Frozen(ThemeColors.Dark.Card);
    public static Color Accent => Colors.Accent;
    public static Color Accent2 => Colors.Accent2;

    private static readonly Dictionary<string, object> Cache = new();

    /// <summary>今のテーマの色から作る筆・ペン（テーマを入れ替えるまで使い回す。描画のスレッドからだけ使う）</summary>
    public static T Cached<T>(string key, Func<ThemeColors, T> make) where T : class
    {
        lock (Cache)
        {
            if (Cache.TryGetValue(key, out var v) && v is T t) return t;
            var made = make(Colors);
            if (made is Freezable f && f.CanFreeze) f.Freeze();
            Cache[key] = made;
            return made;
        }
    }

    internal static void Set(ThemeColors c)
    {
        lock (Cache) Cache.Clear();
        Colors = c;
        ChartBg = Frozen(c.ChartBg);
        ChartGrid = FrozenPen(c.ChartGrid);
        ChartLabel = Frozen(c.ChartLabel);
        Text = Frozen(c.Text);
        SubText = Frozen(c.SubText);
        Track = Frozen(c.Track);
        TipBg = Frozen(c.TipBg);
        Input = Frozen(c.Input);
        Card = Frozen(c.Card);
    }

    public static Brush Frozen(Color c)
    {
        var b = new SolidColorBrush(c);
        b.Freeze();
        return b;
    }

    private static Pen FrozenPen(Color c)
    {
        var p = new Pen(Frozen(c), 1);
        p.Freeze();
        return p;
    }
}

public static class ThemeManager
{
    /// <summary>
    /// アプリの既定のフォント（英数字は Montserrat、日本語は ProFan Maru。足りない文字は Yu Gothic UI）。
    /// 中国語・韓国語・タイ語などの画面では、その言語のフォントを日本語のフォントより先に探す（漢字をその言語の字形にするため）
    /// </summary>
    public static string DefaultFont => $"pack://application:,,,/Assets/Fonts/#Montserrat, {UiFonts.LanguageFonts}pack://application:,,,/Assets/Fonts/#ProFan Maru, Yu Gothic UI";
    /// <summary>同梱の丸ゴシックだけ（英数字も ProFan Maru）</summary>
    public static string BundledMaru => $"pack://application:,,,/Assets/Fonts/#ProFan Maru, {UiFonts.LanguageFonts}Yu Gothic UI";
    /// <summary>フォントの選択肢で「同梱の丸ゴシック」を表す値</summary>
    public const string BundledMaruId = "ProFan Maru";

    /// <summary>テーマを入れ替えたとき（グラフなどは描き直す）</summary>
    public static event Action? Changed;

    public static ThemeColors Current { get; private set; } = ThemeColors.Dark;

    /// <summary>
    /// アクセント色の候補 64 色: 色相を 24° ずつ 15 色 × 明るさ 4 段（OKLCh で同じ鮮やかさにそろえる）＋ 灰色 4 段。
    /// 上の段ほど暗い（ライトテーマ向け）、下の段ほど明るい（ダークテーマ向け）
    /// </summary>
    public static readonly IReadOnlyList<string> AccentPalette = BuildPalette();

    private static List<string> BuildPalette()
    {
        var list = new List<string>(64);
        float[] lightness = { 0.52f, 0.62f, 0.72f, 0.82f };
        foreach (float l in lightness)
        {
            for (int k = 0; k < 15; k++)
            {
                float h = (float)(k * 24 * Math.PI / 180);
                var lab = OkLab.FitGamut(Lab.FromLch(l, 0.17f, h));
                list.Add(OkLab.ToRgb(lab).Hex);
            }
            list.Add(OkLab.ToRgb(Lab.FromLch(l - 0.04f, 0.01f, 4.5f)).Hex);   // 灰色（わずかに青み）
        }
        return list;
    }

    /// <summary>背景色の候補の数（色相 15 × 濃さ 4 段 ＋ 灰色 4 段）</summary>
    public const int BackgroundCount = 64;

    /// <summary>
    /// 背景色の候補（index 0〜63）。同じ番号でも、ダークなら暗い色・ライトなら明るい色になる（テーマを替えても色合いは同じ）。
    /// 16 個ずつ 4 段: 色相を 24° ずつ 15 色 ＋ 灰色。上の段ほど控えめ、下の段ほど色が濃い。
    /// 文字が読みにくくならないよう、明るさは各テーマの範囲に収め、鮮やかさも抑える
    /// </summary>
    public static Color BackgroundSwatch(int index, bool light)
    {
        index = Math.Clamp(index, 0, BackgroundCount - 1);
        int row = index / 16, col = index % 16;
        // 1 段目でも既定（ほぼ無彩色）との違いが分かるよう、いちばん控えめな段にも色をはっきりのせる
        float[] darkL = { 0.16f, 0.195f, 0.23f, 0.265f }, darkC = { 0.04f, 0.055f, 0.07f, 0.085f };
        float[] lightL = { 0.965f, 0.94f, 0.915f, 0.89f }, lightC = { 0.04f, 0.06f, 0.08f, 0.10f };
        float l = light ? lightL[row] : darkL[row];
        float c = col == 15 ? 0.004f : (light ? lightC[row] : darkC[row]);
        float h = (float)((col == 15 ? 260 : col * 24) * Math.PI / 180);
        var rgb = OkLab.ToRgb(OkLab.FitGamut(Lab.FromLch(l, c, h)));
        return Color.FromRgb(rgb.R, rgb.G, rgb.B);
    }

    /// <summary>
    /// 背景色の候補の見本に塗る色。実際の背景色は暗すぎ・淡すぎて見分けにくいので、同じ色相で、
    /// 段（控えめ → 濃い）ごとに淡い色からはっきりした色まで、アクセント色のように見える色にする（ダーク・ライト共通）
    /// </summary>
    public static Color BackgroundDisplay(int index)
    {
        index = Math.Clamp(index, 0, BackgroundCount - 1);
        int row = index / 16, col = index % 16;
        float[] l = { 0.80f, 0.73f, 0.66f, 0.58f }, c = { 0.085f, 0.11f, 0.135f, 0.16f };
        float chroma = col == 15 ? 0.006f : c[row];
        float h = (float)((col == 15 ? 260 : col * 24) * Math.PI / 180);
        var rgb = OkLab.ToRgb(OkLab.FitGamut(Lab.FromLch(l[row], chroma, h)));
        return Color.FromRgb(rgb.R, rgb.G, rgb.B);
    }

    /// <summary>背景色の設定値（"bg12" など）→ 候補の番号。既定・読めない値なら null</summary>
    public static int? BackgroundIndex(string? setting) => PaletteIndex(setting, AppearanceConfig.BackgroundPrefix);

    /// <summary>文字色の設定値（"tx12" など）→ 候補の番号。既定・読めない値なら null</summary>
    public static int? TextColorIndex(string? setting) => PaletteIndex(setting, AppearanceConfig.TextColorPrefix);

    private static int? PaletteIndex(string? setting, string prefix) =>
        setting is { } s && s.Length > prefix.Length && s.StartsWith(prefix, StringComparison.Ordinal)
        && int.TryParse(s.AsSpan(prefix.Length), out int i) && i >= 0 && i < BackgroundCount ? i : null;

    /// <summary>
    /// 文字色の候補（index 0〜63）。16 個ずつ 4 段: 色相を 24° ずつ 15 色 ＋ 灰色。上の段ほど白（黒）に近く、下の段ほど色が濃い。
    /// 同じ番号でも、ダークなら明るい色・ライトなら暗い色になる（どの背景の上でも読める明るさに収める）
    /// </summary>
    public static Color TextSwatch(int index, bool light)
    {
        index = Math.Clamp(index, 0, BackgroundCount - 1);
        int row = index / 16, col = index % 16;
        float[] darkL = { 0.95f, 0.91f, 0.87f, 0.83f }, lightL = { 0.33f, 0.38f, 0.43f, 0.47f };
        // ライトは暗い色ほど色合いが見えにくいので、明るさは白い背景に対して十分読める範囲（明暗の差 4.5:1 前後）まで上げ、鮮やかさも強める
        float[] darkC = { 0.035f, 0.065f, 0.095f, 0.125f }, lightC = { 0.08f, 0.11f, 0.14f, 0.16f };
        float l = light ? lightL[row] : darkL[row];
        float c = col == 15 ? 0.006f : (light ? lightC[row] : darkC[row]);
        float h = (float)((col == 15 ? 260 : col * 24) * Math.PI / 180);
        var rgb = OkLab.ToRgb(OkLab.FitGamut(Lab.FromLch(l, c, h)));
        return Color.FromRgb(rgb.R, rgb.G, rgb.B);
    }

    /// <summary>
    /// 文字色を替えたテーマ: ふつうの文字を選んだ色にし、補足の文字・薄い文字・グラフの目盛りの文字は、
    /// 選んだ色を背景へ寄せて作る（色合いはそろい、文字の濃淡の段差は元のテーマと同じくらいに保つ）
    /// </summary>
    public static ThemeColors WithText(ThemeColors b, Color text)
    {
        Color Mix(double towardBg)
        {
            var t = OkLab.FromRgb(new Rgb(text.R, text.G, text.B));
            var bg = OkLab.FromRgb(new Rgb(b.Card.R, b.Card.G, b.Card.B));
            float k = (float)towardBg;
            var rgb = OkLab.ToRgb(OkLab.FitGamut(new Lab(t.L + (bg.L - t.L) * k, t.A + (bg.A - t.A) * k, t.B + (bg.B - t.B) * k)));
            return Color.FromRgb(rgb.R, rgb.G, rgb.B);
        }
        return b with { Text = text, SubText = Mix(0.36), Muted = Mix(0.56), ChartLabel = Mix(0.48) };
    }

    /// <summary>
    /// 背景色を替えたテーマ: 面の色（背景・サイドバー・カード・枠・入力欄など）を、元のテーマとの明るさの差は保ったまま、
    /// 選んだ背景の色合い（OKLab の a・b）に染める。文字の色はそのまま（明るさの範囲を抑えてあるので読みやすさは変わらない）
    /// </summary>
    public static ThemeColors WithBackground(ThemeColors b, Color bg)
    {
        var target = OkLab.FromRgb(new Rgb(bg.R, bg.G, bg.B));
        var baseBg = OkLab.FromRgb(new Rgb(b.Bg.R, b.Bg.G, b.Bg.B));
        float dL = target.L - baseBg.L;
        Color Tint(Color c)
        {
            var lab = OkLab.FromRgb(new Rgb(c.R, c.G, c.B));
            var tinted = OkLab.FitGamut(new Lab(Math.Clamp(lab.L + dL, 0f, 1f), target.A, target.B));
            var rgb = OkLab.ToRgb(tinted);
            return Color.FromArgb(c.A, rgb.R, rgb.G, rgb.B);
        }
        return b with
        {
            Bg = bg, Sidebar = Tint(b.Sidebar), Card = Tint(b.Card), CardHover = Tint(b.CardHover), Border = Tint(b.Border),
            Input = Tint(b.Input), Track = Tint(b.Track), Row = Tint(b.Row), Inset = Tint(b.Inset), Inset2 = Tint(b.Inset2),
            ChartBg = Tint(b.ChartBg), ChartGrid = Tint(b.ChartGrid), WidgetBg = Tint(b.WidgetBg), Scrim = Tint(b.Scrim), TipBg = Tint(b.TipBg),
        };
    }

    /// <summary>Windows の「アプリ モード」がライトか（読めなければダーク）</summary>
    public static bool SystemUsesLight()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("AppsUseLightTheme") is int v && v != 0;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Windows のアクセント色（読めなければ null）</summary>
    public static Color? SystemAccentColor()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\DWM");
            if (key?.GetValue("AccentColor") is not int abgr) return null;
            uint v = unchecked((uint)abgr);
            return Color.FromRgb((byte)(v & 0xFF), (byte)((v >> 8) & 0xFF), (byte)((v >> 16) & 0xFF));
        }
        catch
        {
            return null;
        }
    }

    /// <summary>設定から、使うテーマの色を決める（アクセント色を選んでいれば、2 つ目の色はそこから色相をずらして作る）</summary>
    public static ThemeColors Resolve(AppearanceConfig a)
    {
        bool light = a.Theme == AppThemes.Light || (a.Theme == AppThemes.System && SystemUsesLight());
        var baseColors = light ? ThemeColors.Light : ThemeColors.Dark;
        if (BackgroundIndex(a.Background) is int bgIndex) baseColors = WithBackground(baseColors, BackgroundSwatch(bgIndex, light));
        if (TextColorIndex(a.TextColor) is int txIndex) baseColors = WithText(baseColors, TextSwatch(txIndex, light));
        Color? accent = a.Accent switch
        {
            AppearanceConfig.SystemAccent => SystemAccentColor(),
            AppearanceConfig.DefaultAccent => null,
            _ => TryHex(a.Accent),
        };
        if (accent is not Color c) return baseColors;
        return baseColors with { Accent = c, Accent2 = Shifted(c, 55) };
    }

    /// <summary>OKLCh で色相を deg 度ずらした色（明るさ・鮮やかさは同じ）</summary>
    public static Color Shifted(Color c, double deg)
    {
        var lab = OkLab.FromRgb(new Rgb(c.R, c.G, c.B));
        double chroma = Math.Sqrt(lab.A * lab.A + lab.B * lab.B);
        double hue = Math.Atan2(lab.B, lab.A) + deg * Math.PI / 180;
        var rgb = OkLab.ToRgb(OkLab.FitGamut(Lab.FromLch(lab.L, (float)Math.Max(chroma, 0.08), (float)hue)));
        return Color.FromRgb(rgb.R, rgb.G, rgb.B);
    }

    private static Color? TryHex(string s)
    {
        try { return (Color)ColorConverter.ConvertFromString(s); }
        catch { return null; }
    }

    /// <summary>フォントの設定 → 画面に使うフォント</summary>
    public static FontFamily FontFor(string? setting) => string.IsNullOrWhiteSpace(setting)
        ? new FontFamily(DefaultFont)
        : setting == BundledMaruId ? new FontFamily(BundledMaru) : new FontFamily($"{setting}, {UiFonts.LanguageFonts}Yu Gothic UI");

    /// <summary>
    /// 見た目を入れ替える: 色の資源（DynamicResource で参照しているもの）・フォント・開いている画面のタイトルバーと文字の大きさ。
    /// 画面を開く前（起動時）にも、開いたあと（設定を変えたとき）にも呼べる
    /// </summary>
    public static void Apply(AppearanceConfig a)
    {
        var app = Application.Current;
        if (app is null) return;
        var c = Resolve(a);
        Current = c;
        ThemePalette.Set(c);
        var r = app.Resources;
        void Put(string name, Color color)
        {
            r[name + "Color"] = color;
            r[name + "Brush"] = ThemePalette.Frozen(color);
        }
        Put("Bg", c.Bg);
        Put("Sidebar", c.Sidebar);
        Put("Card", c.Card);
        Put("CardHover", c.CardHover);
        Put("Border", c.Border);
        Put("Input", c.Input);
        Put("Track", c.Track);
        Put("Text", c.Text);
        Put("SubText", c.SubText);
        Put("Muted", c.Muted);
        Put("Accent", c.Accent);
        Put("Accent2", c.Accent2);
        Put("Ok", c.Ok);
        Put("Warn", c.Warn);
        Put("Danger", c.Danger);
        Put("Row", c.Row);
        Put("Inset", c.Inset);
        Put("Inset2", c.Inset2);
        Put("Overlay", c.Overlay);
        Put("Line", c.Line);
        Put("WidgetBg", c.WidgetBg);
        Put("Scrim", c.Scrim);
        // 淡い色（お知らせの枠・選ばれている印など）
        Put("AccentTint", WithAlpha(c.Accent, 0x15));
        Put("AccentLine", WithAlpha(c.Accent, 0x44));
        Put("AccentFill", WithAlpha(c.Accent, 0x33));
        Put("Accent2Tint", WithAlpha(c.Accent2, 0x15));
        Put("Accent2Line", WithAlpha(c.Accent2, 0x44));
        Put("WarnTint", WithAlpha(c.Warn, 0x15));
        Put("WarnLine", WithAlpha(c.Warn, 0x44));
        Put("WarnFill", WithAlpha(c.Warn, 0x33));
        Put("DangerTint", WithAlpha(c.Danger, 0x22));
        Put("DangerLine", WithAlpha(c.Danger, 0x55));
        var gradient = new LinearGradientBrush(c.Accent, c.Accent2, new Point(0, 0), new Point(1, 1));
        gradient.Freeze();
        r["AccentGradient"] = gradient;
        r["UiFont"] = FontFor(a.FontFamily);

        foreach (Window w in app.Windows) ApplyToWindow(w, a);
        Changed?.Invoke();
    }

    /// <summary>1 つの画面に、タイトルバーの色・文字の大きさを当て、コードで描く部品を描き直す</summary>
    public static void ApplyToWindow(Window w, AppearanceConfig a)
    {
        ApplyTitleBar(w);
        if (w.Content is FrameworkElement root)
        {
            double s = a.TextScale / 100.0;
            root.LayoutTransform = Math.Abs(s - 1) < 0.001 ? Transform.Identity : new ScaleTransform(s, s);
        }
        Redraw(w);
    }

    /// <summary>タイトルバーをテーマに合わせる（ダークモードのオン・オフと、Windows 11 ならタイトルバーの色）</summary>
    public static void ApplyTitleBar(Window w)
    {
        var hwnd = new WindowInteropHelper(w).Handle;
        if (hwnd == IntPtr.Zero) return;
        int dark = Current.IsLight ? 0 : 1;
        DwmSetWindowAttribute(hwnd, 20, ref dark, sizeof(int));        // DWMWA_USE_IMMERSIVE_DARK_MODE
        var bg = Current.Bg;
        int caption = bg.R | (bg.G << 8) | (bg.B << 16);               // COLORREF は BGR 順
        DwmSetWindowAttribute(hwnd, 35, ref caption, sizeof(int));     // DWMWA_CAPTION_COLOR（Windows 11 のみ有効）
    }

    private static void Redraw(DependencyObject d)
    {
        if (d is UIElement e) e.InvalidateVisual();
        int n = VisualTreeHelper.GetChildrenCount(d);
        for (int i = 0; i < n; i++) Redraw(VisualTreeHelper.GetChild(d, i));
    }

    public static Color WithAlpha(Color c, byte a) => Color.FromArgb(a, c.R, c.G, c.B);

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
}
