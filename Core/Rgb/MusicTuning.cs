// ==================================================
// MusicTuning.cs - 音楽連動の「プリセット」「強さ」と、設定から実際の値を決める計算
// Dependencies: .NET 8 (MIT License)
// See CREDITS.txt for full license details.
// ==================================================

namespace ProFanController;

public static class Intensities
{
    public const string Subtle = "Subtle";
    public const string Moderate = "Moderate";
    public const string High = "High";
    public const string Intense = "Intense";
    public static readonly string[] All = { Subtle, Moderate, High, Intense };

    public static string Label(string id) => id switch
    {
        Subtle => "控えめ",
        Moderate => "ふつう",
        High => "強め",
        Intense => "激しい",
        _ => id,
    };
}

/// <summary>色の組み合わせ（パレット）の名前。中身の色は Palette.cs</summary>
public static class Palettes
{
    public const string Rainbow = "Rainbow";
    public const string Neon = "Neon";
    public const string Sunset = "Sunset";
    public const string Ocean = "Ocean";
    public const string Fire = "Fire";
    public const string Vaporwave = "Vaporwave";
    public const string Forest = "Forest";
    public const string Ice = "Ice";
    /// <summary>選んだ色（StaticColor）と、その両隣の色</summary>
    public const string Mono = "Mono";
    public static readonly string[] All = { Rainbow, Neon, Sunset, Ocean, Fire, Vaporwave, Forest, Ice, Mono };

    public static string Label(string id) => id switch
    {
        Rainbow => "レインボー",
        Neon => "ネオン",
        Sunset => "サンセット",
        Ocean => "オーシャン",
        Fire => "ファイア",
        Vaporwave => "ヴェイパーウェイヴ",
        Forest => "フォレスト",
        Ice => "アイス",
        Mono => "選んだ色",
        _ => id,
    };
}

public static class MusicPresets
{
    public const string Chill = "Chill";
    public const string Groove = "Groove";
    public const string Party = "Party";
    public const string Club = "Club";
    public const string Visualizer = "Visualizer";
    public const string Cinema = "Cinema";
    public const string Custom = "Custom";
    public static readonly string[] All = { Chill, Groove, Party, Club, Visualizer, Cinema, Custom };

    public static string Label(string id) => id switch
    {
        Chill => "チル（ゆったり）",
        Groove => "グルーヴ（標準）",
        Party => "パーティー",
        Club => "クラブ（激しい）",
        Visualizer => "ビジュアライザー",
        Cinema => "映画・ゲーム",
        Custom => "カスタム",
        _ => id,
    };
}

public static class IdleEffects
{
    public const string Mood = "Mood";
    public const string Breathing = "Breathing";
    public const string Static = "Static";
    public const string Off = "Off";
    public static readonly string[] All = { Mood, Breathing, Static, Off };

    public static string Label(string id) => id switch
    {
        Mood => "ゆっくり色が変わる",
        Breathing => "呼吸",
        Static => "固定色",
        Off => "消灯",
        _ => id,
    };
}

/// <summary>
/// 「強さ」ごとの値。
/// ColorStepBeats = 色を次へ進める間隔（拍）。CrossfadeMs = 色が切り替わるのにかける時間
/// </summary>
public sealed record IntensityProfile(
    string Name, double ReactionMs, double SmoothnessMs, double MinBrightness, double FlashAmp, double Punch,
    int ColorStepBeats, double CrossfadeMs, bool DownbeatAccent, bool StrobeAllowed);

