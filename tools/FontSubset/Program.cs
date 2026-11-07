// ==================================================
// FontSubset - 同梱の日本語フォント（ProFan Maru）から、使わない文字の形を取り除く開発用の道具
//   (グリフの番号はそのまま残し、使わない文字の形だけを空にして、文字と形の対応表（cmap）から外す。
//    対応表にない文字は、WPF が後ろのフォント（Yu Gothic UI）で描くので、□ にはならない)
// Dependencies: .NET 8 (MIT License)
// See CREDITS.txt for full license details.
// ==================================================
//
// 使い方（リポジトリの一番上で）:
//   dotnet run --project tools/FontSubset -- <元のフォント.ttf> <書き出す.ttf> <リポジトリのフォルダー>
// 残す文字:
//   ・アプリのソース（*.cs・*.xaml）と案内（README.md・Distribution/*.txt）に出てくる文字すべて
//   ・JIS X 0208 第 1 水準の漢字（2,965 字。よく使う漢字と、人名の漢字の多く）
//   ・英数字・ラテン文字・記号・ひらがな・カタカナ・全角の文字（下の Ranges）
// グリフの番号を変えないので、GSUB・GPOS などの表（字形の置き換え・文字の詰め）はそのまま正しく使える。
// 対応表にない（番号だけの）グリフと、残すグリフが部品として使うグリフ（複合グリフ）は残す

using System.Buffers.Binary;
using System.Text;

if (args.Length < 3)
{
    Console.Error.WriteLine("usage: FontSubset <in.ttf> <out.ttf> <repo root>");
    return 2;
}
var font = File.ReadAllBytes(args[0]);
var keepChars = FontSubsetter.CharsToKeep(args[2]);
var result = FontSubsetter.Subset(font, keepChars, out var report);
File.WriteAllBytes(args[1], result);
Console.WriteLine(report);
Console.WriteLine($"{Path.GetFileName(args[0])}: {font.Length / 1024} KB → {result.Length / 1024} KB");
return 0;

static class FontSubsetter
{
    /// <summary>まとめて残す文字の範囲（英数字・記号・かな・全角など）</summary>
    private static readonly (int From, int To)[] Ranges =
    {
        (0x0020, 0x024F), // 英数字・ラテン文字
        (0x0370, 0x03FF), // ギリシャ文字（°・μ など）
        (0x0400, 0x04FF), // キリル文字（JIS の記号に含まれるもの）
        (0x2000, 0x2BFF), // 句読点・単位・矢印・数学記号・罫線・図形・記号
        (0x3000, 0x30FF), // 全角の記号・ひらがな・カタカナ
        (0x31F0, 0x31FF), // 小さいカタカナ
        (0x3200, 0x33FF), // 丸数字・単位の記号
        (0xFE30, 0xFE4F), // 縦書き用の記号
        (0xFF00, 0xFFEF), // 全角・半角の英数字とカタカナ
    };

    /// <summary>残す文字の一覧</summary>
    public static HashSet<int> CharsToKeep(string repoRoot)
    {
        var keep = new HashSet<int>();
        foreach (var (from, to) in Ranges)
            for (int c = from; c <= to; c++) keep.Add(c);

        // JIS X 0208 第 1 水準の漢字（シフト JIS 0x889F〜0x9872）
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        var sjis = Encoding.GetEncoding(932, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback);
        for (int hi = 0x88; hi <= 0x98; hi++)
            for (int lo = 0x40; lo <= 0xFC; lo++)
            {
                int code = (hi << 8) | lo;
                if (lo == 0x7F || code < 0x889F || code > 0x9872) continue;
                try
                {
                    string s = sjis.GetString(new[] { (byte)hi, (byte)lo });
                    if (s.Length == 1) keep.Add(s[0]);
                }
                catch (DecoderFallbackException) { /* 割り当てのない番号 */ }
            }

        // アプリの画面と案内に出てくる文字すべて
        var files = Directory.EnumerateFiles(repoRoot, "*.*", SearchOption.AllDirectories)
            .Where(f => (f.EndsWith(".cs", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".xaml", StringComparison.OrdinalIgnoreCase)
                         || f.EndsWith(".txt", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".md", StringComparison.OrdinalIgnoreCase))
                        && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")
                        && !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}"));
        foreach (var f in files)
        {
            string text = File.ReadAllText(f);
            for (int i = 0; i < text.Length; i++)
            {
                int c = char.IsSurrogatePair(text, i) ? char.ConvertToUtf32(text, i++) : text[i];
                if (c >= 0x80) keep.Add(c);
            }
        }
        return keep;
    }

