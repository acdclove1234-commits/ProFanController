// ==================================================
// BeatTracker.cs - テンポとビートの位置の追跡 (立ち上がりの強さの周期からテンポを求め、ビートの位置を予測して
//                  毎拍ずれを直す。確かさ・ロックの状態・小節の頭・盛り上がり（ドロップ）の判定も行う)
// Dependencies: .NET 8 (MIT License)
// See CREDITS.txt for full license details.
// ==================================================

namespace ProFanController;

/// <summary>テンポの追跡の状態</summary>
public enum BeatLock
{
    /// <summary>テンポをつかんでいない（キックの検出だけで光らせる）</summary>
    Unlocked,
    /// <summary>確かさが下がってきたが、短い間奏などの間はビートの予測を続けている</summary>
    Coasting,
    /// <summary>テンポとビートの位置をつかんでいる（予測したビートで光らせてよい）</summary>
    Locked,
}

/// <summary>
/// テンポとビートの追跡。分析 1 回（約 10.7 ミリ秒）を「1 コマ」として数える。
///
/// テンポ（0.256 秒ごと。立ち上がりの強さの記録が 2.6 秒以上たまってから）:
///   1. 直近 6.4 秒の立ち上がりの強さから、なだらかな変化（移動平均）を引いて、尖った部分だけにする
///   2. 自己相関（ずらして重ねたときの一致度）を FFT で求める
///   3. 69〜181 BPM の各周期 τ について、τ・2τ・3τ・4τ の一致度を足す（くし形のフィルター）
///   4. 120 BPM を中心にした「ありがちさ」を掛け、前回からテンポが急に変わりにくいように滑らかにつなぐ
///   5. 1/20 コマ単位まで細かく求め、倍・半分のテンポの取り違えを低音の強さで確かめる
/// ビートの位置:
///   テンポが決まったら、直近 4.5 秒で立ち上がりがいちばんそろう位置を探して、次のビートを予測する。
///   予測したビートの前後（周期の ±15%）で実際の立ち上がりの山を探し、ずれの 30% を位置に、4% を周期に反映する
/// 確かさとロック:
///   直近 8 拍のずれの大きさと、ビートの位置の立ち上がりが平均よりどれだけ強いかから確かさを求め、
///   0.55 以上が 4 拍続いたらロック、0.30 未満が 4 拍続くか、2 秒間低音の立ち上がりがなければ解除する
/// 取り込みスレッドからだけ使う。結果は変更しない BeatState（Published）として丸ごと差し替える
/// </summary>
public sealed class BeatTracker
{
    // ===== テンポ
    /// <summary>立ち上がりの強さを覚えておく長さ（秒。48 kHz で 600 コマ）</summary>
    private const double HistorySec = 6.4;
    /// <summary>テンポを求め始めるのに必要な記録の長さ（秒）</summary>
    private const double MinHistorySec = 2.6;
    /// <summary>テンポを求め直す間隔（秒。48 kHz で 24 コマ）</summary>
    private const double UpdateSec = 0.256;
    /// <summary>移動平均の幅（秒。48 kHz で 15 コマ）</summary>
    private const double MovingAverageSec = 15 * 512 / 48000.0;
    /// <summary>自己相関で、最低限重なっている長さ（秒。60 コマ）</summary>
    private const double MinOverlapSec = 60 * 512 / 48000.0;
    /// <summary>追いかけるテンポの範囲（BPM。周期 0.864〜0.331 秒）</summary>
    public const double MinBpm = 69.4;
    public const double MaxBpm = 181.5;
    /// <summary>「ありがちなテンポ」の中心（秒。120 BPM）。幅は 1 オクターブ</summary>
    private const double PriorCentreSec = 0.5;
    /// <summary>前回のテンポからの変わりにくさ（対数の幅）</summary>
    private const double TransitionSigma = 0.05;
    /// <summary>周期を細かく求めるときの刻み（コマ）</summary>
    private const double SubFrameStep = 0.05;
    /// <summary>倍・半分の判定に使う長さ（秒）</summary>
    private const double OctaveWindowSec = 5.0;
    /// <summary>1 拍おきの低音の強さの比がこれ未満なら、本当のテンポは半分（周期が倍）</summary>
    private const double OctaveDownRatio = 0.35;
    /// <summary>
    /// 拍と拍のちょうど中間の低音の強さが、拍の位置の強さのこの割合以上なら、本当のテンポは倍（周期が半分）。
    /// （仕様書 A7 の判定は「半分にする」向きだけなので、キックが毎拍鳴る 174 BPM が 87 BPM になるのを防ぐために足した）
    /// </summary>
    private const double OctaveUpRatio = 0.75;
    /// <summary>倍のテンポにするには、拍の中間のうちこの割合以上で、拍の位置の平均の OctaveUpHitLevel 倍以上の低音が必要</summary>
    private const double OctaveUpHitShare = 0.7;
    private const double OctaveUpHitLevel = 0.4;
    /// <summary>「今のテンポに近い」とみなす差（対数の比。±6%）</summary>
    private const double CloseLogRatio = 0.06;
    /// <summary>ロック中に無視するテンポの差（対数の比。±2%）</summary>
    private const double DeadbandLogRatio = 0.02;
    /// <summary>近いテンポの候補に寄せる割合（1 回あたり）</summary>
    private const double FollowGain = 0.25;
    /// <summary>違うテンポに切り替えるのに必要な、同じ候補が続く回数</summary>
    private const int ConfirmUpdates = 3;

