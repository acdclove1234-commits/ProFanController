// ==================================================
// ZoneAndSafetyTests.cs - ゾーンごとの設定・色合わせ・白で見比べるテストと、
//                         メモリの配線（SMBus）がぶつからないようにする確認のロジックテスト
// Dependencies: .NET 8 (MIT License)
// See CREDITS.txt for full license details.
// ==================================================

using ProFanController;

namespace LogicTests;

public static class ZoneAndSafetyTests
{
    /// <summary>ASRock B860 と同じ並び（RGB 端子 1・ARGB 端子 3 つ・基板）</summary>
    private static RgbDevice Board() => new(0, "ASRock B860", 34, 0, new[] { 1, 8, 8, 8, 9 }, "HID: test")
    {
        ZoneNames = new[] { "RGB LED 1 Header", "Addressable Header 1", "Addressable Header 2", "Addressable Header 3", "PCB" },
    };

    private static Rgb[] RenderStatic(RgbDevice dev, RgbDeviceSettings ds, string color = "#FFFFFF", bool white = false)
    {
        var r = new DeviceRenderer(dev);
        var input = new RenderInput
        {
            Cfg = new RgbConfig { Mode = RgbModes.Static, StaticColor = color, Brightness = 100, Gamma = false },
            Device = ds,
            WhiteTest = white,
            T = 1,
            TEval = 1,
            TPrevEval = 1,
        };
        var output = new Rgb[dev.LedCount];
        r.Render(input, output);
        return output;
    }

    public static void TestSegmentZonesMapping()
    {
        var segs = new List<(int Start, int Length, int Zone)>();
        RgbEffects.SegmentZones(34, new[] { 1, 8, 8, 8, 9 }, segs);
        T.Check(segs.SequenceEqual(new[] { (0, 1, 0), (1, 8, 1), (9, 8, 2), (17, 8, 3), (25, 9, 4) }), "each segment knows its zone");
        RgbEffects.SegmentZones(10, new[] { 3, 3 }, segs);
        T.Check(segs.Count == 1 && segs[0] == (0, 10, -1), "zones that don't add up → one segment without a zone");
        var map = RgbEffects.LedZones(34, new[] { 1, 8, 8, 8, 9 });
        T.Check(map[0] == 0 && map[1] == 1 && map[9] == 2 && map[33] == 4, "LED → zone map");
        T.Check(Board().ZoneName(2) == "Addressable Header 2" && new RgbDevice(0, "x", 3).ZoneName(1) == "ゾーン 2", "zone names (fallback when unknown)");
    }

    public static void TestZoneColorGains()
    {
        var ds = new RgbDeviceSettings { Name = "ASRock B860", GainR = 50 };
        ds.Zones.Add(new RgbZoneSettings { Name = "Addressable Header 2", Level = 0 });
        ds.Zones.Add(new RgbZoneSettings { Name = "Addressable Header 3", GainB = 20 });
        var o = RenderStatic(Board(), ds);
        T.Check(o[1] == new Rgb(128, 255, 255), $"device gain: red at 50% on header 1 ({o[1]})");
        T.Check(o[9] == Rgb.Black && o[16] == Rgb.Black, "header 2 at level 0 → off");
        T.Check(o[17] == new Rgb(128, 255, 51), $"zone gain multiplies the device gain on header 3 ({o[17]})");
        var plain = RenderStatic(Board(), new RgbDeviceSettings { Name = "ASRock B860" });
        T.Check(plain.All(c => c == new Rgb(255, 255, 255)), "no calibration → unchanged");
    }

    public static void TestWhiteTestShowsCalibratedWhite()
    {
        var ds = new RgbDeviceSettings { Name = "ASRock B860", GainG = 80 };
        var o = RenderStatic(Board(), ds, color: "#FF0000", white: true);
        T.Check(o.All(c => c == new Rgb(255, 204, 255)), $"white test: every LED white with the color calibration ({o[0]})");
    }

