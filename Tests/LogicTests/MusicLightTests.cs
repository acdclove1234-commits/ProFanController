// ==================================================
// MusicLightTests.cs - 音から LED の光までを通しで動かして、光り方を数値で測る
//   (合成した音を分析しながら 60 コマ/秒で描き、LED の明るさの変化を記録する。
//    速いアルペジオでちかちかしないか・高音のメロディに反応するか・キックがはっきり光るか)
// Dependencies: .NET 8 (MIT License)
// See CREDITS.txt for full license details.
// ==================================================

using ProFanController;

namespace LogicTests;

/// <summary>音を流しながら光を描いた結果（1 コマごとの、ゾーン全体の明るさの平均。0〜1）</summary>
public sealed record LightRun(double[] T, double[] Brightness)
{
    /// <summary>1 コマごとの色（明るさをそろえた RGB の向き。色の変わり方を見る）</summary>
    public double[][] Hue { get; init; } = Array.Empty<double[]>();
    /// <summary>流しているあいだの出来事（キック・スネアなど）</summary>
    public List<AudioEvent> Events { get; init; } = new();

    /// <summary>色がはっきり変わった回数（1 秒あたり。前の 3 コマから色の向きが 0.25 以上変わったら 1 回）</summary>
    public double ColorChangesPerSec(double from, double to)
    {
        int n = 0;
        double lastChange = double.NegativeInfinity;
        for (int i = 3; i < T.Length; i++)
        {
            if (T[i] < from || T[i] >= to) continue;
            double d = 0;
            for (int c = 0; c < 3; c++) d += Math.Abs(Hue[i][c] - Hue[i - 3][c]);
            if (d >= 0.25 && T[i] - lastChange > 0.1) { n++; lastChange = T[i]; }
        }
        return n / (to - from);
    }

    /// <summary>[from, to) 秒のコマ</summary>
    public IEnumerable<(double T, double B)> Between(double from, double to)
    {
        for (int i = 0; i < T.Length; i++) if (T[i] >= from && T[i] < to) yield return (T[i], Brightness[i]);
    }

    /// <summary>はっきりした明るさの山（前後の谷から prominence 以上高い山）の 1 秒あたりの数</summary>
    public double PeaksPerSec(double from, double to, double prominence = 0.12)
    {
        var b = Between(from, to).Select(x => x.B).ToArray();
        if (b.Length < 3) return 0;
        int peaks = 0;
        double lastValley = b[0], candidate = double.NaN;
        bool rising = true;
        for (int i = 1; i < b.Length; i++)
        {
            if (rising)
            {
                if (b[i] < b[i - 1])
                {
                    candidate = b[i - 1];
                    rising = false;
                }
                else if (b[i] < lastValley) lastValley = b[i];
            }
            else
            {
                if (b[i] > b[i - 1])
                {
                    // 山の両側の谷のうち高いほうから測る
                    double valley = Math.Max(lastValley, b[i - 1]);
                    if (candidate - valley >= prominence) peaks++;
                    lastValley = b[i - 1];
                    rising = true;
                }
            }
        }
        return peaks / (to - from);
    }

    /// <summary>1 秒あたりの明るさの変わり幅の合計（コマ間の差の絶対値の合計。大きいほど激しく変わる）</summary>
    public double ChangePerSec(double from, double to)
    {
        var b = Between(from, to).Select(x => x.B).ToArray();
        double s = 0;
        for (int i = 1; i < b.Length; i++) s += Math.Abs(b[i] - b[i - 1]);
        return s / (to - from);
    }

    /// <summary>明るさの上位 10% と下位 10% の差（光が音に合わせて動いている幅）</summary>
    public double Swing(double from, double to)
    {
        var b = Between(from, to).Select(x => x.B).OrderBy(x => x).ToArray();
        if (b.Length == 0) return 0;
        return b[(int)(0.9 * (b.Length - 1))] - b[(int)(0.1 * (b.Length - 1))];
    }

    /// <summary>時刻のリストの直後 [a, b) 秒の明るさの平均</summary>
    public double MeanAfter(IEnumerable<double> times, double a, double b)
    {
        var list = new List<double>();
        foreach (double t in times)
            list.AddRange(Between(t + a, t + b).Select(x => x.B));
        return list.Count > 0 ? list.Average() : 0;
    }
}

public static class MusicLight
{
    /// <summary>描くコマの時刻を、音より何秒遅らせるか（アプリの「音の遅れ」と同じ考え方。分析が追いつくのを待つ）</summary>
    private const double Latency = 0.08;

