# ProFanController

Windows 用のファン制御・監視ツールです。マザーボードのファン端子を温度に合わせて制御し、OpenRGB と連携して PC の LED も光らせます（温度・レインボー・音楽連動など）。

A fan control and monitoring tool for Windows. It drives motherboard fan headers from temperature curves and, with OpenRGB, lights the PC's LEDs (temperature, rainbow, music sync, and more). The UI is in Japanese.

> 個人で作ったツールです。ファンやポンプの制御を誤ると、部品が熱くなりすぎることがあります。ご利用は自己責任でお願いします（LICENSE の「無保証」のとおりです）。

## できること

- **ファン制御**: 温度カーブ・目標温度・固定値・ほかのファンへの連動。複数の温度センサー、ヒステリシス、回転の上がり下がりの速さ、共振する回転数を避ける設定
- **おまかせ調整**: 負荷をかけて測り、目標温度を守れるいちばん静かなカーブを作る
- **安全機能**: 緊急全開、センサーの異常値の除外・固着の検知、ポンプの回転の見張り、見張りプロセス（本体が落ちたらファンを安全な回転数にして起動し直す）、終了・スリープ時は BIOS 制御に戻す
- **監視・記録**: ダッシュボード、履歴、電気代の目安、ファンの劣化・ホコリの検知、異常の前後の記録
- **LED（OpenRGB 連携）**: 温度・レインボー・呼吸・固定色・音楽連動（キック・スネアの検出、テンポの追従、12 種類の光り方）。デバイス・端子（ゾーン）ごとの設定と色合わせ。PNY ARGB Epic-X のグラボは OpenRGB を通さず直接書き込み
- **そのほか**: プロファイルの自動切り替え、スマホからの確認（同じネットワーク内）、ntfy / Discord への通知、タスクトレイ常駐・ログオン時の自動起動

## 必要なもの

- Windows 10 / 11（64bit）のデスクトップ PC
- [PawnIO](https://pawnio.eu/)（ファンの制御用のドライバー。LibreHardwareMonitor 0.9.6 以降で必要）
- 管理者権限（Super I/O にアクセスするため、起動時に UAC の確認が出ます）
- LED を使う場合: [OpenRGB](https://openrgb.org/)（起動しておき、SDK サーバーをオンにする）

ノート PC と、ファン制御に対応していないマザーボードでは使えません。

## 使い方（配布用の zip）

1. [Releases](../../releases) から `ProFanController-<版>-windows-x64.zip` をダウンロードして展開し、`ProFanController` フォルダーを `C:\Tools\ProFanController` など動かさない場所に置く
2. `ProFanController.exe` を起動する。PawnIO が入っていなければ、セットアップの最初の画面の「PawnIO をインストール」で入れて（winget を使います）、「アプリを起動し直す」を押す
   （うまくいかないときは「1_PawnIO をインストール.bat」を右クリック →「管理者として実行」するか、[公式サイト](https://pawnio.eu/)のインストーラーを使う）
3. セットアップの案内に沿って、ファン端子に名前を付ける

詳しくは、zip に入っている「はじめにお読みください.txt」（このリポジトリでは `Distribution/` の中）を見てください。新しい版への更新は、新しい zip を展開せずにダウンロードフォルダーに置いて「2_更新.bat」を実行します（設定・記録はそのまま）。

使うのをやめるときは、設定 →「安全・バックアップ」→「アンインストール（お片付け）」を使います。ファンを BIOS の自動制御に戻したことを確かめてから、ライティングの制御・ログオン時の自動起動・ファイアウォールの許可などを片付けて終了します（設定・記録の削除と PawnIO のアンインストールは、選んだときだけ）。最後に、アプリのフォルダーを自分で削除します。

## ソースからのビルド

.NET 8 SDK が必要です。

```powershell
# 動作確認（Release でビルドして起動）
dotnet build -c Release
# ロジックのテスト
dotnet run -c Release --project Tests/LogicTests
# 配布用の zip を作る（.NET ランタイムごと exe 1 つに発行し、Distribution の中身と一緒にまとめる）
powershell -ExecutionPolicy Bypass -File Distribution/make-dist.ps1
```

`Properties/PublishProfiles` に発行のプロファイルが 2 つあります。

- `Portable`: .NET ランタイムごと 1 つの exe（配布用。make-dist.ps1 が使う）
- `SingleExe`: .NET 8 デスクトップランタイムが入っている PC 用の軽い exe

一番上の `config.json` は開発用の見本です（ビルドしたときの出力フォルダーにだけ写され、配布用には入りません）。

## フォルダーの構成

| 場所 | 中身 |
|---|---|
| `Core/` | ファン制御のエンジン、センサー、設定、OpenRGB との通信、LED の描画（`Core/Rgb`） |
| `Audio/`・`Core/Audio/` | 音楽連動の音の取り込みと分析（帯域・キック・テンポ） |
| `UI/`・`MainWindow*` | 画面（WPF） |
| `Tests/LogicTests/` | ロジックのテスト（画面・実機なしで動く） |
| `Distribution/` | 配布用の zip に入れる説明書・バッチ・ライセンスと、zip を作るスクリプト |

## ライセンス

ProFanController は [MIT License](LICENSE) です。使っている部品（LibreHardwareMonitorLib は MPL-2.0、NAudio・QRCoder・.NET は MIT、フォントは SIL Open Font License 1.1 など）のライセンスは [CREDITS.txt](CREDITS.txt) にまとめています。OpenRGB・PawnIO・Ollama はこのリポジトリに含まず、別に入れて使います。
