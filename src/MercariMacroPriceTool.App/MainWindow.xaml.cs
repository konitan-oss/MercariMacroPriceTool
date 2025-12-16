using MercariMacroPriceTool.Automation;
using MercariMacroPriceTool.Domain;
using MercariMacroPriceTool.Storage;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Data;

namespace MercariMacroPriceTool.App;

public partial class MainWindow : Window, INotifyPropertyChanged
{
    private readonly MercariAutomationService _automationService = new();
    private readonly ItemStateRepository _itemRepository = new();
    private readonly CancellationTokenSource _appCts = new();
    private CancellationTokenSource? _operationCts;
    private readonly string _runStatePath;
    private readonly string _settingsPath;
    private ICollectionView? _listingsView;
    private AppSettings _settings = AppSettings.CreateDefault();
    private int _totalCount;
    private int _excludedCount;
    private int _displayedCount;
    private bool _isRunning;
    private bool _isLogVisible;

    private string _statusMessage = "Idle";
    private string _logText = string.Empty;
    private string _countSummary = "表示 0 / 取得 0（除外 0）";
    private string _selectionSummary = "選択中: 0件";

    public ObservableCollection<ListingRow> Listings { get; } = new();

    public string StatusMessage
    {
        get => _statusMessage;
        set { if (_statusMessage != value) { _statusMessage = value; OnPropertyChanged(); } }
    }

    public string LogText
    {
        get => _logText;
        set { if (_logText != value) { _logText = value; OnPropertyChanged(); } }
    }

    public string CountSummary
    {
        get => _countSummary;
        set { if (_countSummary != value) { _countSummary = value; OnPropertyChanged(); } }
    }

