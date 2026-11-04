// ==================================================
// AppSafetyTests.cs - 設定ファイルの安全対策のテスト (空の鍵・重複した端子・null・プロファイル名の重複)
// Dependencies: .NET 8 (MIT License)
// See CREDITS.txt for full license details.
// ==================================================

using ProFanController;

namespace LogicTests;

public static class AppSafetyTests
{
    public static void TestWebTokenNeverEmpty()
    {
        var cfg = new AppConfig();
        cfg.Web.Token = "";
        cfg.Normalize();
        T.Check(cfg.Web.Token.Length >= WebServer.MinTokenLength, $"empty token regenerated ({cfg.Web.Token.Length} chars)");
        cfg.Web.Token = "abc";
        cfg.Normalize();
        T.Check(cfg.Web.Token.Length >= WebServer.MinTokenLength && cfg.Web.Token != "abc", "too-short token regenerated");
        string keep = new string('a', 16);
        cfg.Web.Token = keep;
        cfg.Normalize();
        T.Check(cfg.Web.Token == keep, "existing 16-char token kept (phone bookmarks keep working)");
        T.Check(WebConfig.NewToken().Length == 32, "new tokens are 16 random bytes");
    }

    public static void TestDuplicateControlIdsRemoved()
    {
        var cfg = new AppConfig();
        var p = new ProfileConfig { Name = "標準" };
        p.Fans.Add(new FanConfig { ControlId = "/lpc/nct6686d/0/control/0", Name = "A" });
        p.Fans.Add(new FanConfig { ControlId = "/LPC/nct6686d/0/control/0", Name = "B" });
        p.Fans.Add(new FanConfig { ControlId = "/lpc/nct6686d/0/control/1", Name = "C" });
        cfg.Profiles.Add(p);
        cfg.Normalize();
        T.Check(p.Fans.Count == 2 && p.Fans[0].Name == "A" && p.Fans[1].Name == "C", "duplicate control id dropped (first kept)");
    }

    public static void TestNullCollectionsFromHandEditedJson()
    {
        var cfg = AppConfig.FromJson("{\"Profiles\":null,\"ScheduleRules\":null,\"Rgb\":null,\"Web\":null,\"Safety\":null}");
        T.Check(cfg.Profiles.Count == 1 && cfg.ScheduleRules is not null && cfg.Rgb is not null && cfg.Rgb.Music is not null && cfg.Safety is not null,
            "null sections replaced by defaults");
        T.Check(cfg.Web.Token.Length >= WebServer.MinTokenLength, "token generated for null web section");
    }

    public static void TestDuplicateProfileNames()
    {
        var cfg = new AppConfig();
        cfg.Profiles.Add(new ProfileConfig { Name = "静音" });
        cfg.Profiles.Add(new ProfileConfig { Name = "静音" });
        cfg.Normalize();
        T.Check(cfg.Profiles[0].Name == "静音" && cfg.Profiles[1].Name == "静音 (2)", "duplicate profile names made unique");
    }

    public static void TestWebLocalNetworkFilter()
    {
        bool L(string a) => WebServer.IsLocalNetwork(System.Net.IPAddress.Parse(a));
        T.Check(L("192.168.1.20") && L("10.0.0.5") && L("172.20.1.1") && L("127.0.0.1") && L("169.254.3.4"), "private / loopback IPv4 allowed");
        T.Check(!L("8.8.8.8") && !L("172.32.0.1") && !L("100.64.0.1"), "public IPv4 rejected");
        T.Check(L("fe80::1") && L("fd00::5") && L("::1") && L("::ffff:192.168.0.3"), "local IPv6 allowed");
        T.Check(!L("2001:4860:4860::8888") && !WebServer.IsLocalNetwork(null), "public IPv6 / null rejected");
    }

    public static void TestUnknownSettingsArePreserved()
    {
        var cfg = AppConfig.FromJson("{\"FutureFeature\":{\"x\":1},\"Rgb\":{\"FutureLed\":true,\"Mode\":\"Static\"}}");
        string json = cfg.ToJson();
        T.Check(json.Contains("FutureFeature") && json.Contains("FutureLed") && cfg.Rgb.Mode == RgbModes.Static, "unknown fields round-trip (downgrade-safe)");
        T.Check(cfg.SchemaVersion == AppConfig.CurrentSchemaVersion, "schema version stamped");
    }

    public static void TestTrimmedProfileNameKeepsReferences()
    {
        var cfg = new AppConfig();
        cfg.Profiles.Add(new ProfileConfig { Name = "標準" });
        cfg.Profiles.Add(new ProfileConfig { Name = "Gaming " });
        cfg.ActiveProfile = "Gaming";
        cfg.ScheduleRules.Add(new ScheduleRule { ProfileName = "Gaming " });
        cfg.Normalize();
        T.Check(cfg.Profiles[1].Name == "Gaming" && cfg.ActiveProfile == "Gaming" && cfg.ScheduleRules[0].ProfileName == "Gaming",
            $"trimmed profile keeps active/rules ({cfg.ActiveProfile})");
    }

    public static void TestLoggerFlushWritesEverything()
    {
        Logger.FileSuffix = "test";
        string marker = "flush-check-" + Guid.NewGuid().ToString("N");
        for (int i = 0; i < 500; i++) Logger.Info(marker + " " + i);
        Logger.Flush(5000);
        string file = Path.Combine(Logger.Directory, $"{DateTime.Now:yyyyMMdd}_test.log");
        // Logger はファイルを開いたままにしているので、Windows でも読めるよう書き込み側と共有して開く
        int count = 0;
        if (File.Exists(file))
        {
            using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream);
            count = reader.ReadToEnd().Split('\n').Count(l => l.Contains(marker));
        }
        T.Check(count == 500, $"all queued lines written after Flush ({count}/500)");
        Logger.FileSuffix = "";
    }
}
