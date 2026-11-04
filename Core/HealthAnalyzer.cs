// ==================================================
// HealthAnalyzer.cs - 長期の記録から、ファンの劣化（同じ Duty で回転数が下がる）と
//                     ホコリ（同じ電力で温度が上がる）を見つける
// Dependencies: .NET 8 (MIT License)
// See CREDITS.txt for full license details.
// ==================================================

namespace ProFanController;

public enum HealthLevel { Collecting, Good, Info, Warning, Danger }

public sealed class HealthItem
{
    public required string Title { get; init; }
    public required HealthLevel Level { get; init; }
    public required string Detail { get; init; }
    /// <summary>主な数値（例: 「60% 時 1,180 rpm（基準 1,250 rpm）」）</summary>
    public string Metric { get; init; } = "";
    /// <summary>変化率（−0.08 = 8% 低下）。表示用</summary>
    public double? Change { get; init; }
}

/// <summary>
/// 比べ方:
///   ・基準 … 「掃除した」日以降（なければ記録の最初）の 7 日間。キャリブレーション済みのファンは、その測定値も基準に使える
///   ・最近 … 直近 7 日間
/// 同じ条件（同じ Duty・同じくらいの消費電力）の時間どうしを比べるので、使い方が変わっても判定がぶれにくい。
/// </summary>
public static class HealthAnalyzer
{
    public static readonly TimeSpan Window = TimeSpan.FromDays(7);
    /// <summary>判定に必要な、記録の最低日数</summary>
    public const int MinDays = 10;
    /// <summary>1 つの条件（Duty の区切り）で比べるのに必要な分数</summary>
    private const int MinSamples = 20;

    public static List<HealthItem> Analyze(IReadOnlyList<MinuteRow> rows, AppConfig cfg, DateTime now)
    {
        var list = new List<HealthItem>();
        var since = cfg.Maintenance.LastCleaned ?? DateTime.MinValue;
        var data = rows.Where(r => r.Time >= since).ToList();
        double days = data.Count == 0 ? 0 : (now - data[0].Time).TotalDays;

        var recentFrom = now - Window;
        var baseRows = data.Where(r => r.Time < (data.Count > 0 ? data[0].Time : now) + Window).ToList();
        var recentRows = data.Where(r => r.Time >= recentFrom).ToList();
        bool enough = days >= MinDays;

        // --- ファン・ポンプ（同じ Duty での回転数）
        foreach (var fan in cfg.Profiles.SelectMany(p => p.Fans).GroupBy(f => f.ControlId, StringComparer.OrdinalIgnoreCase).Select(g => g.First()))
        {
            string rpmKey = HistoryStore.RpmKey(fan.ControlId), dutyKey = HistoryStore.DutyKey(fan.ControlId);
            var cal = cfg.FindCalibration(fan.ControlId);
            bool calBase = cal is not null && cal.Points.Count >= 3 && now - cal.MeasuredAt >= Window && cal.MeasuredAt >= since;
            if (!enough && !calBase)
            {
                list.Add(new HealthItem
                {
                    Title = fan.Name,
                    Level = HealthLevel.Collecting,
                    Detail = $"データを集めています（比べられるまで あと {Math.Max(1, Math.Ceiling(MinDays - days)):F0} 日）。キャリブレーション済みなら、その 7 日後から判定できます。",
                });
                continue;
            }

            var recent = Buckets(recentRows, dutyKey, rpmKey);
            Dictionary<int, double> baseline;
            string baseText;
            if (calBase)
            {
                baseline = recent.Keys.ToDictionary(k => k, k => cal!.EstimateRpm(k) ?? double.NaN);
                baseText = $"{cal!.MeasuredAt:M/d} のキャリブレーション";
            }
            else
            {
                baseline = Buckets(baseRows, dutyKey, rpmKey).ToDictionary(b => b.Key, b => Median(b.Value));
                baseText = $"{baseRows.FirstOrDefault()?.Time:M/d} ごろ";
            }

            var compare = recent.Where(b => b.Value.Count >= MinSamples && baseline.TryGetValue(b.Key, out var bv) && bv > 100)
                .Select(b => (Duty: b.Key, Recent: Median(b.Value), Base: baseline[b.Key], Weight: b.Value.Count))
                .ToList();
            if (compare.Count == 0)
            {
                list.Add(new HealthItem
                {
                    Title = fan.Name,
                    Level = HealthLevel.Collecting,
                    Detail = "同じ回転数（Duty）で回っていた時間がまだ少ないため、比べられません。",
                });
                continue;
            }

            double change = compare.Sum(c => (c.Recent - c.Base) / c.Base * c.Weight) / compare.Sum(c => c.Weight);
            var main = compare.OrderByDescending(c => c.Weight).First();
            string metric = $"{main.Duty}% 時 {main.Recent:N0} rpm（{baseText} {main.Base:N0} rpm）";
            var (level, detail) = change switch
            {
                <= -0.20 => (HealthLevel.Danger, fan.IsPump
                    ? "ポンプの回転数が大きく下がっています。ポンプの劣化や、冷却液の減少が考えられます。早めに点検してください。"
                    : "回転数が大きく下がっています。軸受けの劣化か、羽根やフィルターにホコリが溜まっている可能性があります。掃除しても戻らなければ交換を検討してください。"),
                <= -0.10 => (HealthLevel.Warning, fan.IsPump
                    ? "ポンプの回転数が下がってきています。しばらく様子を見て、さらに下がるようなら点検してください。"
                    : "回転数が少し下がってきています。ファンの羽根やフィルターのホコリを掃除してみてください。"),
                >= 0.15 => (HealthLevel.Info, "以前より速く回っています。ファンを交換した・つなぎ替えた場合は問題ありません。"),
                _ => (HealthLevel.Good, "回転数は以前と変わらず、正常です。"),
            };
            list.Add(new HealthItem { Title = fan.Name, Level = level, Detail = detail, Metric = metric, Change = change });
        }

        // --- 冷え方（同じ消費電力での温度上昇＝熱抵抗）
        AddCooling(list, "CPU の冷え方", StatRoles.CpuTemp, StatRoles.CpuPower, 30, data, baseRows, recentRows, enough, days);
        AddCooling(list, "GPU の冷え方", StatRoles.GpuTemp, StatRoles.GpuPower, 60, data, baseRows, recentRows, enough, days);
        return list;
    }