    // ===== ビートの位置
    /// <summary>ビートの位置を探すときに見る長さ（秒）</summary>
    private const double AcquireWindowSec = 4.5;
    /// <summary>ビートの位置を探すとき、1 拍古くなるごとに掛ける重み（新しい拍ほど重く見る）</summary>
    private const double AcquireDecay = 0.85;
    /// <summary>今の位置よりこの倍率以上そろう位置があれば取り直す</summary>
    private const double ReacquireRatio = 1.3;
    /// <summary>実際の山を探す範囲（周期に対する割合）</summary>
    private const double WindowFraction = 0.15;
    /// <summary>山を探す範囲の最小（秒。48 kHz で 3 コマ）</summary>
    private const double MinWindowSec = 3 * 512 / 48000.0;
    /// <summary>見つけたずれのうち、次のビートの位置に反映する割合（比例）</summary>
    private const double PhaseGain = 0.30;
    /// <summary>見つけたずれのうち、周期に反映する割合（積分）</summary>
    private const double PeriodGain = 0.04;
    /// <summary>ビートの位置の基準になる立ち上がりの強さの下限（ふだんの値は 1 前後）</summary>
    private const double BaseFloor = 0.05;
    /// <summary>確かさの計算に使う直近の拍の数</summary>
    private const int HistLen = 8;
    /// <summary>音楽が鳴っているのに、今のテンポでこの秒数ロックできなければ、別のテンポを試す</summary>
    private const double StuckSec = 4.5;
    /// <summary>試して合わなかったテンポに掛ける重み（くし形のフィルターの結果に掛ける）と、抑える幅（対数の比。±4%）</summary>
    private const double VetoFloor = 0.15;
    private const double VetoLogWidth = 0.04;
    /// <summary>抑えたテンポが元に戻るまでの時間（秒。曲が変わったときに、また選べるように）</summary>
    private const double VetoRecoverSec = 20.0;
    /// <summary>予測からこの秒数より離れた山は、その拍の音とみなさない（ずれた音・別の音）</summary>
    private const double OutlierSec = 0.035;
    /// <summary>そのような山で位置を直すときの強さ（ふつうの何倍か）</summary>
    private const double OutlierGain = 0.25;
    /// <summary>直近の拍のうち、音のあった拍の割合がこれ以下なら確かさ 0、HitShareFull 以上なら満点（その間は比例）</summary>
    private const double HitShareZero = 0.4;
    private const double HitShareFull = 0.74;

    // ===== 確かさとロック
    /// <summary>この確かさ以上が LockBeats 拍続いたらロック</summary>
    private const double LockConf = 0.55;
    /// <summary>この確かさ未満が LockBeats 拍続いたらロックを外す</summary>
    private const double UnlockConf = 0.30;
    private const int LockBeats = 4;
    /// <summary>低音の立ち上がりがこの秒数ないとロックを外す</summary>
    private const double NoOnsetUnlockSec = 2.0;
    /// <summary>無音がこの秒数続いたらテンポの更新を止める</summary>
    private const double FreezeSec = 0.5;
    /// <summary>無音がこの秒数続いたらビートの位置を忘れる</summary>
    private const double SilenceResetSec = 4.0;
    /// <summary>無音の間、予測したビートのたびに確かさに掛ける値</summary>
    private const double SilentBeatDecay = 0.8;

    // ===== 公開する値
    /// <summary>位置の修正を、公開する値にはこの時定数（秒）でなめらかに反映する（光の拍がカクッと飛ばないように）</summary>
    private const double GlideSec = 0.080;
    /// <summary>
    /// 立ち上がりの強さの山（コマの最後の音の時刻）から、本当に鳴った時刻までの差（秒）。
    /// A7 の試作値は 15 ミリ秒。テスト TestTempoLoops で合成音のキックの時刻と比べて確かめている
    /// </summary>
    public const double BeatLatencySec = 0.015;
    /// <summary>小節の頭の判定で、4 拍ごとの記録に掛ける減衰</summary>
    private const double DownbeatDecay = 0.9;
    /// <summary>いちばん強い拍が 2 番目よりこの割合以上強いときだけ、小節の頭を決める</summary>
    private const double DownbeatClear = 0.3;

    private readonly double _f;      // 1 秒あたりのコマ数
    private readonly int _len;       // 記録の長さ（コマ）
    private readonly int _minFrames, _updateEvery, _maHalf, _minOverlap;
    private readonly int _tauMin, _tauMax, _m, _maxLagCap, _acfN, _minW;
    private readonly int _freezeFrames, _silenceFrames, _noOnsetFrames;
    private readonly double _glideDecay;

    private readonly double[] _odf, _low, _acc;
    private long _n = -1;
    private int _count;
    private int _sinceUpdate;

    // テンポの計算用（最初に確保して使い回す）
    private readonly Fft _acfFft;
    private readonly double[] _x, _cum, _acfRe, _acfIm, _acf, _r, _obs, _post, _tmp, _prior, _trans, _phaseScore, _span;
    private double _tempoConf;
    private double _period;                 // 周期（コマ。0 = まだない）
    private double _candidate;
    private int _candidateCount;
    // 試して合わなかったテンポを抑える重み（1 = 抑えない）
    private readonly double[] _veto;
    private readonly int _stuckLimit;
    private readonly double _vetoRecover;
    private int _stuckFrames;

    // ビートの位置
    private bool _hasGrid;
    private double _nextBeat;               // 次のビートの位置（コマ。小数）
    private long _nextIdx = 1;              // 次のビートが何拍目か
    private readonly double[] _pendPos = new double[8];
    private readonly long[] _pendIdx = new long[8];
    private int _pendCount;
    private readonly double[] _errHist = new double[HistLen];
    private readonly double[] _ratioHist = new double[HistLen];
    private readonly bool[] _hitHist = new bool[HistLen];
    private int _histCount, _histHead;
    private double _phaseConf, _conf;
    private BeatLock _lock;
    private int _goodBeats, _badBeats;
    private long _lastLowOnset;
    private int _closedFrames;
    private bool _silenceDone;
    private readonly double[] _sdb = new double[4];
    private int _sdbCount;
    private int _downbeat;
    private double _glide;                  // 公開する位置の、なめらかに戻す途中のずれ（秒）
    private double _tNow;
    private BeatState _published = BeatState.None;

