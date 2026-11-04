// ==================================================
// CurveEvaluator.cs - 温度→Duty 変換 (EMA平滑化 + ヒステリシス + 線形補間)
// Dependencies: .NET 8 (MIT License)
// See CREDITS.txt for full license details.
// ==================================================

namespace ProFanController;

public static class CurveMath
{
    /// <summary>温度順に並んだ点列を線形補間する。範囲外は端の値で頭打ち</summary>
    public static double Interpolate(IReadOnlyList<CurvePoint> sorted, double t)
    {
        if (sorted.Count == 0) return 100.0;
        if (t <= sorted[0].Temp) return sorted[0].Duty;
        if (t >= sorted[^1].Temp) return sorted[^1].Duty;

        for (int i = 0; i < sorted.Count - 1; i++)
        {
            var a = sorted[i];
            var b = sorted[i + 1];
            if (t <= b.Temp)
            {
                double range = b.Temp - a.Temp;
                if (range <= 0.001) return b.Duty;
                return a.Duty + (t - a.Temp) / range * (b.Duty - a.Duty);
            }
        }
        return 100.0;
    }
}

public sealed class CurveEvaluator
{
    private readonly CurvePoint[] _points;
    private double? _ema;
    private double? _effective;

    public CurveConfig Config { get; }
    public double? EffectiveTemp => _effective;

    public CurveEvaluator(CurveConfig cfg)
    {
        Config = cfg;
        _points = cfg.Points.OrderBy(p => p.Temp).ToArray();
    }

    /// <summary>設定変更で作り直したときに、平滑化の途中状態を引き継いで回転が跳ねないようにする</summary>
    public void CopyStateFrom(CurveEvaluator old)
    {
        if (old.Config.SensorId != Config.SensorId) return;
        _ema = old._ema;
        _effective = old._effective;
    }

    /// <summary>直近の生温度を受け取り、要求 Duty(%) を返す</summary>
    public double Evaluate(double rawTemp)
    {
        // 1. EMA でセンサーのノイズや瞬間的なスパイクをならす
        double alpha = Config.EmaAlpha;
        _ema = _ema is null ? rawTemp : alpha * rawTemp + (1 - alpha) * _ema.Value;

        // 2. ヒステリシス: 上昇には即追従、下降は HysteresisCelsius 分だけ遅れて追従
        //    （effective は常に [ema, ema + hysteresis] の範囲に収まる）
        double ema = _ema.Value;
        _effective = Math.Clamp(_effective ?? ema, ema, ema + Config.HysteresisCelsius);

        return CurveMath.Interpolate(_points, _effective.Value);
    }
}
