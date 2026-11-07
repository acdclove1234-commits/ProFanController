// ==================================================
// OutputPathTests.cs - 出力先の種類と音のソフトの見分け（OutputPath）のテスト
// Dependencies: .NET 8 (MIT License)
// See CREDITS.txt for full license details.
// ==================================================

using ProFanController;

namespace LogicTests;

public static class OutputPathTests
{
    public static void TestKinds()
    {
        // よくある出力先（名前・列挙子・形）
        T.Check(OutputPath.Classify("ヘッドホン (USB Audio DAC)", "USB", 3).Kind == OutputKind.Usb, "USB DAC");
        T.Check(OutputPath.Classify("Realtek Digital Output (Realtek(R) Audio)", "HDAUDIO", 8).Kind == OutputKind.Onboard, "onboard S/PDIF");
        T.Check(OutputPath.Classify("Monitor (NVIDIA High Definition Audio)", "HDAUDIO", 9).Kind == OutputKind.Display, "monitor over HDMI / DisplayPort");
        T.Check(OutputPath.Classify("CABLE Input (VB-Audio Virtual Cable)", "ROOT", 1).Kind == OutputKind.Virtual, "VB-Audio cable");
        T.Check(OutputPath.Classify("スピーカー (Steam Streaming Speakers)", "ROOT", 1).Kind == OutputKind.Virtual, "Steam streaming speakers");
        // Bluetooth・仮想の出力先など
        T.Check(OutputPath.Classify("ヘッドホン (WH-1000XM5)", "BTHENUM", 3).Kind == OutputKind.Bluetooth, "Bluetooth headphones (enumerator)");
        T.Check(OutputPath.Classify("Headset (AirPods Pro)", "", 0).Kind == OutputKind.Bluetooth, "Bluetooth by name");
        T.Check(OutputPath.Classify("Voicemeeter Input (VB-Audio Voicemeeter VAIO)", "ROOT", 1).Software.Contains("Voicemeeter"), "Voicemeeter named");
        T.Check(OutputPath.Classify("SteelSeries Sonar - Media (SteelSeries Sonar Virtual Audio Device)", "SWD", 1) is { Kind: OutputKind.Virtual } s && s.Software[0] == "SteelSeries Sonar", "Sonar media device");
        T.Check(OutputPath.Classify("Razer BlackShark V2 Pro", "USB", 3).Kind == OutputKind.Usb, "a Razer USB headset is a real USB device, not virtual");
        T.Check(OutputPath.Classify("Remote Audio", "", 0).Kind == OutputKind.Remote, "remote desktop");
        T.Check(OutputPath.Classify("Speakers (Something)", "", 0).Kind == OutputKind.Unknown, "unknown stays unknown");
    }

    public static void TestSuggestions()
    {
        var bt = OutputPath.Classify("Buds", "BTHENUM", 3);
        T.Check(bt.SuggestedDelayMs == 180 && OutputPath.ShouldSuggest(bt, 0) && !OutputPath.ShouldSuggest(bt, 180), "Bluetooth: suggest +180 until the delay is set");
        var hdmi = OutputPath.Classify("TV (NVIDIA High Definition Audio)", "HDAUDIO", 9);
        T.Check(OutputPath.ShouldSuggest(hdmi, 0) && !OutputPath.ShouldSuggest(hdmi, 50) && !OutputPath.ShouldSuggest(hdmi, 120), "HDMI: suggest when the delay is far below the estimate, not when the user set more (TV)");
        var usb = OutputPath.Classify("USB DAC", "USB", 3);
        T.Check(usb.SuggestedDelayMs == 0 && !OutputPath.ShouldSuggest(usb, 0) && OutputPath.Hint(usb).Length == 0, "USB: no suggestion");
        var cable = OutputPath.Classify("CABLE Input (VB-Audio Virtual Cable)", "ROOT", 1);
        T.Check(OutputPath.Hint(cable).Contains("VB-Audio") && OutputPath.ShouldSuggest(cable, 0), "virtual cable: hint names the software");
    }

    public static void TestSoftware()
    {
        var sw = OutputPath.SoftwareFrom(new[] { "audiodg", "NahimicSvc64", "NahimicSvc32", "NahimicAPO4Volume", "RtkAudUService64", "explorer" });
        T.Check(sw.Count == 1 && sw[0] == "Nahimic", $"Nahimic found once, Realtek's service is not an effect ({string.Join(",", sw)})");
        var info = OutputPath.Classify("USB DAC", "USB", 3, sw);
        T.Check(OutputPath.SoftwareNote(info).Contains("Nahimic") && OutputPath.SoftwareNote(info).Contains("同期テスト"), "software note names Nahimic and points to the sync test");
        var plain = OutputPath.Classify("CABLE Input (VB-Audio Virtual Cable)", "ROOT", 1);
        T.Check(OutputPath.SoftwareNote(plain).Length == 0, "a plain virtual cable is not an effect");
    }
}
