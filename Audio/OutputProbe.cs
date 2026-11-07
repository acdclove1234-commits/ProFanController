// ==================================================
// OutputProbe.cs - 出力先の情報（列挙子の名前・形）と、動いている音のソフトを Windows から読む
//   読んだ値から種類を決めるのは OutputPath（テストできる純粋な計算）
// Dependencies: NAudio (MIT License) / .NET 8 (MIT License)
// See CREDITS.txt for full license details.
// ==================================================

using System.Diagnostics;
using NAudio.CoreAudioApi;

namespace ProFanController;

internal static class OutputProbe
{
    /// <summary>PKEY_AudioEndpoint_FormFactor（1 スピーカー・3 ヘッドホン・8 光デジタル・9 HDMI など）</summary>
    private static readonly PropertyKey FormFactorKey = new(new Guid("1da5d803-d492-4edd-8c23-e0c0ffee7f0e"), 0);
    /// <summary>PKEY_Device_EnumeratorName（USB・HDAUDIO・BTHENUM・ROOT・SWD など）</summary>
    private static readonly PropertyKey EnumeratorKey = new(new Guid("a45c254e-df1c-4efd-8020-67d146a850e0"), 24);
    /// <summary>動いているソフトを調べ直す間隔（秒。プロセスの一覧を取るのは少し重いので）</summary>
    private const double SoftwareEverySec = 30;

    private static readonly object Gate = new();
    private static List<string> _software = new();
    private static double _softwareAt = double.NegativeInfinity;

    /// <summary>出力先を見分ける（読めない値は空・0 として扱う）</summary>
    public static OutputInfo Describe(MMDevice device)
    {
        string name = "", enumerator = "";
        int formFactor = 0;
        try { name = device.FriendlyName; } catch { /* 外れた出力先 */ }
        try
        {
            var props = device.Properties;
            if (props.Contains(EnumeratorKey)) enumerator = props[EnumeratorKey].Value?.ToString() ?? "";
            if (props.Contains(FormFactorKey) && props[FormFactorKey].Value is { } ff) formFactor = Convert.ToInt32(ff);
        }
        catch { /* 読めない出力先 */ }
        return OutputPath.Classify(name, enumerator, formFactor, RunningSoftware());
    }

    /// <summary>動いている音のソフト（30 秒ごとに調べ直す）</summary>
    public static List<string> RunningSoftware()
    {
        double now = AudioClock.Now;
        lock (Gate)
        {
            if (now - _softwareAt < SoftwareEverySec) return _software;
            _softwareAt = now;
        }
        var names = new List<string>();
        try
        {
            foreach (var p in Process.GetProcesses())
            {
                try { names.Add(p.ProcessName); }
                catch { /* 終了した */ }
                finally { p.Dispose(); }
            }
        }
        catch { /* 一覧を取れない */ }
        var found = OutputPath.SoftwareFrom(names);
        lock (Gate) _software = found;
        return found;
    }
}
