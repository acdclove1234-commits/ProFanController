// ==================================================
// MusicTimingTests.cs - 光る時刻が、音より早いか遅いかを測る（録音した曲で、アプリと同じ時間の流れを再現する）
//   ・音は 10 ミリ秒ずつ、少し遅れて届く（プロセス ループバックの取り込みと同じ）
//   ・60 コマ/秒で描き、LED は送ってから 8 ミリ秒後に光る（USB のデバイス）
//   ・物差しの拍（BeatReference）と、光が明るくなり始めた時刻を比べる（どちらも「取り込んだ音の時刻」で）。
//     耳に届く音は、さらに出力の遅れ（Windows の音の経路と DAC）の分だけ後になる
// 使い方: MUSIC_TIMING=<wav のフォルダー> [MUSIC_CONFIG=<config.json>] dotnet run -c Release TestWavTiming
// Dependencies: .NET 8 (MIT License)
// See CREDITS.txt for full license details.
// ==================================================

using ProFanController;

namespace LogicTests;

public static class MusicTiming
{
    private static readonly List<AudioEvent> _scratch = new();
    /// <summary>直前の Realtime で、出来事を見つけるまでの遅れ（ミリ秒）</summary>
    public static readonly List<(AudioEventKind Kind, double Ms)> Detect = new();

    /// <summary>描いたコマ: LED が光った時刻（取り込んだ音の時刻）・明るさ・テンポをつかんでいたか</summary>
    public sealed record Run(double[] T, double[] B, bool[] Locked, List<(double T, double Odf)> Hops, List<(AudioEventKind Kind, double T, double LagMs)> EventLags);

    /// <summary>アプリと同じ時間の流れで、音を流しながら描く</summary>
    public static Run Realtime(float[] buf, int fs, RgbConfig cfg, double deliverySec = 0.005, double ledLatencySec = 0.008)
    {
        var e = new AudioEngine(fs) { Settings = MusicTuning.EngineSettings(cfg) };
        var hops = new List<(double T, double Odf)>();
        e.Onsets.DiagHop = (t, odf, bn) => hops.Add((t, (1.0 * bn[0] + 0.8 * bn[1] + 0.35 * bn[2] + 0.15 * bn[3]) / 2.3));
        var m = MusicTuning.Resolve(cfg);
        var dev = new RgbDevice(2, "Strip", 30, 4, null, "HID: x");
        var r = new DeviceRenderer(dev);
        var input = new RenderInput
        {
            Cfg = new RgbConfig { Mode = RgbModes.Music, MusicStyle = cfg.MusicStyle, Brightness = 100, Gamma = false, StaticColor = cfg.StaticColor },
            Music = m,
            Device = new RgbDeviceSettings { Name = dev.Name },
        };
        var output = new Rgb[dev.LedCount];
        const double t0 = 1000;
        int chunk = fs / 100, pos = 0;
        long cursor = 0;
        double last = double.NaN;
        var ts = new List<double>();
        var bs = new List<double>();
        var locks = new List<bool>();
        var lags = new List<(AudioEventKind, double, double)>();
        long detCursor = 0;
        Detect.Clear();
        double total = buf.Length / (double)fs;
        for (int k = 0; ; k++)
        {
            double now = t0 + k / 60.0;
            if (now - t0 > total) break;
            // 届いている音: 塊の最後の音が「今 − 届くまでの遅れ」より前の塊
            while (pos < buf.Length && t0 + (pos + chunk) / (double)fs <= now - deliverySec)
            {
                int n = Math.Min(chunk, buf.Length - pos);
                e.Push(new ReadOnlySpan<float>(buf, pos, n), t0 + pos / (double)fs);
                // 見つかった瞬間（いちばん新しい音の時刻）と、出来事の時刻の差 = 見つけるまでの遅れ
                _scratch.Clear();
                e.Events.Read(ref detCursor, double.PositiveInfinity, _scratch);
                foreach (var ev in _scratch) Detect.Add((ev.Kind, (t0 + (pos + n) / (double)fs - ev.T) * 1000));
                pos += n;
            }
            double tEval = DeviceChannelTime(now, ledLatencySec, m.AudioDelayMs);
            input.T = now;
            input.TEval = tEval;
            input.TPrevEval = double.IsNaN(last) ? tEval : last;
            input.Dt = double.IsNaN(last) ? 0 : 1 / 60.0;
            input.Beat = e.Beat;
            input.Features = e.Features;
            if (!e.Features.Sample(tEval, input.Frame)) input.Frame.Clear();
            input.Events.Clear();
            e.Events.Read(ref cursor, tEval, input.Events);
            // 出来事が描く側に届いて光に出るまでの遅れ（光った時刻 − 出来事の時刻）
            foreach (var ev in input.Events) lags.Add((ev.Kind, ev.T, (now + ledLatencySec - ev.T) * 1000));
            r.Render(input, output);
            last = tEval;
            var l = r.LastLight;
            double v = 0;
            for (int i = 0; i < dev.LedCount; i++) v += l[i].MaxChannel;
            ts.Add(now + ledLatencySec);
            bs.Add(v / dev.LedCount);
            locks.Add(e.Beat.Locked);
        }
        return new Run(ts.ToArray(), bs.ToArray(), locks.ToArray(), hops, lags);
    }

