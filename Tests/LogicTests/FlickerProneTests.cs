// ==================================================
// FlickerProneTests.cs - ちかちかしやすいグラボ（ASRock の Radeon）の扱いのテスト
// Dependencies: .NET 8 (MIT License)
// See CREDITS.txt for full license details.
// ==================================================

using ProFanController;

namespace LogicTests;

public static class FlickerProneTests
{
    public static void TestAsrockRadeonIsFlickerProne()
    {
        var pg = new RgbDevice(1, "ASRock Radeon RX 7900 XTX Phantom Gaming", 3, RgbDevice.TypeGpu);
        var pny = new RgbDevice(2, "PNY GeForce RTX 5070Ti ARGB Epic-X OC", 6, RgbDevice.TypeGpu);
        var mb = new RgbDevice(0, "ASRock B860 Steel Legend WiFi", 250, 0);
        T.Check(pg.IsFlickerProne && pg.IsSlow, "ASRock Radeon GPU is flicker-prone");
        T.Check(!pny.IsFlickerProne && !mb.IsFlickerProne, "PNY GPU and ASRock motherboard are not");
        T.Check(RgbDeviceChannel.MaxFpsFor(pg, new RgbDeviceSettings()) == RgbDeviceChannel.FlickerProneMaxFps, "ASRock Radeon sends at most 4 times per second");
        T.Check(RgbDeviceChannel.MaxFpsFor(pny, new RgbDeviceSettings()) == RgbDeviceChannel.GpuMaxFps, "other GPUs send at most 5 per second (one update is 41 slow I2C writes)");
        T.Check(pny.IsLowWrite && !pny.IsFlickerProne && pny.DefaultLatencyMs == 60, "PNY GPU: low-write, not flicker-prone, 60 ms lead");
        T.Check(!mb.IsLowWrite && new RgbDevice(3, "ENE DRAM", 8, RgbDevice.TypeDram).DefaultLatencyMs == 20, "motherboard/RAM unaffected");
        T.Check(RgbDeviceChannel.MaxFpsFor(pg, new RgbDeviceSettings { MaxFps = 30 }) == RgbDeviceChannel.FlickerProneMaxFps, "user setting cannot raise the flicker-prone cap");
        T.Check(RgbDeviceChannel.MaxFpsFor(pg, new RgbDeviceSettings { MaxFps = 2 }) == 2, "user setting can lower it");
        T.Check(RgbDeviceChannel.MaxFpsFor(pny, new RgbDeviceSettings { MaxFps = 5 }) == 5, "user setting still wins for other devices");
    }

    public static void TestNoForcedResendAndModeSubstitution()
    {
        var pg = new RgbDevice(1, "ASRock Radeon RX 7900 XTX Phantom Gaming", 4, RgbDevice.TypeGpu, new[] { 1, 1, 1, 1 }, "I2C", "Direct", 1);
        var r = new DeviceRenderer(pg);
        T.Check(r.ModeFor(RgbModes.Rainbow) == RgbModes.RainbowStatic && r.ModeFor(RgbModes.Breathing) == RgbModes.Static
            && r.ModeFor(RgbModes.Static) == RgbModes.Static, "flowing modes held still on the flicker-prone GPU");
        var mem = new DeviceRenderer(new RgbDevice(2, "ENE DRAM", 8, RgbDevice.TypeDram));
        T.Check(mem.ModeFor(RgbModes.Rainbow) == RgbModes.Rainbow, "other devices keep flowing modes");
        T.Check(DeviceRenderer.FlickerProneVariant(EffectVariant.MixSlow, "Auto") == EffectVariant.BeatHueSlow
            && DeviceRenderer.FlickerProneVariant(EffectVariant.Mood, "Auto") == EffectVariant.BeatHueSlow
            && DeviceRenderer.FlickerProneVariant(EffectVariant.Mood, MusicStyles.Mood) == EffectVariant.Mood
            && DeviceRenderer.FlickerProneVariant(EffectVariant.Spectrum, MusicStyles.Spectrum) == EffectVariant.BeatHueSlow,
            "music effects mapped to low-write versions");

        // 固定色: 最初に 1 回送ったら、同じ色のままなら 1 分たっても送り直さない
        var cfg = new RgbConfig { Mode = RgbModes.Static, StaticColor = "#400000" };
        var ch = new RgbDeviceChannel(pg, 0);
        int sends = 0;
        for (double t = 0; t < 60; t += 0.1)
        {
            ch.Configure(cfg, MusicTuning.Resolve(cfg), new RgbDeviceSettings { Name = pg.Name }, false);
            ch.Prepare(t, 0.1, null, null, BeatState.None, null, false, false);
            if (!ch.Render()) continue;
            var reason = ch.ShouldSend(t, 0.1, false);
            if (reason != SendReason.None) { ch.MarkSent(t, 0.1, reason, 1); sends++; }
        }
        T.Check(sends == 1, $"static colour is written once, no 5 s resend ({sends} writes in 60 s)");
    }

