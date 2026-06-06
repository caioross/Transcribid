using System.Windows;
using System.Windows.Threading;

namespace AudioRecorder;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Never crash silently — show the error and keep the app alive when possible.
        DispatcherUnhandledException += (s, args) =>
        {
            MessageBox.Show(
                "Erro inesperado:\n\n" + args.Exception.Message,
                "Recorder",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            args.Handled = true;
        };
    }
}