    /// <summary>アプリの EvalTime と同じ（描く時刻 + デバイスの遅れ − 音の遅れの補正）</summary>
    private static double DeviceChannelTime(double now, double ledLatencySec, double audioDelayMs) => now + ledLatencySec - audioDelayMs / 1000.0;

    /// <summary>光が明るくなり始めた時刻（1 コマで明るさが大きく上がった最初のコマ）</summary>
    public static List<(double T, bool Locked)> Rises(Run run, double from)
    {
        var list = new List<(double, bool)>();
        var b = run.B;
        for (int i = 2; i < b.Length; i++)
        {
            if (run.T[i] < from) continue;
            double d = b[i] - b[i - 1], dPrev = b[i - 1] - b[i - 2];
            if (d >= 0.08 && dPrev < 0.08) list.Add((run.T[i], run.Locked[i]));
        }
        return list;
    }

    /// <summary>いちばん近い物差しの拍との差（秒。+ = 光が後）。拍の間（8 分音符）は数えない</summary>
    public static double ErrTo(List<double> beats, double t)
    {
        int k = beats.BinarySearch(t);
        if (k < 0) k = ~k;
        double best = double.PositiveInfinity;
        for (int j = Math.Max(0, k - 1); j <= Math.Min(beats.Count - 1, k); j++)
            if (Math.Abs(t - beats[j]) < Math.Abs(best)) best = t - beats[j];
        return best;
    }

    public static double Quantile(IReadOnlyList<double> xs, double q)
    {
        if (xs.Count == 0) return double.NaN;
        var a = xs.OrderBy(x => x).ToArray();
        return a[(int)Math.Round(q * (a.Length - 1))];
    }

    /// <summary>物差し（BeatReference）の拍が、本当の拍からどれだけずれるか（合成音で測る。秒。+ = 物差しが後）</summary>
    public static double ReferenceBias()
    {
        var errs = new List<double>();
        foreach (var c in MusicBench.Cases())
        {
            if (c.Name.StartsWith("funk")) continue;
            var e = new AudioEngine(c.Gen.Fs);
            var hops = new List<(double T, double Odf)>();
            e.Onsets.DiagHop = (t, odf, bn) => hops.Add((t - 1000, (1.0 * bn[0] + 0.8 * bn[1] + 0.35 * bn[2] + 0.15 * bn[3]) / 2.3));
            new EngineRun(e).Feed(c.Gen.Buf);
            var rb = BeatReference.Track(hops, out _);
            foreach (double t in rb.Where(t => t > 8))
            {
                double err = ErrTo(c.Beats, t);
                double p = 60 / c.Bpm;
                // 物差しが倍・半分のテンポのときも、拍に重なっているものだけ使う
                if (Math.Abs(err) < 0.25 * p) errs.Add(err);
            }
        }
        return Quantile(errs, 0.5);
    }
}

