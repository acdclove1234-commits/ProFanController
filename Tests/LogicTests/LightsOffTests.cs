// ==================================================
// LightsOffTests.cs - 消灯（制御は続けたまま黒にする）のテスト
// Dependencies: .NET 8 (MIT License)
// See CREDITS.txt for full license details.
// ==================================================

using ProFanController;

namespace LogicTests;

public static class LightsOffTests
{
    public static void TestLightsOffFadesToBlackAndBack()
    {
        var dev = new RgbDevice(0, "Strip", 10, 4, null, "HID: s");
        var r = new DeviceRenderer(dev);
        var cfg = new RgbConfig { Mode = RgbModes.Static, StaticColor = "#FF0000", Brightness = 100, Gamma = false };
        var input = new RenderInput { Cfg = cfg, Device = new RgbDeviceSettings { Name = "Strip" } };
        var output = new Rgb[10];
        double t = 1;
        void Step(int frames)
        {
            for (int i = 0; i < frames; i++, t += 1 / 60.0)
            {
                input.T = input.TEval = t;
                input.Dt = 1 / 60.0;
                r.Render(input, output);
            }
        }
        Step(30);
        T.Check(output[0].R > 200, $"on: red ({output[0]})");
        cfg.LightsOff = true;
        Step(3);
        T.Check(output[0].R > 20 && output[0].R < 250, $"turning off fades instead of cutting ({output[0].R})");
        Step(90);
        T.Check(output.All(c => c == Rgb.Black), "after 1.5 s every LED is black");
        cfg.LightsOff = false;
        Step(60);
        T.Check(output[0].R > 200, $"turning back on restores the same colour ({output[0]})");

        // 消灯したまま起動しても、最初のコマから黒（一瞬光らない）
        var r2 = new DeviceRenderer(dev);
        var cfg2 = new RgbConfig { Mode = RgbModes.Static, StaticColor = "#FF0000", Brightness = 100, Gamma = false, LightsOff = true };
        var in2 = new RenderInput { Cfg = cfg2, Device = new RgbDeviceSettings { Name = "Strip" }, T = 1, TEval = 1 };
        var out2 = new Rgb[10];
        r2.Render(in2, out2);
        T.Check(out2.All(c => c == Rgb.Black), "starting with lights off: black from the first frame");
    }
}

public static class SensorNameTests
{
    private static SensorInfo S(string name, string hw, string type = "Temperature") => new($"/{hw.ToLowerInvariant()}/0/{type.ToLowerInvariant()}/{name.GetHashCode() & 0xffff}", name, hw + " test", hw, type);

    public static void TestSensorNamesAcrossVendors()
    {
        var intel = new[] { S("Core #1", "Cpu"), S("CPU Package", "Cpu"), S("Core Max", "Cpu") };
        var amd = new[] { S("Core (CCD1)", "Cpu"), S("Core (Tctl/Tdie)", "Cpu") };
        var amdNew = new[] { S("CCD1 (Tdie)", "Cpu"), S("Tctl", "Cpu") };
        T.Check(SensorNames.CpuTemperature(intel)?.Name == "CPU Package", "Intel: CPU Package");
        T.Check(SensorNames.CpuTemperature(amd)?.Name == "Core (Tctl/Tdie)", "AMD (older names): Core (Tctl/Tdie)");
        T.Check(SensorNames.CpuTemperature(amdNew)?.Name == "Tctl", "AMD (other names): Tctl");
        T.Check(SensorNames.CpuTemperature(new[] { S("Something", "Cpu") })?.Name == "Something", "unknown names: the first CPU temperature");

        var nv = new[] { S("GPU Hot Spot", "GpuNvidia"), S("GPU Core", "GpuNvidia"), S("GPU Memory Junction", "GpuNvidia") };
        var radeon = new[] { S("GPU Junction", "GpuAmd"), S("GPU Edge", "GpuAmd"), S("GPU Memory", "GpuAmd") };
        T.Check(SensorNames.GpuCoreTemperature(nv)?.Name == "GPU Core" && SensorNames.GpuHotSpotTemperature(nv)?.Name == "GPU Hot Spot", "NVIDIA: core and hot spot");
        T.Check(SensorNames.GpuCoreTemperature(radeon)?.Name == "GPU Edge" && SensorNames.GpuHotSpotTemperature(radeon)?.Name == "GPU Junction", "Radeon: edge and junction");
        T.Check(SensorNames.GpuCoreTemperature(new[] { S("GPU Hot Spot", "GpuNvidia"), S("Board", "GpuNvidia") })?.Name == "Board", "core fallback skips hot spot");

        var loads = new[] { S("CPU Core #1", "Cpu", "Load"), S("CPU Total", "Cpu", "Load"), S("GPU Memory Controller", "GpuNvidia", "Load"), S("D3D 3D", "GpuNvidia", "Load") };
        T.Check(SensorNames.Find(loads, "Cpu", "Load", SensorNames.CpuLoad)?.Name == "CPU Total", "CPU load: CPU Total");
        T.Check(SensorNames.Find(loads, "Gpu", "Load", SensorNames.GpuLoad)?.Name == "D3D 3D", "GPU load without 'GPU Core': D3D 3D (not the memory controller)");
    }
}

