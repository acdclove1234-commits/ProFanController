// ==================================================
// CurvePresets.cs - カーブのプリセット (静音/バランス/冷却重視/全開) とプロファイルの自動生成
// Dependencies: .NET 8 (MIT License)
// See CREDITS.txt for full license details.
// ==================================================

using System.Collections.ObjectModel;

namespace ProFanController;

/// <summary>センサーの種類によって「適温」が違うため、プリセットの温度をずらす</summary>
public enum SensorKind { Cpu, Gpu, GpuHotspot, Delta, Other }

public static class CurvePresets
{
    public const string Silent = "静音";
    public const string Balanced = "バランス";
    public const string Performance = "冷却重視";
    public const string Full = "全開";

    public static readonly string[] All = { Silent, Balanced, Performance, Full };

    public static SensorKind Classify(string sensorId, string sensorName)
    {
        if (sensorId.StartsWith(VirtualSensorConfig.IdPrefix)) return SensorKind.Other;
        if (sensorName.Contains("Hot Spot", StringComparison.OrdinalIgnoreCase)) return SensorKind.GpuHotspot;
        if (sensorId.StartsWith("/gpu")) return SensorKind.Gpu;
        if (sensorId.Contains("cpu")) return SensorKind.Cpu;
        return SensorKind.Other;
    }

    public static List<CurvePoint> Create(string preset, SensorKind kind)
    {
        if (kind == SensorKind.Delta)
        {
            return preset switch
            {
                Silent => P((3, 25), (6, 35), (9, 55), (14, 100)),
                Performance => P((1, 35), (4, 55), (7, 80), (10, 100)),
                Full => P((0, 100), (30, 100)),
                _ => P((2, 30), (5, 45), (8, 70), (12, 100)),
            };
        }

        // CPU/GPU 基準の点列。Hot Spot は Edge より 10〜15°C 高く出るのでずらす
        double shift = kind == SensorKind.GpuHotspot ? 12 : 0;
        var pts = preset switch
        {
            Silent => P((45, 25), (65, 35), (78, 55), (90, 100)),
            Performance => P((35, 40), (55, 60), (70, 85), (80, 100)),
            Full => P((20, 100), (100, 100)),
            _ => P((40, 30), (60, 45), (75, 70), (88, 100)),
        };
        if (preset != Full)
        {
            foreach (var p in pts) p.Temp = Math.Min(p.Temp + shift, 110);
        }
        return pts;
    }

    private static List<CurvePoint> P(params (double T, double D)[] pts)
        => pts.Select(p => new CurvePoint { Temp = p.T, Duty = p.D }).ToList();

    /// <summary>既存プロファイルのカーブを、静音寄り／冷却寄りに変形したコピーを作る</summary>
    public static ProfileConfig DeriveProfile(ProfileConfig source, string name, string direction)
    {
        var fans = new ObservableCollection<FanConfig>();
        foreach (var src in source.Fans)
        {
            var fan = src.DeepCopy();
            if (!fan.IsPump)
            {
                foreach (var p in fan.Curves.SelectMany(c => c.Points))
                {
                    if (direction == Silent)
                    {
                        p.Temp += 4;
                        p.Duty = Math.Max(fan.MinDuty, Math.Round(p.Duty * 0.85));
                    }
                    else
                    {
                        p.Temp -= 4;
                        p.Duty = Math.Min(100, p.Duty + 15);
                    }
                }
                if (direction == Silent) fan.MaxStepUpPerTick = Math.Max(2, fan.MaxStepUpPerTick * 0.6);
            }
            fans.Add(fan);
        }
        return new ProfileConfig { Name = name, Role = ProfileRoles.FromPreset(direction), Fans = fans };
    }
}

/// <summary>
/// プロファイルの役割（名前ではなくこれで判断する。名前を変えても、おまかせ調整の目標温度・新しいファンのカーブ・「静音」の一時使用が正しく動くように）
/// </summary>
public static class ProfileRoles
{
    public const string Silent = "Silent";
    public const string Balanced = "Balanced";
    public const string Performance = "Performance";
    public const string Custom = "Custom";
    public static readonly string[] All = { Silent, Balanced, Performance, Custom };

    public static string Label(string id) => id switch
    {
        Silent => L("静音"),
        Balanced => L("標準"),
        Performance => L("冷却重視"),
        _ => L("その他"),
    };

    /// <summary>役割がまだないプロファイル（前の版の設定）は、名前から決める（1 回だけ）</summary>
    public static string FromName(string? name)
    {
        string n = name ?? "";
        if (n.Contains("静音") || n.Contains("Silent", StringComparison.OrdinalIgnoreCase) || n.Contains("夜")) return Silent;
        if (n.Contains("パフォーマンス") || n.Contains("冷却") || n.Contains("Performance", StringComparison.OrdinalIgnoreCase) || n.Contains("ゲーム")) return Performance;
        if (n.Contains("標準") || n.Contains("バランス") || n.Contains("Balanced", StringComparison.OrdinalIgnoreCase)) return Balanced;
        return Custom;
    }

    public static string Normalize(string? id) => id is not null && All.Contains(id) ? id : Custom;

    /// <summary>役割 → カーブのプリセット（新しいファンを足すとき）</summary>
    public static string Preset(string role) => role switch
    {
        Silent => CurvePresets.Silent,
        Performance => CurvePresets.Performance,
        _ => CurvePresets.Balanced,
    };

    /// <summary>役割 → おまかせ調整の目標温度のずらし（°C。静音は高め、冷却重視は低め）</summary>
    public static double TargetOffset(string role) => role switch
    {
        Silent => 5,
        Performance => -5,
        _ => 0,
    };

    /// <summary>カーブのプリセット → 役割（プロファイルを作るとき）</summary>
    public static string FromPreset(string preset) => preset switch
    {
        CurvePresets.Silent => Silent,
        CurvePresets.Performance or CurvePresets.Full => Performance,
        CurvePresets.Balanced => Balanced,
        _ => Custom,
    };
}
