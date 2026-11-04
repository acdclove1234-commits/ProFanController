// ==================================================
// OpenRgbTests.cs - OpenRGB 連携のテスト (描く間隔・遅いデバイスの送るタイミング・出来事の読み取り・
//                   パケットの形・繋ぎ直しの待ち時間・止めるときの光り方・偽の OpenRGB サーバーとの通信)
// Dependencies: .NET 8 (MIT License)
// See CREDITS.txt for full license details.
// ==================================================

using System.Buffers.Binary;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using ProFanController;

namespace LogicTests;

public static class OpenRgbTests
{
    /// <summary>1 コマの間隔（秒）</summary>
    private const double P = 1.0 / 60;

    // =====================================================================
    // 描く間隔（FrameCadence）
    // =====================================================================

    public static void TestCadenceSkipAhead()
    {
        var c = new FrameCadence(P, 0);
        var rnd = new Random(1);
        var renders = new List<(double Wall, double T)>();
        double now = 0;
        while (now < 2.0)
        {
            if (c.IsDue(now))
            {
                renders.Add((now, c.Begin(now)));
                c.Advance();
                if (renders.Count == 30) now += 1.5 * P;   // 少し遅れた（2 コマ分未満）
                if (renders.Count == 60) now += 0.2;       // 大きく遅れた（PC が忙しかった）
                continue;
            }
            now = c.Next + rnd.NextDouble() * 0.001; // 0〜1 ミリ秒遅れて起きる
        }

        bool even = true;
        for (int i = 1; i < 30; i++) even &= Math.Abs(renders[i].T - renders[i - 1].T - P) < 1e-9;
        T.Check(even, "scheduled render times are exactly 1/60 s apart even with wake-up jitter");

        bool monotonic = true;
        for (int i = 1; i < renders.Count; i++) monotonic &= renders[i].T > renders[i - 1].T;
        T.Check(monotonic, "scheduled times never go backwards");

        // 実際の時刻で P/2 以内に続けて描いたコマの最大数（まとめ描き）
        int maxBurst = 1, run = 1;
        for (int i = 1; i < renders.Count; i++)
        {
            run = renders[i].Wall - renders[i - 1].Wall < P / 2 ? run + 1 : 1;
            maxBurst = Math.Max(maxBurst, run);
        }
        T.Check(maxBurst <= 2, $"never bursts: at most 2 frames back to back (max {maxBurst})");

        // 0.2 秒止まった分（12 コマ）は取り戻さない
        int expectedWithoutCatchUp = (int)Math.Round((2.0 - 0.2) / P);
        T.Check(Math.Abs(renders.Count - expectedWithoutCatchUp) <= 3, $"no catch-up burst after a 200 ms stall ({renders.Count} frames, ~{expectedWithoutCatchUp} expected)");

        // 間隔を変えたら次のコマをすぐ描く
        var c2 = new FrameCadence(RgbSync.StaticPeriod, 0);
        c2.Begin(0);
        c2.Advance(); // 次は 0.1 秒
        c2.SetPeriod(P, 0.02);
        T.Check(c2.IsDue(0.02) && c2.Period == P, "switching to 60 Hz renders immediately");
    }

    // =====================================================================
    // 送るタイミング（SendScheduler）
    // =====================================================================

    public static void TestSchedulerRates()
    {
        foreach (var (fps, expected) in new[] { (60.0, 600), (30.0, 300), (8.0, 80), (12.0, 120) })
        {
            var s = new SendScheduler();
            double interval = 1 / fps;
            int sends = 0;
            double last = double.NegativeInfinity, minGap = double.PositiveInfinity;
            for (int k = 0; k < 600; k++)
            {
                double t = k * P;
                var r = s.Decide(t, P, interval, false);
                if (r == SendReason.None) continue;
                s.Commit(t, P, interval, r);
                sends++;
                minGap = Math.Min(minGap, t - last);
                last = t;
            }
            T.Check(Math.Abs(sends - expected) <= 1, $"{fps} fps over 10 s → {sends} sends (expected {expected})");
            T.Check(interval <= P ? minGap >= P - 1e-9 : minGap >= interval - P - 1e-9, $"{fps} fps: min gap {minGap * 1000:F1} ms");
        }
    }

    public static void TestSlowDeviceBeatAligned()
    {
        const double interval = 1 / 8.0;
        const double latency = 0.020; // GPU の遅れ 20 ms・音の遅れの補正 0
        foreach (double bpm in new[] { 90.0, 128.0, 174.0 })
        {
            var beat = new BeatState(60 / bpm, 0.237, 0, 1, true, 0);
            var s = new SendScheduler();
            var sends = new List<(double T, SendReason R)>();
            int beats = 0, hit = 0;
            double prevEval = double.NaN;
            for (int k = 0; k < 600; k++)
            {
                double t = k * P, eval = t + latency;
                bool cue = !double.IsNaN(prevEval) && beat.Crossed(prevEval, eval);
                prevEval = eval;
                var r = s.Decide(t, P, interval, cue, beat.NextBeatAfter(eval) - eval);
                if (r != SendReason.None)
                {
                    s.Commit(t, P, interval, r);
                    sends.Add((t, r));
                }
                if (cue && t > 1)
                {
                    beats++;
                    if (r != SendReason.None) hit++;
                }
            }
            double minGap = double.PositiveInfinity;
            for (int i = 1; i < sends.Count; i++) minGap = Math.Min(minGap, sends[i].T - sends[i - 1].T);
            int aligned = sends.Count(x => x.R == SendReason.Aligned);
            T.Check(beats > 10 && hit == beats, $"{bpm} BPM: every predicted beat is sent on its frame ({hit}/{beats}, {aligned} early sends)");
            T.Check(minGap >= SendScheduler.EarlyFraction * interval - 1e-9, $"{bpm} BPM: sends at least 0.6 × interval apart (min {minGap * 1000:F1} ms)");
            double maxGap = 0;
            for (int i = 1; i < sends.Count; i++) maxGap = Math.Max(maxGap, sends[i].T - sends[i - 1].T);
            T.Check(sends.Count <= 10 / (SendScheduler.EarlyFraction * interval) && sends.Count >= 70, $"{bpm} BPM: {sends.Count} sends in 10 s (about 8 fps, never above 1/(0.6·interval))");
            T.Check(maxGap < 2 * interval, $"{bpm} BPM: waiting for a beat never leaves a gap of 2 intervals (max {maxGap * 1000:F0} ms)");

            // 早めに送ったら、次のいつもの送信はそこから 1 間隔後（次のビートが先に来る場合を除く）
            bool rephased = true;
            for (int i = 0; i + 1 < sends.Count; i++)
            {
                if (sends[i].R != SendReason.Aligned || sends[i + 1].R != SendReason.Regular) continue;
                rephased &= Math.Abs(sends[i + 1].T - (sends[i].T + interval)) <= P / 2 + 1e-9;
            }
            T.Check(rephased, $"{bpm} BPM: after an early send the regular cadence restarts one interval later");
        }
    }

