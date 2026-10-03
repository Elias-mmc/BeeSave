using System.Windows;

namespace MinecraftSaves;

public partial class App : System.Windows.Application
{
    protected override void OnStartup(System.Windows.StartupEventArgs e)
    {
        try
        {
            var hasBundledTools = BundleInstaller.InstallBundledToolsIfPresent();
            if (hasBundledTools &&
                SelfInstallService.StartInstalledCopyIfNeeded(Environment.GetCommandLineArgs().Skip(1).ToArray()))
            {
                Shutdown();
                return;
            }
        }
        catch (Exception exception)
        {
            var details = exception.Message;
            var errorLog = System.IO.Path.Combine(AppPaths.DataDirectory, "startup-error.log");
            try
            {
                System.IO.Directory.CreateDirectory(AppPaths.DataDirectory);
                System.IO.File.WriteAllText(errorLog, exception.ToString());
                details += $"{System.Environment.NewLine}{System.Environment.NewLine}Registro de diagnóstico: {errorLog}";
            }
            catch (Exception loggingException) when (
                loggingException is System.IO.IOException or System.UnauthorizedAccessException)
            {
                details += $"{System.Environment.NewLine}{System.Environment.NewLine}No se pudo escribir el registro: {loggingException.Message}";
            }

            System.Windows.MessageBox.Show(
                details,
                "No se pudieron preparar las herramientas",
                System.Windows.MessageBoxButton.OK,
                System.Windows.MessageBoxImage.Error);
            Shutdown(1);
            return;
        }

        base.OnStartup(e);
    }
}