    /// <param name="frameRate">1 秒あたりのコマ数（サンプリング周波数 / 512。48 kHz で 93.75）</param>
    public BeatTracker(double frameRate)
    {
        _f = frameRate;
        _len = (int)Math.Round(HistorySec * frameRate);
        _minFrames = (int)Math.Round(MinHistorySec * frameRate);
        _updateEvery = Math.Max(1, (int)Math.Round(UpdateSec * frameRate));
        _maHalf = Math.Max(1, (int)Math.Round(MovingAverageSec * frameRate) / 2);
        _minOverlap = (int)Math.Round(MinOverlapSec * frameRate);
        _tauMin = (int)Math.Round(60 * frameRate / MaxBpm);
        _tauMax = (int)Math.Round(60 * frameRate / MinBpm);
        _m = _tauMax - _tauMin + 1;
        _maxLagCap = 4 * _tauMax + 4;
        _acfN = 1;
        while (_acfN < _len + _maxLagCap + 1) _acfN <<= 1;
        _minW = Math.Max(2, (int)Math.Round(MinWindowSec * frameRate));
        _freezeFrames = (int)Math.Round(FreezeSec * frameRate);
        _silenceFrames = (int)Math.Round(SilenceResetSec * frameRate);
        _noOnsetFrames = (int)Math.Round(NoOnsetUnlockSec * frameRate);
        _glideDecay = Math.Exp(-1 / (frameRate * GlideSec));

        _odf = new double[_len];
        _low = new double[_len];
        _acc = new double[_len];
        _acfFft = new Fft(_acfN);
        _x = new double[_len];
        _cum = new double[_len + 1];
        _acfRe = new double[_acfN];
        _acfIm = new double[_acfN];
        _acf = new double[_maxLagCap + 2];
        _r = new double[_m];
        _obs = new double[_m];
        _post = new double[_m];
        _tmp = new double[_m];
        _prior = new double[_m];
        _veto = new double[_m];
        _stuckLimit = (int)Math.Round(StuckSec * frameRate);
        _vetoRecover = UpdateSec / VetoRecoverSec;
        _trans = new double[_m * _m];
        _phaseScore = new double[_tauMax * 2 + 4];
        _span = new double[_len];

        // 「ありがちさ」: 120 BPM（0.5 秒）を中心に、対数で 1 オクターブの幅
        for (int j = 0; j < _m; j++)
        {
            double tau = _tauMin + j;
            double oct = Math.Log2(tau / (PriorCentreSec * frameRate));
            _prior[j] = Math.Exp(-0.5 * oct * oct);
        }
        // テンポの移り変わりやすさ: 近いテンポほど移りやすい。
        // どの行も同じ数で割る（行ごとに合計を 1 にすると、範囲の端のテンポ（181・69 BPM）は片側にしか隣がないぶん
        // 「その場にとどまる」重みが約 2 倍になり、一度そこに入ると抜け出せなくなるため）
        double rowMax = 0;
        for (int i = 0; i < _m; i++)
        {
            double sum = 0;
            for (int j = 0; j < _m; j++)
            {
                double d = Math.Log((_tauMin + j) / (double)(_tauMin + i)) / TransitionSigma;
                double a = Math.Exp(-0.5 * d * d) + 1e-3;
                _trans[i * _m + j] = a;
                sum += a;
            }
            rowMax = Math.Max(rowMax, sum);
        }
        for (int k = 0; k < _trans.Length; k++) _trans[k] /= rowMax;
        Reset();
    }

    /// <summary>公開しているテンポとビートの位置（テンポの倍率を反映したもの）</summary>
    public BeatState Published => _published;
    public BeatLock Lock => _lock;
    /// <summary>確かさ（0〜1）= ビートの位置の確かさ × (0.5 + 0.5 × テンポの確かさ)</summary>
    public double Confidence => _conf;
    public double TempoConfidence => _tempoConf;
    public double PhaseConfidence => _phaseConf;
    /// <summary>テンポの倍率を掛ける前の周期（秒。まだなければ 0）</summary>
    public double PeriodSec => _period > 0 ? _period / _f : 0;
    /// <summary>くし形のフィルターの結果（テスト・調査用。添え字 j が周期 TauMin + j コマ）</summary>
    internal double[] CombScores => _r;
    internal int TauMin => _tauMin;
    /// <summary>最後にくし形のフィルターが選んだ周期（コマ）と、倍・半分の直し（−1 = 周期を倍、1 = 半分、0 = なし。調査用）</summary>
    internal double DiagCombTau { get; private set; }
    internal int DiagOctave { get; private set; }
    internal Action<string>? DiagLog { get; set; }
    internal double FrameRate => _f;
    /// <summary>小節の頭の拍の番号の余り（0〜3。倍率を掛ける前）</summary>
    public int Downbeat => _downbeat;

    public void Reset()
    {
        Array.Clear(_odf);
        Array.Clear(_low);
        Array.Clear(_acc);
        _n = -1;
        _count = 0;
        _sinceUpdate = 0;
        for (int j = 0; j < _m; j++) _post[j] = 1.0 / _m;
        Array.Fill(_veto, 1.0);
        _stuckFrames = 0;
        _tempoConf = 0;
        _period = 0;
        _candidate = 0;
        _candidateCount = 0;
        _hasGrid = false;
        _nextBeat = 0;
        _nextIdx = 1;
        _pendCount = 0;
        ClearHist();
        _phaseConf = _conf = 0;
        _lock = BeatLock.Unlocked;
        _goodBeats = _badBeats = 0;
        _lastLowOnset = long.MinValue / 4;
        _closedFrames = 0;
        _silenceDone = false;
        Array.Clear(_sdb);
        _sdbCount = 0;
        _downbeat = 0;
        _glide = 0;
        _tNow = 0;
        _published = BeatState.None;
    }

    /// <summary>
    /// 1 コマ分を処理する。
    /// </summary>
    /// <param name="odf">立ち上がりの強さ（OnsetDetector.Odf。反応しない間は 0）</param>
    /// <param name="low">低音の立ち上がりの強さ（OnsetDetector.Low）</param>
    /// <param name="accent">キックの強さ（キックの包絡線の振幅。小節の頭の判定に使う）</param>
    /// <param name="lowOnset">このコマでキックか低い中音の立ち上がりを検出した</param>
    /// <param name="gateOpen">音が鳴っている</param>
    /// <param name="tHop">このコマのいちばん新しい音の時刻（AudioClock の秒）</param>
    /// <param name="tempoFactor">テンポの倍率（0.5 / 1 / 2）</param>
    public void Process(double odf, double low, double accent, bool lowOnset, bool gateOpen, double tHop, double tempoFactor)
    {
        _n++;
        _tNow = tHop;
        int slot = (int)(_n % _len);
        _odf[slot] = double.IsFinite(odf) ? odf : 0;
        _low[slot] = double.IsFinite(low) ? low : 0;
        _acc[slot] = double.IsFinite(accent) ? accent : 0;
        if (_count < _len) _count++;
        if (lowOnset) _lastLowOnset = _n;

        // 無音: 0.5 秒でテンポの更新を止め、4 秒でビートの位置を忘れる（テンポは半分だけ覚えておく）
        if (gateOpen)
        {
            _closedFrames = 0;
            _silenceDone = false;
        }
        else
        {
            _closedFrames++;
            if (!_silenceDone && _closedFrames >= _silenceFrames) SilenceReset();
        }

        if (++_sinceUpdate >= _updateEvery)
        {
            _sinceUpdate = 0;
            if (_count >= _minFrames && _closedFrames < _freezeFrames) UpdateTempo();
        }

        if (_hasGrid && _period > 0)
        {
            // 予測したビートの位置を過ぎたら「確かめ待ち」に入れ、次のビートへ進む
            while (_n >= _nextBeat)
            {
                AddPending(_nextBeat, _nextIdx);
                _nextBeat += _period;
                _nextIdx++;
            }
            int w = Window();
            for (int i = 0; i < _pendCount;)
            {
                if (_n >= _pendPos[i] + w)
                {
                    double pos = _pendPos[i];
                    long idx = _pendIdx[i];
                    RemovePending(i);
                    Verify(pos, idx, w, gateOpen);
                }
                else i++;
            }
        }

        // 音楽が鳴っているのに、今のテンポでロックできないまま StuckSec 続いたら、そのテンポをしばらく抑えて別の候補を試す
        if (_hasGrid && _lock == BeatLock.Unlocked && gateOpen && _n - _lastLowOnset <= _noOnsetFrames)
        {
            if (++_stuckFrames >= _stuckLimit) VetoCurrentTempo();
        }
        else if (_lock != BeatLock.Unlocked) _stuckFrames = 0;

        // 2 秒間、低音の立ち上がりがなければロックを外す
        if (_lock != BeatLock.Unlocked && _n - _lastLowOnset > _noOnsetFrames)
        {
            _lock = BeatLock.Unlocked;
            _goodBeats = 0;
        }

        _glide *= _glideDecay;
        if (Math.Abs(_glide) < 1e-7) _glide = 0;
        Publish(tempoFactor);
    }

