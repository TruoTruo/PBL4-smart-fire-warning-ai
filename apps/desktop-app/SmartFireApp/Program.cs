using SmartFireApp.Forms;

namespace SmartFireApp;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        ApplicationConfiguration.Initialize();
        Application.Run(new DashboardForm());
    }
}
