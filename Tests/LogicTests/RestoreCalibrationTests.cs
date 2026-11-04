// ==================================================
// RestoreCalibrationTests.cs - BIOS 復帰の確実化（戻す命令が黙って飛ばされても戻し直す）と、
//                              キャリブレーション中の安全対策（GPU 保護・ポンプ保護の優先、温度による中止）のテスト
// Dependencies: .NET 8 (MIT License)
// See CREDITS.txt for full license details.
// ==================================================

using ProFanController;

namespace LogicTests;

public static class RestoreCalibrationTests
{
    private const string Cpu = "/intelcpu/0/temperature/0";
    private const string Fan0 = "/lpc/nct6686d/0/control/0";
    private const string Rpm0 = "/lpc/nct6686d/0/fan/0";
    private const string Fan2 = "/lpc/nct6686d/0/control/2";
    private const string GpuFan = "/gpu-nvidia/0/control/0";
    private const string GpuRpm = "/gpu-nvidia/0/fan/0";

    private static AppConfig Config(params FanConfig[] fans)
    {
        var cfg = new AppConfig();
        var p = new ProfileConfig { Name = "標準" };
        foreach (var f in fans) p.Fans.Add(f);
        cfg.Profiles.Add(p);
        cfg.Normalize();
        return cfg;
    }

    private static FanConfig FixedFan(string id, double duty, string? rpm = null) => new()
    {
        Name = "Front",
        ControlId = id,
        RpmSensorId = rpm,
        Mode = FanModes.Fixed,
        FixedDuty = duty,
        MinDuty = 20,
    };

    private static FanConfig CurveFan(string id, string sensor, string? rpm = null) => new()
    {
        Name = "Curve",
        ControlId = id,
        RpmSensorId = rpm,
        MinDuty = 20,
        Curves =
        {
            new CurveConfig { Name = "T", SensorId = sensor, Points = new() { new() { Temp = 30, Duty = 30 }, new() { Temp = 110, Duty = 30 } } },
        },
    };

    // =====================================================================
    // BIOS 復帰（戻す命令が例外なしに飛ばされる場合）
    // =====================================================================

    /// <summary>一時停止で BIOS に戻すとき、1 回目の「戻す」が黙って飛ばされても、同じ周期のくり返しで本当に戻る</summary>
    public static void TestPauseRestoreSurvivesDroppedDefault()
    {
        var hw = new FakeHardware();
        var fan = hw.AddControl(Fan0, 40);
        var rig = new Rig(Config(FixedFan(Fan0, 25)), hw);
        rig.Tick(2);
        T.Check(!fan.ChipInBios && fan.Register != fan.BiosDuty, "fan under software control");

        fan.DropDefaultCount = 1;
        rig.E.SetPaused(true);
        rig.Tick();
        T.Check(fan.ChipInBios && fan.Register == fan.BiosDuty, "chip really back in BIOS although the first SetDefault was dropped");
        T.Check(rig.Fan(Fan0).State == FanState.Paused, "state paused");
    }

    /// <summary>その周期の切り替えが全部飛ばされても、次の周期以降の戻し直しで BIOS に戻る</summary>
    public static void TestPendingRestoreRepeatsOnLaterTicks()
    {
        var hw = new FakeHardware();
        var fan = hw.AddControl(Fan0, 40);
        var rig = new Rig(Config(FixedFan(Fan0, 25)), hw);
        rig.Tick(2);
        fan.DropDefaultCount = FanSafety.RestoreAttempts;
        rig.E.SetPaused(true);
        rig.Tick();
        T.Check(!fan.ChipInBios, "all restores in the pausing tick were dropped (chip still manual)");
        rig.Tick();
        T.Check(fan.ChipInBios && fan.Register == fan.BiosDuty, "re-restored on the next tick");
        int writes = fan.Writes.Count;
        rig.Tick(5);
        T.Check(fan.Writes.Count <= writes + FanSafety.RestoreRepeatTicks && fan.ChipInBios, "pending restores stop after a few ticks and leave BIOS control");
    }

    /// <summary>戻し直しの途中で制御を再開したら、戻し直しはやめて通常の書き込みを続ける</summary>
    public static void TestPendingRestoreCancelledWhenControlResumes()
    {
        var hw = new FakeHardware();
        var fan = hw.AddControl(Fan0, 40);
        var rig = new Rig(Config(FixedFan(Fan0, 50)), hw);
        rig.Tick(2);
        rig.E.SetPaused(true);
        rig.Tick();
        T.Check(fan.ChipInBios, "paused → BIOS");
        rig.E.SetPaused(false);
        rig.Tick(3);
        T.Check(!fan.InBios && !fan.ChipInBios && Math.Abs(fan.Register - 50) < 1, "control resumed and not restored again");
        T.Check(rig.Fan(Fan0).State == FanState.Active, "state active");
    }

    /// <summary>終了時の RestoreAll も、例外なしに飛ばされた「戻す」をくり返しで補う</summary>
    public static void TestRestoreAllSurvivesDroppedDefaults()
    {
        var hw = new FakeHardware();
        var fan = hw.AddControl(Fan0, 40);
        var other = hw.AddControl(Fan2, 40);
        var rig = new Rig(Config(FixedFan(Fan0, 25), FixedFan(Fan2, 30)), hw);
        rig.Tick(2);
        fan.DropDefaultCount = 2;
        other.DropDefaultCount = 1;
        T.Check(rig.E.RestoreAllForTest(), "RestoreAll reports success");
        T.Check(fan.ChipInBios && other.ChipInBios && fan.Register == fan.BiosDuty, "both chips really in BIOS");

        // 最後の「戻す」が例外になったら失敗として知らせる（見張りに「正常終了」と伝えない）
        rig.Tick(2);
        fan.ThrowOnDefaultCount = FanSafety.RestoreAttempts;
        T.Check(!rig.E.RestoreAllForTest(), "restore reports failure when every attempt throws");
    }