public static class ProfileRoleTests
{
    public static void TestRolesFromNamesAndRename()
    {
        T.Check(ProfileRoles.FromName("静音") == ProfileRoles.Silent && ProfileRoles.FromName("夜用") == ProfileRoles.Silent, "静音 / 夜用 → silent");
        T.Check(ProfileRoles.FromName("パフォーマンス") == ProfileRoles.Performance && ProfileRoles.FromName("冷却重視") == ProfileRoles.Performance, "パフォーマンス / 冷却 → performance");
        T.Check(ProfileRoles.FromName("標準") == ProfileRoles.Balanced && ProfileRoles.FromName("作業用") == ProfileRoles.Custom, "標準 → balanced, other → custom");

        var cfg = new AppConfig();
        cfg.Profiles.Add(new ProfileConfig { Name = "静音" });
        cfg.Profiles.Add(new ProfileConfig { Name = "標準" });
        cfg.Normalize();
        T.Check(cfg.Profiles[0].Role == ProfileRoles.Silent && cfg.Profiles[1].Role == ProfileRoles.Balanced, "old configs get roles from names once");
        cfg.Profiles[0].Name = "Silent night";
        cfg.Profiles[0].Name = "おやすみ";
        cfg.Normalize();
        T.Check(cfg.Profiles[0].Role == ProfileRoles.Silent, "renaming keeps the role");
        T.Check(ProfileRoles.TargetOffset(cfg.Profiles[0].Role) == 5 && ProfileRoles.Preset(cfg.Profiles[0].Role) == CurvePresets.Silent, "the role drives auto-tune offset and new-fan curve");
    }
}

public static class ImageCleanupTests
{
    public static void TestOnlyOldUnusedImagesAreRemoved()
    {
        string dir = Path.Combine(Path.GetTempPath(), "pfc_img_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            string P(string n) => Path.Combine(dir, n);
            foreach (var n in new[] { "used.png", "old.png", "new.jpg", "notes.txt" }) File.WriteAllText(P(n), "x");
            var old = DateTime.Now.AddDays(-30);
            File.SetLastWriteTime(P("used.png"), old);
            File.SetLastWriteTime(P("old.png"), old);
            File.SetLastWriteTime(P("notes.txt"), old);
            var unused = ImageCleanup.Unused(dir, new[] { "used.png" }, DateTime.Now).Select(Path.GetFileName).ToList();
            T.Check(unused.SequenceEqual(new[] { "old.png" }), $"only the old, unreferenced image is removed ({string.Join(",", unused)})");
            T.Check(ImageCleanup.Unused(Path.Combine(dir, "missing"), Array.Empty<string>(), DateTime.Now).Count == 0, "missing folder: nothing to do");
        }
        finally { Directory.Delete(dir, true); }
    }
}
