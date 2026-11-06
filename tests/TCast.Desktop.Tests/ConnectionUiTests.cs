using System.Net;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using TCast.Desktop.Services;
using TCast.Desktop.ViewModels;
using TCast.Desktop.Views;
using TCast.Infrastructure.Connection;
using TCast.Infrastructure.Discovery;
using TCast.Protocol.Commands;
using TCast.Protocol.Responses;
using Xunit;

namespace TCast.Desktop.Tests;

public sealed class ConnectionUiTests
{
    [Fact]
    public async Task RemoteCommandsRequireHandshakeAndDisableAfterDisconnect()
    {
        await using var connection = new FakeConnection();
        await using var coordinator = new ConnectionCoordinator(connection);
        var remote = new RemoteViewModel(coordinator, new InlineDispatcher(), new Dialogs());
        Assert.False(remote.SendKeyCommand.CanExecute(TcastKey.Ok));
        Assert.False(remote.SendTextCommand.CanExecute(null));
        await coordinator.ConnectAsync(IPAddress.Loopback);
        Assert.True(remote.SendKeyCommand.CanExecute(TcastKey.Ok));
        await remote.SendKeyCommand.ExecuteAsync(TcastKey.Ok);
        Assert.Equal("149>>15", Assert.Single(connection.Sent));
        await coordinator.DisconnectAsync();
        Assert.False(remote.SendKeyCommand.CanExecute(TcastKey.Ok));
        Assert.False(remote.SendTextCommand.CanExecute(null));
    }

    [Fact]
    public async Task PowerRequiresConfirmationAndSameSessionAfterDialog()
    {
        await using var connection = new FakeConnection();
        await using var coordinator = new ConnectionCoordinator(connection);
        var dialogs = new Dialogs();
        var remote = new RemoteViewModel(coordinator, new InlineDispatcher(), dialogs);
        await coordinator.ConnectAsync(IPAddress.Loopback);
        await remote.SendKeyCommand.ExecuteAsync(TcastKey.Power);
        Assert.Empty(connection.Sent);
        dialogs.Accept = true;
        dialogs.OnConfirm = () => connection.ChangeSession();
        await remote.SendKeyCommand.ExecuteAsync(TcastKey.Power);
        Assert.Empty(connection.Sent);
        dialogs.OnConfirm = null;
        await remote.SendKeyCommand.ExecuteAsync(TcastKey.Power);
        Assert.Equal("149>>20", Assert.Single(connection.Sent));
    }

    [Fact]
    public async Task TextValidationDoesNotClearInputOrRecordSuccessOnFailure()
    {
        await using var connection = new FakeConnection();
        await using var coordinator = new ConnectionCoordinator(connection);
        var remote = new RemoteViewModel(coordinator, new InlineDispatcher(), new Dialogs());
        await coordinator.ConnectAsync(IPAddress.Loopback);
        remote.TextToSend = "bad>>text";
        await remote.SendTextCommand.ExecuteAsync(null);
        Assert.Empty(connection.Sent);
        Assert.Empty(remote.RecentCommands);
        Assert.Equal("bad>>text", remote.TextToSend);
        Assert.NotNull(remote.ErrorText);
        connection.SendError = new System.IO.IOException("closed");
        remote.TextToSend = "hello";
        await remote.SendTextCommand.ExecuteAsync(null);
        Assert.Empty(remote.RecentCommands);
        Assert.Equal("hello", remote.TextToSend);
    }

    [Fact]
    public async Task LocalWriteMustCompleteBeforeSuccessAndNewInputIsPreserved()
    {
        await using var connection = new FakeConnection { WriteGate = new(TaskCreationOptions.RunContinuationsAsynchronously) };
        await using var coordinator = new ConnectionCoordinator(connection);
        var remote = new RemoteViewModel(coordinator, new InlineDispatcher(), new Dialogs());
        await coordinator.ConnectAsync(IPAddress.Loopback);
        remote.TextToSend = "first";
        Task sending = remote.SendTextCommand.ExecuteAsync(null);
        Assert.Empty(remote.RecentCommands);
        remote.TextToSend = "second";
        connection.WriteGate.SetResult();
        await sending;
        Assert.Single(remote.RecentCommands);
        Assert.Equal("second", remote.TextToSend);
    }