    /// <summary>buf を流しながら、30 LED のテープを 60 コマ/秒で描く</summary>
    public static LightRun Run(float[] buf, int fs, ResolvedMusic m)
    {
        var e = new AudioEngine(fs) { Settings = MusicTuning.EngineSettings(new RgbConfig { Mode = RgbModes.Music }) };
        var dev = new RgbDevice(2, "Strip", 30, 4, null, "HID: x");
        var r = new DeviceRenderer(dev);
        var input = new RenderInput
        {
            Cfg = new RgbConfig { Mode = RgbModes.Music, Brightness = 100, Gamma = false },
            Music = m,
            Device = new RgbDeviceSettings { Name = dev.Name },
        };
        var output = new Rgb[dev.LedCount];
        const double t0 = 1000;
        long cursor = 0;
        int pos = 0, chunk = 480;
        double last = double.NaN;
        var ts = new List<double>();
        var bs = new List<double>();
        var hs = new List<double[]>();
        var evs = new List<AudioEvent>();
        double total = buf.Length / (double)fs;
        for (int k = 0; ; k++)
        {
            double sec = k / 60.0;
            if (sec + Latency > total) break;
            // 描く時刻 + 遅れの分まで、音を流しておく
            while (pos < buf.Length && pos / (double)fs < sec + Latency)
            {
                int n = Math.Min(chunk, buf.Length - pos);
                e.Push(new ReadOnlySpan<float>(buf, pos, n), t0 + pos / (double)fs);
                pos += n;
            }
            double tEval = t0 + sec;
            input.T = input.TEval = tEval;
            input.TPrevEval = double.IsNaN(last) ? tEval : last;
            input.Dt = double.IsNaN(last) ? 0 : tEval - last;
            input.Beat = e.Beat;
            input.Features = e.Features;
            if (!e.Features.Sample(tEval, input.Frame)) input.Frame.Clear();
            input.Events.Clear();
            e.Events.Read(ref cursor, tEval, input.Events);
            evs.AddRange(input.Events);
            r.Render(input, output);
            last = tEval;
            var l = r.LastLight;
            double v = 0, cr = 0, cg = 0, cb = 0;
            for (int i = 0; i < dev.LedCount; i++) { v += l[i].MaxChannel; cr += l[i].R; cg += l[i].G; cb += l[i].B; }
            double sum = cr + cg + cb;
            ts.Add(sec);
            bs.Add(v / dev.LedCount);
            hs.Add(sum > 1e-6 ? new[] { cr / sum, cg / sum, cb / sum } : new[] { 1 / 3.0, 1 / 3.0, 1 / 3.0 });
        }
        return new LightRun(ts.ToArray(), bs.ToArray()) { Hue = hs.ToArray(), Events = evs };
    }

    /// <summary>今の設定（強さ Moderate・ビートで色替え）の光り方</summary>
    public static ResolvedMusic Default(string style = MusicStyles.BeatHue)
    {
        var cfg = new RgbConfig { Mode = RgbModes.Music, MusicStyle = style };
        return MusicTuning.Resolve(cfg);
    }

    /// <summary>減衰する音（倍音つき）。撥弦（アルペジオ）やベルの代わり</summary>
    public static void Pluck(SignalGen g, double t, double freq, double amp, double decaySec, double harmonics = 3)
    {
        int fs = g.Fs;
        int i0 = (int)(t * fs), n = (int)(Math.Min(decaySec * 5, 2.0) * fs);
        for (int i = 0; i < n && i0 + i < g.Buf.Length; i++)
        {
            double tt = i / (double)fs;
            double env = Math.Exp(-tt / decaySec) * Math.Min(1, tt / 0.003);
            double s = 0;
            for (int h = 1; h <= harmonics; h++) s += Math.Sin(2 * Math.PI * freq * h * tt) / (h * h);
            g.Buf[i0 + i] += (float)(amp * env * s);
        }
    }

    /// <summary>速いアルペジオ（ドラムなし、1 秒に notesPerSec 音、和音を上下する）と小さなパッド</summary>
    public static (SignalGen G, List<double> Notes) Arpeggio(double notesPerSec = 12, double seconds = 16, double register = 1)
    {
        var g = new SignalGen(48000, seconds, seed: 31);
        double[] chord = new[] { 523.3, 659.3, 784.0, 1046.5, 784.0, 659.3 }.Select(f => f * register).ToArray();
        var notes = new List<double>();
        int k = 0;
        for (double t = 0.3; t < seconds - 0.3; t += 1 / notesPerSec, k++)
        {
            Pluck(g, t, chord[k % chord.Length] * (k / 24 % 2 == 0 ? 1 : 0.89), 0.18, 0.18);
            notes.Add(t);
        }
        g.Tone(261.6, 0.02, 0.2, seconds);
        g.Pink(-48, 0.2, seconds);
        return (g, notes);
    }

