// ==================================================
// MainWindow.Tools.cs - 診断・AI アドバイザー・冷却テストの画面
// Dependencies: WPF / .NET 8 (MIT License), Ollama (MIT License・別途インストール)
// See CREDITS.txt for full license details.
// ==================================================

using System.Collections.ObjectModel;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using ProFanController.UI;
using ProFanController.UI.Controls;

namespace ProFanController;

public partial class MainWindow
{
    private readonly ObservableCollection<FindingVm> _findings = new();
    private List<DiagnosticFinding> _lastFindings = new();
    private CancellationTokenSource? _advisorCts;
    private CoolingTestRunner? _test;
    private CoolingTestReport? _lastTestReport;

    // =====================================================================
    // 自動診断
    // =====================================================================

    private void OnRunDiagnosticsClick(object sender, RoutedEventArgs e) => RunDiagnostics();

    private void RunDiagnostics()
    {
        if (_catalog is null) return;
        FlushCommit();
        int minutes = int.TryParse(DiagWindowCombo.SelectedValue as string, out var m) ? m : 120;
        _lastFindings = Diagnostics.Run(_history, Config, _controller.EffectiveProfile, _catalog.Temperatures, TimeSpan.FromMinutes(minutes));
        _findings.Clear();
        foreach (var f in _lastFindings) _findings.Add(new FindingVm { Finding = f });
    }

