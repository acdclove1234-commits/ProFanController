// ==================================================
// TemperatureCalmTests.cs - 温度モードのちかちか対策（温度のならし・グラボの書き込み回数・デバイス間のそろい）
// Dependencies: .NET 8 (MIT License)
// See CREDITS.txt for full license details.
// ==================================================

using ProFanController;

namespace LogicTests;

public static class TemperatureCalmTests
{
    private static RgbDevice Pny() => new(2, "PNY GeForce RTX 5070Ti ARGB Epic-X OC", 41, RgbDevice.TypeGpu, new[] { 4, 20, 17 }, "I2C: NVIDIA, address 0x60", "Direct", 7);
    private static RgbDevice Rx() => new(3, "ASRock Radeon RX 7900 XTX Phantom Gaming", 4, RgbDevice.TypeGpu, new[] { 1, 1, 1, 1 }, "I2C", "Direct", 4);
    private static RgbDevice Board() => new(0, "ASRock B860 Steel Legend WiFi", 34, 0, null, "HID: x", "Direct", 3);

    public static void TestTemperatureFilter()
    {
        // 1 秒ごとに 60/66 °C を行き来しても、表示の温度はほとんど動かない
        var f = new RgbTemperatureFilter();
        double? first = null; int changes = 0; double? prev = null;
        for (int s = 0; s < 120; s++)
        {
            var v = f.Push(s % 2 == 0 ? 60 : 66, s);
            if (s >= 20) { first ??= v; if (prev is not null && v != prev) changes++; prev = v; }
        }
        T.Check(changes <= 1, $"60/66 °C alternating: display changes {changes} times in 100 s");
        // 45 °C で 1 秒だけ +15 °C 跳ねても変わらない
        var g = new RgbTemperatureFilter();
        double? before = null, after = null;
        for (int s = 0; s < 40; s++) { var v = g.Push(s == 20 ? 60 : 45, s); if (s == 19) before = v; if (s == 30) after = v; }
        T.Check(before == 45 && after == 45, $"a 1-s spike is ignored ({before} → {after})");
        // 本当に上がったら 30 秒以内に追いつく（ヒステリシスの幅 1.5 °C まで）
        var h = new RgbTemperatureFilter();
        double? end = null;
        for (int s = 0; s < 50; s++) end = h.Push(s < 10 ? 45 : 75, s);
        T.Check(end >= 73.5, $"a real rise is followed ({end} °C 40 s after 45 → 75)");
        // 読めない値（127 °C）は捨てる・30 秒までは前の値を保つ
        var k = new RgbTemperatureFilter();
        k.Push(50, 0);
        T.Check(k.Push(127, 1) == 50 && k.Push(null, 29) == 50 && k.Push(null, 31) is null, "placeholder ignored, value held 30 s, then null");
    }

    public static void TestGpuWritesRarelyInTemperatureMode()
    {
        var cfg = new RgbConfig { Mode = RgbModes.Temperature };
        var f = new RgbTemperatureFilter();
        var pny = new RgbDeviceChannel(Pny(), 0);
        var rx = new RgbDeviceChannel(Rx(), 0);
        var board = new RgbDeviceChannel(Board(), 0);
        var all = new[] { board, pny, rx };
        foreach (var ch in all) ch.Configure(cfg, MusicTuning.Resolve(cfg), new RgbDeviceSettings { Name = ch.Device.Name }, false);
        var sends = new int[3];
        double? temp = null;
        int worst = 0;
        for (int k = 0; k < 1200; k++)   // 120 s, 10 コマ／秒
        {
            double t = k * 0.1;
            if (k % 10 == 3) temp = f.Push((k / 10) % 2 == 0 ? 60 : 66, t);
            for (int i = 0; i < 3; i++)
            {
                var ch = all[i];
                ch.Prepare(t, 0.1, null, null, BeatState.None, temp, false, false);
                if (!ch.Render()) continue;
                var reason = ch.ShouldSend(t, 0.1, false);
                if (reason == SendReason.None) continue;
                ch.MarkSent(t, 0.1, reason, 1);
                sends[i]++;
            }
            if (t > 20)
            {
                var a = board.CopyLastSent()![0]; var b = pny.CopyLastSent()![0]; var c = rx.CopyLastSent()![0];
                worst = Math.Max(worst, Math.Max(Math.Abs(a.R - b.R) + Math.Abs(a.G - b.G) + Math.Abs(a.B - b.B), Math.Abs(b.R - c.R) + Math.Abs(b.G - c.G) + Math.Abs(b.B - c.B)));
            }
        }
        T.Info($"temperature mode, 60/66 °C alternating for 120 s: board {sends[0]}, PNY {sends[1]}, RX {sends[2]} writes; worst colour difference {worst}");
        T.Check(sends[1] <= 6 && sends[2] <= 6, "GPUs are written only a few times (was ~540 / ~120 in 120 s)");
        T.Check(worst <= 30, "GPUs show the same colour step as the board");
    }

