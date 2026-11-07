// ==================================================
// SampleRateTests.cs - 音のサンプリング周波数（48 / 96 / 192 kHz）が変わっても、音楽連動の結果が変わらないか
//   出力先の形式を 96 / 192 kHz にすると、「すべての音」の取り込みはその周波数で届く（特定のアプリだけのときは 48 kHz）。
//   分析は 44.1 / 48 kHz で調整してあるので、AudioEngine は 48 kHz に落としてから分析する（Decimator）
// Dependencies: .NET 8 (MIT License)
// See CREDITS.txt for full license details.
// ==================================================

using ProFanController;

namespace LogicTests;

public static class SampleRateTests
{
    public static void TestDecimatorKeepsBassAndRemovesUltrasound()
    {
        const int fs = 192000;
        var d = new Decimator(Decimator.FactorFor(fs));
        T.Check(d.Factor == 4 && Decimator.FactorFor(96000) == 2 && Decimator.FactorFor(176400) == 4
                && Decimator.FactorFor(384000) == 8 && Decimator.FactorFor(48000) == 1 && Decimator.FactorFor(44100) == 1
                && Decimator.FactorFor(64000) == 1, "factors: 192k→1/4, 96k→1/2, 176.4k→1/4, 384k→1/8, 48k / 44.1k / 64k unchanged");

        double Rms(double freq)
        {
            d.Reset();
            double sum = 0;
            int n = 0;
            for (int i = 0; i < fs; i++)
            {
                if (!d.Push((float)Math.Sin(2 * Math.PI * freq * i / fs), out float y)) continue;
                if (i < fs / 10) continue; // フィルターが落ち着くまで
                sum += y * y;
                n++;
            }
            return Math.Sqrt(sum / n) * Math.Sqrt(2);
        }
        double bass = Rms(60), mid = Rms(1000), high = Rms(15000), ultra = Rms(40000);
        T.Check(Math.Abs(bass - 1) < 0.01 && Math.Abs(mid - 1) < 0.01, $"bass and mids keep their level ({bass:F3}, {mid:F3})");
        T.Check(high > 0.95, $"15 kHz still passes ({high:F3})");
        T.Check(ultra < 0.01, $"40 kHz (would fold back below 24 kHz) is removed ({ultra:F4})");
    }

    public static void TestSameResultsAt96And192Khz()
    {
        var base48 = MusicBench.Cases(48000).Select(c => (c, MusicBench.Measure(c), Counts(c))).ToList();
        foreach (int fs in new[] { 96000, 192000 })
        {
            int i = 0;
            foreach (var c in MusicBench.Cases(fs))
            {
                var (_, b, (bk, bs)) = base48[i++];
                var r = MusicBench.Measure(c);
                var (k, s) = Counts(c);
                string at = $"{c.Name} @ {fs / 1000} kHz";
                T.Check(r.TempoVerdict == b.TempoVerdict, $"{at}: same tempo as 48 kHz ({r.EstBpm:F1} vs {b.EstBpm:F1})");
                T.Check(r.LockedFrac >= b.LockedFrac - 0.15, $"{at}: locked about as long ({r.LockedFrac:P0} vs {b.LockedFrac:P0})");
                T.Check(Math.Abs(k - bk) <= Math.Max(3, 0.1 * bk), $"{at}: kicks {k} vs {bk}");
                // スネアの数は少しばらつく（合成音の雑音は 1 個ずつ乱数で作るので、周波数が違うと別の雑音になる）
                T.Check(Math.Abs(s - bs) <= Math.Max(5, 0.4 * bs), $"{at}: snares {s} vs {bs}");
                if (b.Flashes > 0 && r.Flashes > 0)
                    T.Check(Math.Abs(r.MedianErrMs - b.MedianErrMs) <= 6, $"{at}: beat timing {r.MedianErrMs:F1} ms vs {b.MedianErrMs:F1} ms");
            }
        }
    }

    private static (int Kicks, int Snares) Counts(MusicCase c)
    {
        int fs = c.Gen.Fs;
        var run = new EngineRun(new AudioEngine(fs)).Feed(c.Gen.Buf, chunk: fs / 100);
        return (run.Events.Count(e => e.Kind == AudioEventKind.Kick), run.Events.Count(e => e.Kind == AudioEventKind.Snare));
    }
}