    private sealed record Table(string Tag, int Offset, int Length);

    public static byte[] Subset(byte[] font, HashSet<int> keepChars, out string report)
    {
        var tables = ReadTables(font);
        var head = tables["head"];
        int numGlyphs = U16(font, tables["maxp"].Offset + 4);
        bool longLoca = BinaryPrimitives.ReadInt16BigEndian(font.AsSpan(head.Offset + 50)) == 1;
        var loca = new int[numGlyphs + 1];
        for (int i = 0; i <= numGlyphs; i++)
            loca[i] = longLoca ? (int)U32(font, tables["loca"].Offset + 4 * i) : 2 * U16(font, tables["loca"].Offset + 2 * i);
        var cmap = ReadCmap(font, tables["cmap"].Offset);
        int glyf = tables["glyf"].Offset;

        // 残すグリフ: .notdef・残す文字の形・対応表にないグリフ（字形の置き換え先など）、それらが部品として使うグリフ
        var keep = new bool[numGlyphs];
        keep[0] = true;
        var mapped = new bool[numGlyphs];
        foreach (var (c, g) in cmap)
        {
            if (g >= numGlyphs) continue;
            mapped[g] = true;
            if (keepChars.Contains(c)) keep[g] = true;
        }
        for (int g = 0; g < numGlyphs; g++) if (!mapped[g]) keep[g] = true;
        var stack = new Stack<int>(Enumerable.Range(0, numGlyphs).Where(g => keep[g]));
        while (stack.Count > 0)
        {
            int g = stack.Pop();
            foreach (int part in Components(font, glyf + loca[g], loca[g + 1] - loca[g]))
                if (part < numGlyphs && !keep[part]) { keep[part] = true; stack.Push(part); }
        }

        // 新しい glyf・loca（長い形式）: 残さないグリフは長さ 0（空の形）
        using var glyfOut = new MemoryStream();
        var newLoca = new byte[4 * (numGlyphs + 1)];
        for (int g = 0; g < numGlyphs; g++)
        {
            BinaryPrimitives.WriteUInt32BigEndian(newLoca.AsSpan(4 * g), (uint)glyfOut.Length);
            if (!keep[g]) continue;
            glyfOut.Write(font, glyf + loca[g], loca[g + 1] - loca[g]);
            while (glyfOut.Length % 4 != 0) glyfOut.WriteByte(0);
        }
        BinaryPrimitives.WriteUInt32BigEndian(newLoca.AsSpan(4 * numGlyphs), (uint)glyfOut.Length);

        // 新しい cmap: 残す文字だけ（形を空にした文字は、対応表から外して後ろのフォントで描かせる）
        var kept = cmap.Where(p => keepChars.Contains(p.Key) && p.Value < numGlyphs && keep[p.Value]).OrderBy(p => p.Key).ToList();
        var newCmap = BuildCmap(kept);

        var headBytes = font.AsSpan(head.Offset, head.Length).ToArray();
        BinaryPrimitives.WriteInt16BigEndian(headBytes.AsSpan(50), 1); // loca は長い形式
        BinaryPrimitives.WriteUInt32BigEndian(headBytes.AsSpan(8), 0); // checkSumAdjustment は最後に入れる

        var outTables = new SortedDictionary<string, byte[]>(StringComparer.Ordinal);
        foreach (var (tag, t) in tables)
        {
            if (tag == "DSIG") continue; // 署名は中身を変えると無効になる
            outTables[tag] = tag switch
            {
                "glyf" => glyfOut.ToArray(),
                "loca" => newLoca,
                "cmap" => newCmap,
                "head" => headBytes,
                _ => font.AsSpan(t.Offset, t.Length).ToArray(),
            };
        }
        var output = WriteFont(font, outTables);
        report = $"glyphs {numGlyphs}: kept {keep.Count(k => k)}, emptied {keep.Count(k => !k)}; characters {cmap.Count} → {kept.Count}";
        return output;
    }