    // ================================================================
    // テンポ

    private void UpdateTempo()
    {
        RecoverVeto();
        double tf = EstimateTempo();
        if (tf <= 0) return;
        if (_period > 0 && Math.Abs(Math.Log(tf / _period)) < CloseLogRatio)
        {
            // 今のテンポに近い: 少しずつ寄せる（ロック中は 2% 以内の差を無視して、細かく揺れないようにする）
            if (_phaseConf < 0.5 || Math.Abs(Math.Log(tf / _period)) > DeadbandLogRatio)
            {
                SetPeriodKeepLast(_period + FollowGain * (tf - _period));
            }
            _candidateCount = 0;
        }
        else if (_lock == BeatLock.Locked && _phaseConf >= 0.5)
        {
            // ロック中で、今の拍の並びが実際の音とよく合っている: 別のテンポの候補には乗り換えない
            // （くし形のフィルターは拍でない繰り返し（3 連の刻みなど）も拾うので。本当に曲のテンポが変われば、
            //   拍が合わなくなって確かさが下がり、ロックが外れてから乗り換える）
            _candidateCount = 0;
        }
        else
        {
            // 今のテンポと違う: 同じ候補（±6%）が 3 回続いたら切り替える
            if (_candidateCount > 0 && Math.Abs(Math.Log(tf / _candidate)) < CloseLogRatio)
            {
                _candidateCount++;
                _candidate = tf;
            }
            else
            {
                _candidate = tf;
                _candidateCount = 1;
            }
            if (_candidateCount >= ConfirmUpdates)
            {
                _candidateCount = 0;
                _period = tf;
                AcquirePhase(allowGlide: false);
                _stuckFrames = 0;
                return;
            }
        }

        if (_period <= 0) return;
        if (!_hasGrid)
        {
            AcquirePhase(allowGlide: false);
        }
        else if (_phaseConf < 0.5)
        {
            // 位置に自信がないときは、もっとそろう位置があれば取り直す
            int p = PhaseScores(_period);
            int best = 0;
            for (int i = 1; i < p; i++) if (_phaseScore[i] > _phaseScore[best]) best = i;
            double last = _nextBeat - _period;
            double phi = _n - last;
            phi -= Math.Floor(phi / _period) * _period;
            int cur = (int)Math.Round(phi) % p;
            if (_phaseScore[best] > ReacquireRatio * Math.Max(1e-9, _phaseScore[cur])) AcquirePhase(allowGlide: true);
        }
    }

