// ==================================================
// LocalizationTests.cs - 画面の言葉の翻訳（Core/Loc.cs）と、翻訳表（Assets/Lang/*.txt）のテスト
// Dependencies: .NET 8 (MIT License)
// See CREDITS.txt for full license details.
// ==================================================

using System.Text.RegularExpressions;
using ProFanController;

namespace LogicTests;

public static class LocalizationTests
{
    public static void TestLanguageIsResolved()
    {
        T.Check(Localization.Resolve("", "ja-JP") == "ja", "Japanese Windows → Japanese");
        T.Check(Localization.Resolve("", "en-GB") == "en", "English Windows → English");
        T.Check(Localization.Resolve("", "zh-CN") == "zh-Hans" && Localization.Resolve("", "zh-SG") == "zh-Hans", "mainland China / Singapore → Simplified");
        T.Check(Localization.Resolve("", "zh-TW") == "zh-Hant" && Localization.Resolve("", "zh-HK") == "zh-Hant"
                && Localization.Resolve("", "zh-Hant-MO") == "zh-Hant", "Taiwan / Hong Kong / Hant → Traditional");
        T.Check(Localization.Resolve("", "pt-PT") == "pt-BR" && Localization.Resolve("", "de-AT") == "de" && Localization.Resolve("", "in-ID") == "id",
            "regional variants use the language");
        T.Check(Localization.Resolve("", "ar-SA") == "en", "an unsupported language falls back to English");
        T.Check(Localization.Resolve("fr", "ja-JP") == "fr", "the setting wins over Windows");
        T.Check(Localization.Resolve("xx", "ko-KR") == "ko", "an unknown setting is ignored");
        T.Check(Localization.Resolve("", "") == "ja", "no information → the source language");
    }

    public static void TestLookupFormatsAndFallsBack()
    {
        var table = string.Join("\n", new[]
        {
            Entry("保存しました", "Saved"),
            Entry("{0} 個のファン", "{0} fans"),
            Entry("温度 {0:F1} °C（{1}）", "{1}: {0:F1} °C"),
            Entry("{{設定}} {0}", "{{settings}} {0}"),
            Entry("{0} 分", "{5} broken"),
            Entry("2 行\n目", "two\nlines"),
        });
        try
        {
            Localization.UseTable("en", new StringReader(table));
            int n = 3;
            double t = 41.26;
            T.Check(L("保存しました") == "Saved", "plain text is translated");
            T.Check(L("知らない言葉") == "知らない言葉", "missing text stays in the source language");
            T.Check(L($"{n} 個のファン") == "3 fans", $"holes become numbered placeholders ({L($"{n} 個のファン")})");
            T.Check(L($"温度 {t:F1} °C（{"CPU"}）") == "CPU: 41.3 °C" || L($"温度 {t:F1} °C（{"CPU"}）") == "CPU: 41,3 °C",
                $"format specifiers are kept and placeholders can be reordered ({L($"温度 {t:F1} °C（{"CPU"}）")})");
            T.Check(L($"{{設定}} {n}") == "{settings} 3", $"literal braces are doubled in the key ({L($"{{設定}} {n}")})");
            T.Check(L($"{n} 分") == "3 分", "a broken translation falls back to the source text");
            T.Check(LF("{0} 個のファン", 2) == "2 fans", "LF translates a numbered format");
            T.Check(L("2 行\n目") == "two\nlines", "\\n in the table is a line break");
            T.Check(Localization.Has("保存しました") && !Localization.Has("知らない言葉"), "Has tells whether a translation exists");
        }
        finally
        {
            Localization.UseTable("ja", null);
        }
        T.Check(L("保存しました") == "保存しました" && L($"{1} 個のファン") == "1 個のファン", "back to Japanese after the test");
    }

    public static void TestEscapeRoundTrip()
    {
        string s = "a\\b\nc\td\re";
        string line = Localization.HashText(s) + "\t" + Localization.Escape(s);
        var t = Localization.Parse(new StringReader("# comment\n\n" + line + "\n"));
        T.Check(t.Count == 1 && t[Localization.Hash(s)] == s, "escaped text reads back unchanged");
    }

    /// <summary>
    /// 翻訳表が見出しの一覧（keys.tsv）と合っているか: 知らない見出しがない・番号（{0} {1:F1}）がそろっている・見出しが重ならない。
    /// 言語ごとの訳した割合も出す
    /// </summary>
    public static void TestTranslationTables()
    {
        string? dir = FindLangDir();
        T.Check(dir is not null, "Assets/Lang is found");
        if (dir is null) return;
        var keys = ReadKeys(Path.Combine(dir, "keys.tsv"));
        T.Check(keys.Count > 1000, $"keys.tsv lists the UI text ({keys.Count} keys)");
        T.Check(keys.Keys.All(h => Localization.Hash(keys[h]) == h), "every key's hash matches its text");

        foreach (var file in Directory.GetFiles(dir, "*.txt").OrderBy(f => f, StringComparer.Ordinal))
        {
            string code = Path.GetFileNameWithoutExtension(file);
            T.Check(Localization.Languages.Any(l => l.Code == code), $"{code}: a supported language");
            var table = Localization.Parse(new StringReader(File.ReadAllText(file)));
            var unknown = table.Keys.Where(h => !keys.ContainsKey(h)).ToList();
            var badPlaceholders = table.Where(kv => keys.ContainsKey(kv.Key) && Placeholders(kv.Value) != Placeholders(keys[kv.Key]))
                .Select(kv => $"{kv.Key:x8} 「{keys[kv.Key]}」→「{kv.Value}」").ToList();
            var empty = table.Where(kv => kv.Value.Trim().Length == 0).Select(kv => kv.Key.ToString("x8")).ToList();
            int done = keys.Keys.Count(table.ContainsKey);
            T.Check(unknown.Count == 0, $"{code}: no translations for text that is gone ({unknown.Count}: {string.Join(", ", unknown.Take(5).Select(h => h.ToString("x8")))})");
            T.Check(badPlaceholders.Count == 0, $"{code}: placeholders match the source ({badPlaceholders.Count}) {string.Join(" / ", badPlaceholders.Take(3))}");
            T.Check(empty.Count == 0, $"{code}: no empty translations ({string.Join(", ", empty.Take(5))})");
            Console.WriteLine($"     {code,-8} {done,5} / {keys.Count} translated ({100.0 * done / keys.Count:F1}%)");
        }
    }

    private static string Entry(string ja, string text) => Localization.HashText(ja) + "\t" + Localization.Escape(text);

    /// <summary>番号の並び（{0} {1:F1} …。{{ }} は文字なので数えない）。順番は問わない</summary>
    private static string Placeholders(string s)
    {
        string plain = s.Replace("{{", "").Replace("}}", "");
        return string.Join(" ", Regex.Matches(plain, @"\{\d+(,-?\d+)?(:[^{}]*)?\}").Select(m => m.Value).OrderBy(v => v, StringComparer.Ordinal));
    }

    private static Dictionary<uint, string> ReadKeys(string path)
    {
        var keys = new Dictionary<uint, string>();
        foreach (var line in File.ReadLines(path))
        {
            if (line.StartsWith('#') || line.Length < 10) continue;
            var parts = line.Split('\t');
            keys[uint.Parse(parts[0], System.Globalization.NumberStyles.HexNumber)] = Localization.Unescape(parts[1]);
        }
        return keys;
    }

    private static string? FindLangDir()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d is not null && !File.Exists(Path.Combine(d.FullName, "ProFanController.csproj"))) d = d.Parent;
        return d is null ? null : Path.Combine(d.FullName, "Assets", "Lang");
    }
}
