using System.ComponentModel;
using System.Windows;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using TCast.Desktop.Services;
using TCast.Desktop.ViewModels;
using TCast.Desktop.Views;
using TCast.Infrastructure.Apk;
using TCast.Infrastructure.DependencyInjection;

namespace TCast.Desktop;

public partial class App : Application
{
    private IHost? _host;
    private bool _closing;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        ShutdownMode = ShutdownMode.OnExplicitShutdown;
        HostApplicationBuilder builder = Host.CreateApplicationBuilder();
        builder.Services.AddTcastInfrastructure();
        builder.Services.AddSingleton<IUiDispatcher, WpfUiDispatcher>();
        builder.Services.AddSingleton<IUiDialogs, WpfUiDialogs>();
        builder.Services.AddSingleton<ShellViewModel>();
        builder.Services.AddSingleton<DiscoveryViewModel>();
        builder.Services.AddSingleton<RemoteViewModel>();
        builder.Services.AddSingleton<ApkViewModel>();
        builder.Services.AddSingleton<DiagnosticsViewModel>();
        builder.Services.AddSingleton<ShellWindow>();
        _host = builder.Build();
        await _host.StartAsync();
        MainWindow = _host.Services.GetRequiredService<ShellWindow>();
        MainWindow.Closing += OnMainWindowClosing;
        MainWindow.Show();
    }

    private async void OnMainWindowClosing(object? sender, CancelEventArgs e)
    {
        e.Cancel = true;
        if (_closing)
        {
            return;
        }

        if (_host?.Services.GetRequiredService<IApkTransferService>() is { } transfer &&
            (transfer.IsBusy || transfer.HasLease) &&
            !_host.Services.GetRequiredService<IUiDialogs>().Confirm("退出 TCast", "仍有安装任务或文件服务租约。退出将停止本地下载服务，可能导致电视下载失败；不会取消电视安装。确认退出？"))
        {
            return;
        }

        _closing = true;
        MainWindow.IsEnabled = false;
        try
        {
            if (_host is not null)
            {
                await _host.StopAsync(TimeSpan.FromSeconds(5));
            }
        }
        finally
        {
            try
            {
                if (_host is IAsyncDisposable disposable)
                {
                    await disposable.DisposeAsync();
                }
                else
                {
                    _host?.Dispose();
                }
            }
            finally
            {
                _host = null;
                MainWindow.Closing -= OnMainWindowClosing;
                Shutdown();
            }
        }
    }
}
