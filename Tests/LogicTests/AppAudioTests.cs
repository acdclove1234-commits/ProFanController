// ==================================================
// AppAudioTests.cs - 「特定のアプリだけ」の取り込み方のテスト (どのプロセスを取り込むか・以前からの方式に戻す判断・試し直す時期)
// Dependencies: .NET 8 (MIT License)
// See CREDITS.txt for full license details.
// ==================================================

using ProFanController;

namespace LogicTests;

public static class AppAudioTests
{
    /// <summary>Spotify と同じ形: もとのプロセス（親は終了したランチャー）が 7 つの子プロセスを起動している</summary>
    private static List<ProcEntry> SpotifyTree() => new()
    {
        new(30296, 30276, "Spotify"),
        new(30316, 30296, "Spotify"),
        new(30560, 30296, "Spotify"),
        new(30628, 30296, "Spotify"),
        new(3024, 30296, "Spotify"),
        new(10836, 30296, "Spotify"),
        new(27820, 30296, "Spotify"),
        new(28264, 30296, "Spotify"),
        new(500, 4, "svchost"),
    };

    public static void TestSupportsProcessLoopback()
    {
        T.Check(!AppAudioTarget.Supports(new Version(10, 0, 19045)), "Windows 10 22H2 (19045) is not supported");
        T.Check(AppAudioTarget.Supports(new Version(10, 0, 20348)), "Windows 10 build 20348 is supported");
        T.Check(AppAudioTarget.Supports(new Version(10, 0, 22631)), "Windows 11 23H2 is supported");
        T.Check(AppAudioTarget.Supports(new Version(10, 0, 26300)), "Windows 11 26300 is supported");
        T.Check(!AppAudioTarget.Supports(new Version(6, 3, 9600)), "Windows 8.1 is not supported");
    }

    public static void TestNormalizeName()
    {
        T.Check(AppAudioTarget.NormalizeName(" Spotify.exe ") == "Spotify", "trims and strips .exe");
        T.Check(AppAudioTarget.NormalizeName("chrome.EXE") == "chrome", ".exe in any case");
        T.Check(AppAudioTarget.NormalizeName("my.app") == "my.app", "other dots are kept");
        T.Check(AppAudioTarget.NormalizeName(null) == "" && AppAudioTarget.NormalizeName("  ") == "", "empty stays empty");
    }

    public static void TestChooseTargetSpotifyTree()
    {
        var procs = SpotifyTree();
        // 音のセッションがまだない（一時停止中・起動直後）: 子を一番多く持つ、もとのプロセス
        T.Check(AppAudioTarget.ChooseTarget(procs, "Spotify", new List<SessionEntry>()) == 30296, "no session → main (root) process");
        // セッションを持っているのが子プロセスでも、木ごと取り込めるよう一番上を選ぶ
        var childSession = new List<SessionEntry> { new(27820, true, 0.3f) };
        T.Check(AppAudioTarget.ChooseTarget(procs, "Spotify", childSession) == 30296, "session owned by a child → its root");
        T.Check(AppAudioTarget.ChooseTarget(procs, "spotify.exe", new List<SessionEntry> { new(30296, true, 0.2f) }) == 30296, "name match ignores case and .exe");
        T.Check(AppAudioTarget.ChooseTarget(procs, "Chrome", childSession) == 0, "not running → 0");
        T.Check(AppAudioTarget.ChooseTarget(procs, "", childSession) == 0, "no name → 0");
        // ほかのアプリのセッションは関係ない
        T.Check(AppAudioTarget.ChooseTarget(procs, "Spotify", new List<SessionEntry> { new(500, true, 0.9f) }) == 30296, "sessions of other apps are ignored");
    }

