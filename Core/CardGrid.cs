// ==================================================
// CardGrid.cs - カードを並べるときの列の数と 1 列の幅の計算 (画面の幅に合わせて広げる。AdaptiveWrapPanel が使う)
// Dependencies: .NET 8 (MIT License)
// See CREDITS.txt for full license details.
// ==================================================

namespace ProFanController;

public static class CardGrid
{
    /// <summary>
    /// 幅 width に count 枚を並べるときの列の数と 1 列の幅。
    /// 列の数は「幅 ÷ minWidth」（カードの数より多くはしない）、1 列の幅は幅を等分した値（maxWidth まで）
    /// </summary>
    public static (int Columns, double Slot) Layout(double width, int count, double minWidth, double maxWidth)
    {
        minWidth = Math.Max(1, minWidth);
        if (count <= 0) return (1, minWidth);
        if (double.IsInfinity(width) || double.IsNaN(width)) return (count, minWidth);
        int cols = Math.Clamp((int)Math.Floor(width / minWidth), 1, count);
        double slot = Math.Min(width / cols, Math.Max(minWidth, maxWidth));
        // 1 列しか入らないほど狭いときは、その幅に収める（はみ出して切れないように）
        if (cols == 1) slot = Math.Min(slot, Math.Max(0, width));
        return (cols, slot);
    }
}
