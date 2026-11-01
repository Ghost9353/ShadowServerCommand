using System.Windows;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Serilog;
using SSC.Infrastructure;
namespace SSC.Desktop;
public partial class App : Application
{
    private IHost? _host;
    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        try
        {
            _host=Host.CreateDefaultBuilder(e.Args).UseContentRoot(AppContext.BaseDirectory).AddSsc("desktop")
                .ConfigureServices(services => { services.AddSingleton<SSC.Core.IWatchdogStatusSource>(new WatchdogStatusFile(ServicePaths.Dashboard)); services.AddSingleton<MainViewModel>(); services.AddSingleton<MainWindow>(); }).Build();
            await _host.StartAsync();
            MainWindow=_host.Services.GetRequiredService<MainWindow>(); MainWindow.Show();
        }
        catch(Exception ex)
        {
            Log.Error(ex,"Desktop startup failed");
            MessageBox.Show("SSC could not start. Verify Phase 1 simulation settings, installation files and local data permissions.\n"+ex.Message,"SSC startup error",MessageBoxButton.OK,MessageBoxImage.Error);
            Shutdown(1);
        }
    }
    protected override void OnExit(ExitEventArgs e)
    {
        _host?.Dispose(); Log.CloseAndFlush(); base.OnExit(e);
    }
}
