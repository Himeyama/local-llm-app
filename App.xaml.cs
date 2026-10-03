using Microsoft.UI.Xaml;

namespace LocalLlm.Gui;

public partial class App : Application
{
    private Window? window;
    public App()
    {
        UnhandledException += (_, e) => Trace(e.Exception.ToString());
        InitializeComponent();
    }
    internal static void Trace(string message)
    {
        try { File.AppendAllText(Path.Combine(AppContext.BaseDirectory, "startup.log"), $"{DateTime.Now:O} {message}{Environment.NewLine}"); } catch { }
    }
    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        window = new MainWindow();
        window.Activate();
        window.AppWindow.Show();
    }
}