    public string SelectionSummary
    {
        get => _selectionSummary;
        set { if (_selectionSummary != value) { _selectionSummary = value; OnPropertyChanged(); } }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public MainWindow()
    {
        InitializeComponent();
        DataContext = this;
        _runStatePath = Path.Combine(StoragePathProvider.EnsureLocalDirectory(), "runstate.json");
        _settingsPath = Path.Combine(StoragePathProvider.EnsureLocalDirectory(), "settings.json");

        _listingsView = CollectionViewSource.GetDefaultView(Listings);
        _listingsView.Filter = FilterListing;

        LoadSettings();
        UpdateCountSummary(0, 0, 0);
        UpdateSelectionSummary();

        Listings.CollectionChanged += Listings_CollectionChanged;
        ApplyLogVisibility();
    }
    private async void FetchItemsButton_Click(object sender, RoutedEventArgs e)
    {
        if (_operationCts != null)
        {
            MessageBox.Show("Another operation is running. Stop it first.", "Info", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        if (!int.TryParse(StartRowTextBox.Text, out var startRow) || startRow < 1) startRow = 1;
        if (!int.TryParse(EndRowTextBox.Text, out var endRow) || endRow < startRow) endRow = Math.Max(startRow, 1);

        FetchItemsButton.IsEnabled = false;
        RunPriceButton.IsEnabled = false;
        StopButton.IsEnabled = true;
        StatusMessage = "Fetching items...";
        AppendLog(StatusMessage);

        _operationCts = CancellationTokenSource.CreateLinkedTokenSource(_appCts.Token);
        var progress = new Progress<string>(msg => { StatusMessage = msg; AppendLog(msg); });

        try
        {
            AppendLog("S1: FetchListings start");
            var listingsRaw = await _automationService.FetchListingsAsync(startRow, endRow, progress, _operationCts.Token);
            AppendLog($"S1: FetchListings completed rawCount={(listingsRaw?.Count() ?? 0)}");

            var listings = (listingsRaw ?? Array.Empty<ListingItem>()).ToList();
            var nullItems = listings.Count(x => x is null);
            var safeListings = listings.Where(x => x != null).Select(x => x!).ToList();

            var invalidItems = safeListings.Where(x =>
                string.IsNullOrWhiteSpace(x.ItemUrl) ||
                string.IsNullOrWhiteSpace(x.Title) ||
                string.IsNullOrWhiteSpace(x.ItemId)).ToList();
            safeListings = safeListings.Except(invalidItems).ToList();

            var pausedCount = safeListings.Count(x => x.IsPaused);
            var displayed = safeListings.Where(x => !x.IsPaused).ToList();
            _totalCount = listings.Count;
            _excludedCount = pausedCount;

            AppendLog($"Data check: total={listings.Count}, nullItems={nullItems}, invalidItems={invalidItems.Count}, paused={pausedCount}, displayed={displayed.Count}");

            Listings.Clear();
            AppendLog("S3: DataGrid clear complete");
            var added = 0;
            foreach (var item in displayed)
            {
                try
                {
                    var runCount = 0;
                    var lastDownAtText = string.Empty;
                    var lastDownAmount = 0;
                    var lastDownLabel = string.Empty;
                    var displayPrice = item.Price;

                    if (!string.IsNullOrWhiteSpace(item.ItemId))
                    {
                        try
                        {
                            var existing = await _itemRepository.GetByItemIdAsync(item.ItemId);
                            runCount = existing?.RunCount ?? 0;

                            if (displayPrice <= 0 && (existing?.BasePrice ?? 0) > 0)
                            {
                                displayPrice = existing!.BasePrice;
                            }

                            if (displayPrice > 0 && existing != null && existing.BasePrice == 0)
                            {
                                existing.BasePrice = displayPrice;
                                await _itemRepository.UpsertAsync(existing);
                            }

                            lastDownAtText = FormatTimestamp(existing?.LastDownAt);
                            lastDownAmount = existing?.LastDownAmount ?? 0;
                            lastDownLabel = BuildLastDownLabel(existing?.LastDownRatePercent, existing?.LastDownDailyDownYen, existing?.LastDownRunIndex);
                        }
                        catch (Exception repoEx)
                        {
                            AppendLog($"DB lookup failed (continuing) ItemId={item.ItemId}: {repoEx.Message}");
                            runCount = 0;
                        }
                    }

                    Listings.Add(new ListingRow
                    {
                        ItemId = item.ItemId,
                        Title = item.Title,
                        Price = displayPrice,
                        FormattedPrice = displayPrice.ToString("N0"),
                        StatusText = item.StatusText,
                        ItemUrl = item.ItemUrl,
                        LastDownAtText = lastDownAtText,
                        LastDownLabelText = lastDownLabel,
                        LastDownAmount = lastDownAmount,
                        LikeCount = 0,
                        RunCount = runCount,
                        ResultStatus = "未実行",
                        LastMessage = string.Empty,
                        ExecutedAt = string.Empty,
                        IsChecked = false
                    });
                    added++;
                }
                catch (Exception addEx)
                {
                    AppendLog($"Row add exception (skip) ItemId={item.ItemId}: {addEx}");
                }
            }
            AppendLog($"S4: DataGrid add complete count={added}");

            try
            {
                ApplyFilter();
                UpdateCountSummary(_totalCount, _excludedCount, _displayedCount);
            }
            catch (Exception ex)
            {
                AppendLog("ApplyFilter threw (continuing)");
                AppendLog(ex.ToString());
            }

            var zeroPriceCount = Listings.Count(x => x.Price <= 0);
            if (zeroPriceCount > 0)
            {
                AppendLog($"Notice: price=0 rows {zeroPriceCount}.");
            }
            StatusMessage = $"Fetched: {listings.Count}, Excluded: {pausedCount}, Displayed: {displayed.Count}";
            AppendLog(StatusMessage);
            AppendLog("S6: FetchItems completed");
        }
        catch (OperationCanceledException)
        {
            StatusMessage = "Fetch canceled.";
            AppendLog(StatusMessage);
        }
        catch (Exception ex)
        {
            AppendLog("Fetch exception:");
            AppendLog(ex.ToString());
            ShowLogArea();

            try
            {
                var evidenceBase = $"FetchItems_Failed_{DateTime.Now:yyyyMMdd-HHmmss}";
                var evidencePath = await _automationService.SaveEvidenceAsync(evidenceBase, CancellationToken.None);
                AppendLog($"FetchItems evidence saved: {evidencePath}");
            }
            catch (Exception evEx)
            {
                AppendLog($"FetchItems evidence save failed: {evEx}");
            }

            MessageBox.Show($"商品取得に失敗しました: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            StatusMessage = "商品取得に失敗しました。";
            AppendLog(StatusMessage);
        }
        finally
        {
            _operationCts?.Dispose();
            _operationCts = null;
            FetchItemsButton.IsEnabled = true;
            RunPriceButton.IsEnabled = true;
            ResetItemButton.IsEnabled = true;
            ClearSkipButton.IsEnabled = true;
            StopButton.IsEnabled = false;
            ApplyFilterButton.IsEnabled = true;
            ListingsGrid.IsEnabled = true;
            EnableInputs();
        }
    }
    private async void RunPriceButton_Click(object sender, RoutedEventArgs e)
    {
        if (_operationCts != null)
        {
            MessageBox.Show("Another operation is running. Stop it first.", "Info", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var selected = Listings.Where(x => x.IsChecked).ToList();
        if (selected.Count == 0)
        {
            AppendLog("No checked items. Nothing to do.");
            StatusMessage = "対象なし";
            return;
        }

        var settingsSnapshot = CaptureSettingsFromUi();
        var confirm = MessageBox.Show(
            $"対象件数: {selected.Count}\n" +
            $"値下げ率: {settingsSnapshot.RatePercent}%\n" +
            $"毎日値下げ: {settingsSnapshot.DailyDownYen}円\n" +
            $"待機①: {settingsSnapshot.WaitAfterPauseSec}秒\n" +
            $"待機②: {settingsSnapshot.WaitAfterResumeSec}秒\n" +
            $"商品間待機: {settingsSnapshot.ItemGapSec}秒\n\n実行しますか？",
            "確認",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);
        if (confirm != MessageBoxResult.Yes)
        {
            AppendLog("Run canceled by user at confirmation.");
            return;
        }

        SaveSettings(); // 自動保存
        AppendLog($"Settings fixed (Rate={settingsSnapshot.RatePercent}, Daily={settingsSnapshot.DailyDownYen}, Retry={settingsSnapshot.RetryCount}/{settingsSnapshot.RetryWaitSec}s)");
        AppendLog($"Run confirm: targets={selected.Count}, waits=({settingsSnapshot.WaitAfterPauseSec}/{settingsSnapshot.WaitAfterResumeSec}/{settingsSnapshot.ItemGapSec})");

        int ratePercent = settingsSnapshot.RatePercent;
        int dailyDownYen = settingsSnapshot.DailyDownYen;
        int waitAfterPause = settingsSnapshot.WaitAfterPauseSec;
        int waitAfterResume = settingsSnapshot.WaitAfterResumeSec;
        int waitBetweenItems = settingsSnapshot.ItemGapSec;
        int retryCount = settingsSnapshot.RetryCount;
        int retryWait = settingsSnapshot.RetryWaitSec;
        var today = DateTime.UtcNow.ToString("yyyy-MM-dd");

        LockUiForRun();
        _operationCts = CancellationTokenSource.CreateLinkedTokenSource(_appCts.Token);
        var token = _operationCts.Token;

        var logDir = Path.Combine(StoragePathProvider.EnsureLocalDirectory(), "logs");
        Directory.CreateDirectory(logDir);
        var logPath = Path.Combine(logDir, $"{DateTime.Now:yyyyMMdd-HHmm}_price-run.csv");

        var runState = LoadRunState();
        if (runState == null || runState.IsCompleted)
        {
            runState = RunState.CreateNew(selected);
        }
        else
        {
            AppendLog("Found previous runstate. Resume remaining.");
            foreach (var row in selected)
            {
                var stateItem = runState.Items.FirstOrDefault(i => i.ItemId == row.ItemId);
                if (stateItem != null)
                {
                    row.ResultStatus = stateItem.Status;
                    row.LastMessage = stateItem.Message ?? string.Empty;
                    row.ExecutedAt = stateItem.ExecutedAt ?? string.Empty;
                }
            }
        }
        SaveRunState(runState);

        int success = 0, fail = 0, skip = 0, cancel = 0;
        AppendLog($"Run price for {selected.Count} items. Log: {logPath}");
        StatusMessage = "価格改定を開始します…";

        await using var writer = new StreamWriter(logPath, append: false, Encoding.UTF8);
        await writer.WriteLineAsync("ItemId,Title,ItemUrl,BasePrice,NewPrice,Result,Message,ExecutedAt,Step,RetryUsed,EvidencePath");

        try
        {
            for (int idx = 0; idx < selected.Count; idx++)
            {
                var row = selected[idx];
                var stateItem = runState.Items.FirstOrDefault(i => i.ItemId == row.ItemId);
                if (stateItem == null)
                {
                    stateItem = new RunItemState { ItemId = row.ItemId, Title = row.Title, Status = "未実行" };
                    runState.Items.Add(stateItem);
                }

                runState.CurrentIndex = idx;
                SaveRunState(runState);

                if (token.IsCancellationRequested)
                {
                    cancel++;
                    UpdateRowResult(row, "キャンセル", "停止要求により中断", string.Empty);
                    await WriteLogLineAsync(writer, row, row.Price, row.Price, "キャンセル", "停止要求により中断", DateTime.Now, "Canceled", 0, string.Empty);
                    stateItem.Status = "キャンセル";
                    stateItem.Message = "停止要求により中断";
                    SaveRunState(runState);
                    break;
                }

                if (!string.Equals(stateItem.Status, "未実行", StringComparison.Ordinal))
                {
                    skip++;
                    AppendLog($"[{row.ItemId}] already {stateItem.Status}, skip.");
                    continue;
                }

                var execTime = DateTime.Now;
                string resultStatusForWait = "失敗";
                try
                {
                    var result = await ProcessPriceUpdateAsync(row, ratePercent, dailyDownYen, waitAfterPause, waitAfterResume, retryCount, retryWait, today, token);
                    UpdateRowResult(row, result.Status, result.Message, execTime.ToString("yyyy-MM-dd HH:mm:ss"));
                    resultStatusForWait = result.Status;

                    if (result.Status == "成功") success++;
                    else if (result.Status == "スキップ") skip++;
                    else fail++;

                    await WriteLogLineAsync(writer, row, result.BasePrice, result.NewPrice, result.Status, result.Message, execTime, result.Step, result.RetryUsed, result.EvidencePath);

                    stateItem.Status = result.Status;
                    stateItem.Message = result.Message;
                    stateItem.ExecutedAt = execTime.ToString("yyyy-MM-dd HH:mm:ss");
                    SaveRunState(runState);
                }
                catch (OperationCanceledException)
                {
                    cancel++;
                    UpdateRowResult(row, "キャンセル", "停止要求により中断", execTime.ToString("yyyy-MM-dd HH:mm:ss"));
                    await WriteLogLineAsync(writer, row, row.Price, row.Price, "キャンセル", "停止要求により中断", execTime, "Canceled", 0, string.Empty);
                    stateItem.Status = "キャンセル";
                    stateItem.Message = "停止要求により中断";
                    stateItem.ExecutedAt = execTime.ToString("yyyy-MM-dd HH:mm:ss");
                    SaveRunState(runState);
                    break;
                }

                var isLast = idx == selected.Count - 1;
                if (!isLast && resultStatusForWait == "成功" && waitBetweenItems > 0)
                {
                    StatusMessage = $"[WaitBetweenItems] {waitBetweenItems}s (next item exists)";
                    AppendLog(StatusMessage);
                    for (var i = 0; i < waitBetweenItems; i++)
                    {
                        token.ThrowIfCancellationRequested();
                        await Task.Delay(1000, token);
                    }
                }
                else if (isLast || resultStatusForWait != "成功")
                {
                    var finalWait = 10;
                    StatusMessage = $"[WaitAfterLastItem] {finalWait}s (no next item)";
                    AppendLog(StatusMessage);
                    for (var i = 0; i < finalWait; i++)
                    {
                        token.ThrowIfCancellationRequested();
                        await Task.Delay(1000, token);
                    }
                }
            }

            var total = selected.Count;
            var summary = $"Total:{total} / Success:{success} / Fail:{fail} / Skip:{skip} / Cancel:{cancel}";
            StatusMessage = $"価格改定完了 {summary}";
            AppendLog(StatusMessage);

            if (!token.IsCancellationRequested)
            {
                runState.IsCompleted = true;
                SaveRunState(runState);
            }
        }
        finally
        {
            _operationCts?.Dispose();
            _operationCts = null;
            UnlockUiAfterRun();
        }
    }
    private async Task<(string Status, string Message, int BasePrice, int NewPrice, string Step, int RetryUsed, string EvidencePath)> ProcessPriceUpdateAsync(
        ListingRow row,
        int ratePercent,
        int dailyDownYen,
        int waitAfterPause,
        int waitAfterResume,
        int retryCount,
        int retryWait,
        string today,
        CancellationToken token)
    {
        if (string.IsNullOrWhiteSpace(row.ItemId))
        {
            return ("スキップ", "ItemId missing", row.Price, row.Price, "Validate", 0, string.Empty);
        }

        var state = await _itemRepository.GetByItemIdAsync(row.ItemId);
        if (state == null)
        {
            state = new ItemState
            {
                ItemId = row.ItemId,
                ItemUrl = row.ItemUrl,
                Title = row.Title,
                BasePrice = row.Price,
                RunCount = 1,
                LastRunDate = null,
                LastDownAmount = 0,
                LastDownAt = null,
                LastDownRatePercent = null,
                LastDownDailyDownYen = null,
                LastDownRunIndex = null
            };
            await _itemRepository.UpsertAsync(state);
            AppendLog($"First registration: {row.Title} BasePrice={state.BasePrice}, RunCount={state.RunCount}");
        }

        if (string.Equals(state.LastRunDate, today, StringComparison.Ordinal))
        {
            return ("スキップ", "Already done today", state.BasePrice, state.BasePrice, "AlreadyDone", 0, string.Empty);
        }

        var rateDown = (int)Math.Floor(state.BasePrice * (ratePercent / 100.0));
        var dailyDown = dailyDownYen * state.RunCount;
        var newPrice = Math.Max(1, state.BasePrice - (rateDown + dailyDown));
        var lastDownAmount = Math.Max(0, state.BasePrice - newPrice);
        var downAtRaw = DateTime.Now;
        var downAt = FormatTimestamp(downAtRaw.ToString("yyyy-MM-dd HH:mm:ss"));
        var runIndex = state.RunCount;
        var downLabel = BuildLastDownLabel(ratePercent, dailyDownYen, runIndex);

        AppendLog($"[{row.ItemId}] start update NewPrice={newPrice}, BasePrice={state.BasePrice}, RunCount={state.RunCount}");

        var options = new PriceUpdateOptions
        {
            WaitAfterPauseSeconds = waitAfterPause,
            WaitAfterResumeSeconds = waitAfterResume
        };

        var progress = new Progress<string>(msg => { StatusMessage = msg; AppendLog(msg); });

        try
        {
            var updateResult = await _automationService.RunPriceUpdateCycleAsync(
                row.ItemUrl,
                newPrice,
                state.BasePrice,
                options,
                retryCount,
                retryWait,
                progress,
                token);

            var updated = new ItemState
            {
                ItemId = state.ItemId,
                ItemUrl = row.ItemUrl,
                Title = row.Title,
                BasePrice = state.BasePrice,
                RunCount = state.RunCount + 1,
                LastRunDate = today,
                LastDownAmount = lastDownAmount,
                LastDownAt = downAt,
                LastDownRatePercent = ratePercent,
                LastDownDailyDownYen = dailyDownYen,
                LastDownRunIndex = runIndex
            };
            await _itemRepository.UpsertAsync(updated);
            row.RunCount = updated.RunCount;
            row.LastDownAmount = lastDownAmount;
            row.LastDownAtText = downAt;
            row.LastDownLabelText = downLabel;
            AppendLog($"LastDown saved: ItemId={row.ItemId}, Label={downLabel}, Amount={lastDownAmount}, At={downAt}");

            return ("成功", "完走しました", state.BasePrice, newPrice, updateResult.LastStep, updateResult.RetryUsed, string.Empty);
        }
        catch (StepFailedException ex)
        {
            var evidenceBase = $"{DateTime.Now:yyyyMMdd-HHmm}_{row.ItemId}_{ex.StepName}";
            var evidencePath = await _automationService.SaveEvidenceAsync(evidenceBase, token);
            var message = $"{ex.StepName} failed: {ex.InnerException?.Message}";
            AppendLog($"[{row.ItemId}] failed {message} evidence: {evidencePath}");
            ShowLogArea();
            return ("失敗", message, state.BasePrice, newPrice, ex.StepName, ex.RetryUsed, evidencePath);
        }
    }

    private void StopButton_Click(object sender, RoutedEventArgs e)
    {
        _operationCts?.Cancel();
        AppendLog("Stop requested.");
    }

    private void OpenUrlButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement fe) return;
        var url = fe.Tag as string;
        if (string.IsNullOrWhiteSpace(url))
        {
            AppendLog("OpenUrl skipped: empty url.");
            return;
        }

        try
        {
            var psi = new ProcessStartInfo(url) { UseShellExecute = true };
            Process.Start(psi);
            AppendLog($"OpenUrl: {url}");
        }
        catch (Exception ex)
        {
            AppendLog($"OpenUrl failed: {ex.Message}");
            ShowLogArea();
        }
    }

    private void CopyUrlButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement fe) return;
        var url = fe.Tag as string;
        if (string.IsNullOrWhiteSpace(url))
        {
            AppendLog("CopyUrl skipped: empty url.");
            return;
        }

        try
        {
            Clipboard.SetText(url);
            AppendLog($"CopyUrl: {url}");
        }
        catch (Exception ex)
        {
            AppendLog($"CopyUrl failed: {ex.Message}");
            ShowLogArea();
        }
    }

    private void ApplyFilterButton_Click(object sender, RoutedEventArgs e)
    {
        ApplyFilter();
    }

    private void ApplyFilter()
    {
        var before = Listings.Count;

        try
        {
            _listingsView = CollectionViewSource.GetDefaultView(Listings);
            _listingsView.Filter = FilterListing;
            _listingsView.Refresh();

            var after = _listingsView?.Cast<object>().Count() ?? 0;
            _displayedCount = after;
            UpdateCountSummary(_totalCount, _excludedCount, _displayedCount);
            AppendLog($"Filter applied: {before} -> {after}");
        }
        catch (Exception ex)
        {
            AppendLog("Filter apply failed. Disable filter and continue:");
            AppendLog(ex.ToString());

            try
            {
                _listingsView = CollectionViewSource.GetDefaultView(Listings);
                _listingsView.Filter = null;
                _listingsView.Refresh();
                _displayedCount = Listings.Count;
                UpdateCountSummary(_totalCount, _excludedCount, _displayedCount);
            }
            catch (Exception ex2)
            {
                AppendLog("Filter clear also failed (continuing):");
                AppendLog(ex2.ToString());
            }

            AppendLog("Filter disabled; continuing with list display.");
        }
    }

    private bool FilterListing(object obj)
    {
        if (obj is not ListingRow row) return false;

        var search = SearchTextBox?.Text ?? string.Empty;
        var keywords = search.Split(new[] { ' ', '　' }, StringSplitOptions.RemoveEmptyEntries);
        if (keywords.Length == 0) return true;

        var title = row.Title ?? string.Empty;
        return keywords.All(k => title.IndexOf(k, StringComparison.OrdinalIgnoreCase) >= 0);
    }

    private void ClearSkipButton_Click(object sender, RoutedEventArgs e)
    {
        _ = ClearSkipButton_ClickAsync();
    }

    private async Task ClearSkipButton_ClickAsync()
    {
        if (_operationCts != null)
        {
            AppendLog("Cannot clear skip while running. Stop first.");
            MessageBox.Show("実行中のためスキップ解除できません。停止後に再試行してください。", "確認", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        if (ListingsGrid?.SelectedItem is not ListingRow row)
        {
            AppendLog("No selection for skip clear.");
            MessageBox.Show("スキップ解除対象が選択されていません。", "確認", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        if (string.IsNullOrWhiteSpace(row.ItemId))
        {
            AppendLog("ItemId empty; skip clear aborted.");
            MessageBox.Show("スキップ解除対象の ItemId が空です。", "確認", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var confirm = MessageBox.Show(
            $"Clear today's skip? RunCount will be kept.\nTitle: {row.Title}\nItemId: {row.ItemId}",
            "確認",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);
        if (confirm != MessageBoxResult.Yes)
        {
            AppendLog("Skip clear canceled.");
            return;
        }

        try
        {
            var selectedCount = ListingsGrid?.SelectedItems.Count ?? 0;
            if (selectedCount <= 0)
            {
                AppendLog("Skip clear ignored: no selection.");
                MessageBox.Show("選択行がありません。", "確認", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var confirm1 = MessageBox.Show(
                $"選択 {selectedCount} 件の LastRunDate をクリアして今日のスキップを解除します。\n実行しますか？",
                "確認",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question);
            if (confirm1 != MessageBoxResult.Yes)
            {
                AppendLog("Skip clear canceled (first confirm).");
                return;
            }

            var confirm2 = MessageBox.Show(
                "最終確認: スキップ解除を実行しますか？",
                "最終確認",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning);
            if (confirm2 != MessageBoxResult.Yes)
            {
                AppendLog("Skip clear canceled (second confirm).");
                return;
            }

            var ok = await _itemRepository.ClearLastRunDateAsync(row.ItemId, CancellationToken.None);
            if (ok)
            {
                row.ExecutedAt = string.Empty;
                AppendLog($"ClearSkip ok: ItemId={row.ItemId}, LastRunDate cleared (RunCount kept={row.RunCount})");
            }
            else
            {
                AppendLog($"ClearSkip skipped: ItemId={row.ItemId} (record not found)");
            }
        }
        catch (Exception ex)
        {
            AppendLog("ClearSkip failed:");
            AppendLog(ex.ToString());

            try
            {
                var evidenceBase = $"ClearSkip_Failed_{DateTime.Now:yyyyMMdd-HHmmss}";
                var evidencePath = await _automationService.SaveEvidenceAsync(evidenceBase, CancellationToken.None);
                AppendLog($"ClearSkip evidence saved: {evidencePath}");
            }
            catch (Exception evEx)
            {
                AppendLog($"ClearSkip evidence save failed: {evEx}");
            }

            MessageBox.Show($"スキップ解除に失敗しました: {ex.Message}", "エラー", MessageBoxButton.OK, MessageBoxImage.Error);
            ShowLogArea();
        }
    }

    private void ResetItemButton_Click(object sender, RoutedEventArgs e)
    {
        _ = ResetItemButton_ClickAsync();
    }

    private async Task ResetItemButton_ClickAsync()
    {
        if (_operationCts != null)
        {
            AppendLog("Cannot reset while running. Stop first.");
            MessageBox.Show("実行中のためリセットできません。停止後に再試行してください。", "確認", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        if (ListingsGrid?.SelectedItem is not ListingRow row)
        {
            AppendLog("No selection for reset.");
            MessageBox.Show("リセット対象が選択されていません。", "確認", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        if (string.IsNullOrWhiteSpace(row.ItemId))
        {
            AppendLog("ItemId empty; reset aborted.");
            MessageBox.Show("リセット対象の ItemId が空です。", "確認", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var confirm = MessageBox.Show(
            $"Reset item to initial state (RunCount will be cleared).\nTitle: {row.Title}\nItemId: {row.ItemId}",
            "確認",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);
        if (confirm != MessageBoxResult.Yes)
        {
            AppendLog("Reset canceled.");
            return;
        }

        try
        {
            var selectedCount = ListingsGrid?.SelectedItems.Count ?? 0;
            if (selectedCount <= 0)
            {
                AppendLog("Reset ignored: no selection.");
                MessageBox.Show("選択行がありません。", "確認", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var confirm1 = MessageBox.Show(
                $"選択 {selectedCount} 件を初回状態に戻します。\nRunCount/LastRunDate/前回値下げ履歴がクリアされます。\n実行しますか？",
                "確認",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question);
            if (confirm1 != MessageBoxResult.Yes)
            {
                AppendLog("Reset canceled (first confirm).");
                return;
            }

            var confirm2 = MessageBox.Show(
                "最終確認: 初回に戻します。よろしいですか？",
                "最終確認",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning);
            if (confirm2 != MessageBoxResult.Yes)
            {
                AppendLog("Reset canceled (second confirm).");
                return;
            }

            var ok = await _itemRepository.ResetItemAsync(row.ItemId, 0, CancellationToken.None);
            if (ok)
            {
                row.RunCount = 0;
                row.LastDownAmount = 0;
                row.LastDownAtText = string.Empty;
                row.LastDownLabelText = string.Empty;
                row.ExecutedAt = string.Empty;
                AppendLog($"FullReset ok: ItemId={row.ItemId}, RunCount=0, LastRunDate/LastDown cleared");
            }
            else
            {
                AppendLog($"FullReset skipped: ItemId={row.ItemId} (record not found)");
            }
        }
        catch (Exception ex)
        {
            AppendLog("FullReset failed:");
            AppendLog(ex.ToString());

            try
            {
                var evidenceBase = $"ResetItem_Failed_{DateTime.Now:yyyyMMdd-HHmmss}";
                var evidencePath = await _automationService.SaveEvidenceAsync(evidenceBase, CancellationToken.None);
                AppendLog($"ResetItem evidence saved: {evidencePath}");
            }
            catch (Exception evEx)
            {
                AppendLog($"ResetItem evidence save failed: {evEx}");
            }

            MessageBox.Show($"リセットに失敗しました: {ex.Message}", "エラー", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void DevButton_Click(object sender, RoutedEventArgs e)
    {
        _ = DevButton_ClickAsync();
    }

    private async Task DevButton_ClickAsync()
    {
        var today = DateTime.UtcNow.ToString("yyyy-MM-dd");
        var dummy = new ItemState
        {
            ItemId = "dev-dummy-001",
            ItemUrl = "https://example.invalid/dev-dummy",
            Title = "DEV DUMMY",
            BasePrice = 1234,
            RunCount = 0,
            LastRunDate = null,
            LastDownAmount = 0,
            LastDownAt = null
        };

        try
        {
            await _itemRepository.UpsertAsync(dummy);
            await _itemRepository.UpdateRunCountIfNewDayAsync(dummy.ItemId, today);
            var loaded = await _itemRepository.GetByItemIdAsync(dummy.ItemId);

            var message = loaded == null
                ? "Load failed."
                : $"ItemId: {loaded.ItemId}\nBasePrice: {loaded.BasePrice}\nRunCount: {loaded.RunCount}\nLastRunDate: {loaded.LastRunDate ?? "(null)"}\nUpdatedAt: {loaded.UpdatedAt ?? "(null)"}";

            MessageBox.Show(message, "DEV CHECK", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Dev data error: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        _operationCts?.Cancel();
        _appCts.Cancel();
        _settings = CaptureSettingsFromUi();
        SaveSettings();
        base.OnClosed(e);
    }
    private void UpdateRowResult(ListingRow row, string status, string message, string executedAt)
    {
        row.ResultStatus = status;
        row.LastMessage = message;
        row.ExecutedAt = executedAt;
    }

    private static async Task WriteLogLineAsync(
        StreamWriter writer,
        ListingRow row,
        int basePrice,
        int newPrice,
        string result,
        string message,
        DateTime executedAt,
        string step,
        int retryUsed,
        string evidencePath)
    {
        static string Escape(string? value)
        {
            var safe = (value ?? string.Empty).Replace("\"", "\"\"").Replace("\r", " ").Replace("\n", " ");
            return $"\"{safe}\"";
        }

        var line = string.Join(",",
            Escape(row.ItemId),
            Escape(row.Title),
            Escape(row.ItemUrl),
            basePrice.ToString(),
            newPrice.ToString(),
            Escape(result),
            Escape(message),
            Escape(executedAt.ToString("yyyy-MM-dd HH:mm:ss")),
            Escape(step ?? string.Empty),
            retryUsed.ToString(),
            Escape(evidencePath ?? string.Empty));

        await writer.WriteLineAsync(line);
    }

    private void AppendLog(string message)
    {
        var line = $"[{DateTime.Now:HH:mm:ss}] {message}";
        LogText = string.IsNullOrEmpty(LogText) ? line : $"{LogText}{Environment.NewLine}{line}";
    }

    private void ToggleLogButton_Click(object sender, RoutedEventArgs e)
    {
        _isLogVisible = !_isLogVisible;
        ApplyLogVisibility();
    }

    private void ShowLogArea()
    {
        _isLogVisible = true;
        ApplyLogVisibility();
    }

    private void ApplyLogVisibility()
    {
        if (LogArea == null || ToggleLogButton == null) return;
        LogArea.Visibility = _isLogVisible ? Visibility.Visible : Visibility.Collapsed;
        ToggleLogButton.Content = _isLogVisible ? "ログを隠す" : "ログを表示";
    }

    private static string BuildLastDownLabel(int? rate, int? daily, int? runIndex)
    {
        if (rate == null || daily == null || runIndex == null)
        {
            return string.Empty;
        }

        if (runIndex <= 0)
        {
            return $"{rate}%";
        }

        var dailyPart = daily.Value * runIndex.Value;
        return $"{rate}%＋{dailyPart}円";
    }

    private void DisableActionButtons()
    {
        RunPriceButton.IsEnabled = false;
        FetchItemsButton.IsEnabled = false;
    }

    private void EnableActionButtons()
    {
        RunPriceButton.IsEnabled = true;
        FetchItemsButton.IsEnabled = true;
    }

    private RunState? LoadRunState()
    {
        try
        {
            if (!File.Exists(_runStatePath)) return null;
            var json = File.ReadAllText(_runStatePath, Encoding.UTF8);
            return JsonSerializer.Deserialize<RunState>(json);
        }
        catch (Exception ex)
        {
            AppendLog($"runstate load failed: {ex.Message}");
            return null;
        }
    }

    private void SaveRunState(RunState state)
    {
        try
        {
            var json = JsonSerializer.Serialize(state, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(_runStatePath, json, Encoding.UTF8);
        }
        catch (Exception ex)
        {
            AppendLog($"runstate save failed: {ex.Message}");
        }
    }

    private void LoadSettings()
    {
        try
        {
            if (File.Exists(_settingsPath))
            {
                var json = File.ReadAllText(_settingsPath, Encoding.UTF8);
                var loaded = JsonSerializer.Deserialize<AppSettings>(json);
                if (loaded != null)
                {
                    _settings = loaded;
                    ApplySettingsToUi(_settings);
                    return;
                }
            }
        }
        catch (Exception ex)
        {
            AppendLog($"settings load failed. fallback default: {ex.Message}");
        }

        _settings = AppSettings.CreateDefault();
        ApplySettingsToUi(_settings);
    }

    private void SaveSettings()
    {
        try
        {
            var json = JsonSerializer.Serialize(_settings, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(_settingsPath, json, Encoding.UTF8);
        }
        catch (Exception ex)
        {
            AppendLog($"settings save failed: {ex.Message}");
        }
    }

    private void DisableInputs()
    {
        StartRowTextBox.IsEnabled = false;
        EndRowTextBox.IsEnabled = false;
        SearchTextBox.IsEnabled = false;
        RatePercentTextBox.IsEnabled = false;
        DailyDownYenTextBox.IsEnabled = false;
        WaitAfterPauseTextBox.IsEnabled = false;
        WaitAfterResumeTextBox.IsEnabled = false;
        WaitBetweenItemsTextBox.IsEnabled = false;
        RetryCountTextBox.IsEnabled = false;
        RetryWaitTextBox.IsEnabled = false;
    }

    private void EnableInputs()
    {
        StartRowTextBox.IsEnabled = true;
        EndRowTextBox.IsEnabled = true;
        SearchTextBox.IsEnabled = true;
        RatePercentTextBox.IsEnabled = true;
        DailyDownYenTextBox.IsEnabled = true;
        WaitAfterPauseTextBox.IsEnabled = true;
        WaitAfterResumeTextBox.IsEnabled = true;
        WaitBetweenItemsTextBox.IsEnabled = true;
        RetryCountTextBox.IsEnabled = true;
        RetryWaitTextBox.IsEnabled = true;
    }

    private void LockUiForRun()
    {
        _isRunning = true;
        AppendLog("UI lock ON (running)");
        FetchItemsButton.IsEnabled = false;
        RunPriceButton.IsEnabled = false;
        ResetItemButton.IsEnabled = false;
        ClearSkipButton.IsEnabled = false;
        ApplyFilterButton.IsEnabled = false;
        ListingsGrid.IsEnabled = false;
        DisableInputs();
        StopButton.IsEnabled = true;
    }

    private void UnlockUiAfterRun()
    {
        _isRunning = false;
        AppendLog("UI lock OFF (idle)");
        FetchItemsButton.IsEnabled = true;
        RunPriceButton.IsEnabled = true;
        ResetItemButton.IsEnabled = true;
        ClearSkipButton.IsEnabled = true;
        ApplyFilterButton.IsEnabled = true;
        ListingsGrid.IsEnabled = true;
        EnableInputs();
        StopButton.IsEnabled = false;
    }

    private void UpdateCountSummary(int total, int excluded, int displayed)
    {
        CountSummary = $"表示 {displayed} / 取得 {total}（除外 {excluded}）";
        CountSummaryTextBlock.Text = CountSummary;
    }

    private void UpdateSelectionSummary()
    {
        var selectedCount = Listings.Count(x => x.IsChecked);
        var selectedRows = ListingsGrid?.SelectedItems.Count ?? 0;
        SelectionSummary = $"選択中: {selectedRows}件 / チェック: {selectedCount}件";
        SelectionSummaryTextBlock.Text = SelectionSummary;
    }

    private void Listings_CollectionChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
    {
        if (e.NewItems != null)
        {
            foreach (var item in e.NewItems.OfType<ListingRow>())
            {
                item.PropertyChanged += ListingRow_PropertyChanged;
            }
        }

        if (e.OldItems != null)
        {
            foreach (var item in e.OldItems.OfType<ListingRow>())
            {
                item.PropertyChanged -= ListingRow_PropertyChanged;
            }
        }

        UpdateSelectionSummary();
    }

    private void ListingRow_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ListingRow.IsChecked))
        {
            UpdateSelectionSummary();
        }
    }

    private void ListingsGrid_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        UpdateSelectionSummary();
    }

    private static string FormatTimestamp(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return string.Empty;
        if (DateTime.TryParse(raw, out var dt))
        {
            return dt.ToString("yyyy-MM-dd HH:mm");
        }
        return raw;
    }

    private void ApplySettingsToUi(AppSettings settings)
    {
        StartRowTextBox.Text = settings.StartRow.ToString();
        EndRowTextBox.Text = settings.EndRow.ToString();
        SearchTextBox.Text = settings.SearchText;
        RatePercentTextBox.Text = settings.RatePercent.ToString();
        DailyDownYenTextBox.Text = settings.DailyDownYen.ToString();
        WaitAfterPauseTextBox.Text = settings.WaitAfterPauseSec.ToString();
        WaitAfterResumeTextBox.Text = settings.WaitAfterResumeSec.ToString();
        WaitBetweenItemsTextBox.Text = settings.ItemGapSec.ToString();
        RetryCountTextBox.Text = settings.RetryCount.ToString();
        RetryWaitTextBox.Text = settings.RetryWaitSec.ToString();
        ApplyFilter();
    }

    private static int ParseOrDefault(string? text, int fallback)
    {
        return int.TryParse(text, out var value) && value > 0 ? value : fallback;
    }

    private AppSettings CaptureSettingsFromUi()
    {
        return new AppSettings
        {
            StartRow = ParseOrDefault(StartRowTextBox.Text, 1),
            EndRow = ParseOrDefault(EndRowTextBox.Text, 500),
            SearchText = SearchTextBox.Text ?? string.Empty,
            RatePercent = ParseOrDefault(RatePercentTextBox.Text, 10),
            DailyDownYen = ParseOrDefault(DailyDownYenTextBox.Text, 100),
            WaitAfterPauseSec = ParseOrDefault(WaitAfterPauseTextBox.Text, 30),
            WaitAfterResumeSec = ParseOrDefault(WaitAfterResumeTextBox.Text, 10),
            ItemGapSec = ParseOrDefault(WaitBetweenItemsTextBox.Text, 250),
            RetryCount = ParseOrDefault(RetryCountTextBox.Text, 2),
            RetryWaitSec = ParseOrDefault(RetryWaitTextBox.Text, 2)
        };
    }

    protected void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}

public class RunState
{
    public string SessionId { get; set; } = DateTime.Now.ToString("yyyyMMddHHmmss");
    public string StartedAt { get; set; } = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
    public int TargetCount { get; set; }
    public int CurrentIndex { get; set; }
    public bool IsCompleted { get; set; }
    public List<RunItemState> Items { get; set; } = new();

    public static RunState CreateNew(IEnumerable<ListingRow> items)
    {
        var state = new RunState();
        state.Items = items.Select(i => new RunItemState
        {
            ItemId = i.ItemId,
            Title = i.Title,
            Status = "未実行",
            Message = string.Empty
        }).ToList();
        state.TargetCount = state.Items.Count;
        return state;
    }
}

public class RunItemState
{
    public string ItemId { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string Status { get; set; } = "未実行";
    public string? Message { get; set; }
    public string? ExecutedAt { get; set; }
}

public class AppSettings
{
    public int StartRow { get; set; }
    public int EndRow { get; set; }
    public string SearchText { get; set; } = string.Empty;
    public int RatePercent { get; set; }
    public int DailyDownYen { get; set; }
    public int WaitAfterPauseSec { get; set; }
    public int WaitAfterResumeSec { get; set; }
    public int ItemGapSec { get; set; }
    public int RetryCount { get; set; }
    public int RetryWaitSec { get; set; }

    public static AppSettings CreateDefault() => new()
    {
        StartRow = 1,
        EndRow = 500,
        SearchText = string.Empty,
        RatePercent = 10,
        DailyDownYen = 100,
        WaitAfterPauseSec = 30,
        WaitAfterResumeSec = 10,
        ItemGapSec = 250,
        RetryCount = 2,
        RetryWaitSec = 2
    };
}
