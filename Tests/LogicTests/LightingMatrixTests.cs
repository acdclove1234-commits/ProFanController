// ==================================================
// LightingMatrixTests.cs - 光り方 × デバイスの種類ごとに、実際に送る回数と色の跳ねを測る
//   (RgbSync の 1 コマと同じ順で RgbDeviceChannel を動かす。ちかちかの原因になる書き込みの多さを、毎回のビルドで確かめる)
// Dependencies: .NET 8 (MIT License)
// See CREDITS.txt for full license details.
// ==================================================

using ProFanController;

namespace LogicTests;

public static class LightingMatrixTests
{
    private static RgbDevice Board() => new(0, "ASRock B860 Steel Legend WiFi", 34, 0, new[] { 10, 24 }, "HID: x", "Direct", 3);
    private static RgbDevice Keyboard() => new(1, "Logitech G610 Orion", 117, 5, null, "HID: y", "Direct", 2);
    private static RgbDevice Pny() => new(2, "PNY GeForce RTX 5070Ti ARGB Epic-X OC", 41, RgbDevice.TypeGpu, new[] { 4, 20, 17 }, "I2C: NVIDIA, address 0x60", "Direct", 7);
    private static RgbDevice Rx() => new(3, "ASRock Radeon RX 7900 XTX Phantom Gaming", 4, RgbDevice.TypeGpu, new[] { 1, 1, 1, 1 }, "I2C", "Direct", 1);
    private static RgbDevice Dram() => new(4, "ENE DRAM", 8, RgbDevice.TypeDram, null, "I2C: PIIX4, address 0x70", "Direct", 4);

    /// <summary>1 つの組み合わせの結果</summary>
    private sealed record Result(double WritesPerMin, double MaxStep, double BigStepsPerMin);

    /// <summary>
    /// 60 秒動かす（RgbSync.RenderTick と同じ: 動く光り方は 1/60 秒、止まった光り方は 0.1 秒ごと）。
    /// 温度は 1 秒ごとに RgbTemperatureFilter を通して渡す（Arrow Lake の CPU パッケージのように数°C 跳ねる値）
    /// </summary>
    private static Result Run(RgbDevice dev, RgbConfig cfg, RgbDeviceSettings? settings = null, int seed = 1)
    {
        var ch = new RgbDeviceChannel(dev, 0);
        ch.Configure(cfg, MusicTuning.Resolve(cfg), settings ?? new RgbDeviceSettings { Name = dev.Name }, false);
        bool animated = RgbEffects.IsAnimated(cfg.Mode);
        double tick = animated ? RgbSync.AnimatedPeriod : RgbSync.StaticPeriod;
        var filter = new RgbTemperatureFilter();
        var rng = new Random(seed);
        double? temp = null;
        int writes = 0, big = 0;
        double maxStep = 0;
        Rgb[]? last = null;
        int nextSecond = 0;
        const double Duration = 60;
        for (double t = 0; t < Duration; t += tick)
        {
            if (t >= nextSecond)
            {
                // 軽い負荷: ふだん 42 °C、4 回に 1 回くらい +6〜18 °C 跳ねる
                double raw = 42 + (rng.NextDouble() < 0.25 ? 6 + rng.NextDouble() * 12 : rng.NextDouble() * 2);
                temp = filter.Push(raw, t);
                nextSecond++;
            }
            ch.Prepare(t, tick, null, null, BeatState.None, temp, false, false);
            if (!ch.Render()) continue;
            var reason = ch.ShouldSend(t, tick, animated);
            if (reason == SendReason.None) continue;
            ch.MarkSent(t, tick, reason, 1);
            writes++;
            var now = ch.CopyLastSent()!;
            if (last is not null)
            {
                double step = 0;
                for (int i = 0; i < now.Length; i++)
                    step = Math.Max(step, Math.Abs(now[i].R - last[i].R) + Math.Abs(now[i].G - last[i].G) + Math.Abs(now[i].B - last[i].B));
                maxStep = Math.Max(maxStep, step);
                if (step > 40) big++;
            }
            last = now;
        }
        return new Result(writes * 60 / Duration, maxStep, big * 60 / Duration);
    }

