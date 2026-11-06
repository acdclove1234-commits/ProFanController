// ==================================================
// AudioCapture.cs - 音楽連動のための音の取り込み (スピーカー／ヘッドホンに出ている音を WASAPI ループバックで読み取る。
//                   音が届いた合図ですぐ読み取り、出力先の切り替え・抜き差し・スリープ復帰に自動で追従する。
//                   「特定のアプリだけ」はアプリごとの音量メーターで判定する)
// Dependencies: NAudio.Wasapi (MIT License), .NET 8 (MIT License)
// See CREDITS.txt for full license details.
// ==================================================

using System.Diagnostics;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;
using NAudio.Wave;

namespace ProFanController;

/// <summary>
/// 専用のスレッドで音を取り込み、AudioEngine に渡し続ける。マイクは使わない。
///   ・音が届いた合図（イベント）ですぐ読み取る（10 ミリ秒ごと）。合図が来ない環境では、一定間隔で読みに行く方式に自動で切り替える
///   ・届いた音の 1 つ 1 つに、SampleClock で正確な時刻を付ける
///   ・何も再生していないとき Windows は音を送ってこないので、その間は無音（0）を自分で足して時間を進める
///     （テンポの記録や、光がゆっくり消えていく動きが途切れないように）
///   ・出力先の切り替え・抜き差しは Windows からの知らせで、止まってしまったときは自動で取り込み直す
/// 「特定のアプリだけ」のときは、全体の音量のうちそのアプリの音量の割合を反応の強さに掛ける。
/// </summary>
public sealed class AudioCapture : IDisposable
{
    /// <summary>音が届かない状態がこれだけ続いたら、無音で時間を進める（秒）</summary>
    private const double SilenceAfter = 0.1;
    /// <summary>無音で埋めるのは「今」からこれだけ前まで（秒。本物の音が遅れて届いたときに重ならないように）</summary>
    private const double SilenceLead = 0.01;
    /// <summary>音が鳴っているのにこれだけ何も届かなければ、取り込みが止まったとみなす（秒）</summary>
    private const double StallAfter = 1.5;
    /// <summary>アプリの音の割合をならす時定数（秒）</summary>
    private const double AppGateTau = 0.08;

    private readonly object _feedGate = new();
    private readonly AutoResetEvent _wake = new(false);
    private Thread? _thread;
    private volatile bool _stop;
    private volatile bool _restartRequested;
    private volatile string _source = "";
    private AudioEngineSettings _settings = AudioEngineSettings.Default;
    private bool _clickDetect;
    private float[] _mono = new float[4096];
    private readonly float[] _zeros = new float[AudioEngine.Hop];
    private SampleClock _clock = new(48000);
    private double _lastArrival = double.NegativeInfinity;
    private double _nextSampleT = double.NegativeInfinity;
    private string _lastLog = "";
    private int _failures;
    /// <summary>出力先の音量メーターの最後の値（0〜1。無音で埋めてよいかの判断に使う）</summary>
    private double _lastPeak;
    /// <summary>合図での読み取りが止まった出力先（次からは一定間隔で読む）</summary>
    private readonly HashSet<string> _pollingEndpoints = new();

    /// <summary>分析エンジン（出力先のサンプリング周波数が変わると作り直す。使うたびにこのプロパティから読む）</summary>
    public AudioEngine Engine { get; private set; } = new() { StatsLogging = true };
    /// <summary>状態（設定画面の表示用）</summary>
    public string Status { get; private set; } = "停止中";
    /// <summary>いま音を取り込んでいる出力先の名前（スピーカー・ヘッドホンなど）</summary>
    public string EndpointName { get; private set; } = "";
    /// <summary>いま音を取り込んでいる出力先の ID</summary>
    public string EndpointId { get; private set; } = "";
    /// <summary>音が届いた合図で読み取っている（false なら一定間隔で読みに行く方式）</summary>
    public bool EventDriven { get; private set; }
    public bool Running => _thread is not null;

    /// <summary>同期テストのクリック音を記録させる（エンジンを作り直しても引き継ぐ）</summary>
    public bool ClickDetect
    {
        get => _clickDetect;
        set { _clickDetect = value; Engine.ClickDetect = value; }
    }