    [Fact]
    public async Task DiscoveryDeduplicatesAndStaleDoesNotDisconnectControl()
    {
        await using var connection = new FakeConnection();
        await using var coordinator = new ConnectionCoordinator(connection);
        var discovery = new FakeDiscovery();
        var vm = new DiscoveryViewModel(discovery, coordinator, new InlineDispatcher(), new Dialogs());
        var device = new DiscoveredDevice(IPAddress.Loopback, "TV", "", "0", DiscoveryState.Available,
            DateTimeOffset.Now, DateTimeOffset.Now);
        discovery.Publish(device);
        vm.SelectedDevice = device;
        discovery.Publish(device with { Name = "updated" });
        Assert.Single(vm.Devices);
        Assert.Equal("updated", vm.SelectedDevice?.Name);
        await coordinator.ConnectAsync(IPAddress.Loopback);
        discovery.Publish(device with { State = DiscoveryState.Stale });
        Assert.Equal(ConnectionState.Connected, coordinator.State);
        Assert.False(vm.ConnectManualCommand.CanExecute(null));
        vm.ClearOfflineCommand.Execute(null);
        Assert.Empty(vm.Devices);
        Assert.Null(vm.SelectedDevice);
    }

    [Fact]
    public async Task AdapterSwitchRequiresConfirmationAndClearsSelection()
    {
        await using var connection = new FakeConnection();
        await using var coordinator = new ConnectionCoordinator(connection);
        var discovery = new FakeDiscovery();
        var dialogs = new Dialogs();
        var vm = new DiscoveryViewModel(discovery, coordinator, new InlineDispatcher(), dialogs);
        var first = new NetworkAdapter("first", "first", IPAddress.Parse("192.168.1.10"), IPAddress.Parse("255.255.255.0"));
        var second = first with { InterfaceId = "second", Address = IPAddress.Parse("192.168.2.10") };
        vm.SelectedAdapter = first;
        await vm.RescanCommand.ExecuteAsync(null);
        vm.SelectedAdapter = second;
        await vm.RescanCommand.ExecuteAsync(null);
        Assert.Equal(first, vm.SelectedAdapter);
        Assert.Equal(0, discovery.Stops);
        dialogs.Accept = true;
        vm.SelectedAdapter = second;
        await vm.RescanCommand.ExecuteAsync(null);
        Assert.Equal(1, discovery.Stops);
        Assert.Equal(second, discovery.Adapter);
    }

    [Fact]
    public async Task QueuedConnectionEventsCannotReenableCommandsAfterDisconnect()
    {
        await using var connection = new FakeConnection();
        await using var coordinator = new ConnectionCoordinator(connection);
        var dispatcher = new QueuedDispatcher();
        var remote = new RemoteViewModel(coordinator, dispatcher, new Dialogs());
        await coordinator.ConnectAsync(IPAddress.Loopback);
        await coordinator.DisconnectAsync();
        dispatcher.Flush();
        Assert.False(remote.IsReady);
        Assert.False(remote.SendTextCommand.CanExecute(null));
    }

    [Fact]
    public async Task QueuedOldAdapterDiscoveryDoesNotRepopulateNewList()
    {
        await using var connection = new FakeConnection();
        await using var coordinator = new ConnectionCoordinator(connection);
        var dispatcher = new QueuedDispatcher();
        var discovery = new FakeDiscovery();
        var vm = new DiscoveryViewModel(discovery, coordinator, dispatcher, new Dialogs { Accept = true });
        var first = new NetworkAdapter("first", "first", IPAddress.Parse("192.168.1.10"), IPAddress.Parse("255.255.255.0"));
        vm.SelectedAdapter = first;
        await vm.RescanCommand.ExecuteAsync(null);
        discovery.Publish(new(IPAddress.Parse("192.168.1.20"), "old", "", "", DiscoveryState.Available,
            DateTimeOffset.Now, DateTimeOffset.Now));
        vm.SelectedAdapter = first with { InterfaceId = "second", Address = IPAddress.Parse("192.168.2.10") };
        await vm.RescanCommand.ExecuteAsync(null);
        dispatcher.Flush();
        Assert.Empty(vm.Devices);
    }

