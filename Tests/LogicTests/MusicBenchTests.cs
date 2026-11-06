// ==================================================
// MusicBenchTests.cs - 音楽連動の精度を、いろいろなリズムの合成音で測る (ロックするまでの時間・テンポ・
//                      光る拍の時刻のずれ)。四つ打ち以外（ロック・ヒップホップ・トラップ・ドラムンベースなど）も確かめる
// Dependencies: .NET 8 (MIT License)
// See CREDITS.txt for full license details.
// ==================================================

using ProFanController;

namespace LogicTests;

/// <summary>1 曲分の合成音と、本当の拍の時刻</summary>
public sealed record MusicCase(string Name, double Bpm, SignalGen Gen, List<double> Beats, bool HalfOk = true, bool DoubleOk = false);

public static class MusicBench
{
    public const double Seconds = 24;

    /// <summary>16 分音符で書いたドラムの並び（1 小節 = 16 マス）。'x' = 鳴らす。swing は 2 つ目の 16 分の遅れ（0〜1/3 拍）</summary>
    public static List<double> Pattern(SignalGen g, double bpm, double from, double to, string kick, string snare, string hats,
        double swing = 0, double jitterMs = 0, double driftTo = 0, int seed = 1, double kickAmp = 0.7, double snareAmp = 0.4, double hatAmp = 0.06)
    {
        var rng = new Random(seed);
        double Gauss() => Math.Sqrt(-2 * Math.Log(1 - rng.NextDouble())) * Math.Cos(2 * Math.PI * rng.NextDouble());
        var beats = new List<double>();
        double t = from;
        int step = 0;
        while (t < to - 1e-9)
        {
            // テンポが少しずつ変わる（生演奏）
            double b = driftTo > 0 ? bpm + (driftTo - bpm) * (t - from) / (to - from) : bpm;
            double p = 60 / b;
            int s = step % 16;
            if (s % 4 == 0) beats.Add(t);
            double at = t + (s % 2 == 1 ? swing * p / 2 : 0) + (jitterMs > 0 ? Gauss() * jitterMs / 1000 : 0);
            if (kick[s] == 'x') g.Kick(at, kickAmp);
            if (snare[s] == 'x') g.Snare(at, snareAmp);
            else if (snare[s] == 'g') g.Snare(at, snareAmp * 0.25);
            if (hats[s] == 'x') g.Hat(at, hatAmp);
            else if (hats[s] == 'o') g.Hat(at, hatAmp * 2);
            t += p / 4;
            step++;
        }
        return beats;
    }

    /// <summary>ベース（2 拍ごとに音が変わる）と和音</summary>
    public static void Backing(SignalGen g, double bpm, double from, double to, double level = 1)
    {
        double p = 60 / bpm;
        double[] notes = { 55, 55, 65.4, 49 };
        int k = 0;
        for (double t = from; t < to - 1e-9; t += p, k++)
        {
            double f = notes[(k / 2) % 4];
            double end = Math.Min(to, t + p - 0.01);
            g.Tone(f, 0.10 * level, t + 0.02, end);
        }
        g.Tone(261.6, 0.03 * level, from, to);
        g.Tone(329.6, 0.025 * level, from, to);
        g.Tone(392, 0.025 * level, from, to);
        g.Pink(-36 + 20 * Math.Log10(Math.Max(1e-3, level)), from, to);
    }

