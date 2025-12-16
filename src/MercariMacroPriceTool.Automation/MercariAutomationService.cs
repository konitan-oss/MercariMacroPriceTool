using Microsoft.Playwright;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace MercariMacroPriceTool.Automation;

/// <summary>
/// Playwright を使って listings 取得と価格改定操作を担うサービス。
/// </summary>
public class MercariAutomationService
{
    private readonly string _storageStatePath;
    private readonly IReadOnlyList<string> _pausedTextCandidates;
    private readonly IReadOnlyList<string> _editButtonSelectors;
    private readonly IReadOnlyList<string> _priceInputSelectors;
    private readonly IReadOnlyList<string> _pauseSelectors;
    private readonly IReadOnlyList<string> _resumeSelectors;
    private readonly IReadOnlyList<string> _popupCloseSelectors;

    private IPlaywright? _playwright;
    private IBrowser? _browser;
    private IBrowserContext? _context;
    private IPage? _page;

    public MercariAutomationService()
    {
        var solutionRoot = SolutionPathLocator.FindSolutionRoot();
        var localDir = Path.Combine(solutionRoot, ".local");
        Directory.CreateDirectory(localDir);
        _storageStatePath = Path.Combine(localDir, "storageState.json");

        _pausedTextCandidates = SelectorsConfig.GetPausedTextCandidates();
        _editButtonSelectors = SelectorsConfig.GetEditButtonSelectors();
        _priceInputSelectors = SelectorsConfig.GetPriceInputSelectors();
        _pauseSelectors = SelectorsConfig.GetPauseSelectors();
        _resumeSelectors = SelectorsConfig.GetResumeSelectors();
        _popupCloseSelectors = SelectorsConfig.GetPopupCloseSelectors();
    }

    public async Task LaunchBrowserAsync(Func<Task>? promptForManualLogin = null, CancellationToken cancellationToken = default)
    {
        _playwright ??= await Playwright.CreateAsync();

        _browser ??= await _playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions
        {
            Headless = false,
            Channel = "chrome"
        });

        if (_context == null)
        {
            var contextOptions = new BrowserNewContextOptions();
            var exists = File.Exists(_storageStatePath);
            Console.WriteLine($"[StorageState] path={_storageStatePath}, exists={exists}");
            if (File.Exists(_storageStatePath))
            {
                contextOptions.StorageStatePath = _storageStatePath;
                Console.WriteLine("[StorageState] applied storageState.json");
            }

            _context = await _browser.NewContextAsync(contextOptions);
            _page = await _context.NewPageAsync();
        }

        if (_page == null)
        {
            _page = await _context!.NewPageAsync();
        }

        await _page.GotoAsync(AutomationEndpoints.ListingsUrl, new() { WaitUntil = WaitUntilState.DOMContentLoaded, Timeout = 60000 });

