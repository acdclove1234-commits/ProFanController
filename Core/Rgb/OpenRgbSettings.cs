// ==================================================
// OpenRgbSettings.cs - OpenRGB の設定ファイル（OpenRGB.json）を読んで、メモリの配線（SMBus）を
//                      ほかのソフトと順番に使う設定（shared_smbus_access）がオフになっていないか確かめる
// Dependencies: .NET 8 (MIT License)
// See CREDITS.txt for full license details.
// ==================================================

using System.Text.Json;

namespace ProFanController;

/// <summary>
/// OpenRGB はメモリ・マザーボードの LED を SMBus で書く。このアプリ（LibreHardwareMonitor）もメモリの温度を SMBus で読む。
/// 両方とも「Global\Access_SMBUS.HTP.Method」という共通の合図（ミューテックス）で順番に使う作りで、
/// OpenRGB では設定の Drivers → shared_smbus_access がこの合図を使うかどうか（書かれていなければ使う）。
/// オフにされていると、同時に使ってぶつかり、メモリの温度が読めなくなったり、SPD（メモリの情報）が読めなくなったりする報告がある
/// </summary>
public static class OpenRgbSettings
{
    /// <summary>OpenRGB の設定ファイルの場所（ふつうに起動したとき・サービスとして動かしたとき）</summary>
    public static IEnumerable<string> SettingsPaths()
    {
        string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        if (appData.Length > 0) yield return Path.Combine(appData, "OpenRGB", "OpenRGB.json");
        string windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        if (windows.Length > 0) yield return Path.Combine(windows, "System32", "config", "systemprofile", "AppData", "Roaming", "OpenRGB", "OpenRGB.json");
    }

    /// <summary>
    /// 設定の文字列から、SMBus をほかのソフトと順番に使うか（shared_smbus_access）を読む。
    /// 書かれていなければ OpenRGB の既定（使う）なので true。読めない文字列なら null
    /// </summary>
    public static bool? SharedSmbusAccess(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json, new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return null;
            if (doc.RootElement.TryGetProperty("Drivers", out var drivers) && drivers.ValueKind == JsonValueKind.Object
                && drivers.TryGetProperty("shared_smbus_access", out var shared)
                && shared.ValueKind is JsonValueKind.True or JsonValueKind.False)
                return shared.GetBoolean();
            return true;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>shared_smbus_access がオフになっている設定ファイルの場所（なければ null。読めないファイルは飛ばす）</summary>
    public static string? FindUnsharedSmbus() => FindUnsharedSmbus(SettingsPaths());

    public static string? FindUnsharedSmbus(IEnumerable<string> paths)
    {
        foreach (var path in paths)
        {
            string text;
            try
            {
                if (!File.Exists(path)) continue;
                text = File.ReadAllText(path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                continue;
            }
            if (SharedSmbusAccess(text) == false) return path;
        }
        return null;
    }
}
