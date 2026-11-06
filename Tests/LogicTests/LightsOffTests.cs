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
