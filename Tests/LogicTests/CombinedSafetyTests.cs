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

    /// <summary>
    /// カーブで水温だけを使っていても、CPU の温度と GPU のコア・Hot Spot は緊急全開で見る
    /// （ポンプの不調などで CPU だけが熱くなったときに、水温の低い回転のままにしない）
    /// </summary>
    public static void TestEmergencyWatchesCpuAndGpuWithoutCurves()
    {
        const string Water = "/lpc/nct6686d/0/temperature/5";
        const string CpuPkg = "/intelcpu/0/temperature/16";
        const string GpuCore = "/gpu-nvidia/0/temperature/0";
        const string GpuHot = "/gpu-nvidia/0/temperature/2";
        FakeHardware Hw()
        {
            var hw = new FakeHardware();
            hw.AddControl(Fan0, 30);
            hw.Values[Water] = 32;
            hw.Values[CpuPkg] = 60;
            hw.Values[GpuCore] = 60;
            hw.Values[GpuHot] = 70;
            hw.Values[Rpm0] = 900;
            hw.Temps.Add(new SensorInfo(Water, "Liquid", "Nuvoton NCT6686D", "SuperIO", "Temperature"));
            hw.Temps.Add(new SensorInfo("/intelcpu/0/temperature/0", "CPU Core #1", "Intel Core Ultra", "Cpu", "Temperature"));
            hw.Temps.Add(new SensorInfo(CpuPkg, "CPU Package", "Intel Core Ultra", "Cpu", "Temperature"));
            hw.Temps.Add(new SensorInfo(GpuCore, "GPU Core", "NVIDIA GeForce RTX", "GpuNvidia", "Temperature"));
            hw.Temps.Add(new SensorInfo(GpuHot, "GPU Hot Spot", "NVIDIA GeForce RTX", "GpuNvidia", "Temperature"));
            return hw;
        }
        AppConfig Cfg()
        {
            var cfg = Config(new FanConfig
            {
                Name = "Radiator", ControlId = Fan0, RpmSensorId = Rpm0, MinDuty = 20,
                Curves = { new CurveConfig { Name = "水温", SensorId = Water, Points = new() { new() { Temp = 25, Duty = 30 }, new() { Temp = 45, Duty = 80 } } } },
            });
            cfg.EmergencyTemperature = 100;
            cfg.EmergencyHotspotTemperature = 108;
            return cfg;
        }

        var ids = FanEngine.AlwaysWatchedTemps(Hw().Temps).ToList();
        T.Check(ids.SequenceEqual(new[] { CpuPkg, GpuCore, GpuHot }), $"watched: CPU package, GPU core, GPU hot spot ({string.Join(", ", ids)})");

        // CPU だけが 105°C（水温は 32°C のまま）
        var hw = Hw();
        var rig = new Rig(Cfg(), hw);
        rig.Tick(3);
        T.Check(!rig.Last!.Emergency, "normal temperatures: no emergency");
        hw.Values[CpuPkg] = 105;
        rig.Tick();
        T.Check(rig.Last!.Emergency && hw.Controls[Fan0].LastWrite == 100, $"CPU 105°C with only a coolant curve → full speed (last write {hw.Controls[Fan0].LastWrite:F0}%)");

        // GPU の Hot Spot は Hot Spot のしきい値（108°C）で見る
        var hw2 = Hw();
        var rig2 = new Rig(Cfg(), hw2);
        rig2.Tick(3);
        hw2.Values[GpuHot] = 104;
        rig2.Tick(2);
        T.Check(!rig2.Last!.Emergency, "GPU hot spot 104°C is under its own limit (108°C)");
        hw2.Values[GpuHot] = 110;
        rig2.Tick();
        T.Check(rig2.Last!.Emergency, "GPU hot spot 110°C → emergency");

        // 温度の一覧がない（部品の名前が分からない）ときは、これまでどおりカーブのセンサーだけ
        T.Check(!FanEngine.AlwaysWatchedTemps(Array.Empty<SensorInfo>()).Any(), "no catalog → nothing extra");
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