    /// <summary>ゆっくりした高音のベルのメロディ（ドラムなし、1 秒に 2 音、2〜4 kHz）</summary>
    public static (SignalGen G, List<double> Notes) TrebleMelody(double seconds = 16)
    {
        var g = new SignalGen(48000, seconds, seed: 32);
        double[] mel = { 2093, 2349, 2637, 3136, 2637, 2349, 3520, 3136 };
        var notes = new List<double>();
        int k = 0;
        for (double t = 0.4; t < seconds - 0.5; t += 0.5, k++)
        {
            Pluck(g, t, mel[k % mel.Length], 0.14, 0.45, 2);
            notes.Add(t);
        }
        g.Tone(196, 0.02, 0.2, seconds);
        g.Pink(-50, 0.2, seconds);
        return (g, notes);
    }
}

public static class MusicLightTests
{
    /// <summary>調査用: 環境変数 LIGHT_REPORT=1 のとき、3 つの合成音での光り方の数値を表示する（BEAT_TUNE も使える）</summary>
    public static void TestLightReport()
    {
        if (Environment.GetEnvironmentVariable("LIGHT_REPORT") != "1") return;
        // LIGHT_SWEEP に「名前=値;…|…」を入れると、組ごとに表示する
        foreach (var spec in (Environment.GetEnvironmentVariable("LIGHT_SWEEP") ?? Environment.GetEnvironmentVariable("BEAT_TUNE") ?? "").Split('|'))
        {
            var saved = BeatTuneSweepTests.Snapshot();
            try
            {
                BeatTuneSweepTests.Apply(spec);
                T.Info($"[{(spec.Length == 0 ? "current" : spec)}]");
                Report();
            }
            finally { BeatTuneSweepTests.Restore(saved); }
        }
    }

    private static void Report()
    {
        var m = MusicLight.Default();
        foreach (var (rate, reg, name) in new[] { (12.0, 1.0, "mid 12/s"), (8.0, 1.0, "mid 8/s"), (8.0, 0.25, "low 8/s"), (6.0, 0.25, "low 6/s"), (12.0, 0.5, "low-mid 12/s"), (8.0, 0.125, "bass 8/s"), (4.0, 0.125, "bass 4/s") })
        {
            var (arp, _) = MusicLight.Arpeggio(rate, 16, reg);
            var la = MusicLight.Run(arp.Buf, arp.Fs, m);
            double secs = 11.5;
            T.Info($"arpeggio {name,-12}: peaks {la.PeaksPerSec(4, 15.5):F1}/s  swing {la.Swing(4, 15.5):F2}  color changes {la.ColorChangesPerSec(4, 15.5):F1}/s  kick {la.Events.Count(x => x.Kind == AudioEventKind.Kick && x.T > 1004) / secs:F1}/s onset {la.Events.Count(x => x.Kind == AudioEventKind.Onset && x.T > 1004) / secs:F1}/s");
        }
        var (mel, notes) = MusicLight.TrebleMelody();
        var lm = MusicLight.Run(mel.Buf, mel.Fs, m);
        var late = notes.Where(t => t > 4).ToList();
        T.Info($"treble melody 2/s: peaks {lm.PeaksPerSec(4, 15.5):F1}/s  swing {lm.Swing(4, 15.5):F2}  after note {lm.MeanAfter(late, 0.03, 0.15):F2} vs before {lm.MeanAfter(late, -0.12, -0.02):F2}  color changes {lm.ColorChangesPerSec(4, 15.5):F1}/s");
        var c = MusicBench.Cases().First(x => x.Name.StartsWith("rock"));
        var lr = MusicLight.Run(c.Gen.Buf, c.Gen.Fs, m);
        var beats = c.Beats.Where(t => t > 8).ToList();
        var kicks = c.Gen.KickTimes.Where(t => t > 8).ToList();
        T.Info($"rock 120: kick {lr.MeanAfter(kicks, 0.0, 0.08):F2} vs between {lr.MeanAfter(beats, 0.20, 0.30):F2}  peaks {lr.PeaksPerSec(8, 23):F1}/s  swing {lr.Swing(8, 23):F2}  color changes {lr.ColorChangesPerSec(8, 23):F1}/s");
    }
}

