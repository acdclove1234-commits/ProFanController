// ==================================================
// Decimator.cs - 音を整数分の 1 のサンプリング周波数に落とす（192 kHz → 48 kHz など）
//   高い音を先に落としてから（線形位相の FIR。折り返しの雑音を出さない）、d 個に 1 個を残す。
//   音楽連動の分析は 44.1 / 48 kHz で調整してあるので、出力先を 96 / 192 kHz にしていても同じ結果になるよう、
//   分析の前にこれを通す（AudioEngine）
// Dependencies: .NET 8 (MIT License)
// See CREDITS.txt for full license details.
// ==================================================

namespace ProFanController;

public sealed class Decimator
{
    private readonly float[] _h;
    private readonly float[] _buf;
    private int _pos;
    private int _phase;

    /// <param name="factor">何分の 1 にするか（2 以上）</param>
    public Decimator(int factor)
    {
        if (factor < 2) throw new ArgumentOutOfRangeException(nameof(factor));
        Factor = factor;
        Taps = 32 * factor + 1;
        _h = Design(factor, Taps);
        _buf = new float[Taps * 2];
    }

    public int Factor { get; }
    public int Taps { get; }
    /// <summary>出した音の遅れ（入力のサンプル数。線形位相なので、どの高さの音も同じだけ遅れる）</summary>
    public double DelaySamples => (Taps - 1) / 2.0;

    /// <summary>
    /// 入力のサンプリング周波数から、何分の 1 にするかを決める（44.1 / 48 kHz の系列に戻す。
    /// 88.2 / 96 kHz → 1/2、176.4 / 192 kHz → 1/4、352.8 / 384 kHz → 1/8。それ以外は落とさない）
    /// </summary>
    public static int FactorFor(int sampleRate)
    {
        foreach (int d in new[] { 8, 4, 2 })
        {
            if (sampleRate % d != 0) continue;
            int r = sampleRate / d;
            if (r is 44100 or 48000) return d;
        }
        return 1;
    }

    /// <summary>1 個入れる。d 個入れるごとに 1 個出す（出したら true）</summary>
    public bool Push(float x, out float y)
    {
        // 同じ値を 2 か所に書き、直近 Taps 個がいつも続けて並ぶようにする（掛け算の途中で折り返さない）
        _buf[_pos] = x;
        _buf[_pos + Taps] = x;
        if (++_pos == Taps) _pos = 0;
        if (++_phase < Factor)
        {
            y = 0;
            return false;
        }
        _phase = 0;
        double acc = 0;
        int start = _pos; // いちばん古い値
        for (int k = 0; k < Taps; k++) acc += _h[k] * _buf[start + k];
        y = (float)acc;
        return true;
    }

    public void Reset()
    {
        Array.Clear(_buf);
        _pos = 0;
        _phase = 0;
    }

    /// <summary>ブラックマン窓の sinc（通過帯域の端 = 出力のナイキスト周波数の 90%。合計 1 = 低い音の大きさを変えない）</summary>
    private static float[] Design(int factor, int taps)
    {
        double fc = 0.5 / factor * 0.9; // 入力のサンプリング周波数に対する割合
        int m = taps - 1;
        var h = new double[taps];
        double sum = 0;
        for (int n = 0; n < taps; n++)
        {
            double k = n - m / 2.0;
            double sinc = k == 0 ? 2 * fc : Math.Sin(2 * Math.PI * fc * k) / (Math.PI * k);
            double w = 0.42 - 0.5 * Math.Cos(2 * Math.PI * n / m) + 0.08 * Math.Cos(4 * Math.PI * n / m);
            h[n] = sinc * w;
            sum += h[n];
        }
        var f = new float[taps];
        for (int n = 0; n < taps; n++) f[n] = (float)(h[n] / sum);
        return f;
    }
}
