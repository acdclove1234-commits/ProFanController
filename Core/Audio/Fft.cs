// ==================================================
// Fft.cs - 高速フーリエ変換 (FFT。音の波を「周波数ごとの強さ」に分ける。三角関数の表を先に作っておく版)
// Dependencies: .NET 8 (MIT License)
// See CREDITS.txt for full license details.
// ==================================================

namespace ProFanController;

/// <summary>
/// 基数 2 の FFT（大きさは 2 のべき乗: 1024・2048 など）。
/// 毎回 cos / sin を計算すると遅いので、作るときに表（回転因子・ビット反転の並び）を作っておき、
/// 変換のたびにメモリを確保しない（1 秒に 100 回近く呼ぶため）。
/// 1 つのインスタンスは 1 つのスレッドから使う
/// </summary>
public sealed class Fft
{
    private readonly int[] _rev;
    private readonly double[] _cos;
    private readonly double[] _sin;

    public Fft(int size)
    {
        if (size < 2 || (size & (size - 1)) != 0) throw new ArgumentException(L("FFT の大きさは 2 のべき乗にする"), nameof(size));
        Size = size;
        _rev = new int[size];
        int bits = 0;
        while ((1 << bits) < size) bits++;
        for (int i = 0; i < size; i++)
        {
            int r = 0;
            for (int b = 0; b < bits; b++) r |= ((i >> b) & 1) << (bits - 1 - b);
            _rev[i] = r;
        }
        // 回転因子 e^(−2πi·t/N) の表（t = 0〜N/2 − 1）
        _cos = new double[size / 2];
        _sin = new double[size / 2];
        for (int t = 0; t < size / 2; t++)
        {
            _cos[t] = Math.Cos(2 * Math.PI * t / size);
            _sin[t] = Math.Sin(2 * Math.PI * t / size);
        }
    }

    public int Size { get; }

    /// <summary>
    /// その場で変換する（re = 実部、im = 虚部。どちらも長さ Size）。
    /// 結果の k 番目は周波数 k × (サンプリング周波数 / Size) Hz の成分。逆変換の 1/N は掛けない
    /// </summary>
    public void Forward(double[] re, double[] im)
    {
        int n = Size;
        // ビット反転の順に並べ替える
        for (int i = 0; i < n; i++)
        {
            int j = _rev[i];
            if (i < j)
            {
                (re[i], re[j]) = (re[j], re[i]);
                (im[i], im[j]) = (im[j], im[i]);
            }
        }
        // 2 個ずつ → 4 個ずつ → … と、小さな変換を組み合わせていく（バタフライ演算）
        for (int len = 2, step = n >> 1; len <= n; len <<= 1, step >>= 1)
        {
            int half = len >> 1;
            for (int i = 0; i < n; i += len)
            {
                for (int k = 0, t = 0; k < half; k++, t += step)
                {
                    double wr = _cos[t], wi = -_sin[t];
                    int a = i + k, b = a + half;
                    double br = re[b], bi = im[b];
                    double tr = br * wr - bi * wi;
                    double ti = br * wi + bi * wr;
                    double ar = re[a], ai = im[a];
                    re[b] = ar - tr;
                    im[b] = ai - ti;
                    re[a] = ar + tr;
                    im[a] = ai + ti;
                }
            }
        }
    }

    /// <summary>
    /// 周期的なハン窓 w[n] = 0.5 − 0.5·cos(2πn/N)（N で割る。N − 1 で割る「対称な」窓より FFT に合う）。
    /// 区切りの端で波が急に切れると、ない音（雑音）が見えてしまうので、両端をなだらかに 0 にする。
    /// この窓の 2 乗の合計は 3N/8
    /// </summary>
    public static double[] PeriodicHann(int size)
    {
        var w = new double[size];
        for (int i = 0; i < size; i++) w[i] = 0.5 - 0.5 * Math.Cos(2 * Math.PI * i / size);
        return w;
    }
}
