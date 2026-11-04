# ==================================================
# update.ps1 - ProFanController をワンクリックで更新する
#   1. ダウンロードフォルダの最新の ProFanController_v*.zip を探す
#   2. 動いているアプリを正しく終了させる（ファンを BIOS 制御に戻してから閉じる）
#   3. このフォルダに展開してビルドし、発行先の exe を差し替える
#   4. 新しい版を起動する
# 「更新.bat」から管理者権限で実行される
# ==================================================
$ErrorActionPreference = 'Stop'
$src = $PSScriptRoot
$dest = 'C:\Tools\ProFanController'
$exe = Join-Path $dest 'ProFanController.exe'

function Pause-Exit($code) { Write-Host ''; Read-Host 'Enter キーで閉じます'; exit $code }

try {
    Write-Host '=== ProFanController の更新 ===' -ForegroundColor Cyan

    # 1. 最新の zip（配布用パッケージは除く）
    $zip = Get-ChildItem "$env:USERPROFILE\Downloads\ProFanController_v*.zip" -ErrorAction SilentlyContinue |
           Sort-Object LastWriteTime -Descending | Select-Object -First 1
    if ($zip) {
        Write-Host "更新ファイル: $($zip.Name)（$($zip.LastWriteTime)）"
    } else {
        Write-Host 'ダウンロードフォルダに ProFanController_v*.zip がないので、今のソースのままビルドします。' -ForegroundColor Yellow
    }

    # 2. 動いているアプリに終了の合図を送り、終わるまで待つ
    if (Get-Process ProFanController -ErrorAction SilentlyContinue) {
        Write-Host 'ProFanController を終了しています（ファンを BIOS 制御に戻します）...'
        try { [System.Threading.EventWaitHandle]::OpenExisting('Global\ProFanController.Exit').Set() | Out-Null }
        catch { Write-Host '終了の合図を送れませんでした（古い版の可能性）。トレイのアイコンから「終了」してください。' -ForegroundColor Yellow }
        for ($i = 0; $i -lt 30 -and (Get-Process ProFanController -ErrorAction SilentlyContinue); $i++) { Start-Sleep -Milliseconds 500 }
        if (Get-Process ProFanController -ErrorAction SilentlyContinue) {
            Write-Host 'まだ動いています。トレイのアイコンを右クリック →「終了」してから、もう一度実行してください。' -ForegroundColor Red
            Pause-Exit 1
        }
    }

    # 3. 展開してビルド（設定ファイルは発行先のものをそのまま使う）
    if ($zip) {
        Write-Host '展開しています...'
        Expand-Archive $zip.FullName -DestinationPath $src -Force
    }
    Remove-Item -Recurse -Force (Join-Path $src 'bin'), (Join-Path $src 'obj') -ErrorAction SilentlyContinue
    Write-Host 'ビルドしています（1〜2 分）...'
    Push-Location $src
    dotnet publish -p:PublishProfile=SingleExe --nologo -v quiet
    $ok = $LASTEXITCODE -eq 0
    Pop-Location
    if (-not $ok) { Write-Host 'ビルドに失敗しました。上のエラーを確認してください。' -ForegroundColor Red; Pause-Exit 1 }

    # 4. 起動
    Write-Host '更新が完了しました。ProFanController を起動します。' -ForegroundColor Green
    Start-Process $exe
    Start-Sleep -Seconds 2
}
catch {
    Write-Host "エラー: $($_.Exception.Message)" -ForegroundColor Red
    Pause-Exit 1
}
