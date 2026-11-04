// ==================================================
// RgbTemperatureFilter.cs - 温度モードの温度を 1 か所でならす（全デバイスで同じ値を使う）
// Dependencies: .NET 8 (MIT License)
// See CREDITS.txt for full license details.
// ==================================================

namespace ProFanController;

/// <summary>
/// LED の温度モード用の温度。読み取りの周期（0.25〜5 秒）ごとの値（CPU パッケージは一瞬だけ数°C 跳ねる）を
///   1. ありえない値（0 以下・150 以上・127/128）を捨てる
///   2. 直近 4.5 秒（最低 5 個）の中央値（読む間隔によらず、2 秒くらいまでの跳ねを消す）
///   3. 時定数 6 秒の EMA（時刻の差で計算。周期が変わっても同じ）。始めの 2 秒（3 個まで）は中央値をそのまま使う
///   4. 表示中の値から 1.5°C 以上離れたときだけ、0.5°C 刻みで表示を変える（ヒステリシス）
///   5. 読めないときは 30 秒まで前の値を使う
/// の順にならす。全デバイスがこの 1 つの値を使うので、色の段がそろう
/// </summary>
public sealed class RgbTemperatureFilter
{
    /// <summary>中央値に使う最低の個数（読む間隔が長いとき。1 秒ごとなら直近 5 秒）</summary>
    public const int MedianCount = 5;
    /// <summary>中央値をとる時間（秒）。読む間隔が短く（0.25 秒ごとなど）ても、この時間の中の値をすべて使う</summary>
    public const double WindowSec = 4.5;
    /// <summary>始めのこの秒数（または 3 個まで）は、ならさず中央値をそのまま使う（最初の 1 個の跳ねを長く引きずらない）</summary>
    public const double WarmUpSec = 2.0;
    public const double TauSec = 6.0;
    public const double HysteresisC = 1.5;
    public const double GridC = 0.5;
    public const double HoldSec = 30;
    private const double MaxGapSec = 5;
    /// <summary>覚えておく値の数の上限（0.25 秒ごとでも 4.5 秒分が入る）</summary>
    private const int Capacity = 32;

    private readonly double[] _v = new double[Capacity];
    private readonly double[] _t = new double[Capacity];
    private readonly double[] _sorted = new double[Capacity];
    private int _count, _head;   // _head = いちばん古い値の位置
    private double _ema = double.NaN, _emaT, _display = double.NaN;
    private double _lastValidT = double.NegativeInfinity;

    public void Reset()
    {
        _count = _head = 0;
        _ema = _display = double.NaN;
        _lastValidT = double.NegativeInfinity;
    }

    /// <param name="raw">温度（null = 読めない）</param>
    /// <param name="now">時刻（秒。AudioClock.Now）</param>
    /// <returns>表示に使う温度（30 秒以上読めなければ null）</returns>
    public double? Push(double? raw, double now)
    {
        if (raw is double v && FanSafety.IsPlausibleTemp((float)v))
        {
            if (now - _lastValidT > HoldSec) Reset();
            if (_count == Capacity)
            {
                _head = (_head + 1) % Capacity;
                _count--;
            }
            int at = (_head + _count) % Capacity;
            _v[at] = v;
            _t[at] = now;
            _count++;
            // 古い値を捨てる（ただし最低 MedianCount 個は残す。読む間隔が長いときも 5 個の中央値になる）
            while (_count > MedianCount && now - _t[_head] > WindowSec)
            {
                _head = (_head + 1) % Capacity;
                _count--;
            }
            for (int i = 0; i < _count; i++) _sorted[i] = _v[(_head + i) % Capacity];
            Array.Sort(_sorted, 0, _count);
            // 偶数個のときは低いほう（始めの 2 個のうち 1 個が跳ねていても、跳ねた値を選ばない）
            double m = _sorted[(_count - 1) / 2];
            double gap = now - _emaT;
            bool warmUp = _count <= 3 || now - _t[_head] < WarmUpSec;
            if (double.IsNaN(_ema) || !(gap >= 0) || warmUp) _ema = m;
            else _ema += (m - _ema) * (1 - Math.Exp(-Math.Min(gap, MaxGapSec) / TauSec));
            _emaT = now;
            _lastValidT = now;
            if (double.IsNaN(_display) || Math.Abs(_ema - _display) >= HysteresisC)
                _display = Math.Round(_ema / GridC) * GridC;
            return _display;
        }
        return now - _lastValidT <= HoldSec && !double.IsNaN(_display) ? _display : null;
    }
}
