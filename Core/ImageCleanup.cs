// ==================================================
// ImageCleanup.cs - デバイスの画像（images フォルダ）のうち、どの設定からも使われていない古いファイルを消す
// Dependencies: .NET 8 (MIT License)
// See CREDITS.txt for full license details.
// ==================================================

namespace ProFanController;

/// <summary>
/// 画像を差し替えたとき、元に戻す（Ctrl+Z）のために古いファイルは残している。元に戻す記録はアプリを終わると消えるので、
/// 起動したときに「設定から使われていない」かつ「KeepDays 日より前のファイル」を消す（最近の設定のバックアップから戻したときに困らないよう、新しいものは残す）
/// </summary>
public static class ImageCleanup
{
    public const int KeepDays = 7;
    private static readonly string[] Extensions = { ".png", ".jpg", ".jpeg", ".bmp", ".webp" };

    /// <summary>消すファイルの一覧（消すのは呼び出し側。テストしやすいように分ける）</summary>
    public static List<string> Unused(string directory, IEnumerable<string> referenced, DateTime now)
    {
        var list = new List<string>();
        if (!Directory.Exists(directory)) return list;
        var keep = new HashSet<string>(referenced.Where(n => !string.IsNullOrEmpty(n)), StringComparer.OrdinalIgnoreCase);
        foreach (var path in Directory.EnumerateFiles(directory))
        {
            string name = Path.GetFileName(path);
            if (keep.Contains(name)) continue;
            if (!Extensions.Contains(Path.GetExtension(name).ToLowerInvariant())) continue;
            if (now - File.GetLastWriteTime(path) < TimeSpan.FromDays(KeepDays)) continue;
            list.Add(path);
        }
        return list;
    }

    /// <summary>使われていない古い画像を消す。消した数を返す（消せなかったものは飛ばす）</summary>
    public static int Run(string directory, IEnumerable<string> referenced)
    {
        int n = 0;
        foreach (var path in Unused(directory, referenced, DateTime.Now))
        {
            try { File.Delete(path); n++; }
            catch (Exception ex) { Logger.Warn($"使われていない画像を消せません: {Path.GetFileName(path)}: {ex.Message}"); }
        }
        if (n > 0) Logger.Info($"使われていないデバイスの画像を {n} 個消しました");
        return n;
    }
}
