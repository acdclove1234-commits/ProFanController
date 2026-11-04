// ==================================================
// CaseLayoutTests.cs - ケースの中の配置のテスト (場所の座標・流れる向きの位置・デバイスをまたいだ光のつながり)
// Dependencies: .NET 8 (MIT License)
// See CREDITS.txt for full license details.
// ==================================================

using ProFanController;

namespace LogicTests;

public static class CaseLayoutTests
{
    private static RgbDevice Board() => new(0, "ASRock B860", 34, 0, new[] { 1, 8, 8, 8, 9 }, "HID: test")
    {
        ZoneNames = new[] { "RGB LED 1 Header", "Addressable Header 1", "Addressable Header 2", "Addressable Header 3", "PCB" },
    };

    /// <summary>ゾーンの情報がないデバイス（全体で 1 つのまとまり）</summary>
    private static RgbDevice Strip(string name, int leds) => new(1, name, leds, 0, null, "HID: test");

    private static Rgb[] Render(RgbDevice dev, RgbDeviceSettings ds, bool caseLayout, string direction = CaseDirections.Up, string mode = RgbModes.RainbowStatic)
    {
        var r = new DeviceRenderer(dev);
        var input = new RenderInput
        {
            Cfg = new RgbConfig { Mode = mode, Brightness = 100, Gamma = false, CaseLayout = caseLayout, CaseDirection = direction },
            Device = ds,
            T = 1, TEval = 1, TPrevEval = 1,
        };
        var output = new Rgb[dev.LedCount];
        r.Render(input, output);
        return output;
    }

    private static RgbDeviceSettings Placed(string device, params (string Zone, string Place)[] zones)
    {
        var s = new RgbDeviceSettings { Name = device };
        foreach (var (zone, place) in zones) s.Zones.Add(new RgbZoneSettings { Name = zone, Place = place });
        return s;
    }

    public static void TestPlacesAreInsideTheCase()
    {
        foreach (var p in CaseLayout.Places)
        {
            var pts = CaseLayout.Points(p, 24);
            bool inside = pts.All(q => q.X >= 0 && q.X <= 1 && q.Y >= 0 && q.Y <= 1);
            T.Check(inside, $"{p.Id}: all LEDs inside the case");
        }
        T.Check(CaseLayout.Places.Select(p => p.Id).Distinct().Count() == CaseLayout.Places.Count, "place ids are unique");
        T.Check(CaseLayout.Find("nope") is null && CaseLayout.Find("") is null, "unknown / empty place → none");
    }

    public static void TestRingsSplitLedsAndKeepOrder()
    {
        var up = CaseLayout.Find("front-3-up")!;
        var pts = CaseLayout.Points(up, 24);
        // 8 個ずつ 3 つの輪。最初の輪（下）の中心は 2 番目・3 番目より下
        double Cy(int ring) => pts.Skip(ring * 8).Take(8).Average(q => q.Y);
        T.Check(Cy(0) > Cy(1) && Cy(1) > Cy(2), $"chained fans go bottom → top ({Cy(0):F2}, {Cy(1):F2}, {Cy(2):F2})");
        var s = CaseLayout.Positions(up, 24, CaseDirections.Up);
        T.Check(s.Skip(16).Average() > s.Take(8).Average(), "flowing up: the top fan is further along");
        var odd = CaseLayout.Points(up, 25);
        T.Check(odd.Length == 25, "LEDs that don't divide evenly are all placed");
    }

    public static void TestDirections()
    {
        T.Near(CaseLayout.Along(CaseDirections.Up, 0.5, 1), 0, 1e-6, "up: bottom = start");
        T.Near(CaseLayout.Along(CaseDirections.Up, 0.5, 0), 1, 1e-6, "up: top = end");
        T.Near(CaseLayout.Along(CaseDirections.Down, 0.5, 0), 0, 1e-6, "down: top = start");
        T.Near(CaseLayout.Along(CaseDirections.BackToFront, 0, 0.5), 0, 1e-6, "back → front: rear = start");
        T.Near(CaseLayout.Along(CaseDirections.FrontToBack, 0, 0.5), 1, 1e-6, "front → back: rear = end");
        T.Near(CaseLayout.Along(CaseDirections.Outward, 0.5, 0.5), 0, 1e-6, "outward: centre = start");
        T.Near(CaseLayout.Along(CaseDirections.Outward, 0, 0), 1, 1e-6, "outward: corner = end");
        T.Near(CaseLayout.Along("bogus", 0.5, 1), 0, 1e-6, "unknown direction → up");
    }

