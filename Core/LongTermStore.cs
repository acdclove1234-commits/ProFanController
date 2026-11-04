// ==================================================
// LongTermStore.cs - 長期の記録 (1 分ごとの平均をファイルに保存。アプリを再起動しても履歴が残り、劣化の検知に使う)
// Dependencies: .NET 8 (MIT License)
// See CREDITS.txt for full license details.
// ==================================================

using System.Globalization;
using System.Text;

namespace ProFanController;

/// <summary>1 分間の平均値（キーは HistoryStore と同じ: センサー ID・rpm:端子・duty:端子）</summary>
public sealed class MinuteRow
{
    public DateTime Time { get; init; }
    public Dictionary<string, float> Values { get; } = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>その 1 分間の Duty の変化幅（duty:端子 → 最大 − 最小）。回転が安定していた時間だけを比べるため</summary>
    public Dictionary<string, float> DutySpread { get; } = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>役割（cpuTemp など）→ センサー ID。部品を交換しても役割で比べられるように</summary>
    public IReadOnlyDictionary<string, string> Roles { get; init; } = new Dictionary<string, string>();

    public float? Get(string key) => Values.TryGetValue(key, out var v) ? v : null;
    public float? Role(string role) => Roles.TryGetValue(role, out var id) ? Get(id) : null;
}

public static class StatRoles
{
    public const string CpuTemp = "cpuTemp";
    public const string GpuTemp = "gpuTemp";
    public const string Ambient = "ambient";
    public const string CpuPower = "cpuPower";
    public const string GpuPower = "gpuPower";
    public const string CpuLoad = "cpuLoad";
    public const string GpuLoad = "gpuLoad";
}

/// <summary>
/// 1 秒ごとのスナップショットを 1 分ごとに平均して、data\minutes_yyyyMMdd.csv に追記する。
/// 列（記録するセンサー）が変わったら、その日のファイルを分ける（minutes_yyyyMMdd_2.csv）。
/// 1 行 100 バイト程度なので、180 日分でも 30 MB 弱。画面スレッドから使う。
/// </summary>
public sealed class LongTermStore
{
    public const int KeepDays = 180;

    public static string Directory => Path.Combine(AppContext.BaseDirectory, "data");

    private sealed class Acc
    {
        public double Sum;
        public int Count;
        public double Min = double.MaxValue;
        public double Max = double.MinValue;
    }

    private readonly Dictionary<string, Acc> _acc = new(StringComparer.OrdinalIgnoreCase);
    private DateTime _minute = DateTime.MinValue;
    private DateTime _firstSample, _lastSample;
    private string? _file;
    private List<string> _columns = new();
    private string _rolesLine = "";

    /// <summary>記録する列（キー）と役割。設定やハードウェアが変わったら呼び直す</summary>
    public IReadOnlyList<string> Keys { get; private set; } = Array.Empty<string>();
    public IReadOnlyDictionary<string, string> Roles { get; private set; } = new Dictionary<string, string>();

    public void Configure(IEnumerable<string> keys, IReadOnlyDictionary<string, string> roles)
    {
        Keys = keys.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        Roles = new Dictionary<string, string>(roles);
    }

    /// <summary>毎秒呼ぶ。分が変わったら前の 1 分を書き出す</summary>
    public void Add(DateTime time, IReadOnlyDictionary<string, float?> values, IReadOnlyList<FanStatus> fans)
    {
        var minute = new DateTime(time.Year, time.Month, time.Day, time.Hour, time.Minute, 0);
        if (minute != _minute)
        {
            Flush();
            _minute = minute;
            _firstSample = time;
        }
        _lastSample = time;
        foreach (var key in Keys)
        {
            double? v = null;
            if (key.StartsWith("rpm:")) v = fans.FirstOrDefault(f => string.Equals(f.ControlId, key[4..], StringComparison.OrdinalIgnoreCase))?.Rpm;
            else if (key.StartsWith("duty:")) v = fans.FirstOrDefault(f => string.Equals(f.ControlId, key[5..], StringComparison.OrdinalIgnoreCase))?.Duty;
            else if (values.TryGetValue(key, out var x)) v = x;
            if (v is not double d || double.IsNaN(d)) continue;
            if (!_acc.TryGetValue(key, out var a)) _acc[key] = a = new Acc();
            a.Sum += d;
            a.Count++;
            a.Min = Math.Min(a.Min, d);
            a.Max = Math.Max(a.Max, d);
        }
    }