    public static IEnumerable<MusicCase> Cases()
    {
        const int fs = 48000;
        MusicCase Make(string name, double bpm, Func<SignalGen, List<double>> build, bool halfOk = true, bool doubleOk = false, int seed = 1)
        {
            var g = new SignalGen(fs, Seconds, seed);
            var beats = build(g);
            return new MusicCase(name, bpm, g, beats, halfOk, doubleOk);
        }

        yield return Make("four-on-the-floor 128", 128, g =>
        {
            var b = Pattern(g, 128, 0.2, Seconds, "x...x...x...x...", "....x.......x...", "..o...o...o...o.");
            Backing(g, 128, 0.2, Seconds);
            return b;
        });
        yield return Make("rock 120", 120, g =>
        {
            var b = Pattern(g, 120, 0.2, Seconds, "x.......x.x.....", "....x.......x...", "x.x.x.x.x.x.x.x.");
            Backing(g, 120, 0.2, Seconds);
            return b;
        }, seed: 2);
        yield return Make("hip-hop 90 swing", 90, g =>
        {
            var b = Pattern(g, 90, 0.2, Seconds, "x......x..x.....", "....x.......x...", "x.x.x.x.x.x.x.x.", swing: 0.33);
            Backing(g, 90, 0.2, Seconds);
            return b;
        }, seed: 3);
        yield return Make("trap 140 half-time", 140, g =>
        {
            var b = Pattern(g, 140, 0.2, Seconds, "x..........x....", "........x.......", "xxxxxxxxxxxxxxxx", hatAmp: 0.05);
            Backing(g, 70, 0.2, Seconds);
            return b;
        }, halfOk: true, seed: 4);
        yield return Make("drum & bass 174", 174, g =>
        {
            var b = Pattern(g, 174, 0.2, Seconds, "x.........x.....", "....x.......x...", "x.x.x.x.x.x.x.x.");
            Backing(g, 174, 0.2, Seconds);
            return b;
        }, seed: 5);
        yield return Make("reggaeton 95", 95, g =>
        {
            var b = Pattern(g, 95, 0.2, Seconds, "x...x...x...x...", "...x..x....x..x.", "x.x.x.x.x.x.x.x.");
            Backing(g, 95, 0.2, Seconds);
            return b;
        }, seed: 6);
        yield return Make("funk 105 syncopated", 105, g =>
        {
            var b = Pattern(g, 105, 0.2, Seconds, "x..x..x...x..x..", "..g.x..g.g..x..g", "xxxxxxxxxxxxxxxx", hatAmp: 0.04);
            Backing(g, 105, 0.2, Seconds);
            return b;
        }, seed: 7);
        yield return Make("live rock 116→124", 124, g =>
        {
            var b = Pattern(g, 116, 0.2, Seconds, "x.......x.x.....", "....x.......x...", "x.x.x.x.x.x.x.x.", jitterMs: 10, driftTo: 124, seed: 8);
            Backing(g, 120, 0.2, Seconds);
            return b;
        }, seed: 8);
        yield return Make("ballad 76 no kick", 76, g =>
        {
            var b = Pattern(g, 76, 0.2, Seconds, "................", "....x.......x...", "x.x.x.x.x.x.x.x.", hatAmp: 0.05);
            Backing(g, 76, 0.2, Seconds, 1.5);
            return b;
        }, halfOk: true, doubleOk: true, seed: 9);
        yield return Make("quiet four-on-the-floor 124", 124, g =>
        {
            var b = Pattern(g, 124, 0.2, Seconds, "x...x...x...x...", "....x.......x...", "..o...o...o...o.", kickAmp: 0.05, snareAmp: 0.03, hatAmp: 0.006);
            Backing(g, 124, 0.2, Seconds, 0.08);
            return b;
        }, seed: 10);
    }

    /// <summary>1 曲の結果</summary>
    public sealed record Result(string Name, double TrueBpm, double EstBpm, string TempoVerdict, double LockedFrac, double LockAt,
        int Flashes, double OnBeatFrac, double MedianAbsErrMs, double MedianErrMs);