    private static Dictionary<string, Table> ReadTables(byte[] f)
    {
        int n = U16(f, 4);
        var d = new Dictionary<string, Table>();
        for (int i = 0; i < n; i++)
        {
            int r = 12 + 16 * i;
            d[Encoding.ASCII.GetString(f, r, 4)] = new Table(Encoding.ASCII.GetString(f, r, 4), (int)U32(f, r + 8), (int)U32(f, r + 12));
        }
        return d;
    }

    /// <summary>cmap の Windows Unicode の表（形式 12 があればそれ、なければ形式 4）から、文字 → グリフ番号</summary>
    private static Dictionary<int, int> ReadCmap(byte[] f, int cmap)
    {
        int n = U16(f, cmap + 2);
        int best = -1, bestFormat = 0;
        for (int i = 0; i < n; i++)
        {
            int r = cmap + 4 + 8 * i;
            int platform = U16(f, r), encoding = U16(f, r + 2);
            int off = cmap + (int)U32(f, r + 4);
            int format = U16(f, off);
            if (platform == 3 && encoding == 10 && format == 12) { best = off; bestFormat = 12; }
            else if (platform == 3 && encoding == 1 && format == 4 && bestFormat != 12) { best = off; bestFormat = 4; }
        }
        if (best < 0) throw new InvalidDataException("no Windows Unicode cmap");
        var map = new Dictionary<int, int>();
        if (bestFormat == 12)
        {
            uint groups = U32(f, best + 12);
            for (int g = 0; g < groups; g++)
            {
                int r = best + 16 + 12 * g;
                uint start = U32(f, r), end = U32(f, r + 4), gid = U32(f, r + 8);
                for (uint c = start; c <= end; c++) map[(int)c] = (int)(gid + (c - start));
            }
        }
        else
        {
            int segX2 = U16(f, best + 6), seg = segX2 / 2;
            int ends = best + 14, starts = ends + segX2 + 2, deltas = starts + segX2, offsets = deltas + segX2;
            for (int s = 0; s < seg; s++)
            {
                int end = U16(f, ends + 2 * s), start = U16(f, starts + 2 * s);
                short delta = BinaryPrimitives.ReadInt16BigEndian(f.AsSpan(deltas + 2 * s));
                int ro = U16(f, offsets + 2 * s);
                for (int c = start; c <= end && c != 0xFFFF; c++)
                {
                    int g = ro == 0 ? (c + delta) & 0xFFFF : U16(f, offsets + 2 * s + ro + 2 * (c - start));
                    if (ro != 0 && g != 0) g = (g + delta) & 0xFFFF;
                    if (g != 0) map[c] = g;
                }
            }
        }
        return map;
    }

    /// <summary>複合グリフが部品として使うグリフの番号</summary>
    private static IEnumerable<int> Components(byte[] f, int at, int length)
    {
        if (length < 10 || BinaryPrimitives.ReadInt16BigEndian(f.AsSpan(at)) >= 0) yield break;
        int p = at + 10;
        while (true)
        {
            int flags = U16(f, p), gid = U16(f, p + 2);
            yield return gid;
            p += 4 + ((flags & 0x0001) != 0 ? 4 : 2);
            if ((flags & 0x0008) != 0) p += 2;
            else if ((flags & 0x0040) != 0) p += 4;
            else if ((flags & 0x0080) != 0) p += 8;
            if ((flags & 0x0020) == 0) break;
        }
    }

