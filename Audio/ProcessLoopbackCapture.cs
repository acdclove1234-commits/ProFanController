// ==================================================
// ProcessLoopbackCapture.cs - 「特定のアプリだけ」のための、アプリ（とその子プロセス）の音だけの取り込み
//                             (Windows のプロセス ループバック。Windows 10 ビルド 20348 以降・Windows 11。
//                              ほかのアプリの音は混ざらない。プロセスの一覧・終了の確認もここで行う)
// Dependencies: NAudio.Wasapi (MIT License), .NET 8 (MIT License)
// See CREDITS.txt for full license details.
// ==================================================

using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;
using NAudio.Wasapi.CoreAudioApi.Interfaces;
using NAudio.Wave;

namespace ProFanController;

/// <summary>
/// 指定したプロセスと、その子プロセスが出している音だけを取り込む（どの出力先に出ていても取り込める）。
/// 形式は 48 kHz・32bit 浮動小数・2ch に固定（Windows が変換する）。
/// 音が届いた合図（10 ミリ秒ごと）で専用のスレッドが読み取り、DataAvailable に渡す。
/// 読み取りのたびに配列などを作らない（受け取り用の配列は使い回す）
/// </summary>
internal sealed class ProcessLoopbackCapture : IDisposable
{
    public const int SampleRate = 48000;
    public const int Channels = 2;
    public const int BytesPerFrame = Channels * 4;

    /// <summary>Windows の仮想の出力先「プロセス ループバック」（VIRTUAL_AUDIO_DEVICE_PROCESS_LOOPBACK）</summary>
    private const string VirtualDevice = @"VAD\Process_Loopback";
    private static readonly Guid IidAudioClient = new("1CB9AD4C-DBFA-4c32-B178-C2F568A703B2");
    /// <summary>取り込み用のバッファの長さ（100 ナノ秒単位。50 ミリ秒。合図ごとに読むので遅れにはならない）</summary>
    private const long BufferDuration = 50 * 10_000;

    private readonly EventWaitHandle _ready = new(false, EventResetMode.AutoReset);
    private AudioClient? _client;
    private Thread? _thread;
    private volatile bool _stop;
    private byte[] _buffer = new byte[BytesPerFrame * 4096];
    private long _packets;
    private long _silentPackets;
    private long _frames;

    /// <summary>取り込むプロセスの PID</summary>
    public int ProcessId { get; }
    /// <summary>子プロセスの音も取り込む</summary>
    public bool IncludeTree { get; }
    /// <summary>届いた音（32bit 浮動小数・2ch・48 kHz の並び, バイト数）。取り込みのスレッドから呼ばれる。配列は次の呼び出しで使い回す</summary>
    public Action<byte[], int>? DataAvailable { get; set; }
    /// <summary>取り込みが止まった（読み取りに失敗した）。取り込みのスレッドから呼ばれる</summary>
    public Action<Exception>? Stopped { get; set; }
    /// <summary>受け取った音の塊の数（確認用）</summary>
    public long Packets => Interlocked.Read(ref _packets);
    /// <summary>そのうち、Windows が「無音」と印を付けていた塊の数（確認用）</summary>
    public long SilentPackets => Interlocked.Read(ref _silentPackets);
    /// <summary>受け取った音の個数（1 チャンネル分。確認用）</summary>
    public long Frames => Interlocked.Read(ref _frames);

    /// <summary>この Windows でアプリの音だけを取り込めるか</summary>
    public static bool IsSupported => OperatingSystem.IsWindows() && AppAudioTarget.Supports(Environment.OSVersion.Version);

    public ProcessLoopbackCapture(int processId, bool includeTree = true)
    {
        ProcessId = processId;
        IncludeTree = includeTree;
    }