    private void OnApplyFindingClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: FindingVm vm } || vm.Finding.Apply is null) return;
        string result = vm.Finding.Apply(Config);
        vm.MarkApplied(result);
        WatchConfig();
        ScheduleCommit();
        // 開いているカーブ画面にも反映する
        if (SelectedCurve is { } c) ShowCurve(c);
        UpdateCalibrationPanel();
    }

    // =====================================================================
    // AI 冷却アドバイザー（Ollama）
    // =====================================================================

    private void LoadAdvisorSettings()
    {
        OllamaUrlBox.Text = Config.Advisor.OllamaUrl;
        if (!string.IsNullOrEmpty(Config.Advisor.Model))
        {
            ModelCombo.ItemsSource = new[] { Config.Advisor.Model };
            ModelCombo.SelectedIndex = 0;
        }
        _ = RefreshModelsAsync(quiet: true);
    }

    /// <summary>Ollama に入っているモデル一覧を取り直す。保存してあるモデルがなければ、使えるモデルに切り替える</summary>
    private async Task RefreshModelsAsync(bool quiet)
    {
        if (!quiet) AdvisorStatus.Text = "Ollama に接続しています…";
        try
        {
            var models = await OllamaClient.ListModelsAsync(Config.Advisor.OllamaUrl, CancellationToken.None);
            ModelCombo.ItemsSource = models;
            string? saved = Config.Advisor.Model;
            ModelCombo.SelectedItem = models.Contains(saved ?? "") ? saved : models.FirstOrDefault();
            if (models.Count == 0)
                AdvisorStatus.Text = "モデルがありません。PowerShell で「ollama pull qwen2.5:7b」などを実行して取得してください";
            else if (!string.IsNullOrEmpty(saved) && !models.Contains(saved))
                AdvisorStatus.Text = $"保存していたモデル「{saved}」が見つからないため、「{models[0]}」に切り替えました";
            else if (!quiet)
                AdvisorStatus.Text = $"{models.Count} 個のモデルが見つかりました";
        }
        catch (Exception ex)
        {
            if (!quiet) AdvisorStatus.Text = $"接続できません（Ollama は起動していますか？）: {ex.Message}";
        }
    }

    private void OnAdvisorSettingChanged(object sender, RoutedEventArgs e)
    {
        string url = OllamaUrlBox.Text.Trim();
        if (url.Length == 0 || url == Config.Advisor.OllamaUrl) return;
        Config.Advisor.OllamaUrl = url;
        ScheduleCommit();
    }

    private void OnModelChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ModelCombo.SelectedItem is not string model || model == Config.Advisor.Model) return;
        Config.Advisor.Model = model;
        ScheduleCommit();
    }

    private async void OnRefreshModelsClick(object sender, RoutedEventArgs e)
    {
        OnAdvisorSettingChanged(sender, e);
        await RefreshModelsAsync(quiet: false);
    }

    private string BuildAdvisorPrompt(bool includeTest)
    {
        var temps = _tempCards.Select(c => (c.SensorId, c.Title))
            .Concat(_catalog?.Temperatures.Where(t => _tempCards.All(c => c.SensorId != t.Id) && t.HardwareType is "SuperIO" or "Storage")
                        .Select(t => (t.Id, $"{t.Name} ({t.HardwareName})")) ?? Enumerable.Empty<(string, string)>())
            .ToList();
        if (_lastFindings.Count == 0 && _history.Count >= 120)
        {
            _lastFindings = Diagnostics.Run(_history, Config, _controller.EffectiveProfile, _catalog!.Temperatures, TimeSpan.FromHours(24));
        }
        var trend = _healthItems.Where(h => h.Item.Level != HealthLevel.Collecting)
            .Select(h => $"{h.Title}: {h.Item.Level}（{h.Metric}）");
        return AdvisorPrompt.Build(_history, Config, _controller.EffectiveProfile, temps, MetricHistoryKeys.ToList(),
            _lastFindings, includeTest ? _lastTestReport : null, QuestionBox.Text, trend);
    }

    private void OnShowPromptClick(object sender, RoutedEventArgs e)
    {
        if (_catalog is null) return;
        AdvisorOutput.Text = "【AI に渡す内容】\n\n" + AdvisorPrompt.System + "\n\n---\n\n" + BuildAdvisorPrompt(includeTest: _lastTestReport is not null);
    }

    private void OnAnalyzeClick(object sender, RoutedEventArgs e) => _ = AnalyzeAsync(includeTest: _lastTestReport is not null);

    private void OnStopAnalyzeClick(object sender, RoutedEventArgs e) => _advisorCts?.Cancel();

    private async Task AnalyzeAsync(bool includeTest)
    {
        if (_catalog is null || _advisorCts is not null) return;
        if (ModelCombo.SelectedItem is not string model)
        {
            AdvisorStatus.Text = "先に「一覧を更新」でモデルを選んでください";
            return;
        }
        if (_history.Count < 60)
        {
            AdvisorStatus.Text = "記録が少なすぎます。数分使ってから実行してください";
            return;
        }

        string prompt = BuildAdvisorPrompt(includeTest);
        _advisorCts = new CancellationTokenSource();
        AnalyzeButton.IsEnabled = false;
        StopAnalyzeButton.Visibility = Visibility.Visible;
        AdvisorOutput.Text = "";
        AdvisorStatus.Text = $"{model} が分析しています…（初回はモデルの読み込みに時間がかかります）";
        var sb = new StringBuilder();
        try
        {
            await foreach (var piece in OllamaClient.ChatStreamAsync(Config.Advisor.OllamaUrl, model, AdvisorPrompt.System, prompt, _advisorCts.Token))
            {
                sb.Append(piece);
                AdvisorOutput.Text = sb.ToString().TrimStart();
                AdvisorOutput.ScrollToEnd();
            }
            AdvisorStatus.Text = $"完了（{DateTime.Now:HH:mm}）";
            SaveAdvisorReport(sb.ToString());
        }
        catch (OperationCanceledException)
        {
            AdvisorStatus.Text = "停止しました";
        }
        catch (Exception ex) when (ex.Message.Contains("not found"))
        {
            AdvisorStatus.Text = $"モデル「{model}」が Ollama にありません。「一覧を更新」で選び直すか、PowerShell で「ollama pull {model}」を実行してください";
        }
        catch (Exception ex)
        {
            AdvisorStatus.Text = $"エラー: {ex.Message}";
        }
        finally
        {
            _advisorCts.Dispose();
            _advisorCts = null;
            AnalyzeButton.IsEnabled = true;
            StopAnalyzeButton.Visibility = Visibility.Collapsed;
        }
    }

    /// <summary>あとで見返せるよう、AI の回答を reports フォルダに残す</summary>
    private static void SaveAdvisorReport(string text)
    {
        try
        {
            string dir = Path.Combine(AppContext.BaseDirectory, "reports");
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, $"AI_{DateTime.Now:yyyyMMdd_HHmm}.txt"), text, new UTF8Encoding(true));
        }
        catch (Exception ex)
        {
            Logger.Warn($"AI レポートの保存に失敗: {ex.Message}");
        }
    }

    // =====================================================================
    // 冷却テスト
    // =====================================================================

    private void OnStartTestClick(object sender, RoutedEventArgs e)
    {
        if (_catalog is null || _test is not null) return;
        if (_tuner is not null)
        {
            MessageBox.Show(this, "おまかせ調整が実行中です。終わってから始めてください。", "ProFanController", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        var cpuTemp = _catalog.Temperatures.FirstOrDefault(t => t.HardwareType.StartsWith("Cpu") && t.Name is "CPU Package" or "Core (Tctl/Tdie)" or "Core (Tctl)")
                      ?? _catalog.Temperatures.FirstOrDefault(t => t.HardwareType.StartsWith("Cpu"));
        if (cpuTemp is null)
        {
            MessageBox.Show(this, "CPU の温度センサーが見つかりません。", "ProFanController", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        int minutes = Test5.IsChecked == true ? 5 : Test15.IsChecked == true ? 15 : 10;
        var answer = MessageBox.Show(this,
            $"約 {minutes + 2} 分かけて冷却テストをします。\n\n" +
            "・テスト中は CPU が全力で動くため、ファンの音が大きくなります。\n" +
            $"・CPU 温度が {Config.EmergencyTemperature - 3:F0}°C に達したら自動で負荷を止めます。\n" +
            "・ほかの重いアプリは閉じておいてください。",
            "冷却テスト", MessageBoxButton.OKCancel, MessageBoxImage.Information);
        if (answer != MessageBoxResult.OK) return;

        var all = _catalog.AllSensors.ToList();
        var power = all.FirstOrDefault(s => s.HardwareType.StartsWith("Cpu") && s.SensorType == "Power" && s.Name == "CPU Package");
        var clocks = all.Where(s => s.HardwareType.StartsWith("Cpu") && s.SensorType == "Clock" && s.Name.Contains("Core") && !s.Name.Contains("Bus")).Select(s => s.Id).ToList();
        var fans = (Config.FindProfile(_controller.EffectiveProfile)?.Fans ?? new()).Select(f => (f.ControlId, f.Name)).ToList();

        _test = new CoolingTestRunner(cpuTemp.Id, power?.Id, clocks, fans, TimeSpan.FromMinutes(minutes), Config.EmergencyTemperature - 3);
        TestChart.Store = _history;
        TestChart.Series = new List<ChartSeries> { new() { Key = cpuTemp.Id, Name = "CPU", Brush = Palette.Series[0] } }
            .Concat(fans.Select((f, i) => new ChartSeries { Key = HistoryStore.RpmKey(f.ControlId), Name = f.Name, IsRpm = true, Brush = Palette.Series[(i + 2) % Palette.Series.Length] }))
            .ToList();
        StartTestButton.Visibility = Visibility.Collapsed;
        CancelTestButton.Visibility = Visibility.Visible;
        StartTuneButton.IsEnabled = false;
        TestResultCard.Visibility = Visibility.Collapsed;
        Logger.Info($"冷却テスト開始（{minutes} 分）");
    }

    private void OnCancelTestClick(object sender, RoutedEventArgs e)
    {
        _test?.Cancel();
        if (_test is not null) FinishTest();
    }

    /// <summary>毎周期: テストを進め、表示を更新する</summary>
    private void UpdateTest(EngineSnapshot s)
    {
        if (_test is null) return;
        _test.OnSnapshot(s);
        TestProgress.Value = Math.Clamp(_test.Progress * 100, 0, 100);
        TestStatus.Text = _test.StatusText;
        TestLive.Text = $"現在 {_test.LastTemp:F0}°C ／ 最高 {_test.MaxTemp:F0}°C";
        TestChart.Range = (s.Time - _test.StartedAt) + TimeSpan.FromSeconds(10);
        if (TestPage.Visibility == Visibility.Visible) TestChart.Refresh();
        if (_test.Current == CoolingTestRunner.Phase.Done) FinishTest();
    }

    private void FinishTest()
    {
        if (_test is null) return;
        _lastTestReport = _test.Report;
        _test.Dispose();
        _test = null;
        StartTestButton.Visibility = Visibility.Visible;
        CancelTestButton.Visibility = Visibility.Collapsed;
        StartTuneButton.IsEnabled = true;
        TestProgress.Value = 100;
        if (_lastTestReport is null) return;

        TestStatus.Text = _lastTestReport.AbortReason ?? "完了しました";
        TestVerdict.Text = $"{_lastTestReport.Verdict}（安定時 {_lastTestReport.SteadyTemp:F0}°C）";
        TestVerdict.Foreground = _lastTestReport.Verdict switch
        {
            "とても良好" or "良好" => Palette.Ok,
            "やや高め" or "中断" => Palette.Warn,
            _ => Palette.Danger,
        };
        TestReportText.Text = _lastTestReport.ToText();
        TestResultCard.Visibility = Visibility.Visible;
        ShowAlert($"冷却テストが終わりました: {_lastTestReport.Verdict}", AlertLevel.Info);
        Logger.Info("冷却テスト終了\n" + _lastTestReport.ToText());
    }

    private void OnSaveReportClick(object sender, RoutedEventArgs e)
    {
        if (_lastTestReport is null) return;
        var dialog = new SaveFileDialog
        {
            Title = "冷却テストのレポートを保存",
            Filter = "テキスト (*.txt)|*.txt",
            FileName = $"CoolingTest_{_lastTestReport.StartedAt:yyyyMMdd_HHmm}.txt",
        };
        if (dialog.ShowDialog(this) == true) File.WriteAllText(dialog.FileName, _lastTestReport.ToText(), new UTF8Encoding(true));
    }

    private void OnAnalyzeTestClick(object sender, RoutedEventArgs e)
    {
        NavTools.IsChecked = true;
        if (string.IsNullOrWhiteSpace(QuestionBox.Text))
            QuestionBox.Text = "直近の冷却テストの結果を評価して、ファン設定の改善案を教えてください。";
        _ = AnalyzeAsync(includeTest: true);
    }

    /// <summary>テスト・調整中にアプリを終了するときは、負荷を必ず止める</summary>
    public void StopTestIfRunning()
    {
        _test?.Dispose();
        _tuner?.Dispose();
    }
}