    // =====================================================================
    // デバイスごとの入力（RgbDeviceChannel）
    // =====================================================================

    private static RgbDeviceChannel SlowChannel(RgbConfig cfg, double now = 0)
    {
        // 更新の遅い I2C のデバイス（メモリなど。グラボは別の決まり: 1 秒に 5 回・早め 60 ミリ秒）
        var dev = new RgbDevice(0, "Slow I2C", 4, RgbDevice.TypeDram, null, "I2C: /dev/i2c-3");
        var ch = new RgbDeviceChannel(dev, now);
        ch.Configure(cfg, MusicTuning.Resolve(cfg), new RgbDeviceSettings { Name = "Slow I2C" }, false);
        return ch;
    }

    public static void TestKickLatchedAcrossUnsentFrames()
    {
        var cfg = new RgbConfig { Mode = RgbModes.Music };
        var ch = SlowChannel(cfg);
        var ring = new EventRing();
        var features = new FeatureRing();
        ring.Add(AudioEventKind.Kick, -0.5, 3); // 始める前の古い出来事（読み飛ばすべき）

        var delivered = new List<(int K, AudioEvent E)>();
        var decisions = new List<SendReason>();
        var latched = new List<bool>();
        for (int k = 0; k < 20; k++)
        {
            double t = k * P;
            if (k == 1) ring.Add(AudioEventKind.Kick, 0.01, 2); // 最初に送った直後のキック
            ch.Prepare(t, P, features, ring, BeatState.None, null, false, false);
            foreach (var e in ch.Input.Events) delivered.Add((k, e));
            latched.Add(ch.Scheduler.AccentLatched);
            var r = ch.Decide(t, P);
            decisions.Add(r);
            if (r != SendReason.None) ch.MarkSent(t, P, r, 0.1);
        }
        T.Check(delivered.Count == 1 && delivered[0].K == 1 && delivered[0].E.T == 0.01, $"the kick is delivered exactly once, old events are skipped ({delivered.Count})");
        T.Check(decisions[0] == SendReason.Regular, "first frame is sent");
        T.Check(latched[1] && latched[2] && latched[3] && latched[4], "kick stays latched while frames are not sent");
        T.Check(decisions[1] == SendReason.None && decisions[4] == SendReason.None, "no early send before 0.6 × interval");
        T.Check(decisions[5] == SendReason.Aligned && !latched[6], $"early send at the first frame ≥ 0.6 × interval after the last send ({decisions[5]})");
        int nextRegular = decisions.FindIndex(6, d => d != SendReason.None);
        T.Check(nextRegular is 12 or 13, $"next regular send one interval after the early send (frame {nextRegular})");

        // ハイハット・スネアでは早めに送らない。クリック（同期テスト）では送る
        var ch2 = SlowChannel(cfg);
        var ring2 = new EventRing();
        ch2.Prepare(0, P, features, ring2, BeatState.None, null, false, false);
        ch2.MarkSent(0, P, ch2.Decide(0, P), 0.1);
        ring2.Add(AudioEventKind.Hat, 0.005, 3);
        ring2.Add(AudioEventKind.Snare, 0.006, 3);
        ch2.Prepare(P, P, features, ring2, BeatState.None, null, false, false);
        T.Check(ch2.Input.Events.Count == 2 && !ch2.Scheduler.AccentLatched, "hats and snares do not trigger early sends");
        ring2.Add(AudioEventKind.Click, 0.03, 1);
        ch2.Prepare(2 * P, P, features, ring2, BeatState.None, null, false, false);
        T.Check(ch2.Scheduler.AccentLatched, "sync-test clicks trigger early sends");

        // テンポをつかんでいるときはキックではなくビートの頭で送る
        var locked = new BeatState(0.5, 0.1, 0, 0.9, true, 0);
        var ch3 = SlowChannel(cfg);
        var ring3 = new EventRing();
        ch3.Prepare(0, P, features, ring3, locked, null, false, false);
        ring3.Add(AudioEventKind.Kick, 0.03, 3);
        ch3.Prepare(P, P, features, ring3, locked, null, false, false);
        T.Check(!ch3.Scheduler.AccentLatched, "kicks are not used for early sends while the tempo is locked");
        bool cue = false;
        for (int k = 2; k < 60 && !cue; k++)
        {
            ch3.Prepare(k * P, P, features, ring3, locked, null, false, false);
            cue = ch3.BeatCue;
            // ビート（0.1 秒）が読む時刻の区間 (前のコマ, このコマ] に入ったコマで合図が出る（GPU の遅れ 20 ms → 0.0833 秒のコマ）
            if (cue) T.Check(k == 5 && ch3.Input.TPrevEval < 0.1 && ch3.Input.TEval >= 0.1, $"beat cue on the frame whose eval window contains the beat (frame {k}: ({ch3.Input.TPrevEval:F3}, {ch3.Input.TEval:F3}])");
            else T.Check(ch3.NextBeatIn > 0 && ch3.NextBeatIn <= 0.5, "time to the next beat is known while locked");
        }
        T.Check(cue, "locked tempo produces a beat cue");
        cfg.Music.TempoMode = TempoModes.KickOnly;
        var ch4 = SlowChannel(cfg);
        ch4.Prepare(0, P, features, ring3, locked, null, false, false);
        ring3.Add(AudioEventKind.Kick, 0.05, 3);
        ch4.Prepare(4 * P, P, features, ring3, locked, null, false, false);
        T.Check(ch4.Scheduler.AccentLatched, "tempo mode 'kick only' uses kicks even when locked");
    }

