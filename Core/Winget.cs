// ==================================================
// Winget.cs - Windows の winget（パッケージ マネージャー）を画面を出さずに実行し、終了コードと出力を読み取る
//   (winget の場所の確認・実行・終了コードの分類・進み具合の表示用の文に直す)
// Dependencies: .NET 8 (MIT License), winget（Windows 標準の「アプリ インストーラー」に付属）
// See CREDITS.txt for full license details.
// ==================================================

using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;

namespace ProFanController;

/// <summary>winget の終了コードの意味（判断に使う大まかな種類）</summary>
public enum WingetOutcome
{
    /// <summary>成功</summary>
    Success,
    /// <summary>成功したが、仕上げに PC の再起動が要る</summary>
    SuccessRebootRequired,
    /// <summary>すでに入っている（入れ直す・更新するものがない）</summary>
    AlreadyInstalled,
    /// <summary>（アンインストールで）入っていない・見つからない</summary>
    NotInstalled,
    /// <summary>PC を再起動してから、もう一度やり直す必要がある</summary>
    RebootThenRetry,
    /// <summary>パッケージが見つからない（名前が変わった・取得元の一覧が古いなど）</summary>
    PackageNotFound,
    /// <summary>インターネットにつながらない・ダウンロードに失敗した</summary>
    NetworkError,
    /// <summary>取得元（winget のパッケージの一覧）を開けない・壊れている</summary>
    SourceError,
    /// <summary>組織のポリシー（グループ ポリシー）で止められている</summary>
    BlockedByPolicy,
    /// <summary>ほかのインストールが動いている</summary>
    AnotherInstallRunning,
    /// <summary>そのソフトがほかのソフトに使われている</summary>
    InUse,
    /// <summary>途中で取り消された</summary>
    Cancelled,
    /// <summary>winget が古くて、指定した引数を知らない</summary>
    WingetTooOld,
    /// <summary>管理者権限が要る</summary>
    RequiresAdmin,
    /// <summary>そのほかの失敗</summary>
    Failed,
    /// <summary>winget が見つからない・起動できない</summary>
    WingetMissing,
    /// <summary>時間内に終わらなかった（止めた）</summary>
    TimedOut,
}

/// <summary>winget を 1 回実行した結果</summary>
/// <param name="ExitCode">終了コード（起動できなかった・時間切れなら null）</param>
/// <param name="WingetMissing">winget が見つからない・起動できなかった</param>
/// <param name="TimedOut">時間内に終わらなかったので止めた</param>
/// <param name="Output">出力の最後のほう（進み具合の記号などを取り除いたもの。ログ用）</param>
/// <param name="Error">起動できなかったときの理由</param>
public sealed record WingetRun(int? ExitCode, bool WingetMissing, bool TimedOut, IReadOnlyList<string> Output, string? Error = null)
{
    public static WingetRun Missing(string? error = null) => new(null, true, false, Array.Empty<string>(), error);

    /// <summary>この実行の結果の種類</summary>
    public WingetOutcome Outcome =>
        WingetMissing ? WingetOutcome.WingetMissing
        : TimedOut ? WingetOutcome.TimedOut
        : Winget.Classify(ExitCode ?? -1);

    /// <summary>ログ・画面に出す終了コード（16 進数）</summary>
    public string CodeText => ExitCode is { } c ? Winget.CodeText(c) : "（なし）";
}

