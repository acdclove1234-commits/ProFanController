// ==================================================
// Palette.cs - 音楽連動の色の組み合わせ (パレット)。数色の「見本の色」の間を OKLab で滑らかにつないだ色の表
// Dependencies: .NET 8 (MIT License)
// See CREDITS.txt for full license details.
// ==================================================

namespace ProFanController;

/// <summary>
/// パレット = 数色の見本の色（ストップ）を、輪のようにつないだ色の表（見本 1 つあたり 64 色。見本 4 つで 256 色）。
///   位置 x（0〜1）で色を引く。x = k / 見本の数 のとき k 番目の見本の色。1 を超えると最初に戻る（x の小数部分だけを見る）
///   見本と見本の間は OKLab でつなぐので、途中で灰色っぽくくすまない
/// 表は (パレット名, 選んだ色) ごとに 1 回だけ作って使い回す（Get）。作った後は読むだけなので、どのスレッドから使ってもよい
/// </summary>
public sealed class ColorPalette
{
    /// <summary>見本 1 つあたりの表の色の数（見本 4 つで 256 色。見本の色がちょうど表に入るよう、見本の数の倍数にする）</summary>
    public const int EntriesPerStop = 64;
    /// <summary>レインボーで、ビートごとに色相を進める角度（度）</summary>
    public const double RainbowHueStepDeg = 70;
    /// <summary>音量メーター用（緑 → 黄 → 赤）。設定画面では選べない</summary>
    public const string MeterName = "Meter";

    /// <summary>Mono（選んだ色）の両隣の色: 色相を ±25°、明るさを ±0.08 ずらす</summary>
    private const float MonoHueShiftDeg = 25f;
    private const float MonoLightShift = 0.08f;

    private static readonly Dictionary<string, string[]> Stops = new()
    {
        [Palettes.Neon] = new[] { "#FF0080", "#7C3AED", "#00E5FF", "#00FF85" },
        [Palettes.Sunset] = new[] { "#FF3D00", "#FF8A00", "#FFC400", "#FF006E" },
        [Palettes.Ocean] = new[] { "#0033FF", "#0091FF", "#00E5D1", "#7CFFCB" },
        [Palettes.Fire] = new[] { "#FF0000", "#FF4500", "#FF9F00", "#FFD000" },
        [Palettes.Vaporwave] = new[] { "#FF71CE", "#01CDFE", "#05FFA1", "#B967FF" },
        [Palettes.Forest] = new[] { "#00FF41", "#7CFC00", "#00B36B", "#2EFFC7" },
        [Palettes.Ice] = new[] { "#A0E9FF", "#00B4FF", "#5E60FF" },
        [Palettes.Rainbow] = new[] { "#FF0000", "#FFFF00", "#00FF00", "#00FFFF", "#0000FF", "#FF00FF" },
        [MeterName] = new[] { "#34D399", "#FBBF24", "#FF3B30" },
    };

    private static readonly Dictionary<(string Name, string Color), ColorPalette> Cache = new();
    private static readonly object CacheGate = new();

    private readonly Lab[] _lab;
    private readonly LinColor[] _ledGamma;
    private readonly LinColor[] _ledFlat;

    private ColorPalette(string name, Lab[] stops)
    {
        Name = name;
        StopCount = stops.Length;
        StopColors = new Rgb[stops.Length];
        for (int k = 0; k < stops.Length; k++) StopColors[k] = OkLab.ToRgb(stops[k]);
        int n = stops.Length;
        Size = n * EntriesPerStop;
        _lab = new Lab[Size];
        _ledGamma = new LinColor[Size];
        _ledFlat = new LinColor[Size];
        for (int i = 0; i < Size; i++)
        {
            // 見本の k 番目と k+1 番目（最後の次は最初）の間を OKLab の直線でつなぐ
            int k = i / EntriesPerStop;
            float f = (i % EntriesPerStop) / (float)EntriesPerStop;
            var lab = Lab.Lerp(stops[k % n], stops[(k + 1) % n], f);
            _lab[i] = lab;
            OkLab.ToEncoded(lab, out float r, out float g, out float b);
            _ledGamma[i] = new LinColor(OutputStage.Lin(r, true), OutputStage.Lin(g, true), OutputStage.Lin(b, true));
            _ledFlat[i] = new LinColor(r, g, b);
        }
    }

    /// <summary>表の色の数（見本の数 × 64）</summary>
    public int Size { get; }

    /// <summary>パレット名（Palettes の値、または MeterName）</summary>
    public string Name { get; }
    /// <summary>見本の色の数</summary>
    public int StopCount { get; }
    /// <summary>見本の色（画面の色。設定画面の色見本用）</summary>
    public Rgb[] StopColors { get; }

