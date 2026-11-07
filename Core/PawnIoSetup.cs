// ==================================================
// PawnIoSetup.cs - ファン制御用ドライバー PawnIO の、アプリの中からのインストール・アンインストール
//   (winget に渡す引数・終わったあとの確認・結果の読み取り方と、利用者への案内の文)
// Dependencies: .NET 8 (MIT License), winget, PawnIO (https://pawnio.eu/ ・別途インストール)
// See CREDITS.txt for full license details.
// ==================================================

namespace ProFanController;

/// <summary>PawnIO のインストール・アンインストールの結果と、そのあとにしてほしいこと</summary>
/// <param name="Ok">目的を果たせた（入った・消えた・もともとそうだった）</param>
/// <param name="Message">画面に出す説明</param>
/// <param name="NeedsAppRestart">アプリを起動し直すと使えるようになる</param>
/// <param name="NeedsPcRestart">PC の再起動が要る</param>
/// <param name="OfferOfficialSite">公式サイト（手動のインストーラー）を案内する</param>
public sealed record PawnIoResult(bool Ok, string Message, bool NeedsAppRestart, bool NeedsPcRestart, bool OfferOfficialSite);

public static class PawnIoSetup
{
    /// <summary>winget のパッケージの名前</summary>
    public const string PackageId = "namazso.PawnIO";
    /// <summary>公式サイト（winget が使えないときは、ここからインストーラーを入手してもらう）</summary>
    public const string OfficialSite = "https://pawnio.eu/";

    /// <summary>
    /// インストールの引数。取得元は winget の公式の一覧（--source winget）に限る（Microsoft Store の取得元の同意や不調に左右されないように）。
    /// --silent で PawnIO のインストーラーの画面を出さない（PawnIO の定義では "-install -silent" が渡される）
    /// </summary>
    public const string InstallArguments =
        $"install --id {PackageId} -e --source winget --silent --accept-source-agreements --accept-package-agreements";

    /// <summary>アンインストールの引数（取得元は限らない：公式サイトのインストーラーで入れた PawnIO も対象にするため）</summary>
    public const string UninstallArguments =
        $"uninstall --id {PackageId} -e --silent --accept-source-agreements";

    /// <summary>インストール・アンインストールを待つ最長の時間（初めて winget を使う PC では、一覧の取得に時間がかかる）</summary>
    public static readonly TimeSpan Timeout = TimeSpan.FromMinutes(10);

    /// <summary>PawnIO がインストールされているか（アンインストールの情報が登録されているか）</summary>
    public static bool IsInstalled() => HardwareHub.IsPawnIoInstalled();

    /// <summary>
    /// インストールの結果を読み取る。
    /// detectedAfter: 終わったあとに PawnIO が見つかったか ／ loadedAtStartup: いま動いているアプリが、起動したときに PawnIO を使えていたか
    /// </summary>
    public static PawnIoResult InterpretInstall(WingetRun run, bool detectedAfter, bool loadedAtStartup)
    {
        var outcome = run.Outcome;
        string code = run.CodeText;
        // 起動したときに使えていたなら、起動し直す必要はない（もともと入っていた）
        bool restartApp = !loadedAtStartup;

        if (outcome == WingetOutcome.SuccessRebootRequired)
        {
            return new(true, L("PawnIO をインストールしました。仕上げに PC の再起動が必要です。PC を再起動すると、ファンを制御できるようになります。"),
                NeedsAppRestart: false, NeedsPcRestart: true, OfferOfficialSite: false);
        }
        if (detectedAfter && outcome is WingetOutcome.Success or WingetOutcome.AlreadyInstalled)
        {
            string head = outcome == WingetOutcome.Success ? L("PawnIO をインストールしました。") : L("PawnIO はすでにインストールされています。");
            return restartApp
                ? new(true, head + L("アプリを起動し直すと、ファンを制御できるようになります（起動し直しても、ファンの端子が見つからないときは PC を再起動してください）。"),
                    NeedsAppRestart: true, NeedsPcRestart: false, OfferOfficialSite: false)
                : new(true, head + L("このまま使えます。"), NeedsAppRestart: false, NeedsPcRestart: false, OfferOfficialSite: false);
        }
        if (detectedAfter)
        {
            // winget はエラーを返したが、PawnIO は入っている（途中まで進んだ・別の方法で入った など）
            return new(true, L($"winget はエラー（{code}）を返しましたが、PawnIO は見つかりました。アプリを起動し直してください。")
                             + L("それでもファンの端子が見つからないときは、PC を再起動するか、公式サイトのインストーラーで入れ直してください。"),
                NeedsAppRestart: restartApp, NeedsPcRestart: false, OfferOfficialSite: true);
        }
        if (outcome is WingetOutcome.Success or WingetOutcome.AlreadyInstalled)
        {
            return new(false, L("winget は「終わった」と返しましたが、PawnIO が見つかりません。PC を再起動してから、もう一度確かめてください。")
                              + L("それでも見つからないときは、公式サイトのインストーラーを使ってください。"),
                NeedsAppRestart: false, NeedsPcRestart: true, OfferOfficialSite: true);
        }
        string why = outcome switch
        {
            WingetOutcome.WingetMissing => L("winget（Windows の「アプリ インストーラー」）が見つからない・起動できないため、インストールできませんでした。")
                                           + L("Microsoft Store で「アプリ インストーラー」を入れる・更新するか、公式サイトのインストーラーを使ってください。"),
            WingetOutcome.TimedOut => L($"{Timeout.TotalMinutes:F0} 分たっても終わらなかったため、止めました。インターネットの接続を確かめて、もう一度試すか、公式サイトのインストーラーを使ってください。"),
            WingetOutcome.NetworkError => L($"ダウンロードに失敗しました（{code}）。インターネットにつながっているか確かめて、もう一度試してください。"),
            WingetOutcome.SourceError => L($"winget のパッケージの一覧を読めませんでした（{code}）。時間をおいてもう一度試すか、公式サイトのインストーラーを使ってください。"),
            WingetOutcome.PackageNotFound => L($"winget で PawnIO（{PackageId}）が見つかりませんでした（{code}）。公式サイトのインストーラーを使ってください。"),
            WingetOutcome.BlockedByPolicy => L($"この PC のポリシー（会社・学校の管理など）で、winget でのインストールが止められています（{code}）。管理している人に相談してください。"),
            WingetOutcome.AnotherInstallRunning => L($"ほかのソフトのインストールが動いています（{code}）。終わってから、もう一度試してください。"),
            WingetOutcome.InUse => L($"PawnIO が使われているため、入れられませんでした（{code}）。PawnIO を使うほかのソフト（FanControl・LibreHardwareMonitor など）を終了するか、PC を再起動してから試してください。"),
            WingetOutcome.RebootThenRetry => L($"PC の再起動が必要です（{code}）。PC を再起動してから、もう一度インストールしてください。"),
            WingetOutcome.Cancelled => L($"インストールが取り消されました（{code}）。"),
            WingetOutcome.WingetTooOld => L($"winget が古いため、インストールできませんでした（{code}）。Microsoft Store で「アプリ インストーラー」を更新するか、公式サイトのインストーラーを使ってください。"),
            WingetOutcome.RequiresAdmin => L($"管理者権限が必要です（{code}）。アプリを管理者として起動し直してから、もう一度試してください。"),
            _ => L($"インストールに失敗しました（winget のエラー {code}）。公式サイトのインストーラーを使ってください。"),
        };
        bool pcRestart = outcome is WingetOutcome.RebootThenRetry or WingetOutcome.InUse;
        bool offerSite = outcome is not (WingetOutcome.Cancelled or WingetOutcome.AnotherInstallRunning or WingetOutcome.BlockedByPolicy or WingetOutcome.RequiresAdmin);
        return new(false, why, NeedsAppRestart: false, NeedsPcRestart: pcRestart, OfferOfficialSite: offerSite);
    }

