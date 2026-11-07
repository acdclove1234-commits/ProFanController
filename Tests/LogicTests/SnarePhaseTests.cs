// ==================================================
// SnarePhaseTests.cs - スネアで「裏拍でつかんでいる」ことを見分けられるかの調査と、その仕組みのテスト
// Dependencies: .NET 8 (MIT License)
// See CREDITS.txt for full license details.
// ==================================================

using ProFanController;

namespace LogicTests;

public static class SnarePhaseTests
{
    /// <summary>
    /// 調査（MUSIC_TIMING）: ありがちなテンポの中心（PriorCentreSec）を変えると、録音した曲の倍テンポの時間・同期・拍の上の割合と、
    /// 合成音のベンチのテンポの判定がどう変わるか
    /// </summary>
    public static void TestPriorSweep()
    {
        string? folder = Environment.GetEnvironmentVariable("MUSIC_TIMING");
        if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder)) return;
        var files = Directory.GetFiles(folder, "*.wav").Where(f => File.Exists(Path.ChangeExtension(f, ".done"))).OrderBy(f => f).ToList();
        double saved = BeatTracker.Tune.PriorCentreSec;
        try
        {
            foreach (double c in (Environment.GetEnvironmentVariable("MUSIC_PRIORS") ?? "0.5,0.55,0.6").Split(',').Select(double.Parse))
            {
                BeatTracker.Tune.PriorCentreSec = c;
                T.Info($"prior {60 / c:F0} BPM: {SweepRun(files)}");
            }
        }
        finally { BeatTracker.Tune.PriorCentreSec = saved; }
    }

    /// <summary>調査（MUSIC_TIMING）: 速いテンポ（150 BPM より上）で半分にする判定のしきい値を変えたとき</summary>
    public static void TestFastOctaveSweep()
    {
        string? folder = Environment.GetEnvironmentVariable("MUSIC_TIMING");
        if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder)) return;
        var files = Directory.GetFiles(folder, "*.wav").Where(f => File.Exists(Path.ChangeExtension(f, ".done"))).OrderBy(f => f).ToList();
        double saved = BeatTracker.Tune.FastOctaveDownRatio;
        try
        {
            foreach (double r in (Environment.GetEnvironmentVariable("MUSIC_FASTOCT") ?? "0.35,0.5,0.6,0.7").Split(',').Select(double.Parse))
            {
                BeatTracker.Tune.FastOctaveDownRatio = r;
                T.Info($"fast octave-down ratio {r:F2}: {SweepRun(files)}");
            }
        }
        finally { BeatTracker.Tune.FastOctaveDownRatio = saved; }
    }

    private static string SweepRun(List<string> files)
    {
        {
            {
                double dbl = 0, half = 0, locked = 0;
                var evals = new List<BeatEval>();
                foreach (var f in files)
                {
                    var mono = MusicWavTests.ReadWavMono(f, out int fs);
                    var e = new AudioEngine(fs) { Settings = MusicTuning.EngineSettings(new RgbConfig { Mode = RgbModes.Music }) };
                    var hops = new List<(double T, double Odf)>();
                    e.Onsets.DiagHop = (t, odf, bn) => hops.Add((t, (1.0 * bn[0] + 0.8 * bn[1] + 0.35 * bn[2] + 0.15 * bn[3]) / 2.3));
                    var run = new EngineRun(e).Feed(mono);
                    var rb = BeatReference.Track(hops, out double refBpm);
                    evals.Add(BeatEvaluator.Evaluate(run, hops, rb, refBpm));
                    foreach (var b in run.Beats)
                    {
                        if (!b.Locked || b.Bpm <= 0) continue;
                        locked++;
                        double r = b.Bpm / refBpm;
                        if (Math.Abs(r - 2) < 0.12) dbl++;
                        else if (Math.Abs(r - 0.5) < 0.03) half++;
                    }
                }
                var bench = MusicBench.Cases().Select(MusicBench.Measure).ToList();
                return $"songs synced {evals.Average(x => x.SyncedFrac):P0} on-beat {evals.Average(x => x.OnFrac):P0} " +
                       $"double {dbl / Math.Max(1, locked):P1} half {half / Math.Max(1, locked):P1} | bench " +
                       string.Join(" ", bench.Select(r => $"{r.Name.Split(' ')[0]}:{r.TempoVerdict}:{r.OnBeatFrac:P0}"));
            }
        }
    }

    /// <summary>調査（MUSIC_TIMING）: ロック中の時間のうち、物差しと同じテンポ・倍・半分でつかんでいた割合（曲ごと）</summary>
    public static void TestOctaveShareDiag()
    {
        string? folder = Environment.GetEnvironmentVariable("MUSIC_TIMING");
        if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder)) return;
        double same = 0, dbl = 0, half = 0, other = 0;
        foreach (string wav in Directory.GetFiles(folder, "*.wav").Where(f => File.Exists(Path.ChangeExtension(f, ".done"))).OrderBy(f => f))
        {
            var mono = MusicWavTests.ReadWavMono(wav, out int fs);
            var e = new AudioEngine(fs) { Settings = MusicTuning.EngineSettings(new RgbConfig { Mode = RgbModes.Music }) };
            var hops = new List<(double T, double Odf)>();
            e.Onsets.DiagHop = (t, odf, bn) => hops.Add((t, (1.0 * bn[0] + 0.8 * bn[1] + 0.35 * bn[2] + 0.15 * bn[3]) / 2.3));
            var run = new EngineRun(e).Feed(mono);
            BeatReference.Track(hops, out double refBpm);
            int s = 0, d = 0, h = 0, o = 0;
            for (int i = 0; i < run.Frames.Count; i++)
            {
                var b = run.Beats[i];
                if (!b.Locked || b.Bpm <= 0) continue;
                double r = b.Bpm / refBpm;
                if (Math.Abs(r - 1) < 0.06) s++;
                else if (Math.Abs(r - 2) < 0.12) d++;
                else if (Math.Abs(r - 0.5) < 0.03) h++;
                else o++;
            }
            int n = Math.Max(1, s + d + h + o);
            same += s; dbl += d; half += h; other += o;
            T.Info($"{Path.GetFileNameWithoutExtension(wav)} ref {refBpm,5:F1}: same {s / (double)n,4:P0} double {d / (double)n,4:P0} half {h / (double)n,4:P0} other {o / (double)n,4:P0}");
        }
        double all = Math.Max(1, same + dbl + half + other);
        T.Info($"ALL locked time: same {same / all:P1}, double {dbl / all:P1}, half {half / all:P1}, other {other / all:P1}");
    }

    /// <summary>
    /// 調査（MUSIC_TIMING=wav のフォルダー）: ロック中の 20 秒ごとに、スネアがアプリの拍の上（± 0.1 拍）と拍の間（半拍 ± 0.1 拍）の
    /// どちらに多いか。アプリの拍が物差しと合っている区間と、半拍ずれている区間で分けて数える
    /// </summary>
    public static void TestSnarePhaseDiag()
    {
        string? folder = Environment.GetEnvironmentVariable("MUSIC_TIMING");
        if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder)) return;
        double bias = MusicTiming.ReferenceBias();
        var right = new List<(int On, int Half)>();
        var off = new List<(int On, int Half)>();
        foreach (string wav in Directory.GetFiles(folder, "*.wav").Where(f => File.Exists(Path.ChangeExtension(f, ".done"))).OrderBy(f => f))
        {
            var mono = MusicWavTests.ReadWavMono(wav, out int fs);
            var e = new AudioEngine(fs) { Settings = MusicTuning.EngineSettings(new RgbConfig { Mode = RgbModes.Music }) };
            var hops = new List<(double T, double Odf)>();
            e.Onsets.DiagHop = (t, odf, bn) => hops.Add((t, (1.0 * bn[0] + 0.8 * bn[1] + 0.35 * bn[2] + 0.15 * bn[3]) / 2.3));
            var run = new EngineRun(e).Feed(mono);
            var refBeats = BeatReference.Track(hops, out _).Select(t => t - bias).ToList();
            var snares = run.Events.Where(x => x.Kind == AudioEventKind.Snare).Select(x => x.T).ToList();
            int songRight = 0, songOff = 0;
            var offWindows = new List<string>();
            for (double w = run.T0 + 10; w + 20 <= run.Frames[^1].T; w += 20)
            {
                int mid = run.Frames.FindIndex(f => f.T >= w + 10);
                if (mid < 0) break;
                var b = run.Beats[mid];
                if (!b.Locked || b.Bpm <= 0) continue;
                double p = 60 / b.Bpm, anchor = b.NextBeatAfter(w + 10);
                double Phase(double t) { double x = (t - anchor) / p; return x - Math.Floor(x); }
                var seg = snares.Where(t => t >= w && t < w + 20).ToList();
                int on = seg.Count(t => { double f = Phase(t); return f < 0.1 || f > 0.9; });
                int half = seg.Count(t => Math.Abs(Phase(t) - 0.5) < 0.1);
                int agree = 0, halfOff = 0;
                for (double t = anchor - Math.Ceiling(10 / p) * p; t < w + 20; t += p)
                {
                    if (t < w) continue;
                    double err = Math.Abs(MusicTiming.ErrTo(refBeats, t));
                    if (err < 0.07) agree++;
                    else if (Math.Abs(err - p / 2) < 0.07) halfOff++;
                }
                if (agree > 2 * halfOff) { right.Add((on, half)); songRight++; }
                else if (halfOff > 2 * agree) { off.Add((on, half)); songOff++; offWindows.Add($"{w - run.T0:F0}s on {on} half {half}"); }
            }
            T.Info($"{Path.GetFileNameWithoutExtension(wav)}: windows right {songRight}, half-off {songOff} {string.Join(", ", offWindows)}");
        }
        void Report(string name, List<(int On, int Half)> xs)
        {
            if (xs.Count == 0) { T.Info($"{name}: -"); return; }
            int Exceed(double ratio, int min) => xs.Count(x => x.Half >= min && x.Half >= ratio * Math.Max(1, x.On));
            T.Info($"{name}: {xs.Count} windows, snares on {xs.Sum(x => x.On)} half {xs.Sum(x => x.Half)}; " +
                   $"half>=1.5x&>=3: {Exceed(1.5, 3)}, half>=2x&>=3: {Exceed(2, 3)}, half>=2x&>=4: {Exceed(2, 4)}, half>=3x&>=4: {Exceed(3, 4)}");
        }
        Report("app on the reference beats", right);
        Report("app half a beat off      ", off);
    }
}
