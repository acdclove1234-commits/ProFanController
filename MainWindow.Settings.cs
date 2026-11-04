// ==================================================
// MainWindow.Settings.cs - 設定画面・センサー画面 (プロファイル管理・自動切り替え・仮想センサー・起動設定)
// Dependencies: WPF / .NET 8 (MIT License)
// See CREDITS.txt for full license details.
// ==================================================

using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using ProFanController.UI;

namespace ProFanController;

public partial class MainWindow
{
    private readonly ObservableCollection<SensorGroupVm> _sensorGroups = new();
    private readonly Dictionary<string, SensorRowVm> _sensorRows = new(StringComparer.OrdinalIgnoreCase);
    private readonly ObservableCollection<VirtualRowVm> _virtualRows = new();

    // =====================================================================
    // プロファイル
    // =====================================================================

    /// <summary>サイドバー・トレイ・自動切り替え表示を、今のプロファイル状態に合わせる</summary>
    public void UpdateProfileIndicators()
    {
        _syncing = true;
        SidebarProfileCombo.SelectedItem = Config.FindProfile(Config.ActiveProfile);
        _syncing = false;

        if (_controller.EffectiveReason is { } reason)
        {
            AutoSwitchText.Text = $"{reason} → {_controller.EffectiveProfile}";
            AutoSwitchText.Visibility = Visibility.Visible;
            AutoSwitchStatus.Text = _controller.AutoProcess is { } process && reason.StartsWith("自動")
                ? $"現在「{process}」が起動中のため「{_controller.EffectiveProfile}」を使っています"
                : "";
        }
        else
        {
            AutoSwitchText.Visibility = Visibility.Collapsed;
            AutoSwitchStatus.Text = "";
        }
    }