    /// <summary>テンポの候補を求める（周期。コマ単位の小数）。求められなければ 0</summary>
    private double EstimateTempo()
    {
        int len = _count;
        long oldest = _n - len + 1;
        for (int t = 0; t < len; t++) _x[t] = _odf[(int)((oldest + t) % _len)];

        // 1. 移動平均を引いて、尖った部分だけにする
        _cum[0] = 0;
        for (int t = 0; t < len; t++) _cum[t + 1] = _cum[t] + _x[t];
        for (int t = 0; t < len; t++)
        {
            int a = Math.Max(0, t - _maHalf), b = Math.Min(len - 1, t + _maHalf);
            double ma = (_cum[b + 1] - _cum[a]) / (b - a + 1);
            double o = _x[t] - ma;
            _acfRe[t] = o > 0 ? o : 0;
            _acfIm[t] = 0;
        }
        for (int t = len; t < _acfN; t++)
        {
            _acfRe[t] = 0;
            _acfIm[t] = 0;
        }

        // 2. 自己相関を FFT で: |FFT(o)|² をもう一度 FFT すると、ずらして重ねた積の合計になる（0 を足して長さを 2 倍以上にしてあるので、端が回り込まない）
        _acfFft.Forward(_acfRe, _acfIm);
        for (int i = 0; i < _acfN; i++)
        {
            _acfRe[i] = _acfRe[i] * _acfRe[i] + _acfIm[i] * _acfIm[i];
            _acfIm[i] = 0;
        }
        _acfFft.Forward(_acfRe, _acfIm);
        int maxLag = Math.Min(_maxLagCap, len - _minOverlap);
        if (maxLag < 2 * _tauMax + 1) return 0;
        for (int tau = 0; tau <= maxLag; tau++) _acf[tau] = _acfRe[tau] / _acfN / (len - tau);
        double a0 = _acf[0];
        if (a0 <= 1e-12)
        {
            _tempoConf = 0;
            return 0;
        }
        for (int tau = 0; tau <= maxLag; tau++) _acf[tau] /= a0;

        // 3. くし形のフィルター: τ・2τ・3τ・4τ の付近（±(p−1)）の平均を足す（記録が短くて届かない分は、届いた分から見積もる）
        double sumR = 0, sumR2 = 0;
        for (int j = 0; j < _m; j++)
        {
            int tau = _tauMin + j;
            double acc = 0;
            int terms = 0;
            for (int p = 1; p <= 4; p++)
            {
                int c = p * tau;
                if (c + (p - 1) > maxLag) break;
                double s = 0;
                for (int v = -(p - 1); v <= p - 1; v++) s += _acf[c + v];
                acc += s / (2 * p - 1);
                terms++;
            }
            double r = terms > 0 ? acc * 4 / terms : 0;
            if (r < 0) r = 0;
            _r[j] = r;
            sumR += r;
            sumR2 += r * r;
        }

        // 4. ありがちさを掛け、前回の結果から滑らかにつなぐ（最大値をつなぐ: ビタビの 1 段分）
        double obsSum = 0;
        for (int j = 0; j < _m; j++)
        {
            _obs[j] = _r[j] * _prior[j] * _veto[j];
            obsSum += _obs[j];
        }
        if (obsSum <= 1e-12)
        {
            _tempoConf = 0;
            return 0;
        }
        for (int j = 0; j < _m; j++) _obs[j] /= obsSum;
        double postSum = 0;
        for (int j = 0; j < _m; j++)
        {
            double best = 0;
            for (int i = 0; i < _m; i++)
            {
                double v = _post[i] * _trans[i * _m + j];
                if (v > best) best = v;
            }
            _tmp[j] = best * _obs[j];
            postSum += _tmp[j];
        }
        if (postSum <= 1e-300)
        {
            for (int j = 0; j < _m; j++) _post[j] = _obs[j];
        }
        else
        {
            for (int j = 0; j < _m; j++) _post[j] = _tmp[j] / postSum;
        }
        int jStar = 0;
        for (int j = 1; j < _m; j++) if (_post[j] > _post[jStar]) jStar = j;
        int tauStar = _tauMin + jStar;

        // 5. 1/20 コマ単位で細かく: τ*±1 の範囲で Σ acf(p·t) がいちばん大きい t
        int pMax = 0;
        for (int p = 1; p <= 4; p++) if (p * (tauStar + 1) <= maxLag - 1) pMax = p;
        double tf = tauStar, bestScore = double.NegativeInfinity;
        for (int s = 0; s <= (int)Math.Round(2 / SubFrameStep); s++)
        {
            double t = tauStar - 1 + s * SubFrameStep;
            double score = 0;
            for (int p = 1; p <= pMax; p++) score += SmallStats.Lerp(_acf, p * t, maxLag + 1);
            if (score > bestScore)
            {
                bestScore = score;
                tf = t;
            }
        }

        // 6. 倍・半分のテンポの取り違えを、低音の立ち上がりの強さで確かめる
        DiagCombTau = tf;
        DiagOctave = 0;
        if (OctaveDown(tf)) { tf *= 2; DiagOctave = -1; }
        else if (OctaveUp(tf)) { tf /= 2; DiagOctave = 1; }
        // 試して合わなかったテンポ（抑えているテンポ）には、倍・半分の直しで戻さない
        if (DiagOctave != 0 && VetoAt(tf) < 0.5) { tf = DiagCombTau; DiagOctave = 0; }

        // 7. テンポの確かさ: 周期での一致度と、くし形の結果の飛び抜け具合（z スコア）
        double meanR = sumR / _m;
        double std = Math.Sqrt(Math.Max(0, sumR2 / _m - meanR * meanR));
        double z = std > 1e-12 ? (_r[jStar] - meanR) / std : 0;
        double ac = SmallStats.Lerp(_acf, tf, maxLag + 1);
        _tempoConf = Math.Clamp((ac - 0.05) / 0.20, 0, 1) * Math.Clamp((z - 1.5) / 1.5, 0, 1);
        return tf;
    }

    /// <summary>周期 tau の拍の位置のうち、低音の立ち上がりがいちばんそろう位置（新しい端から何コマ前か）</summary>
    private int BestLowPhase(double tau, out int beats)
    {
        int p = Math.Max(1, (int)Math.Ceiling(tau));
        int window = (int)Math.Min(_count, Math.Round(OctaveWindowSec * _f));
        beats = Math.Max(1, (int)(window / tau));
        int best = 0;
        double bestS = double.NegativeInfinity;
        for (int phi = 0; phi < p; phi++)
        {
            double s = 0;
            for (int k = 0; k < beats; k++) s += LowAt(_n - phi - (long)Math.Round(k * tau));
            if (s > bestS)
            {
                bestS = s;
                best = phi;
            }
        }
        return best;
    }

    /// <summary>仕様 A7: 1 拍おきの低音の強さが大きく違えば、本当の周期は 2 倍</summary>
    private bool OctaveDown(double tau)
    {
        if (2 * tau > _tauMax) return false;
        int phi = BestLowPhase(tau, out int beats);
        double even = 0, odd = 0;
        int ne = 0, no = 0;
        for (int k = 0; k < beats; k++)
        {
            long i = _n - phi - (long)Math.Round(k * tau);
            double v = Math.Max(LowAt(i - 1), Math.Max(LowAt(i), LowAt(i + 1)));
            if ((k & 1) == 0) { even += v; ne++; }
            else { odd += v; no++; }
        }
        if (ne == 0 || no == 0) return false;
        even /= ne;
        odd /= no;
        double mx = Math.Max(even, odd);
        return mx > 1e-9 && Math.Min(even, odd) / mx < OctaveDownRatio;
    }

    /// <summary>
    /// 追加の判定: 拍の中間にも拍と同じくらいの低音があれば、本当の周期は半分。
    /// 平均だけでなく、拍の中間の多く（OctaveUpHitShare 以上）で低音が鳴っていることも確かめる
    /// （ヒップホップのように 1 小節に 1 回だけ裏拍にキックがある曲を、倍のテンポにしないため）
    /// </summary>
    private bool OctaveUp(double tau)
    {
        double half = tau / 2;
        if (half < _tauMin) return false;
        int phi = BestLowPhase(tau, out int beats);
        double on = 0, off = 0;
        for (int k = 0; k < beats; k++)
        {
            on += Max3(_n - phi - (long)Math.Round(k * tau));
            off += Max3(_n - phi - (long)Math.Round((k + 0.5) * tau));
        }
        if (on <= 1e-9 || off / on < OctaveUpRatio) return false;
        double need = OctaveUpHitLevel * on / beats;
        int hits = 0;
        for (int k = 0; k < beats; k++)
            if (Max3(_n - phi - (long)Math.Round((k + 0.5) * tau)) >= need) hits++;
        return hits >= OctaveUpHitShare * beats;

        double Max3(long i) => Math.Max(LowAt(i - 1), Math.Max(LowAt(i), LowAt(i + 1)));
    }

    // ================================================================
    // ビートの位置