    // =====================================================================
    // キャリブレーションの安全
    // =====================================================================

    /// <summary>GPU のファンをキャリブレーション中に GPU が熱くなったら、GPU 保護が勝って全開になり、キャリブレーションは中止される</summary>
    public static void TestGpuGuardOverridesCalibration()
    {
        var hw = new FakeHardware();
        var ctl = hw.AddControl(GpuFan, 40);
        hw.Values[GpuRpm] = 1500;
        hw.GpuCore = 50;
        var cfg = Config(FixedFan(GpuFan, 50, GpuRpm));
        cfg.Safety.AllowGpuFanControl = true;
        var rig = new Rig(cfg, hw);
        string? msg = null;
        rig.E.CalibrationFinished += (_, r, m) => msg = r is null ? m : "ok";
        rig.Tick();
        rig.E.StartCalibration(GpuFan);
        rig.Tick(2);
        T.Check(rig.Fan(GpuFan).State == FanState.Calibrating, "GPU fan calibrating");

        // GPU 保護の温度（85°C）まで上がる。80°C を超えた時点で温度による中止も働く
        hw.GpuCore = 90;
        rig.Tick(3);
        T.Check(msg is not null && msg != "ok", $"calibration aborted ({msg})");
        T.Check(rig.Fan(GpuFan).State == FanState.GpuGuard && ctl.LastWrite == 100, "GPU guard wins: 100%");
    }

    /// <summary>GPU 保護が働いた周期から、キャリブレーションの Duty より GPU 保護を優先する（中止を待たない）</summary>
    public static void TestGpuGuardWinsWithinTheSameTick()
    {
        var hw = new FakeHardware();
        var ctl = hw.AddControl(GpuFan, 40);
        hw.Values[GpuRpm] = 1500;
        hw.GpuCore = 50;
        var cfg = Config(FixedFan(GpuFan, 50, GpuRpm));
        cfg.Safety.AllowGpuFanControl = true;
        var rig = new Rig(cfg, hw);
        string? msg = null;
        rig.E.CalibrationFinished += (_, r, m) => msg = r is null ? m : "ok";
        rig.Tick();
        rig.E.StartCalibration(GpuFan);
        rig.Tick(2);
        // ホットスポットだけ熱い（コアは 80°C 未満なので温度による中止は働かない）
        hw.GpuHotSpot = 105;
        rig.Tick(2);
        T.Check(rig.Fan(GpuFan).State == FanState.GpuGuard && ctl.LastWrite == 100, "guard engaged on the 2nd hot reading → 100% immediately");
        rig.Tick();
        T.Check(msg is not null && msg.Contains("GPU"), $"calibration aborted by the GPU guard ({msg})");
    }

    /// <summary>目標温度モード（カーブなし）のファンも、目標センサーが熱ければキャリブレーションを中止する</summary>
    public static void TestTargetModeCalibrationAbortsWhenHot()
    {
        var hw = new FakeHardware();
        hw.AddControl(Fan0, 40);
        hw.Values[Rpm0] = 1500;
        hw.Values[Cpu] = 50;
        var f = new FanConfig { Name = "Target", ControlId = Fan0, RpmSensorId = Rpm0, Mode = FanModes.Target, TargetSensorId = Cpu, MinDuty = 20 };
        var cfg = Config(f);
        cfg.EmergencyTemperature = 95;
        var rig = new Rig(cfg, hw);
        string? msg = null;
        rig.E.CalibrationFinished += (_, r, m) => msg = r is null ? m : "ok";
        rig.Tick();
        rig.E.StartCalibration(Fan0);
        rig.Tick(2);
        T.Check(rig.Fan(Fan0).State == FanState.Calibrating && msg is null, "calibrating at 50°C");
        hw.Values[Cpu] = 85;
        rig.Tick(2);
        T.Check(msg is not null && msg.Contains("温度"), $"aborted on target sensor temperature ({msg})");
        T.Check(rig.Fan(Fan0).State != FanState.Calibrating, "normal control resumed");
    }

    /// <summary>固定モード（自分の温度センサーなし）のファンは、緊急全開の判定に使うセンサーで中止する</summary>
    public static void TestFixedModeCalibrationAbortsOnSystemTemps()
    {
        var hw = new FakeHardware();
        hw.AddControl(Fan0, 40);
        hw.AddControl(Fan2, 40);
        hw.Values[Rpm0] = 1500;
        hw.Values[Cpu] = 50;
        var cfg = Config(FixedFan(Fan0, 50, Rpm0), CurveFan(Fan2, Cpu));
        cfg.EmergencyTemperature = 95;
        var rig = new Rig(cfg, hw);
        string? msg = null;
        rig.E.CalibrationFinished += (_, r, m) => msg = r is null ? m : "ok";
        rig.Tick();
        rig.E.StartCalibration(Fan0);
        rig.Tick(2);
        T.Check(rig.Fan(Fan0).State == FanState.Calibrating, "fixed fan calibrating");
        hw.Values[Cpu] = 85; // 緊急温度（95°C）未満
        rig.Tick(2);
        T.Check(!rig.Last!.Emergency, "no emergency");
        T.Check(msg is not null && msg.Contains("温度"), $"aborted on system temperature ({msg})");
    }
}
