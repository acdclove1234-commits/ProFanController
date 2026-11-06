// ==================================================
// CombinedSafetyTests.cs - 状態と異常が重なったときの安全のテスト (測定中に温度が読めなくなる・テストモード中のポンプ保護・
//                          緊急温度を超える急な上昇・低い回転の指示で止まったファン)
// Dependencies: .NET 8 (MIT License)
// See CREDITS.txt for full license details.
// ==================================================

using ProFanController;

namespace LogicTests;

public static class CombinedSafetyTests
{
    private const string Cpu = "/intelcpu/0/temperature/0";
    private const string Fan0 = "/lpc/nct6686d/0/control/0";
    private const string Rpm0 = "/lpc/nct6686d/0/fan/0";
    private const string Pump = "/lpc/nct6686d/0/control/1";
    private const string PumpRpm = "/lpc/nct6686d/0/fan/1";

    private static AppConfig Config(params FanConfig[] fans)
    {
        var cfg = new AppConfig();
        var p = new ProfileConfig { Name = "標準" };
        foreach (var f in fans) p.Fans.Add(f);
        cfg.Profiles.Add(p);
        cfg.Normalize();
        return cfg;
    }

    private static FanConfig CurveFan(double duty = 40) => new()
    {
        Name = "Front",
        ControlId = Fan0,
        RpmSensorId = Rpm0,
        MinDuty = 20,
        Curves =
        {
            new CurveConfig { Name = "CPU", SensorId = Cpu, Points = new() { new() { Temp = 30, Duty = duty }, new() { Temp = 110, Duty = duty } } },
        },
    };

    /// <summary>測定（キャリブレーション）中に温度だけ読めなくなったら、測定を中止して通常の制御（安全側）に戻す</summary>
    public static void TestCalibrationStopsWhenTemperatureLost()
    {
        var hw = new FakeHardware();
        var ctl = hw.AddControl(Fan0, 40);
        hw.Values[Cpu] = 50;
        hw.Values[Rpm0] = 1200;
        var rig = new Rig(Config(CurveFan()), hw);
        string? message = null;
        rig.E.CalibrationFinished += (_, r, m) => message = r is null ? m : "ok";
        rig.Tick(2);
        rig.E.StartCalibration(Fan0);
        rig.Tick(3);
        T.Check(rig.Fan(Fan0).State == FanState.Calibrating, "calibrating before the sensor is lost");
        hw.Values[Cpu] = null;
        rig.Tick(3);
        T.Check(rig.Fan(Fan0).State != FanState.Calibrating, $"calibration stops when the temperature cannot be read (state {rig.Fan(Fan0).State})");
        T.Check(message is not null && message.Contains("温度"), $"the reason mentions the temperature ({message})");
        T.Check(ctl.LastWrite >= 40, $"the fan is not left at a low calibration duty (last write {ctl.LastWrite:F0}%)");
    }

    /// <summary>テストモード中でも、ポンプの回転が落ちたら保護運転で 100% にする。そのことを知らせに書く</summary>
    public static void TestDryRunPumpProtectionIsAnnounced()
    {
        var hw = new FakeHardware();
        var pump = hw.AddControl(Pump, 60);
        hw.Values[Cpu] = 50;
        hw.Values[PumpRpm] = 2500;
        var cfg = Config(new FanConfig
        {
            Name = "Pump", ControlId = Pump, RpmSensorId = PumpRpm, Role = FanRoles.Pump,
            Mode = FanModes.Fixed, FixedDuty = 60, MinDuty = 20, PumpAlertRpm = 800,
        });
        cfg.DryRun = true;
        var rig = new Rig(cfg, hw);
        rig.Tick(15);
        T.Check(pump.Writes.Count == 0, "dry run: nothing is written while the pump is fine");
        hw.Values[PumpRpm] = 300;
        rig.Tick(4);
        T.Check(pump.LastWrite == 100, "dry run: pump protection still writes 100%");
        T.Check(rig.Alerts.Any(a => a.Level == AlertLevel.Danger && a.Message.Contains("テストモード")),
            "the alert says that protection wrote to the pump even in test mode");
    }

