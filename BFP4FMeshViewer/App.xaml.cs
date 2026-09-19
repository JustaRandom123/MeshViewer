using System.IO;
using System.Windows;

namespace BFP4FMeshViewer
{
    public partial class App : Application
    {
        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);

            var window = new MainWindow();
            MainWindow = window;
            ShutdownMode = ShutdownMode.OnMainWindowClose;
            window.Show();

            // Ordner optional als Kommandozeilenargument
            if (e.Args.Length > 0 && Directory.Exists(e.Args[0]))
                window.LoadFolder(e.Args[0]);
        }
    }
}
