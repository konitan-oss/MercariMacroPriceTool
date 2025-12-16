using System;
using System.Collections.Generic;
using System.IO;

namespace MercariMacroPriceTool.Automation;

public static class SelectorsConfig
{
    private const string SelectorsFileName = "SELECTORS.md";

    private static IReadOnlyList<string>? _pausedCache;
    private static IReadOnlyList<string>? _editCache;
    private static IReadOnlyList<string>? _priceInputCache;
    private static IReadOnlyList<string>? _saveCache;
    private static IReadOnlyList<string>? _pauseCache;
    private static IReadOnlyList<string>? _resumeCache;
    private static IReadOnlyList<string>? _popupCloseCache;
    private static IReadOnlyList<string>? _pauseConfirmCache;
    private static IReadOnlyList<string>? _resumeConfirmCache;

    public static IReadOnlyList<string> GetPausedTextCandidates() =>
        _pausedCache ??= LoadList("PausedTextCandidates", new[]
        {
            "公開停止中",
            "出品を再開する",
            "停止中"
        });

    public static IReadOnlyList<string> GetEditButtonSelectors() =>
        _editCache ??= LoadList("EditButtonSelectors", new[]
        {
            "a[href^=\"/sell/edit/\"]",
            "a:has-text(\"商品の編集\")",
            "button:has-text(\"商品の編集\")",
            "[data-testid=\"edit-button\"]"
        });

    public static IReadOnlyList<string> GetPriceInputSelectors() =>
        _priceInputCache ??= LoadList("PriceInputSelectors", new[]
        {
            "input[name=\"price\"]",
            "input[data-testid*=\"price\"]",
            "input[type=\"number\"]"
        });

    public static IReadOnlyList<string> GetSaveButtonSelectors() =>
        _saveCache ??= LoadList("SaveButtonSelectors", new[]
        {
            "button:has-text(\"更新する\")",
            "button:has-text(\"保存\")",
            "button[data-testid=\"save-button\"]"
        });

    public static IReadOnlyList<string> GetPauseButtonSelectors() =>
        _pauseCache ??= LoadList("PauseButtonSelectors", new[]
        {
            "button:has-text(\"出品を一時停止する\")",
            "button:has-text(\"出品を停止する\")",
            "button[data-testid=\"pause-button\"]"
        });

    public static IReadOnlyList<string> GetResumeButtonSelectors() =>
        _resumeCache ??= LoadList("ResumeButtonSelectors", new[]
        {
            "button:has-text(\"出品を再開する\")",
            "button:has-text(\"出品を再開\")",
            "button[data-testid=\"resume-button\"]"
        });

    public static IReadOnlyList<string> GetPauseSelectors() =>
        _pauseConfirmCache ??= LoadList("PauseSelectors", new[]
        {
            "button:has-text(\"出品を一時停止\")",
            "a:has-text(\"出品を一時停止\")",
            "[role=\"button\"]:has-text(\"出品を一時停止\")",
            "[data-testid*=\"pause\"]"
        });

    public static IReadOnlyList<string> GetResumeSelectors() =>
        _resumeConfirmCache ??= LoadList("ResumeSelectors", new[]
        {
            "button:has-text(\"出品を再開\")",
            "a:has-text(\"出品を再開\")",
            "[role=\"button\"]:has-text(\"出品を再開\")",
            "[data-testid*=\"resume\"]"
        });

    public static IReadOnlyList<string> GetPopupCloseSelectors() =>
        _popupCloseCache ??= LoadList("PopupCloseSelectors", new[]
        {
            "button[aria-label=\"閉じる\"]",
            "[data-testid=\"modal-close\"]",
            "[data-testid=\"close\"]",
            "button:has-text(\"閉じる\")",
            "button:has-text(\"×\")"
        });

    private static IReadOnlyList<string> LoadList(string heading, IReadOnlyList<string> defaults)
    {
        try
        {
            var root = SolutionPathLocator.FindSolutionRoot();
            var path = Path.Combine(root, "docs", SelectorsFileName);
            if (!File.Exists(path))
            {
                return defaults;
            }

            var lines = File.ReadAllLines(path);
            var results = new List<string>();
            var inSection = false;

            foreach (var rawLine in lines)
            {
                var line = rawLine.Trim();
                if (line.StartsWith("### ", StringComparison.OrdinalIgnoreCase))
                {
                    inSection = string.Equals(line.Substring(4), heading, StringComparison.OrdinalIgnoreCase);
                    continue;
                }

                if (line.StartsWith("## ", StringComparison.OrdinalIgnoreCase))
                {
                    if (inSection)
                    {
                        break;
                    }
                    continue;
                }

                if (inSection && line.StartsWith("-", StringComparison.Ordinal))
                {
                    var value = line.TrimStart('-').Trim();
                    var hashIndex = value.IndexOf('#');
                    if (hashIndex >= 0)
                    {
                        value = value[..hashIndex].Trim();
                    }

                    if (!string.IsNullOrWhiteSpace(value))
                    {
                        results.Add(value);
                    }
                }
            }

            return results.Count > 0 ? results : defaults;
        }
        catch
        {
            return defaults;
        }
    }
}
