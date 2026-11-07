// ==================================================
// OutputPath.cs - 音を聞いている出力先の種類と、音の効果・音の経路を変えるソフトを見分ける
//   光は「取り込んだ音」（アプリが出した音。効果をかける前）に合わせて光る。耳に届く音は、出力先の種類や
//   音のソフトによって遅れる（Bluetooth は 150〜250 ミリ秒、テレビは 50〜150 ミリ秒、仮想の出力先を通すと数十ミリ秒）。
//   出力先とソフトを見分けて、音の遅れの補正の目安と、案内の文を出す（純粋な計算なのでテストできる）
// Dependencies: .NET 8 (MIT License)
// See CREDITS.txt for full license details.
// ==================================================

namespace ProFanController;

/// <summary>出力先の種類</summary>
public enum OutputKind
{
    /// <summary>分からない</summary>
    Unknown,
    /// <summary>マザーボードの端子（アナログ・光デジタル）</summary>
    Onboard,
    /// <summary>USB の DAC・ヘッドセット</summary>
    Usb,
    /// <summary>HDMI・DisplayPort（モニター・テレビ・AV アンプ）</summary>
    Display,
    /// <summary>Bluetooth のイヤホン・スピーカー</summary>
    Bluetooth,
    /// <summary>ソフトが作った仮想の出力先（音はそこから別の出力先へ送られる）</summary>
    Virtual,
    /// <summary>リモート デスクトップ</summary>
    Remote,
}

/// <summary>出力先を見分けた結果</summary>
/// <param name="SuggestedDelayMs">音の遅れの補正の目安（ミリ秒。0 なら補正はいらない）</param>
/// <param name="Software">見つかった音のソフト（効果・経路を変えるもの。なければ空）</param>
public sealed record OutputInfo(string Name, OutputKind Kind, int SuggestedDelayMs, IReadOnlyList<string> Software)
{
    public static readonly OutputInfo None = new("", OutputKind.Unknown, 0, Array.Empty<string>());
}

public static class OutputPath
{
    /// <summary>Bluetooth の目安（ミリ秒）。SBC・AAC はおよそ 150〜250。aptX Low Latency などはもっと短い</summary>
    public const int BluetoothDelayMs = 180;
    /// <summary>HDMI・DisplayPort の目安（ミリ秒）。モニターのスピーカーは 30〜60、テレビは映像の処理に合わせて 50〜150</summary>
    public const int DisplayDelayMs = 50;
    /// <summary>仮想の出力先（ミキサー・仮想ケーブル）を通るときの目安（ミリ秒）。ソフトのバッファーの分</summary>
    public const int VirtualDelayMs = 30;

    /// <summary>仮想の出力先を作るソフト（出力先の名前に含まれる文字 → ソフトの名前）</summary>
    private static readonly (string Word, string Software)[] VirtualNames =
    {
        ("Voicemeeter", "Voicemeeter"), ("VoiceMeeter", "Voicemeeter"),
        ("VB-Audio", "VB-Audio Virtual Cable"), ("CABLE Input", "VB-Audio Virtual Cable"),
        ("Sonar", "SteelSeries Sonar"),
        ("Steam Streaming", "Steam Streaming Speakers"),
        ("NVIDIA Broadcast", "NVIDIA Broadcast"),
        ("Virtual", ""),
    };

    /// <summary>Bluetooth らしい出力先の名前</summary>
    private static readonly string[] BluetoothNames = { "Bluetooth", "Hands-Free", "AirPods", "Buds", "WH-1000", "WF-1000" };

    /// <summary>HDMI・DisplayPort らしい出力先の名前</summary>
    private static readonly string[] DisplayNames = { "HDMI", "DisplayPort", "Display Audio", "NVIDIA High Definition Audio", "AMD High Definition Audio" };

    /// <summary>
    /// 音の効果をかける・経路を変えるソフト（動いているプロセスの名前の始まり → ソフトの名前）。
    /// 効果は取り込んだ音には入らない（アプリが出した音を取り込むため）が、耳に届く音は遅れることがある
    /// </summary>
    public static readonly (string Process, string Software)[] SoftwareProcesses =
    {
        ("NahimicSvc", "Nahimic"), ("NahimicAPO", "Nahimic"),
        ("DolbyDAX", "Dolby"), ("DolbyAccess", "Dolby"),
        ("DtsApo", "DTS"), ("DTSSoundUnbound", "DTS"),
        ("SteelSeriesSonar", "SteelSeries Sonar"),
        ("voicemeeter", "Voicemeeter"),
        ("FxSound", "FxSound"),
        ("Boom3D", "Boom 3D"),
        ("SonicStudio", "Sonic Studio"),
        ("THXSpatial", "THX Spatial Audio"),
    };

