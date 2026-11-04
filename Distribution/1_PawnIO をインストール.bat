@echo off
chcp 65001 >nul
echo PawnIO（ファン制御用ドライバ）をインストールします...
echo.
net session >nul 2>&1
if errorlevel 1 (
  echo このファイルを右クリックして「管理者として実行」してください。
  pause
  exit /b 1
)
winget install --id namazso.PawnIO -e --accept-source-agreements --accept-package-agreements
if errorlevel 1 (
  echo.
  echo winget でのインストールに失敗しました。
  echo ブラウザで https://pawnio.eu/ を開くので、インストーラーを入手して実行してください。
  start https://pawnio.eu/
) else (
  echo.
  echo インストールが完了しました。ProFanController.exe を起動してください。
)
pause
