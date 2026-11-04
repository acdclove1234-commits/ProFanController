// ==================================================
// RenderClock.cs - LED を描く間隔を正確に保つ時計 (高精度の待機タイマー・省電力による間引きの解除・
//                  1 秒に 60 コマの予定表・デバイスごとの「いつ送るか」の判断・送信の速さの調整)
// Dependencies: .NET 8 (MIT License)
// See CREDITS.txt for full license details.
// ==================================================

using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace ProFanController;

/// <summary>RenderClock.Wait が戻った理由</summary>
public enum ClockWake
{
    /// <summary>指定した時間がたった</summary>
    Elapsed,
    /// <summary>止める合図が来た</summary>
    Cancelled,
    /// <summary>「すぐ見直して」の合図が来た（設定の変更・再接続ボタンなど）</summary>
    Woken,
}

/// <summary>
/// LED を描くスレッド専用の「待つ」ための時計。
/// Windows の普通の待ち（WaitOne(ミリ秒)）は 15.6 ミリ秒単位でしか起きられないため、1 秒に 60 コマ（16.7 ミリ秒ごと）を
/// 正確に刻めない。そこで Windows 10 (1803) 以降の「高精度の待機タイマー」を使う。
/// 作れない古い Windows では、動くモードの間だけ timeBeginPeriod(1) で Windows 全体のタイマーを 1 ミリ秒単位にする。
/// 止める合図（cancel）と「すぐ見直して」の合図（wake）も同時に待つので、停止や設定の変更にはすぐ反応する
/// </summary>
public sealed class RenderClock : IDisposable
{
    /// <summary>CREATE_WAITABLE_TIMER_HIGH_RESOLUTION（高精度のタイマーを作る）</summary>
    private const uint CreateWaitableTimerHighResolution = 0x2;
    /// <summary>TIMER_ALL_ACCESS</summary>
    private const uint TimerAllAccess = 0x1F0003;
    /// <summary>タイマーの合図が何かの理由で来なくても、これだけ余分に待ったら起きる（ミリ秒）</summary>
    private const int SafetyMarginMs = 100;

    private readonly SafeWaitHandle? _timer;
    private readonly WaitHandle? _timerHandle;
    /// <summary>待つ合図の一覧（毎コマ配列を作らないよう、最初に用意しておく）</summary>
    private readonly WaitHandle[] _withTimer;
    private readonly WaitHandle[] _withoutTimer;
    private bool _fine;
    private bool _disposed;

    /// <param name="cancel">止める合図（CancellationToken.WaitHandle など）</param>
    /// <param name="wake">「すぐ見直して」の合図</param>
    public RenderClock(WaitHandle cancel, WaitHandle wake)
    {
        _withoutTimer = new[] { cancel, wake };
        try
        {
            if (OperatingSystem.IsWindows())
            {
                var h = CreateWaitableTimerExW(IntPtr.Zero, null, CreateWaitableTimerHighResolution, TimerAllAccess);
                if (h.IsInvalid) h.Dispose();
                else _timer = h;
            }
        }
        catch
        {
            _timer = null; // 古い Windows（関数がない）・Windows 以外
        }
        if (_timer is not null)
        {
            _timerHandle = new NativeWaitHandle(_timer);
            _withTimer = new[] { _timerHandle, cancel, wake };
        }
        else
        {
            _withTimer = _withoutTimer;
        }
    }

    /// <summary>高精度の待機タイマーを使えている（false なら timeBeginPeriod で代用）</summary>
    public bool HighResolution => _timer is not null;

    /// <summary>
    /// 高精度のタイマーが使えないとき、動くモードの間だけ Windows のタイマーを 1 ミリ秒単位にする（on = true）。
    /// 止まっているモードでは元に戻す（Windows 全体の消費電力が少し増えるため）
    /// </summary>
    public void SetFineResolution(bool on)
    {
        if (HighResolution || on == _fine || _disposed) return;
        try
        {
            if (!OperatingSystem.IsWindows()) return;
            if (on) timeBeginPeriod(1);
            else timeEndPeriod(1);
            _fine = on;
        }
        catch
        {
            // winmm.dll がない環境では何もしない
        }
    }