    public static void TestChooseTargetSeveralInstances()
    {
        // 別々に起動された 2 つの木（ブラウザーの別プロファイルなど）
        var procs = new List<ProcEntry>
        {
            new(100, 1, "app"), new(101, 100, "app"), new(102, 100, "app"),
            new(200, 1, "app"), new(201, 200, "app"),
        };
        T.Check(AppAudioTarget.ChooseTarget(procs, "app", new List<SessionEntry>()) == 100, "no session → the larger tree");
        // 音が出ているセッションが、再生中なだけのセッションより優先される
        var sessions = new List<SessionEntry> { new(101, true, 0f), new(201, true, 0.4f) };
        T.Check(AppAudioTarget.ChooseTarget(procs, "app", sessions) == 200, "sounding session wins over an active silent one");
        sessions = new List<SessionEntry> { new(101, false, 0f), new(201, true, 0f) };
        T.Check(AppAudioTarget.ChooseTarget(procs, "app", sessions) == 200, "active session wins over an inactive one");
        sessions = new List<SessionEntry> { new(102, true, 0.2f), new(201, true, 0.6f) };
        T.Check(AppAudioTarget.ChooseTarget(procs, "app", sessions) == 200, "louder session wins among sounding ones");
        // 同じ大きさの木なら PID の小さいほう（毎回同じ結果になる）
        var even = new List<ProcEntry> { new(300, 1, "app"), new(301, 300, "app"), new(200, 1, "app"), new(201, 200, "app") };
        T.Check(AppAudioTarget.ChooseTarget(even, "app", new List<SessionEntry>()) == 200, "tie → lower PID, deterministic");
    }

    public static void TestChooseTargetBrokenParents()
    {
        // 親の PID が使い回されて輪になっていても止まる
        var loop = new List<ProcEntry> { new(10, 20, "app"), new(20, 30, "app"), new(30, 10, "app") };
        int t = AppAudioTarget.ChooseTarget(loop, "app", new List<SessionEntry> { new(10, true, 0.5f) });
        T.Check(t == 10 || t == 20 || t == 30, $"cyclic parents terminate ({t})");
        var self = new List<ProcEntry> { new(7, 7, "app") };
        T.Check(AppAudioTarget.ChooseTarget(self, "app", new List<SessionEntry>()) == 7, "self-parent terminates");
        // 親が別の名前のプロセス（エクスプローラーなど）なら、そこで止まる
        var shell = new List<ProcEntry> { new(1000, 1, "explorer"), new(2000, 1000, "app"), new(2001, 2000, "app") };
        T.Check(AppAudioTarget.ChooseTarget(shell, "app", new List<SessionEntry> { new(2001, true, 0.5f) }) == 2000, "stops at a parent with another name");
    }

    public static void TestTreeAndRetarget()
    {
        var procs = new List<ProcEntry> { new(100, 1, "app"), new(101, 100, "app"), new(200, 1, "app") };
        T.Check(AppAudioTarget.InTree(procs, "app", 100, 101) && AppAudioTarget.InTree(procs, "app", 100, 100), "child and root are in the tree");
        T.Check(!AppAudioTarget.InTree(procs, "app", 100, 200) && !AppAudioTarget.InTree(procs, "app", 100, 999), "other tree / unknown pid are not");
        var sessions = new List<SessionEntry> { new(101, false, 0.2f) };
        T.Check(AppAudioTarget.TreeSounding(procs, "app", 100, sessions) && !AppAudioTarget.TreeSounding(procs, "app", 200, sessions), "sounding session counts for its tree only");

        T.Check(!AppAudioTarget.ShouldRetarget(100, true, 100, true, true), "same target → keep");
        T.Check(AppAudioTarget.ShouldRetarget(100, false, 300, false, false), "current exited → retarget");
        T.Check(AppAudioTarget.ShouldRetarget(100, false, 0, false, false), "current exited, nothing new → retarget (falls back)");
        T.Check(AppAudioTarget.ShouldRetarget(0, false, 300, false, false), "nothing captured yet → take the new one");
        T.Check(!AppAudioTarget.ShouldRetarget(100, true, 200, true, true), "both sounding → keep (no needless reset)");
        T.Check(!AppAudioTarget.ShouldRetarget(100, true, 200, false, false), "new one is silent → keep");
        T.Check(AppAudioTarget.ShouldRetarget(100, true, 200, false, true), "sound moved to another tree → retarget");
        T.Check(!AppAudioTarget.ShouldRetarget(100, true, 0, false, false), "alive but not chosen now → keep");
    }

