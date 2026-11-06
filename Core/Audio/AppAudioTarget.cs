// ==================================================
// AppAudioTarget.cs - 「特定のアプリだけ」の音の取り込み方を決める (どのプロセスの音を直接取り込むか・
//                     取り込めないときに全体の音＋アプリの割合に戻すか・いつ試し直すか。画面や Windows の音の仕組みは使わない)
// Dependencies: .NET 8 (MIT License)
// See CREDITS.txt for full license details.
// ==================================================

namespace ProFanController;

/// <summary>プロセスの一覧の 1 行（PID・親の PID・名前。名前は拡張子 .exe を除いたもの）</summary>
public readonly record struct ProcEntry(int Pid, int ParentPid, string Name);

/// <summary>アプリの音のセッションの 1 つ（持ち主の PID・再生中か・いまの音の大きさ 0〜1）</summary>
public readonly record struct SessionEntry(int Pid, bool Active, float Peak);

/// <summary>「特定のアプリだけ」の取り込み方</summary>
public enum AppCaptureMode
{
    /// <summary>アプリを選んでいない: 出力先の音をすべて取り込む</summary>
    AllSounds,
    /// <summary>選んだアプリ（とその子プロセス）の音だけを直接取り込む</summary>
    Process,
    /// <summary>出力先の音をすべて取り込み、そのうちアプリの音の割合を反応の強さに掛ける（以前からの方式）</summary>
    EndpointGate,
}

/// <summary>以前からの方式（EndpointGate）になった理由</summary>
public enum AppCaptureWhy
{
    None,
    /// <summary>この Windows はアプリの音だけを取り込む機能がない（Windows 10 ビルド 20348 より前）</summary>
    Unsupported,
    /// <summary>選んだアプリが起動していない</summary>
    NotRunning,
    /// <summary>直接の取り込みに失敗した（試し直すまでの間）</summary>
    Failed,
}

/// <summary>取り込み方の決定（モード・取り込むプロセスの PID・理由）</summary>
public readonly record struct AppCaptureDecision(AppCaptureMode Mode, int Pid, AppCaptureWhy Why);

/// <summary>
/// 選んだアプリのどのプロセスの音を取り込むかを決める。
/// アプリは複数のプロセスで動いていることが多い（Spotify は 8 つ。最初のプロセスがほかのプロセスを起動する）。
/// 子プロセスもまとめて取り込めるので、音を出しているプロセスの、同じ名前の親をたどった一番上（もとのプロセス）を選ぶ
/// </summary>
public static class AppAudioTarget
{
    /// <summary>アプリの音だけを取り込める最初の Windows のビルド（Windows 10 ビルド 20348・Windows 11 はすべて）</summary>
    public const int ProcessLoopbackMinBuild = 20348;
    /// <summary>これより大きければ「音が出ている」とみなす（音量メーターの値）</summary>
    public const float SoundingPeak = 0.001f;

    /// <summary>この Windows でアプリの音だけを取り込めるか</summary>
    public static bool Supports(Version os) => os.Major > 10 || (os.Major == 10 && os.Build >= ProcessLoopbackMinBuild);

    /// <summary>設定のアプリ名をプロセス名にそろえる（前後の空白・拡張子 .exe を除く）</summary>
    public static string NormalizeName(string? source)
    {
        string s = (source ?? "").Trim();
        if (s.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) s = s[..^4].TrimEnd();
        return s;
    }

    /// <summary>
    /// 取り込むプロセスの PID（見つからなければ 0）。
    ///   1. 選んだ名前のプロセスが音のセッションを持っていれば、音が出ている → 再生中 → それ以外の順に一番よいもの
    ///   2. セッションがなければ、同じ名前のプロセスをいちばん多く子に持つもの
    /// のどちらかから、同じ名前の親をたどった一番上を返す（子プロセスの音もまとめて取り込めるように）
    /// </summary>
    public static int ChooseTarget(IReadOnlyList<ProcEntry> procs, string name, IReadOnlyList<SessionEntry> sessions)
    {
        name = NormalizeName(name);
        if (name.Length == 0) return 0;
        var parents = Candidates(procs, name);
        if (parents.Count == 0) return 0;

        int best = 0, bestRank = -1;
        float bestPeak = -1;
        foreach (var s in sessions)
        {
            if (!parents.ContainsKey(s.Pid)) continue;
            int rank = s.Peak > SoundingPeak ? 2 : s.Active ? 1 : 0;
            if (rank > bestRank || (rank == bestRank && s.Peak > bestPeak))
            {
                best = s.Pid;
                bestRank = rank;
                bestPeak = s.Peak;
            }
        }
        if (best != 0) return RootOf(parents, best);

        // セッションがない（まだ音を出していない）: 同じ名前のプロセスがいちばん多い木の一番上（同じ数なら PID の小さいほう）
        var size = new Dictionary<int, int>();
        foreach (int pid in parents.Keys)
        {
            int root = RootOf(parents, pid);
            size[root] = size.TryGetValue(root, out int n) ? n + 1 : 1;
        }
        int chosen = 0, chosenSize = 0;
        foreach (var (root, n) in size)
        {
            if (n > chosenSize || (n == chosenSize && root < chosen))
            {
                chosen = root;
                chosenSize = n;
            }
        }
        return chosen;
    }