    /// <summary>
    /// seconds 秒待つ。途中で cancel・wake の合図が来たらすぐ戻る。
    /// 高精度のタイマーでは 1 ミリ秒より細かく起きられる。代用のときは 1 ミリ秒単位（四捨五入）
    /// </summary>
    public ClockWake Wait(double seconds)
    {
        if (seconds <= 0 || _disposed) return ClockWake.Elapsed;
        if (_timer is not null)
        {
            // 負の値 = 今からの相対時間（100 ナノ秒単位）
            long due = -Math.Max(1, (long)(seconds * 1e7));
            bool set;
            try { set = SetWaitableTimerEx(_timer, ref due, 0, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 0); }
            catch { set = false; }
            if (set)
            {
                int r = WaitHandle.WaitAny(_withTimer, (int)(seconds * 1000) + SafetyMarginMs);
                return r switch { 1 => ClockWake.Cancelled, 2 => ClockWake.Woken, _ => ClockWake.Elapsed };
            }
        }
        int ms = Math.Max(1, (int)Math.Round(seconds * 1000));
        int i = WaitHandle.WaitAny(_withoutTimer, ms);
        return i switch { 0 => ClockWake.Cancelled, 1 => ClockWake.Woken, _ => ClockWake.Elapsed };
    }

    private static int _throttlingDone;

    /// <summary>
    /// このプロセスを Windows の省電力による間引き（EcoQoS・タイマーの精度を下げる）の対象から外す。
    /// タスクトレイに隠れていても 1 秒に 60 コマを保つため。1 回呼べば十分（2 回目以降は何もしない）。
    /// 対応していない Windows では何もせず false
    /// </summary>
    public static bool DisablePowerThrottling()
    {
        if (Interlocked.Exchange(ref _throttlingDone, 1) != 0) return true;
        try
        {
            if (!OperatingSystem.IsWindows()) return false;
            var state = new ProcessPowerThrottlingState
            {
                Version = 1, // PROCESS_POWER_THROTTLING_CURRENT_VERSION
                // EXECUTION_SPEED (0x1) と IGNORE_TIMER_RESOLUTION (0x4) を「間引かない」(StateMask = 0) にする
                ControlMask = 0x1 | 0x4,
                StateMask = 0,
            };
            return SetProcessInformation(GetCurrentProcess(), ProcessPowerThrottling, ref state, (uint)Marshal.SizeOf<ProcessPowerThrottlingState>());
        }
        catch
        {
            return false;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        SetFineResolution(false);
        _disposed = true;
        _timerHandle?.Dispose(); // タイマーのハンドルも閉じる
    }

    /// <summary>Windows のハンドルを WaitHandle.WaitAny で待てるようにする包み</summary>
    private sealed class NativeWaitHandle : WaitHandle
    {
        public NativeWaitHandle(SafeWaitHandle handle) => SafeWaitHandle = handle;
    }

    private const int ProcessPowerThrottling = 4; // PROCESS_INFORMATION_CLASS の ProcessPowerThrottling

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessPowerThrottlingState
    {
        public uint Version;
        public uint ControlMask;
        public uint StateMask;
    }

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "CreateWaitableTimerExW")]
    private static extern SafeWaitHandle CreateWaitableTimerExW(IntPtr timerAttributes, string? timerName, uint flags, uint desiredAccess);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWaitableTimerEx(SafeWaitHandle timer, ref long dueTime, int period, IntPtr completionRoutine,
        IntPtr argToCompletionRoutine, IntPtr wakeContext, uint tolerableDelay);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetProcessInformation(IntPtr process, int infoClass, ref ProcessPowerThrottlingState info, uint size);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentProcess();

    [DllImport("winmm.dll")]
    private static extern uint timeBeginPeriod(uint period);

    [DllImport("winmm.dll")]
    private static extern uint timeEndPeriod(uint period);
}

/// <summary>
/// 一定の間隔でコマを描くための予定表（純粋な計算なのでテストできる）。
///   ・描く時刻は「予定の時刻」（Next）を使う。実際に起きた時刻は少しばらつくが、予定の時刻で描けば動きが均等になる
///   ・1 コマ描いたら Next += 間隔
///   ・大きく遅れた（2 コマ分より多い）ときは、遅れを取り戻そうと何コマも続けて描かず、今の時刻から数え直す
/// </summary>
public sealed class FrameCadence
{
    /// <summary>予定より少し早く起きても描いてよい幅（秒）。予定の時刻で描くので見た目は変わらない</summary>
    public const double EarlyTolerance = 0.0005;

    /// <param name="period">コマの間隔（秒）</param>
    /// <param name="start">最初のコマの予定の時刻（AudioClock の秒）</param>
    public FrameCadence(double period, double start)
    {
        Period = period;
        Next = start;
    }

