// ==================================================
// Advisor.cs - AI 冷却アドバイザー (履歴の統計を要約し、ローカルの Ollama に分析させる)
// Dependencies: .NET 8 / System.Net.Http / System.Text.Json (MIT License), Ollama (MIT License・別途インストール)
// See CREDITS.txt for full license details.
// ==================================================

using System.Globalization;
using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace ProFanController;

/// <summary>Ollama の HTTP API（既定 http://localhost:11434）。データは PC の外に出ない</summary>
public static class OllamaClient
{
    private static readonly HttpClient Http = new() { Timeout = Timeout.InfiniteTimeSpan };

    public static async Task<List<string>> ListModelsAsync(string baseUrl, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(5));
        string json = await Http.GetStringAsync($"{baseUrl.TrimEnd('/')}/api/tags", cts.Token);
        var root = JsonNode.Parse(json);
        return root?["models"]?.AsArray().Select(m => m?["name"]?.GetValue<string>() ?? "").Where(n => n.Length > 0).ToList() ?? new();
    }

    /// <summary>回答を少しずつ受け取る（ストリーミング）。考え中の文章（thinking）は除く</summary>
    public static async IAsyncEnumerable<string> ChatStreamAsync(string baseUrl, string model, string system, string user,
        [EnumeratorCancellation] CancellationToken ct)
    {
        var body = new JsonObject
        {
            ["model"] = model,
            ["stream"] = true,
            ["messages"] = new JsonArray
            {
                new JsonObject { ["role"] = "system", ["content"] = system },
                new JsonObject { ["role"] = "user", ["content"] = user },
            },
            ["options"] = new JsonObject { ["temperature"] = 0.3 },
        };
        using var req = new HttpRequestMessage(HttpMethod.Post, $"{baseUrl.TrimEnd('/')}/api/chat")
        {
            Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"),
        };
        using var res = await Http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
        if (!res.IsSuccessStatusCode)
        {
            string err = await res.Content.ReadAsStringAsync(ct);
            throw new HttpRequestException(L($"Ollama がエラーを返しました ({(int)res.StatusCode}): {err}"));
        }

        using var reader = new StreamReader(await res.Content.ReadAsStreamAsync(ct));
        bool inThink = false;
        while (await reader.ReadLineAsync(ct) is { } line)
        {
            if (line.Length == 0) continue;
            var node = JsonNode.Parse(line);
            if (node?["error"]?.GetValue<string>() is { } error) throw new HttpRequestException(error);
            string piece = node?["message"]?["content"]?.GetValue<string>() ?? "";

            // <think>…</think> を出力するモデル（qwen3 など）は、その部分を表示しない
            if (piece.Contains("<think>")) { inThink = true; piece = piece[..piece.IndexOf("<think>")]; }
            if (inThink)
            {
                int close = piece.IndexOf("</think>");
                if (close < 0) continue;
                inThink = false;
                piece = piece[(close + 8)..];
            }
            if (piece.Length > 0) yield return piece;
            if (node?["done"]?.GetValue<bool>() == true) yield break;
        }
    }
}

/// <summary>AI に渡す「PC の冷却状況の要約」を作る。生データではなく統計だけを渡す</summary>
public static class AdvisorPrompt
{
    /// <summary>
    /// AI への指示（指示そのものは日本語のまま。答えは画面の言語で書いてもらう。
    /// 見出しの「## 全体の評価」なども、その言語に直して書くよう頼む）
    /// </summary>
    public static string System => Localization.IsSource
        ? SystemJa
        : SystemJa.Replace("日本語で分かりやすく", $"{Localization.Current.NativeName}（{Localization.Current.EnglishName}）で分かりやすく")
          + $"\nWrite the whole answer, including the section headings, in {Localization.Current.EnglishName} ({Localization.Current.NativeName}), not in Japanese.";

    private const string SystemJa =
        "あなたは自作 PC の冷却とファン制御に詳しいエンジニアです。" +
        "ユーザーの PC の計測データの要約を読み、日本語で分かりやすくアドバイスしてください。\n" +
        "出力の構成:\n" +
        "## 全体の評価（2〜3 文）\n" +
        "## 気になる点（箇条書き。根拠となる数値を添える）\n" +
        "## 具体的な改善案（ファンカーブの温度と % など、設定できる数値で示す。優先度の高い順に最大 5 つ）\n" +
        "## 注意点\n" +
        "データから断定できないことは「推測ですが」と明記してください。部品の交換を勧める前に、設定で改善できる方法を優先してください。\n" +
        "ケースファンは GPU Hot Spot では下げられないので、ケースファンの基準には GPU 温度（Edge）や CPU 温度を勧めてください。" +
        "固定モードや BIOS 任せのファンのカーブは使われていないので、カーブの変更は勧めないでください。";