        if (!File.Exists(_storageStatePath))
        {
            if (promptForManualLogin != null)
            {
                await promptForManualLogin();
            }

            cancellationToken.ThrowIfCancellationRequested();
            await _context!.StorageStateAsync(new BrowserContextStorageStateOptions { Path = _storageStatePath });
        }
    }

    /// <summary>
    /// listings ページから StartRow〜EndRow の範囲で商品一覧を取得する。
    /// </summary>
    public async Task<IReadOnlyList<ListingItem>> FetchListingsAsync(int startRow, int endRow, IProgress<string>? progress = null, CancellationToken cancellationToken = default)
    {
        if (startRow < 1) startRow = 1;
        if (endRow < startRow) endRow = startRow;

        try
        {
            await LaunchBrowserAsync(null, cancellationToken);
            if (_page == null) throw new InvalidOperationException("Playwright page is not initialized.");

            await _page.GotoAsync(AutomationEndpoints.ListingsUrl, new() { WaitUntil = WaitUntilState.DOMContentLoaded, Timeout = 60000 });

            // 一覧ページが成立しているか確認。成立しない場合は証跡を残して空リスト返却。
            var ready = await IsListingsPageReadyAsync(progress, cancellationToken);
            if (!ready)
            {
                try
                {
                    var evidence = await SaveEvidenceAsync($"FetchListings_NotReady_{DateTime.Now:yyyyMMdd-HHmmss}", CancellationToken.None);
                    progress?.Report($"[FetchListings] listings page not ready. evidence: {evidence}");
                }
                catch
                {
                    // ignore
                }
                return Array.Empty<ListingItem>();
            }

            var cardSelector = await DetermineCardSelectorAsync(_page, cancellationToken);
            progress?.Report($"カードセレクタ: {cardSelector}");

            var maxLoop = 20;
            var currentCount = await CountCardsAsync(_page, cardSelector, cancellationToken);
            var targetCount = endRow;

            for (var i = 0; i < maxLoop && currentCount < targetCount; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await _page.EvaluateAsync("window.scrollBy(0, document.body.scrollHeight);");
                await _page.WaitForTimeoutAsync(800);
                currentCount = await CountCardsAsync(_page, cardSelector, cancellationToken);
                progress?.Report($"スクロール {i + 1}/{maxLoop}: 読み込み件数 {currentCount}");
            }

            if (currentCount < startRow)
            {
                throw new InvalidOperationException("指定範囲に必要な件数を読み込めませんでした。");
            }

            var handles = await _page.Locator(cardSelector).ElementHandlesAsync();
            var sliced = handles
                .Skip(startRow - 1)
                .Take(Math.Max(0, endRow - startRow + 1))
                .ToList();

            var results = new List<ListingItem>();
            foreach (var handle in sliced)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var item = await ExtractListingAsync(handle);
                results.Add(item);
            }

            // __NEXT_DATA__ フォールバック
            await TryFillPricesFromNextDataAsync(results, progress, cancellationToken);

            // 価格 0 のみ商品ページで補完（上限 10 件）
            var zeroPriceItems = results.Where(x => x.Price <= 0 && !string.IsNullOrWhiteSpace(x.ItemUrl)).Take(10).ToList();
            var originalUrl = _page.Url;
            var filledFromItemPage = 0;
            foreach (var item in zeroPriceItems)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var price = await FetchPriceFromItemPageAsync(item.ItemUrl, progress, cancellationToken);
                if (price > 0)
                {
                    item.Price = price;
                    filledFromItemPage++;
                }
            }

            if (!string.IsNullOrWhiteSpace(originalUrl))
            {
                await _page.GotoAsync(originalUrl, new() { WaitUntil = WaitUntilState.DOMContentLoaded, Timeout = 60000 });
            }

            var zeroCount = results.Count(x => x.Price <= 0);
            progress?.Report($"価格取得サマリ: 全体 {results.Count} 件, 0件 {zeroCount}, 個別補完 {filledFromItemPage} 件");

            if (zeroCount >= 3)
            {
                try
                {
                    var evidence = await SaveEvidenceAsync($"FetchListings_ZeroPrice_{DateTime.Now:yyyyMMdd-HHmm}", cancellationToken);
                    progress?.Report($"価格0が多いため証跡保存: {evidence}");
                }
                catch
                {
                    // ベストエフォート
                }
            }

            return results;
        }
        catch (Exception ex)
        {
            try
            {
                var evidence = await SaveEvidenceAsync($"FetchListings_Failed_{DateTime.Now:yyyyMMdd-HHmmss}", CancellationToken.None);
                progress?.Report($"[FetchListings] failed: {ex.GetType().Name}: {ex.Message}");
                progress?.Report($"[FetchListings] evidence: {evidence}");
            }
            catch
            {
                // ignore evidence failure
            }

            throw;
        }
    }

    /// <summary>
    /// 商品ページを開き、Save を使わずに停止/再開で価格改定を確定する。
    /// </summary>
    public async Task<PriceUpdateResult> RunPriceUpdateCycleAsync(
        string itemUrl,
        int newPrice,
        int basePrice,
        PriceUpdateOptions options,
        int retryCount,
        int retryWaitSec,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        await LaunchBrowserAsync(null, cancellationToken);
        if (_page == null) throw new InvalidOperationException("Playwright page is not initialized.");

        var result = new PriceUpdateResult();
        var currentStep = "Init";

        async Task NavigateAsync(string url, string step)
        {
            currentStep = step;
            await ExecuteWithRetryAsync(step, retryCount, retryWaitSec, progress, cancellationToken, async () =>
            {
                progress?.Report($"[{step}] Navigate start (WaitUntil=DOMContentLoaded, Timeout=90s): {url}");
                await _page!.GotoAsync(url, new() { WaitUntil = WaitUntilState.DOMContentLoaded, Timeout = 90000 });
                progress?.Report($"[{step}] Goto success");
                await _page.WaitForSelectorAsync("main#main", new() { Timeout = 30000 });
                progress?.Report($"[{step}] main#main detected");
                await DismissObstructionsAsync(_page, cancellationToken, msg => progress?.Report(msg));
            });
            result.LastStep = step;
        }

        try
        {
            // 値下げ側
            await NavigateAsync(itemUrl, "NavigateItem");
            currentStep = "EditClick";
            await ClickFirstAsync(_editButtonSelectors, "EditClick", retryCount, retryWaitSec, progress, cancellationToken, result);

            currentStep = "PriceInput";
            await FillPriceAsync(newPrice, "PriceInput", retryCount, retryWaitSec, progress, cancellationToken, result);

            currentStep = "Pause";
            await ClickFirstAsync(_pauseSelectors, "Pause", retryCount, retryWaitSec, progress, cancellationToken, result);
            await WaitForPauseSuccessAsync(itemUrl, progress, cancellationToken);

            await WaitWithCancellation(options.WaitAfterPauseSeconds, "WaitAfterPause", progress, cancellationToken);
            result.LastStep = "WaitAfterPause";

            // 復帰側
            await NavigateAsync(itemUrl, "NavigateItemResume");
            currentStep = "EditBeforeResume";
            await ClickFirstAsync(_editButtonSelectors, "EditBeforeResume", retryCount, retryWaitSec, progress, cancellationToken, result);

            currentStep = "PriceInputRestore";
            await FillPriceAsync(basePrice, "PriceInputRestore", retryCount, retryWaitSec, progress, cancellationToken, result);

            currentStep = "Resume";
            await ClickFirstAsync(_resumeSelectors, "Resume", retryCount, retryWaitSec, progress, cancellationToken, result);
            await WaitForResumeSuccessAsync(itemUrl, progress, cancellationToken);

            await WaitWithCancellation(options.WaitAfterResumeSeconds, "WaitAfterResume", progress, cancellationToken);
            result.LastStep = "WaitAfterResume";

            progress?.Report("価格改定サイクルを完了しました");
            return result;
        }
        catch (OperationCanceledException)
        {
            await TrySaveCanceledEvidenceAsync(itemUrl, currentStep, cancellationToken);
            throw;
        }
    }

    public async Task<string> SaveEvidenceAsync(string baseName, CancellationToken cancellationToken)
    {
        if (_page == null) throw new InvalidOperationException("Playwright page is not initialized.");

        var solutionRoot = SolutionPathLocator.FindSolutionRoot();
        var logDir = Path.Combine(solutionRoot, ".local", "logs", "evidence");
        Directory.CreateDirectory(logDir);

        var pngPath = Path.Combine(logDir, $"{baseName}.png");
        var htmlPath = Path.Combine(logDir, $"{baseName}.html");

        await _page.ScreenshotAsync(new() { Path = pngPath, FullPage = true });
        var html = await _page.ContentAsync();
        await File.WriteAllTextAsync(htmlPath, html, cancellationToken);

        return $"{pngPath};{htmlPath}";
    }

    private async Task TrySaveCanceledEvidenceAsync(string itemUrl, string step, CancellationToken cancellationToken)
    {
        try
        {
            var baseName = $"{DateTime.Now:yyyyMMdd-HHmm}_Canceled_{step}";
            await SaveEvidenceAsync(baseName, cancellationToken);
        }
        catch
        {
            // ベストエフォート。失敗しても握りつぶす。
        }
    }

    private async Task<int> ExecuteWithRetryAsync(string stepName, int retryCount, int retryWaitSec, IProgress<string>? progress, CancellationToken cancellationToken, Func<Task> action)
    {
        var attempt = 0;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                if (attempt > 0)
                {
                    progress?.Report($"[{stepName}] リトライ {attempt}/{retryCount}");
                }
                await action();
                return attempt;
            }
            catch (Exception ex) when (attempt < retryCount)
            {
                attempt++;
                progress?.Report($"[{stepName}] 失敗: {ex.Message} -> 再試行 {attempt}/{retryCount}");
                await Task.Delay(TimeSpan.FromSeconds(Math.Max(1, retryWaitSec)), cancellationToken);
            }
            catch (Exception ex)
            {
                throw new StepFailedException(stepName, attempt, ex);
            }
        }
    }

    private async Task ClickFirstAsync(IReadOnlyList<string> selectors, string label, int retryCount, int retryWaitSec, IProgress<string>? progress, CancellationToken cancellationToken, PriceUpdateResult result)
    {
        if (_page == null) throw new InvalidOperationException("Page is not initialized.");

        foreach (var selector in selectors)
        {
            cancellationToken.ThrowIfCancellationRequested();
            progress?.Report($"[{label}] セレクタ試行: {selector}");
            try
            {
                var attempts = await ExecuteWithRetryAsync(label, retryCount, retryWaitSec, progress, cancellationToken, async () =>
                {
                    var locator = _page.Locator(selector).First;
                    await locator.WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Visible, Timeout = 30000 });
                    await ClickWithRecoveryAsync(_page, locator, label, progress, cancellationToken);
                });

                progress?.Report($"[{label}] success: {selector} (リトライ {attempts})");
                result.LastStep = label;
                result.RetryUsed += attempts;
                await _page.WaitForTimeoutAsync(300);
                return;
            }
            catch (Exception ex)
            {
                progress?.Report($"[{label}] 失敗: {ex.Message} (selector: {selector})");
                // 次の候補へ
            }
        }

        throw new StepFailedException(label, 0, new InvalidOperationException($"{label} に成功するセレクタが見つかりませんでした。"));
    }

    private async Task FillPriceAsync(int price, string label, int retryCount, int retryWaitSec, IProgress<string>? progress, CancellationToken cancellationToken, PriceUpdateResult result)
    {
        if (_page == null) throw new InvalidOperationException("Page is not initialized.");

        foreach (var selector in _priceInputSelectors)
        {
            cancellationToken.ThrowIfCancellationRequested();
            progress?.Report($"[{label}] セレクタ試行: {selector}");
            var attempts = await ExecuteWithRetryAsync(label, retryCount, retryWaitSec, progress, cancellationToken, async () =>
            {
                var target = _page.Locator(selector).First;
                await target.WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Visible, Timeout = 30000 });
                await ClickWithRecoveryAsync(_page, target, label + "/Focus", progress, cancellationToken);
                await target.FillAsync(price.ToString());
            });

            progress?.Report($"[{label}] success: {selector} (リトライ {attempts})");
            result.LastStep = label;
            result.RetryUsed += attempts;
            await _page.WaitForTimeoutAsync(200);
            return;
        }

        throw new StepFailedException(label, 0, new InvalidOperationException("価格入力欄が見つかりません。"));
    }

    private async Task ClickWithRecoveryAsync(IPage page, ILocator locator, string label, IProgress<string>? progress, CancellationToken cancellationToken)
    {
        await locator.WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Visible, Timeout = 10000 });
        try
        {
            await locator.ClickAsync(new() { Timeout = 8000 });
            progress?.Report($"[{label}] click ok (normal)");
            return;
        }
        catch (PlaywrightException ex) when (IsInterceptionLike(ex))
        {
            progress?.Report($"[{label}] click intercepted -> dismiss then retry");
            await DismissObstructionsAsync(page, cancellationToken, msg => progress?.Report(msg));
        }
        catch (PlaywrightException ex)
        {
            progress?.Report($"[{label}] click failed ({ex.Message}) -> retry");
        }

        // retry after dismiss
        try
        {
            await locator.ClickAsync(new() { Timeout = 8000 });
            progress?.Report($"[{label}] click ok (retry)");
            return;
        }
        catch (PlaywrightException ex)
        {
            progress?.Report($"[{label}] click retry failed ({ex.Message}) -> JS click");
            await locator.EvaluateAsync("el => el.click()");
            progress?.Report($"[{label}] click ok (js)");
        }
    }

    private async Task DismissObstructionsAsync(IPage page, CancellationToken cancellationToken, Action<string>? log)
    {
        log?.Invoke("[PopupDismiss] start");
        foreach (var selector in _popupCloseSelectors)
        {
            cancellationToken.ThrowIfCancellationRequested();
            log?.Invoke($"[PopupDismiss] try: {selector}");
            try
            {
                var locator = page.Locator(selector).First;
                await locator.WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Visible, Timeout = 1000 });
                await locator.ClickAsync(new() { Timeout = 2000 });
                await WaitForLocatorToDisappearAsync(locator, cancellationToken);
                log?.Invoke($"[PopupDismiss] closed: {selector}");
                return;
            }
            catch (TimeoutException)
            {
                // not found
            }
            catch (PlaywrightException ex)
            {
                log?.Invoke($"[PopupDismiss] fail: {selector} ({ex.Message})");
            }
        }

        try
        {
            log?.Invoke("[PopupDismiss] try: Escape");
            await page.Keyboard.PressAsync("Escape");
            await page.WaitForTimeoutAsync(300);
        }
        catch (Exception ex)
        {
            log?.Invoke($"[PopupDismiss] escape fail: {ex.Message}");
        }

        log?.Invoke("[PopupDismiss] giveup");
    }

    private async Task WaitForPauseSuccessAsync(string itemUrl, IProgress<string>? progress, CancellationToken cancellationToken)
    {
        progress?.Report("[Pause] success 判定待ち");
        await WaitForStateAsync(itemUrl, _pausedTextCandidates, progress, cancellationToken);
    }

    private async Task WaitForResumeSuccessAsync(string itemUrl, IProgress<string>? progress, CancellationToken cancellationToken)
    {
        progress?.Report("[Resume] success 判定待ち");
        await WaitForStateAsync(itemUrl, Array.Empty<string>(), progress, cancellationToken);
    }

    private async Task WaitForStateAsync(string itemUrl, IReadOnlyList<string> pauseTexts, IProgress<string>? progress, CancellationToken cancellationToken)
    {
        var timeout = 30000;
        var start = DateTime.UtcNow;
        while ((DateTime.UtcNow - start).TotalMilliseconds < timeout)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var url = _page?.Url ?? string.Empty;
            if (url.Contains("/item/", StringComparison.OrdinalIgnoreCase))
            {
                progress?.Report("[StateCheck] URLで商品ページに戻ったと判定");
                return;
            }

            if (_page != null && pauseTexts.Any())
            {
                var textFound = await _page.GetByText(pauseTexts.First(), new() { Exact = false }).IsVisibleAsync(new LocatorIsVisibleOptions { Timeout = 500 }).ConfigureAwait(false);
                if (textFound)
                {
                    progress?.Report("[StateCheck] 公開停止中テキストを検出");
                    return;
                }
            }

            if (_page != null)
            {
                foreach (var sel in _editButtonSelectors)
                {
                    try
                    {
                        var vis = await _page.Locator(sel).First.IsVisibleAsync(new LocatorIsVisibleOptions { Timeout = 500 });
                        if (vis)
                        {
                            progress?.Report("[StateCheck] 商品ページ側の編集ボタンを検出");
                            return;
                        }
                    }
                    catch
                    {
                        // ignore
                    }
                }
            }

            await Task.Delay(500, cancellationToken);
        }

        throw new StepFailedException("StateCheck", 0, new TimeoutException("状態遷移を確認できませんでした"));
    }

    private static async Task WaitForLocatorToDisappearAsync(ILocator locator, CancellationToken cancellationToken)
    {
        try
        {
            await locator.WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Detached, Timeout = 5000 });
            return;
        }
        catch
        {
            // fall through
        }

        try
        {
            await locator.WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Hidden, Timeout = 5000 });
        }
        catch
        {
            // ignore
        }
    }

    private static bool IsInterceptionLike(PlaywrightException ex)
    {
        var message = ex.Message ?? string.Empty;
        return message.Contains("not visible", StringComparison.OrdinalIgnoreCase)
               || message.Contains("intercept", StringComparison.OrdinalIgnoreCase)
               || message.Contains("other element would receive the click", StringComparison.OrdinalIgnoreCase)
               || message.Contains("element is detached", StringComparison.OrdinalIgnoreCase)
               || message.Contains("not attached", StringComparison.OrdinalIgnoreCase);
    }

    private async Task<bool> IsListingsPageReadyAsync(IProgress<string>? progress, CancellationToken cancellationToken)
    {
        try
        {
            await _page!.WaitForSelectorAsync("a[href*=\"/item/\"]", new() { Timeout = 5000 });
            return true;
        }
        catch
        {
            progress?.Report("[FetchListings] listings page not ready (anchor not found). Login/取得失敗の可能性。");
            return false;
        }
    }

    private static async Task WaitWithCancellation(int seconds, string label, IProgress<string>? progress, CancellationToken cancellationToken)
    {
        if (seconds <= 0) return;
        progress?.Report($"{label}: {seconds} 秒待機");
        for (var i = 0; i < seconds; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await Task.Delay(1000, cancellationToken);
        }
    }

    private static async Task<int> CountCardsAsync(IPage page, string selector, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return await page.Locator(selector).CountAsync();
    }

    private static readonly string[] CardSelectorCandidates =
    {
        "[data-testid='mypage-item-card']",
        "[data-testid='mypage-item']",
        "li[data-testid='mypage-item']",
        "section a[href*='/item/']",
        "a[href*='/item/']"
    };

    private static async Task<string> DetermineCardSelectorAsync(IPage page, CancellationToken cancellationToken)
    {
        foreach (var selector in CardSelectorCandidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var count = await page.Locator(selector).CountAsync();
            if (count > 0)
            {
                return selector;
            }
        }

        return "a[href*='/item/']";
    }

    private async Task<ListingItem> ExtractListingAsync(IElementHandle card)
    {
        var pausedTexts = _pausedTextCandidates.ToArray();
        var data = await card.EvaluateAsync<ListingItem>(@"
(node, pausedTexts) => {
    const text = node.innerText || '';
    const findFirst = (root, selectors) => {
        for (const sel of selectors) {
            const el = root.querySelector(sel);
            if (el) return el;
        }
        return null;
    };

    const link = node.tagName === 'A' ? node : findFirst(node, ['a[href*=""/item/""]', 'a[data-testid*=""item"" i]']);
    const itemUrl = link?.href || '';
    const itemIdMatch = itemUrl.match(/item\/([A-Za-z0-9\-]+)/);
    const itemId = itemIdMatch ? itemIdMatch[1] : '';

    const titleEl = findFirst(node, ['[data-testid*=""title"" i]', 'h3', 'h2', 'h4', 'p']);
    let title = (titleEl?.textContent || '').trim();
    if (!title && link) {
        title = (link.textContent || '').trim();
    }

    const statusEl = findFirst(node, ['[data-testid*=""status"" i]', '[class*=""status"" i]', 'button', 'div']);
    let statusText = (statusEl?.textContent || '').trim();
    if (!statusText) {
        const lines = text.split('\n').map(x => x.trim()).filter(Boolean);
        statusText = lines.find(x => pausedTexts.some(p => x.includes(p))) || lines[0] || '';
    }

    const combined = (statusText || '') + ' ' + text;
    const isPaused = pausedTexts.some(p => combined.includes(p));

    return {
        ItemId: itemId,
        Title: title,
        Price: 0,
        ItemUrl: itemUrl,
        StatusText: statusText,
        IsPaused: isPaused,
        RawText: text
    };
}", pausedTexts);

        // 最優先でカードテキストから価格を抽出
        var price = TryExtractPriceFromText(data.RawText ?? string.Empty);
        if (price > 0)
        {
            data.Price = price;
        }

        return data;
    }

    private static int TryExtractPriceFromText(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return 0;
        var cleaned = text.Replace("\u00a0", " ");
        var match = System.Text.RegularExpressions.Regex.Match(cleaned, @"[¥￥]\s*([0-9]{1,3}(?:,[0-9]{3})*|[0-9]+)");
        if (match.Success)
        {
            if (int.TryParse(match.Groups[1].Value.Replace(",", ""), out var price) && price > 0)
            {
                return price;
            }
        }
        return 0;
    }

    private async Task TryFillPricesFromNextDataAsync(List<ListingItem> items, IProgress<string>? progress, CancellationToken cancellationToken)
    {
        try
        {
            var scriptLocator = _page!.Locator("script#__NEXT_DATA__").First;
            var exists = await scriptLocator.CountAsync();
            if (exists == 0)
            {
                progress?.Report("[NEXT_DATA] not found, skip");
                return;
            }

            var script = await scriptLocator.InnerTextAsync();
            if (string.IsNullOrWhiteSpace(script)) return;

            foreach (var item in items.Where(x => x.Price <= 0 && !string.IsNullOrWhiteSpace(x.ItemId)))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var pattern = $"\\\"{item.ItemId}\\\"[^{{}}]*?\\\"price\\\":(\\d+)";
                var m = System.Text.RegularExpressions.Regex.Match(script, pattern, System.Text.RegularExpressions.RegexOptions.Singleline);
                if (m.Success && int.TryParse(m.Groups[1].Value, out var price) && price > 0)
                {
                    item.Price = price;
                    progress?.Report($"[NEXT_DATA] price filled for {item.ItemId}: {price}");
                }
            }
        }
        catch (Exception ex)
        {
            progress?.Report($"[NEXT_DATA] parse skip: {ex.Message}");
        }
    }

    private async Task<int> FetchPriceFromItemPageAsync(string itemUrl, IProgress<string>? progress, CancellationToken cancellationToken)
    {
        try
        {
            await _page!.GotoAsync(itemUrl, new() { WaitUntil = WaitUntilState.DOMContentLoaded, Timeout = 60000 });
            await _page.WaitForSelectorAsync("main#main", new() { Timeout = 30000 });
            var json = await _page.Locator("script[type=\"application/ld+json\"]").First.InnerTextAsync();
            if (string.IsNullOrWhiteSpace(json)) return 0;

            using var doc = System.Text.Json.JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("offers", out var offers))
            {
                if (offers.TryGetProperty("price", out var priceProp))
                {
                    if (priceProp.ValueKind == System.Text.Json.JsonValueKind.Number && priceProp.TryGetInt32(out var priceNum))
                    {
                        return priceNum;
                    }
                    if (priceProp.ValueKind == System.Text.Json.JsonValueKind.String && int.TryParse(priceProp.GetString(), out var priceStr))
                    {
                        return priceStr;
                    }
                }
            }
        }
        catch (Exception ex)
        {
            progress?.Report($"[ItemPagePrice] fail {itemUrl}: {ex.Message}");
        }

        return 0;
    }
}
