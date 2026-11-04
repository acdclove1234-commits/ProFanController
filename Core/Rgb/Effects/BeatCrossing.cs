// ==================================================
// BeatCrossing.cs - 予測したビートの頭をまたいだかの判定 (ビートの予測が書き換わっても、
//                   同じビートを 2 回合図にしない・取りこぼさない)
// Dependencies: .NET 8 (MIT License)
// See CREDITS.txt for full license details.
// ==================================================

namespace ProFanController;

/// <summary>
/// 毎コマ、前のコマの時刻 t0 から今の時刻 t1 まで（t0 は含まず t1 は含む）に予測したビートの頭が来たかを決める。
/// BeatState は予測が少し動くたびに作り直される（位相の補正は 1 拍の 1/4 まで）ので、今の BeatState だけで
/// Index(t0) と Index(t1) を比べると、予測が後ろへずれたとき同じビートを 2 回、前へずれたとき 0 回にしてしまう。
/// そこで「最後に合図にしたビートの予測時刻」を覚えておき、
///   t1 以前でいちばん新しいビートの頭 b が、最後に合図にしたビートより半拍以上あと
///   かつ b が t0 − 半拍より後（前へずれて t0 より前になったビートは、次のコマで少し遅れて合図にする）
/// のときだけ合図にする。覚えがない・古い（2 拍以上前）・時刻が戻ったときは、今の BeatState だけで決める
/// </summary>
public sealed class BeatCrossing
{
    /// <summary>最後に合図にしたビートの予測時刻（なければ NaN）</summary>
    private double _lastT = double.NaN;

    /// <summary>最後に合図にしたビートの予測時刻（なければ NaN。テスト用）</summary>
    public double LastFiredT => _lastT;

    /// <summary>覚えを捨てる</summary>
    public void Reset() => _lastT = double.NaN;

    /// <summary>
    /// このコマでビートの頭をまたいだか。active = false（テンポを使わない）なら覚えを捨てて false。
    /// またいだら beatT にそのビートの予測時刻（t1 以前でいちばん新しい拍の頭）を入れる。またいでいなければ t1
    /// </summary>
    public bool Step(BeatState beat, bool active, double t0, double t1, out double beatT)
    {
        beatT = t1;
        if (!active || !beat.HasTempo || !(beat.Period > 0))
        {
            _lastT = double.NaN;
            return false;
        }
        if (!(t1 > t0)) return false;
        double p = beat.Period;
        long idx = beat.Index(t1);
        double b = beat.LastBeatT + (idx - beat.IndexAtLast) * p;
        bool fresh = double.IsNaN(_lastT) || t1 < _lastT || t0 - _lastT > 2 * p;
        bool crossed = fresh
            ? beat.Index(t0) < idx
            : b > _lastT + 0.5 * p && b > t0 - 0.5 * p;
        if (!crossed) return false;
        _lastT = b;
        beatT = b;
        return true;
    }
}
