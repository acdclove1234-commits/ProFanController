# ==================================================
# update.ps1 - ProFanController（配布版）をワンクリックで更新する / one-click updater for the release folder
#   1. ダウンロードフォルダから、ProFanController.exe が入った zip を探す（名前に ProFan を含むもの。新しい順）
#   2. 別の一時フォルダに展開して中身を確かめる（exe の版・製品名。版は日付ではなく exe に書かれた版で比べる）
#   3. 公式の配布物か確かめる（インターネットにつながれば、GitHub のリリースの SHA-256 と zip を比べる）
#   4. 動いているアプリを正しく終了させる（ファンを BIOS 制御に戻してから閉じる）
#   5. 今のファイルを backups フォルダに退避してから、決まったファイルだけを差し替える
#      （exe・説明書・更新用のファイル・ライセンス。設定 config.json・ログ・記録・画像には触れない）
#   6. 新しい版を起動し、すぐに終わってしまったら、退避したファイルに戻して前の版を起動し直す
# 「2_更新.bat」から管理者権限で実行される
# 表示は Windows の表示言語が日本語なら日本語、それ以外は英語（T 日本語 英語）
# 引数（試験用）: -InstallDir 入れ替える先、-DownloadsDir zip を探す場所、-Yes 確認を飛ばす、-NoStart 起動しない、-Offline 公式との照合をしない
# ==================================================
param(
    [string]$InstallDir = $PSScriptRoot,
    [string]$DownloadsDir = (Join-Path $env:USERPROFILE 'Downloads'),
    [switch]$Yes,
    [switch]$NoStart,
    [switch]$Offline
)
$ErrorActionPreference = 'Stop'
$dir = $InstallDir
$exe = Join-Path $dir 'ProFanController.exe'
$repo = 'acdclove1234-commits/ProFanController'
# 差し替えてよいファイル（ここにないものは、zip に入っていても使わない）
$allowedFiles = @('ProFanController.exe', 'はじめにお読みください.txt', 'README_en.txt', 'update.ps1', '1_PawnIO をインストール.bat', '2_更新.bat')
$allowedDirs = @('licenses')

# 表示言語は「Windows の表示言語」で決める。Get-UICulture は、コンソールのコードページが日本語に対応していないと
# en-US に置き換わるため使わない
$isJa = $false
try { $isJa = "$((Get-WinUserLanguageList)[0].LanguageTag)" -like 'ja*' }
catch { $isJa = [Globalization.CultureInfo]::InstalledUICulture.Name -like 'ja*' }
function T([string]$ja, [string]$en) { if ($isJa) { return $ja } else { return $en } }
function Pause-Exit($code) { if (-not $Yes) { Write-Host ''; Read-Host (T 'Enter キーで閉じます' 'Press Enter to close') | Out-Null }; exit $code }
function Ask($question) { if ($Yes) { return $true }; return (Read-Host "$question (y/N)") -eq 'y' }
function Get-Version($path) {
    $v = (Get-Item $path).VersionInfo
    try { return [version]($v.FileVersion -replace '[^0-9.]', '') } catch { return $null }
}

