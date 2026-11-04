# ==================================================
# make-dist.ps1 - 配布用の zip（ProFanController_配布用_v<版>.zip）を作る
#   1. .NET ランタイムごと exe 1 つに発行する（Portable の発行プロファイル。.NET が入っていない PC でも動く）
#   2. この Distribution フォルダーの説明書・バッチ・ライセンスと一緒に ProFanController フォルダーへまとめる
#   3. リポジトリの一番上に zip を作る（設定・ログ・記録は入れない）
# 使い方（リポジトリの一番上で）: powershell -ExecutionPolicy Bypass -File Distribution\make-dist.ps1
# ==================================================
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$project = Join-Path $root 'ProFanController.csproj'
$version = ([xml](Get-Content $project -Raw)).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
if (-not $version) { throw 'ProFanController.csproj に <Version> がありません' }

$work = Join-Path ([IO.Path]::GetTempPath()) "ProFanController_dist_$([Guid]::NewGuid().ToString('N'))"
$publish = Join-Path $work 'publish'
$package = Join-Path $work 'ProFanController'
try {
    Write-Host "ProFanController $version を発行しています..." -ForegroundColor Cyan
    dotnet publish $project -p:PublishProfile=Portable "-p:PublishDir=$publish\" --nologo -v quiet
    if ($LASTEXITCODE -ne 0) { throw 'dotnet publish に失敗しました' }

    New-Item -ItemType Directory -Force $package | Out-Null
    Copy-Item (Join-Path $publish 'ProFanController.exe') $package
    # このスクリプト自身は入れない
    Get-ChildItem $PSScriptRoot | Where-Object { $_.Name -ne 'make-dist.ps1' } | Copy-Item -Destination $package -Recurse

    $zip = Join-Path $root "ProFanController_配布用_v$version.zip"
    if (Test-Path $zip) { Remove-Item $zip }
    # 日本語のファイル名を正しく入れ、区切りを「/」にするため、1 つずつ入れる（ProFanController フォルダーごと）
    Add-Type -AssemblyName System.IO.Compression, System.IO.Compression.FileSystem
    $archive = [IO.Compression.ZipFile]::Open($zip, [IO.Compression.ZipArchiveMode]::Create)
    try {
        foreach ($file in Get-ChildItem $package -Recurse -File) {
            $name = 'ProFanController/' + $file.FullName.Substring($package.Length + 1).Replace('\', '/')
            [void][IO.Compression.ZipFileExtensions]::CreateEntryFromFile($archive, $file.FullName, $name, [IO.Compression.CompressionLevel]::Optimal)
        }
    }
    finally {
        $archive.Dispose()
    }
    Write-Host "できました: $zip" -ForegroundColor Green
}
finally {
    Remove-Item -Recurse -Force $work -ErrorAction SilentlyContinue
}