    /// <summary>新しい cmap: (0,3) と (3,1) は形式 4（基本多言語面）、(3,10) は形式 12</summary>
    private static byte[] BuildCmap(List<KeyValuePair<int, int>> kept)
    {
        var f4 = Format4(kept.Where(p => p.Key <= 0xFFFF).ToList());
        var f12 = Format12(kept);
        int header = 4 + 8 * 3;
        var o = new byte[header + f4.Length + f12.Length];
        BinaryPrimitives.WriteUInt16BigEndian(o.AsSpan(0), 0);
        BinaryPrimitives.WriteUInt16BigEndian(o.AsSpan(2), 3);
        void Rec(int i, int platform, int encoding, int offset)
        {
            BinaryPrimitives.WriteUInt16BigEndian(o.AsSpan(4 + 8 * i), (ushort)platform);
            BinaryPrimitives.WriteUInt16BigEndian(o.AsSpan(6 + 8 * i), (ushort)encoding);
            BinaryPrimitives.WriteUInt32BigEndian(o.AsSpan(8 + 8 * i), (uint)offset);
        }
        Rec(0, 0, 3, header);
        Rec(1, 3, 1, header);
        Rec(2, 3, 10, header + f4.Length);
        f4.CopyTo(o, header);
        f12.CopyTo(o, header + f4.Length);
        return o;
    }

    private static byte[] Format4(List<KeyValuePair<int, int>> map)
    {
        // 連続した文字の並びを 1 区間にし、区間ごとにグリフ番号の配列で引く（最後に 0xFFFF の区間）
        var segs = new List<(int Start, int End)>();
        foreach (var (c, _) in map)
        {
            if (segs.Count > 0 && segs[^1].End == c - 1) segs[^1] = (segs[^1].Start, c);
            else segs.Add((c, c));
        }
        segs.Add((0xFFFF, 0xFFFF));
        int seg = segs.Count;
        var gids = map.ToDictionary(p => p.Key, p => p.Value);
        var glyphArray = new List<ushort>();
        var rangeOffsets = new int[seg];
        int arrayStartIndex = 0;
        for (int s = 0; s < seg - 1; s++)
        {
            rangeOffsets[s] = arrayStartIndex;
            for (int c = segs[s].Start; c <= segs[s].End; c++) glyphArray.Add((ushort)gids[c]);
            arrayStartIndex = glyphArray.Count;
        }
        int length = 16 + 8 * seg + 2 * glyphArray.Count;
        var o = new byte[length];
        void W(int at, int v) => BinaryPrimitives.WriteUInt16BigEndian(o.AsSpan(at), (ushort)v);
        W(0, 4); W(2, length); W(4, 0);
        int segX2 = 2 * seg;
        int search = 2 * (1 << (int)Math.Floor(Math.Log2(seg)));
        W(6, segX2); W(8, search); W(10, (int)Math.Floor(Math.Log2(seg))); W(12, segX2 - search);
        int ends = 14, starts = ends + segX2 + 2, deltas = starts + segX2, offsets = deltas + segX2, array = offsets + segX2;
        for (int s = 0; s < seg; s++)
        {
            W(ends + 2 * s, segs[s].End);
            W(starts + 2 * s, segs[s].Start);
            if (s == seg - 1)
            {
                W(deltas + 2 * s, 1); // 0xFFFF → 0
                W(offsets + 2 * s, 0);
                continue;
            }
            W(deltas + 2 * s, 0);
            // idRangeOffset: この欄の位置から、配列の中のこの区間の先頭までの距離（バイト）
            int target = array + 2 * rangeOffsets[s];
            W(offsets + 2 * s, target - (offsets + 2 * s));
        }
        for (int i = 0; i < glyphArray.Count; i++) W(array + 2 * i, glyphArray[i]);
        return o;
    }