$tmp = Join-Path $env:TEMP ("ProFanController_update_" + [Guid]::NewGuid().ToString('N'))
$backup = $null
try {
    Write-Host (T '=== ProFanController の更新 ===' '=== Updating ProFanController ===') -ForegroundColor Cyan
    if (-not (Test-Path $exe)) { Write-Host (T "更新する ProFanController.exe が見つかりません: $exe" "ProFanController.exe to update was not found: $exe") -ForegroundColor Red; Pause-Exit 1 }
    $curVer = Get-Version $exe

    # 1〜2. zip を探して、別の一時フォルダに展開して確かめる
    $zip = $null; $newExe = $null; $newDir = $null
    $zips = Get-ChildItem (Join-Path $DownloadsDir '*.zip') -ErrorAction SilentlyContinue |
            Where-Object { $_.Name -like '*ProFan*' } | Sort-Object LastWriteTime -Descending
    foreach ($z in $zips) {
        Remove-Item -Recurse -Force $tmp -ErrorAction SilentlyContinue
        New-Item -ItemType Directory -Force $tmp | Out-Null
        try { Expand-Archive $z.FullName -DestinationPath $tmp -Force } catch { continue }
        $found = Get-ChildItem $tmp -Recurse -Filter 'ProFanController.exe' | Select-Object -First 1
        if ($found -and $found.VersionInfo.ProductName -like '*ProFanController*') { $zip = $z; $newExe = $found; $newDir = $found.DirectoryName; break }
    }
    if (-not $newExe) {
        Write-Host (T "「$DownloadsDir」に、ProFanController.exe が入った zip が見つかりません。" "No zip containing ProFanController.exe was found in `"$DownloadsDir`".") -ForegroundColor Yellow
        Write-Host (T '受け取った zip（展開しないまま）をダウンロードフォルダに保存してから、もう一度実行してください。' 'Save the new zip (still zipped) to your Downloads folder, then run this again.')
        Pause-Exit 1
    }
    $newVer = Get-Version $newExe.FullName
    Write-Host (T "更新ファイル: $($zip.FullName)" "Update file: $($zip.FullName)")
    Write-Host (T "  今の版: $curVer → 新しい版: $newVer" "  Current version: $curVer -> new version: $newVer")
    if ($null -ne $newVer -and $null -ne $curVer -and $newVer -le $curVer) {
        Write-Host (T 'zip の版は、今の版と同じか古いです。' 'The version in the zip is the same as, or older than, the current one.') -ForegroundColor Yellow
        if (-not (Ask (T 'それでも上書きしますか？' 'Overwrite anyway?'))) { Pause-Exit 0 }
    }

    # 3. 公式の配布物か（GitHub のリリースにある zip の SHA-256 と比べる）
    $hash = (Get-FileHash $zip.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
    $verified = $false
    if (-not $Offline -and $null -ne $newVer) {
        try {
            [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
            $tag = "v$($newVer.Major).$($newVer.Minor).$($newVer.Build)"
            $rel = Invoke-RestMethod "https://api.github.com/repos/$repo/releases/tags/$tag" -Headers @{ 'User-Agent' = 'ProFanController-Updater' } -TimeoutSec 15
            $digests = @($rel.assets | ForEach-Object { "$($_.digest)" -replace '^sha256:', '' } | Where-Object { $_ })
            if ($digests -contains $hash) { $verified = $true; Write-Host (T "  公式のリリース $tag の zip と一致しました（SHA-256）。" "  Matches the zip of the official release $tag (SHA-256).") -ForegroundColor Green }
            elseif ($digests.Count -gt 0) { Write-Host (T "  注意: 公式のリリース $tag の zip と中身が違います（作り直した zip か、別の人が作った zip です）。" "  Warning: this zip differs from the official release $tag (it was rebuilt, or made by someone else).") -ForegroundColor Yellow }
            else { Write-Host (T "  公式のリリース $tag には照合用の値がありませんでした。" "  The official release $tag has no checksum to compare with.") -ForegroundColor Yellow }
        }
        catch { Write-Host (T "  公式のリリースと照合できませんでした（インターネットにつながっていない・リリースがまだない など）: $($_.Exception.Message)" "  Could not compare with the official release (no internet connection, or no release yet): $($_.Exception.Message)") -ForegroundColor Yellow }
    }
    Write-Host (T "  zip の SHA-256: $hash" "  SHA-256 of the zip: $hash")
    if (-not $verified -and -not (Ask (T '公式の配布物と確かめられませんでした。信頼できる人から受け取った zip なら、続けますか？' 'This zip could not be confirmed as an official release. Continue only if you got it from someone you trust. Continue?'))) { Pause-Exit 0 }

    # 4. この場所で動いているアプリだけに、終了の合図を送る
    $running = @(Get-Process ProFanController -ErrorAction SilentlyContinue | Where-Object { try { $_.Path -eq $exe } catch { $false } })
    if ($running.Count -gt 0) {
        Write-Host (T 'ProFanController を終了しています（ファンを BIOS 制御に戻します）...' 'Closing ProFanController (the fans go back to BIOS control)...')
        try { [System.Threading.EventWaitHandle]::OpenExisting('Global\ProFanController.Exit').Set() | Out-Null }
        catch { Write-Host (T '終了の合図を送れませんでした。トレイのアイコンを右クリック →「終了」してください。' 'Could not ask the app to exit. Right-click the tray icon and choose Exit [終了].') -ForegroundColor Yellow }
        for ($i = 0; $i -lt 60 -and ($running | Where-Object { -not $_.HasExited }); $i++) { Start-Sleep -Milliseconds 500; $running | ForEach-Object { $_.Refresh() } }
        if ($running | Where-Object { -not $_.HasExited }) {
            Write-Host (T 'まだ動いています。トレイのアイコンを右クリック →「終了」してから、もう一度実行してください。' 'The app is still running. Right-click the tray icon, choose Exit [終了], then run this again.') -ForegroundColor Red
            Pause-Exit 1
        }
    }

    # 5. 退避してから、決まったファイルだけを差し替える
    $stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
    $backup = Join-Path $dir "backups\update-$stamp-from-$curVer"
    New-Item -ItemType Directory -Force $backup | Out-Null
    foreach ($f in $allowedFiles) { $p = Join-Path $dir $f; if (Test-Path $p) { Copy-Item $p $backup -Force } }
    foreach ($d in $allowedDirs) { $p = Join-Path $dir $d; if (Test-Path $p) { Copy-Item $p (Join-Path $backup $d) -Recurse -Force } }
    Write-Host (T "今のファイルを退避しました: $backup" "Backed up the current files: $backup")

    Write-Host (T 'ファイルを差し替えています...' 'Replacing files...')
    try {
        foreach ($f in $allowedFiles) { $p = Join-Path $newDir $f; if (Test-Path $p) { Copy-Item $p $dir -Force } }
        foreach ($d in $allowedDirs) {
            $p = Join-Path $newDir $d
            if (Test-Path $p) { New-Item -ItemType Directory -Force (Join-Path $dir $d) | Out-Null; Copy-Item (Join-Path $p '*') (Join-Path $dir $d) -Recurse -Force }
        }
    }
    catch {
        Write-Host (T "差し替えに失敗しました: $($_.Exception.Message)。前の版に戻します..." "Replacing failed: $($_.Exception.Message). Restoring the previous version...") -ForegroundColor Red
        Copy-Item (Join-Path $backup '*') $dir -Recurse -Force
        if (-not $NoStart) { Start-Process $exe }
        Pause-Exit 1
    }

    # 6. 起動して、すぐに終わってしまわないか確かめる（終わったら前の版に戻す）
    if ($NoStart) { Write-Host (T "更新が完了しました（起動はしていません）。版: $newVer" "Update complete (not started). Version: $newVer") -ForegroundColor Green; exit 0 }
    Write-Host (T '更新が完了しました。ProFanController を起動します...' 'Update complete. Starting ProFanController...') -ForegroundColor Green
    $p = Start-Process $exe -PassThru
    Start-Sleep -Seconds 10
    $p.Refresh()
    if ($p.HasExited -and -not (Get-Process ProFanController -ErrorAction SilentlyContinue)) {
        Write-Host (T "新しい版がすぐに終了しました（終了コード $($p.ExitCode)）。前の版に戻して起動し直します。" "The new version exited right away (exit code $($p.ExitCode)). Restoring and starting the previous version.") -ForegroundColor Red
        Copy-Item (Join-Path $backup '*') $dir -Recurse -Force
        Start-Process $exe
        Write-Host (T "logs フォルダの記録を、作った人に見せてください。" "Please send the files in the logs folder to the developer.") -ForegroundColor Yellow
        Pause-Exit 1
    }
    Write-Host (T "起動しました。版: $newVer" "Started. Version: $newVer") -ForegroundColor Green
    Start-Sleep -Seconds 2
}
catch {
    Write-Host (T "エラー: $($_.Exception.Message)" "Error: $($_.Exception.Message)") -ForegroundColor Red
    Pause-Exit 1
}
finally {
    Remove-Item -Recurse -Force $tmp -ErrorAction SilentlyContinue
}