public static class Winget
{
    // ----- 終了コード（https://github.com/microsoft/winget-cli/blob/master/doc/windows/package-manager/winget/returnCodes.md） -----
    public const int Ok = 0;
    public static readonly int InternalError = unchecked((int)0x8A150001);
    public static readonly int InvalidArguments = unchecked((int)0x8A150002);
    public static readonly int CommandFailed = unchecked((int)0x8A150003);
    public static readonly int CtrlSignalReceived = unchecked((int)0x8A150005);
    public static readonly int DownloadFailed = unchecked((int)0x8A150008);
    public static readonly int SourcesInvalid = unchecked((int)0x8A15000B);
    public static readonly int SourceDataMissing = unchecked((int)0x8A15000F);
    public static readonly int NoApplicableInstaller = unchecked((int)0x8A150010);
    public static readonly int InstallerHashMismatch = unchecked((int)0x8A150011);
    public static readonly int NoApplicationsFound = unchecked((int)0x8A150014);
    public static readonly int NoSourcesDefined = unchecked((int)0x8A150015);
    public static readonly int MultipleApplicationsFound = unchecked((int)0x8A150016);
    public static readonly int NoManifestFound = unchecked((int)0x8A150017);
    public static readonly int CommandRequiresAdmin = unchecked((int)0x8A150019);
    public static readonly int UpdateNotApplicable = unchecked((int)0x8A15002B);
    public static readonly int DownloadSizeMismatch = unchecked((int)0x8A15002E);
    public static readonly int NoUninstallInfoFound = unchecked((int)0x8A15002F);
    public static readonly int ExecUninstallCommandFailed = unchecked((int)0x8A150030);
    public static readonly int BlockedByPolicy = unchecked((int)0x8A15003A);
    public static readonly int SourceDataIntegrityFailure = unchecked((int)0x8A15003F);
    public static readonly int StreamReadFailure = unchecked((int)0x8A150040);
    public static readonly int PackageAgreementsNotAccepted = unchecked((int)0x8A150041);
    public static readonly int SourceOpenFailed = unchecked((int)0x8A150045);
    public static readonly int SourceAgreementsNotAccepted = unchecked((int)0x8A150046);
    public static readonly int FailedToOpenAllSources = unchecked((int)0x8A15004B);
    public static readonly int UpgradeVersionNotNewer = unchecked((int)0x8A15004F);
    public static readonly int PackageAlreadyInstalled = unchecked((int)0x8A150061);
    public static readonly int DownloadDependencies = unchecked((int)0x8A15006B);
    public static readonly int ServiceUnavailable = unchecked((int)0x8A15006D);
    public static readonly int ZeroByteInstaller = unchecked((int)0x8A150086);
    public static readonly int InstallPackageInUse = unchecked((int)0x8A150101);
    public static readonly int InstallInProgress = unchecked((int)0x8A150102);
    public static readonly int InstallFileInUse = unchecked((int)0x8A150103);
    public static readonly int InstallNoNetwork = unchecked((int)0x8A150107);
    public static readonly int InstallRebootRequiredToFinish = unchecked((int)0x8A150109);
    public static readonly int InstallRebootRequiredForInstall = unchecked((int)0x8A15010A);
    public static readonly int InstallRebootInitiated = unchecked((int)0x8A15010B);
    public static readonly int InstallCancelledByUser = unchecked((int)0x8A15010C);
    public static readonly int InstallAlreadyInstalled = unchecked((int)0x8A15010D);
    public static readonly int InstallBlockedByPolicy = unchecked((int)0x8A15010F);
    public static readonly int InstallPackageInUseByApplication = unchecked((int)0x8A150111);
    /// <summary>インストーラーそのものが返す「成功したが再起動が要る」（Windows の標準のコード）</summary>
    public const int SuccessRebootRequired = 3010;
    /// <summary>インストーラーそのものが返す「再起動を始めた」</summary>
    public const int SuccessRebootInitiated = 1641;

    /// <summary>終了コードを、判断に使う種類に分ける（知らないコードは Failed）</summary>
    public static WingetOutcome Classify(int code)
    {
        if (code == Ok) return WingetOutcome.Success;
        if (code == SuccessRebootRequired || code == SuccessRebootInitiated
            || code == InstallRebootRequiredToFinish || code == InstallRebootInitiated) return WingetOutcome.SuccessRebootRequired;
        if (code == UpdateNotApplicable || code == PackageAlreadyInstalled || code == InstallAlreadyInstalled
            || code == UpgradeVersionNotNewer) return WingetOutcome.AlreadyInstalled;
        if (code == InstallRebootRequiredForInstall) return WingetOutcome.RebootThenRetry;
        if (code == NoApplicationsFound || code == NoManifestFound || code == MultipleApplicationsFound
            || code == NoApplicableInstaller) return WingetOutcome.PackageNotFound;
        if (code == DownloadFailed || code == InstallNoNetwork || code == ServiceUnavailable || code == DownloadSizeMismatch
            || code == StreamReadFailure || code == ZeroByteInstaller || code == DownloadDependencies
            || code == InstallerHashMismatch) return WingetOutcome.NetworkError;
        if (code == SourcesInvalid || code == SourceDataMissing || code == NoSourcesDefined || code == SourceOpenFailed
            || code == SourceAgreementsNotAccepted || code == FailedToOpenAllSources || code == SourceDataIntegrityFailure
            || code == PackageAgreementsNotAccepted) return WingetOutcome.SourceError;
        if (code == BlockedByPolicy || code == InstallBlockedByPolicy) return WingetOutcome.BlockedByPolicy;
        if (code == InstallInProgress) return WingetOutcome.AnotherInstallRunning;
        if (code == InstallPackageInUse || code == InstallFileInUse || code == InstallPackageInUseByApplication) return WingetOutcome.InUse;
        if (code == InstallCancelledByUser || code == CtrlSignalReceived) return WingetOutcome.Cancelled;
        if (code == InvalidArguments) return WingetOutcome.WingetTooOld;
        if (code == CommandRequiresAdmin) return WingetOutcome.RequiresAdmin;
        return WingetOutcome.Failed;
    }