    public static void TestEventsDeliveredExactlyOnce()
    {
        var cfg = new RgbConfig { Mode = RgbModes.Music };
        var ch = SlowChannel(cfg);
        var ring = new EventRing();
        var features = new FeatureRing();
        var rnd = new Random(7);
        var counts = new Dictionary<long, int>();
        bool early = false;
        double nextEvent = 0.05;
        for (int k = 0; k < 180; k++)
        {
            double t = k * P;
            // 出来事は少し遅れて記録される（検出に時間がかかる）
            while (nextEvent < t - 0.03)
            {
                long id = ring.Add(AudioEventKind.Kick, nextEvent, 2);
                counts[id] = 0;
                nextEvent += 0.02 + rnd.NextDouble() * 0.1;
            }
            if (k == 90)
            {
                // 途中で「音の遅れの補正」を +100 ms にする（読む時刻が過去に戻る）
                cfg.Music.AudioDelayMs = 100;
                ch.Configure(cfg, MusicTuning.Resolve(cfg), new RgbDeviceSettings { Name = "GPU" }, false);
            }
            ch.Prepare(t, P, features, ring, BeatState.None, null, false, false);
            foreach (var e in ch.Input.Events)
            {
                counts[e.Id]++;
                early |= e.T > ch.Input.TEval + 1e-12;
            }
            var r = ch.Decide(t, P); // 送るのは間隔ごと（描くのは毎コマ）
            if (r != SendReason.None) ch.MarkSent(t, P, r, 0.1);
        }
        int dup = counts.Values.Count(v => v > 1);
        int lost = counts.Where(kv => kv.Value == 0).Count(kv => ring.LastId - kv.Key > 3);
        T.Check(counts.Count > 20 && dup == 0 && lost == 0, $"{counts.Count} events: each delivered exactly once across sent and unsent frames (dup {dup}, lost {lost})");
        T.Check(!early, "an event is never delivered before its time");
    }

    public static void TestEvalTime()
    {
        T.Near(RgbDeviceChannel.EvalTime(10, 8, 0), 10.008, 1e-12, "USB: read 8 ms ahead");
        T.Near(RgbDeviceChannel.EvalTime(10, 20, 150), 9.87, 1e-12, "I2C + Bluetooth delay 150 ms: read 130 ms in the past");
        T.Near(RgbDeviceChannel.EvalTime(10, 0, -100), 10.1, 1e-12, "negative audio delay: read ahead");

        var usb = new RgbDevice(0, "Strip", 30, 0, null, "HID: /dev/hidraw0");
        var gpu = new RgbDevice(1, "GPU", 4, RgbDevice.TypeGpu);
        var def = new RgbDeviceSettings();
        var ram = new RgbDevice(2, "ENE DRAM", 8, RgbDevice.TypeDram);
        T.Check(RgbDeviceChannel.LatencyFor(usb, def) == 8 && RgbDeviceChannel.LatencyFor(ram, def) == 20 && RgbDeviceChannel.LatencyFor(gpu, def) == 60,
            "default latency USB 8 / I2C 20 / GPU 60 ms");
        T.Check(RgbDeviceChannel.LatencyFor(usb, new RgbDeviceSettings { LatencyMs = 50 }) == 50
            && RgbDeviceChannel.LatencyFor(usb, new RgbDeviceSettings { LatencyMs = 0 }) == 0, "user latency overrides (0 allowed)");
        T.Check(RgbDeviceChannel.MaxFpsFor(usb, def) == 60 && RgbDeviceChannel.MaxFpsFor(ram, def) == 8 && RgbDeviceChannel.MaxFpsFor(gpu, def) == 5
            && RgbDeviceChannel.MaxFpsFor(gpu, new RgbDeviceSettings { MaxFps = 12 }) == 5 && RgbDeviceChannel.MaxFpsFor(gpu, new RgbDeviceSettings { MaxFps = 4 }) == 4
            && RgbDeviceChannel.MaxFpsFor(ram, new RgbDeviceSettings { MaxFps = 12 }) == 12, "max fps: auto 60 / 8 / GPU 5 (GPU can only be lowered), user override");

        // 分析結果の記録を、読む時刻で補って読む
        var cfg = new RgbConfig { Mode = RgbModes.Music };
        cfg.Music.AudioDelayMs = 150;
        var ch = SlowChannel(cfg);
        var ring = new FeatureRing();
        var f = new AudioFrame { T = 9.8, Bass = 0 };
        ring.Write(f);
        f.T = 9.9;
        f.Bass = 1;
        ring.Write(f);
        ch.Prepare(10.0, P, ring, null, BeatState.None, 55, false, false);
        T.Near(ch.Input.TEval, 9.87, 1e-9, "channel TEval = tNow + Ldev − O");
        T.Check(ch.Input.TPrevEval == ch.Input.TEval && ch.Input.T == 10.0, "first frame: TPrevEval = TEval");
        T.Near(ch.Input.Frame.Bass, 0.7, 1e-5, "features interpolated at TEval");
        T.Check(ch.Input.Temperature == 55 && ch.Input.Events.Count == 0, "temperature passed, no events without a ring");
        ch.Prepare(10.0 + P, P, null, null, BeatState.None, null, false, false);
        T.Near(ch.Input.TPrevEval, 9.87, 1e-9, "next frame: TPrevEval = previous TEval");
        T.Near(ch.Input.Dt, P, 1e-9, "dt = time since the previous frame");
        T.Check(ch.Input.Frame.Bass == 0 && !ch.Input.Frame.GateOpen, "no engine → silent frame");
    }

    // =====================================================================
    // パケット・繋ぎ直し・止めるとき
    // =====================================================================

    public static void TestUpdateLedsPacket()
    {
        var buf = new byte[8]; // 小さすぎる → 大きくなる
        var colors = new[] { new Rgb(1, 2, 3), new Rgb(10, 20, 30), new Rgb(255, 0, 128) };
        int n = OpenRgbClient.BuildUpdateLeds(ref buf, 5, colors, false);
        T.Check(n == 16 + 6 + 12 && buf.Length >= n, $"packet length 34 ({n})");
        T.Check(Encoding.ASCII.GetString(buf, 0, 4) == "ORGB", "magic ORGB");
        T.Check(BinaryPrimitives.ReadUInt32LittleEndian(buf.AsSpan(4)) == 5, "device index");
        T.Check(BinaryPrimitives.ReadUInt32LittleEndian(buf.AsSpan(8)) == 1050, "packet id UPDATELEDS 1050");
        T.Check(BinaryPrimitives.ReadUInt32LittleEndian(buf.AsSpan(12)) == 18, "header payload size 18");
        T.Check(BinaryPrimitives.ReadUInt32LittleEndian(buf.AsSpan(16)) == 18, "data size includes itself");
        T.Check(BinaryPrimitives.ReadUInt16LittleEndian(buf.AsSpan(20)) == 3, "LED count");
        T.Check(buf[22] == 1 && buf[23] == 2 && buf[24] == 3 && buf[25] == 0 && buf[30] == 255 && buf[31] == 0 && buf[32] == 128, "colors R, G, B, 0");

        Array.Fill(buf, (byte)0xEE); // 使い回すバッファが汚れていても
        n = OpenRgbClient.BuildUpdateLeds(ref buf, 0, colors, true);
        T.Check(buf[22] == 2 && buf[23] == 1 && buf[26] == 20 && buf[27] == 10 && buf[28] == 30, "R/G swapped");
        T.Check(buf[25] == 0 && buf[29] == 0 && buf[33] == 0, "padding byte is zeroed when the buffer is reused");
        var before = buf;
        OpenRgbClient.BuildUpdateLeds(ref buf, 0, colors, false);
        T.Check(ReferenceEquals(before, buf), "buffer reused when big enough");
    }

