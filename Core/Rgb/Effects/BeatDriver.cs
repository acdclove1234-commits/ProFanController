// ==================================================
// BeatDriver.cs - ビートの合図とビートの光の計算 (予測したビート・キックなどの出来事から、
//                 色を進めるきっかけと「ぱっと光って消える」光の強さを、デバイスごとに求める)
// Dependencies: .NET 8 (MIT License)
// See CREDITS.txt for full license details.
// ==================================================

namespace ProFanController;

/// <summary>
/// デバイスごとに 1 つ。毎コマ、前のコマの時刻 t0 から今の時刻 t1 まで（t0 は含まず t1 は含む）を見て、
///   合図（Trigger）: テンポをつかんでいれば予測したビートの頭、つかんでいなければ強いキック（強さ 1.5 以上、0.25 秒以上あける）
///   ビートの光（Flash）= 確かさ × 予測の光 + (1 − 確かさ) × 出来事の光
///       予測の光 = exp(−拍の頭からの秒数 / τf)、τf = 0.35 × 1 拍（0.12〜0.28 秒）
///       出来事の光 = a × exp(−出来事からの秒数 / 0.25 秒)、a = 0.5 + 0.25 × (強さ − 1)（0.5〜1）
///   色の切り替え: 合図 ColorStepBeats 回ごとに、パレットを 1 つ進める（テンポをつかんでいれば小節の頭にそろえる）
/// 時刻はすべて「音が鳴った本当の時刻」で計算するので、1 秒間のコマ数が違っても同じ光り方になる
/// </summary>
public sealed class BeatDriver
{
    /// <summary>合図にするキックの強さの下限（しきい値の何倍か）</summary>
    public const float TriggerMinStrength = 1.5f;
    /// <summary>合図の最小間隔（秒）</summary>
    public const double TriggerMinGapSec = 0.25;
    /// <summary>出来事の光にするキックの強さの下限</summary>
    public const float FlashMinStrength = 1.2f;
    /// <summary>この間隔（秒）以内のキックと立ち上がりは、同じ 1 つの音とみなす（光を 2 回にしない）</summary>
    public const double SameHitSec = 0.06;
    /// <summary>出来事の光が消えていく時定数（秒）</summary>
    public const double OnsetFlashTauSec = 0.25;
    /// <summary>更新の遅いデバイスでの、ビートの光が消えていく時定数の下限（秒）。速く消えると見逃すため</summary>
    public const double SlowFlashTauMinSec = 0.30;
    /// <summary>更新の遅いデバイスでの、色の切り替えにかける時間の下限（秒）</summary>
    public const double SlowCrossfadeMinSec = 0.15;

    private double _lastTriggerT = double.NegativeInfinity;
    // 予測したビートの頭をまたいだか（予測が書き換わっても同じビートを 2 回合図にしない）
    private readonly BeatCrossing _crossing = new();
    // 出来事の光は、直近 2 つを覚えておき明るいほうを使う（新しい光を弱めたとき、前の光が急に消えないように）
    private double _onT1 = double.NegativeInfinity, _onT0 = double.NegativeInfinity;
    private float _onA1, _onA0, _onG1 = 1, _onG0 = 1;
    private float _predGain = 1;
    // このコマで新しく分かった光（検出の遅れで、前のコマの範囲より前の時刻のこともある）
    private bool _fresh1, _fresh0;

    /// <summary>激しい点滅の制限</summary>
    public FlashLimiter Limiter { get; } = new();

    /// <summary>状態を最初に戻す（デバイスを作り直したときなど）</summary>
    public void Reset()
    {
        _lastTriggerT = _onT1 = _onT0 = double.NegativeInfinity;
        _onA1 = _onA0 = 0;
        _onG1 = _onG0 = _predGain = 1;
        _crossing.Reset();
        Limiter.Reset();
    }