    /// <summary>
    /// 周期 tau で、新しい端から phi コマ前をビートとしたときの「そろい具合」S[phi] を _phaseScore に入れる。
    /// S[φ] = Σ_k (0.5·odf[i−1] + odf[i] + 0.5·odf[i+1])·0.85^k、i = n − φ − round(k·tau)。戻り値は φ の数
    /// </summary>
    private int PhaseScores(double tau)
    {
        int p = Math.Min(_phaseScore.Length, Math.Max(1, (int)Math.Ceiling(tau)));
        int beats = (int)(AcquireWindowSec * _f / tau);
        long oldest = _n - _count + 1;
        for (int phi = 0; phi < p; phi++)
        {
            double s = 0, w = 1;
            for (int k = 0; k <= beats; k++)
            {
                long i = _n - phi - (long)Math.Round(k * tau);
                if (i - 1 < oldest) break;
                s += (0.5 * OdfAt(i - 1) + OdfAt(i) + 0.5 * OdfAt(i + 1)) * w;
                w *= AcquireDecay;
            }
            _phaseScore[phi] = s;
        }
        return p;
    }

    /// <summary>今の周期で、ビートの位置を取り直す</summary>
    private void AcquirePhase(bool allowGlide)
    {
        DiagLog?.Invoke($"acquire glide={allowGlide} t {_tNow:F2} period {60 * _f / _period:F1}");
        int p = PhaseScores(_period);
        int best = 0;
        for (int i = 1; i < p; i++) if (_phaseScore[i] > _phaseScore[best]) best = i;
        double off = SmallStats.ParabolicOffset(_phaseScore[(best - 1 + p) % p], _phaseScore[best], _phaseScore[(best + 1) % p]);
        double lastPos = _n - (best + off);
        double next = lastPos;
        while (next <= _n) next += _period;

        double oldLast = _nextBeat - _period;
        bool had = _hasGrid;
        if (had)
        {
            // 拍の番号が時刻とずれないように、前の予測でいちばん近い拍の番号を引き継ぐ
            double oldPeriod = _period;
            long idx = _nextIdx + (long)Math.Round((next - _nextBeat) / Math.Max(1, oldPeriod));
            double shift = next - _period - oldLast;
            _nextIdx = idx;
            if (allowGlide && Math.Abs(shift) < 0.25 * _period) _glide -= shift / _f;
            else _glide = 0;
        }
        else
        {
            _glide = 0;
        }
        _nextBeat = next;
        _hasGrid = true;
        _pendCount = 0;
        ClearHist();
        _goodBeats = _badBeats = 0;
        if (!allowGlide)
        {
            Array.Clear(_sdb);
            _sdbCount = 0;
        }
    }

    /// <summary>周期を変える（いちばん最近のビートの位置は動かさず、次のビートの位置を動かす）</summary>
    private void SetPeriodKeepLast(double period)
    {
        period = Math.Clamp(period, _tauMin * 0.9, _tauMax * 1.1);
        if (_hasGrid)
        {
            double last = _nextBeat - _period;
            _nextBeat = last + period;
        }
        _period = period;
    }

    /// <summary>予測したビートの前後で、実際の立ち上がりの山を探して位置と周期を直し、確かさを更新する</summary>
    private void Verify(double pb, long idx, int w, bool gateOpen)
    {
        if (!gateOpen)
        {
            // 無音の間は合わせない（確かさだけ少しずつ下げる）
            _conf *= SilentBeatDecay;
            _phaseConf *= SilentBeatDecay;
            return;
        }
        long c = (long)Math.Round(pb);
        int best = 0;
        double mx = double.NegativeInfinity, segSum = 0;
        for (int j = 0; j <= 2 * w; j++)
        {
            double v = OdfAt(c - w + j);
            segSum += v;
            if (v > mx)
            {
                mx = v;
                best = j;
            }
        }
        double off = 0;
        if (best > 0 && best < 2 * w) off = SmallStats.ParabolicOffset(OdfAt(c - w + best - 1), mx, OdfAt(c - w + best + 1));
        double e = c - w + best + off - pb;

        // ふだんの強さ: 直近 2 拍分の中央値と平均
        int span = Math.Min(_count, Math.Max(3, (int)Math.Round(2 * _period)));
        double sum = 0;
        for (int k = 0; k < span; k++)
        {
            double v = OdfAt(_n - k);
            _span[k] = v;
            sum += v;
        }
        Array.Sort(_span, 0, span);
        double median = (span & 1) == 1 ? _span[span >> 1] : 0.5 * (_span[(span >> 1) - 1] + _span[span >> 1]);
        double baseV = Math.Max(median, BaseFloor);
        double meanAll = Math.Max(sum / span, 1e-6);
        double s = Math.Clamp(mx / (2 * baseV) - 0.5, 0, 1);
        // 予測から OutlierSec より離れた山は、その拍の音ではなく近くの別の音（拍の少し前のキックなど）とみなす:
        // 「音のない拍」として数え、位置の修正も弱くする（スウィングやシンコペーションで 1 小節に 1 回ずれた音に引っぱられないように）
        bool outlier = Math.Abs(e) > OutlierSec * _f;
        double gain = outlier ? s * OutlierGain : s;

        // 位置と周期の修正（比例・積分）。公開する位置は、なめらかに追いつかせる
        double oldLast = _nextBeat - _period;
        _nextBeat += PhaseGain * gain * e;
        double newPeriod = Math.Clamp(_period + PeriodGain * gain * e, _tauMin * 0.9, _tauMax * 1.1);
        _period = newPeriod;
        _glide -= (_nextBeat - _period - oldLast) / _f;

        AddHist(e, (segSum / (2 * w + 1)) / meanAll, s > 0.2 && !outlier);
        DiagLog?.Invoke($"beat {idx} t {_tNow:F2} e {e * 1000 / _f:F0}ms s {s:F2} ratio {(segSum / (2 * w + 1)) / meanAll:F2} w {w * 1000 / _f:F0}ms{(outlier ? " outlier" : "")}");
        UpdateConfidence();
        UpdateLock();
        UpdateDownbeat(idx, c, w);
    }