    /// <summary>
    /// 取り込みの準備をする（Windows に取り込み口を作ってもらい、形式を決める）。失敗したら例外を投げる。
    /// まだ取り込みは始めない（Start で始める）
    /// </summary>
    public void Open(TimeSpan timeout)
    {
        if (_client is not null) return;
        var client = Activate(ProcessId, IncludeTree, timeout);
        try
        {
            var format = WaveFormat.CreateIeeeFloatWaveFormat(SampleRate, Channels);
            client.Initialize(AudioClientShareMode.Shared,
                AudioClientStreamFlags.Loopback | AudioClientStreamFlags.EventCallback | AudioClientStreamFlags.AutoConvertPcm | AudioClientStreamFlags.SrcDefaultQuality,
                BufferDuration, 0, format, Guid.Empty);
            client.SetEventHandle(_ready.SafeWaitHandle.DangerousGetHandle());
            _ = client.AudioCaptureClient; // ここで作っておく（読み取りのスレッドで初めて作らないように）
            _client = client;
        }
        catch
        {
            client.Dispose();
            throw;
        }
    }

    /// <summary>取り込みを始める（Open のあと）</summary>
    public void Start()
    {
        var client = _client ?? throw new InvalidOperationException("取り込みの準備ができていません");
        if (_thread is not null) return;
        _stop = false;
        client.Start();
        _thread = new Thread(ReadLoop) { IsBackground = true, Name = "ProcessLoopback", Priority = ThreadPriority.AboveNormal };
        _thread.Start();
    }

    public void Stop()
    {
        _stop = true;
        _ready.Set();
        var thread = _thread;
        _thread = null;
        if (thread is not null && thread != Thread.CurrentThread) thread.Join(2000);
        try { _client?.Stop(); } catch { /* すでに止まっている・取り込み口が無くなった */ }
    }

    public void Dispose()
    {
        Stop();
        try { _client?.Dispose(); } catch { /* 終了時は無視 */ }
        _client = null;
        _ready.Dispose();
    }

    private void ReadLoop()
    {
        try
        {
            var capture = _client!.AudioCaptureClient;
            while (!_stop)
            {
                // 音を出していない間は合図が来ないこともある。止める指示を見逃さないよう、ときどき起きる
                if (!_ready.WaitOne(100) || _stop) continue;
                int packet = capture.GetNextPacketSize();
                while (packet > 0 && !_stop)
                {
                    IntPtr data = capture.GetBuffer(out int frames, out AudioClientBufferFlags flags);
                    int bytes = frames * BytesPerFrame;
                    if (_buffer.Length < bytes) _buffer = new byte[Math.Max(bytes, _buffer.Length * 2)];
                    bool silent = (flags & AudioClientBufferFlags.Silent) != 0;
                    if (bytes > 0)
                    {
                        if (silent) Array.Clear(_buffer, 0, bytes);
                        else Marshal.Copy(data, _buffer, 0, bytes);
                    }
                    capture.ReleaseBuffer(frames);
                    Interlocked.Increment(ref _packets);
                    if (silent) Interlocked.Increment(ref _silentPackets);
                    Interlocked.Add(ref _frames, frames);
                    if (bytes > 0) DataAvailable?.Invoke(_buffer, bytes);
                    packet = capture.GetNextPacketSize();
                }
            }
        }
        catch (Exception ex)
        {
            if (!_stop) Stopped?.Invoke(ex);
        }
    }

    // =====================================================================
    // 取り込み口を作る（ActivateAudioInterfaceAsync。終わるまで待つ）
    // =====================================================================