    /// <summary>緊急温度を一気に超える急上昇（65 → 105°C）でも、最初の周期で緊急全開にする</summary>
    public static void TestEmergencyOnSuddenJump()
    {
        var hw = new FakeHardware();
        var ctl = hw.AddControl(Fan0, 30);
        hw.Values[Cpu] = 65;
        hw.Values[Rpm0] = 1000;
        var cfg = Config(CurveFan(30));
        cfg.EmergencyTemperature = 100;
        var rig = new Rig(cfg, hw);
        rig.Tick(3);
        hw.Values[Cpu] = 105;
        rig.Tick();
        T.Check(rig.Last!.Emergency && ctl.LastWrite == 100, $"65 → 105°C: emergency on the first tick (last write {ctl.LastWrite:F0}%)");
        // ありえない値（127°C などの読み取りエラーの印）では緊急全開にしない
        var hw2 = new FakeHardware();
        hw2.AddControl(Fan0, 30);
        hw2.Values[Cpu] = 65;
        hw2.Values[Rpm0] = 1000;
        var rig2 = new Rig(cfg, hw2);
        rig2.Tick(3);
        hw2.Values[Cpu] = 127;
        rig2.Tick();
        T.Check(!rig2.Last!.Emergency, "sentinel 127°C does not trigger the emergency");
    }

    /// <summary>測ってあるファンに、回り続けるはずの回転（止まる回転より上）を指示しているのに 0 rpm なら、30% 未満でも止まっていると知らせる</summary>
    public static void TestStallBelow30PercentWhenCalibrated()
    {
        var hw = new FakeHardware();
        hw.AddControl(Fan0, 25);
        hw.Values[Cpu] = 50;
        hw.Values[Rpm0] = 0;
        var cfg = Config(new FanConfig { Name = "Front", ControlId = Fan0, RpmSensorId = Rpm0, Mode = FanModes.Fixed, FixedDuty = 25, MinDuty = 20 });
        cfg.Calibrations.Add(new CalibrationResult { ControlId = Fan0, MaxRpm = 1500, StartDuty = 18, StopDuty = 12 });
        var rig = new Rig(cfg, hw);
        rig.Tick(30);
        T.Check(rig.Fan(Fan0).State == FanState.Stall, $"25% (above the measured stop duty 12%) with 0 rpm → stall (state {rig.Fan(Fan0).State})");
        T.Check(rig.Alerted("回転していません", AlertLevel.Warning), "stall warning raised");

        // 測っていないファンは、これまでどおり 30% 以上のときだけ（低い回転で止まる仕様のファンもあるため）
        var hw2 = new FakeHardware();
        hw2.AddControl(Fan0, 25);
        hw2.Values[Cpu] = 50;
        hw2.Values[Rpm0] = 0;
        var cfg2 = Config(new FanConfig { Name = "Front", ControlId = Fan0, RpmSensorId = Rpm0, Mode = FanModes.Fixed, FixedDuty = 25, MinDuty = 20 });
        var rig2 = new Rig(cfg2, hw2);
        rig2.Tick(30);
        T.Check(rig2.Fan(Fan0).State != FanState.Stall, "uncalibrated fan at 25% with 0 rpm is not reported (may be a zero-rpm fan)");

        // 測ってあっても、止まる回転より下を指示しているとき（止める意図）は知らせない
        var hw3 = new FakeHardware();
        hw3.AddControl(Fan0, 10);
        hw3.Values[Cpu] = 50;
        hw3.Values[Rpm0] = 0;
        var cfg3 = Config(new FanConfig { Name = "Front", ControlId = Fan0, RpmSensorId = Rpm0, Mode = FanModes.Fixed, FixedDuty = 10, MinDuty = 0 });
        cfg3.Calibrations.Add(new CalibrationResult { ControlId = Fan0, MaxRpm = 1500, StartDuty = 18, StopDuty = 12 });
        var rig3 = new Rig(cfg3, hw3);
        rig3.Tick(30);
        T.Check(rig3.Fan(Fan0).State != FanState.Stall, "10% (below the stop duty, meant to stop) with 0 rpm is not a stall");
    }
}