    [Fact]
    public void ShellAndPagesLoadWithRealResourceStylesAndViewModelTemplates()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                var app = new Application();
                app.Resources.MergedDictionaries.Add(new ResourceDictionary
                {
                    Source = new Uri("/TCast.Desktop;component/Themes/Palette.xaml", UriKind.Relative),
                });
                app.Resources.MergedDictionaries.Add(new ResourceDictionary
                {
                    Source = new Uri("/TCast.Desktop;component/Themes/Controls.xaml", UriKind.Relative),
                });
                var connection = new FakeConnection();
                var coordinator = new ConnectionCoordinator(connection);
                var dispatcher = new InlineDispatcher();
                var dialogs = new Dialogs();
                var discovery = new DiscoveryViewModel(new FakeDiscovery(), coordinator, dispatcher, dialogs);
                var remote = new RemoteViewModel(coordinator, dispatcher, dialogs);
                var diagnostics = new DiagnosticsViewModel(
                    new TCast.Infrastructure.Diagnostics.DiagnosticsService(TimeProvider.System), dispatcher, dialogs);
                var shell = new ShellViewModel(dispatcher, coordinator, discovery, remote, CreateApk(dialogs, dispatcher, coordinator, discovery), diagnostics);
                Assert.True(coordinator.ConnectAsync(IPAddress.Loopback).IsCompletedSuccessfully);
                discovery.SelectedDevice = new DiscoveredDevice(IPAddress.Parse("127.0.0.2"), "other", "", "",
                    DiscoveryState.Available, DateTimeOffset.Now, DateTimeOffset.Now);
                Assert.Contains("127.0.0.1", shell.ConnectionText);
                shell.SendRemoteKey(TcastKey.Ok);
                Assert.Empty(connection.Sent);
                shell.NavigateCommand.Execute("remote");
                shell.SendRemoteKey(TcastKey.Ok);
                Assert.Equal("149>>15", Assert.Single(connection.Sent));
                Assert.True(coordinator.DisconnectAsync().IsCompletedSuccessfully);
                shell.SendRemoteKey(TcastKey.Ok);
                Assert.Single(connection.Sent);
                var window = new ShellWindow(shell);
                Assert.NotNull(window.Resources[new DataTemplateKey(typeof(DiscoveryViewModel))]);
                Assert.NotNull(window.Resources[new DataTemplateKey(typeof(RemoteViewModel))]);
                Assert.NotNull(window.Resources[new DataTemplateKey(typeof(ApkViewModel))]);
                discovery.SelectedDevice = null;
                discovery.StatusText = "请选择局域网网卡";
                remote.ErrorText = null;
                remote.RecentCommands.Clear();
                var root = (Grid)window.Content;
                Assert.Equal(4, shell.NavigationItems.Count);
                Assert.Equal("diagnostics", shell.NavigationItems[^1].Key);
                foreach (string page in new[] { "discovery", "remote", "apk", "diagnostics" })
                {
                    shell.NavigateCommand.Execute(page);
                    Assert.Equal(page, Assert.Single(shell.NavigationItems, item => item.IsSelected).Key);
                    root.Measure(new Size(1440, 960));
                    root.Arrange(new Rect(0, 0, 1440, 960));
                    root.UpdateLayout();
                    var sidebar = (ContentControl)window.FindName("DesktopNavigation");
                    var workspace = (Grid)window.FindName("Workspace");
                    Assert.Equal(sidebar.TranslatePoint(new Point(), root).Y, workspace.TranslatePoint(new Point(), root).Y);
                    Assert.Equal(sidebar.ActualHeight, workspace.ActualHeight);
                    string? output = Environment.GetEnvironmentVariable("TCAST_UI_CAPTURE_DIR");
                    if (!string.IsNullOrEmpty(output) && System.IO.Directory.Exists(output))
                    {
                        var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap(1440, 960, 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
                        bitmap.Render(root);
                        var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
                        encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
                        using var file = System.IO.File.Create(System.IO.Path.Combine(output, $"tcast-{page}.png"));
                        encoder.Save(file);
                    }
                }
                foreach (UserControl view in new UserControl[] { new DiscoveryView { DataContext = discovery }, new RemoteView { DataContext = remote }, new ApkView { DataContext = CreateApk(dialogs, dispatcher, coordinator, discovery) }, new DiagnosticsView { DataContext = diagnostics } })
                {
                    foreach (double width in new[] { 536d, 704d, 1000d, 1168d })
                    {
                        view.Measure(new Size(width, double.PositiveInfinity));
                        view.Arrange(new Rect(0, 0, width, view.DesiredSize.Height));
                        view.UpdateLayout();
                        Assert.True(view.DesiredSize.Width <= width);
                    }
                }
                window.Close();
                app.Shutdown();
            }
            catch (Exception ex)
            {
                failure = ex;
            }
            finally
            {
                Dispatcher.CurrentDispatcher.InvokeShutdown();
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(15)));
        Assert.Null(failure);
    }

    private static ApkViewModel CreateApk(IUiDialogs dialogs, IUiDispatcher dispatcher,
        IConnectionCoordinator connection, DiscoveryViewModel discovery)
    {
        var validator = new TCast.Infrastructure.Apk.ApkContainerValidator();
        return new ApkViewModel(dialogs, dispatcher, connection, discovery,
            new TCast.Infrastructure.Apk.ApkSnapshotFactory(),
            new TCast.Infrastructure.Apk.AxmlApkMetadataReader(validator),
            new IdleTransfer());
    }

    private sealed class IdleTransfer : TCast.Infrastructure.Apk.IApkTransferService
    {
        public event EventHandler<TCast.Infrastructure.Apk.ApkTransferProgress>? ProgressChanged { add { } remove { } }
        public bool IsBusy => false;
        public bool HasLease => false;
        public TCast.Infrastructure.Apk.ApkTransferProgress? Current => null;
        public Uri? DownloadUrl => null;
        public Task StartAsync(TCast.Protocol.Models.ApkMetadata metadata, TCast.Infrastructure.Apk.ApkSnapshot snapshot,
            IPAddress localAddress, IPAddress targetAddress, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task StopWaitingAsync() => Task.CompletedTask;
        public Task StopServingAsync() => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class QueuedDispatcher : IUiDispatcher
    {
        private readonly Queue<Action> _actions = new();
        public void Post(Action action) => _actions.Enqueue(action);
        public void Flush()
        {
            while (_actions.TryDequeue(out Action? action))
            {
                action();
            }
        }
    }

    private sealed class InlineDispatcher : IUiDispatcher
    {
        public void Post(Action action) => action();
    }

    private sealed class Dialogs : IUiDialogs
    {
        public bool Accept;
        public Action? OnConfirm;
        public string? PickApkFile() => null;
        public FileExportRequest? PickDiagnosticsExportFile() => null;
        public bool Confirm(string title, string message)
        {
            OnConfirm?.Invoke();
            return Accept;
        }
        public void ShowError(string title, string message) { }
    }

    private sealed class FakeConnection : ITcastConnection
    {
        public List<string> Sent { get; } = [];
        public Exception? SendError;
        public TaskCompletionSource? WriteGate;
        public event EventHandler<ConnectionState>? StateChanged;
        public event EventHandler<(ConnectionSession Session, TcastReply Reply)>? ReplyReceived { add { } remove { } }
        public event EventHandler<Exception>? Faulted { add { } remove { } }
        public ConnectionState State { get; private set; }
        public ConnectionSession? Session { get; private set; }
        public ConnectedInfo? Info => null;
        public void ChangeSession() => Session = new((Session?.Id ?? 0) + 1);
        public Task ConnectAsync(IPAddress address, CancellationToken cancellationToken = default)
        {
            ChangeSession();
            State = ConnectionState.Connected;
            StateChanged?.Invoke(this, State);
            return Task.CompletedTask;
        }
        public IReplyWaiter Expect(Func<TcastReply, bool> predicate) => throw new NotSupportedException();
        public async Task SendAsync(string body, CancellationToken cancellationToken = default)
        {
            if (SendError is not null) { throw SendError; }
            if (WriteGate is not null) { await WriteGate.Task; }
            Sent.Add(body);
        }
        public Task SendKeyAsync(TcastKey key, CancellationToken cancellationToken = default) => SendAsync(TcastCommands.Key(key), cancellationToken);
        public Task DisconnectAsync()
        {
            State = ConnectionState.Disconnected;
            Session = null;
            StateChanged?.Invoke(this, State);
            return Task.CompletedTask;
        }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class FakeDiscovery : IDiscoveryService
    {
        public int Stops;
        public NetworkAdapter? Adapter;
        public event EventHandler<DiscoveryState>? StateChanged { add { } remove { } }
        public event EventHandler<DiscoveredDevice>? DeviceChanged;
        public event EventHandler<DiscoveredDevice>? DeviceLost { add { } remove { } }
        public event EventHandler<string>? UnrecognizedPacket { add { } remove { } }
        public DiscoveryState State { get; private set; }
        public void Publish(DiscoveredDevice device) => DeviceChanged?.Invoke(this, device);
        public Task StartAsync(NetworkAdapter adapter, CancellationToken cancellationToken = default)
        {
            Adapter = adapter;
            State = DiscoveryState.Scanning;
            return Task.CompletedTask;
        }
        public Task ScanAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task StopAsync()
        {
            Stops++;
            State = DiscoveryState.Stopped;
            return Task.CompletedTask;
        }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