    public static void TestBackoff()
    {
        var seq = Enumerable.Range(1, 8).Select(ReconnectBackoff.DelaySec).ToArray();
        T.Check(seq.SequenceEqual(new[] { 1, 2, 4, 8, 16, 30, 30, 30 }), $"backoff 1,2,4,8,16,30… ({string.Join(",", seq)})");
        T.Check(ReconnectBackoff.DelaySec(0) == 1 && ReconnectBackoff.DelaySec(1000) == 30, "backoff bounds");
    }

    public static void TestStopFrame()
    {
        var cfg = new RgbConfig { StaticColor = "#FF8000", Brightness = 50, Gamma = false };
        var dst = new Rgb[3];
        Array.Fill(dst, new Rgb(9, 9, 9));
        cfg.OnStop = RgbStopBehaviors.Off;
        T.Check(RgbSync.StopFrame(cfg, dst) && dst.All(c => c == Rgb.Black), "Off → black");
        cfg.OnStop = RgbStopBehaviors.Static;
        T.Check(RgbSync.StopFrame(cfg, dst) && dst.All(c => c == new Rgb(128, 64, 0)), $"Static at 50 % brightness ({dst[0].Hex})");
        cfg.Gamma = true;
        cfg.Brightness = 100;
        T.Check(RgbSync.StopFrame(cfg, dst) && dst.All(c => c == new Rgb(255, 56, 0)), $"Static with gamma like the static mode ({dst[0].Hex})");
        cfg.OnStop = RgbStopBehaviors.Keep;
        Array.Fill(dst, new Rgb(9, 9, 9));
        T.Check(!RgbSync.StopFrame(cfg, dst) && dst.All(c => c == new Rgb(9, 9, 9)), "Keep → nothing sent, frame untouched");
        cfg.OnStop = "Weird";
        T.Check(RgbSync.StopFrame(cfg, dst) && dst.All(c => c == Rgb.Black), "unknown → off");
    }

    public static void TestGovernor()
    {
        var g = new SendRateGovernor();
        double t = 0;
        for (int i = 0; i < 40; i++) g.Observe(12, t += 0.125, 8);
        T.Check(g.Limited && g.Limit < 8, $"slow writes lower the rate ({g.Limit:F1} fps)");
        for (int i = 0; i < 200; i++) g.Observe(12, t += 0.25, 8);
        T.Check(g.Limit >= SendRateGovernor.MinFps && g.Limit <= 4.5, $"never below the minimum ({g.Limit:F1} fps)");
        for (int i = 0; i < 400; i++) g.Observe(0.2, t += 0.25, 8);
        T.Check(!g.Limited, "fast writes restore the configured rate");
        var g2 = new SendRateGovernor();
        for (int i = 0; i < 200; i++) g2.Observe(i % 4 == 0 ? 8 : 0.3, i / 60.0, 60);
        T.Check(!g2.Limited, "an occasional slow write does not lower the rate");
    }

    public static void TestSelectTargets()
    {
        var devices = new List<RgbDevice> { new(0, "A", 10), new(1, "B", 0), new(2, "C", 5) };
        var missing = new List<string>();
        var all = RgbSync.SelectTargets(devices, new List<string>(), missing);
        T.Check(all.Select(d => d.Name).SequenceEqual(new[] { "A", "C" }) && missing.Count == 0, "empty selection = all devices with LEDs");
        var sel = RgbSync.SelectTargets(devices, new List<string> { "C", "Ghost", "Ghost" }, missing);
        T.Check(sel.Count == 1 && sel[0].Name == "C" && missing.SequenceEqual(new[] { "Ghost" }), "selected device found, absent one reported once");
        RgbSync.SelectTargets(devices, new List<string> { "B" }, missing);
        T.Check(missing.Count == 0, "present device without LEDs is not reported as missing");
    }

    public static void TestParseProtocol0()
    {
        var v1 = FakeOpenRgbServer.Blob("Strip", 0, "HID: x", 7, 1);
        var v0 = FakeOpenRgbServer.Blob("Strip", 0, "HID: x", 7, 0);
        var a = OpenRgbClient.ParseControllerInfo(v1);
        var b = OpenRgbClient.ParseControllerInfo(v0, 0);
        T.Check(a.Name == "Strip" && a.LedCount == 7 && a.Location == "HID: x" && a.Zones.SequenceEqual(new[] { 7 }), "protocol 1 blob");
        T.Check(b.Name == "Strip" && b.LedCount == 7 && b.Location == "HID: x", "protocol 0 blob (no vendor string)");
    }

    // =====================================================================
    // 偽の OpenRGB サーバーとの通信
    // =====================================================================