    /// <summary>
    /// 1 コマ分を計算して c（Trigger・Fl・Locked など）に書く。t0 = 前のコマの音の時刻、t1 = 今のコマの音の時刻。
    /// c.Beat・c.Events・c.Music・c.Pal・c.Slow は先に入れておくこと
    /// </summary>
    public void Update(EffectContext c, double t0, double t1)
    {
        var beat = c.Beat;
        var m = c.Music;
        bool auto = m.TempoMode == TempoModes.Auto;
        bool off = m.TempoMode == TempoModes.Off;
        Limiter.Enabled = m.ReduceFlashing;

        // ---- テンポ（Auto のときだけ予測を使う） ----
        c.HasTempo = auto && beat.HasTempo && beat.Period > 0;
        c.Conf = c.HasTempo ? (float)Math.Clamp(beat.Confidence, 0, 1) : 0f;
        c.Locked = c.HasTempo && beat.Locked;
        c.Period = c.HasTempo ? beat.Period : 0.5;
        c.Phase = c.HasTempo ? beat.Phase(t1) : 0;
        c.BeatIndex = c.HasTempo ? beat.Index(t1) : 0;
        c.Downbeat = c.Locked && beat.Bar(t1) == 0;
        // またいだビートの時刻 = t1 を含む拍の頭（1 コマで 2 拍以上またぐのは極端に遅いときだけなので、新しいほうを使う）
        c.BeatCrossed = _crossing.Step(beat, c.HasTempo, t0, t1, out c.BeatCrossT);
        c.Trigger = false;
        c.TriggerT = t1;
        c.TriggerStrength = 0;
        _fresh1 = _fresh0 = false;

        // ---- 出来事と予測ビートを、時刻の順に処理する ----
        bool beatPending = c.BeatCrossed && !off;
        var events = c.Events;
        for (int i = 0; i < events.Count; i++)
        {
            var ev = events[i];
            if (beatPending && ev.T > c.BeatCrossT)
            {
                OnPredictedBeat(c);
                beatPending = false;
            }
            if (off) continue;
            bool kickLike = ev.Kind == AudioEventKind.Kick || (auto && ev.Kind == AudioEventKind.Onset);
            if (!kickLike) continue;
            OnOnset(c, ev);
        }
        if (beatPending) OnPredictedBeat(c);

        // ---- ビートの光（今の時刻の値と、このコマの間の最大値） ----
        c.Fl = c.FlPeak = 0;
        c.FlPeakT = t1;
        if (off) return;
        double now = FlashAt(c, t1);
        double peak = now, peakT = t1;
        // 光が最大になるのは光り始めの瞬間（予測したビートの頭・キックの時刻）。このコマの間にあったか、
        // このコマで初めて届いた（検出が遅れた）光なら、そこも見る
        if (c.BeatCrossed && c.BeatCrossT > t0) Consider(c, c.BeatCrossT, ref peak, ref peakT);
        if (_onT1 <= t1 && (_fresh1 || _onT1 > t0)) Consider(c, _onT1, ref peak, ref peakT);
        if (_onT0 <= t1 && (_fresh0 || _onT0 > t0)) Consider(c, _onT0, ref peak, ref peakT);
        c.Fl = (float)Math.Clamp(m.FlashAmp * now, 0, 1);
        c.FlPeak = (float)Math.Clamp(m.FlashAmp * peak, 0, 1);
        c.FlPeakT = peakT;
    }

    private void Consider(EffectContext c, double t, ref double peak, ref double peakT)
    {
        double v = FlashAt(c, t + 1e-6);
        if (v > peak) { peak = v; peakT = t; }
    }

    /// <summary>時刻 t のビートの光（FlashAmp を掛ける前）= 確かさ × 予測の光 + (1 − 確かさ) × 出来事の光</summary>
    public double FlashAt(EffectContext c, double t)
    {
        if (c.Music.TempoMode == TempoModes.Off) return 0;
        var beat = c.Beat;
        bool slow = c.Slow;
        double fpred = 0;
        if (c.HasTempo && c.Conf > 0)
        {
            double tauF = Math.Clamp(0.35 * beat.Period, 0.12, 0.28);
            if (slow) tauF = Math.Max(tauF, SlowFlashTauMinSec);
            fpred = Math.Exp(-beat.Phase(t) * beat.Period / tauF) * _predGain;
        }
        double tauO = slow ? Math.Max(OnsetFlashTauSec, SlowFlashTauMinSec) : OnsetFlashTauSec;
        double fons = Math.Max(Pulse(t, _onT1, _onA1 * _onG1, tauO), Pulse(t, _onT0, _onA0 * _onG0, tauO));
        return c.Conf * fpred + (1 - c.Conf) * fons;
    }