    private static AudioClient Activate(int processId, bool includeTree, TimeSpan timeout)
    {
        var parameters = new ActivationParams
        {
            ActivationType = 1, // AUDIOCLIENT_ACTIVATION_TYPE_PROCESS_LOOPBACK
            TargetProcessId = (uint)processId,
            // PROCESS_LOOPBACK_MODE_INCLUDE_TARGET_PROCESS_TREE = 0 / _EXCLUDE_ = 1
            ProcessLoopbackMode = includeTree ? 0 : 1,
        };
        int paramsSize = Marshal.SizeOf<ActivationParams>();
        IntPtr block = Marshal.AllocHGlobal(paramsSize + Marshal.SizeOf<PropVariantBlob>());
        bool freeBlock = true;
        IActivateAudioInterfaceAsyncOperation? operation = null;
        var handler = new ActivationHandler();
        try
        {
            Marshal.StructureToPtr(parameters, block, false);
            IntPtr variant = block + paramsSize;
            Marshal.StructureToPtr(new PropVariantBlob { Vt = 65 /* VT_BLOB */, Size = (uint)paramsSize, Data = block }, variant, false);
            int hr = ActivateAudioInterfaceAsync(VirtualDevice, IidAudioClient, variant, handler, out operation);
            if (hr < 0) throw new ProcessLoopbackException($"取り込み口を作れません（{Hr(hr)}）", hr);
            if (!handler.Done.Wait(timeout))
            {
                // Windows がまだ中身を読むかもしれないので、渡したメモリは解放しない（わずかな量）
                freeBlock = false;
                throw new ProcessLoopbackException("Windows の音のサービスが応答しません", 0);
            }
            if (handler.Result < 0 || handler.Client is null)
                throw new ProcessLoopbackException($"取り込み口を作れません（{Hr(handler.Result)}）", handler.Result);
            return new AudioClient((IAudioClient)handler.Client);
        }
        finally
        {
            if (operation is not null) Marshal.ReleaseComObject(operation);
            handler.Done.Dispose();
            if (freeBlock) Marshal.FreeHGlobal(block);
        }
    }

    private static string Hr(int hr)
    {
        string text = Marshal.GetExceptionForHR(hr)?.Message ?? "";
        return text.Length > 0 ? $"0x{hr:X8}: {text}" : $"0x{hr:X8}";
    }

    [DllImport("Mmdevapi.dll", ExactSpelling = true)]
    private static extern int ActivateAudioInterfaceAsync(
        [MarshalAs(UnmanagedType.LPWStr)] string deviceInterfacePath,
        [MarshalAs(UnmanagedType.LPStruct)] Guid riid,
        IntPtr activationParams,
        IActivateAudioInterfaceCompletionHandler completionHandler,
        out IActivateAudioInterfaceAsyncOperation activationOperation);

    /// <summary>AUDIOCLIENT_ACTIVATION_PARAMS（種類と AUDIOCLIENT_PROCESS_LOOPBACK_PARAMS）</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct ActivationParams
    {
        public int ActivationType;
        public uint TargetProcessId;
        public int ProcessLoopbackMode;
    }

    /// <summary>PROPVARIANT（VT_BLOB のときの形）</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct PropVariantBlob
    {
        public ushort Vt;
        public ushort Reserved1;
        public ushort Reserved2;
        public ushort Reserved3;
        public uint Size;
        public IntPtr Data;
    }

    /// <summary>取り込み口ができたときの知らせを受け取る（Windows の別スレッドから呼ばれる。どのスレッドから呼んでもよい印 IAgileObject を付ける）</summary>
    [ComVisible(true)]
    [ClassInterface(ClassInterfaceType.None)]
    private sealed class ActivationHandler : IActivateAudioInterfaceCompletionHandler, IAgileObject
    {
        public readonly ManualResetEventSlim Done = new(false);
        public int Result = unchecked((int)0x80004005); // E_FAIL
        public object? Client;

        public void ActivateCompleted(IActivateAudioInterfaceAsyncOperation activateOperation)
        {
            try
            {
                activateOperation.GetActivateResult(out int hr, out object client);
                Result = hr;
                Client = hr >= 0 ? client : null;
            }
            catch (Exception ex)
            {
                Result = ex.HResult;
            }
            finally
            {
                try { Done.Set(); } catch (ObjectDisposedException) { /* 待つのをやめたあとに届いた */ }
            }
        }
    }