    /// <summary>反応する音（空ならすべて、アプリ名ならそのアプリだけ）と分析の設定を変える。すぐに反映する</summary>
    public void Configure(string source, AudioEngineSettings settings)
    {
        string s = (source ?? "").Trim();
        // "Spotify.exe" のように拡張子付きで入力されても探せるように
        if (s.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) s = Path.GetFileNameWithoutExtension(s);
        var engine = Engine;
        bool tempoChanged = engine.Settings.TempoFactor != settings.TempoFactor || engine.Settings.TempoMode != settings.TempoMode;
        _source = s;
        _settings = settings;
        engine.Settings = settings;
        if (tempoChanged) engine.ResetTempo();
    }

    public void Start()
    {
        if (_thread is not null) return;
        _stop = false;
        _restartRequested = false;
        _thread = new Thread(Run) { IsBackground = true, Name = "AudioCapture", Priority = ThreadPriority.AboveNormal };
        _thread.Start();
    }

    public void Stop()
    {
        var thread = _thread;
        if (thread is null) return;
        _stop = true;
        _wake.Set();
        thread.Join(3000);
        _thread = null;
        Status = "停止中";
        EndpointName = "";
        EndpointId = "";
    }

    /// <summary>取り込みをやり直す（スリープからの復帰後・出力先の切り替えなど）。止まっていれば何もしない</summary>
    public void Restart()
    {
        if (_thread is null) return;
        _restartRequested = true;
        _wake.Set();
    }

    private void Run()
    {
        MMDeviceEnumerator? enumerator = null;
        EndpointNotifier? notifier = null;
        try
        {
            while (!_stop)
            {
                _restartRequested = false;
                try
                {
                    // 起動直後で Windows の音のサービスがまだ準備中のこともあるので、作るところから再試行の対象にする
                    if (enumerator is null)
                    {
                        enumerator = new MMDeviceEnumerator();
                        var n = new EndpointNotifier(this);
                        try { enumerator.RegisterEndpointNotificationCallback(n); notifier = n; }
                        catch (Exception ex) { Log($"音楽連動: 出力先の切り替えの知らせを受け取れません（5 秒ごとに確認します）: {ex.Message}"); }
                    }
                    var result = CaptureOnce(enumerator);
                    _failures = 0;
                    if (result == CaptureResult.SwitchToPolling && EndpointId.Length > 0 && _pollingEndpoints.Add(EndpointId))
                        Log($"音楽連動: 音が届いた合図が来ないため、この出力先（{EndpointName}）は一定間隔で読み取ります");
                }
                catch (Exception ex)
                {
                    _failures++;
                    // 失敗が続くときは、待つ時間を 3 → 10 → 30 秒と延ばす（記録も同じ内容は繰り返さない）
                    int wait = _failures <= 1 ? 3 : _failures == 2 ? 10 : 30;
                    Status = $"音を取り込めません（{wait} 秒後に再試行）: {Message(ex)}";
                    Log($"音楽連動: 取り込みに失敗: {Message(ex)}");
                    // 取り込みを閉じたときの「止まった」合図が残っていると待たずに再試行してしまうので、消してから待つ
                    if (!_stop && !_restartRequested) _wake.Reset();
                    _wake.WaitOne(TimeSpan.FromSeconds(wait));
                }
            }
        }
        finally
        {
            if (notifier is not null)
            {
                try { enumerator?.UnregisterEndpointNotificationCallback(notifier); } catch { /* 終了時は無視 */ }
            }
            try { enumerator?.Dispose(); } catch { /* 終了時は無視 */ }
        }
    }

    private enum CaptureResult { Normal, SwitchToPolling }

    /// <summary>今の出力先から取り込みを 1 回行う（出力先が変わる・止まる・止めるまで戻らない）</summary>
    private CaptureResult CaptureOnce(MMDeviceEnumerator enumerator)
    {
        if (!enumerator.HasDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia))
        {
            Status = "音の出力先（スピーカー・ヘッドホン）が見つかりません";
            EndpointName = "";
            EndpointId = "";
            _wake.WaitOne(TimeSpan.FromSeconds(3));
            return CaptureResult.Normal;
        }

