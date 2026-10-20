using System.Windows;

namespace UITestAutomation
{
    // DPI awareness (Per-Monitor V2) is declared in app.manifest.
    // Setting it from code after WPF has started is too late and silently fails.
    public partial class App : Application
    {
    }
}
