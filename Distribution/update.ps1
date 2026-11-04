# ==================================================
# update.ps1 - ProFanController（配布版）をワンクリックで更新する
#   1. ダウンロードフォルダから、ProFanController.exe が入った最新の zip を探す（名前は問わない）
#   2. 動いているアプリを正しく終了させる（ファンを BIOS 制御に戻してから閉じる）
#   3. exe と説明書だけを差し替える（設定 config.json・ログ・画像はそのまま）
#   4. 新しい版を起動する
# 「更新.bat」から管理者権限で実行される
# ==================================================
$ErrorActionPreference = 'Stop'
$dir = $PSScriptRoot
$exe = Join-Path $dir 'ProFanController.exe'

function Pause-Exit($code) { Write-Host ''; Read-Host 'Enter キーで閉じます'; exit $code }

try {
    Write-Host '=== ProFanController の更新 ===' -ForegroundColor Cyan

    # 名前は問わず、ダウンロードフォルダの新しい zip から ProFanController.exe を含むものを探す
    # （Google ドライブでフォルダごとダウンロードすると「ProFanController-日付-001.zip」のような名前になるため）
    $tmp = Join-Path $env:TEMP "ProFanController_update"
    $newExe = $null
    $zips = Get-ChildItem "$env:USERPROFILE\Downloads\*.zip" -ErrorAction SilentlyContinue |
            Where-Object { $_.Name -like '*ProFan*' } | Sort-Object LastWriteTime -Descending
    foreach ($zip in $zips) {
        Remove-Item -Recurse -Force $tmp -ErrorAction SilentlyContinue
        Expand-Archive $zip.FullName -DestinationPath $tmp -Force
        $newExe = Get-ChildItem $tmp -Recurse -Filter 'ProFanController.exe' | Select-Object -First 1
        if ($newExe) { Write-Host "更新ファイル: $($zip.Name)（$($zip.LastWriteTime)）"; break }
    }
    if (-not $newExe) {
        Write-Host '「ダウンロード」フォルダに、ProFanController.exe が入った zip が見つかりません。' -ForegroundColor Yellow
        Write-Host '受け取った zip（展開しないまま）をダウンロードフォルダに保存してから、もう一度実行してください。'
        Pause-Exit 1
    }
    if ($newExe.LastWriteTime -le (Get-Item $exe).LastWriteTime) {
        Write-Host 'ダウンロードフォルダの版は、今の版と同じか古いようです。' -ForegroundColor Yellow
        if ((Read-Host 'それでも上書きしますか？ (y/N)') -ne 'y') { Pause-Exit 0 }
    }
    $newDir = $newExe.DirectoryName

    # 動いているアプリに終了の合図を送り、終わるまで待つ
    if (Get-Process ProFanController -ErrorAction SilentlyContinue) {
        Write-Host 'ProFanController を終了しています（ファンを BIOS 制御に戻します）...'
        try { [System.Threading.EventWaitHandle]::OpenExisting('Global\ProFanController.Exit').Set() | Out-Null }
        catch { Write-Host '終了の合図を送れませんでした。トレイのアイコンを右クリック →「終了」してください。' -ForegroundColor Yellow }
        for ($i = 0; $i -lt 60 -and (Get-Process ProFanController -ErrorAction SilentlyContinue); $i++) { Start-Sleep -Milliseconds 500 }
        if (Get-Process ProFanController -ErrorAction SilentlyContinue) {
            Write-Host 'まだ動いています。トレイのアイコンを右クリック →「終了」してから、もう一度実行してください。' -ForegroundColor Red
            Pause-Exit 1
        }
    }

    # exe・説明書・ライセンス・更新スクリプトを差し替える（config.json / logs / images / reports は残す）
    Write-Host 'ファイルを差し替えています...'
    Copy-Item (Join-Path $newDir '*') $dir -Recurse -Force -Exclude 'config.json'
    Remove-Item -Recurse -Force $tmp -ErrorAction SilentlyContinue

    Write-Host '更新が完了しました。ProFanController を起動します。' -ForegroundColor Green
    Start-Process $exe
    Start-Sleep -Seconds 2
}
catch {
    Write-Host "エラー: $($_.Exception.Message)" -ForegroundColor Red
    Pause-Exit 1
}
