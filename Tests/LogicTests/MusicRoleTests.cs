// ==================================================
// MusicRoleTests.cs - 音楽連動の「パーツごとの役割」「選んだ色だけ」と、音ごとに見分けやすくした「きらめき」のテスト
// Dependencies: .NET 8 (MIT License)
// See CREDITS.txt for full license details.
// ==================================================

using ProFanController;

namespace LogicTests;

public static class MusicRoleTests
{
    private static RgbDevice Board() => new(0, "ASRock B860", 34, RgbDevice.TypeMotherboard, new[] { 1, 8, 8, 8, 9 }, "HID: test")
    {
        ZoneNames = new[] { "RGB LED 1 Header", "Addressable Header 1", "Addressable Header 2", "Addressable Header 3", "PCB" },
    };

    private sealed class Run
    {
        public readonly DeviceRenderer R;
        public readonly RenderInput In = new();
        private readonly Rgb[] _out;
        private double _last = double.NaN;

        public Run(RgbDevice dev, ResolvedMusic m, RgbDeviceSettings ds)
        {
            R = new DeviceRenderer(dev);
            _out = new Rgb[dev.LedCount];
            In.Cfg = new RgbConfig { Mode = RgbModes.Music, Brightness = 100, Gamma = false, StaticColor = "#FF0000" };
            In.Music = m;
            In.Device = ds;
            In.Frame.GateOpen = true;
            In.Frame.Activity = 1;
        }

        public void Step(double t, params AudioEvent[] events)
        {
            In.T = In.TEval = In.Frame.T = t;
            In.TPrevEval = double.IsNaN(_last) ? t : _last;
            In.Dt = double.IsNaN(_last) ? 0 : t - _last;
            In.Events.Clear();
            In.Events.AddRange(events);
            R.Render(In, _out);
            _last = t;
        }

        /// <summary>LED [from, from+n) の明るさ（いちばん強い色の成分）の平均</summary>
        public float Avg(int from, int n)
        {
            var l = R.LastLight;
            float s = 0;
            for (int i = from; i < from + n; i++) s += l[i].MaxChannel;
            return s / n;
        }
    }

    public static void TestRoleDefaults()
    {
        var board = Board();
        T.Check(MusicSounds.Resolve(null, board, "Addressable Header 1", null) == MusicRole.Bass, "motherboard ARGB header (fans) → bass");
        T.Check(MusicSounds.Resolve(null, board, "PCB", null) == MusicRole.Mid, "motherboard PCB lighting → mid");
        var gpu = new RgbDevice(1, "GPU", 6, RgbDevice.TypeGpu, null, "I2C: x");
        var ram = new RgbDevice(2, "RAM", 8, RgbDevice.TypeDram, null, "I2C: y");
        var kb = new RgbDevice(3, "Keyboard", 100, RgbDevice.TypeKeyboard, null, "HID: z");
        T.Check(MusicSounds.Resolve(MusicSounds.Auto, gpu, null, null) == MusicRole.Mid, "GPU → mid");
        T.Check(MusicSounds.Resolve(MusicSounds.Auto, ram, null, null) == MusicRole.Treble, "memory → treble");
        T.Check(MusicSounds.Resolve(MusicSounds.Auto, kb, null, null) == MusicRole.Treble, "keyboard → treble");
        T.Check(MusicSounds.Resolve(null, gpu, null, "front-3-up") == MusicRole.Bass, "a zone placed as front fans → bass (place wins over device type)");
        T.Check(MusicSounds.Resolve(MusicSounds.All, board, "Addressable Header 1", "front-top") == MusicRole.All, "an explicit choice wins");
        T.Check(MusicSounds.Normalize("bogus") == MusicSounds.Auto, "unknown value → Auto");
    }

    public static void TestRoleEffectLightsTheRightZone()
    {
        // 端子 1 = 低音（自動）、端子 2 = 高音（指定）
        var ds = new RgbDeviceSettings { Name = "ASRock B860" };
        ds.Zones.Add(new RgbZoneSettings { Name = "Addressable Header 2", Sound = MusicSounds.Treble });
        var m = ResolvedMusic.Default with { Effect = MusicStyles.Role, Palette = Palettes.Neon, MinBrightness = 0, TempoMode = TempoModes.Off };
        var r = new Run(Board(), m, ds);
        r.Step(1.0);
        r.Step(1.5);
        float bass0 = r.Avg(1, 8), treble0 = r.Avg(9, 8);
        r.Step(1.51, new AudioEvent(1, AudioEventKind.Kick, 1.51, 2f));
        float bassK = r.Avg(1, 8), trebleK = r.Avg(9, 8);
        T.Check(bassK > bass0 + 0.3f && trebleK < treble0 + 0.05f, $"kick lights the bass zone only (bass {bass0:F2} → {bassK:F2}, treble {treble0:F2} → {trebleK:F2})");
        r.Step(2.5);
        float bass1 = r.Avg(1, 8), treble1 = r.Avg(9, 8);
        r.Step(2.51, new AudioEvent(2, AudioEventKind.Hat, 2.51, 2f));
        float bassH = r.Avg(1, 8), trebleH = r.Avg(9, 8);
        T.Check(trebleH > treble1 + 0.3f && bassH < bass1 + 0.05f, $"hi-hat lights the treble zone only (treble {treble1:F2} → {trebleH:F2}, bass {bass1:F2} → {bassH:F2})");
        r.Step(3.5);
        T.Check(r.Avg(9, 8) < 0.1f && r.Avg(1, 8) < 0.1f, "both fade out when quiet");
    }