    /// <summary>
    /// アンインストールの終了コードを分ける。「見つからない」は「入っていない」と読む
    /// （winget はインストールされていないパッケージのアンインストールに NO_APPLICATIONS_FOUND を返すため）
    /// </summary>
    public static WingetOutcome ClassifyUninstall(int code)
    {
        if (code == NoApplicationsFound || code == NoManifestFound) return WingetOutcome.NotInstalled;
        if (code == NoUninstallInfoFound || code == ExecUninstallCommandFailed) return WingetOutcome.Failed;
        return Classify(code);
    }

    /// <summary>終了コードの 16 進数の表記（winget のエラーは 0x8A15xxxx。インストーラーのコードは 10 進数も添える）</summary>
    public static string CodeText(int code) =>
        code is >= 0 and < 0x10000 ? $"{code}" : $"0x{unchecked((uint)code):X8}";

    // ----- 場所 -----

    /// <summary>
    /// winget の場所の候補。winget は「アプリ実行エイリアス」として、ユーザーごとの
    /// %LOCALAPPDATA%\Microsoft\WindowsApps\winget.exe に置かれる。
    /// 名前だけ（"winget.exe"）で起動すると、Windows はまずこのアプリのフォルダーを探すので、
    /// 管理者権限で動くこのアプリでは、そこに置かれた同じ名前の偽物が動くおそれがある。必ず完全なパスで起動する
    /// </summary>
    public static IEnumerable<string> Candidates(string? localAppData)
    {
        if (!string.IsNullOrEmpty(localAppData))
            yield return Path.Combine(localAppData, "Microsoft", "WindowsApps", "winget.exe");
    }