    public static void TestZoneOverridesLayoutAndEffect()
    {
        var dev = Board();
        var ds = new RgbDeviceSettings { Name = dev.Name, Reverse = true };
        ds.Zones.Add(new RgbZoneSettings { Name = "Addressable Header 1", Reverse = false, Effect = MusicStyles.Meter });
        var r = new DeviceRenderer(dev);
        var cfg = new RgbConfig { Mode = RgbModes.Music, MusicStyle = MusicStyles.BeatHue, Brightness = 100 };
        var input = new RenderInput { Cfg = cfg, Music = MusicTuning.Resolve(cfg), Device = ds, T = 1, TEval = 1, TPrevEval = 1 };
        var output = new Rgb[dev.LedCount];
        r.Render(input, output);
        var c = r.Context;
        T.Check(!c.Segments[1].Reverse && c.Segments[2].Reverse && c.Segments[3].Reverse, "zone setting overrides the device direction only for its header");
        T.Check(r.SegmentVariant(1) == EffectVariant.Meter, $"header 1 uses its own effect ({r.SegmentVariant(1)})");
        T.Check(r.SegmentVariant(2) == r.SegmentVariant(3) && r.SegmentVariant(2) != EffectVariant.Meter, "other headers keep the device effect");

        // 明るさなど、並びに関係のない設定だけ変えたときは、光り方を最初からにしない
        int version = c.LayoutVersion;
        var ds2 = new RgbDeviceSettings { Name = dev.Name, Reverse = true, Level = 60 };
        ds2.Zones.Add(new RgbZoneSettings { Name = "Addressable Header 1", Reverse = false, Effect = MusicStyles.Meter });
        input.Device = ds2;
        input.T = input.TEval = 1.02;
        input.TPrevEval = 1;
        r.Render(input, output);
        T.Check(c.LayoutVersion == version, "changing only the level keeps the layout (effects are not restarted)");
        var ds3 = new RgbDeviceSettings { Name = dev.Name, Reverse = false };
        input.Device = ds3;
        r.Render(input, output);
        T.Check(c.LayoutVersion != version && !c.Segments[2].Reverse, "changing the direction rebuilds the layout");
    }

    public static void TestZoneSettingsNormalize()
    {
        var ds = new RgbDeviceSettings { Name = "x", GainR = 150, Level = -5 };
        ds.Zones.Add(new RgbZoneSettings { Name = "A" });                     // 既定のまま → 消す
        ds.Zones.Add(new RgbZoneSettings { Name = "B", GainG = 300, Level = 50 });
        ds.Zones.Add(new RgbZoneSettings { Name = "" });
        ds.Normalize();
        T.Check(ds.GainR == 100 && ds.Level == 0, "device gains clamped to 0–100");
        T.Check(ds.Zones.Count == 1 && ds.Zones[0].Name == "B" && ds.Zones[0].GainG == 100, "default/unnamed zones dropped, gains clamped");
    }

