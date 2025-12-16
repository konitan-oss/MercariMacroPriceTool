using System.Configuration;
using System.Data;
using System.Windows;

namespace MercariMacroPriceTool.App;

/// <summary>
/// Interaction logic for App.xaml
/// </summary>
public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        var progress = new Progress<string>(_ => { });
        if (!PlaywrightBootstrap.EnsureBrowsersPath(progress))
        {
            Shutdown();
            return;
        }

        base.OnStartup(e);
    }
}
