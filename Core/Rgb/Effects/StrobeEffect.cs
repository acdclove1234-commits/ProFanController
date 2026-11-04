// ==================================================
// StrobeEffect.cs - 光り方 E11「ストロボ」 (小節の頭のビートで白く強く点滅する。「激しい」かつ点滅の制限がオフのときだけ)
// Dependencies: .NET 8 (MIT License)
// See CREDITS.txt for full license details.
// ==================================================

namespace ProFanController;

/// <summary>
/// ストロボ: 光の形 = 40 ミリ秒だけ 1、その後 exp(−経過/30 ミリ秒) で消える（白）。
///   テンポをつかんでいれば小節の頭（1 拍目）の予測ビートで光る（時刻から計算するので遅れない）
///   つかんでいなければ、強いキック（強さ 2 以上）で光る
/// 光過敏への配慮のため、「激しい点滅を抑える」がオンのとき・更新の遅いデバイスでは使わない（「ビートで色替え」になる）
/// </summary>
public sealed class StrobeEffect : IMusicEffect
{
    /// <summary>全開で光らせる秒数</summary>
    public const double HoldSec = 0.04;
    /// <summary>その後消えていく時定数（秒）</summary>
    public const double FadeTauSec = 0.03;
    /// <summary>テンポがないとき、光らせるキックの強さの下限</summary>
    public const float MinKickStrength = 2f;

    private double _lastKickT = double.NegativeInfinity;
    private float _i;
    private LinColor _floor;

    public string Name => MusicStyles.Strobe;

    public void Reset(EffectContext c) => _lastKickT = double.NegativeInfinity;

    public void Update(EffectContext c)
    {
        var ev = c.Events;
        for (int i = 0; i < ev.Count; i++)
            if (ev[i].Kind == AudioEventKind.Kick && ev[i].Strength >= MinKickStrength) _lastKickT = Math.Max(_lastKickT, ev[i].T);

        double age;
        if (c.Locked)
        {
            // いちばん近い過去の小節の頭からの秒数 = いまの拍の頭からの秒数 + 小節の中の拍 × 1 拍
            age = c.Phase * c.Period + c.Beat.Bar(c.T) * c.Period;
        }
        else age = c.T - _lastKickT;
        _i = Shape(age);
        _floor = c.ColorAt(0) * c.F0;
    }

    /// <summary>光ってからの秒数 age の明るさ（0〜1）</summary>
    public static float Shape(double age)
    {
        if (!(age >= 0)) return 0;
        if (age < HoldSec) return 1;
        return (float)Math.Exp(-(age - HoldSec) / FadeTauSec);
    }

    public void Render(EffectContext c, in LedSegment s, LinColor[] dst)
    {
        var col = (_floor + LinColor.White * _i).Clamp01();
        for (int j = s.Start; j < s.Start + s.Count; j++) dst[j] = col;
    }
}
