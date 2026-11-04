// ==================================================
// ProfileSwitcher.cs - 実行中のアプリを監視して、ルールに一致したプロファイルを通知する
// Dependencies: .NET 8 (MIT License)
// See CREDITS.txt for full license details.
// ==================================================

using System.Diagnostics;

namespace ProFanController;

public sealed class ProfileSwitcher : IDisposable
{
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(3);

    private readonly object _gate = new();
    private Timer? _timer;
    private List<(string Process, string Profile)> _rules = new();
    private (string Process, string Profile)? _current;

    /// <summary>一致するルールが変わったときに発生（null は一致なし）。スレッドプールで発生する</summary>
    public event Action<string?, string?>? MatchChanged;

    public void Start() => _timer = new Timer(_ => Poll(), null, TimeSpan.FromSeconds(1), Interval);

    /// <summary>設定画面で編集されたルールを取り込む（上にあるルールほど優先）</summary>
    public void UpdateRules(IEnumerable<AutoSwitchRule> rules)
    {
        lock (_gate)
        {
            _rules = rules
                .Where(r => !string.IsNullOrWhiteSpace(r.ProcessName))
                .Select(r => (Normalize(r.ProcessName), r.ProfileName))
                .ToList();
        }
    }

    public static string Normalize(string processName)
    {
        string name = processName.Trim();
        return name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? name[..^4] : name;
    }

    /// <summary>ウィンドウを持つ実行中アプリの一覧（ルール追加時の候補）</summary>
    public static List<string> RunningApps()
    {
        var names = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in Process.GetProcesses())
        {
            try
            {
                if (p.MainWindowHandle != IntPtr.Zero) names.Add(p.ProcessName);
            }
            catch
            {
                // 権限のないプロセスは無視
            }
            finally
            {
                p.Dispose();
            }
        }
        return names.ToList();
    }

    private void Poll()
    {
        try
        {
            List<(string Process, string Profile)> rules;
            lock (_gate) rules = _rules;

            (string Process, string Profile)? match = null;
            if (rules.Count > 0)
            {
                var running = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var p in Process.GetProcesses())
                {
                    running.Add(p.ProcessName);
                    p.Dispose();
                }
                foreach (var rule in rules)
                {
                    if (!running.Contains(rule.Process)) continue;
                    match = rule;
                    break;
                }
            }

            if (match != _current)
            {
                _current = match;
                MatchChanged?.Invoke(match?.Process, match?.Profile);
            }
        }
        catch (Exception ex)
        {
            Logger.Warn($"プロセス監視に失敗: {ex.Message}");
        }
    }

    public void Dispose() => _timer?.Dispose();
}