    /// <summary>コマの間隔（秒）</summary>
    public double Period { get; private set; }
    /// <summary>次のコマの予定の時刻（AudioClock の秒）</summary>
    public double Next { get; private set; }

    /// <summary>次のコマを描く時刻になったか</summary>
    public bool IsDue(double now) => now >= Next - EarlyTolerance;

    /// <summary>
    /// これから描くコマの時刻を返す（予定の時刻）。
    /// 2 コマ分より多く遅れていたら、予定を今の時刻に合わせ直す（まとめ描きしない）
    /// </summary>
    public double Begin(double now)
    {
        if (now - Next > 2 * Period) Next = now;
        return Next;
    }

    /// <summary>1 コマ描き終えたら呼ぶ（予定を 1 コマ分進める）</summary>
    public void Advance() => Next += Period;

    /// <summary>間隔を変える（動くモード ⇔ 止まっているモード）。変えたときは次のコマをすぐ描く</summary>
    public void SetPeriod(double period, double now)
    {
        if (period == Period) return;
        Period = period;
        if (Next > now) Next = now;
    }
}

/// <summary>SendScheduler.Decide の答え</summary>
public enum SendReason
{
    /// <summary>まだ送らない</summary>
    None,
    /// <summary>いつもの間隔で送る</summary>
    Regular,
    /// <summary>ビート（またはキック）に合わせて、いつもより早めに送る</summary>
    Aligned,
}

/// <summary>
/// デバイスに送るタイミングを決める（純粋な計算なのでテストできる）。
/// 速いデバイス（USB）は毎コマ送る。遅いデバイス（GPU など I2C。1 秒に 8 回まで）は、
///   ・いつもの間隔（1 / MaxFps 秒）ごとに送る（端数は次に持ち越すので、平均するとちょうど MaxFps 回）
///   ・ビートの瞬間（またはテンポをつかんでいないときはキックの直後）は、前に送ってから間隔の 6 割たっていれば早めに送る。
///     早めに送ったら、次のいつもの送信はそこから 1 間隔後にする（ビートに合わせて刻み直す）
///   ・テンポをつかんでいて、次のビートが「間隔の 6 割」より近いときは、いつもの送信をビートまで待つ
///     （直前に送ってしまうと、6 割あける決まりのせいでビートの瞬間に送れなくなるため）
/// こうすると、1 秒に 8 回しか送れない GPU でもビートの光を取りこぼさない
/// </summary>
public sealed class SendScheduler
{
    /// <summary>早めに送るときも、前に送ってからいつもの間隔のこの割合はあける（送りすぎて表示が乱れないように）</summary>
    public const double EarlyFraction = 0.6;

    /// <summary>次にいつもの間隔で送る予定の時刻（まだ送っていなければ負の無限大）</summary>
    public double NextDue { get; private set; } = double.NegativeInfinity;
    /// <summary>前に送った時刻（まだなら負の無限大）</summary>
    public double LastSend { get; private set; } = double.NegativeInfinity;
    /// <summary>前に送ってから、早めに送るきっかけ（キックなど）があった。送ると消える</summary>
    public bool AccentLatched { get; private set; }

    /// <summary>早めに送るきっかけ（キックなど）を覚えておく。実際に送るまで消えない</summary>
    public void Latch() => AccentLatched = true;

    public void Reset()
    {
        NextDue = double.NegativeInfinity;
        LastSend = double.NegativeInfinity;
        AccentLatched = false;
    }

    /// <param name="tNow">このコマの時刻（予定の時刻）</param>
    /// <param name="tick">コマの間隔（秒。1/60 など）</param>
    /// <param name="interval">いつもの送る間隔（秒。1 / MaxFps）</param>
    /// <param name="beatCue">このコマでビートの頭をまたいだ（テンポをつかんでいるときだけ true にする）</param>
    /// <param name="nextBeatIn">次のビートの頭まで何秒か（テンポをつかんでいないときは正の無限大）</param>
    public SendReason Decide(double tNow, double tick, double interval, bool beatCue, double nextBeatIn = double.PositiveInfinity)
    {
        // コマの間隔より短い間隔を求められたら毎コマ送る
        if (interval <= tick * 1.0001) return SendReason.Regular;
        double since = tNow - LastSend;
        double early = EarlyFraction * interval - 1e-9;
        if (beatCue && since >= early) return SendReason.Aligned;
        // いつもの送信は、予定の時刻にいちばん近いコマで
        if (tNow >= NextDue - tick / 2)
        {
            // 次のビートが近いなら、ビートのコマまで待つ（ビートのコマは今から何コマ後か、で比べる）
            double beatFrameIn = Math.Ceiling(nextBeatIn / tick - 1e-6) * tick;
            if (beatFrameIn < early && since + beatFrameIn < 2 * interval) return SendReason.None;
            return SendReason.Regular;
        }
        if (AccentLatched && since >= early) return SendReason.Aligned;
        return SendReason.None;
    }