    public static Result Measure(MusicCase c)
    {
        var run = new EngineRun(new AudioEngine(c.Gen.Fs)).Feed(c.Gen.Buf);
        double lockAt = double.NaN;
        int locked = 0, total = 0;
        for (int i = 0; i < run.Frames.Count; i++)
        {
            double s = run.Frames[i].T - run.T0;
            if (double.IsNaN(lockAt) && run.Beats[i].Locked) lockAt = s;
            if (s >= 8) { total++; if (run.Beats[i].Locked) locked++; }
        }
        var last = run.Beats[^1];
        double ratio = last.Bpm / c.Bpm;
        string verdict = Math.Abs(ratio - 1) <= 0.02 ? "ok" : Math.Abs(ratio - 0.5) <= 0.01 ? "half" : Math.Abs(ratio - 2) <= 0.04 ? "double"
            : Math.Abs(ratio - 1.5) <= 0.03 ? "3/2" : Math.Abs(ratio - 2 / 3.0) <= 0.015 ? "2/3" : "wrong";

        // 光る拍（ロック中に拍の頭をまたいだ時刻）が、本当の拍にどれだけ近いか（8 秒以降）
        var errs = new List<double>();
        for (int i = 1; i < run.Frames.Count; i++)
        {
            var b = run.Beats[i];
            double t0 = run.Frames[i - 1].T, t1 = run.Frames[i].T;
            if (t1 - run.T0 < 8 || !b.Locked || !b.Crossed(t0, t1)) continue;
            double beat = b.NextBeatAfter(t0) - run.T0;
            // 倍のテンポなら 8 分音符、それ以外は 4 分音符の並びと比べる
            double grid = Math.Abs(b.Bpm / c.Bpm - 2) <= 0.06 ? 0.5 : 1;
            double best = double.PositiveInfinity;
            for (int k = 0; k < c.Beats.Count; k++)
            {
                double p = k + 1 < c.Beats.Count ? c.Beats[k + 1] - c.Beats[k] : c.Beats[k] - c.Beats[k - 1];
                for (double h = 0; h < 1 - 1e-9; h += grid)
                {
                    double d = beat - (c.Beats[k] + h * p);
                    if (Math.Abs(d) < Math.Abs(best)) best = d;
                }
            }
            errs.Add(best * 1000);
        }
        double Med(IEnumerable<double> xs) { var a = xs.OrderBy(x => x).ToArray(); return a.Length == 0 ? double.NaN : a[a.Length / 2]; }
        return new Result(c.Name, c.Bpm, last.Bpm, verdict, total == 0 ? 0 : locked / (double)total, lockAt,
            errs.Count, errs.Count == 0 ? 0 : errs.Count(e => Math.Abs(e) <= 50) / (double)errs.Count, Med(errs.Select(Math.Abs)), Med(errs));
    }

    public static string Format(Result r) =>
        $"{r.Name,-28} true {r.TrueBpm,5:F1} est {r.EstBpm,6:F1} {r.TempoVerdict,-6} locked {r.LockedFrac * 100,5:F1}% (first {r.LockAt,4:F1} s)  flashes {r.Flashes,3}  on-beat {r.OnBeatFrac * 100,5:F1}%  |err| {r.MedianAbsErrMs,5:F1} ms  bias {r.MedianErrMs,6:F1} ms";
}

public static class MusicBenchTests
{
    /// <summary>いろいろなリズムで、ロック・テンポ・光る拍の時刻を測る</summary>
    public static void TestBenchMusicStyles()
    {
        foreach (var c in MusicBench.Cases())
        {
            var r = MusicBench.Measure(c);
            T.Info(MusicBench.Format(r));
            if (c.Name.StartsWith("funk"))
            {
                // 拍でない繰り返しの強い曲: ロックできなくてもよい（キックで光る）。ただしロックするなら拍の上で
                T.Check(r.Flashes == 0 || r.OnBeatFrac >= 0.9, $"{c.Name}: never flashes off the beat");
                continue;
            }
            bool tempoOk = r.TempoVerdict == "ok" || (c.HalfOk && r.TempoVerdict == "half") || (c.DoubleOk && r.TempoVerdict == "double");
            T.Check(tempoOk, $"{c.Name}: tempo {r.EstBpm:F1} BPM ({r.TempoVerdict})");
            T.Check(r.LockedFrac >= 0.8, $"{c.Name}: locked ≥ 80 % of the time after 8 s ({r.LockedFrac * 100:F0} %)");
            T.Check(r.OnBeatFrac >= 0.9 && r.MedianAbsErrMs <= 30, $"{c.Name}: flashes land on the beat ({r.OnBeatFrac * 100:F0} %, median {r.MedianAbsErrMs:F1} ms)");
        }
    }
}

