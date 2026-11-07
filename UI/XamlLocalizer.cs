// ==================================================
// XamlLocalizer.cs - XAML に書いた日本語を、画面を作るときに画面の言語へ置き換える
//   XAML はそのまま日本語で書く（見出しは日本語の文そのもの。翻訳表は Core/Loc.cs）。
//   要素が画面に載る（Loaded）たびに、その要素の文字の値を訳す。
//   訳す値: TextBlock.Text・Content・Header・ToolTip・Window.Title・TextBox.Tag（入力欄の薄い案内に使っている）。
//   Binding で結び付けた値は訳さない（コードの側で L() を通す）。ただし Binding の StringFormat に日本語があれば訳す。
//   Tag はほかの要素では「選ばれた値」として使っているので訳さない（ファンカーブの「静音」ボタンなど）
// Dependencies: WPF / .NET 8 (MIT License)
// See CREDITS.txt for full license details.
// ==================================================

using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;

namespace ProFanController.UI;

public static class XamlLocalizer
{
    private static bool _registered;

    /// <summary>元の言葉（日本語）以外のときに 1 回だけ呼ぶ。以後に作る画面・部品すべてが対象</summary>
    public static void Register()
    {
        if (_registered) return;
        _registered = true;
        EventManager.RegisterClassHandler(typeof(FrameworkElement), FrameworkElement.LoadedEvent,
            new RoutedEventHandler((s, _) => { if (s is FrameworkElement fe) Translate(fe); }), handledEventsToo: true);
    }

    /// <summary>1 つの要素の文字を訳す（中の要素は、それぞれの Loaded で訳す）</summary>
    public static void Translate(FrameworkElement e)
    {
        switch (e)
        {
            case TextBlock tb:
                TranslateValue(tb, TextBlock.TextProperty);
                TranslateStringFormat(tb, TextBlock.TextProperty);
                break;
            case TextBox box:
                TranslateValue(box, FrameworkElement.TagProperty);
                break;
            case Window w:
                TranslateValue(w, Window.TitleProperty);
                break;
        }
        if (e is HeaderedContentControl hc) TranslateValue(hc, HeaderedContentControl.HeaderProperty);
        if (e is HeaderedItemsControl hi) TranslateValue(hi, HeaderedItemsControl.HeaderProperty);
        if (e is ContentControl cc and not Window) TranslateValue(cc, ContentControl.ContentProperty);
        TranslateValue(e, FrameworkElement.ToolTipProperty);

        // 閉じている選択欄の項目（ComboBoxItem など）は、開くまで画面に載らないので、ここで先に訳す
        // （選んでいる項目の文字は、閉じた選択欄にもそのまま表示されるため）
        if (e is ItemsControl ic && !ic.IsItemsSourceInUse())
            foreach (var item in ic.Items)
                if (item is FrameworkElement child && !child.IsLoaded) Translate(child);
    }

    private static bool IsItemsSourceInUse(this ItemsControl ic) => ic.ItemsSource is not null;

    /// <summary>文字の値（Binding・リソースなどの式でないもの）を訳す</summary>
    private static void TranslateValue(DependencyObject d, DependencyProperty p)
    {
        if (d.GetValue(p) is not string s || !HasNonAscii(s)) return;
        var source = DependencyPropertyHelper.GetValueSource(d, p);
        if (source.IsExpression || source.IsAnimated) return;
        string t = L(s);
        if (!ReferenceEquals(t, s) && t != s) d.SetCurrentValue(p, t);
    }

    /// <summary>{Binding …, StringFormat={}{0:F1} 倍} のような、日本語の入った書式を訳す（Binding を作り直す）</summary>
    private static void TranslateStringFormat(FrameworkElement e, DependencyProperty p)
    {
        if (BindingOperations.GetBinding(e, p) is not Binding b || b.StringFormat is not { } fmt || !HasNonAscii(fmt)) return;
        string t = L(fmt);
        if (t == fmt) return;
        var copy = new Binding
        {
            Path = b.Path,
            Mode = b.Mode,
            Converter = b.Converter,
            ConverterParameter = b.ConverterParameter,
            ConverterCulture = b.ConverterCulture,
            StringFormat = t,
            FallbackValue = b.FallbackValue,
            TargetNullValue = b.TargetNullValue,
            UpdateSourceTrigger = b.UpdateSourceTrigger,
        };
        if (b.Source is not null) copy.Source = b.Source;
        else if (b.RelativeSource is not null) copy.RelativeSource = b.RelativeSource;
        else if (!string.IsNullOrEmpty(b.ElementName)) copy.ElementName = b.ElementName;
        e.SetBinding(p, copy);
    }

    private static bool HasNonAscii(string s)
    {
        foreach (char c in s)
            if (c > 0x7F) return true;
        return false;
    }
}
