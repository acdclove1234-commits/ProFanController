// ==================================================
// Compositor.cs - 光り方の重ね合わせの部品 (激しい点滅の制限・音が止まったときの光り方への切り替え・
//                 光り方を変えたときのクロスフェード)
// Dependencies: .NET 8 (MIT License)
// See CREDITS.txt for full license details.
// ==================================================

namespace ProFanController;

/// <summary>
/// 激しい点滅を抑える（「激しい点滅を抑える」がオンのとき。光過敏への配慮: WCAG 2.3.1 の「1 秒に 3 回まで」）。
/// 新しいビートの光は、前に受け付けた光から 333 ミリ秒以上たっていれば受け付ける（強さ 1 倍）。
/// それより短い間隔の光は 0.35 倍に弱める。60 ミリ秒以内の合図（キックと予測ビートがほぼ同時など）は同じ 1 回の光とみなす
/// </summary>
public sealed class FlashLimiter
{
    /// <summary>受け付ける光の最小間隔（秒）。1 秒に 3 回まで</summary>
    public const double MinGapSec = 0.333;
    /// <summary>この間隔（秒）より近い合図は、同じ 1 回の光とみなす</summary>
    public const double MergeSec = 0.06;
    /// <summary>受け付けなかった光の強さの倍率</summary>
    public const float RejectedGain = 0.35f;

    private double _lastAccepted = double.NegativeInfinity;

    /// <summary>制限する（オフなら Decide は常に 1）</summary>
    public bool Enabled { get; set; } = true;
    /// <summary>受け付けた光の数（テスト・表示用）</summary>
    public int Accepted { get; private set; }
    /// <summary>弱めた光の数</summary>
    public int Rejected { get; private set; }
    /// <summary>受け付けた光どうしの、いちばん短い間隔（秒。テスト用）</summary>
    public double MinAcceptedGap { get; private set; } = double.PositiveInfinity;

    public void Reset()
    {
        _lastAccepted = double.NegativeInfinity;
        Accepted = Rejected = 0;
        MinAcceptedGap = double.PositiveInfinity;
    }

    /// <summary>時刻 t に始まる光の強さの倍率を決める（1 = そのまま、0.35 = 弱める）。t は光の本当の時刻</summary>
    public float Decide(double t)
    {
        if (!Enabled) return 1f;
        double gap = Math.Abs(t - _lastAccepted);
        if (gap < MergeSec) return 1f;
        if (gap >= MinGapSec)
        {
            if (!double.IsInfinity(_lastAccepted)) MinAcceptedGap = Math.Min(MinAcceptedGap, gap);
            _lastAccepted = Math.Max(_lastAccepted, t);
            Accepted++;
            return 1f;
        }
        Rejected++;
        return RejectedGain;
    }
}

/// <summary>
/// 音が止まったときの光り方へ切り替える割合（0 = 音楽連動、1 = 止まっているときの光り方）。
/// 無音（門が閉じている、または反応の強さが 0.05 未満）が IdleDelaySec 続いたら 1 秒かけて 1 へ、
/// 音が戻ったら 80 ミリ秒で 0 へ戻す
/// </summary>
public sealed class IdleMixer
{
    /// <summary>止まっているときの光り方へ切り替えるのにかける秒数</summary>
    public const double RampInSec = 1.0;
    /// <summary>
    /// 音が戻ったとき、音楽連動へ戻すのにかける秒数（すぐ戻す: 曲の頭・ブレイク明けの最初のビートを見せるため。
    /// 音の門は音が止まっても 0.3 秒開いたままなので、短い音だけを待って無視することはできない）
    /// </summary>
    public const double RampOutSec = 0.08;
    /// <summary>反応の強さ（Activity）がこれ未満なら無音とみなす</summary>
    public const float QuietActivity = 0.05f;

    private double _quietFor;

    /// <summary>止まっているときの光り方の割合（0〜1）</summary>
    public float Weight { get; private set; }

    public void Reset(float weight = 0)
    {
        _quietFor = 0;
        Weight = weight;
    }

    /// <summary>1 コマ進める</summary>
    public void Update(AudioFrame f, double idleDelaySec, float dt)
    {
        bool quiet = !f.GateOpen || f.Activity < QuietActivity;
        _quietFor = quiet ? _quietFor + dt : 0;
        if (_quietFor >= idleDelaySec) Weight = (float)Math.Min(1, Weight + dt / RampInSec);
        else Weight = (float)Math.Max(0, Weight - dt / RampOutSec);
    }
}

/// <summary>
/// 光り方・パレット・モードを変えたときのクロスフェード（0.4 秒）。
/// 変えた瞬間の色（光の量）を覚えておき、そこから新しい光り方の色へ、光の量のまま混ぜていく
/// </summary>
public sealed class SnapshotFade
{
    /// <summary>クロスフェードにかける秒数</summary>
    public const double DurationSec = 0.4;

    private LinColor[] _from = Array.Empty<LinColor>();
    private double _t0;
    private bool _active;

    public bool Active => _active;

    /// <summary>時刻 t に、いま表示している色 current からクロスフェードを始める</summary>
    public void Start(ReadOnlySpan<LinColor> current, double t)
    {
        if (_from.Length != current.Length) _from = new LinColor[current.Length];   // LED の数が変わったときだけ
        current.CopyTo(_from);
        _t0 = t;
        _active = true;
    }

    public void Cancel() => _active = false;

    /// <summary>時刻 t のクロスフェードを frame に適用する（frame = 新しい光り方の色。書き換える）</summary>
    public void Apply(Span<LinColor> frame, double t)
    {
        if (!_active) return;
        if (_from.Length != frame.Length) { _active = false; return; }
        float s = EffectContext.Smoothstep((t - _t0) / DurationSec);
        if (s >= 1) { _active = false; return; }
        for (int i = 0; i < frame.Length; i++) frame[i] = LinColor.Lerp(_from[i], frame[i], s);
    }
}