/// <summary>LED の描画で使う、音楽連動の設定を決めたもの（1 コマごとに作り直さない。設定が変わったときだけ作る）</summary>
/// <param name="Effect">光り方（MusicStyles）</param>
/// <param name="MinBrightness">いちばん暗いときの明るさ（0〜0.4。ガンマ補正の前の値）</param>
/// <param name="FlashAmp">ビートの光の強さ（0〜1）</param>
/// <param name="ColorStepBeats">色を次へ進める間隔（拍。1 / 2 / 4）</param>
/// <param name="CrossfadeMs">色の切り替えにかける時間（ミリ秒）</param>
/// <param name="StrobeAllowed">ストロボを使ってよい（強さ「激しい」で、点滅を抑える設定がオフ）</param>
/// <param name="AudioDelayMs">音の遅れの補正（ミリ秒。+ で LED を遅らせる）</param>
/// <param name="Speed">流れる速さの倍率（RgbConfig.Speed）</param>
public sealed record ResolvedMusic(
    string Effect, string Intensity, string Palette, double MinBrightness, double FlashAmp, int ColorStepBeats,
    double CrossfadeMs, bool DownbeatAccent, bool StrobeAllowed, bool ReduceFlashing, double AudioDelayMs,
    string IdleEffect, double IdleDelaySec, string TempoMode, double Speed)
{
    public static readonly ResolvedMusic Default = MusicTuning.Resolve(new RgbConfig());
}

public static class MusicTuning
{
    private static readonly IntensityProfile[] Profiles =
    {
        new(Intensities.Subtle, 40, 600, 35, 0.2, 30, 4, 600, false, false),
        new(Intensities.Moderate, 10, 250, 20, 0.5, 50, 2, 250, false, false),
        new(Intensities.High, 4, 160, 12, 0.85, 65, 1, 80, false, false),
        new(Intensities.Intense, 0, 90, 4, 1.0, 80, 1, 0, true, true),
    };

    public static IntensityProfile Profile(string intensity) =>
        Profiles.FirstOrDefault(p => p.Name == intensity) ?? Profiles[1];

    /// <summary>「強さ」を選んだとき: 反応の速さ・滑らかさ・メリハリ・いちばん暗いときの明るさを、その強さの値にする</summary>
    public static void ApplyIntensity(MusicConfig m, string intensity)
    {
        var p = Profile(intensity);
        m.Intensity = p.Name;
        m.ReactionMs = p.ReactionMs;
        m.SmoothnessMs = p.SmoothnessMs;
        m.Punch = p.Punch;
        m.MinBrightness = p.MinBrightness;
    }

    /// <summary>プリセットを選んだとき: 光り方・強さ・パレットなどをまとめて書き換える</summary>
    public static void ApplyPreset(RgbConfig cfg, string preset)
    {
        var m = cfg.Music;
        (string effect, string intensity, string palette) = preset switch
        {
            MusicPresets.Chill => (MusicStyles.Mood, Intensities.Subtle, Palettes.Ocean),
            MusicPresets.Groove => (MusicStyles.Pulse, Intensities.Moderate, Palettes.Neon),
            MusicPresets.Party => (MusicStyles.Ripple, Intensities.High, Palettes.Vaporwave),
            MusicPresets.Club => (MusicStyles.BeatHue, Intensities.Intense, Palettes.Neon),
            MusicPresets.Visualizer => (MusicStyles.Spectrum, Intensities.High, Palettes.Rainbow),
            MusicPresets.Cinema => (MusicStyles.Pulse, Intensities.Subtle, Palettes.Fire),
            _ => (cfg.MusicStyle, m.Intensity, m.Palette),
        };
        cfg.MusicStyle = effect;
        ApplyIntensity(m, intensity);
        m.Palette = palette;
        if (preset == MusicPresets.Cinema)
        {
            m.Punch = 30;
            m.MinBrightness = 40;
        }
        m.Preset = MusicPresets.All.Contains(preset) ? preset : MusicPresets.Custom;
    }

