# ==================================================
# update.ps1 - ProFanController（配布版）をワンクリックで更新する
#   1. ダウンロードフォルダから、ProFanController.exe が入った zip を探す（名前に ProFan を含むもの。新しい順）
#   2. 別の一時フォルダに展開して中身を確かめる（exe の版・製品名。版は日付ではなく exe に書かれた版で比べる）
#   3. 公式の配布物か確かめる（インターネットにつながれば、GitHub のリリースの SHA-256 と zip を比べる）
#   4. 動いているアプリを正しく終了させる（ファンを BIOS 制御に戻してから閉じる）
#   5. 今のファイルを backups フォルダに退避してから、決まったファイルだけを差し替える
#      （exe・説明書・更新用のファイル・ライセンス。設定 config.json・ログ・記録・画像には触れない）
#   6. 新しい版を起動し、すぐに終わってしまったら、退避したファイルに戻して前の版を起動し直す
# 「2_更新.bat」から管理者権限で実行される
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
$allowedFiles = @('ProFanController.exe', 'はじめにお読みください.txt', 'update.ps1', '1_PawnIO をインストール.bat', '2_更新.bat')
$allowedDirs = @('licenses')

function Pause-Exit($code) { if (-not $Yes) { Write-Host ''; Read-Host 'Enter キーで閉じます' | Out-Null }; exit $code }
function Ask($question) { if ($Yes) { return $true }; return (Read-Host "$question (y/N)") -eq 'y' }
function Get-Version($path) {
    $v = (Get-Item $path).VersionInfo
    try { return [version]($v.FileVersion -replace '[^0-9.]', '') } catch { return $null }
}

$tmp = Join-Path $env:TEMP ("ProFanController_update_" + [Guid]::NewGuid().ToString('N'))
$backup = $null
try {
    Write-Host '=== ProFanController の更新 ===' -ForegroundColor Cyan
    if (-not (Test-Path $exe)) { Write-Host "更新する ProFanController.exe が見つかりません: $exe" -ForegroundColor Red; Pause-Exit 1 }
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
        Write-Host "「$DownloadsDir」に、ProFanController.exe が入った zip が見つかりません。" -ForegroundColor Yellow
        Write-Host '受け取った zip（展開しないまま）をダウンロードフォルダに保存してから、もう一度実行してください。'
        Pause-Exit 1
    }
    $newVer = Get-Version $newExe.FullName
    Write-Host "更新ファイル: $($zip.FullName)"
    Write-Host "  今の版: $curVer → 新しい版: $newVer"
    if ($null -ne $newVer -and $null -ne $curVer -and $newVer -le $curVer) {
        Write-Host 'zip の版は、今の版と同じか古いです。' -ForegroundColor Yellow
        if (-not (Ask 'それでも上書きしますか？')) { Pause-Exit 0 }
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
            if ($digests -contains $hash) { $verified = $true; Write-Host "  公式のリリース $tag の zip と一致しました（SHA-256）。" -ForegroundColor Green }
            elseif ($digests.Count -gt 0) { Write-Host "  注意: 公式のリリース $tag の zip と中身が違います（作り直した zip か、別の人が作った zip です）。" -ForegroundColor Yellow }
            else { Write-Host "  公式のリリース $tag には照合用の値がありませんでした。" -ForegroundColor Yellow }
        }
        catch { Write-Host "  公式のリリースと照合できませんでした（インターネットにつながっていない・リリースがまだない など）: $($_.Exception.Message)" -ForegroundColor Yellow }
    }
    Write-Host "  zip の SHA-256: $hash"
    if (-not $verified -and -not (Ask '公式の配布物と確かめられませんでした。信頼できる人から受け取った zip なら、続けますか？')) { Pause-Exit 0 }

    # 4. この場所で動いているアプリだけに、終了の合図を送る
    $running = @(Get-Process ProFanController -ErrorAction SilentlyContinue | Where-Object { try { $_.Path -eq $exe } catch { $false } })
    if ($running.Count -gt 0) {
        Write-Host 'ProFanController を終了しています（ファンを BIOS 制御に戻します）...'
        try { [System.Threading.EventWaitHandle]::OpenExisting('Global\ProFanController.Exit').Set() | Out-Null }
        catch { Write-Host '終了の合図を送れませんでした。トレイのアイコンを右クリック →「終了」してください。' -ForegroundColor Yellow }
        for ($i = 0; $i -lt 60 -and ($running | Where-Object { -not $_.HasExited }); $i++) { Start-Sleep -Milliseconds 500; $running | ForEach-Object { $_.Refresh() } }
        if ($running | Where-Object { -not $_.HasExited }) {
            Write-Host 'まだ動いています。トレイのアイコンを右クリック →「終了」してから、もう一度実行してください。' -ForegroundColor Red
            Pause-Exit 1
        }
    }

    # 5. 退避してから、決まったファイルだけを差し替える
    $stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
    $backup = Join-Path $dir "backups\update-$stamp-from-$curVer"
    New-Item -ItemType Directory -Force $backup | Out-Null
    foreach ($f in $allowedFiles) { $p = Join-Path $dir $f; if (Test-Path $p) { Copy-Item $p $backup -Force } }
    foreach ($d in $allowedDirs) { $p = Join-Path $dir $d; if (Test-Path $p) { Copy-Item $p (Join-Path $backup $d) -Recurse -Force } }
    Write-Host "今のファイルを退避しました: $backup"

    Write-Host 'ファイルを差し替えています...'
    try {
        foreach ($f in $allowedFiles) { $p = Join-Path $newDir $f; if (Test-Path $p) { Copy-Item $p $dir -Force } }
        foreach ($d in $allowedDirs) {
            $p = Join-Path $newDir $d
            if (Test-Path $p) { New-Item -ItemType Directory -Force (Join-Path $dir $d) | Out-Null; Copy-Item (Join-Path $p '*') (Join-Path $dir $d) -Recurse -Force }
        }
    }
    catch {
        Write-Host "差し替えに失敗しました: $($_.Exception.Message)。前の版に戻します..." -ForegroundColor Red
        Copy-Item (Join-Path $backup '*') $dir -Recurse -Force
        if (-not $NoStart) { Start-Process $exe }
        Pause-Exit 1
    }

    # 6. 起動して、すぐに終わってしまわないか確かめる（終わったら前の版に戻す）
    if ($NoStart) { Write-Host "更新が完了しました（起動はしていません）。版: $newVer" -ForegroundColor Green; exit 0 }
    Write-Host '更新が完了しました。ProFanController を起動します...' -ForegroundColor Green
    $p = Start-Process $exe -PassThru
    Start-Sleep -Seconds 10
    $p.Refresh()
    if ($p.HasExited -and -not (Get-Process ProFanController -ErrorAction SilentlyContinue)) {
        Write-Host "新しい版がすぐに終了しました（終了コード $($p.ExitCode)）。前の版に戻して起動し直します。" -ForegroundColor Red
        Copy-Item (Join-Path $backup '*') $dir -Recurse -Force
        Start-Process $exe
        Write-Host "logs フォルダの記録を、作った人に見せてください。" -ForegroundColor Yellow
        Pause-Exit 1
    }
    Write-Host "起動しました。版: $newVer" -ForegroundColor Green
    Start-Sleep -Seconds 2
}
catch {
    Write-Host "エラー: $($_.Exception.Message)" -ForegroundColor Red
    Pause-Exit 1
}
finally {
    Remove-Item -Recurse -Force $tmp -ErrorAction SilentlyContinue
}
