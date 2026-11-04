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
        return new ProfileConfig { Name = name, Fans = fans };
    }
}