    [ComImport]
    [Guid("94ea2b94-e9cc-49e0-c0ff-ee64ca8f5b90")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAgileObject { }
}

/// <summary>アプリの音だけの取り込みを始められなかった（HResult は Windows のエラーの番号。不明なら 0）</summary>
internal sealed class ProcessLoopbackException : Exception
{
    public ProcessLoopbackException(string message, int hresult) : base(message) => HResult = hresult;
}

/// <summary>プロセスの一覧と、プロセスの終了の確認（Windows の API で直接調べる）</summary>
internal static class ProcessList
{
    /// <summary>名前（拡張子 .exe を除く・大文字小文字は区別しない）が一致するプロセスの一覧（PID・親の PID）</summary>
    public static List<ProcEntry> Snapshot(string name)
    {
        var list = new List<ProcEntry>();
        IntPtr snap = CreateToolhelp32Snapshot(SnapProcess, 0);
        if (snap == InvalidHandle) return list;
        try
        {
            var e = new ProcessEntry32 { Size = (uint)Marshal.SizeOf<ProcessEntry32>() };
            for (bool ok = Process32FirstW(snap, ref e); ok; ok = Process32NextW(snap, ref e))
            {
                string exe = AppAudioTarget.NormalizeName(e.ExeFile);
                if (string.Equals(exe, name, StringComparison.OrdinalIgnoreCase)) list.Add(new ProcEntry((int)e.ProcessId, (int)e.ParentProcessId, exe));
            }
        }
        finally
        {
            CloseHandle(snap);
        }
        return list;
    }

    /// <summary>
    /// 終了を確認するためにプロセスを開く。開けないときは null。
    /// gone は、もう無い（終了した・PID が別のプログラムに使い回された）ときに true、権限がなくて開けないときは false
    /// </summary>
    public static SafeProcessHandle? Open(int pid, string name, out bool gone)
    {
        gone = false;
        var h = OpenProcess(Synchronize | QueryLimitedInformation, false, pid);
        if (h.IsInvalid)
        {
            // ERROR_INVALID_PARAMETER: その PID のプロセスは無い
            gone = Marshal.GetLastWin32Error() == ErrorInvalidParameter;
            h.Dispose();
            return null;
        }
        var path = new char[1024];
        int size = path.Length;
        bool named = QueryFullProcessImageNameW(h, 0, path, ref size);
        if (!IsAlive(h) || (named && !string.Equals(AppAudioTarget.NormalizeName(Path.GetFileName(new string(path, 0, size))), name, StringComparison.OrdinalIgnoreCase)))
        {
            gone = true;
            h.Dispose();
            return null;
        }
        return h;
    }

    /// <summary>プロセスがまだ動いているか（配列などを作らない）</summary>
    public static bool IsAlive(SafeProcessHandle h) => !h.IsInvalid && !h.IsClosed && WaitForSingleObject(h, 0) == WaitTimeout;

    private const uint SnapProcess = 0x00000002;
    private const uint Synchronize = 0x00100000;
    private const uint QueryLimitedInformation = 0x00001000;
    private const uint WaitTimeout = 0x00000102;
    private const int ErrorInvalidParameter = 87;
    private static readonly IntPtr InvalidHandle = new(-1);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ProcessEntry32
    {
        public uint Size;
        public uint Usage;
        public uint ProcessId;
        public IntPtr DefaultHeapId;
        public uint ModuleId;
        public uint Threads;
        public uint ParentProcessId;
        public int PriorityClassBase;
        public uint Flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string ExeFile;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr CreateToolhelp32Snapshot(uint flags, uint processId);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, ExactSpelling = true)]
    private static extern bool Process32FirstW(IntPtr snapshot, ref ProcessEntry32 entry);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, ExactSpelling = true)]
    private static extern bool Process32NextW(IntPtr snapshot, ref ProcessEntry32 entry);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern SafeProcessHandle OpenProcess(uint access, bool inherit, int processId);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, ExactSpelling = true)]
    private static extern bool QueryFullProcessImageNameW(SafeProcessHandle process, uint flags, [Out] char[] name, ref int size);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint WaitForSingleObject(SafeProcessHandle handle, uint milliseconds);
}
