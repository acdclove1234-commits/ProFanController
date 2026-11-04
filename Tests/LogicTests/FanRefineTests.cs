// ==================================================
// FanRefineTests.cs - ファン制御の精度のテスト (一瞬の跳ねの除去・更新間隔によらないならし方と上げ下げの速さ)
// Dependencies: .NET 8 (MIT License)
// See CREDITS.txt for full license details.
// ==================================================

using ProFanController;

namespace LogicTests;

public static class FanRefineTests
{
    private const string Cpu = "/intelcpu/0/temperature/0";
    private const string Fan0 = "/lpc/nct6686d/0/control/0";

    private static CurveConfig Curve(double alpha = 1, double hysteresis = 4) => new()
    {
        Name = "CPU", SensorId = Cpu, EmaAlpha = alpha, HysteresisCelsius = hysteresis,
        Points = new() { new() { Temp = 30, Duty = 20 }, new() { Temp = 80, Duty = 100 } },
    };

    public static void TestSpikeIsIgnored()
    {
        var ev = new CurveEvaluator(Curve());
        for (int i = 0; i < 5; i++) ev.Evaluate(50);
        double steady = ev.Evaluate(50);
        double spike = ev.Evaluate(85);          // 1 回だけ跳ねた
        double after = ev.Evaluate(50);
        T.Near(spike, steady, 1e-9, "a one-sample spike does not raise the fan");
        T.Near(after, steady, 1e-9, "and nothing is left behind by the hysteresis");
        T.Near(ev.EffectiveTemp ?? -1, 50, 1e-9, "effective temperature stays at 50°C");

        // 続いて上がったときは、1 周期遅れで追従する
        ev.Evaluate(70);
        double second = ev.Evaluate(70);
        T.Near(second, CurveMath.Interpolate(new CurvePoint[] { new() { Temp = 30, Duty = 20 }, new() { Temp = 80, Duty = 100 } }, 70), 1e-9,
            "a sustained rise is followed on the 2nd sample");
        // 下がる向きの一瞬の読み違いも無視する
        ev.Evaluate(70);
        double dip = ev.Evaluate(20);
        T.Near(dip, second, 1e-9, "a one-sample dip is ignored too");
    }

    public static void TestFirstSamplesAreNotDelayed()
    {
        var ev = new CurveEvaluator(Curve());
        double first = ev.Evaluate(80);
        T.Near(first, 100, 1e-9, "the very first reading is used as is (no start-up delay)");
    }

    public static void TestSmoothingDoesNotDependOnInterval()
    {
        // 1 秒ごと 10 回と、0.25 秒ごと 40 回で、同じ時間に同じだけ追いつく
        // （跳ねの除去は 1 回分遅れるので、その差 1 秒と 0.25 秒が残る程度）
        var slow = new CurveEvaluator(Curve(alpha: 0.2, hysteresis: 0));
        var fast = new CurveEvaluator(Curve(alpha: 0.2, hysteresis: 0));
        for (int i = 0; i < 3; i++) { slow.Evaluate(40); fast.Evaluate(40, 0.25); }
        for (int i = 0; i < 10; i++) slow.Evaluate(60, 1.0);
        for (int i = 0; i < 40; i++) fast.Evaluate(60, 0.25);
        T.Info($"after 10 s: 1 s ticks {slow.EffectiveTemp:F2}°C, 0.25 s ticks {fast.EffectiveTemp:F2}°C");
        T.Near(fast.EffectiveTemp ?? 0, slow.EffectiveTemp ?? 0, 1.0, "same response time at 250 ms and 1000 ms intervals");
        T.Near(CurveMath.AlphaFor(0.2, 1), 0.2, 1e-12, "1 s step uses the configured value");
        T.Near(CurveMath.AlphaFor(1, 0.25), 1, 1e-12, "1.0 means no smoothing at any interval");
        double half = CurveMath.AlphaFor(0.2, 0.5);
        T.Near(1 - (1 - half) * (1 - half), 0.2, 1e-12, "two half-second steps equal one second");
    }

    public static void TestRampSpeedIsPerSecond()
    {
        var hw = new FakeHardware();
        hw.AddControl(Fan0, 20);
        hw.Values[Cpu] = 30;
        var fan = new FanConfig { Name = "Front", ControlId = Fan0, MinDuty = 20, MaxStepUpPerTick = 4, Curves = { Curve() } };
        var cfg = new AppConfig();
        var p = new ProfileConfig { Name = "標準" };
        p.Fans.Add(fan);
        cfg.Profiles.Add(p);
        cfg.PollingIntervalMs = 500;
        cfg.Normalize();
        var rig = new Rig(cfg, hw);
        void Step() { rig.E.StepForTest(); rig.Now += 500; }
        for (int i = 0; i < 3; i++) Step();
        hw.Values[Cpu] = 80;
        Step(); Step();                          // 跳ねの除去で 1 周期遅れてから上がり始める
        double start = rig.Fan(Fan0).Duty ?? 0;
        for (int i = 0; i < 4; i++) Step();     // 2 秒
        double end = rig.Fan(Fan0).Duty ?? 0;
        T.Info($"500 ms interval: {start:F1}% → {end:F1}% in 2 s");
        T.Near(end - start, 8, 1.01, "4 %/s at a 500 ms interval rises about 8% in 2 seconds (not 16%)");
    }
}
