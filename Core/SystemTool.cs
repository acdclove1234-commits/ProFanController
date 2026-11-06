// ==================================================
// SystemTool.cs - Windows に付属のツール (netsh・schtasks・powercfg・explorer) を安全に起動する
// Dependencies: .NET 8 (MIT License)
// See CREDITS.txt for full license details.
// ==================================================

using System.Diagnostics;

namespace ProFanController;

/// <summary>
/// このアプリは管理者権限で動くので、"netsh.exe" のように名前だけで起動すると、
/// アプリのフォルダーなど書き換えられる場所に置かれた同じ名前の偽物が実行されるおそれがある。
/// Windows のシステムフォルダーの完全なパスで起動し、作業フォルダーもシステムフォルダーにする
/// </summary>
public static class SystemTool
{
    /// <summary>C:\Windows\System32 にあるツールの完全なパス</summary>
    public static string PathOf(string exe) => Path.Combine(Environment.SystemDirectory, exe);

    /// <summary>System32 のツールを、画面を出さずに起動する設定</summary>
    public static ProcessStartInfo Start(string exe, string arguments) => new(PathOf(exe), arguments)
    {
        CreateNoWindow = true,
        UseShellExecute = false,
        WorkingDirectory = Environment.SystemDirectory,
    };

    /// <summary>
    /// フォルダーやファイルをエクスプローラーで開く（ファイルは関連付けられたアプリで開く）。
    /// エクスプローラー経由にすると、開いたアプリは管理者権限ではなく、ふだんの権限で動く
    /// </summary>
    public static void Open(string path)
    {
        string explorer = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe");
        Process.Start(new ProcessStartInfo(explorer, $"\"{path}\"")
        {
            UseShellExecute = false,
            WorkingDirectory = Environment.SystemDirectory,
        })?.Dispose();
    }

    /// <summary>
    /// Web ページを、ふだん使っているブラウザーで開く（エクスプローラー経由なので、ブラウザーは管理者権限にならない）。
    /// 開くのは https のアドレスだけ（ほかの形の文字列をエクスプローラーに渡さない）
    /// </summary>
    public static void OpenUrl(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
            throw new ArgumentException($"https のアドレスではありません: {url}", nameof(url));
        Open(uri.AbsoluteUri);
    }

    /// <summary>エクスプローラーで、そのファイル・フォルダーが入っているフォルダーを開いて、選んだ状態にする</summary>
    public static void Reveal(string path)
    {
        string explorer = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe");
        Process.Start(new ProcessStartInfo(explorer, $"/select,\"{path.TrimEnd(Path.DirectorySeparatorChar)}\"")
        {
            UseShellExecute = false,
            WorkingDirectory = Environment.SystemDirectory,
        })?.Dispose();
    }
}