    /// <summary>winget の場所（見つからなければ null）</summary>
    public static string? Locate()
    {
        foreach (var path in Candidates(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData)))
        {
            try { if (File.Exists(path)) return path; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* 次の候補へ */ }
        }
        return null;
    }

    // ----- 実行 -----

    /// <summary>ログに残す出力の行数（最後のほう）</summary>
    private const int KeepLines = 60;

    /// <summary>
    /// winget を画面を出さずに実行し、終わるまで待つ（timeout を過ぎたら止める）。
    /// 出力の 1 行ごとに onLine を呼ぶ（別のスレッドから。進み具合の記号は取り除いてある・空の行は渡さない）。
    /// 入力は閉じておく（確認の質問が出ても、待ち続けずに終わるように）
    /// </summary>
    public static async Task<WingetRun> RunAsync(string arguments, Action<string>? onLine, TimeSpan timeout, CancellationToken cancel = default)
    {
        string? exe = Locate();
        if (exe is null) return WingetRun.Missing("winget.exe が見つかりません");

        var psi = new ProcessStartInfo(exe, arguments)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            // winget は出力先がファイルやパイプのとき UTF-8 で書く
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            WorkingDirectory = Environment.SystemDirectory,
        };
        var lines = new Queue<string>();
        void Receive(string? data)
        {
            if (data is null) return;
            string clean = CleanLine(data);
            if (clean.Length == 0) return;
            lock (lines)
            {
                // 同じ行（進み具合の描き直し）は 1 回だけ残す
                if (lines.Count == 0 || lines.Last() != clean)
                {
                    lines.Enqueue(clean);
                    while (lines.Count > KeepLines) lines.Dequeue();
                }
            }
            try { onLine?.Invoke(clean); } catch { /* 表示の失敗で止めない */ }
        }

        using var p = new Process { StartInfo = psi };
        p.OutputDataReceived += (_, e) => Receive(e.Data);
        p.ErrorDataReceived += (_, e) => Receive(e.Data);
        try
        {
            if (!p.Start()) return WingetRun.Missing("winget を起動できませんでした");
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or IOException)
        {
            // アプリ インストーラーが壊れている（エイリアスはあるが中身がない）ときもここに来る
            return WingetRun.Missing(ex.Message);
        }
        try { p.StandardInput.Close(); } catch { /* 入力を閉じられなくても続ける */ }
        p.BeginOutputReadLine();
        p.BeginErrorReadLine();

        using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancel);
        limit.CancelAfter(timeout);
        try
        {
            await p.WaitForExitAsync(limit.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try { p.Kill(entireProcessTree: true); } catch { /* もう終わっている */ }
            lock (lines) return new WingetRun(null, false, true, lines.ToList(), cancel.IsCancellationRequested ? "取り消しました" : "時間切れ");
        }
        lock (lines) return new WingetRun(p.ExitCode, false, false, lines.ToList());
    }

    // ----- 出力の読み取り（表示用。判断には使わない：winget の文は Windows の表示言語で変わるため） -----

    private static readonly Regex Escapes = new(@"\x1B\[[0-9;?]*[ -/]*[@-~]|\x1B\][^\x07\x1B]*(\x07|\x1B\\)|\x1B[@-Z\\-_]", RegexOptions.Compiled);
    /// <summary>進み具合のバー（ブロックの文字）</summary>
    private static readonly Regex Bars = new(@"[▀-▟]+", RegexOptions.Compiled);
    /// <summary>くるくる回る記号だけの行</summary>
    private static readonly Regex SpinnerOnly = new(@"^[\s\-\\|/]*$", RegexOptions.Compiled);
    private static readonly Regex Spaces = new(@"\s{2,}", RegexOptions.Compiled);
    private static readonly Regex Size = new(@"(\d+(?:[.,]\d+)?\s*[KMG]?i?B)\s*/\s*(\d+(?:[.,]\d+)?\s*[KMG]?i?B)", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex Percent = new(@"(?<!\d)(\d{1,3})\s*%", RegexOptions.Compiled);

    /// <summary>出力の 1 行から、色の指定・進み具合のバー・くるくる回る記号を取り除く（何も残らなければ空文字）</summary>
    public static string CleanLine(string raw)
    {
        string s = Escapes.Replace(raw, "");
        s = Bars.Replace(s, " ");
        s = new string(s.Where(c => !char.IsControl(c)).ToArray());
        if (SpinnerOnly.IsMatch(s)) return "";
        return Spaces.Replace(s, " ").Trim();
    }

    /// <summary>
    /// 出力の 1 行（CleanLine 済み）を、画面に出す短い文にする。表示しなくてよい行は null。
    /// 英語と日本語の表示の両方を見るが、当てはまらなくても困らない（そのときは行をそのまま短くして出す）
    /// </summary>
    public static string? Describe(string line)
    {
        // 文字化けした行（読み取りの文字コードが合わなかった）は出さない
        if (line.Length == 0 || line.Contains('�')) return null;
        if (Size.Match(line) is { Success: true } m) return $"ダウンロードしています… {m.Groups[1].Value} / {m.Groups[2].Value}";
        bool Has(params string[] words) => words.Any(w => line.Contains(w, StringComparison.OrdinalIgnoreCase));
        if (Has("Successfully uninstalled", "正常にアンインストール", "アンインストールが完了")) return "アンインストールが終わりました。確認しています…";
        if (Has("Successfully installed", "正常にインストール", "インストールが完了")) return "インストールが終わりました。確認しています…";
        if (Has("Starting package uninstall", "アンインストールを開始")) return "アンインストールしています…（少し時間がかかります）";
        if (Has("Starting package install", "インストールを開始")) return "インストールしています…（少し時間がかかります）";
        if (Has("hash", "ハッシュ")) return "ダウンロードしたファイルを確かめています…";
        if (Has("Downloading", "ダウンロード")) return "ダウンロードしています…";
        if (Percent.Match(line) is { Success: true } pm && line.Length <= 12) return $"進めています… {pm.Groups[1].Value}%";
        if (!line.Any(char.IsLetter)) return null;
        return "winget: " + (line.Length > 120 ? line[..117] + "…" : line);
    }
}