    public static void TestSolidPalette()
    {
        var p = ColorPalette.Get(Palettes.Solid, "#FF0000");
        bool same = true;
        var first = p.ScreenAt(0);
        for (double x = 0; x < 1; x += 0.05) same &= p.ScreenAt(x) == first;
        T.Check(same && first.R > 240 && first.G < 20 && first.B < 20, $"solid palette: every position is the chosen colour ({first})");
        T.Check(!ReferenceEquals(p, ColorPalette.Get(Palettes.Solid, "#00FF00")), "solid palette depends on the chosen colour");
        T.Check(Palettes.UsesStaticColor(Palettes.Solid) && Palettes.UsesStaticColor(Palettes.Mono) && !Palettes.UsesStaticColor(Palettes.Neon), "which palettes use the chosen colour");

        // 選んだ色だけ: ビートで色替えでも色は変わらない（明るさだけ）
        var m = ResolvedMusic.Default with { Effect = MusicStyles.BeatHue, Palette = Palettes.Solid, MinBrightness = 0, TempoMode = TempoModes.Off };
        var r = new Run(new RgbDevice(5, "Strip", 10, 4, null, "HID: s"), m, new RgbDeviceSettings { Name = "Strip" });
        bool red = true;
        for (int i = 0; i < 20; i++)
        {
            double t = 1 + i * 0.25;
            r.Step(t, new AudioEvent(i + 1, AudioEventKind.Kick, t, 2f));
            var c = r.R.LastLight[0];
            if (c.MaxChannel > 0.05f) red &= c.R >= c.MaxChannel - 1e-4f && c.G < 0.1f * c.R && c.B < 0.1f * c.R;
        }
        T.Check(red, "beat-hue with the solid palette stays red on every beat");
    }

    public static void TestSparkleSoundsLookDifferent()
    {
        var m = ResolvedMusic.Default with { Effect = MusicStyles.Sparkle, Palette = Palettes.Neon, MinBrightness = 0, TempoMode = TempoModes.Off };
        var strip = new RgbDevice(5, "Strip", 100, 4, null, "HID: s");
        var ds = new RgbDeviceSettings { Name = "Strip", Mirror = false };

        // キック: ゾーン全体がそろって明るくなる
        var r = new Run(strip, m, ds);
        r.In.Frame.MidE = 0;
        r.Step(1.0);
        float before = r.Avg(0, 100);
        r.Step(1.01, new AudioEvent(1, AudioEventKind.Kick, 1.01, 2f));
        float after = r.Avg(0, 100);
        T.Check(after > before * 2 && after > 0.15f, $"kick: the whole zone swells ({before:F2} → {after:F2})");

        // スネア: 続けて並んだ LED がまとまって光る
        var r2 = new Run(strip, m, ds);
        r2.In.Frame.MidE = 0;
        r2.Step(5.0);
        r2.Step(5.01, new AudioEvent(1, AudioEventKind.Snare, 5.01, 2f));
        var lit = Enumerable.Range(0, 100).Where(i => r2.R.LastLight[i].MaxChannel > 0.3f).ToList();
        int runs = lit.Count == 0 ? 0 : 1 + lit.Zip(lit.Skip(1), (a, b) => b - a > 1 ? 1 : 0).Sum();
        T.Check(lit.Count >= 8 && runs <= 2, $"snare: one block of neighbouring LEDs ({lit.Count} lit in {runs} block(s))");

        // ハイハット: 離れた白っぽい粒
        var r3 = new Run(strip, m, ds);
        r3.In.Frame.MidE = 0;
        r3.Step(9.0);
        r3.Step(9.01, new AudioEvent(1, AudioEventKind.Hat, 9.01, 2f));
        var dots = Enumerable.Range(0, 100).Where(i => r3.R.LastLight[i].MaxChannel > 0.3f).Select(i => r3.R.LastLight[i]).ToList();
        bool whitish = dots.All(c => Math.Min(c.R, Math.Min(c.G, c.B)) > 0.25f * c.MaxChannel);
        T.Check(dots.Count is >= 1 and <= 8 && whitish, $"hi-hat: a few whitish dots ({dots.Count})");
    }
}