    /// <summary>pid が root か、その子孫（同じ名前のプロセスの中で）か</summary>
    public static bool InTree(IReadOnlyList<ProcEntry> procs, string name, int root, int pid)
    {
        if (root == 0 || pid == 0) return false;
        var parents = Candidates(procs, NormalizeName(name));
        return parents.ContainsKey(pid) && RootOf(parents, pid) == root;
    }

    /// <summary>取り込んでいるプロセスの木で、音が出ているセッションがあるか</summary>
    public static bool TreeSounding(IReadOnlyList<ProcEntry> procs, string name, int root, IReadOnlyList<SessionEntry> sessions)
    {
        foreach (var s in sessions)
        {
            if ((s.Active || s.Peak > SoundingPeak) && InTree(procs, name, root, s.Pid)) return true;
        }
        return false;
    }

    /// <summary>
    /// 取り込むプロセスを変えるか。今のプロセスが終わった・見つからないときは変える。
    /// 生きているときは、新しい候補が音を出していて、今のプロセスの木からは音が出ていないときだけ変える
    /// （取り込み直すとテンポの記録が最初からになるので、むやみに変えない）
    /// </summary>
    public static bool ShouldRetarget(int current, bool currentAlive, int desired, bool currentSounding, bool desiredSounding)
    {
        if (desired == current) return false;
        if (current == 0 || !currentAlive) return true;
        return desired != 0 && desiredSounding && !currentSounding;
    }

    /// <summary>選んだ名前のプロセス（PID → 親の PID）</summary>
    private static Dictionary<int, int> Candidates(IReadOnlyList<ProcEntry> procs, string name)
    {
        var parents = new Dictionary<int, int>();
        foreach (var p in procs)
        {
            if (p.Pid != 0 && string.Equals(NormalizeName(p.Name), name, StringComparison.OrdinalIgnoreCase)) parents[p.Pid] = p.ParentPid;
        }
        return parents;
    }

    /// <summary>同じ名前の親をたどった一番上（親の PID が使い回されて輪になっていても止まる）</summary>
    private static int RootOf(Dictionary<int, int> parents, int pid)
    {
        int cur = pid;
        for (int i = 0; i < 64; i++)
        {
            int parent = parents[cur];
            if (parent == cur || parent == pid || !parents.ContainsKey(parent)) break;
            cur = parent;
        }
        return cur;
    }
}

/// <summary>
/// 「特定のアプリだけ」で、直接の取り込みを試すか、以前からの方式に戻すかを決める。
/// 失敗したプロセスは、30 秒 → 2 分 → 10 分あけて試し直し、それでもだめならアプリが起動し直すまで試さない
/// （戻すたびに取り込み直してテンポの記録が最初からになるので、何度も繰り返さない）
/// </summary>
public sealed class AppCapturePlanner
{
    /// <summary>失敗の回数ごとの、試し直すまでの時間（秒）。これを超えたら、そのプロセスでは試さない</summary>
    public static readonly double[] RetryDelays = { 30, 120, 600 };
    /// <summary>これだけ問題なく取り込めたら、失敗の回数を数え直す（秒）</summary>
    public const double HealthyAfter = 60;

    private string _source = "";
    private int _failedPid;
    private int _failures;
    private double _retryAt = double.NegativeInfinity;

    /// <summary>失敗が続いているプロセス（なければ 0）</summary>
    public int FailedPid => _failedPid;
    /// <summary>そのプロセスで続けて失敗した回数</summary>
    public int Failures => _failures;
    /// <summary>次に試せる時刻（もう試さないときは +∞）</summary>
    public double RetryAt => _retryAt;

    /// <summary>取り込み方を決める（source は NormalizeName 済みのアプリ名、targetPid は ChooseTarget の結果）</summary>
    public AppCaptureDecision Decide(string source, bool supported, int targetPid, double now)
    {
        if (!string.Equals(source, _source, StringComparison.OrdinalIgnoreCase))
        {
            // 選ぶアプリが変わった: 前のアプリの失敗は持ち越さない
            _source = source;
            Clear();
        }
        if (source.Length == 0) return new(AppCaptureMode.AllSounds, 0, AppCaptureWhy.None);
        if (!supported) return new(AppCaptureMode.EndpointGate, 0, AppCaptureWhy.Unsupported);
        if (targetPid == 0) return new(AppCaptureMode.EndpointGate, 0, AppCaptureWhy.NotRunning);
        if (targetPid != _failedPid) Clear(); // アプリが起動し直した（別のプロセスになった）ので、すぐ試す
        if (_failures > 0 && now < _retryAt) return new(AppCaptureMode.EndpointGate, targetPid, AppCaptureWhy.Failed);
        return new(AppCaptureMode.Process, targetPid, AppCaptureWhy.None);
    }

    /// <summary>直接の取り込みに失敗した（始められない・取り込めていない）。次に試せるまでの秒数を返す（もう試さないなら +∞）</summary>
    public double Failed(int pid, double now)
    {
        if (pid != _failedPid)
        {
            _failedPid = pid;
            _failures = 0;
        }
        _failures++;
        double wait = _failures <= RetryDelays.Length ? RetryDelays[_failures - 1] : double.PositiveInfinity;
        _retryAt = now + wait;
        return wait;
    }

    /// <summary>しばらく問題なく取り込めた（失敗の回数を数え直す）</summary>
    public void Healthy(int pid)
    {
        if (pid == _failedPid) Clear();
    }

    private void Clear()
    {
        _failedPid = 0;
        _failures = 0;
        _retryAt = double.NegativeInfinity;
    }
}
