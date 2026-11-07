// ==================================================
// TuningResetTests.cs - 音楽連動の「反応のしかた」の初期値の表示と「初期値に戻す」のテスト
// Dependencies: .NET 8 (MIT License)
// See CREDITS.txt for full license details.
// ==================================================

using ProFanController;

namespace LogicTests;

public static class TuningResetTests
{
    public static void TestChangedValuesAreListedAndReset()
    {
        var cfg = new RgbConfig { Mode = RgbModes.Music };
        MusicTuning.ApplyPreset(cfg, MusicPresets.Groove);
        T.Check(MusicTuning.ChangedTuningSummary(cfg).Length == 0, "fresh preset: nothing changed");

        cfg.Sensitivity = 2;
        cfg.Music.Punch = 70;
        cfg.Music.DynamicRangeDb = 50;
        cfg.Music.AudioDelayMs = 180;
        cfg.Music.Preset = MusicPresets.Custom; // 画面では、値を変えると自動で「カスタム」になる
        string s = MusicTuning.ChangedTuningSummary(cfg);
        T.Check(s.Contains("反応の感度 2.0（初期 1.0）") && s.Contains("メリハリ 70%（初期 55%）") && s.Contains("明るさに使う音量の幅 50 dB（初期 40 dB）"),
            $"changed values listed with their defaults ({s})");
        T.Check(!s.Contains("遅れ"), "the audio delay is not listed (it is a per-speaker calibration)");

        MusicTuning.ResetTuning(cfg);
        T.Check(MusicTuning.ChangedTuningSummary(cfg).Length == 0, "after reset: nothing changed");
        T.Check(cfg.Sensitivity == 1 && cfg.Music.Punch == 55 && cfg.Music.DynamicRangeDb == 40, "values are back to the defaults");
        T.Check(cfg.Music.Preset == MusicPresets.Groove, $"the preset is selected again when everything matches ({cfg.Music.Preset})");
        T.Check(cfg.Music.AudioDelayMs == 180, "the audio delay is kept");
    }

    public static void TestDefaultsFollowTheIntensityAndCinema()
    {
        var cfg = new RgbConfig { Mode = RgbModes.Music };
        MusicTuning.ApplyPreset(cfg, MusicPresets.Groove);
        MusicTuning.ApplyIntensity(cfg.Music, Intensities.High);
        cfg.Music.Preset = MusicPresets.Custom;
        var hi = MusicTuning.Profile(Intensities.High);
        var smooth = MusicTuning.TuningValues(cfg).First(v => v.Label == "滑らかさ");
        T.Check(smooth.Default == hi.SmoothnessMs && !smooth.Changed, $"defaults of the intensity-linked values follow the chosen intensity ({smooth.Default} ms)");

        MusicTuning.ApplyPreset(cfg, MusicPresets.Cinema);
        var punch = MusicTuning.TuningValues(cfg).First(v => v.Label == "メリハリ");
        T.Check(punch.Default == MusicTuning.CinemaPunch && !punch.Changed, "cinema preset: its own punch counts as the default");
        cfg.Music.MinBrightness = 10;
        MusicTuning.ResetTuning(cfg);
        T.Check(cfg.Music.MinBrightness == MusicTuning.CinemaMinBrightness && cfg.Music.Preset == MusicPresets.Cinema,
            $"cinema: reset brings back the cinema values ({cfg.Music.MinBrightness}%, {cfg.Music.Preset})");

        // プリセットと違う光り方なら、戻しても「カスタム」のまま
        cfg.MusicStyle = MusicStyles.Sparkle;
        cfg.Music.Preset = MusicPresets.Custom;
        cfg.Music.Punch = 90;
        MusicTuning.ResetTuning(cfg);
        T.Check(cfg.Music.Preset == MusicPresets.Custom, "a style that no preset uses stays custom after the reset");
    }
}

public static class AdvisorOptionTests
{
    /// <summary>AI 冷却アドバイザーは実験的な機能: 既定はオフ。オンにしたら設定ファイルに残り、古い設定ファイル（項目がない）はオフ</summary>
    public static void TestAdvisorIsOptInAndSaved()
    {
        var cfg = new AppConfig();
        cfg.Normalize();
        T.Check(!cfg.Advisor.Enabled, "the advisor is off by default");
        cfg.Advisor.Enabled = true;
        var back = AppConfig.FromJson(cfg.ToJson());
        T.Check(back.Advisor.Enabled, "turning it on is saved");
        var old = AppConfig.FromJson("{\"Advisor\":{\"OllamaUrl\":\"http://localhost:11434\",\"Model\":\"qwen2.5:7b\"}}");
        T.Check(!old.Advisor.Enabled && old.Advisor.Model == "qwen2.5:7b", "an old config without the switch stays off and keeps its model");
    }
}