    /// <summary>
    /// 確かさ = 時刻のずれ（音があった拍だけで測る）× 拍の位置の強さ × 音のあった拍の割合。
    /// 音のない拍を「いちばん大きなずれ」として数えると、1 小節に 1 拍だけ音のない曲（ヒップホップ・ドラムンベースなど）で
    /// いつまでもロックできないので、音のない拍は割合（HitShareFull 以上で満点）でだけ見る
    /// </summary>
    private void UpdateConfidence()
    {
        if (_histCount == 0) return;
        double e2 = 0, ratio = 0;
        int hits = 0;
        for (int i = 0; i < _histCount; i++)
        {
            ratio += _ratioHist[i];
            if (!_hitHist[i]) continue;
            e2 += _errHist[i] * _errHist[i];
            hits++;
        }
        double cErr = 0;
        if (hits > 0)
        {
            double rmsMs = Math.Sqrt(e2 / hits) * 1000 / _f;
            cErr = Math.Clamp((40 - rmsMs) / 30, 0, 1);
        }
        double cRatio = Math.Clamp((ratio / _histCount - 1) / 0.5, 0, 1);
        double cHit = Math.Clamp((hits / (double)_histCount - HitShareZero) / (HitShareFull - HitShareZero), 0, 1);
        _phaseConf = cErr * cRatio * cHit;
        _conf = _phaseConf * (0.5 + 0.5 * _tempoConf);
    }

    private void UpdateLock()
    {
        _goodBeats = _conf >= LockConf ? _goodBeats + 1 : 0;
        _badBeats = _conf < UnlockConf ? _badBeats + 1 : 0;
        if (_lock == BeatLock.Unlocked)
        {
            if (_goodBeats >= LockBeats) _lock = BeatLock.Locked;
        }
        else if (_badBeats >= LockBeats)
        {
            _lock = BeatLock.Unlocked;
        }
        else
        {
            _lock = _conf >= LockConf ? BeatLock.Locked : BeatLock.Coasting;
        }
    }

    /// <summary>
    /// 小節の頭: 4 拍のうち、キックがはっきり強い拍があれば、それを 1 拍目とする。
    /// 仕様 A7 は「低音の立ち上がりの強さ」だが、それは平均で割ってあり強弱の差が小さく出るので、
    /// キックの包絡線の振幅（強く叩くほど大きい）を使う。4 つの枠がそれぞれ 2 回以上たまるまでは決めない
    /// </summary>
    private void UpdateDownbeat(long idx, long c, int w)
    {
        double m = 0;
        for (long i = c - w; i <= c + w; i++) m = Math.Max(m, AccAt(i));
        int slot = (int)(((idx % 4) + 4) % 4);
        _sdb[slot] = DownbeatDecay * _sdb[slot] + m;
        if (_sdbCount < 8 && ++_sdbCount < 8) return;
        int best = 0;
        for (int i = 1; i < 4; i++) if (_sdb[i] > _sdb[best]) best = i;
        double second = 0;
        for (int i = 0; i < 4; i++) if (i != best && _sdb[i] > second) second = _sdb[i];
        if (_sdb[best] > 1e-9 && (_sdb[best] - second) / _sdb[best] > DownbeatClear) _downbeat = best;
    }

    /// <summary>
    /// 今のテンポでロックできないまま時間がたった: そのテンポ（±4%）をしばらく抑え、テンポの記録を半分忘れて、
    /// 次に強い候補を試せるようにする（シンコペーションの多い曲で、拍ではない繰り返しのテンポに張りつかないように）
    /// </summary>
    private void VetoCurrentTempo()
    {
        _stuckFrames = 0;
        if (_period <= 0) return;
        DiagLog?.Invoke($"stuck: veto {60 * _f / _period:F1} BPM");
        for (int j = 0; j < _m; j++)
            if (Math.Abs(Math.Log((_tauMin + j) / _period)) < VetoLogWidth) _veto[j] = Math.Min(_veto[j], VetoFloor);
        _candidateCount = 0;
        // ロックできない原因でいちばん多いのは倍・半分の取り違えなので、まず倍のテンポ、次に半分のテンポを試す
        double up = _period / 2, down = _period * 2;
        double next = up >= _tauMin * 0.97 && VetoAt(up) >= 0.5 ? up : down <= _tauMax * 1.03 && VetoAt(down) >= 0.5 ? down : 0;
        if (next > 0)
        {
            // 試すテンポに、テンポの記録を寄せる（すぐに元の候補へ戻らず、ロックできるか確かめる時間をとる）
            double sum = 0;
            for (int j = 0; j < _m; j++)
            {
                double d = Math.Log((_tauMin + j) / next) / TransitionSigma;
                _post[j] = Math.Exp(-0.5 * d * d) + 1e-6;
                sum += _post[j];
            }
            for (int j = 0; j < _m; j++) _post[j] /= sum;
            DiagLog?.Invoke($"stuck: try {60 * _f / next:F1} BPM");
            _period = next;
            AcquirePhase(allowGlide: false);
            return;
        }
        for (int j = 0; j < _m; j++) _post[j] = 0.3 * _post[j] + 0.7 / _m;
    }

    /// <summary>抑えたテンポを少しずつ元に戻す（テンポを求め直すたびに呼ぶ）</summary>
    private void RecoverVeto()
    {
        for (int j = 0; j < _m; j++) _veto[j] += (1 - _veto[j]) * _vetoRecover;
    }

    /// <summary>周期 tau（コマ）のテンポを抑えている重み（範囲の外なら 1）</summary>
    private double VetoAt(double tau)
    {
        int j = (int)Math.Round(tau) - _tauMin;
        return j >= 0 && j < _m ? _veto[j] : 1;
    }

    private void SilenceReset()
    {
        _silenceDone = true;
        _hasGrid = false;
        _pendCount = 0;
        ClearHist();
        _phaseConf = _conf = 0;
        _lock = BeatLock.Unlocked;
        _goodBeats = _badBeats = 0;
        _candidateCount = 0;
        _glide = 0;
        for (int j = 0; j < _m; j++) _post[j] = 0.5 * _post[j] + 0.5 / _m;
        _stuckFrames = 0;
    }

    // ================================================================
    // 公開