    public static void TestGpuStaticResendAndModes()
    {
        var cfg = new RgbConfig { Mode = RgbModes.Static, StaticColor = "#400000" };
        var ch = new RgbDeviceChannel(Pny(), 0);
        int sends = 0;
        for (double t = 0; t < 119.95; t += 0.1)
        {
            ch.Configure(cfg, MusicTuning.Resolve(cfg), new RgbDeviceSettings { Name = Pny().Name }, false);
            ch.Prepare(t, 0.1, null, null, BeatState.None, null, false, false);
            if (!ch.Render()) continue;
            var reason = ch.ShouldSend(t, 0.1, false);
            if (reason != SendReason.None) { ch.MarkSent(t, 0.1, reason, 1); sends++; }
        }
        T.Check(sends == 2, $"PNY static colour: first write + one 60 s resend ({sends} writes in 120 s)");
        var r = new DeviceRenderer(Pny());
        T.Check(r.ModeFor(RgbModes.Rainbow) == RgbModes.RainbowStatic && r.ModeFor(RgbModes.Breathing) == RgbModes.Static, "GPU: flowing modes held still");
        T.Check(RgbDeviceChannel.MaxFpsFor(Pny(), new RgbDeviceSettings { MaxFps = 60 }) == RgbDeviceChannel.GpuMaxFps, "GPU send rate cannot be raised");
    }

    public static void TestSoundLeavesIdleImmediately()
    {
        // 音が戻ったら、すぐ（80 ミリ秒で）音楽連動に戻す（曲の頭・ブレイク明けの最初のビートを見せる）
        var mixer = new IdleMixer();
        var quiet = new AudioFrame { GateOpen = false, Activity = 0 };
        var sound = new AudioFrame { GateOpen = true, Activity = 0.3f };
        for (int i = 0; i < 300; i++) mixer.Update(quiet, 2, 1f / 60);
        T.Check(mixer.Weight == 1, "idle after 5 s of silence");
        for (int i = 0; i < 3; i++) mixer.Update(sound, 2, 1f / 60);
        T.Check(mixer.Weight < 0.5f, $"music is back within 50 ms (weight {mixer.Weight:F2})");
        for (int i = 0; i < 3; i++) mixer.Update(sound, 2, 1f / 60);
        T.Check(mixer.Weight == 0, "music fully back within 100 ms");
    }

    public static void TestWhiteOnlyDevice()
    {
        var kb = new RgbDevice(1, "Logitech G610 Orion", 10, 5, null, "HID: x");
        var cfg = new RgbConfig { Mode = RgbModes.Temperature };
        var ch = new RgbDeviceChannel(kb, 0);
        ch.Configure(cfg, MusicTuning.Resolve(cfg), new RgbDeviceSettings { Name = kb.Name, WhiteOnly = true }, false);
        int lo = 255, hi = 0;
        foreach (double temp in new[] { 40.0, 58, 65, 72, 85 })
        {
            var r = new RgbDeviceChannel(kb, 0);
            r.Configure(cfg, MusicTuning.Resolve(cfg), new RgbDeviceSettings { Name = kb.Name, WhiteOnly = true }, false);
            r.Prepare(1, 0.1, null, null, BeatState.None, temp, false, false);
            r.Render();
            var c = r.Frame[0];
            T.Check(c.R == c.G && c.G == c.B, $"white-only: grey at {temp} °C ({c.Hex})");
            lo = Math.Min(lo, c.R); hi = Math.Max(hi, c.R);
        }
        T.Check(lo == 255 && hi == 255, $"white-only: constant brightness in temperature mode ({lo}..{hi})");
        T.Check(kb.IsWhiteOnly && !new RgbDevice(0, "Logitech G810 Orion Spectrum", 10, 5).IsWhiteOnly, "G610 is detected as white-only, G810 is not");
    }
}