    /// <summary>分析エンジンの設定</summary>
    public static AudioEngineSettings EngineSettings(RgbConfig cfg)
    {
        var m = cfg.Music ?? new MusicConfig();
        return new AudioEngineSettings(
            GainDb: Math.Clamp(m.GainDb, -12, 12),
            DynamicRangeDb: Math.Clamp(m.DynamicRangeDb, 24, 60),
            AutoBalance: Math.Clamp(m.AutoBalance, 0, 100) / 100,
            TrebleTilt: Math.Clamp(m.TrebleTilt, 0, 4.5),
            Contrast: Math.Clamp(m.Contrast, 0.7, 2.0),
            ReactionMs: Math.Clamp(m.ReactionMs, 0, 40),
            SmoothnessMs: Math.Clamp(m.SmoothnessMs, 60, 600),
            Punch: Math.Clamp(m.Punch, 0, 100) / 100,
            BeatSensitivity: Math.Clamp(cfg.Sensitivity, 0.5, 3),
            TempoMode: m.TempoMode,
            TempoFactor: m.TempoFactor);
    }

    /// <summary>LED の描画で使う値</summary>
    public static ResolvedMusic Resolve(RgbConfig cfg)
    {
        var m = cfg.Music ?? new MusicConfig();
        var p = Profile(m.Intensity);
        bool reduce = m.ReduceFlashing;
        return new ResolvedMusic(
            Effect: MusicStyles.All.Contains(cfg.MusicStyle) ? cfg.MusicStyle : MusicStyles.BeatHue,
            Intensity: p.Name,
            Palette: Palettes.All.Contains(m.Palette) ? m.Palette : Palettes.Rainbow,
            MinBrightness: Math.Clamp(m.MinBrightness, 0, 40) / 100,
            FlashAmp: p.FlashAmp,
            ColorStepBeats: p.ColorStepBeats,
            CrossfadeMs: p.CrossfadeMs,
            DownbeatAccent: p.DownbeatAccent,
            StrobeAllowed: p.StrobeAllowed && !reduce,
            ReduceFlashing: reduce,
            AudioDelayMs: Math.Clamp(m.AudioDelayMs, -100, 400),
            IdleEffect: IdleEffects.All.Contains(m.IdleEffect) ? m.IdleEffect : IdleEffects.Mood,
            IdleDelaySec: Math.Clamp(m.IdleDelaySec, 1, 10),
            TempoMode: m.TempoMode,
            Speed: Math.Clamp(cfg.Speed, 0.3, 10));
    }

    /// <summary>光り方の表示名と説明（設定画面・タスクトレイ用）</summary>
    public static (string Label, string Description) Describe(string effect) => effect switch
    {
        MusicStyles.BeatHue => ("ビートで色替え", "ビートのたびに次の色へ切り替わり、ぱっと光って消えていく"),
        MusicStyles.Pulse => ("パルス", "選んだ色が低音に合わせて脈打ち、ふわっと残光が残る"),
        MusicStyles.Mix => ("ミックス", "低音・中音・高音の強さで色が混ざる"),
        MusicStyles.Meter => ("音量メーター", "音量の分だけ端から点き、ピークの点がゆっくり落ちてくる"),
        MusicStyles.Spectrum => ("スペクトラム", "LED の並びに低音〜高音を並べ、それぞれの強さで光る"),
        MusicStyles.Energy => ("エナジー", "低音・中音・高音の帯が、それぞれの強さの分だけ伸びる"),
        MusicStyles.Scroll => ("流れる波", "今の音の色が端から流れていく（ウォーターフォール）"),
        MusicStyles.Ripple => ("波紋", "キックのたびに光の輪が広がる"),
        MusicStyles.Sparkle => ("きらめき", "ハイハットやスネアで、ランダムな LED がきらっと光る"),
        MusicStyles.Chase => ("テンポ追従", "ビートに合わせて光の帯が走る"),
        MusicStyles.Mood => ("ムード", "曲の雰囲気で色がゆっくり変わる（点滅しない）"),
        MusicStyles.Strobe => ("ストロボ", "ビートで強く点滅する（「激しい点滅を抑える」をオフにしたときだけ）"),
        _ => (effect, ""),
    };
}
