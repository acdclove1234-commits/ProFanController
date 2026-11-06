// ==================================================
// RoleEffect.cs - 光り方「パーツごとの役割」 (ゾーンごとに、低音・中音・高音・音全体のどれかに反応して光る)
// Dependencies: .NET 8 (MIT License)
// See CREDITS.txt for full license details.
// ==================================================

namespace ProFanController;

/// <summary>
/// パーツごとの役割: ゾーンの設定「反応する音」（LedSegment.Role）ごとに、決まった音だけに反応する。
///   低音: キックのたびにぱっと光り（τ 0.22 秒で消える）、ベースの強さで明るさが残る
///   中音: スネアのたびに光り（τ 0.18 秒）、ボーカルなどの中音の強さで明るさが残る
///   高音: ハイハットのたびに白っぽく短く光り（τ 0.09 秒）、高音の強さで明るさが残る
///   音全体: 音量で明るさが変わる（点滅しない）
/// 色は役割ごとにパレットの位置をずらす（低音 0・中音 1/3・高音 2/3）。ビートで色が進むときも、役割の違いは保たれる。
/// パレット「選んだ色だけ」なら、すべて同じ色で明るさだけが変わる。
/// 遅いデバイス（slow）は、光の消え方を 1.8 倍ゆっくりにする（一瞬の光を取りこぼさないように）
/// </summary>
public sealed class RoleEffect : IMusicEffect
{
    public const double BassTauSec = 0.22;
    public const double MidTauSec = 0.18;
    public const double TrebleTauSec = 0.09;
    public const double AllTauSec = 0.30;
    /// <summary>遅いデバイスで消え方に掛ける倍率</summary>
    public const double SlowFactor = 1.8;
    /// <summary>ハイハットの光で白へ寄せる割合（× 光の強さ）</summary>
    public const float HatWhiteMix = 0.35f;

    private const int Roles = 4;
    private readonly bool _slow;
    private readonly double[] _hitT = new double[Roles];
    private readonly float[] _hitS = new float[Roles];
    private readonly float[] _glow = new float[Roles];
    private readonly float[] _flash = new float[Roles];
    private readonly LinColor[] _color = new LinColor[Roles];

    public RoleEffect(bool slow = false) => _slow = slow;

    public string Name => MusicStyles.Role;

    /// <summary>役割ごとの今の明るさ（0〜1。テスト用）</summary>
    public float LevelOf(MusicRole r) => _glow[(int)r];

    public void Reset(EffectContext c)
    {
        Array.Fill(_hitT, double.NegativeInfinity);
        Array.Clear(_hitS);
        Array.Clear(_glow);
        Array.Clear(_flash);
    }

    private double Tau(MusicRole r)
    {
        double t = r switch { MusicRole.Bass => BassTauSec, MusicRole.Mid => MidTauSec, MusicRole.Treble => TrebleTauSec, _ => AllTauSec };
        return _slow ? t * SlowFactor : t;
    }

    public void Update(EffectContext c)
    {
        // 役割ごとの出来事（キック・スネア・ハイハット）。光った本当の時刻を覚える
        var ev = c.Events;
        for (int i = 0; i < ev.Count; i++)
        {
            var e = ev[i];
            int r = e.Kind switch
            {
                AudioEventKind.Kick => (int)MusicRole.Bass,
                AudioEventKind.Snare => (int)MusicRole.Mid,
                AudioEventKind.Hat => (int)MusicRole.Treble,
                _ => -1,
            };
            if (r < 0) continue;
            _hitT[r] = e.T;
            _hitS[r] = Math.Clamp(0.55f + 0.25f * e.Strength, 0, 1);
        }

        var f = c.Frame;
        for (int r = 0; r < Roles; r++)
        {
            var role = (MusicRole)r;
            double tau = Tau(role);
            // 今の分析結果は、本当の時刻からの経過時間だけ減らす（古い値を何度も拾い直さない）
            float age = (float)Math.Exp(-(c.T - c.FrameTime) / tau);
            float level = role switch
            {
                MusicRole.Bass => Math.Max(f.BassE, 0.8f * f.KickEnv),
                MusicRole.Mid => f.MidE,
                MusicRole.Treble => f.TrebleE,
                _ => f.LoudVU,
            } * age;
            float hit = role == MusicRole.All ? 0f : _hitS[r] * (float)Math.Exp(-Math.Max(0, c.T - _hitT[r]) / tau);
            _flash[r] = hit;
            // 出来事の光をいちばん強く、音の強さはその 75% までにする（どの音で光ったかが分かるように）
            float x = Math.Max(hit, 0.75f * level);
            _glow[r] = Math.Max(x, _glow[r] * EffectContext.Decay(c.Dt, tau));
            double off = role switch { MusicRole.Bass => 0, MusicRole.Mid => 1 / 3.0, MusicRole.Treble => 2 / 3.0, _ => 0.5 };
            _color[r] = role == MusicRole.Treble && hit > 0.05f
                ? OkLab.ToLed(Lab.Lerp(c.ColorLabAt(off), Lab.White, HatWhiteMix * hit), c.Gamma)
                : c.ColorAt(off);
        }
    }

    public void Render(EffectContext c, in LedSegment s, LinColor[] dst)
    {
        int r = (int)s.Role;
        var col = _color[r] * c.Ilin(_glow[r]);
        for (int j = s.Start; j < s.Start + s.Count; j++) dst[j] = col;
    }
}