    public static void TestWriteBudgetPerModeAndDevice()
    {
        var devices = new (string Name, Func<RgbDevice> Make)[]
        {
            ("Board(USB)", Board), ("G610", Keyboard), ("PNY GPU", Pny), ("RX(ASRock)", Rx), ("DRAM", Dram),
        };
        var modes = new (string Name, RgbConfig Cfg)[]
        {
            ("温度", new RgbConfig { Mode = RgbModes.Temperature }),
            ("固定色", new RgbConfig { Mode = RgbModes.Static, StaticColor = "#6A00FF" }),
            ("虹(固定)", new RgbConfig { Mode = RgbModes.RainbowStatic }),
            ("虹(流れる)", new RgbConfig { Mode = RgbModes.Rainbow, Speed = 1 }),
            ("呼吸", new RgbConfig { Mode = RgbModes.Breathing, Speed = 1 }),
        };
        T.Info("書き込み回数／分（最大の色の跳ね・大きな跳ね／分）");
        foreach (var (mName, cfg) in modes)
        {
            var line = new System.Text.StringBuilder($"{mName,-8}");
            foreach (var (dName, make) in devices)
            {
                var r = Run(make(), cfg);
                line.Append($" | {dName} {r.WritesPerMin,6:F1} ({r.MaxStep,3:F0}, {r.BigStepsPerMin:F1})");
                bool gpu = dName is "PNY GPU" or "RX(ASRock)";
                // グラボ: どの光り方でも 1 分に 10 回以下（以前は温度モードで 140〜270 回、虹で 260 回）
                if (gpu) T.Check(r.WritesPerMin <= 10, $"{dName} / {mName}: GPU written rarely ({r.WritesPerMin:F1}/min)");
                // 止まった光り方は、送り直しを除いてほとんど書かない
                // 止まった光り方: 遅いデバイスは送り直しだけ。速い USB デバイスは 0.2 秒ごとに同じ色（電源などの ARGB が自分の光り方に戻らないように）
                bool fast = dName is "Board(USB)" or "G610";
                if (mName is "固定色" or "虹(固定)")
                    T.Check(fast ? r.WritesPerMin is >= 250 and <= 310 && r.MaxStep == 0 : r.WritesPerMin <= 13,
                        $"{dName} / {mName}: static modes write only resends ({r.WritesPerMin:F1}/min, max step {r.MaxStep:F0})");
                // 温度モード: 色の大きな跳ね（ちかちか）はない
                if (mName == "温度") T.Check(r.BigStepsPerMin <= 1, $"{dName} / 温度: no big colour jumps ({r.BigStepsPerMin:F1}/min, max step {r.MaxStep:F0})");
            }
            T.Info(line.ToString());
        }
    }

    public static void TestGpuCanOptInToFlowingRainbow()
    {
        var cfg = new RgbConfig { Mode = RgbModes.Rainbow, Speed = 1 };
        var held = Run(Pny(), cfg);
        var flowing = Run(Pny(), cfg, new RgbDeviceSettings { Name = Pny().Name, AnimateOnGpu = true });
        T.Info($"PNY 虹(流れる): 既定 {held.WritesPerMin:F1}/分、「虹・呼吸も動かす」オン {flowing.WritesPerMin:F1}/分");
        T.Check(held.WritesPerMin <= 2 && flowing.WritesPerMin > 30, "GPU rainbow is held by default and flows when the user opts in");
        T.Check(flowing.WritesPerMin <= 5 * 60, "even when flowing, the GPU never exceeds 5 writes/s");
        var rx = Run(Rx(), cfg, new RgbDeviceSettings { Name = Rx().Name, AnimateOnGpu = true });
        T.Check(rx.WritesPerMin <= 2, "the flicker-prone ASRock Radeon stays held even if the option is set");
    }

    public static void TestWhiteOnlyKeyboardFollowsBrightnessNotHue()
    {
        // 白だけのキーボードは、虹でも色相で明るさが変わらない（R・G・B の最大 = いつも明るい）
        var ch = new RgbDeviceChannel(Keyboard(), 0);
        var cfg = new RgbConfig { Mode = RgbModes.Rainbow, Speed = 3, Gamma = false };
        ch.Configure(cfg, MusicTuning.Resolve(cfg), new RgbDeviceSettings { Name = Keyboard().Name }, false);
        int lo = 255, hi = 0;
        for (double t = 0; t < 20; t += 1.0 / 60)
        {
            ch.Prepare(t, 1.0 / 60, null, null, BeatState.None, null, false, false);
            ch.Render();
            foreach (var c in ch.Frame) { lo = Math.Min(lo, c.R); hi = Math.Max(hi, c.R); T_AssertGrey(c); }
        }
        T.Check(lo >= 250, $"white-only keyboard stays bright through the whole rainbow ({lo}..{hi})");
    }

    private static void T_AssertGrey(Rgb c)
    {
        if (c.R != c.G || c.G != c.B) throw new InvalidOperationException($"white-only device got a colour {c.Hex}");
    }
}