    public static void TestOpenRgbSharedSmbusSetting()
    {
        T.Check(OpenRgbSettings.SharedSmbusAccess("{\"Drivers\":{\"shared_smbus_access\":false}}") == false, "explicitly off → false");
        T.Check(OpenRgbSettings.SharedSmbusAccess("{\"Drivers\":{\"shared_smbus_access\":true}}") == true, "explicitly on → true");
        T.Check(OpenRgbSettings.SharedSmbusAccess("{\"Server\":{\"default_port\":6742}}") == true, "not written → OpenRGB default (shared)");
        T.Check(OpenRgbSettings.SharedSmbusAccess("not json") is null, "broken file → unknown");

        string dir = Path.Combine(Path.GetTempPath(), "pfc-openrgb-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            string on = Path.Combine(dir, "on.json"), off = Path.Combine(dir, "off.json");
            File.WriteAllText(on, "{\"Drivers\":{}}");
            File.WriteAllText(off, "{\"Drivers\":{\"shared_smbus_access\":false}}");
            T.Check(OpenRgbSettings.FindUnsharedSmbus(new[] { Path.Combine(dir, "missing.json"), on }) is null, "missing/shared files → no warning");
            T.Check(OpenRgbSettings.FindUnsharedSmbus(new[] { on, off }) == off, "the file with sharing off is reported");
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    public static void TestSceneColor()
    {
        var red = new SceneColor();
        red.Add(new[] { new LinColor(1, 0, 0), new LinColor(0.5f, 0, 0), LinColor.Black });
        T.Check(red.TryGet(out var c, out float level), "a lit scene has a color");
        T.Check(c.R > 0.99f && c.G < 0.1f && c.B < 0.1f, $"all red → red ({c})");
        T.Near(level, 0.5, 0.01, "level = average brightness of all LEDs (dark ones count)");

        var mix = new SceneColor();
        mix.Add(new[] { new LinColor(1, 0, 0), new LinColor(1, 0, 0), new LinColor(1, 0, 0), new LinColor(0, 0, 1) });
        mix.TryGet(out var m, out _);
        T.Check(m.R > m.B, $"mostly red with a little blue leans red ({m})");

        var rainbow = new SceneColor();
        rainbow.Add(new[] { new LinColor(1, 0, 0), new LinColor(0, 1, 0), new LinColor(0, 0, 1), new LinColor(1, 1, 0), new LinColor(0, 1, 1), new LinColor(1, 0, 1) });
        rainbow.TryGet(out var w, out _);
        T.Check(Math.Min(w.R, Math.Min(w.G, w.B)) > 0.5f, $"every hue at once → close to white ({w})");

        var dark = new SceneColor();
        dark.Add(new[] { LinColor.Black, LinColor.Black });
        T.Check(!dark.TryGet(out _, out _), "all dark → no color");
    }

    public static void TestSlowDeviceFollowsColor()
    {
        var gpu = new RgbDevice(1, "ASRock Radeon RX 7900 XTX Phantom Gaming", 4, RgbDevice.TypeGpu, null, "I2C: AMD ADL");
        var r = new DeviceRenderer(gpu);
        var cfg = new RgbConfig { Mode = RgbModes.Music, Brightness = 100, Gamma = false };
        var input = new RenderInput { Cfg = cfg, Music = MusicTuning.Resolve(cfg), Device = new RgbDeviceSettings { Name = gpu.Name }, T = 1, TEval = 1, TPrevEval = 1,
            FollowColor = new LinColor(0, 1, 0), FollowLevel = 0.3f };
        var o = new Rgb[4];
        r.Render(input, o);
        T.Check(o.All(x => x == new Rgb(0, 255, 0)), $"the GPU shows the other devices' color at full level (no flicker from brightness changes) ({o[0]})");
        T.Check(r.ActiveEffect == DeviceRenderer.FollowEffectName, "status shows that it follows the others");

        var ram = new RgbDevice(2, "ENE DRAM", 8, RgbDevice.TypeDram, null, "I2C: PIIX4");
        var rr = new DeviceRenderer(ram);
        input.Device = new RgbDeviceSettings { Name = ram.Name };
        var o2 = new Rgb[8];
        rr.Render(input, o2);
        T.Check(o2[0].G == (byte)Math.Round(255 * DeviceRenderer.SlowMinBrightness) && o2[0].R == 0, $"memory follows the color, brightness not below the slow floor ({o2[0]})");
    }

    public static void TestConflictingSoftware()
    {
        var found = InstallCheck.FindConflicts(new[] { "explorer", "L-Connect 3", "LEDKeeper2", "ProFanController", "FanControl", "iCUESetup", "MSI.CentralServer" });
        T.Check(found.Contains("Lian Li L-Connect") && found.Contains("MSI Mystic Light") && found.Contains("MSI Center"), $"vendor apps found ({string.Join(", ", found)})");
        T.Check(found.Contains("FanControl"), "FanControl (exact name) found");
        T.Check(found.Contains("Corsair iCUE"), "iCUE found by part of the name");
        T.Check(InstallCheck.FindConflicts(new[] { "ProFanController", "ProFanController.Watchdog", "explorer" }).Count == 0, "this app itself is never a conflict");
        T.Check(InstallCheck.FindConflicts(new[] { "MyFanControlHelper" }).Count == 0, "FanControl needs the exact name");
        T.Check(InstallCheck.FindConflicts(new[] { "LEDKeeper2", "LEDKeeper2" }).Count == 1, "each app listed once");
    }

    public static void TestAutoStartLocation()
    {
        string xml = "<Task><Actions Context=\"Author\"><Exec><Command>C:\\Users\\User\\Downloads\\ProFanController_配布用_v6.4.4\\ProFanController\\ProFanController.exe</Command><Arguments>--minimized</Arguments></Exec></Actions></Task>";
        string? cmd = InstallCheck.CommandFromTaskXml(xml);
        T.Check(cmd == @"C:\Users\User\Downloads\ProFanController_配布用_v6.4.4\ProFanController\ProFanController.exe", $"command read from the task ({cmd})");
        T.Check(InstallCheck.CommandFromTaskXml("<Task></Task>") is null, "no command → null");
        T.Check(InstallCheck.CommandFromTaskXml("<Command>C:\\A &amp; B\\x.exe</Command>") == @"C:\A & B\x.exe", "XML entities decoded");
        T.Check(InstallCheck.PointsElsewhere(@"C:\Tools\Old\ProFanController.exe", @"C:\Tools\ProFanController\ProFanController.exe"), "different folder → elsewhere");
        T.Check(!InstallCheck.PointsElsewhere(@"c:\tools\profancontroller\ProFanController.exe", @"C:\Tools\ProFanController\ProFanController.exe"), "same path (case) → not elsewhere");
        T.Check(!InstallCheck.PointsElsewhere(null, @"C:\Tools\ProFanController\ProFanController.exe"), "no auto-start → nothing to warn");
        string downloads = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads", "ProFanController_配布用_v6.4.4", "ProFanController");
        T.Check(InstallCheck.UnstableFolderName(downloads) == "ダウンロード", "running from Downloads is flagged");
        T.Check(InstallCheck.UnstableFolderName(@"C:\Tools\ProFanController") is null, "C:\\Tools is fine");
    }

    public static void TestCopyInstall()
    {
        string root = Path.Combine(Path.GetTempPath(), "pfc-move-" + Guid.NewGuid().ToString("N"));
        string from = Path.Combine(root, "Downloads", "ProFanController_配布用_v6.4.5", "ProFanController");
        string to = Path.Combine(root, "Tools", "ProFanController");
        try
        {
            Directory.CreateDirectory(Path.Combine(from, "logs"));
            Directory.CreateDirectory(Path.Combine(from, "data"));
            File.WriteAllText(Path.Combine(from, "ProFanController.exe"), "new exe");
            File.WriteAllText(Path.Combine(from, "config.json"), "{\"new\":true}");
            File.WriteAllText(Path.Combine(from, "logs", "20261104.log"), "log");
            File.WriteAllText(Path.Combine(from, "data", "history.csv"), "1,2,3");
            Directory.CreateDirectory(to);
            File.WriteAllText(Path.Combine(to, "config.json"), "{\"old\":true}");

            string exe = InstallCheck.CopyInstall(from, to);
            T.Check(exe == Path.Combine(to, "ProFanController.exe") && File.ReadAllText(exe) == "new exe", "exe copied to the new place");
            T.Check(File.ReadAllText(Path.Combine(to, "config.json")) == "{\"new\":true}", "the settings in use are carried over");
            T.Check(Directory.GetFiles(Path.Combine(to, "backups"), "config_*_before-move.json").Length == 1, "the old settings at the new place are kept in backups");
            T.Check(File.Exists(Path.Combine(to, "data", "history.csv")), "records are carried over");
            T.Check(!Directory.Exists(Path.Combine(to, "logs")), "logs (may be in use) are not copied");
            T.Check(InstallCheck.CopyInstall(to, to) == exe, "moving to the same place does nothing");
        }
        finally
        {
            try { Directory.Delete(root, true); } catch { /* 一時フォルダーなので残っても害はない */ }
        }
    }

    public static void TestDirectGpuAutoLatency()
    {
        T.Check(RgbDeviceChannel.AutoDirectLatencyMs(null, 8) == 8, "not measured yet → default");
        T.Check(RgbDeviceChannel.AutoDirectLatencyMs(3.0, 8) == 4, "measured 3 ms write → 4 ms lead");
        T.Check(RgbDeviceChannel.AutoDirectLatencyMs(120, 8) == 40, "capped at 40 ms");
    }

    public static void TestDimmHardwareIds()
    {
        T.Check(HardwareHub.IsDimm("/memory/dimm/1") && HardwareHub.IsDimm("/memory/dimm/3"), "DIMM temperature hardware");
        T.Check(!HardwareHub.IsDimm("/ram") && !HardwareHub.IsDimm("/memory/generic"), "memory usage is not DIMM");
        T.Check(new SafetyConfig().ReadMemoryTemperature, "memory temperature is read by default");
    }
}