    /// <summary>
    /// 最初の見本から最後の見本までの幅（= (見本の数 − 1) / 見本の数）。
    /// 「低音 → 高音」のように端から端へ並べるときは x × Span で引く（x = 1 で最後の見本。最初の色に戻らない）
    /// </summary>
    public double Span => (StopCount - 1) / (double)StopCount;

    /// <summary>ビート 1 回分で色を進める量（ふつうは見本 1 つ分。レインボーは色相 70° 分）</summary>
    public double Step => Name == Palettes.Rainbow ? RainbowHueStepDeg / 360 : 1.0 / StopCount;

    /// <summary>位置 x の色（LED に送る光の量。ガンマ補正済み）。x は何でもよい（小数部分だけを見る）</summary>
    public LinColor At(double x, bool gamma)
    {
        var lut = gamma ? _ledGamma : _ledFlat;
        Locate(x, out int i, out int j, out float f);
        return LinColor.Lerp(lut[i], lut[j], f);
    }

    /// <summary>位置 x の色（OKLab。クロスフェードや白との混ぜ合わせに使う）</summary>
    public Lab LabAt(double x)
    {
        Locate(x, out int i, out int j, out float f);
        return Lab.Lerp(_lab[i], _lab[j], f);
    }

    /// <summary>位置 x の色（画面の色。設定画面のプレビュー用）</summary>
    public Rgb ScreenAt(double x) => OkLab.ToRgb(LabAt(x));

    private void Locate(double x, out int i, out int j, out float f)
    {
        double u = x - Math.Floor(x);   // 小数部分（負の数でも 0〜1）
        if (!(u >= 0 && u < 1)) u = 0;  // NaN・無限大は最初の色にする
        double p = u * Size;
        i = (int)p;
        if (i >= Size) i = Size - 1;
        f = (float)(p - i);
        j = i + 1 == Size ? 0 : i + 1;
    }

    /// <summary>
    /// パレットを取り出す（初めてのときだけ作る）。name が分からなければレインボー。
    /// staticColor は Mono（選んだ色）のときだけ使う（"#RRGGBB"）
    /// </summary>
    public static ColorPalette Get(string? name, string? staticColor)
    {
        string n = name is not null && (Stops.ContainsKey(name) || Palettes.UsesStaticColor(name)) ? name : Palettes.Rainbow;
        string key = Palettes.UsesStaticColor(n) ? (staticColor ?? "") : "";
        lock (CacheGate)
        {
            if (Cache.TryGetValue((n, key), out var p)) return p;
            p = n == Palettes.Mono ? BuildMono(Rgb.Parse(staticColor))
                : n == Palettes.Solid ? BuildSolid(Rgb.Parse(staticColor))
                : new ColorPalette(n, Parse(Stops[n]));
            Cache[(n, key)] = p;
            return p;
        }
    }

    /// <summary>選んだ色だけのパレット（同じ色の見本 2 つ）。どの位置を引いても選んだ色なので、光り方は明るさだけで音に合わせる</summary>
    private static ColorPalette BuildSolid(Rgb color)
    {
        var c = OkLab.FromRgb(color);
        return new ColorPalette(Palettes.Solid, new[] { c, c });
    }

    private static ColorPalette? _meter;

    /// <summary>音量メーター用のパレット（緑 → 黄 → 赤）</summary>
    public static ColorPalette Meter => _meter ??= Get(MeterName, null);

    private static Lab[] Parse(string[] hex)
    {
        var stops = new Lab[hex.Length];
        for (int i = 0; i < hex.Length; i++) stops[i] = OkLab.FromRgb(Rgb.Parse(hex[i]));
        return stops;
    }

    /// <summary>
    /// 選んだ色から作るパレット: [選んだ色, 色相 +25°・少し明るく, 色相 −25°・少し暗く]。
    /// 位置 0 が選んだ色そのものなので、パルスなどは選んだ色で光る。表せない色は鮮やかさを下げて収める
    /// </summary>
    private static ColorPalette BuildMono(Rgb color)
    {
        var c = OkLab.FromRgb(color);
        float l = c.L, ch = c.Chroma, h = c.Hue;
        float dh = MonoHueShiftDeg * MathF.PI / 180;
        var stops = new[]
        {
            c,
            OkLab.FitGamut(Lab.FromLch(Math.Clamp(l + MonoLightShift, 0.05f, 0.98f), ch, h + dh)),
            OkLab.FitGamut(Lab.FromLch(Math.Clamp(l - MonoLightShift, 0.05f, 0.98f), ch, h - dh)),
        };
        return new ColorPalette(Palettes.Mono, stops);
    }
}