        using var device = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
        string id = device.ID;
        bool polling = _pollingEndpoints.Contains(id);
        using var capture = new LowLatencyLoopback(device, eventSync: !polling);
        var format = capture.WaveFormat;
        Guid? sub = format is WaveFormatExtensible ext ? ext.SubFormat : null;
        var kind = PcmConvert.Detect((int)format.Encoding, format.BitsPerSample, sub);
        if (kind == PcmKind.Unsupported || format.SampleRate <= 0)
        {
            throw new NotSupportedException($"この音の形式には対応していません（{format.Encoding}・{format.BitsPerSample} bit・{format.SampleRate} Hz）");
        }
        int channels = Math.Max(1, format.Channels);

        lock (_feedGate)
        {
            if (Engine.SampleRate != format.SampleRate)
            {
                Engine = new AudioEngine(format.SampleRate) { Settings = _settings, ClickDetect = _clickDetect, StatsLogging = true };
            }
            else
            {
                Engine.Reset(); // 出力先が変わった・取り込み直した: テンポの記録などを最初から
            }
            _clock = new SampleClock(format.SampleRate);
            _lastArrival = double.NegativeInfinity;
            _nextSampleT = double.NegativeInfinity;
        }

        Exception? stopped = null;
        bool stoppedFlag = false;
        capture.DataAvailable += (_, e) => Feed(e.Buffer, e.BytesRecorded, kind, channels);
        capture.RecordingStopped += (_, e) => { stopped = e.Exception; Volatile.Write(ref stoppedFlag, true); _wake.Set(); };
        capture.StartRecording();

        EndpointName = device.FriendlyName;
        EndpointId = id;
        EventDriven = !polling;
        Status = $"音を取り込み中: {device.FriendlyName}";
        Log($"音楽連動: 取り込み開始（{device.FriendlyName}・{format.SampleRate} Hz・{channels} ch・{kind}・{(polling ? "一定間隔" : "合図で読み取り")}）");

        var result = CaptureResult.Normal;
        var sessions = new List<AudioSessionControl>();
        var sessionDevices = new List<MMDevice>();
        double appMissingSince = double.NaN;
        bool appMissingLogged = false;
        string sessionsFor = "";
        double sessionsAt = double.NegativeInfinity;
        double deviceCheckAt = AudioClock.Now;
        double meterAt = double.NegativeInfinity;
        double started = AudioClock.Now;
        double gate = 1;
        double lastLoop = AudioClock.Now;
        try
        {
            while (!_stop && !_restartRequested && !Volatile.Read(ref stoppedFlag))
            {
                _wake.WaitOne(10);
                double now = AudioClock.Now;
                double dt = Math.Clamp(now - lastLoop, 0, 0.5);
                lastLoop = now;

                FillSilence(now, _lastPeak > 0.001);

                // 出力先が変わっていないか（知らせを受け取れない環境のための予備。5 秒ごと）
                if (now - deviceCheckAt > 5)
                {
                    deviceCheckAt = now;
                    using var current = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
                    if (current.ID != id) break;
                }

                // 音が鳴っているのに何も届かない: 取り込みが止まっている（合図が来ない環境なら方式を切り替える）
                if (now - meterAt > 0.1)
                {
                    meterAt = now;
                    double lastData = Volatile.Read(ref _lastArrival);
                    double since = now - (double.IsNegativeInfinity(lastData) ? started : lastData);
                    _lastPeak = device.AudioMeterInformation.MasterPeakValue;
                    if (since > StallAfter && _lastPeak > 0.01)
                    {
                        Log("音楽連動: 音が鳴っているのに取り込めていないため、取り込み直します");
                        result = double.IsNegativeInfinity(lastData) && !polling ? CaptureResult.SwitchToPolling : CaptureResult.Normal;
                        break;
                    }
                }

                // 「特定のアプリだけ」: そのアプリの音の割合（ならして使う）
                string source = _source;
                double target;
                if (source.Length == 0)
                {
                    target = 1;
                }
                else
                {
                    if (source != sessionsFor || now - sessionsAt > 3)
                    {
                        sessionsAt = now;
                        sessionsFor = source;
                        DisposeAll(sessions);
                        DisposeDevices(sessionDevices);
                        sessions = FindSessions(enumerator, device, source, sessionDevices);
                    }
                    double total = device.AudioMeterInformation.MasterPeakValue;
                    double app = 0;
                    foreach (var s in sessions)
                    {
                        try { app = Math.Max(app, s.AudioMeterInformation.MasterPeakValue); }
                        catch { /* アプリが終了した */ }
                    }
                    target = total < 0.001 ? 0 : Math.Clamp(app / total, 0, 1);
                    // 音は鳴っているのに、選んだアプリの音がどの出力先にも見つからない状態が続いたら、1 回だけ記録に残す
                    if (total > 0.01 && sessions.Count == 0)
                    {
                        if (double.IsNaN(appMissingSince)) appMissingSince = now;
                        else if (!appMissingLogged && now - appMissingSince > 10)
                        {
                            appMissingLogged = true;
                            Log($"音楽連動: 「特定のアプリだけ」で選んだ {source} の音が見つかりません（ほかの音には反応しません）。アプリが再生中か、選んだアプリが合っているか確認してください");
                        }
                    }
                    else appMissingSince = double.NaN;
                }
                gate += (target - gate) * (1 - Math.Exp(-dt / AppGateTau));
                Engine.AppGate = gate;
            }
        }
        finally
        {
            DisposeAll(sessions);
            DisposeDevices(sessionDevices);
            try { capture.StopRecording(); } catch { /* 止まっている */ }
        }

