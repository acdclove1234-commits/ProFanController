// ==================================================
// Logger.cs - 日付ごとのテキストログ (logs\yyyyMMdd.log。見張りプロセスは logs\yyyyMMdd_watchdog.log)
//   書き込みは別スレッドでまとめて行い、ファン制御のスレッドを待たせない。30 日より古いログは消す
// Dependencies: .NET 8 (MIT License)
// See CREDITS.txt for full license details.
// ==================================================

using System.Collections.Concurrent;

namespace ProFanController;

public static class Logger
{
    /// <summary>書き込み待ちの最大行数（これを超えたら古いものから捨てずに、新しい行を捨てる＝止まらないことを優先）</summary>
    private const int MaxQueued = 10_000;
    private const int KeepDays = 30;

    private static readonly BlockingCollection<string> Queue = new(new ConcurrentQueue<string>(), MaxQueued);
    /// <summary>まだファイルに書いていない行の数（Flush はこれが 0 になるまで待つ）</summary>
    private static int _pending;
    private static readonly object StartGate = new();
    private static Thread? _writer;

    public static string Directory => Path.Combine(AppContext.BaseDirectory, "logs");

    /// <summary>ファイル名の後ろに付ける名前（見張りプロセスは "watchdog"。別のプロセスと同じファイルに書くと行が混ざるため）</summary>
    public static string FileSuffix { get; set; } = "";

    public static void Info(string message) => Write("INFO ", message);
    public static void Warn(string message) => Write("WARN ", message);
    public static void Error(string message) => Write("ERROR", message);

    private static void Write(string level, string message)
    {
        try
        {
            EnsureWriter();
            string thread = Thread.CurrentThread.Name is { Length: > 0 } n ? n : Environment.CurrentManagedThreadId.ToString();
            string line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{level}] [{thread}] {message}";
            Interlocked.Increment(ref _pending);
            if (!Queue.TryAdd(line)) Interlocked.Decrement(ref _pending);
        }
        catch
        {
            // ログの失敗でファン制御を止めない
        }
    }

    /// <summary>書き込み待ちのログをファイルに書き終えるまで待つ（終了時・異常終了時。最大 timeoutMs ミリ秒）</summary>
    public static void Flush(int timeoutMs = 1000)
    {
        try { SpinWait.SpinUntil(() => Volatile.Read(ref _pending) <= 0, timeoutMs); } catch { /* 無視 */ }
    }

    private static void EnsureWriter()
    {
        if (_writer is not null) return;
        lock (StartGate)
        {
            if (_writer is not null) return;
            _writer = new Thread(WriterLoop) { IsBackground = true, Name = "Logger", Priority = ThreadPriority.BelowNormal };
            _writer.Start();
        }
    }

    private static void WriterLoop()
    {
        DeleteOldLogs();
        StreamWriter? writer = null;
        string openFile = "";
        while (true)
        {
            string line;
            try { line = Queue.Take(); }
            catch { return; }
            try
            {
                string file = Path.Combine(Directory, $"{DateTime.Now:yyyyMMdd}{(FileSuffix.Length > 0 ? "_" + FileSuffix : "")}.log");
                if (file != openFile || writer is null)
                {
                    writer?.Dispose();
                    System.IO.Directory.CreateDirectory(Directory);
                    // ほかのプログラム（メモ帳など）で開いていても書けるように、読み書きの共有を許す
                    var stream = new FileStream(file, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
                    writer = new StreamWriter(stream) { AutoFlush = false };
                    openFile = file;
                }
                int written = 1;
                writer.WriteLine(line);
                // 続けて届いている行はまとめて書き、なくなったらファイルに書き出す
                while (Queue.TryTake(out var more)) { writer.WriteLine(more); written++; }
                writer.Flush();
                Interlocked.Add(ref _pending, -written);
                written = 0;
            }
            catch
            {
                // 書けなかった行は捨てる（数だけ合わせて、Flush が待ち続けないようにする）
                writer?.Dispose();
                writer = null;
                Interlocked.Exchange(ref _pending, Queue.Count);
            }
        }
    }

    private static void DeleteOldLogs()
    {
        try
        {
            if (!System.IO.Directory.Exists(Directory)) return;
            foreach (var f in System.IO.Directory.GetFiles(Directory, "*.log"))
            {
                if (File.GetLastWriteTime(f) < DateTime.Now.AddDays(-KeepDays)) File.Delete(f);
            }
        }
        catch
        {
            // 消せなくても続ける
        }
    }
}