    private void OnSidebarProfileChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncing || SidebarProfileCombo.SelectedItem is not ProfileConfig p) return;
        FlushCommit();
        _controller.SetActiveProfile(p.Name);
    }

    private void OnUseProfileClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: ProfileConfig p }) return;
        FlushCommit();
        _controller.SetActiveProfile(p.Name);
    }

    private string UniqueProfileName(string baseName)
    {
        string name = baseName;
        for (int i = 2; Config.FindProfile(name) is not null; i++) name = $"{baseName} {i}";
        return name;
    }

    private void OnNewProfileClick(object sender, RoutedEventArgs e)
    {
        var source = Config.FindProfile(Config.ActiveProfile) ?? Config.Profiles.First();
        AddProfileCopy(source, UniqueProfileName("新しいプロファイル"));
    }

    private void OnDuplicateProfileClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: ProfileConfig p }) AddProfileCopy(p, UniqueProfileName($"{p.Name} のコピー"));
    }

    private void AddProfileCopy(ProfileConfig source, string name)
    {
        var copy = new ProfileConfig { Name = name, Fans = new ObservableCollection<FanConfig>(source.Fans.Select(f => f.DeepCopy())) };
        Config.Profiles.Add(copy);
        WatchConfig();
        ScheduleCommit();
    }

    private void OnDeleteProfileClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: ProfileConfig p }) return;
        if (Config.Profiles.Count <= 1)
        {
            MessageBox.Show(this, "プロファイルは最低 1 つ必要です。", "ProFanController", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        // このプロファイルを使っているルールも一緒に消える（スケジュールは残りのプロファイルに切り替える）ことを、先に伝える
        int autoRules = Config.AutoSwitchRules.Count(r => r.ProfileName == p.Name);
        int schedules = Config.ScheduleRules.Count(r => r.ProfileName == p.Name);
        bool quiet = Config.Quick.QuietProfile == p.Name;
        string fallback = Config.Profiles.First(x => x != p).Name;
        var notes = new List<string>();
        if (autoRules > 0) notes.Add($"・アプリによる自動切り替えのルール {autoRules} 件を削除します");
        if (schedules > 0) notes.Add($"・スケジュール {schedules} 件は「{fallback}」に切り替えます");
        if (quiet) notes.Add($"・ワンタッチ操作の「静音」は「{fallback}」を使うようにします");
        string detail = notes.Count > 0 ? "\n\n" + string.Join("\n", notes) + "\n\n（「元に戻す」で取り消せます）" : "";
        if (MessageBox.Show(this, $"プロファイル「{p.Name}」を削除しますか？{detail}", "ProFanController", MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK)
            return;

        Config.Profiles.Remove(p);
        if (Config.ActiveProfile == p.Name) Config.ActiveProfile = Config.Profiles[0].Name;
        foreach (var r in Config.AutoSwitchRules.Where(r => r.ProfileName == p.Name).ToList()) Config.AutoSwitchRules.Remove(r);
        foreach (var r in Config.ScheduleRules.Where(r => r.ProfileName == p.Name)) r.ProfileName = fallback;
        if (quiet) Config.Quick.QuietProfile = fallback;
        if (EditProfile is null) SelectEditProfile(Config.ActiveProfile, null, 0);
        WatchConfig();
        ScheduleCommit();
        UpdateProfileIndicators();
    }

    // =====================================================================
    // 自動切り替えルール
    // =====================================================================

    private void OnPickRunningAppClick(object sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu { PlacementTarget = (UIElement)sender, Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom };
        foreach (var app in ProfileSwitcher.RunningApps())
        {
            var item = new MenuItem { Header = app };
            item.Click += (_, _) => NewRuleProcess.Text = app;
            menu.Items.Add(item);
        }
        if (menu.Items.Count == 0) menu.Items.Add(new MenuItem { Header = "ウィンドウのあるアプリが見つかりません", IsEnabled = false });
        menu.MaxHeight = 420;
        menu.IsOpen = true;
    }

    private void OnAddRuleClick(object sender, RoutedEventArgs e)
    {
        string process = ProfileSwitcher.Normalize(NewRuleProcess.Text);
        if (process.Length == 0 || NewRuleProfile.SelectedItem is not ProfileConfig p)
        {
            MessageBox.Show(this, "アプリ名とプロファイルを指定してください。", "ProFanController", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        Config.AutoSwitchRules.Add(new AutoSwitchRule { ProcessName = process, ProfileName = p.Name });
        NewRuleProcess.Text = "";
        WatchConfig();
        ScheduleCommit();
    }

    private void OnRemoveRuleClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: AutoSwitchRule r }) return;
        Config.AutoSwitchRules.Remove(r);
        WatchConfig();
        ScheduleCommit();
    }

    // =====================================================================
    // 仮想センサー・センサー一覧
    // =====================================================================

    private void RebuildSensorGroups()
    {
        if (_catalog is null) return;
        _sensorGroups.Clear();
        _sensorRows.Clear();
        foreach (var g in _catalog.Groups)
        {
            var vm = new SensorGroupVm { Name = g.Name, HardwareType = g.HardwareType };
            foreach (var s in g.Sensors)
            {
                var row = new SensorRowVm { Info = s };
                vm.Rows.Add(row);
                _sensorRows[s.Id] = row;
            }
            _sensorGroups.Add(vm);
        }

        // 仮想センサーの材料（実センサーのみ）
        var physical = _sensorOptions.Where(o => !o.Id.StartsWith(VirtualSensorConfig.IdPrefix)).ToList();
        VirtualSourcePicker.ItemsSource = physical.Select(o => new SourcePickVm { Option = o }).ToList();
        DeltaA.ItemsSource = physical;
        DeltaB.ItemsSource = physical;
    }

    private void RebuildVirtualRows()
    {
        _virtualRows.Clear();
        foreach (var vs in Config.VirtualSensors)
        {
            var names = vs.Sources.Select(id => FindSensorOption(id)?.Name ?? id);
            string sources = vs.Kind == VirtualSensorKinds.Delta ? string.Join(" − ", names) : string.Join(" ／ ", names);
            _virtualRows.Add(new VirtualRowVm { Config = vs, SourcesText = sources });
        }
    }

    private void OnVirtualKindChanged(object sender, SelectionChangedEventArgs e)
    {
        if (DeltaPicker is null) return;
        bool delta = (NewVirtualKind.SelectedValue as string) == VirtualSensorKinds.Delta;
        DeltaPicker.Visibility = delta ? Visibility.Visible : Visibility.Collapsed;
        VirtualSourcePicker.Visibility = delta ? Visibility.Collapsed : Visibility.Visible;
    }

    private void OnAddVirtualClick(object sender, RoutedEventArgs e)
    {
        string kind = NewVirtualKind.SelectedValue as string ?? VirtualSensorKinds.Max;
        List<string> sources;
        if (kind == VirtualSensorKinds.Delta)
        {
            if (DeltaA.SelectedItem is not SensorOption a || DeltaB.SelectedItem is not SensorOption b || a.Id == b.Id)
            {
                MessageBox.Show(this, "A と B に別々のセンサーを選んでください。", "ProFanController", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            sources = new() { a.Id, b.Id };
        }
        else
        {
            sources = (VirtualSourcePicker.ItemsSource as IEnumerable<SourcePickVm> ?? Enumerable.Empty<SourcePickVm>())
                .Where(s => s.Selected).Select(s => s.Option.Id).ToList();
            if (sources.Count < 2)
            {
                MessageBox.Show(this, "2 つ以上のセンサーを選んでください。", "ProFanController", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
        }

        var vs = new VirtualSensorConfig
        {
            Id = VirtualSensorConfig.NewId(),
            Name = string.IsNullOrWhiteSpace(NewVirtualName.Text) ? "仮想センサー" : NewVirtualName.Text.Trim(),
            Kind = kind,
            Sources = new ObservableCollection<string>(sources),
        };
        Config.VirtualSensors.Add(vs);
        foreach (var s in VirtualSourcePicker.ItemsSource as IEnumerable<SourcePickVm> ?? Enumerable.Empty<SourcePickVm>()) s.Selected = false;
        AfterVirtualSensorsChanged();
        ShowAlert($"仮想センサー「{vs.Name}」を作成しました。ファンカーブの「連動する温度センサー」で選べます。", AlertLevel.Info);
    }

    private void OnRemoveVirtualClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: VirtualSensorConfig vs }) return;
        bool used = Config.Profiles.SelectMany(p => p.Fans).SelectMany(f => f.Curves).Any(c => c.SensorId == vs.Id);
        string msg = used
            ? $"「{vs.Name}」はファンカーブで使われています。削除すると、そのカーブはセンサー異常（全開）になります。削除しますか？"
            : $"「{vs.Name}」を削除しますか？";
        if (MessageBox.Show(this, msg, "ProFanController", MessageBoxButton.OKCancel, used ? MessageBoxImage.Warning : MessageBoxImage.Question) != MessageBoxResult.OK)
            return;
        Config.VirtualSensors.Remove(vs);
        foreach (var c in Config.DashboardCards.Where(c => c.SensorId == vs.Id).ToList()) Config.DashboardCards.Remove(c);
        AfterVirtualSensorsChanged();
    }

    private void AfterVirtualSensorsChanged()
    {
        RebuildSensorOptions();
        RebuildVirtualRows();
        RebuildTempCards();
        RegisterHistoryNames();
        WatchConfig();
        ScheduleCommit();
        if (SelectedCurve is { } c) ShowCurve(c);
    }

    // =====================================================================
    // 起動・ウィジェット・ファイル
    // =====================================================================

    private async void OnAutoStartClick(object sender, RoutedEventArgs e)
    {
        bool want = AutoStartSwitch.IsChecked == true;
        AutoStartSwitch.IsEnabled = false;
        bool ok = await Task.Run(() => want ? AutoStart.Enable() : AutoStart.Disable());
        AutoStartSwitch.IsChecked = await Task.Run(AutoStart.IsEnabled);
        AutoStartSwitch.IsEnabled = true;

        if (!ok)
        {
            MessageBox.Show(this, "タスクスケジューラへの登録に失敗しました。ログを確認してください。", "ProFanController",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        else if (want && AppContext.BaseDirectory.Contains(@"\bin\Debug\", StringComparison.OrdinalIgnoreCase))
        {
            MessageBox.Show(this,
                "開発用フォルダ（bin\\Debug）の exe を登録しました。\n" +
                "ビルドし直すと場所が変わることがあるため、「発行」した exe から登録し直すことをおすすめします。",
                "ProFanController", MessageBoxButton.OK, MessageBoxImage.Information);
        }
    }

    private void OnWidgetSwitchClick(object sender, RoutedEventArgs e) => WidgetToggleRequested?.Invoke();

    public void SetWidgetSwitch(bool visible) => WidgetSwitch.IsChecked = visible;

    private void OnOpenWizardClick(object sender, RoutedEventArgs e) => OpenWizard();

    public void OpenWizard()
    {
        if (_catalog is null)
        {
            MessageBox.Show(this, "ハードウェアの初期化が終わるまでお待ちください。", "ProFanController", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        FlushCommit();
        var wizard = new SetupWizard(_controller, _catalog, _sensorOptions.ToList()) { Owner = this, Icon = Icon };
        _wizard = wizard;
        try
        {
            if (wizard.ShowDialog() == true)
            {
                ShowAlert("セットアップが完了しました。気に入らなければ、ファンカーブ画面の「元に戻す」で以前の設定に戻せます。", AlertLevel.Info);
                NavDashboard.IsChecked = true;
            }
        }
        finally
        {
            _wizard = null;
        }
    }

    private SetupWizard? _wizard;

    /// <summary>ウィザード表示中は回転数の表示を更新する</summary>
    public void ForwardSnapshotToWizard(EngineSnapshot s) => _wizard?.OnSnapshot(s);

    private void OnOpenFolderClick(object sender, RoutedEventArgs e)
        => SystemTool.Open(AppContext.BaseDirectory);

    private void OnOpenLogsClick(object sender, RoutedEventArgs e)
    {
        Directory.CreateDirectory(Logger.Directory);
        SystemTool.Open(Logger.Directory);
    }
}

/// <summary>センサー画面の仮想センサー 1 行分</summary>
public sealed class VirtualRowVm : ObservableObject
{
    private string _valueText = "--";

    public required VirtualSensorConfig Config { get; init; }
    public required string SourcesText { get; init; }
    public string ValueText { get => _valueText; private set => Set(ref _valueText, value); }

    public void Update(IReadOnlyDictionary<string, float?> values)
        => ValueText = values.TryGetValue(Config.Id, out var v) && v.HasValue ? $"{v.Value:F1} °C" : "--";
}

// ----- 監視・自動化まわりの設定（スケジュール・電源プラン・ワンタッチ・スマホ） -----
public partial class MainWindow
{
    /// <summary>プロファイルごとの電源プランの選択肢（先頭は「変えない」）</summary>
    public ObservableCollection<PowerPlan> PowerPlanOptions { get; } = new() { new PowerPlan("", "電源プランを変えない", false) };

    private async Task LoadPowerPlansAsync()
    {
        var plans = await Task.Run(PowerPlans.List);
        foreach (var p in plans) PowerPlanOptions.Add(p);
    }

    /// <summary>変更通知を持たない設定（Quick / Web / Advisor など）を画面に反映する</summary>
    // ----- 見た目 -----

    private readonly ObservableCollection<LightingChoiceVm> _accentChoices = new();
    private readonly ObservableCollection<LightingChoiceVm> _backgroundChoices = new();
    private readonly ObservableCollection<LightingChoiceVm> _textColorChoices = new();
    private bool? _textSwatchesLight;
    private bool _appearanceReady;

    /// <summary>見た目の欄の選択肢を作る（テーマ・アクセント色 64 色・フォント）。フォントの一覧は 1 回だけ作る</summary>
    private void InitAppearance()
    {
        if (!_appearanceReady)
        {
            _appearanceReady = true;
            ThemeCombo.ItemsSource = new[]
            {
                new ChoiceOption(AppThemes.System, "Windows に合わせる"),
                new ChoiceOption(AppThemes.Dark, "ダーク"),
                new ChoiceOption(AppThemes.Light, "ライト"),
            };
            foreach (var hex in ThemeManager.AccentPalette)
            {
                var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
                brush.Freeze();
                _accentChoices.Add(new LightingChoiceVm { Kind = LightingChoiceKind.Accent, Id = hex, Label = hex, Tip = hex, Swatch = brush });
            }
            AccentSwatches.ItemsSource = _accentChoices;
            BackgroundSwatches.ItemsSource = _backgroundChoices;
            TextColorSwatches.ItemsSource = _textColorChoices;
            // 文字色の見本は、ダーク／ライトで色が変わるので、テーマを入れ替えたら作り直す
            ThemeManager.Changed += () => Dispatcher.BeginInvoke(RefreshAppearanceUi);
            var fonts = new List<ChoiceOption>
            {
                new("", "既定（英数字 Montserrat・日本語 ProFan Maru）"),
                new(ThemeManager.BundledMaruId, "ProFan Maru（同梱の丸ゴシック）"),
            };
            var lang = System.Windows.Markup.XmlLanguage.GetLanguage("ja-jp");
            foreach (var f in Fonts.SystemFontFamilies
                         .Select(f => (Id: f.Source, Label: f.FamilyNames.TryGetValue(lang, out var ja) ? ja : f.Source))
                         .Where(f => !f.Id.Contains('#'))
                         .DistinctBy(f => f.Id)
                         .OrderBy(f => f.Label, StringComparer.CurrentCulture))
                fonts.Add(new ChoiceOption(f.Id, f.Label == f.Id ? f.Id : $"{f.Label}（{f.Id}）"));
            FontCombo.ItemsSource = fonts;
        }
        RefreshAppearanceUi();
    }

    /// <summary>選ばれているアクセント色の印と、フォントの見本を、いまの設定に合わせる</summary>
    private void RefreshAppearanceUi()
    {
        var a = Config.Appearance;
        foreach (var c in _accentChoices) c.IsSelected = string.Equals(c.Id, a.Accent, StringComparison.OrdinalIgnoreCase);
        AccentDefaultButton.FontWeight = a.Accent == AppearanceConfig.DefaultAccent ? FontWeights.Bold : FontWeights.Normal;
        AccentSystemButton.FontWeight = a.Accent == AppearanceConfig.SystemAccent ? FontWeights.Bold : FontWeights.Normal;
        if (_backgroundChoices.Count == 0)
        {
            // 見本は、実際の背景色ではなく、色合いと濃さが分かるはっきりした色で塗る（ダーク・ライト共通）
            string[] rows = { "控えめ", "やや控えめ", "やや濃い", "濃い" };
            for (int i = 0; i < ThemeManager.BackgroundCount; i++)
            {
                var brush = new SolidColorBrush(ThemeManager.BackgroundDisplay(i));
                brush.Freeze();
                string name = i % 16 == 15 ? "灰色" : $"色相 {i % 16 * 24}°";
                _backgroundChoices.Add(new LightingChoiceVm
                {
                    Kind = LightingChoiceKind.Background, Id = AppearanceConfig.BackgroundPrefix + i, Label = name,
                    Tip = $"{name}・{rows[i / 16]}", Swatch = brush,
                });
            }
        }
        foreach (var c in _backgroundChoices) c.IsSelected = c.Id == a.Background;
        bool light = ThemeManager.Current.IsLight;
        if (_textSwatchesLight != light)
        {
            _textSwatchesLight = light;
            _textColorChoices.Clear();
            string[] rows = { "白に近い", "淡い", "やや濃い", "濃い" };
            if (light) rows = new[] { "黒に近い", "淡い", "やや濃い", "濃い" };
            for (int i = 0; i < ThemeManager.BackgroundCount; i++)
            {
                var brush = new SolidColorBrush(ThemeManager.TextSwatch(i, light));
                brush.Freeze();
                string name = i % 16 == 15 ? "灰色" : $"色相 {i % 16 * 24}°";
                _textColorChoices.Add(new LightingChoiceVm
                {
                    Kind = LightingChoiceKind.TextColor, Id = AppearanceConfig.TextColorPrefix + i, Label = name,
                    Tip = $"{name}・{rows[i / 16]}", Swatch = brush,
                });
            }
        }
        foreach (var c in _textColorChoices) c.IsSelected = c.Id == a.TextColor;
        TextColorDefaultButton.FontWeight = a.TextColor == AppearanceConfig.DefaultTextColor ? FontWeights.Bold : FontWeights.Normal;
        BackgroundDefaultButton.FontWeight = a.Background == AppearanceConfig.DefaultBackground ? FontWeights.Bold : FontWeights.Normal;
        FontPreview.FontFamily = ThemeManager.FontFor(a.FontFamily);
        // 元の値が一覧にない（ほかの PC で選んだフォントなど）ときは、選択欄が空にならないよう既定として表示する
        if (FontCombo.ItemsSource is IEnumerable<ChoiceOption> list && !list.Any(o => o.Id == a.FontFamily)) FontCombo.SelectedIndex = 0;
    }

    private void OnAccentDefaultClick(object sender, RoutedEventArgs e)
    {
        Config.Appearance.Accent = AppearanceConfig.DefaultAccent;
        RefreshAppearanceUi();
    }

    private void OnTextColorDefaultClick(object sender, RoutedEventArgs e)
    {
        Config.Appearance.TextColor = AppearanceConfig.DefaultTextColor;
        RefreshAppearanceUi();
    }

    private void OnBackgroundDefaultClick(object sender, RoutedEventArgs e)
    {
        Config.Appearance.Background = AppearanceConfig.DefaultBackground;
        RefreshAppearanceUi();
    }

    private void OnAccentSystemClick(object sender, RoutedEventArgs e)
    {
        Config.Appearance.Accent = AppearanceConfig.SystemAccent;
        RefreshAppearanceUi();
    }

    private void LoadPlainSettings()
    {
        InitAppearance();
        RestorePlanSwitch.IsChecked = Config.RestorePowerPlanOnExit;
        HotkeysSwitch.IsChecked = Config.Quick.HotkeysEnabled;
        QuietProfileCombo.ItemsSource = Config.Profiles;
        QuietProfileCombo.SelectedItem = Config.FindProfile(Config.Quick.QuietProfile);
        WebSwitch.IsChecked = Config.Web.Enabled;
        WebPortBox.Text = Config.Web.Port.ToString();
        LoadAdvisorSettings();
        UpdateWebInfo();
    }

    private void OnRestorePlanClick(object sender, RoutedEventArgs e)
    {
        Config.RestorePowerPlanOnExit = RestorePlanSwitch.IsChecked == true;
        ScheduleCommit();
    }

    private void OnAddScheduleClick(object sender, RoutedEventArgs e)
    {
        string quiet = Config.FindProfile(Config.Quick.QuietProfile)?.Name ?? Config.Profiles.First().Name;
        Config.ScheduleRules.Add(new ScheduleRule { Days = ScheduleRule.Everyday, Start = "23:00", End = "07:00", ProfileName = quiet });
        WatchConfig();
        ScheduleCommit();
    }

    private void OnRemoveScheduleClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: ScheduleRule r }) return;
        Config.ScheduleRules.Remove(r);
        WatchConfig();
        ScheduleCommit();
    }

    private void OnHotkeysClick(object sender, RoutedEventArgs e)
    {
        Config.Quick.HotkeysEnabled = HotkeysSwitch.IsChecked == true;
        ScheduleCommit();
        ((App)Application.Current).UpdateHotkeys();
    }

    private void OnQuietProfileChanged(object sender, SelectionChangedEventArgs e)
    {
        if (QuietProfileCombo.SelectedItem is not ProfileConfig p || p.Name == Config.Quick.QuietProfile) return;
        Config.Quick.QuietProfile = p.Name;
        ScheduleCommit();
    }

    // ----- スマホから監視 -----

    private void OnWebSwitchClick(object sender, RoutedEventArgs e)
    {
        Config.Web.Enabled = WebSwitch.IsChecked == true;
        FlushCommit();
        ((App)Application.Current).UpdateWebServer();
        UpdateWebInfo();
    }

    private void OnWebPortChanged(object sender, RoutedEventArgs e)
    {
        if (!int.TryParse(WebPortBox.Text, out int port) || port is < 1024 or > 65535)
        {
            WebPortBox.Text = Config.Web.Port.ToString();
            return;
        }
        if (port == Config.Web.Port) return;
        Config.Web.Port = port;
        FlushCommit();
        ((App)Application.Current).UpdateWebServer();
        UpdateWebInfo();
    }

    private void OnRegenerateTokenClick(object sender, RoutedEventArgs e)
    {
        Config.Web.Token = WebConfig.NewToken();
        FlushCommit();
        ((App)Application.Current).UpdateWebServer();
        UpdateWebInfo();
    }

    /// <summary>スマホで開く URL と QR コードを表示する</summary>
    public void UpdateWebInfo()
    {
        bool running = ((App)Application.Current).WebRunning;
        WebInfoPanel.Visibility = Config.Web.Enabled ? Visibility.Visible : Visibility.Collapsed;
        if (!Config.Web.Enabled) return;

        var urls = WebServer.LocalUrls(Config.Web.Port, Config.Web.Token);
        WebUrls.Text = running
            ? (urls.Count > 0 ? string.Join("\n", urls) : "この PC のネットワークアドレスが見つかりません")
            : "Web サーバーを開始できませんでした。ポート番号を変えて試してください（ログに詳細があります）";
        WebQr.Source = urls.Count > 0 && running ? CreateQr(urls[0]) : null;
    }

    private static System.Windows.Media.ImageSource? CreateQr(string text)
    {
        try
        {
            using var generator = new QRCoder.QRCodeGenerator();
            using var data = generator.CreateQrCode(text, QRCoder.QRCodeGenerator.ECCLevel.M);
            byte[] png = new QRCoder.PngByteQRCode(data).GetGraphic(8);
            var bmp = new System.Windows.Media.Imaging.BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
            bmp.StreamSource = new MemoryStream(png);
            bmp.EndInit();
            bmp.Freeze();
            return bmp;
        }
        catch (Exception ex)
        {
            Logger.Warn($"QR コードの作成に失敗: {ex.Message}");
            return null;
        }
    }
}