public static class MusicTimingTests
{
    /// <summary>
    /// 調査: スネアを、候補を見つけたコマで決めても（2 コマ待たなくても）間違えないか。
    /// 候補のコマで「キックの帯がほとんど立ち上がっていない」なら、その後にキックが見つかることは少ないはず
    /// </summary>
    public static void TestSnareEarlyDiag()
    {
        string? folder = Environment.GetEnvironmentVariable("MUSIC_TIMING");
        if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder)) return;
        var rows = new List<(double Kb, bool HatFirst, bool G1First, bool KickNear, bool HatOnly, long Rel)>();
        foreach (string wav in Directory.GetFiles(folder, "*.wav").OrderBy(f => f))
        {
            if (!File.Exists(Path.ChangeExtension(wav, ".done"))) continue;
            var mono = MusicWavTests.ReadWavMono(wav, out int fs);
            var e = new AudioEngine(fs) { Settings = MusicTuning.EngineSettings(new RgbConfig { Mode = RgbModes.Music }) };
            e.Onsets.DiagSnareFirst = (t, kb, hf, g1f, kn, ho, rel) => rows.Add((kb, hf, g1f, kn, ho, rel));
            new EngineRun(e).Feed(mono);
        }
        int window = 2;
        int emitted = rows.Count(r => !r.KickNear && !r.HatOnly);
        int kickAfter = rows.Count(r => r.KickNear && r.Rel > 0);
        int kickBefore = rows.Count(r => r.KickNear && r.Rel <= 0);
        int hatAfter = rows.Count(r => r.HatOnly && !r.HatFirst);
        T.Info($"snare candidates {rows.Count}: emitted {emitted}, kick near (known at first hop {kickBefore}, found later {kickAfter}), hat only {rows.Count(r => r.HatOnly)} (hat found later {hatAfter})");
        string Dist(IEnumerable<double> xs) { var a = xs.ToList(); return a.Count == 0 ? "-" : $"p10 {MusicTiming.Quantile(a, 0.1):F2} p50 {MusicTiming.Quantile(a, 0.5):F2} p90 {MusicTiming.Quantile(a, 0.9):F2}"; }
        T.Info($"  kick band at first hop: emitted {Dist(rows.Where(r => !r.KickNear && !r.HatOnly).Select(r => r.Kb))}; kick found later {Dist(rows.Where(r => r.KickNear && r.Rel > 0).Select(r => r.Kb))}");
        foreach (double th in new[] { 0.3, 0.5, 0.8, 1.0, 1.5, 2.0, 3.0 })
        {
            // 早く決める条件: 候補のコマまでにキックがない・キックの帯 < th・（低い中音も出ている か ハイハットがない）
            bool Early((double Kb, bool HatFirst, bool G1First, bool KickNear, bool HatOnly, long Rel) r)
                => !(r.KickNear && r.Rel <= 0 && r.Rel >= -window) && r.Kb < th && (r.G1First || !r.HatFirst);
            int early = rows.Count(Early);
            int earlyWrong = rows.Count(r => Early(r) && (r.KickNear || r.HatOnly));
            int earlyRight = rows.Count(r => Early(r) && !r.KickNear && !r.HatOnly);
            T.Info($"  threshold {th:F1}: decided early {early}, of the real snares {earlyRight}/{emitted} ({earlyRight / (double)Math.Max(1, emitted):P0}) early, wrong {earlyWrong} ({earlyWrong / (double)Math.Max(1, early):P1} of early)");
        }
    }

    public static void TestWavTiming()
    {
        string? folder = Environment.GetEnvironmentVariable("MUSIC_TIMING");
        if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder)) return;
        var cfg = new RgbConfig { Mode = RgbModes.Music };
        string? cfgPath = Environment.GetEnvironmentVariable("MUSIC_CONFIG");
        if (!string.IsNullOrEmpty(cfgPath) && File.Exists(cfgPath))
        {
            cfg = AppConfig.FromJson(File.ReadAllText(cfgPath)).Rgb;
            cfg.Mode = RgbModes.Music;
        }
        double bias = MusicTiming.ReferenceBias();
        T.Info($"style {cfg.MusicStyle}, preset {cfg.Music.Preset}, intensity {cfg.Music.Intensity}, audio delay {cfg.Music.AudioDelayMs} ms, reference bias {bias * 1000:F1} ms");
        var allLocked = new List<double>();
        var allFree = new List<double>();
        var eventLag = new Dictionary<AudioEventKind, List<double>>();
        var eventErr = new Dictionary<AudioEventKind, List<double>>();
        var detectAll = new List<(AudioEventKind Kind, double Ms)>();
        double lockedTime = 0, totalTime = 0;
        int songs = 0;
        foreach (string wav in Directory.GetFiles(folder, "*.wav").OrderBy(f => f))
        {
            if (!File.Exists(Path.ChangeExtension(wav, ".done"))) continue; // 録音中の曲は使わない
            var mono = MusicWavTests.ReadWavMono(wav, out int fs);
            if (mono.Length < fs * 20) continue;
            var run = MusicTiming.Realtime(mono, fs, cfg);
            detectAll.AddRange(MusicTiming.Detect);
            var refBeats = BeatReference.Track(run.Hops, out double refBpm).Select(t => t - bias).ToList();
            double from = 1000 + 10;
            var rises = MusicTiming.Rises(run, from);
            var locked = new List<double>();
            var free = new List<double>();
            double period = refBpm > 0 ? 60 / refBpm : 0.5;
            foreach (var (t, isLocked) in rises)
            {
                double err = MusicTiming.ErrTo(refBeats, t);
                if (Math.Abs(err) > Math.Min(0.15, 0.3 * period)) continue; // 拍から離れた光（裏拍・メロディーなど）は数えない
                (isLocked ? locked : free).Add(err * 1000);
            }
            int frames = run.T.Count(t => t >= from);
            int lockedFrames = run.T.Select((t, i) => (t, i)).Count(x => x.t >= from && run.Locked[x.i]);
            lockedTime += lockedFrames;
            totalTime += frames;
            allLocked.AddRange(locked);
            allFree.AddRange(free);
            songs++;
            // キック・スネア・ハイハットの時刻が、拍からどれだけずれて記録されたか（拍の近くのものだけ）と、光に出るまでの遅れ
            foreach (var kind in new[] { AudioEventKind.Kick, AudioEventKind.Snare, AudioEventKind.Hat })
            {
                var evs = run.EventLags.Where(x => x.Kind == kind && x.T >= from).ToList();
                if (!eventLag.ContainsKey(kind)) { eventLag[kind] = new(); eventErr[kind] = new(); }
                eventLag[kind].AddRange(evs.Select(x => x.LagMs));
                eventErr[kind].AddRange(evs.Select(x => MusicTiming.ErrTo(refBeats, x.T)).Where(x => Math.Abs(x) <= Math.Min(0.06, 0.15 * period)).Select(x => x * 1000));
            }
            string Q(List<double> xs) => xs.Count == 0 ? "   -" : $"{MusicTiming.Quantile(xs, 0.5),5:F0} ({MusicTiming.Quantile(xs, 0.1):F0}..{MusicTiming.Quantile(xs, 0.9):F0})";
            T.Info($"{Path.GetFileNameWithoutExtension(wav)} {mono.Length / (double)fs,4:F0}s {refBpm,5:F1} bpm  locked {lockedFrames / (double)Math.Max(1, frames),4:P0}  " +
                   $"locked flashes {locked.Count,4} err ms {Q(locked)}  free flashes {free.Count,4} err ms {Q(free)}  " +
                   $"kick lag {Q(run.EventLags.Where(x => x.Kind == AudioEventKind.Kick && x.T >= from).Select(x => x.LagMs).ToList())}");
        }
        if (songs == 0) return;
        string S(List<double> xs) => xs.Count == 0 ? "-" :
            $"median {MusicTiming.Quantile(xs, 0.5):F0} ms, 10-90% {MusicTiming.Quantile(xs, 0.1):F0}..{MusicTiming.Quantile(xs, 0.9):F0} ms, " +
            $"early<-40 {xs.Count(x => x < -40) / (double)xs.Count:P0}, late>+40 {xs.Count(x => x > 40) / (double)xs.Count:P0}";
        T.Info($"ALL {songs} songs, locked {lockedTime / Math.Max(1, totalTime):P0} of the time");
        T.Info($"  locked flashes ({allLocked.Count}): {S(allLocked)}");
        T.Info($"  free flashes   ({allFree.Count}): {S(allFree)}");
        foreach (var (kind, lagsOf) in eventLag)
            T.Info($"  {kind,-5} events {lagsOf.Count,5}: time vs beat {S(eventErr[kind])}; light {MusicTiming.Quantile(lagsOf, 0.5):F0} ms after the event (10-90% {MusicTiming.Quantile(lagsOf, 0.1):F0}..{MusicTiming.Quantile(lagsOf, 0.9):F0}), " +
                   $"found {MusicTiming.Quantile(detectAll.Where(d => d.Kind == kind).Select(d => d.Ms).ToList(), 0.5):F0} ms after the sound");
    }
}