        if (Volatile.Read(ref stoppedFlag) && stopped is not null)
        {
            Log($"音楽連動: 取り込みが止まりました（取り込み直します）: {Message(stopped)}");
            if (!_stop && !_restartRequested) _wake.Reset();
            _wake.WaitOne(500);
        }
        return result;
    }

    /// <summary>届いた音（ふつうは 32bit 浮動小数・2ch）をモノラルにして分析に回す（NAudio の取り込みスレッドから呼ばれる）</summary>
    private void Feed(byte[] buffer, int bytes, PcmKind kind, int channels)
    {
        if (bytes <= 0) return;
        double arrival = AudioClock.Now;
        int frames = bytes / (PcmConvert.BytesPerSample(kind) * channels);
        if (frames <= 0) return;
        lock (_feedGate)
        {
            if (_mono.Length < frames) _mono = new float[Math.Max(frames, _mono.Length * 2)];
            int n = PcmConvert.ToMono(buffer.AsSpan(0, bytes), kind, channels, _mono);
            double t = _clock.Observe(n, arrival);
            // 時刻が前の音より戻らないようにする（無音で埋めた直後に本物の音が届いたときなど）。
            // わずかなずれ（5 ミリ秒未満）は少しずつ戻して正しい時刻に近づけ、大きく重なるときは前の音の続きにする
            if (t < _nextSampleT) t = _nextSampleT - t < 0.005 ? Math.Max(t, _nextSampleT - 0.0002) : _nextSampleT;
            Engine.Push(_mono.AsSpan(0, n), t);
            _nextSampleT = t + (double)n / _clock.SampleRate;
            Volatile.Write(ref _lastArrival, arrival);
        }
    }

    /// <summary>音が届いていない間は、無音（0）を足して時間を進める（今から 10 ミリ秒前まで）</summary>
    private void FillSilence(double now, bool soundPlaying)
    {
        // 出力先のメーターが音を示しているなら、本物の音が遅れて届くだけなので埋めない（曲の途中に無音を挟まない）
        if (soundPlaying) return;
        lock (_feedGate)
        {
            double lastData = _lastArrival;
            if (!double.IsNegativeInfinity(lastData) && now - lastData < SilenceAfter) return;
            int rate = _clock.SampleRate;
            // まだ一度も音が届いていなければ、今から少し前を始まりにする
            double from = double.IsNegativeInfinity(_nextSampleT) ? now - SilenceLead : _nextSampleT;
            int frames = (int)Math.Floor((now - SilenceLead - from) * rate);
            if (double.IsNegativeInfinity(_nextSampleT))
            {
                _nextSampleT = from;
                return;
            }
            if (frames <= 0) return;
            // 長く止まっていた（スリープなど）ときは 1 秒分だけ足し、時刻はそこから飛ばす
            if (frames > rate)
            {
                _nextSampleT = now - SilenceLead - 1.0;
                frames = rate;
            }
            while (frames > 0)
            {
                int n = Math.Min(frames, _zeros.Length);
                _clock.AdvanceSilence(n, now);
                double t = _nextSampleT;
                Engine.Push(_zeros.AsSpan(0, n), t);
                _nextSampleT = t + (double)n / rate;
                frames -= n;
            }
        }
    }

    /// <summary>
    /// 選んだアプリの音のセッションを探す。取り込んでいる出力先にないときは、ほかの出力先も探す
    /// （SteelSeries Sonar・VB-Audio などの仮想の出力先を通すと、アプリの音はそちらのセッションになり、
    ///   実際に鳴っている出力先には見えないため）。ほかの出力先で見つけたときは、その出力先を extra に入れて開いたままにする
    /// </summary>
    private static List<AudioSessionControl> FindSessions(MMDeviceEnumerator enumerator, MMDevice device, string processName, List<MMDevice> extra)
    {
        var list = new List<AudioSessionControl>();
        var pids = new HashSet<uint>();
        foreach (var p in Process.GetProcessesByName(processName))
        {
            using (p) pids.Add((uint)p.Id);
        }
        if (pids.Count == 0) return list;
        AddSessions(device, pids, list);
        if (list.Count > 0) return list;
        try
        {
            foreach (var d in enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
            {
                bool keep = false;
                try
                {
                    if (d.ID != device.ID)
                    {
                        int before = list.Count;
                        AddSessions(d, pids, list);
                        keep = list.Count > before;
                    }
                }
                catch { /* 途中で外れた出力先 */ }
                if (keep) extra.Add(d);
                else d.Dispose();
            }
        }
        catch { /* 出力先の一覧を取れない */ }
        return list;
    }

    private static void AddSessions(MMDevice device, HashSet<uint> pids, List<AudioSessionControl> list)
    {
        var manager = device.AudioSessionManager;
        manager.RefreshSessions();
        var sessions = manager.Sessions;
        for (int i = 0; i < sessions.Count; i++)
        {
            AudioSessionControl? s = null;
            try
            {
                s = sessions[i];
                if (pids.Contains(s.GetProcessID)) { list.Add(s); s = null; }
            }
            catch { /* 途中で終了したセッション */ }
            finally { s?.Dispose(); }
        }
    }

    private static void DisposeDevices(List<MMDevice> devices)
    {
        foreach (var d in devices)
        {
            try { d.Dispose(); } catch { /* 無視 */ }
        }
        devices.Clear();
    }

    private static void DisposeAll(List<AudioSessionControl> sessions)
    {
        foreach (var s in sessions)
        {
            try { s.Dispose(); } catch { /* 無視 */ }
        }
        sessions.Clear();
    }

    private static string Message(Exception ex) => ex is AggregateException { InnerException: { } inner } ? inner.Message : ex.Message;

    /// <summary>同じ内容を何度も記録しない</summary>
    private void Log(string message)
    {
        if (message == _lastLog) return;
        _lastLog = message;
        Logger.Info(message);
    }

    /// <summary>出力先の切り替え・抜き差しの知らせ（Windows の別スレッドから呼ばれるので、合図だけして戻る）</summary>
    private sealed class EndpointNotifier : IMMNotificationClient
    {
        private readonly AudioCapture _owner;
        public EndpointNotifier(AudioCapture owner) => _owner = owner;

        public void OnDefaultDeviceChanged(DataFlow flow, Role role, string defaultDeviceId)
        {
            if (flow == DataFlow.Render && role == Role.Multimedia) _owner.Restart();
        }

        public void OnDeviceStateChanged(string deviceId, DeviceState newState)
        {
            if (deviceId == _owner.EndpointId && newState != DeviceState.Active) _owner.Restart();
        }

        public void OnDeviceRemoved(string deviceId)
        {
            if (deviceId == _owner.EndpointId) _owner.Restart();
        }

        public void OnDeviceAdded(string pwstrDeviceId) { }

        public void OnPropertyValueChanged(string pwstrDeviceId, PropertyKey key) { }
    }

    public void Dispose() => Stop();
}

/// <summary>
/// 遅れの少ないループバック録音。NAudio の WasapiLoopbackCapture は 0.1 秒ずつまとめて渡すので、
/// 音が届いた合図ですぐ読む（10 ミリ秒ずつ）設定の WasapiCapture をループバックとして使う。
/// 合図が来ない古い環境では、一定間隔（20 ミリ秒）で読みに行く
/// </summary>
internal sealed class LowLatencyLoopback : WasapiCapture
{
    public LowLatencyLoopback(MMDevice device, bool eventSync) : base(device, eventSync, eventSync ? 10 : 20) { }

    protected override AudioClientStreamFlags GetAudioClientStreamFlags() => AudioClientStreamFlags.Loopback;
}
