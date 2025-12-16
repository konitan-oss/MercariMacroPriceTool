using System;
using System.IO;

namespace MercariMacroPriceTool.Storage;

/// <summary>
/// ソリューションルートと .local ディレクトリを解決するヘルパー。
/// </summary>
public static class StoragePathProvider
{
    private const string SolutionFileName = "MercariMacroPriceTool.sln";

    public static string GetSolutionRoot()
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

        // 見つからない場合は実行ディレクトリを返す
        return AppContext.BaseDirectory;
    }

    public static string EnsureLocalDirectory()
    {
        var root = GetSolutionRoot();
        var local = Path.Combine(root, ".local");
        Directory.CreateDirectory(local);
        return local;
    }

    public static string GetDatabasePath() => Path.Combine(EnsureLocalDirectory(), "app.db");
}