    private static byte[] Format12(List<KeyValuePair<int, int>> map)
    {
        var groups = new List<(int Start, int End, int Gid)>();
        foreach (var (c, g) in map)
        {
            if (groups.Count > 0 && groups[^1].End == c - 1 && groups[^1].Gid + (c - groups[^1].Start) == g) groups[^1] = (groups[^1].Start, c, groups[^1].Gid);
            else groups.Add((c, c, g));
        }
        var o = new byte[16 + 12 * groups.Count];
        BinaryPrimitives.WriteUInt16BigEndian(o.AsSpan(0), 12);
        BinaryPrimitives.WriteUInt32BigEndian(o.AsSpan(4), (uint)o.Length);
        BinaryPrimitives.WriteUInt32BigEndian(o.AsSpan(12), (uint)groups.Count);
        for (int i = 0; i < groups.Count; i++)
        {
            int r = 16 + 12 * i;
            BinaryPrimitives.WriteUInt32BigEndian(o.AsSpan(r), (uint)groups[i].Start);
            BinaryPrimitives.WriteUInt32BigEndian(o.AsSpan(r + 4), (uint)groups[i].End);
            BinaryPrimitives.WriteUInt32BigEndian(o.AsSpan(r + 8), (uint)groups[i].Gid);
        }
        return o;
    }

    /// <summary>表を 4 バイト境界に並べてフォントを書き出し、表ごとの検査値と head の checkSumAdjustment を入れる</summary>
    private static byte[] WriteFont(byte[] original, SortedDictionary<string, byte[]> tables)
    {
        int n = tables.Count;
        int dir = 12 + 16 * n;
        int size = dir + tables.Values.Sum(t => (t.Length + 3) & ~3);
        var o = new byte[size];
        Array.Copy(original, 0, o, 0, 4); // sfntVersion
        int search = 16 * (1 << (int)Math.Floor(Math.Log2(n)));
        BinaryPrimitives.WriteUInt16BigEndian(o.AsSpan(4), (ushort)n);
        BinaryPrimitives.WriteUInt16BigEndian(o.AsSpan(6), (ushort)search);
        BinaryPrimitives.WriteUInt16BigEndian(o.AsSpan(8), (ushort)Math.Floor(Math.Log2(n)));
        BinaryPrimitives.WriteUInt16BigEndian(o.AsSpan(10), (ushort)(16 * n - search));
        int at = dir, i = 0, headAt = -1;
        foreach (var (tag, data) in tables)
        {
            int r = 12 + 16 * i++;
            Encoding.ASCII.GetBytes(tag).CopyTo(o, r);
            BinaryPrimitives.WriteUInt32BigEndian(o.AsSpan(r + 4), Checksum(data));
            BinaryPrimitives.WriteUInt32BigEndian(o.AsSpan(r + 8), (uint)at);
            BinaryPrimitives.WriteUInt32BigEndian(o.AsSpan(r + 12), (uint)data.Length);
            data.CopyTo(o, at);
            if (tag == "head") headAt = at;
            at += (data.Length + 3) & ~3;
        }
        uint adjust = unchecked(0xB1B0AFBA - Checksum(o));
        BinaryPrimitives.WriteUInt32BigEndian(o.AsSpan(headAt + 8), adjust);
        return o;
    }

    private static uint Checksum(byte[] d)
    {
        uint sum = 0;
        for (int i = 0; i < d.Length; i += 4)
        {
            uint v = 0;
            for (int k = 0; k < 4; k++) v = (v << 8) | (i + k < d.Length ? d[i + k] : 0u);
            sum = unchecked(sum + v);
        }
        return sum;
    }

    private static int U16(byte[] f, int at) => BinaryPrimitives.ReadUInt16BigEndian(f.AsSpan(at));
    private static uint U32(byte[] f, int at) => BinaryPrimitives.ReadUInt32BigEndian(f.AsSpan(at));
}