/// <summary>調査用: 環境変数 MUSIC_DIAG に曲の名前の一部を入れたときだけ、テンポの追跡の途中経過を表示する</summary>
public static class MusicDiagTests
{
    public static void TestDiagTrace()
    {
        string? want = Environment.GetEnvironmentVariable("MUSIC_DIAG");
        if (string.IsNullOrEmpty(want)) return;
        foreach (var c in MusicBench.Cases().Where(c => c.Name.Contains(want, StringComparison.OrdinalIgnoreCase)))
        {
            T.Info($"--- {c.Name} (true {c.Bpm} BPM)");
            var e = new AudioEngine(c.Gen.Fs);
            var tr = e.Tracker;
            if (Environment.GetEnvironmentVariable("MUSIC_DIAG_BEATS") == "1") tr.DiagLog = m => T.Info("   " + m + $" conf {tr.Confidence:F2} pc {tr.PhaseConfidence:F2} {tr.Lock}");
            int fs = c.Gen.Fs, chunk = 480;
            double nextPrint = 1;
            for (int i = 0; i < c.Gen.Buf.Length; i += chunk)
            {
                int n = Math.Min(chunk, c.Gen.Buf.Length - i);
                e.Push(new ReadOnlySpan<float>(c.Gen.Buf, i, n), 1000 + i / (double)fs);
                double s = i / (double)fs;
                if (s < nextPrint) continue;
                nextPrint += 1;
                var r = tr.CombScores;
                var top = Enumerable.Range(0, r.Length).OrderByDescending(j => r[j]).Take(4)
                    .Select(j => $"{60 * tr.FrameRate / (tr.TauMin + j):F0}:{r[j]:F2}");
                T.Info($"{s,5:F1}s bpm {(tr.PeriodSec > 0 ? 60 / tr.PeriodSec : 0),6:F1} tempoConf {tr.TempoConfidence:F2} phaseConf {tr.PhaseConfidence:F2} conf {tr.Confidence:F2} {tr.Lock,-9} pick {(tr.DiagCombTau > 0 ? 60 * tr.FrameRate / tr.DiagCombTau : 0):F0} oct {tr.DiagOctave,2} comb {string.Join(" ", top)}");
            }
        }
    }
}

/// <summary>調査用: 環境変数 MUSIC_WAV に録音した曲（32 ビット浮動小数点の WAV）を入れたときだけ、実際の曲での分析結果を表示する</summary>
public static class MusicWavTests
{
    public static float[] ReadWavMono(string path, out int fs)
    {
        using var br = new BinaryReader(File.OpenRead(path));
        br.ReadBytes(12);
        int ch = 2;
        fs = 48000;
        int bits = 32;
        while (br.BaseStream.Position < br.BaseStream.Length)
        {
            string id = new string(br.ReadChars(4));
            int size = br.ReadInt32();
            if (id == "fmt ")
            {
                br.ReadInt16();
                ch = br.ReadInt16();
                fs = br.ReadInt32();
                br.ReadInt32();
                br.ReadInt16();
                bits = br.ReadInt16();
                br.ReadBytes(size - 16);
            }
            else if (id == "data")
            {
                int n = size / 4 / ch;
                var mono = new float[n];
                for (int i = 0; i < n; i++)
                {
                    float s = 0;
                    for (int c = 0; c < ch; c++) s += br.ReadSingle();
                    mono[i] = s / ch;
                }
                return mono;
            }
            else br.ReadBytes(size);
        }
        return Array.Empty<float>();
    }

    private static string Dist(IEnumerable<float> xs)
    {
        var a = xs.OrderBy(x => x).ToArray();
        if (a.Length == 0) return "-";
        float P(double q) => a[(int)Math.Clamp(q * (a.Length - 1), 0, a.Length - 1)];
        return $"p10 {P(0.1):F2} p50 {P(0.5):F2} p90 {P(0.9):F2} mean {a.Average():F2}";
    }