    /// <summary>Duty を 5% 単位に丸めて、回転が安定していた分の回転数を集める</summary>
    private static Dictionary<int, List<double>> Buckets(IEnumerable<MinuteRow> rows, string dutyKey, string rpmKey)
    {
        var map = new Dictionary<int, List<double>>();
        foreach (var r in rows)
        {
            if (r.Get(dutyKey) is not float duty || r.Get(rpmKey) is not float rpm || rpm < 100) continue;
            if (r.DutySpread.TryGetValue(dutyKey, out var spread) && spread > 3) continue;
            int bucket = (int)(Math.Round(duty / 5) * 5);
            if (!map.TryGetValue(bucket, out var list)) map[bucket] = list = new();
            list.Add(rpm);
        }
        return map;
    }

    /// <summary>
    /// 熱抵抗 = (温度 − 室温の代わり) ÷ 消費電力。値が大きいほど冷えにくい。
    /// 消費電力を区切って、同じくらいの電力の時間どうしを比べる
    /// </summary>
    private static void AddCooling(List<HealthItem> list, string title, string tempRole, string powerRole, double minPower,
        List<MinuteRow> data, List<MinuteRow> baseRows, List<MinuteRow> recentRows, bool enough, double days)
    {
        if (data.Count > 0 && !data.Any(r => r.Role(tempRole) is not null && r.Role(powerRole) is not null)) return;
        if (!enough)
        {
            list.Add(new HealthItem { Title = title, Level = HealthLevel.Collecting, Detail = $"データを集めています（あと {Math.Max(1, Math.Ceiling(MinDays - days)):F0} 日）。" });
            return;
        }

        Dictionary<int, List<double>> Resistance(IEnumerable<MinuteRow> rows)
        {
            var map = new Dictionary<int, List<double>>();
            foreach (var r in rows)
            {
                if (r.Role(tempRole) is not float t || r.Role(powerRole) is not float p || p < minPower) continue;
                double ambient = r.Role(StatRoles.Ambient) is float a and > 10 and < 60 ? a : 25;
                if (t <= ambient) continue;
                int bucket = (int)(Math.Round(p / 20) * 20);
                if (!map.TryGetValue(bucket, out var l)) map[bucket] = l = new();
                l.Add((t - ambient) / p);
            }
            return map;
        }

        var b = Resistance(baseRows);
        var r = Resistance(recentRows);
        var compare = r.Where(x => x.Value.Count >= MinSamples && b.TryGetValue(x.Key, out var bl) && bl.Count >= MinSamples)
            .Select(x => (Power: x.Key, Recent: Median(x.Value), Base: Median(b[x.Key]), Weight: x.Value.Count))
            .ToList();
        if (compare.Count == 0)
        {
            list.Add(new HealthItem { Title = title, Level = HealthLevel.Collecting, Detail = "負荷をかけた時間（ゲームなど）がまだ少ないため、比べられません。" });
            return;
        }
        double change = compare.Sum(c => (c.Recent - c.Base) / c.Base * c.Weight) / compare.Sum(c => c.Weight);
        var main = compare.OrderByDescending(c => c.Weight).First();
        // 温度の上がり幅に直す（例: 100W で 0.40°C/W → 40°C 上昇）
        string metric = $"{main.Power} W 時の温度上昇 {main.Recent * main.Power:F0}°C（以前 {main.Base * main.Power:F0}°C）";
        var (level, detail) = change switch
        {
            >= 0.25 => (HealthLevel.Warning, "以前より明らかに冷えにくくなっています。フィルター・ラジエーター・ヒートシンクのホコリを掃除してください。改善しなければグリスの劣化も考えられます。"),
            >= 0.12 => (HealthLevel.Info, "以前より少し冷えにくくなっています。そろそろフィルターやラジエーターの掃除をおすすめします。夏で室温が高い場合もこうなります。"),
            _ => (HealthLevel.Good, "以前と同じように冷えています。"),
        };
        list.Add(new HealthItem { Title = title, Level = level, Detail = detail, Metric = metric, Change = change });
    }

    private static double Median(List<double> v)
    {
        if (v.Count == 0) return double.NaN;
        var s = v.OrderBy(x => x).ToList();
        return s.Count % 2 == 1 ? s[s.Count / 2] : (s[s.Count / 2 - 1] + s[s.Count / 2]) / 2;
    }
}