    /// <summary>実際に送ったら呼ぶ（Decide の答えを渡す）</summary>
    public void Commit(double tNow, double tick, double interval, SendReason reason)
    {
        if (reason == SendReason.Regular && interval > tick * 1.0001 && !double.IsNegativeInfinity(NextDue))
        {
            NextDue += interval;
            // 送らなかったコマが続いて予定に追いつけていない（このコマでまた送る時刻になる）なら、今から数え直す
            if (tNow >= NextDue - tick / 2) NextDue = tNow + interval;
        }
        else
        {
            NextDue = tNow + interval;
        }
        LastSend = tNow;
        AccentLatched = false;
    }
}

/// <summary>
/// 送信の速さの調整。OpenRGB への書き込みがいつも 5 ミリ秒より長くかかる（OpenRGB の処理が追いつかず
/// 送ったデータがたまっている）なら、そのデバイスの 1 秒あたりの送信回数を下げる。速くなったら少しずつ戻す
/// </summary>
public sealed class SendRateGovernor
{
    /// <summary>この時間より長い書き込みを「遅い」とみなす（ミリ秒）</summary>
    public const double SlowWriteMs = 5;
    /// <summary>下げるときの下限（回／秒）</summary>
    public const double MinFps = 4;
    /// <summary>遅い書き込みの割合をならす重み（1 回の送信あたり）</summary>
    private const double Alpha = 0.1;
    /// <summary>下げてから次に下げるまで（秒）</summary>
    private const double LowerEverySec = 1;
    /// <summary>上げるまでに、速い状態が続いてほしい時間（秒）</summary>
    private const double RaiseAfterSec = 10;

    private double _slowFraction;
    private double _lastChange = double.NegativeInfinity;

    /// <summary>いまの上限（回／秒）。下げていなければ正の無限大</summary>
    public double Limit { get; private set; } = double.PositiveInfinity;
    /// <summary>上限を下げている</summary>
    public bool Limited => !double.IsPositiveInfinity(Limit);

    public void Reset()
    {
        _slowFraction = 0;
        _lastChange = double.NegativeInfinity;
        Limit = double.PositiveInfinity;
    }

    /// <summary>1 回送るたびに呼ぶ</summary>
    /// <param name="writeMs">書き込みにかかった時間（ミリ秒）</param>
    /// <param name="now">時刻（秒）</param>
    /// <param name="configuredFps">設定上の上限（回／秒）</param>
    public void Observe(double writeMs, double now, double configuredFps)
    {
        _slowFraction += ((writeMs > SlowWriteMs ? 1.0 : 0.0) - _slowFraction) * Alpha;
        if (double.IsNegativeInfinity(_lastChange)) _lastChange = now;
        if (_slowFraction > 0.5 && now - _lastChange >= LowerEverySec)
        {
            // 「たいてい遅い」→ 4 分の 3 に下げる
            Limit = Math.Max(MinFps, Math.Min(Limit, configuredFps) * 0.75);
            _lastChange = now;
            _slowFraction = 0.25; // すぐ続けて下げないよう、少し様子を見る
        }
        else if (Limited && _slowFraction < 0.05 && now - _lastChange >= RaiseAfterSec)
        {
            Limit *= 1.25;
            if (Limit >= configuredFps) Limit = double.PositiveInfinity;
            _lastChange = now;
        }
    }
}

/// <summary>1 秒あたりの回数を数える（表示用）</summary>
public sealed class FpsMeter
{
    private int _count;
    private double _since;

    public FpsMeter(double now) => _since = now;

    /// <summary>直近に測った値（回／秒）</summary>
    public double Value { get; private set; }

    public void Count() => _count++;

    /// <summary>前に測ってから 0.25 秒以上たっていれば測り直す。直近の値を返す</summary>
    public double Sample(double now)
    {
        double span = now - _since;
        if (span >= 0.25)
        {
            Value = _count / span;
            _count = 0;
            _since = now;
        }
        return Value;
    }

    public void Reset(double now)
    {
        _count = 0;
        _since = now;
        Value = 0;
    }
}