    public static void TestPlannerDecisions()
    {
        var p = new AppCapturePlanner();
        T.Check(p.Decide("", true, 0, 0).Mode == AppCaptureMode.AllSounds, "no app selected → all sounds");
        var d = p.Decide("Spotify", false, 30296, 0);
        T.Check(d.Mode == AppCaptureMode.EndpointGate && d.Why == AppCaptureWhy.Unsupported, "old Windows → endpoint + gate (unsupported)");
        d = p.Decide("Spotify", true, 0, 0);
        T.Check(d.Mode == AppCaptureMode.EndpointGate && d.Why == AppCaptureWhy.NotRunning, "not running → endpoint + gate (not running)");
        d = p.Decide("Spotify", true, 30296, 0);
        T.Check(d.Mode == AppCaptureMode.Process && d.Pid == 30296 && d.Why == AppCaptureWhy.None, "running on Windows 11 → capture that process");
    }

    public static void TestPlannerRetryBackoff()
    {
        var p = new AppCapturePlanner();
        T.Check(p.Decide("Spotify", true, 30296, 100).Mode == AppCaptureMode.Process, "first try");
        double wait = p.Failed(30296, 100);
        T.Check(wait == 30, $"first failure waits 30 s ({wait})");
        var d = p.Decide("Spotify", true, 30296, 110);
        T.Check(d.Mode == AppCaptureMode.EndpointGate && d.Why == AppCaptureWhy.Failed, "during the wait → endpoint + gate");
        T.Check(p.Decide("Spotify", true, 30296, 131).Mode == AppCaptureMode.Process, "after the wait → try again");
        T.Check(p.Failed(30296, 131) == 120, "second failure waits 2 min");
        T.Check(p.Failed(30296, 300) == 600, "third failure waits 10 min");
        T.Check(double.IsPositiveInfinity(p.Failed(30296, 1000)), "fourth failure: stop trying this process");
        T.Check(p.Decide("Spotify", true, 30296, 1e9).Why == AppCaptureWhy.Failed, "never retried for the same process");
        // アプリが起動し直した（別の PID）: すぐ試す
        d = p.Decide("Spotify", true, 4444, 1001);
        T.Check(d.Mode == AppCaptureMode.Process && d.Pid == 4444, "process restarted (new PID) → retry at once");
        T.Check(p.Failures == 0, "failure count cleared for the new process");
    }

    public static void TestPlannerHealthyAndSourceChange()
    {
        var p = new AppCapturePlanner();
        p.Decide("Spotify", true, 30296, 0);
        p.Failed(30296, 0);
        p.Failed(30296, 40);
        T.Check(p.Failures == 2, "two failures counted");
        p.Healthy(30296);
        T.Check(p.Failures == 0 && p.Decide("Spotify", true, 30296, 41).Mode == AppCaptureMode.Process, "healthy run resets the count");
        T.Check(p.Failed(30296, 41) == 30, "after a healthy run, the next failure waits 30 s again");
        p.Healthy(999);
        T.Check(p.Failures == 1, "healthy report for another process does not clear");
        // 選ぶアプリを変えた: 前のアプリの失敗は持ち越さない
        var d = p.Decide("chrome", true, 30296, 42);
        T.Check(d.Mode == AppCaptureMode.Process && p.Failures == 0, "changing the app clears failures");
        p.Failed(30296, 42);
        T.Check(p.Decide("", true, 0, 43).Mode == AppCaptureMode.AllSounds && p.Failures == 0, "clearing the app clears failures");
    }
}
