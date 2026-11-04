// ==================================================
// SampleClock.cs - 取り込んだ音の 1 つ 1 つが「いつ鳴った音か」を求める時計
// Dependencies: .NET 8 (MIT License)
// See CREDITS.txt for full license details.
// ==================================================

namespace ProFanController;

/// <summary>
/// 音は一定の速さ（48,000 個／秒など）で届くが、まとめて渡されるので「受け取った時刻」はばらつく。
/// 受け取った時刻 − （それまでの個数 ÷ 速さ）の、直近 2 秒の最小値を基準にすると、
/// 受け取りの遅れのばらつきに左右されない正確な時刻が分かる（遅れは必ず 0 以上なので最小値が真の値に近い）。
///   基準 = min( 受け取った時刻 − 受け取り終わった個数 / 速さ )
///   j 番目の音の時刻 = 基準 + j / 速さ
/// </summary>
public sealed class SampleClock
{
    private const double Window = 2.0;
    private readonly Queue<(double At, double Offset)> _offsets = new();
    private double _offset = double.NaN;
    private double _lastArrival = double.NaN;

    public SampleClock(int sampleRate) => SampleRate = sampleRate > 0 ? sampleRate : 48000;

    public int SampleRate { get; }
    /// <summary>これまでに受け取った音の個数（1 チャンネル分）</summary>
    public long Frames { get; private set; }
    public bool Ready => !double.IsNaN(_offset);

    /// <summary>
    /// 音を frames 個受け取ったことを記録する。arrival は受け取った時刻（AudioClock の秒）。
    /// 戻り値は、受け取った最初の音の時刻
    /// </summary>
    public double Observe(int frames, double arrival)
    {
        // 0.1 秒以上何も届かなかった（無音で Windows が送ってこなかった・出力先が変わった）ら、基準を取り直す
        if (!double.IsNaN(_lastArrival) && arrival - _lastArrival > 0.1 + (double)frames / SampleRate) ResetOffsets();
        _lastArrival = arrival;
        long first = Frames;
        Frames += Math.Max(0, frames);
        double off = arrival - (double)Frames / SampleRate;
        while (_offsets.Count > 0 && _offsets.Peek().At < arrival - Window) _offsets.Dequeue();
        _offsets.Enqueue((arrival, off));
        double min = double.PositiveInfinity;
        foreach (var o in _offsets) min = Math.Min(min, o.Offset);
        _offset = min;
        return TimeOf(first);
    }

    /// <summary>
    /// 音が届かなかった間を、無音（0）で埋めるときに使う。frames 個ぶん時計を進め、最初の音の時刻を返す。
    /// 基準は変えない（実際の音が届いたら Observe が取り直す）
    /// </summary>
    public double AdvanceSilence(int frames, double now)
    {
        if (double.IsNaN(_offset)) _offset = now - (double)Frames / SampleRate;
        long first = Frames;
        Frames += Math.Max(0, frames);
        return TimeOf(first);
    }

    /// <summary>frame 番目（0 から数える）の音の時刻</summary>
    public double TimeOf(long frame) => (double.IsNaN(_offset) ? 0 : _offset) + (double)frame / SampleRate;

    /// <summary>いちばん新しい音の時刻（まだ何も受け取っていなければ負の無限大）</summary>
    public double LatestTime => Frames == 0 || double.IsNaN(_offset) ? double.NegativeInfinity : TimeOf(Frames - 1);

    private void ResetOffsets()
    {
        _offsets.Clear();
    }
}