    /// <summary>
    /// 出力先を見分ける。
    /// enumerator は Windows のデバイスの列挙子の名前（USB・HDAUDIO・BTHENUM・ROOT・SWD など）、
    /// formFactor は出力先の形（1 スピーカー・3 ヘッドホン・8 光デジタル・9 HDMI など。分からなければ 0）
    /// </summary>
    public static OutputInfo Classify(string? name, string? enumerator, int formFactor, IEnumerable<string>? runningSoftware = null)
    {
        string n = name ?? "";
        string e = (enumerator ?? "").ToUpperInvariant();
        var software = new List<string>();
        foreach (var s in runningSoftware ?? Array.Empty<string>())
            if (s.Length > 0 && !software.Contains(s)) software.Add(s);

        OutputKind kind;
        if (e.StartsWith("BTH") || BluetoothNames.Any(w => n.Contains(w, StringComparison.OrdinalIgnoreCase))) kind = OutputKind.Bluetooth;
        else if (n.Contains("Remote Audio", StringComparison.OrdinalIgnoreCase) || e == "RDPBUS") kind = OutputKind.Remote;
        else if (e is "ROOT" or "SWD" || VirtualNames.Any(v => n.Contains(v.Word, StringComparison.OrdinalIgnoreCase))) kind = OutputKind.Virtual;
        else if (formFactor == 9 || DisplayNames.Any(w => n.Contains(w, StringComparison.OrdinalIgnoreCase))) kind = OutputKind.Display;
        else if (e == "USB") kind = OutputKind.Usb;
        else if (e is "HDAUDIO" or "INTELAUDIO" or "PCI") kind = OutputKind.Onboard;
        else kind = OutputKind.Unknown;

        if (kind == OutputKind.Virtual)
        {
            foreach (var (word, sw) in VirtualNames)
                if (sw.Length > 0 && n.Contains(word, StringComparison.OrdinalIgnoreCase) && !software.Contains(sw)) software.Insert(0, sw);
        }

        int delay = kind switch
        {
            OutputKind.Bluetooth => BluetoothDelayMs,
            OutputKind.Display => DisplayDelayMs,
            OutputKind.Virtual => VirtualDelayMs,
            _ => 0,
        };
        return new OutputInfo(n, kind, delay, software);
    }

    /// <summary>動いているプロセスの名前から、音のソフトを探す（同じソフトは 1 回だけ）</summary>
    public static List<string> SoftwareFrom(IEnumerable<string> processNames)
    {
        var found = new List<string>();
        foreach (string p in processNames)
        {
            foreach (var (prefix, sw) in SoftwareProcesses)
            {
                if (p.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) && !found.Contains(sw)) found.Add(sw);
            }
        }
        return found;
    }

    /// <summary>
    /// 案内を出すか: 目安のある出力先（Bluetooth・HDMI・仮想）で、今の補正が目安より 20 ミリ秒以上小さいとき
    /// （Bluetooth は以前からの決まりどおり、補正が 150 ミリ秒より小さいとき）
    /// </summary>
    public static bool ShouldSuggest(OutputInfo info, double audioDelayMs) => info.Kind switch
    {
        OutputKind.Bluetooth => audioDelayMs < 150,
        OutputKind.Display or OutputKind.Virtual => audioDelayMs <= info.SuggestedDelayMs - 20,
        _ => false,
    };

    /// <summary>出力先の種類の短い説明（状態の表示用）</summary>
    public static string KindLabel(OutputKind kind) => kind switch
    {
        OutputKind.Onboard => L("マザーボードの端子"),
        OutputKind.Usb => "USB",
        OutputKind.Display => L("HDMI・DisplayPort"),
        OutputKind.Bluetooth => "Bluetooth",
        OutputKind.Virtual => L("仮想の出力先"),
        OutputKind.Remote => L("リモート デスクトップ"),
        _ => "",
    };

    /// <summary>案内の文（出力先の種類ごと。ソフトが見つかっていれば、その名前も添える）</summary>
    public static string Hint(OutputInfo info) => info.Kind switch
    {
        OutputKind.Bluetooth => L("Bluetooth は音が遅れるので +150〜200 ms がおすすめ"),
        OutputKind.Display => L($"HDMI・DisplayPort（モニター・テレビ）は音が遅れることがあります。目安は +{DisplayDelayMs} ms（テレビは +100 ms 以上のことも）"),
        OutputKind.Virtual => L($"仮想の出力先{(info.Software.Count > 0 ? L($"（{info.Software[0]}）") : "")}を通っているので、実際に鳴るまで少し遅れます。目安は +{VirtualDelayMs} ms"),
        _ => "",
    };

    /// <summary>音の効果ソフトの案内（効果は光には入らないこと、ずれたら同期テストで合わせること）</summary>
    public static string SoftwareNote(OutputInfo info)
    {
        var effects = info.Software.Where(s => s is not ("VB-Audio Virtual Cable" or "Steam Streaming Speakers")).ToList();
        if (effects.Count == 0) return "";
        return L($"音のソフト: {string.Join(Localization.ListSeparator, effects)}（光は効果をかける前の音に合わせています。音とずれて感じるときは「同期テスト」で合わせてください）");
    }
}
