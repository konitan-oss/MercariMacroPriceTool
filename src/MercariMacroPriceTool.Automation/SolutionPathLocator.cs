using System;
using System.IO;

namespace MercariMacroPriceTool.Automation;

/// <summary>
/// ビルド出力ディレクトリからソリューションルート（MercariMacroPriceTool.sln）を探すヘルパー。
/// </summary>
public static class SolutionPathLocator
{
    private const string SolutionFileName = "MercariMacroPriceTool.sln";

    public static string FindSolutionRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current != null)
        {
            var candidate = Path.Combine(current.FullName, SolutionFileName);
            if (File.Exists(candidate))
            {
                return current.FullName;
            }
            current = current.Parent;
        }

        // フォールバックとして実行ディレクトリを返す
        return AppContext.BaseDirectory;
    }
}
