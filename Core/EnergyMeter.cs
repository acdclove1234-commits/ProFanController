// ==================================================
// EnergyMeter.cs - 消費電力量と電気代の目安 (CPU・GPU のセンサー値＋その他の目安を積算して日ごとに保存)
// Dependencies: .NET 8 / System.Text.Json (MIT License)
// See CREDITS.txt for full license details.
// ==================================================

using System.Globalization;
using System.Text.Json;

namespace ProFanController;

public sealed class DayEnergy
{
    public double Kwh { get; set; }
    public double CpuKwh { get; set; }
    public double GpuKwh { get; set; }
    /// <summary>記録した時間（アプリが動いていた時間）</summary>
    public double Hours { get; set; }
}

/// <summary>
/// 電力（W）× 時間 を足し合わせて kWh にする。CPU と GPU はセンサーの値、それ以外（マザーボード・メモリ・SSD・
/// ファン・電源の損失など）は設定した目安の W を使う。壁のコンセントで測った値ではないので「目安」として表示する。
/// 画面スレッドから使う。
/// </summary>
public sealed class EnergyMeter
{
    /// <summary>これ以上データが空いたら（スリープなど）、その間は数えない</summary>
    private static readonly TimeSpan MaxGap = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan SaveInterval = TimeSpan.FromMinutes(5);

    public static string FilePath => Path.Combine(LongTermStore.Directory, "energy.json");

    private readonly SortedDictionary<string, DayEnergy> _days = new(StringComparer.Ordinal);
    private DateTime? _last;
    private DateTime _lastSave = DateTime.Now;

    public double CurrentWatts { get; private set; }
    public double CpuWatts { get; private set; }
    public double GpuWatts { get; private set; }

    public EnergyMeter()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                var loaded = JsonSerializer.Deserialize<Dictionary<string, DayEnergy>>(File.ReadAllText(FilePath));
                if (loaded is not null) foreach (var (k, v) in loaded) _days[k] = v;
            }
        }
        catch (Exception ex)
        {
            Logger.Warn($"電力量の記録を読めません: {ex.Message}");
        }
    }

    private static string Key(DateTime d) => d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    public void Add(DateTime time, double? cpuW, double? gpuW, double baseW)
    {
        CpuWatts = cpuW is > 0 and < 1000 ? cpuW.Value : 0;
        GpuWatts = gpuW is > 0 and < 1500 ? gpuW.Value : 0;
        CurrentWatts = CpuWatts + GpuWatts + baseW;

        if (_last is DateTime last && time > last && time - last <= MaxGap)
        {
            double h = (time - last).TotalHours;
            if (!_days.TryGetValue(Key(time), out var day)) _days[Key(time)] = day = new DayEnergy();
            day.Kwh += CurrentWatts * h / 1000;
            day.CpuKwh += CpuWatts * h / 1000;
            day.GpuKwh += GpuWatts * h / 1000;
            day.Hours += h;
        }
        _last = time;
        if (time - _lastSave >= SaveInterval) Save();
    }

    public void Save()
    {
        _lastSave = DateTime.Now;
        try
        {
            Directory.CreateDirectory(LongTermStore.Directory);
            // 2 年より前は消す
            foreach (var k in _days.Keys.Where(k => string.CompareOrdinal(k, Key(DateTime.Today.AddYears(-2))) < 0).ToList()) _days.Remove(k);
            string tmp = FilePath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(_days, new JsonSerializerOptions { WriteIndented = true }));
            File.Move(tmp, FilePath, overwrite: true);
        }
        catch (Exception ex)
        {
            Logger.Warn($"電力量の保存に失敗: {ex.Message}");
        }
    }

    public DayEnergy Day(DateTime date) => _days.TryGetValue(Key(date), out var d) ? d : new DayEnergy();

    public double TodayKwh => Day(DateTime.Today).Kwh;

    /// <summary>今月の合計（kWh）</summary>
    public double MonthKwh(DateTime month)
    {
        string prefix = month.ToString("yyyy-MM", CultureInfo.InvariantCulture);
        return _days.Where(d => d.Key.StartsWith(prefix, StringComparison.Ordinal)).Sum(d => d.Value.Kwh);
    }

    /// <summary>今月の見込み（記録のある日の平均 × 月の日数）</summary>
    public double MonthForecastKwh(DateTime today)
    {
        string prefix = today.ToString("yyyy-MM", CultureInfo.InvariantCulture);
        var days = _days.Where(d => d.Key.StartsWith(prefix, StringComparison.Ordinal) && d.Key != Key(today)).ToList();
        double total = MonthKwh(today);
        if (days.Count == 0) return total;
        double avg = days.Average(d => d.Value.Kwh);
        int remaining = DateTime.DaysInMonth(today.Year, today.Month) - today.Day;
        return total + avg * remaining;
    }

    /// <summary>直近 n 日（古い順）</summary>
    public List<(DateTime Date, DayEnergy Energy)> LastDays(int n)
        => Enumerable.Range(0, n).Select(i => DateTime.Today.AddDays(-(n - 1 - i))).Select(d => (d, Day(d))).ToList();
}