    /// <summary>手でアンインストールするときの案内</summary>
    public static string ManualUninstallHint => L("Windows の「設定」→「アプリ」→「インストールされているアプリ」から PawnIO をアンインストールしてください。");

    /// <summary>理由の文のあとに、手でアンインストールする方法をつなぐ（文の間の空白は言語に合わせる）</summary>
    private static string WithManualHint(string why) => Localization.JoinSentences(why, ManualUninstallHint);

    /// <summary>
    /// アンインストールの結果を読み取る。stillInstalled: 終わったあとも PawnIO のアンインストールの情報が残っているか
    /// </summary>
    public static PawnIoResult InterpretUninstall(WingetRun run, bool stillInstalled)
    {
        var outcome = run.WingetMissing ? WingetOutcome.WingetMissing
            : run.TimedOut ? WingetOutcome.TimedOut
            : Winget.ClassifyUninstall(run.ExitCode ?? -1);
        string code = run.CodeText;

        if (outcome == WingetOutcome.SuccessRebootRequired)
        {
            return new(true, L("PawnIO をアンインストールしました。仕上げに PC を再起動してください。"),
                NeedsAppRestart: false, NeedsPcRestart: true, OfferOfficialSite: false);
        }
        if (!stillInstalled && outcome is WingetOutcome.Success or WingetOutcome.NotInstalled)
        {
            return new(true, outcome == WingetOutcome.Success ? L("PawnIO をアンインストールしました。") : L("PawnIO はインストールされていませんでした。"),
                NeedsAppRestart: false, NeedsPcRestart: false, OfferOfficialSite: false);
        }
        if (!stillInstalled)
        {
            return new(true, L($"winget はエラー（{code}）を返しましたが、PawnIO はなくなりました。念のため PC を再起動してください。"),
                NeedsAppRestart: false, NeedsPcRestart: true, OfferOfficialSite: false);
        }
        if (outcome == WingetOutcome.Success)
        {
            return new(false, WithManualHint(L("winget は「終わった」と返しましたが、PawnIO がまだ残っています。PC を再起動してから確かめ、残っていれば ")),
                NeedsAppRestart: false, NeedsPcRestart: true, OfferOfficialSite: false);
        }
        string why = outcome switch
        {
            WingetOutcome.WingetMissing => WithManualHint(L("winget が見つからない・起動できないため、PawnIO をアンインストールできませんでした。")),
            WingetOutcome.TimedOut => WithManualHint(L($"{Timeout.TotalMinutes:F0} 分たっても終わらなかったため、止めました。")),
            WingetOutcome.InUse => WithManualHint(L($"PawnIO がほかのソフト（FanControl・LibreHardwareMonitor など）に使われているため、アンインストールできませんでした（{code}）。")
                                   + L("PC を再起動してから、")),
            WingetOutcome.NotInstalled => WithManualHint(L($"winget では PawnIO が見つかりませんでした（{code}）。")),
            WingetOutcome.BlockedByPolicy => L($"この PC のポリシーで止められています（{code}）。管理している人に相談してください。"),
            WingetOutcome.Cancelled => WithManualHint(L($"アンインストールが取り消されました（{code}）。")),
            WingetOutcome.AnotherInstallRunning => WithManualHint(L($"ほかのソフトのインストールが動いています（{code}）。終わってから、")),
            _ => WithManualHint(L($"PawnIO をアンインストールできませんでした（winget のエラー {code}）。")),
        };
        return new(false, why, NeedsAppRestart: false, NeedsPcRestart: outcome == WingetOutcome.InUse, OfferOfficialSite: false);
    }
}