public static class MelodyDiagTests
{
    /// <summary>調査用: 環境変数 MELODY_DIAG=1 のとき、ベルのメロディで、音の前後の中音・高音の値と出来事を表示する</summary>
    public static void TestMelodyDiag()
    {
        if (Environment.GetEnvironmentVariable("MELODY_DIAG") != "1") return;
        foreach (var (name, gen) in new[] { ("bells", MusicLight.TrebleMelody()), ("arp low 6/s", MusicLight.Arpeggio(6, 16, 0.25)) })
        {
            var (g, notes) = gen;
            var e = new AudioEngine(48000) { Settings = MusicTuning.EngineSettings(new RgbConfig { Mode = RgbModes.Music }) };
            var bands = new List<(double T, double[] Bn)>();
            e.Onsets.DiagHop = (t, odf, bn) => bands.Add((t, (double[])bn.Clone()));
            var run = new EngineRun(e).Feed(g.Buf);
            T.Info($"--- {name}: events/s " + string.Join(" ", Enum.GetValues<AudioEventKind>().Select(k => $"{k} {run.Events.Count(x => x.Kind == k && x.T > run.T0 + 4) / 11.5:F1}")));
            foreach (double tn in notes.Where(t => t > 6).Take(3))
            {
                foreach (double d in new[] { -0.05, 0.03, 0.1, 0.25, 0.45 })
                {
                    var f = run.Frames.FirstOrDefault(x => x.T - run.T0 >= tn + d);
                    var b = bands.FirstOrDefault(x => x.T - run.T0 >= tn + d).Bn ?? new double[4];
                    if (f is null) continue;
                    T.Info($"  note {tn:F2} {d,5:+0.00;-0.00}s  Mid {f.Mid:F2} MidE {f.MidE:F2}  Treble {f.Treble:F2} TrebleE {f.TrebleE:F2}  Bass {f.Bass:F2} BassE {f.BassE:F2} KickEnv {f.KickEnv:F2}  flux G2 {b[2]:F1} G3 {b[3]:F1}");
                }
            }
        }
    }
}

public static class BeatHueLightTests
{
    /// <summary>
    /// 「ビートで色替え」: 高音のメロディ（ドラムなし）の 1 音ずつで明るくなり、ロックのキックははっきり光り、
    /// 速いアルペジオではちかちかしない（音から LED までを通しで動かして測る）
    /// </summary>
    public static void TestBeatHueFollowsMelodyKickWithoutFlicker()
    {
        var m = MusicLight.Default();
        var (mel, notes) = MusicLight.TrebleMelody();
        var lm = MusicLight.Run(mel.Buf, mel.Fs, m);
        var late = notes.Where(t => t > 4).ToList();
        double after = lm.MeanAfter(late, 0.03, 0.15), before = lm.MeanAfter(late, -0.12, -0.02);
        T.Check(after >= before + 0.04 && lm.Swing(4, 15.5) >= 0.2,
            $"treble melody: brighter right after each note ({after:F2} vs {before:F2}), swing {lm.Swing(4, 15.5):F2} ≥ 0.20");

        var c = MusicBench.Cases().First(x => x.Name.StartsWith("rock"));
        var lr = MusicLight.Run(c.Gen.Buf, c.Gen.Fs, m);
        double kick = lr.MeanAfter(c.Gen.KickTimes.Where(t => t > 8), 0, 0.08), between = lr.MeanAfter(c.Beats.Where(t => t > 8), 0.20, 0.30);
        T.Check(kick - between >= 0.18, $"rock: kicks stand out ({kick:F2} vs {between:F2} between beats)");

        foreach (var (rate, reg, name, maxPeaks) in new[] { (12.0, 1.0, "mid 12/s", 1.0), (6.0, 0.25, "low 6/s", 3.0) })
        {
            var (arp, _) = MusicLight.Arpeggio(rate, 16, reg);
            var la = MusicLight.Run(arp.Buf, arp.Fs, m);
            T.Check(la.PeaksPerSec(4, 15.5) <= maxPeaks && la.Swing(4, 15.5) <= 0.3,
                $"arpeggio {name}: no flicker ({la.PeaksPerSec(4, 15.5):F1} clear peaks/s ≤ {maxPeaks}, swing {la.Swing(4, 15.5):F2} ≤ 0.30)");
        }
    }
}
