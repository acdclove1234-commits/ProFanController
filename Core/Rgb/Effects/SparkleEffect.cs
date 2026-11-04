// ==================================================
// SparkleEffect.cs - 光り方 E8「きらめき」 (ハイハットやスネアで、ランダムな LED がきらっと光る)
// Dependencies: .NET 8 (MIT License)
// See CREDITS.txt for full license details.
// ==================================================

namespace ProFanController;

/// <summary>
/// きらめき:
///   下地 = P(t / 20 秒) × Ilin(0.12 + 0.3 × 中音)   … パレットを 20 秒で 1 周
///   ハイハット（強さ s）: ゾーンの LED のうち n = clamp(round(0.04 × N × s), 1, N/4) 個を選び、
///                         白と半々に混ぜたパレットの色で光らせる（0.08 秒で消える）
///   スネア: n = round(0.08 × N × s) 個を、パレットの色で光らせる（0.16 秒で消える）
///   LED の色 = 下地 + 粒の色 × exp(−(t − 光った時刻)/τ)
/// 粒は光った本当の時刻を覚えるので、1 秒間のコマ数が違っても同じ明るさで消えていく。
/// 乱数（xorshift32）はデバイスごとで、粒を出すときだけ使う。6 個未満のゾーンはハイハットでゾーン全体が 0.5 の明るさで光る
/// </summary>
public sealed class SparkleEffect : IMusicEffect
{
    /// <summary>下地の色がパレットを 1 周する秒数</summary>
    public const double BaseCycleSec = 20;
    /// <summary>ハイハットの粒が消える時定数（秒）</summary>
    public const float HatTauSec = 0.08f;
    /// <summary>スネアの粒が消える時定数（秒）</summary>
    public const float SnareTauSec = 0.16f;
    /// <summary>小さいゾーンでの、ハイハットの光の明るさ</summary>
    public const float SmallZoneLevel = 0.5f;

    private double[] _t0 = Array.Empty<double>();
    private float[] _tau = Array.Empty<float>();
    private LinColor[] _col = Array.Empty<LinColor>();
    private LinColor _base;

    public string Name => MusicStyles.Sparkle;

    public void Reset(EffectContext c)
    {
        int n = c.LedCount;
        if (_t0.Length != n)
        {
            _t0 = new double[n];
            _tau = new float[n];
            _col = new LinColor[n];
        }
        Array.Fill(_t0, double.NegativeInfinity);
        Array.Fill(_tau, HatTauSec);
        Array.Clear(_col);
    }

    public void Update(EffectContext c)
    {
        var ev = c.Events;
        for (int i = 0; i < ev.Count; i++)
        {
            var e = ev[i];
            if (e.Kind == AudioEventKind.Hat) Spawn(c, e.T, e.Strength, hat: true);
            else if (e.Kind == AudioEventKind.Snare) Spawn(c, e.T, e.Strength, hat: false);
        }
        _base = c.PalAt(c.T / BaseCycleSec) * c.Ilin(0.12f + 0.3f * c.Frame.MidE);
    }

    private void Spawn(EffectContext c, double t0, float strength, bool hat)
    {
        for (int k = 0; k < c.SegmentCount; k++)
        {
            var s = c.Segments[k];
            if (s.Small)
            {
                if (!hat) continue;
                // 小さいゾーンは全体を 1 つの粒として光らせる
                var zc = SparkColor(c, true) * SmallZoneLevel;
                for (int j = s.Start; j < s.Start + s.Count; j++) Set(j, t0, HatTauSec, zc);
                continue;
            }
            int n = hat
                ? Math.Clamp((int)MathF.Round(0.04f * s.Count * strength), 1, Math.Max(1, s.Count / 4))
                : (int)MathF.Round(0.08f * s.Count * strength);
            for (int q = 0; q < n; q++)
            {
                int j = s.Start + c.Rng.NextInt(s.Count);
                Set(j, t0, hat ? HatTauSec : SnareTauSec, SparkColor(c, hat));
            }
        }
    }

    /// <summary>粒の色: パレットのランダムな色（ハイハットは白と半々に混ぜる。OKLab で混ぜる）</summary>
    private static LinColor SparkColor(EffectContext c, bool hat)
    {
        double x = c.Rng.NextFloat();
        return hat ? OkLab.ToLed(Lab.Lerp(c.Pal.LabAt(x), Lab.White, 0.5f), c.Gamma) : c.PalAt(x);
    }

    private void Set(int j, double t0, float tau, LinColor col)
    {
        _t0[j] = t0;
        _tau[j] = tau;
        _col[j] = col;
    }

    public void Render(EffectContext c, in LedSegment s, LinColor[] dst)
    {
        double t = c.T;
        for (int j = s.Start; j < s.Start + s.Count; j++)
        {
            double age = t - _t0[j];
            var col = _base;
            if (age >= 0 && age < _tau[j] * 8) col += _col[j] * (float)Math.Exp(-age / _tau[j]);
            dst[j] = col.Clamp01();
        }
    }
}
