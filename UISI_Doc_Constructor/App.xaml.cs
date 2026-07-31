using System.Windows;
using UISI_Doc_Constructor.Services;

namespace UISI_Doc_Constructor;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        OfficeInteropAssemblyResolver.Initialize();
        base.OnStartup(e);

        var mainWindow = new MainWindow();
        MainWindow = mainWindow;
        mainWindow.Show();
    }
}
