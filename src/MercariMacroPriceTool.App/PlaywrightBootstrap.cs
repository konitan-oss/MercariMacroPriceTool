using System;
using System.IO;
using System.Windows;

namespace MercariMacroPriceTool.App;

public static class PlaywrightBootstrap
{
    public static bool EnsureBrowsersPath(IProgress<string>? progress = null)
    {
        try
        {
            var baseDir = AppContext.BaseDirectory;
            var bundled = Path.Combine(baseDir, "ms-playwright");
            if (Directory.Exists(bundled))
            {
                Environment.SetEnvironmentVariable("PLAYWRIGHT_BROWSERS_PATH", bundled);
                progress?.Report($"Playwright browsers path set: {bundled}");
                return true;
            }

            var msg = $"ms-playwright フォルダが見つかりませんでした。配布ZIPを丸ごと解凍して exe と同じ場所に ms-playwright を置いてください。\n期待パス: {bundled}";
            progress?.Report(msg);
            MessageBox.Show(msg, "Playwright 初期化エラー", MessageBoxButton.OK, MessageBoxImage.Error);
            return false;
        }
        catch (Exception ex)
        {
            progress?.Report($"Playwright bootstrap failed: {ex.Message}");
            MessageBox.Show($"Playwright 初期化でエラーが発生しました。{ex.Message}", "Playwright 初期化エラー", MessageBoxButton.OK, MessageBoxImage.Error);
            return false;
        }
    }
}