    public static string Build(HistoryStore h, AppConfig cfg, string profile,
        IReadOnlyList<(string Id, string Name)> temps, IReadOnlyList<(string Id, string Name, string Unit)> metrics,
        IEnumerable<DiagnosticFinding> findings, CoolingTestReport? test, string? userQuestion, IEnumerable<string>? longTerm = null)
    {
        var inv = CultureInfo.InvariantCulture;
        var sb = new StringBuilder();
        if (h.Count == 0) return L("記録がまだありません。");

        var first = h.TimeAt(0);
        var last = h.TimeAt(h.Count - 1);
        sb.AppendLine(L($"# 記録期間: {first:MM/dd HH:mm} 〜 {last:MM/dd HH:mm}（{(last - first).TotalMinutes:F0} 分。起動前の分は 1 分ごとの平均、起動後は 1 秒ごと）"));
        sb.AppendLine(L($"# 実行中のプロファイル: {profile}"));
        sb.AppendLine(L($"# 緊急全開: CPU 等 {cfg.EmergencyTemperature:F0}°C ／ GPU Hot Spot {cfg.EmergencyHotspotTemperature:F0}°C"));
        sb.AppendLine();

        sb.AppendLine(L("## 温度（最小 / 平均 / 95パーセンタイル / 最大, °C）"));
        var skipped = new List<string>();
        foreach (var (id, name) in temps)
        {
            if (Stats(h, id) is not { } s) continue;
            // 0°C 以下・120°C 超えは未接続の端子や読み取り誤り。AI が「故障」と誤解しないよう渡さない
            // 最小値が 70°C を超えている＝アイドル時でも高いままのセンサーも、何もつながっていない可能性が高い
            if (s.Min <= 0 || s.Max > 120 || (s.Min > 70 && !name.Contains("Hot Spot")))
            {
                skipped.Add(name);
                continue;
            }
            sb.AppendLine(string.Format(inv, "- {0}: {1:F0} / {2:F0} / {3:F0} / {4:F0}", name, s.Min, s.Avg, s.P95, s.Max));
        }
        if (skipped.Count > 0)
            sb.AppendLine(L($"（{string.Join(L("、"), skipped)} は、未接続または値が不正なセンサーのため除外しました。これらについては言及しないでください）"));

        sb.AppendLine();
        sb.AppendLine(L("## 負荷など（平均 / 最大）"));
        foreach (var (id, name, unit) in metrics)
        {
            if (Stats(h, id) is { } s) sb.AppendLine(string.Format(inv, "- {0}: {1:F0} / {2:F0} {3}", name, s.Avg, s.Max, unit));
        }

        sb.AppendLine();
        sb.AppendLine(L("## ファンの設定と実績"));
        foreach (var fan in cfg.FindProfile(profile)?.Fans ?? new())
        {
            var duty = Stats(h, HistoryStore.DutyKey(fan.ControlId));
            var rpm = Stats(h, HistoryStore.RpmKey(fan.ControlId));
            var cal = cfg.FindCalibration(fan.ControlId);
            sb.Append(L($"- {fan.Name}（{(fan.IsPump ? L("ポンプ") : L("ファン"))}、制御: {(fan.Enabled ? ModeText(fan) : L("BIOS 任せ"))}）"));
            if (duty is { } d) sb.Append(string.Format(inv, L(" Duty 平均 {0:F0}% / 最大 {1:F0}%"), d.Avg, d.Max));
            if (rpm is { } r) sb.Append(string.Format(inv, L("、回転 平均 {0:F0} / 最大 {1:F0} rpm"), r.Avg, r.Max));
            sb.AppendLine();
            if (fan.Mode == FanModes.Curve)
            {
                foreach (var c in fan.Curves)
                {
                    string pts = string.Join(" → ", c.Points.Select(p => string.Format(inv, "{0:F0}°C:{1:F0}%", p.Temp, p.Duty)));
                    string sensor = temps.FirstOrDefault(t => t.Id == c.SensorId).Name ?? c.SensorId;
                    sb.AppendLine(L($"  - カーブ「{c.Name}」 センサー: {sensor} ／ {pts} ／ ヒステリシス {c.HysteresisCelsius:F1}°C"));
                }
            }
            sb.AppendLine(string.Format(inv, L("  - 最低回転数 {0:F0}%、上昇 {1:F1}%/秒、下降 {2:F1}%/秒"), fan.MinDuty, fan.MaxStepUpPerTick, fan.MaxStepDownPerTick));
            if (cal is not null)
                sb.AppendLine(string.Format(inv, L("  - キャリブレーション: 最大 {0:F0} rpm、{1:F0}% 未満で停止、{2:F0}% で始動"), cal.MaxRpm, cal.StopDuty, cal.StartDuty));
        }

        var list = findings.ToList();
        if (list.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine(L("## 自動診断の結果"));
            foreach (var f in list) sb.AppendLine($"- [{f.Level}] {f.Title}: {f.Detail}");
        }

        if (test is not null)
        {
            sb.AppendLine();
            sb.AppendLine(L("## 直近の冷却テスト"));
            sb.AppendLine(test.ToText());
        }

        var trend = longTerm?.ToList() ?? new();
        if (trend.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine(L("## 長期の傾向（数週間の記録から。ファンの劣化・ホコリのチェック）"));
            foreach (var t in trend) sb.AppendLine($"- {t}");
        }

        if (!string.IsNullOrWhiteSpace(userQuestion))
        {
            sb.AppendLine();
            sb.AppendLine(L("## ユーザーからの質問"));
            sb.AppendLine(userQuestion.Trim());
        }
        return sb.ToString();
    }

    private static string ModeText(FanConfig f) => f.Mode switch
    {
        FanModes.Target => L($"目標温度 {f.TargetTemp:F0}°C"),
        FanModes.Fixed => L($"固定 {f.FixedDuty:F0}%"),
        FanModes.Follow => L("他のファンに連動"),
        _ => L("カーブ"),
    };

    private sealed record SeriesStats(double Min, double Avg, double P95, double Max);

    private static SeriesStats? Stats(HistoryStore h, string key)
    {
        if (!h.Has(key)) return null;
        var values = new List<double>(h.Count);
        for (int i = 0; i < h.Count; i++)
        {
            float v = h.ValueAt(key, i);
            if (!float.IsNaN(v)) values.Add(v);
        }
        if (values.Count == 0) return null;
        values.Sort();
        return new SeriesStats(values[0], values.Average(), values[(int)((values.Count - 1) * 0.95)], values[^1]);
    }
}