    public static void TestOffKeepsTheOldLook()
    {
        var placed = Placed("ASRock B860", ("Addressable Header 1", "front-top"), ("Addressable Header 2", "front-bottom"));
        var off = Render(Board(), placed, caseLayout: false);
        var plain = Render(Board(), new RgbDeviceSettings { Name = "ASRock B860" }, caseLayout: false);
        T.Check(off.SequenceEqual(plain), "with the switch off, chosen places change nothing");
        var on = Render(Board(), new RgbDeviceSettings { Name = "ASRock B860" }, caseLayout: true);
        T.Check(on.SequenceEqual(plain), "with nothing placed, the switch changes nothing");
    }

    public static void TestPlacedLedsUseTheirHeight()
    {
        var placed = Placed("ASRock B860", ("Addressable Header 1", "front-top"), ("Addressable Header 2", "front-bottom"));
        var o = Render(Board(), placed, caseLayout: true);
        // 上のファンと下のファンは、ケースの中の高さが違うので、虹の違う色になる（流れる向き: 下から上へ）
        var top = Rgb.FromHsv(CaseLayout.Along(CaseDirections.Up, 0.9, 0.22) * 360);
        T.Check(o[1] != o[9], $"top and bottom fan differ ({o[1]} vs {o[9]})");
        // 輪の真上の LED（ゾーンの最初）は、輪の中心より少し上
        var ring = CaseLayout.Points(CaseLayout.Find("front-top")!, 8);
        var expected = Rgb.FromHsv(CaseLayout.Along(CaseDirections.Up, ring[0].X, ring[0].Y) * 360);
        T.Check(o[1] == expected, $"first LED of the top fan has the colour of its height ({o[1]} vs {expected}, centre {top})");
        // 場所を決めていない端子 3 は、これまでどおり
        var plain = Render(Board(), new RgbDeviceSettings { Name = "ASRock B860" }, caseLayout: false);
        T.Check(o.Skip(17).Take(8).SequenceEqual(plain.Skip(17).Take(8)), "unplaced header keeps its own layout");
    }

    public static void TestLightContinuesAcrossDevices()
    {
        // 前面の縦のテープを 2 本に分けて、別々のデバイスとして上下に置いたのと同じ: 上半分と下半分で虹がつながる
        var lower = Strip("Lower", 10);
        var upper = Strip("Upper", 10);
        var full = Strip("Full", 20);
        var fullPlaced = Placed("Full", (full.ZoneName(0), "strip-front-up"));
        var whole = Render(full, fullPlaced, caseLayout: true);
        // 1 本の帯の LED 位置（下から上）と同じ高さに、2 台の LED があれば同じ色になる
        var s = CaseLayout.Positions(CaseLayout.Find("strip-front-up")!, 20, CaseDirections.Up);
        T.Check(s[0] < s[19] && Math.Abs(s[0]) < 0.05 && Math.Abs(s[19] - 1) < 0.05, "a front strip spans the case from bottom to top");
        T.Check(whole[0] != whole[19], "colours change along the strip");
        // 下のデバイス（下半分）と上のデバイス（上半分）を、それぞれ「前面のファン」の下と上に置くと、色の順も下から上
        var lo = Render(lower, Placed("Lower", (lower.ZoneName(0), "front-bottom")), caseLayout: true);
        var hi = Render(upper, Placed("Upper", (upper.ZoneName(0), "front-top")), caseLayout: true);
        var loS = CaseLayout.Positions(CaseLayout.Find("front-bottom")!, 10, CaseDirections.Up);
        var hiS = CaseLayout.Positions(CaseLayout.Find("front-top")!, 10, CaseDirections.Up);
        T.Check(lo[0] == Rgb.FromHsv(loS[0] * 360) && hi[0] == Rgb.FromHsv(hiS[0] * 360),
            "each device takes its colour from its place in the case (one rainbow across devices)");
    }

    public static void TestMusicEffectsUseCaseSpan()
    {
        var ds = Placed("Full", ("ゾーン 1", "strip-front-up"));
        var r = new DeviceRenderer(Strip("Full", 20));
        var input = new RenderInput
        {
            Cfg = new RgbConfig { Mode = RgbModes.Music, Brightness = 100, Gamma = false, CaseLayout = true },
            Device = ds,
            T = 1, TEval = 1, TPrevEval = 1,
        };
        r.Render(input, new Rgb[20]);
        var seg = r.Context.Segments[0];
        T.Check(seg.Placed && Math.Abs(seg.Ne - CaseLayout.Span) < 1e-6 && !seg.Mirror && !seg.Ring,
            $"placed zone is one part of a case-wide band (Ne {seg.Ne})");
        T.Check(r.Context.P[0] < r.Context.P[19], "music positions follow the case (bottom → top)");
    }
}