    private void Publish(double tempoFactor)
    {
        if (!_hasGrid || _period <= 0)
        {
            // ビートの位置を忘れたとき: 拍の数え方は残し、ロックと確かさだけ 0 にする
            if (_published.Locked || _published.Confidence > 0)
                _published = _published with { Locked = false, Confidence = 0 };
            return;
        }
        double periodSec = _period / _f;
        double lastPos = _nextBeat - _period;
        long idx = _nextIdx - 1;
        double tLast = _tNow - (_n - lastPos) / _f - BeatLatencySec + _glide;

        double fac = tempoFactor >= 1.5 ? 2 : tempoFactor <= 0.75 ? 0.5 : 1;
        double pp = periodSec / fac;
        long idxP = idx;
        double tP = tLast;
        int db = _downbeat;
        if (fac == 2)
        {
            idxP = 2 * idx;
            db = (2 * _downbeat) % 4;
        }
        else if (fac == 0.5)
        {
            long even = idx - (((idx % 2) + 2) % 2);
            tP = tLast - (idx - even) * periodSec;
            idxP = even / 2;
            db = _downbeat >> 1;
        }
        bool locked = _lock != BeatLock.Unlocked;
        double conf = Math.Round(_conf, 3);

        var old = _published;
        bool changed = !old.HasTempo
            || old.Locked != locked
            || old.DownbeatOffset != db
            || Math.Abs(old.Confidence - conf) >= 0.02
            || Math.Abs(old.Period - pp) >= 2e-4 * pp
            || idxP - old.IndexAtLast >= 8
            || idxP < old.IndexAtLast;
        if (!changed)
        {
            // 前に公開した拍の並びのままで、今の位置とのずれが 0.5 ミリ秒未満なら差し替えない（メモリの確保を減らす）
            double predicted = old.LastBeatT + (idxP - old.IndexAtLast) * old.Period;
            changed = Math.Abs(predicted - tP) >= 0.0005;
        }
        if (changed) _published = new BeatState(pp, tP, idxP, conf, locked, db);
    }

    // ================================================================
    // 小さな道具

    private int Window() => Math.Max(_minW, (int)Math.Round(WindowFraction * _period));

    private double OdfAt(long i) => i > _n || i <= _n - _count || i < 0 ? 0 : _odf[(int)(i % _len)];

    private double LowAt(long i) => i > _n || i <= _n - _count || i < 0 ? 0 : _low[(int)(i % _len)];

    private double AccAt(long i) => i > _n || i <= _n - _count || i < 0 ? 0 : _acc[(int)(i % _len)];

    private void AddPending(double pos, long idx)
    {
        if (_pendCount == _pendPos.Length) RemovePending(0);
        _pendPos[_pendCount] = pos;
        _pendIdx[_pendCount] = idx;
        _pendCount++;
    }

    private void RemovePending(int i)
    {
        for (int k = i + 1; k < _pendCount; k++)
        {
            _pendPos[k - 1] = _pendPos[k];
            _pendIdx[k - 1] = _pendIdx[k];
        }
        _pendCount--;
    }

    private void AddHist(double err, double ratio, bool hit)
    {
        _errHist[_histHead] = err;
        _ratioHist[_histHead] = ratio;
        _hitHist[_histHead] = hit;
        _histHead = (_histHead + 1) % HistLen;
        if (_histCount < HistLen) _histCount++;
    }

    private void ClearHist()
    {
        _histCount = _histHead = 0;
    }
}

/// <summary>
/// 曲の盛り上がり（ドロップ）の判定（仕様 A7 の試作の考え方。実際の曲での調整はまだ）。
///   ブレイク: キックの帯の立ち上がりの 0.5 秒平均 ÷ 8 秒平均 が 0.25 未満の状態が 2 拍以上続いた
///   ドロップ: ブレイクの後、強い立ち上がり（しきい値の 3 倍以上）があり、比が 0.8 以上に戻った
/// </summary>
public sealed class DropDetector
{
    private const double FastSec = 0.5;
    private const double SlowSec = 8.0;
    private const double BreakdownRatio = 0.25;
    private const double DropRatio = 0.8;
    private const double DropStrength = 3.0;
    /// <summary>強い立ち上がりの後、比が戻るのを待つ時間（秒）</summary>
    private const double CandidateSec = 1.5;
    /// <summary>テンポが分からないときに「2 拍」とみなす長さ（秒）</summary>
    private const double DefaultBreakdownSec = 1.0;
    /// <summary>8 秒平均が安定するまで判定しない時間（秒）</summary>
    private const double WarmupSec = 4.0;

    private readonly double _dt, _aFast, _aSlow;
    private readonly long _warmFrames;
    private WarmEma _fast, _slow;
    private double _lowTime;
    private bool _breakdown;
    private double _candT = double.NaN;
    private float _candS;
    private double _candAge;

    public DropDetector(double dt)
    {
        _dt = dt;
        _aFast = TimeConst.Alpha(dt, FastSec);
        _aSlow = TimeConst.Alpha(dt, SlowSec);
        _warmFrames = (long)Math.Round(WarmupSec / dt);
    }

    /// <summary>いまブレイク（静かな部分）の中</summary>
    public bool InBreakdown => _breakdown;

    public void Reset()
    {
        _fast.Reset();
        _slow.Reset();
        _lowTime = 0;
        _breakdown = false;
        _candT = double.NaN;
    }

    /// <summary>1 コマ分を処理する。ドロップと判定したら true（時刻と強さを返す）</summary>
    /// <param name="kickFlux">キックの帯の立ち上がりの合計（OnsetDetector.KickFlux）</param>
    /// <param name="strongest">今回の立ち上がりの強さ（なければ 0）</param>
    /// <param name="onsetT">今回の立ち上がりの本当の時刻</param>
    /// <param name="beatSec">1 拍の長さ（秒。分からなければ 0）</param>
    public bool Update(double kickFlux, bool gateOpen, double strongest, double onsetT, double beatSec, out double t, out float strength)
    {
        t = 0;
        strength = 0;
        if (!gateOpen) return false;
        double fast = _fast.Step(kickFlux, _aFast);
        double slow = _slow.Step(kickFlux, _aSlow);
        if (_slow.N < _warmFrames || slow < 1e-6) return false;
        double ratio = fast / slow;
        double need = beatSec > 0 ? 2 * beatSec : DefaultBreakdownSec;
        if (ratio < BreakdownRatio)
        {
            _lowTime += _dt;
            if (_lowTime > need) _breakdown = true;
        }
        else _lowTime = 0;
        if (!_breakdown) return false;

        if (strongest > DropStrength && double.IsNaN(_candT))
        {
            _candT = onsetT;
            _candS = (float)strongest;
            _candAge = 0;
        }
        if (!double.IsNaN(_candT))
        {
            _candAge += _dt;
            if (ratio > DropRatio)
            {
                t = _candT;
                strength = _candS;
                _breakdown = false;
                _lowTime = 0;
                _candT = double.NaN;
                return true;
            }
            if (_candAge > CandidateSec) _candT = double.NaN;
        }
        return false;
    }
}