    /// <summary>時刻 tStart に強さ a で始まり、時定数 tau で消えていく光の、時刻 t の値</summary>
    private static double Pulse(double t, double tStart, double a, double tau)
    {
        if (a <= 0 || double.IsInfinity(tStart) || t < tStart) return 0;
        return a * Math.Exp(-(t - tStart) / tau);
    }

    /// <summary>予測したビートの頭をまたいだ</summary>
    private void OnPredictedBeat(EffectContext c)
    {
        double tb = c.BeatCrossT;
        if (c.Conf > 0) _predGain = Limiter.Decide(tb);
        if (!c.Locked) return;
        // テンポをつかんでいる: 予測したビートが合図。色は小節の頭にそろえて ColorStepBeats 拍ごとに進める
        int every = StepEvery(c);
        long rel = c.BeatIndex - c.Beat.DownbeatOffset;
        bool step = ((rel % every) + every) % every == 0;
        FireTrigger(c, tb, 1f, step);
    }

    /// <summary>キック（Auto なら立ち上がりも）が来た</summary>
    private void OnOnset(EffectContext c, AudioEvent ev)
    {
        // 出来事の光（ほぼ同時のキックと立ち上がりは 1 つの音として、強いほうを使う）
        if (ev.Strength >= FlashMinStrength)
        {
            float a = Math.Clamp(0.5f + 0.25f * (ev.Strength - 1), 0.5f, 1f);
            if (Math.Abs(ev.T - _onT1) < SameHitSec)
            {
                _onA1 = Math.Max(_onA1, a);
                _fresh1 = true;
            }
            else
            {
                _onT0 = _onT1; _onA0 = _onA1; _onG0 = _onG1; _fresh0 = _fresh1;
                _onT1 = ev.T; _onA1 = a; _onG1 = Limiter.Decide(ev.T); _fresh1 = true;
            }
        }
        // テンポをつかんでいなければ、強いキックが合図
        if (!c.Locked && ev.Strength >= TriggerMinStrength && ev.T - _lastTriggerT >= TriggerMinGapSec)
        {
            int every = StepEvery(c);
            bool step = (c.TriggerCount + 1) % every == 0;
            FireTrigger(c, ev.T, ev.Strength, step);
        }
    }

    private void FireTrigger(EffectContext c, double t, float strength, bool step)
    {
        _lastTriggerT = Math.Max(_lastTriggerT, t);
        c.Trigger = true;
        c.TriggerT = t;
        c.TriggerStrength = strength;
        c.TriggerCount++;
        if (!step) return;
        if (c.Shared is { } shared) shared.Step(t, c.Pal.Step);
        else c.Color.Step(t, c.Pal.Step, CrossfadeSec(c));
    }

    /// <summary>色の切り替えにかける時間（秒）</summary>
    public static double CrossfadeSec(EffectContext c)
    {
        if (c.NoFade) return 0;
        double dur = c.Music.CrossfadeMs / 1000;
        return c.Slow ? Math.Max(dur, SlowCrossfadeMinSec) : dur;
    }

    /// <summary>共有の色が進んでいたら、このデバイスもその色へ切り替える（毎コマ、合図の計算のあとに呼ぶ）</summary>
    public static void SyncSharedColor(EffectContext c)
    {
        if (c.Shared is not { } shared) return;
        // 切り替えの始まりは、共有の色が進んだビートの時刻（ただし今のコマより先にはしない）
        double t = Math.Min(Math.Max(shared.LastStepT, c.TPrev), c.T);
        c.Color.SyncTo(shared, t, CrossfadeSec(c));
    }

    /// <summary>色を何回の合図ごとに進めるか（全デバイスで同じにする。違うとデバイスごとに色がずれる）</summary>
    private static int StepEvery(EffectContext c) => Math.Max(1, c.Music.ColorStepBeats);
}