    public static void TestParseActiveMode()
    {
        // 名前 "GPU"・モード 2 つ（Direct, Static）で、選択中は 0（Direct）の最小限のコントローラー情報（版 1）
        var b = new List<byte>();
        void U16(int v) { b.Add((byte)v); b.Add((byte)(v >> 8)); }
        void I32(int v) { b.AddRange(BitConverter.GetBytes(v)); }
        void Str(string s) { var x = System.Text.Encoding.ASCII.GetBytes(s + "\0"); U16(x.Length); b.AddRange(x); }
        I32(0); I32(RgbDevice.TypeGpu);
        Str("GPU"); Str("ASRock"); Str("d"); Str("v"); Str("s"); Str("I2C");
        U16(2); I32(0);
        foreach (var m in new[] { "Direct", "Static" }) { Str(m); for (int i = 0; i < 9; i++) I32(0); U16(0); }
        var (active, count) = OpenRgbClient.ParseActiveMode(b.ToArray(), 1);
        T.Check(active == "Direct" && count == 2, $"active mode parsed ({active}, {count})");
        T.Check(OpenRgbClient.ParseActiveMode(new byte[5], 1) == ("", 0), "broken data gives empty result");
    }

    public static void TestGpuMoodSendsRarelyAndKeepsBrightness()
    {
        // PNY のグラボで全体が「ムード」: 光り方はムードのまま、明るさは一定で、わずかな変化では送らない
        var pny = new RgbDevice(2, "PNY GeForce RTX 5070Ti ARGB Epic-X OC", 41, RgbDevice.TypeGpu, new[] { 4, 20, 17 });
        var cfg = new RgbConfig { Mode = RgbModes.Music, MusicStyle = MusicStyles.Mood, Gamma = false };
        var ch = new RgbDeviceChannel(pny, 0);
        ch.Configure(cfg, MusicTuning.Resolve(cfg), new RgbDeviceSettings { Name = pny.Name }, false);
        var ring = new FeatureRing();
        var f = new AudioFrame { GateOpen = true, Activity = 1 };
        int sends = 0;
        float minBright = 255, maxBright = 0;
        var rng = new Random(3);
        for (double t = 0; t < 20; t += 1.0 / 60)
        {
            f.T = t;
            f.LoudVU = (float)(0.3 + 0.7 * rng.NextDouble());   // 音量が細かく揺れる
            f.CentroidHz = 800;
            ring.Write(f);
            ch.Prepare(t, 1.0 / 60, ring, null, BeatState.None, null, false, false);
            if (!ch.Render()) continue;
            if (t > 3)
            {
                var c = ch.Frame[10];
                float b = Math.Max(c.R, Math.Max(c.G, c.B));
                minBright = Math.Min(minBright, b);
                maxBright = Math.Max(maxBright, b);
            }
            var reason = ch.ShouldSend(t, 1.0 / 60, true);
            if (reason != SendReason.None) { ch.MarkSent(t, 1.0 / 60, reason, 1); sends++; }
        }
        T.Info($"PNY + ムード: 20 秒で {sends} 回送信、明るさ {minBright}〜{maxBright}");
        T.Check(ch.Renderer.ActiveEffect == MusicStyles.Mood, "GPU keeps the global effect (Mood)");
        T.Check(maxBright - minBright <= 8, $"GPU brightness stays constant while loudness jitters ({minBright}-{maxBright})");
        T.Check(sends <= 20, $"GPU is written rarely in Mood ({sends} writes in 20 s)");
    }
}