    public static void TestWavAnalysis()
    {
        string? path = Environment.GetEnvironmentVariable("MUSIC_WAV");
        if (string.IsNullOrEmpty(path) || !File.Exists(path)) return;
        var mono = ReadWavMono(path, out int fs);
        var cfg = new RgbConfig { Mode = RgbModes.Music };
        if (Environment.GetEnvironmentVariable("MUSIC_RANGE") is { } r && double.TryParse(r, out double rv)) cfg.Music.DynamicRangeDb = rv;
        var e = new AudioEngine(fs) { Settings = MusicTuning.EngineSettings(cfg) };
        var run = new EngineRun(e).Feed(mono);
        var fr = run.Frames.Where(f => f.T - run.T0 > 3).ToList();
        double secs = fr.Count * 512.0 / fs;
        T.Info($"{Path.GetFileName(path)}: {mono.Length / (double)fs:F1} s @ {fs} Hz, range {cfg.Music.DynamicRangeDb} dB");
        T.Info($"  Bass   {Dist(fr.Select(f => f.Bass))}   BassE   {Dist(fr.Select(f => f.BassE))}");
        T.Info($"  Mid    {Dist(fr.Select(f => f.Mid))}   MidE    {Dist(fr.Select(f => f.MidE))}");
        T.Info($"  Treble {Dist(fr.Select(f => f.Treble))}   TrebleE {Dist(fr.Select(f => f.TrebleE))}");
        T.Info($"  LoudVU {Dist(fr.Select(f => f.LoudVU))}   KickEnv {Dist(fr.Select(f => f.KickEnv))}   Activity {Dist(fr.Select(f => f.Activity))}");
        var ev = run.Events.Where(x => x.T - run.T0 > 3).ToList();
        T.Info($"  events/s: kick {ev.Count(x => x.Kind == AudioEventKind.Kick) / secs:F1} snare {ev.Count(x => x.Kind == AudioEventKind.Snare) / secs:F1} hat {ev.Count(x => x.Kind == AudioEventKind.Hat) / secs:F1} onset {ev.Count(x => x.Kind == AudioEventKind.Onset) / secs:F1}");
        var b = run.Beats[^1];
        T.Info($"  tempo {b.Bpm:F1} BPM, locked {run.Locks.Skip(run.Locks.Count / 3).Count(l => l == BeatLock.Locked) * 100.0 / (run.Locks.Count - run.Locks.Count / 3):F0}% of the last 2/3");
    }
}

/// <summary>スネアの見つけ方: 音の多い曲（キックと同時の 2・4 拍目のスネア、ハイハットとベースが鳴り続ける）で、見つけた割合を表示する</summary>
public static class SnareDetectionTests
{
    public static void TestSnaresFoundUnderHatsAndBass()
    {
        foreach (double bpm in new[] { 96.0, 124.0 })
        {
            var g = new SignalGen(48000, 16, seed: (int)bpm + 300);
            g.DenseMix(bpm, 0.3, 16);
            var run = new EngineRun(new AudioEngine(48000)).Feed(g.Buf);
            var truth = g.SnareTimes.Where(t => t > 3 && t < 15.5).ToList();
            var found = run.Kind(AudioEventKind.Snare).Select(e => e.T - run.T0).Where(t => t > 3 && t < 15.6).ToList();
            int hit = truth.Count(t => found.Any(f => Math.Abs(f - t) < 0.04));
            int extra = found.Count(f => truth.All(t => Math.Abs(f - t) >= 0.04));
            T.Info($"dense {bpm} BPM: snares found {hit}/{truth.Count}, extra {extra}");
            // いまは、キックと同時に鳴るスネア（クラップ）を見つけられない（見つける割合を表示するだけ。実際の曲で調整する予定）
            T.Check(extra <= 0.2 * truth.Count + 1, $"dense {bpm} BPM: few false snares ({extra})");
        }
    }
}

public static class SnareDiagTests
{
    public static void TestSnareDiag()
    {
        if (Environment.GetEnvironmentVariable("SNARE_DIAG") != "1") return;
        var g = new SignalGen(48000, 8, seed: 396);
        g.DenseMix(96, 0.3, 8);
        var e = new AudioEngine(48000);
        var o = e.Onsets;
        int hop = 512;
        var snares = g.SnareTimes.Where(t => t > 3 && t < 7).ToList();
        var kicks = g.KickTimes.Where(t => t > 3 && t < 7).ToList();
        for (int i = 0; i + hop <= g.Buf.Length; i += hop)
        {
            e.Push(new ReadOnlySpan<float>(g.Buf, i, hop), 1000 + i / 48000.0);
            double t = (i + hop) / 48000.0;
            bool nearSnare = snares.Any(s => t - s > -0.005 && t - s < 0.06);
            bool nearKickOnly = !nearSnare && kicks.Any(k => t - k > -0.005 && t - k < 0.06);
            if (!nearSnare && !nearKickOnly) continue;
            string P(int k) => $"{o.GroupNorm(k),5:F2}/{o.Picker(k).Threshold,4:F2}";
            T.Info($"{t:F3} {(nearSnare ? "SNARE" : "kick ")} G0 {P(0)} G1 {P(1)} G2 {P(2)} G3 {P(3)}");
        }
    }
}
