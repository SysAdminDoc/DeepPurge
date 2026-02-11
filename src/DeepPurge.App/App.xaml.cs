namespace DeepPurge.App;

public partial class App : System.Windows.Application
{
    protected override void OnStartup(System.Windows.StartupEventArgs e)
    {
        base.OnStartup(e);

        var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
        var principal = new System.Security.Principal.WindowsPrincipal(identity);
        if (!principal.IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator))
        {
            System.Windows.MessageBox.Show("DeepPurge requires administrator privileges.\nPlease run as administrator.",
                "DeepPurge v0.6.0", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Warning);
            Shutdown(1);
            return;
        }
    }
}