    private static bool WaitUntil(Func<bool> cond, int timeoutMs)
    {
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < timeoutMs)
        {
            if (cond()) return true;
            Thread.Sleep(10);
        }
        return cond();
    }

    public static void TestClientAgainstFakeServer()
    {
        using var server = new FakeOpenRgbServer(new[] { ("Fake Strip", 0, "HID: /dev/hidraw3", 10), ("Fake GPU", 2, "I2C: /dev/i2c-4, address 0x29", 4) });
        using (var client = new OpenRgbClient())
        {
            client.Connect("127.0.0.1", server.Port);
            T.Check(client.ServerProtocol == 1 && client.Protocol == 1, $"protocol negotiated ({client.ServerProtocol} → {client.Protocol})");
            var devs = client.GetDevices();
            T.Check(devs.Count == 2 && devs[0].Name == "Fake Strip" && devs[0].LedCount == 10 && !devs[0].IsSlow && devs[1].IsSlow && devs[1].Location.StartsWith("I2C"),
                "devices parsed with location (GPU on I2C is slow)");
            client.UpdateLeds(0, new[] { new Rgb(1, 2, 3) }, false);
            client.PollIncoming();
            T.Check(!client.DeviceListChanged, "no device-list change yet");
            server.SendDeviceListUpdated();
            T.Check(WaitUntil(() => { client.PollIncoming(); return client.DeviceListChanged; }, 1000), "DeviceListUpdated (100) seen by the non-blocking poll");
            T.Check(WaitUntil(() => server.UpdatesFor(0).Count == 1, 1000), "one UPDATELEDS received");
        }

        // 版 0 の古いサーバー: 版の返事をしない → 1 秒待って版 0 で読む
        server.AnswerProtocol = false;
        using (var client = new OpenRgbClient())
        {
            var sw = Stopwatch.StartNew();
            client.Connect("127.0.0.1", server.Port);
            var devs = client.GetDevices();
            T.Check(client.ServerProtocol == 0 && client.Protocol == 0 && sw.ElapsedMilliseconds is >= 900 and < 2500, $"protocol 0 fallback after ~1 s ({sw.ElapsedMilliseconds} ms)");
            T.Check(devs.Count == 2 && devs[1].Name == "Fake GPU" && devs[1].LedCount == 4, "protocol 0 controller data parsed");
        }
    }

    public static void TestLoopbackSession()
    {
        using var server = new FakeOpenRgbServer(new[] { ("Fake Strip", 0, "HID: /dev/hidraw3", 10), ("Fake DRAM", 1, "I2C: /dev/i2c-4, address 0x29", 4) });
        var sync = new RgbSync();
        try
        {
            var cfg = new RgbConfig { Enabled = true, Host = "127.0.0.1", Port = server.Port, Mode = RgbModes.Rainbow, Speed = 3, Gamma = false };
            sync.Configure(cfg);
            T.Check(WaitUntil(() => server.UpdatesFor(0).Count > 5 && server.UpdatesFor(1).Count > 1, 3000), "frames start flowing");
            // Direct と表示されていても、繋いだら 1 回だけ送る（Direct 用の準備をさせる。ASRock のマザーボードの赤と緑の入れ替えなど）
            T.Check(server.ModeSets.Count(x => x == 0) == 1 && server.ModeSets.Count(x => x == 1) == 1, "SetCustomMode once per device on connect");

            Thread.Sleep(200);
            double t1 = AudioClock.Now;
            Thread.Sleep(1000);
            double t2 = AudioClock.Now;
            int strip = server.UpdatesFor(0).Count(u => u.T >= t1 && u.T < t2);
            int gpu = server.UpdatesFor(1).Count(u => u.T >= t1 && u.T < t2);
            T.Info($"loopback rates: strip {strip / (t2 - t1):F1} fps, GPU {gpu / (t2 - t1):F1} fps");
            T.Check(strip >= 35 && strip <= 66, $"fast device ~60 fps ({strip} in {t2 - t1:F2} s)");
            T.Check(gpu >= 5 && gpu <= 10, $"slow device ~8 fps ({gpu} in {t2 - t1:F2} s)");
            T.Check(server.UpdatesFor(0).All(u => u.Leds == 10 && u.PayloadLength == 6 + 40 && u.DataSize == 46), "UPDATELEDS sizes for the strip");
            T.Check(sync.Status.StartsWith("接続中: 2 個"), $"status: {sync.Status}");
            var st = sync.DeviceStatus;
            T.Check(st.Count == 2 && st[1].Slow && st[0].Fps > 30 && st[1].Fps is > 4 and < 11, $"device status ({string.Join(" / ", st.Select(s => $"{s.Name} {s.Fps}"))})");
            var frame = sync.GetLastFrame("Fake Strip");
            T.Check(frame is { Length: 10 }, "last frame available");
            T.Check(!ReferenceEquals(frame, sync.GetLastFrame("Fake Strip")), "last frame is a copy");
            T.Check(sync.GetLastFrame("Nope") is null, "unknown device → null");

            // 再接続ボタン
            int conns = server.Connections;
            sync.Kick();
            T.Check(WaitUntil(() => server.UpdatesFor(0).Count(u => u.Conn == conns + 1) > 2, 2000), "Kick() reconnects immediately and frames continue");

            // OpenRGB 側でデバイスの一覧が変わった
            conns = server.Connections;
            server.SendDeviceListUpdated();
            T.Check(WaitUntil(() => server.UpdatesFor(0).Count(u => u.Conn == conns + 1) > 2, 2000), "DeviceListUpdated → reconnect");

            // 対象から外したデバイスは「止めるときの光り方」（消す）にして、それ以降は送らない
            Thread.Sleep(200);
            var cfg2 = Clone(cfg);
            cfg2.Devices = new List<string> { "Fake Strip", "Ghost" };
            sync.Configure(cfg2);
            Thread.Sleep(300);
            var gpuUpdates = server.UpdatesFor(1);
            T.Check(gpuUpdates.Count > 0 && gpuUpdates[^1].Colors.All(c => c == Rgb.Black), "deselected device gets the OnStop frame (black)");
            int gpuCount = gpuUpdates.Count;
            Thread.Sleep(300);
            T.Check(server.UpdatesFor(1).Count == gpuCount, "no more frames to the deselected device");
            T.Check(sync.Status.Contains("見つかりません: Ghost"), $"missing device reported: {sync.Status}");
            T.Check(sync.DeviceStatus.Any(s => s.Name == "Ghost" && s.Leds == 0 && s.LastError.Length > 0), "missing device listed in device status");

            // 連携をオフ（OnStop = Static）
            var cfg3 = Clone(cfg2);
            cfg3.Enabled = false;
            cfg3.OnStop = RgbStopBehaviors.Static;
            cfg3.StaticColor = "#00FF00";
            cfg3.Brightness = 100;
            var sw = Stopwatch.StartNew();
            sync.Configure(cfg3);
            long stopMs = sw.ElapsedMilliseconds;
            T.Check(stopMs < 600, $"disabling returns quickly ({stopMs} ms)");
            Thread.Sleep(150);
            var last = server.UpdatesFor(0)[^1];
            T.Check(last.Colors.All(c => c == new Rgb(0, 255, 0)), $"OnStop Static sent on disable ({last.Colors[0].Hex})");
            int stripCount = server.UpdatesFor(0).Count;
            Thread.Sleep(200);
            T.Check(server.UpdatesFor(0).Count == stripCount && sync.Status == "停止中", "nothing sent after stopping");

            // すぐにオン → オフ → オン（2 本のスレッドが同時に送らない）
            sync.Configure(cfg);
            sync.Configure(cfg3);
            sync.Configure(cfg);
            T.Check(WaitUntil(() => server.UpdatesFor(0).Count > stripCount + 20, 3000), "quick disable/enable resumes");
            Thread.Sleep(500);
            var recent = server.UpdatesFor(0).Where(u => u.T > AudioClock.Now - 0.5).ToList();
            T.Check(recent.Count <= 33, $"only one render loop runs ({recent.Count} frames in 0.5 s)");

            // スリープ → 復帰
            sync.Suspend();
            T.Check(sync.Status == "スリープ中", "suspended");
            Thread.Sleep(100);
            var afterSuspend = server.UpdatesFor(0)[^1];
            T.Check(afterSuspend.Colors.All(c => c == Rgb.Black), "suspend applies OnStop (off)");
            conns = server.Connections;
            sync.Resume();
            T.Check(WaitUntil(() => server.UpdatesFor(0).Count(u => u.Conn == conns + 1) > 2, 2000), "resume reconnects and renders");

            // アプリの終了（Dispose）
            sw.Restart();
            sync.Dispose();
            T.Check(sw.ElapsedMilliseconds < 600, $"dispose returns quickly ({sw.ElapsedMilliseconds} ms)");
            Thread.Sleep(150);
            T.Check(server.UpdatesFor(0)[^1].Colors.All(c => c == Rgb.Black), "exit applies OnStop (off)");
        }
        finally
        {
            sync.Dispose();
        }
    }

    public static void TestModeProbeWithoutReplyKeepsRendering()
    {
        // 接続中のモードの問い合わせに OpenRGB が答えない（デバイスが消えた直後など）: 1 秒だけ待って送り続ける。
        // エラーにして繋ぎ直したり、何度も待って止まったりしない
        using var server = new FakeOpenRgbServer(new[] { ("Fake Strip", 0, "HID: /dev/hidraw3", 10), ("Fake DRAM", 1, "I2C: /dev/i2c-4, address 0x29", 4) });
        server.SilentProbeDevice = 1;
        var sync = new RgbSync();
        try
        {
            sync.Configure(new RgbConfig { Enabled = true, Host = "127.0.0.1", Port = server.Port, Mode = RgbModes.Rainbow, Speed = 3, Gamma = false });
            var sw = Stopwatch.StartNew();
            T.Check(WaitUntil(() => server.UpdatesFor(0).Count > 5 && server.UpdatesFor(1).Count > 1, 4000), $"frames flow although the probe is not answered ({sw.ElapsedMilliseconds} ms)");
            T.Check(sw.ElapsedMilliseconds < 900, $"the session does not wait for the reply ({sw.ElapsedMilliseconds} ms)");
            Thread.Sleep(1500);
            T.Check(server.Connections == 1, $"no reconnect loop ({server.Connections} connections)");
            T.Check(sync.Status.StartsWith("接続中"), $"no error status: {sync.Status}");
            // 遅れて届いた返事は読み捨てられ、送り続ける
            int n = server.UpdatesFor(0).Count;
            server.ReplyLate(1);
            T.Check(WaitUntil(() => server.UpdatesFor(0).Count > n + 20, 2000) && server.Connections == 1, "a late reply is skipped without breaking the session");
        }
        finally
        {
            sync.Dispose();
        }

        // 問い合わせは、デバイス番号の合う返事だけを受け取る（遅れて届いたほかのデバイスの返事を取り違えない）
        server.SilentProbeDevice = 1;
        using var client = new OpenRgbClient();
        client.Connect("127.0.0.1", server.Port);
        client.GetDevices();
        var sw2 = Stopwatch.StartNew();
        bool timedOut = false;
        try { client.GetActiveMode(1); }
        catch (TimeoutException) { timedOut = true; }
        T.Check(timedOut && sw2.ElapsedMilliseconds is >= 900 and < 2000, $"no reply → TimeoutException after ~1 s ({sw2.ElapsedMilliseconds} ms)");
        server.ReplyLate(1);
        Thread.Sleep(100);
        T.Check(client.GetActiveMode(0) == "Direct", "the next probe skips the late reply for the other device");
    }

    public static void TestModeCheckDoesNotStallAndRestoresDirect()
    {
        // OpenRGB 1.1 はグラボへの書き込みが終わるまでモードの返事を待たせることがある。返事を待たないので LED は止まらない。
        // ほかのソフトにモードを変えられたら、直接色を指定するモードに戻す
        using var server = new FakeOpenRgbServer(new[] { ("Fake Strip", 0, "HID: /dev/hidraw3", 10), ("Fake DRAM", 1, "I2C: /dev/i2c-4, address 0x29", 4) });
        server.ReplyDelayMs = 300;
        double saved = RgbSync.ModeRefreshInterval;
        RgbSync.ModeRefreshInterval = 0.5;
        var sync = new RgbSync();
        try
        {
            sync.Configure(new RgbConfig { Enabled = true, Host = "127.0.0.1", Port = server.Port, Mode = RgbModes.Rainbow, Speed = 3, Gamma = false });
            T.Check(WaitUntil(() => server.UpdatesFor(0).Count > 5, 3000), "frames start");
            Thread.Sleep(3000);   // モードの確認が 5 回ほど行われる（返事はどれも 0.3 秒遅れる）
            var ups = server.UpdatesFor(0).Where(u => u.T > AudioClock.Now - 2.5).Select(u => u.T).ToList();
            double gap = 0;
            for (int i = 1; i < ups.Count; i++) gap = Math.Max(gap, ups[i] - ups[i - 1]);
            T.Check(ups.Count > 100 && gap < 0.12, $"no frame gap while mode replies are slow (largest gap {gap * 1000:F0} ms, {ups.Count} frames)");
            int sets = server.ModeSets.Count(x => x == 0);
            T.Check(sets == 1 && server.Connections == 1, $"Direct mode unchanged → not sent again ({sets} sets, {server.Connections} connections)");

            server.ModeName = "Rainbow Wave";   // ほかのソフトがモードを変えた
            T.Check(WaitUntil(() => server.ModeSets.Count(x => x == 0) > sets, 3000), "a mode changed by other software is set back to Direct");
            T.Check(server.Connections == 1 && sync.Status.StartsWith("接続中"), $"without reconnecting ({sync.Status})");
        }
        finally
        {
            RgbSync.ModeRefreshInterval = saved;
            sync.Dispose();
        }
    }

    public static void TestDirectModeOnConnect()
    {
        // ふつうのデバイスは繋ぐたびに 1 回、ちかちかしやすい ASRock の Radeon は Direct なら送らない（「再接続」ボタンでは送る）
        using var server = new FakeOpenRgbServer(new[] { ("Fake Strip", 0, "HID: /dev/hidraw3", 10), ("ASRock Radeon RX 7900 XTX Phantom Gaming", 2, "I2C: /dev/i2c-5, address 0x2E", 4) });
        var sync = new RgbSync();
        try
        {
            sync.Configure(new RgbConfig { Enabled = true, Host = "127.0.0.1", Port = server.Port, Mode = RgbModes.Static, Gamma = false });
            T.Check(WaitUntil(() => server.UpdatesFor(0).Count > 0 && server.UpdatesFor(1).Count > 0, 3000), "frames start");
            T.Check(server.ModeSets.Count(x => x == 0) == 1 && !server.ModeSets.Contains(1), "connect: strip set to Direct, flicker-prone GPU (already Direct) untouched");

            int conns = server.Connections;
            server.SendDeviceListUpdated();
            T.Check(WaitUntil(() => server.UpdatesFor(1).Any(u => u.Conn == conns + 1), 3000), "reconnect after the device list changed");
            T.Check(server.ModeSets.Count(x => x == 0) == 2 && !server.ModeSets.Contains(1), "reconnect: strip set again, GPU still untouched");

            conns = server.Connections;
            sync.Kick();
            T.Check(WaitUntil(() => server.UpdatesFor(1).Any(u => u.Conn == conns + 1), 3000), "Kick reconnects");
            T.Check(server.ModeSets.Count(x => x == 1) == 1, "the 再接続 button re-initialises the flicker-prone GPU once");

            conns = server.Connections;
            server.SendDeviceListUpdated();
            T.Check(WaitUntil(() => server.UpdatesFor(1).Any(u => u.Conn == conns + 1), 3000), "reconnect again");
            T.Check(server.ModeSets.Count(x => x == 1) == 1, "after that, the GPU is not set again");
        }
        finally
        {
            sync.Dispose();
        }
    }

    public static void TestWhiteOnlyStopFrame()
    {
        // 白だけのキーボード（G610）は、止めるときの色も明るさにする（#6A00FF → 赤 37 ではなく 255 の白）
        using var server = new FakeOpenRgbServer(new[] { ("Logitech G610 Orion", 5, "HID: /dev/hidraw7", 10) });
        var sync = new RgbSync();
        try
        {
            var cfg = new RgbConfig { Enabled = true, Host = "127.0.0.1", Port = server.Port, Mode = RgbModes.Static, StaticColor = "#6A00FF", Brightness = 100 };
            sync.Configure(cfg);
            T.Check(WaitUntil(() => server.UpdatesFor(0).Count > 0, 3000), "keyboard receives frames");
            T.Check(server.UpdatesFor(0)[^1].Colors.All(c => c.R == c.G && c.G == c.B), "running: white only");
            var off = Clone(cfg);
            off.Enabled = false;
            off.OnStop = RgbStopBehaviors.Static;
            sync.Configure(off);
            Thread.Sleep(200);
            var last = server.UpdatesFor(0)[^1].Colors;
            T.Check(last.All(c => c == new Rgb(255, 255, 255)), $"stop frame is white at full level ({last[0].Hex})");
        }
        finally
        {
            sync.Dispose();
        }
    }

    public static void TestReconnectBackoffAndKick()
    {
        // 使われていないポート
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        int port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();

        var sync = new RgbSync();
        try
        {
            sync.Configure(new RgbConfig { Enabled = true, Host = "127.0.0.1", Port = port, Mode = RgbModes.Static });
            // Windows では拒否された接続が失敗するまでに約 2 秒かかる（Linux ではすぐ失敗する）。2 回失敗するまで待てるよう長めに取る
            T.Check(WaitUntil(() => sync.Status.Contains("2 秒後"), 8000), $"backoff grows after repeated failures: {sync.Status}");
            T.Check(!sync.Status.Contains("One or more errors") && !sync.Status.Contains("1 つ以上"), "real error message, not AggregateException");

            using var server = new FakeOpenRgbServer(new[] { ("Fake Strip", 0, "HID: /dev/hidraw3", 10) }, port);
            var sw = Stopwatch.StartNew();
            sync.Kick();
            T.Check(WaitUntil(() => server.UpdatesFor(0).Count > 0, 1500) && sw.ElapsedMilliseconds < 1000, $"Kick() skips the backoff wait ({sw.ElapsedMilliseconds} ms)");
            T.Check(WaitUntil(() => sync.Status.StartsWith("接続中"), 1000), $"connected: {sync.Status}");

            // 止まっているモードは変化がなければ送らない
            Thread.Sleep(300);
            int n = server.UpdatesFor(0).Count;
            Thread.Sleep(500);
            var after = server.UpdatesFor(0).Skip(n).ToList();
            T.Check(after.Count is >= 1 and <= 4 && after.All(u => u.Colors.SequenceEqual(server.UpdatesFor(0)[n - 1].Colors)),
                $"static mode keeps the ARGB line alive with the same colour every 0.2 s ({after.Count} in 0.5 s)");
        }
        finally
        {
            sync.Dispose();
        }
    }

    public static void TestStopDoesNotBlockOnHungServer()
    {
        // 接続は受け付けるが返事をしないサーバー（接続の途中で止まる）
        using var server = new FakeOpenRgbServer(new[] { ("Fake Strip", 0, "HID: /dev/hidraw3", 10) });
        server.AnswerProtocol = false;
        server.Hang = true;
        var sync = new RgbSync();
        try
        {
            var cfg = new RgbConfig { Enabled = true, Host = "127.0.0.1", Port = server.Port, Mode = RgbModes.Rainbow };
            sync.Configure(cfg);
            T.Check(WaitUntil(() => server.Connections == 1, 1000), "connected to the hung server");
            Thread.Sleep(1300); // 版 0 とみなして一覧を求め、返事を待って止まっている
            var sw = Stopwatch.StartNew();
            sync.Stop();
            T.Check(sw.ElapsedMilliseconds < 700, $"Stop() does not block the UI thread ({sw.ElapsedMilliseconds} ms)");
            server.Hang = false;
            server.AnswerProtocol = true;
            sync.Configure(cfg);
            T.Check(WaitUntil(() => server.UpdatesFor(0).Count > 5, 4000), "restart after a hung stop works");
        }
        finally
        {
            sync.Dispose();
        }
    }

    private static RgbConfig Clone(RgbConfig c) =>
        System.Text.Json.JsonSerializer.Deserialize<RgbConfig>(System.Text.Json.JsonSerializer.Serialize(c))!;

    /// <summary>
    /// テスト用の偽の OpenRGB SDK サーバー（127.0.0.1）。版・デバイスの数・コントローラー情報に答え、
    /// SetCustomMode と UPDATELEDS を時刻つきで記録する
    /// </summary>
    private sealed class FakeOpenRgbServer : IDisposable
    {
        /// <param name="Conn">何番目の接続で届いたか（1 から）</param>
        public sealed record Update(double T, int Device, int Leds, int PayloadLength, int DataSize, Rgb[] Colors, int Conn);

        private readonly TcpListener _listener;
        private readonly Thread _thread;
        private readonly (string Name, int Type, string Location, int Leds)[] _devices;
        private readonly object _gate = new();
        private readonly object _writeGate = new();
        private readonly List<Update> _updates = new();
        private readonly List<int> _modeSets = new();
        private volatile NetworkStream? _current;
        private volatile bool _stop;
        private int _connections;

        public FakeOpenRgbServer((string Name, int Type, string Location, int Leds)[] devices, int port = 0)
        {
            _devices = devices;
            _listener = new TcpListener(IPAddress.Loopback, port);
            _listener.Start();
            Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
            _thread = new Thread(Run) { IsBackground = true, Name = "Fake OpenRGB" };
            _thread.Start();
        }

        public int Port { get; }
        /// <summary>このデバイスのコントローラー情報には、接続ごとの最初の 1 回（一覧を読むとき）だけ答える（-1 = すべて答える）</summary>
        public volatile int SilentProbeDevice = -1;
        /// <summary>2 回目からのコントローラー情報の返事を、この時間だけ遅らせる（別のスレッドから返す。ミリ秒）</summary>
        public volatile int ReplyDelayMs;
        /// <summary>選ばれているモードの名前（ほかのソフトにモードを変えられたことにする）。null なら "Direct"</summary>
        public volatile string? ModeName;
        public volatile bool AnswerProtocol = true;
        /// <summary>何にも返事をしない（止まったサーバー）</summary>
        public volatile bool Hang;
        public int Connections => Volatile.Read(ref _connections);
        public List<int> ModeSets { get { lock (_gate) return _modeSets.ToList(); } }

        public List<Update> UpdatesFor(int device)
        {
            lock (_gate) return _updates.Where(u => u.Device == device).ToList();
        }

        /// <summary>答えなかったコントローラー情報を、いまになって返す（遅れて届く返事）</summary>
        public void ReplyLate(int dev)
        {
            var s = _current;
            if (s is null) return;
            var d = _devices[dev];
            Reply(s, (uint)dev, 1, Blob(d.Name, d.Type, d.Location, d.Leds, 1));
        }

        public void SendDeviceListUpdated()
        {
            var s = _current;
            if (s is null) return;
            var h = new byte[16];
            OpenRgbClient.WriteHeader(h, 0, OpenRgbClient.DeviceListUpdated, 0);
            lock (_writeGate) s.Write(h);
        }

        private void Run()
        {
            while (!_stop)
            {
                TcpClient c;
                try { c = _listener.AcceptTcpClient(); }
                catch { return; }
                using (c)
                {
                    var s = c.GetStream();
                    _current = s; // 先に設定してから数を増やす（テストが数を見て送ったときに必ず届くように）
                    int conn = Interlocked.Increment(ref _connections);
                    try { Serve(s, conn); } catch { /* 相手が切った */ }
                    _current = null;
                }
            }
        }

        private void Serve(NetworkStream s, int conn)
        {
            var header = new byte[16];
            var answered = new HashSet<uint>();
            while (!_stop)
            {
                s.ReadExactly(header);
                uint dev = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(4));
                uint id = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(8));
                int size = (int)BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(12));
                var payload = new byte[size];
                s.ReadExactly(payload);
                if (Hang) continue;
                switch (id)
                {
                    case 40: // 版
                        if (AnswerProtocol) Reply(s, 0, 40, BitConverter.GetBytes(1u));
                        break;
                    case 0: // デバイスの数
                        Reply(s, 0, 0, BitConverter.GetBytes((uint)_devices.Length));
                        break;
                    case 1: // コントローラー情報
                    {
                        if (dev == SilentProbeDevice && !answered.Add(dev)) break;   // 2 回目からは答えない
                        bool first = answered.Add(dev);
                        uint proto = payload.Length >= 4 ? BinaryPrimitives.ReadUInt32LittleEndian(payload) : 0;
                        var d = _devices[dev];
                        var blob = Blob(d.Name, d.Type, d.Location, d.Leds, proto, ModeName ?? "Direct");
                        int delay = ReplyDelayMs;
                        if (!first && delay > 0)
                            ThreadPool.QueueUserWorkItem(_ => { Thread.Sleep(delay); try { Reply(s, dev, 1, blob); } catch { /* 切れた */ } });
                        else
                            Reply(s, dev, 1, blob);
                        break;
                    }
                    case 1100: // SetCustomMode
                        lock (_gate) _modeSets.Add((int)dev);
                        break;
                    case 1050: // UPDATELEDS
                    {
                        int dataSize = (int)BinaryPrimitives.ReadUInt32LittleEndian(payload);
                        int leds = BinaryPrimitives.ReadUInt16LittleEndian(payload.AsSpan(4));
                        var colors = new Rgb[leds];
                        for (int i = 0; i < leds && 6 + i * 4 + 2 < payload.Length; i++)
                            colors[i] = new Rgb(payload[6 + i * 4], payload[7 + i * 4], payload[8 + i * 4]);
                        lock (_gate) _updates.Add(new Update(AudioClock.Now, (int)dev, leds, size, dataSize, colors, conn));
                        break;
                    }
                }
            }
        }

        private void Reply(NetworkStream s, uint dev, uint id, byte[] data)
        {
            var packet = new byte[16 + data.Length];
            OpenRgbClient.WriteHeader(packet, dev, id, data.Length);
            data.CopyTo(packet, 16);
            lock (_writeGate) s.Write(packet);
        }

        /// <summary>コントローラー情報（protocol 1 ならメーカー名つき）。モード 1 個・ゾーン 1 個</summary>
        public static byte[] Blob(string name, int type, string location, int leds, uint protocol, string mode = "Direct")
        {
            var ms = new MemoryStream();
            var w = new BinaryWriter(ms);
            void Str(string x) { var b = Encoding.UTF8.GetBytes(x + "\0"); w.Write((ushort)b.Length); w.Write(b); }
            w.Write(0u);
            w.Write(type);
            Str(name);
            if (protocol >= 1) Str("Vendor");
            Str("desc"); Str("1.0"); Str("SN"); Str(location);
            w.Write((ushort)1); w.Write(0);
            Str(mode); for (int k = 0; k < 9; k++) w.Write(0u); w.Write((ushort)0);
            w.Write((ushort)1);
            Str("Zone"); w.Write(1); w.Write(0u); w.Write((uint)leds); w.Write((uint)leds); w.Write((ushort)0);
            w.Write((ushort)leds); for (int l = 0; l < leds; l++) { Str($"LED {l}"); w.Write((uint)l); }
            w.Write((ushort)leds); for (int l = 0; l < leds; l++) w.Write(0u);
            var data = ms.ToArray();
            BinaryPrimitives.WriteUInt32LittleEndian(data, (uint)data.Length);
            return data;
        }

        public void Dispose()
        {
            _stop = true;
            try { _listener.Stop(); } catch { }
            try { _current?.Dispose(); } catch { }
        }
    }
}