    /// <summary>集計中の 1 分を書き出す（アプリ終了時にも呼ぶ）</summary>
    public void Flush()
    {
        if (_acc.Count == 0 || _minute == DateTime.MinValue) return;
        // 30 秒未満しかデータがない分（起動直後・終了直前）は、平均が偏るので捨てる
        if (_lastSample - _firstSample < TimeSpan.FromSeconds(30))
        {
            _acc.Clear();
            return;
        }
        try
        {
            EnsureFile(_minute);
            var sb = new StringBuilder(_minute.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture));
            foreach (var col in _columns)
            {
                sb.Append(',');
                bool spread = col.StartsWith("~");
                string key = spread ? col[1..] : col;
                if (!_acc.TryGetValue(key, out var a) || a.Count == 0) continue;
                double v = spread ? a.Max - a.Min : a.Sum / a.Count;
                sb.Append(v.ToString("0.#", CultureInfo.InvariantCulture));
            }
            File.AppendAllText(_file!, sb.AppendLine().ToString());
        }
        catch (Exception ex)
        {
            Logger.Warn($"長期の記録の保存に失敗: {ex.Message}");
        }
        _acc.Clear();
    }

    private void EnsureFile(DateTime minute)
    {
        // 列: キー、Duty には変化幅（~duty:端子）も付ける
        var columns = Keys.SelectMany(k => k.StartsWith("duty:") ? new[] { k, "~" + k } : new[] { k }).ToList();
        string rolesLine = "#roles " + string.Join(";", Roles.Select(r => $"{r.Key}={r.Value}"));
        string day = minute.ToString("yyyyMMdd", CultureInfo.InvariantCulture);
        if (_file is not null && Path.GetFileName(_file).StartsWith($"minutes_{day}") && columns.SequenceEqual(_columns) && rolesLine == _rolesLine) return;

        System.IO.Directory.CreateDirectory(Directory);
        string path = Path.Combine(Directory, $"minutes_{day}.csv");
        for (int i = 2; File.Exists(path) && !SameHeader(path, columns, rolesLine); i++)
            path = Path.Combine(Directory, $"minutes_{day}_{i}.csv");
        if (!File.Exists(path))
        {
            File.WriteAllText(path, rolesLine + "\n" + "Time," + string.Join(",", columns.Select(Escape)) + "\n", new UTF8Encoding(false));
            Cleanup();
        }
        else
        {
            RepairTrailingNewline(path);
        }
        _file = path;
        _columns = columns;
        _rolesLine = rolesLine;
    }

    /// <summary>前回、行の途中で PC が落ちていたら改行を足す（次の行が前の行にくっついて読めなくなるのを防ぐ）</summary>
    private static void RepairTrailingNewline(string path)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite);
            if (fs.Length == 0) return;
            fs.Seek(-1, SeekOrigin.End);
            if (fs.ReadByte() == '\n') return;
            fs.Seek(0, SeekOrigin.End);
            fs.WriteByte((byte)'\n');
        }
        catch
        {
            // 直せなくても記録は続ける
        }
    }

    private static bool SameHeader(string path, List<string> columns, string rolesLine)
    {
        using var r = OpenShared(path);
        return r.ReadLine() == rolesLine && r.ReadLine() == "Time," + string.Join(",", columns.Select(Escape));
    }

    /// <summary>書き込み中のファイルも読めるように開く（分析は別スレッドで行うため）</summary>
    private static StreamReader OpenShared(string path)
        => new(new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete));

    /// <summary>ID にカンマは入らないが、念のため置き換える</summary>
    private static string Escape(string s) => s.Replace(',', ';');

    private static void Cleanup()
    {
        var limit = DateTime.Today.AddDays(-KeepDays);
        foreach (var f in System.IO.Directory.GetFiles(Directory, "minutes_*.csv"))
        {
            if (File.GetLastWriteTime(f) < limit) File.Delete(f);
        }
    }

    /// <summary>指定した期間の記録を読む（古い順）</summary>
    public static List<MinuteRow> Load(DateTime from, DateTime to)
    {
        var rows = new List<MinuteRow>();
        if (!System.IO.Directory.Exists(Directory)) return rows;
        foreach (var file in System.IO.Directory.GetFiles(Directory, "minutes_*.csv").OrderBy(f => f, StringComparer.Ordinal))
        {
            string name = Path.GetFileNameWithoutExtension(file);
            if (!DateTime.TryParseExact(name.Substring(8, 8), "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var day)) continue;
            if (day < from.Date || day > to.Date) continue;
            try { ReadFile(file, from, to, rows); }
            catch (Exception ex) { Logger.Warn($"長期の記録を読めません: {file}: {ex.Message}"); }
        }
        rows.Sort((a, b) => a.Time.CompareTo(b.Time));
        return rows;
    }

    private static void ReadFile(string file, DateTime from, DateTime to, List<MinuteRow> rows)
    {
        using var r = OpenShared(file);
        string? rolesLine = r.ReadLine();
        string? header = r.ReadLine();
        if (rolesLine is null || header is null || !rolesLine.StartsWith("#roles")) return;
        var roles = rolesLine[6..].Trim().Split(';', StringSplitOptions.RemoveEmptyEntries)
            .Select(p => p.Split('=', 2)).Where(p => p.Length == 2)
            .ToDictionary(p => p[0], p => p[1]);
        var cols = header.Split(',');
        while (r.ReadLine() is { } line)
        {
            var parts = line.Split(',');
            if (parts.Length != cols.Length) continue; // 書き込み途中で落ちた行
            if (!DateTime.TryParseExact(parts[0], "yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out var t)) continue;
            if (t < from || t > to) continue;
            var row = new MinuteRow { Time = t, Roles = roles };
            for (int i = 1; i < cols.Length; i++)
            {
                if (parts[i].Length == 0 || !float.TryParse(parts[i], NumberStyles.Float, CultureInfo.InvariantCulture, out var v)) continue;
                if (cols[i].StartsWith("~")) row.DutySpread[cols[i][1..]] = v;
                else row.Values[cols[i]] = v;
            }
            rows.Add(row);
        }
    }
}
