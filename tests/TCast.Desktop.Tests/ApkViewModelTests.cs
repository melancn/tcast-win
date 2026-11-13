using System.IO;
using System.Net;
using Microsoft.Extensions.DependencyInjection;
using TCast.Infrastructure.DependencyInjection;
using TCast.Desktop.Services;
using TCast.Desktop.ViewModels;
using TCast.Infrastructure.Apk;
using TCast.Infrastructure.Connection;
using TCast.Infrastructure.Discovery;
using TCast.Protocol.Commands;
using TCast.Protocol.Models;
using TCast.Protocol.Responses;
using Xunit;

namespace TCast.Desktop.Tests;

public sealed class ApkViewModelTests
{
    [Fact]
    public async Task ProductionDependencyGraphResolvesAndDisposesWithoutStartingServices()
    {
        var services = new ServiceCollection();
        services.AddTcastInfrastructure();
        services.AddSingleton<IUiDispatcher, Dispatcher>();
        services.AddSingleton<IUiDialogs, Dialogs>();
        services.AddSingleton<DiscoveryViewModel>();
        services.AddSingleton<ApkViewModel>();
        await using var provider = services.BuildServiceProvider();
        ApkViewModel viewModel = provider.GetRequiredService<ApkViewModel>();
        Assert.False(viewModel.CanSend);
        Assert.False(viewModel.TransferActive);
    }

    [Fact]
    public async Task LateSelectionCannotReplaceNewMetadataAndFailureDisablesSend()
    {
        await using var fixture = new Fixture();
        Task first = fixture.ViewModel.SelectAsync("first.apk");
        await fixture.Reader.StartedAsync("first.apk");
        Task second = fixture.ViewModel.SelectAsync("second.apk");
        await fixture.Reader.CompleteAsync("second.apk", "org.second");
        await second;
        await fixture.Reader.CompleteAsync("first.apk", "org.first");
        await first;
        Assert.Equal("org.second", fixture.ViewModel.Metadata?.PackageName);
        Assert.True(fixture.ViewModel.CanSend);
        Task failed = fixture.ViewModel.SelectAsync("failed.apk");
        Assert.False(fixture.ViewModel.CanSend);
        Assert.Null(fixture.ViewModel.Metadata);
        await fixture.Reader.FailAsync("failed.apk");
        await failed;
        Assert.False(fixture.ViewModel.CanSend);
        Assert.NotNull(fixture.ViewModel.ParseError);
    }

    [Fact]
    public async Task TransferAndLeaseGateFilesAdaptersAndTargetChanges()
    {
        await using var fixture = new Fixture();
        Task select = fixture.ViewModel.SelectAsync("test.apk");
        await fixture.Reader.CompleteAsync("test.apk", "org.test");
        await select;
        await fixture.ViewModel.SendCommand.ExecuteAsync(null);
        Assert.False(fixture.ViewModel.TransferActive);
        Assert.True(fixture.ViewModel.HasLease);
        Assert.True(fixture.ViewModel.CanSend);
        Assert.True(fixture.ViewModel.PickFileCommand.CanExecute(null));
        Task reselect = fixture.ViewModel.SelectAsync("ignored.apk");
        await fixture.Reader.CompleteAsync("ignored.apk", "org.ignored");
        await reselect;
        Assert.Equal("org.ignored", fixture.ViewModel.Metadata?.PackageName);
        NetworkAdapter? original = fixture.Discovery.SelectedAdapter;
        fixture.Discovery.SelectedAdapter = original! with { Address = IPAddress.Parse("127.0.0.2") };
        Assert.Equal(original, fixture.Discovery.SelectedAdapter);
        Assert.True(fixture.ViewModel.HasLease);
        await fixture.Connection.DisconnectAsync();
        Assert.False(fixture.Discovery.CanConnect);
        Assert.False(fixture.ViewModel.CanSend);
        fixture.Transfer.Publish(ApkTransferState.Succeeded);
        Assert.False(fixture.ViewModel.TransferActive);
        Assert.Equal("安装成功", Assert.Single(fixture.ViewModel.RecentTasks).Result);
    }

    [Fact]
    public async Task StopServingNeedsConfirmationAndEmptyVersionsUsePlaceholders()
    {
        await using var fixture = new Fixture();
        Task select = fixture.ViewModel.SelectAsync("test.apk");
        await fixture.Reader.CompleteAsync("test.apk", "org.test");
        await select;
        Assert.Equal("—", fixture.ViewModel.VersionText);
        Assert.Equal("—", fixture.ViewModel.VersionCodeText);
        await fixture.ViewModel.SendCommand.ExecuteAsync(null);
        await fixture.ViewModel.StopServingCommand.ExecuteAsync(null);
        Assert.True(fixture.Transfer.HasLease);
        fixture.Dialogs.Accept = true;
        await fixture.ViewModel.StopServingCommand.ExecuteAsync(null);
        Assert.False(fixture.Transfer.HasLease);
    }

    private sealed class Fixture : IAsyncDisposable
    {
        public FakeConnection Connection { get; } = new();
        public Reader Reader { get; } = new();
        public Transfer Transfer { get; } = new();
        public Dialogs Dialogs { get; } = new();
        public DiscoveryViewModel Discovery { get; }
        public ApkViewModel ViewModel { get; }
        public Fixture()
        {
            var coordinator = new ConnectionCoordinator(Connection);
            var dispatcher = new Dispatcher();
            Discovery = new DiscoveryViewModel(new Discovery(), coordinator, dispatcher, Dialogs, Transfer);
            Discovery.SelectedAdapter = new NetworkAdapter("test", "test", IPAddress.Loopback, IPAddress.Parse("255.0.0.0"));
            coordinator.ConnectAsync(IPAddress.Loopback).GetAwaiter().GetResult();
            ViewModel = new ApkViewModel(Dialogs, dispatcher, coordinator, Discovery, new Snapshots(), Reader, Transfer);
        }
        public ValueTask DisposeAsync() => ViewModel.DisposeAsync();
    }

    private sealed class Snapshots : IApkSnapshotFactory
    {
        public Task<ApkSnapshot> CreateAsync(string originalPath, CancellationToken cancellationToken = default) =>
            Task.FromResult(new ApkSnapshot(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), originalPath), originalPath, 1));
    }

    private sealed class Reader : IApkMetadataReader
    {
        private readonly System.Collections.Concurrent.ConcurrentDictionary<string, TaskCompletionSource<TaskCompletionSource<ApkMetadata>>> _requests = new();
        private TaskCompletionSource<TaskCompletionSource<ApkMetadata>> Request(string file) => _requests.GetOrAdd(file,
            _ => new(TaskCreationOptions.RunContinuationsAsynchronously));
        public Task<ApkMetadata> ReadAsync(string path, CancellationToken cancellationToken = default)
        {
            var completion = new TaskCompletionSource<ApkMetadata>(TaskCreationOptions.RunContinuationsAsynchronously);
            Request(Path.GetFileName(path)).SetResult(completion);
            return completion.Task;
        }
        public async Task StartedAsync(string file) => await Request(file).Task.WaitAsync(TimeSpan.FromSeconds(5));
        public async Task CompleteAsync(string file, string package) =>
            (await Request(file).Task.WaitAsync(TimeSpan.FromSeconds(5))).SetResult(new(package, package, "", "", true));
        public async Task FailAsync(string file) =>
            (await Request(file).Task.WaitAsync(TimeSpan.FromSeconds(5))).SetException(new InvalidDataException());
    }

    private sealed class Transfer : IApkTransferService
    {
        public event EventHandler<ApkTransferProgress>? ProgressChanged;
        public bool IsBusy { get; private set; }
        public bool HasLease { get; private set; }
        public ApkTransferProgress? Current { get; private set; }
        public Uri? DownloadUrl => IsBusy ? new Uri("http://127.0.0.1:1234/apk/test") : null;
        public Task StartAsync(ApkMetadata metadata, ApkSnapshot snapshot, IPAddress localAddress, IPAddress targetAddress, CancellationToken cancellationToken = default)
        {
            Current = new("test", metadata, ApkTransferState.AwaitingDownload, null, null, targetAddress);
            IsBusy = false;
            HasLease = true;
            ProgressChanged?.Invoke(this, Current);
            return Task.CompletedTask;
        }
        public void Publish(ApkTransferState state)
        {
            Current = Current! with { State = state };
            if (Current.IsTerminal) { IsBusy = false; }
            ProgressChanged?.Invoke(this, Current);
        }

        public Task StopServingAsync()
        {
            IsBusy = false;
            HasLease = false;
            if (Current is not null) { Publish(ApkTransferState.Sent); }
            return Task.CompletedTask;
        }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class Dialogs : IUiDialogs
    {
        public bool Accept;
        public string? PickApkFile() => null;
        public FileExportRequest? PickDiagnosticsExportFile() => null;
        public bool Confirm(string title, string message) => Accept;
        public void ShowError(string title, string message) { }
    }
    private sealed class Dispatcher : IUiDispatcher
    {
        public void Post(Action action) => action();
    }
    private sealed class FakeConnection : ITcastConnection
    {
        public event EventHandler<ConnectionState>? StateChanged;
        public event EventHandler<(ConnectionSession Session, TcastReply Reply)>? ReplyReceived { add { } remove { } }
        public event EventHandler<Exception>? Faulted { add { } remove { } }
        public ConnectionState State { get; private set; }
        public ConnectionSession? Session => new(1);
        public ConnectedInfo? Info => null;
        public Task ConnectAsync(IPAddress address, CancellationToken cancellationToken = default)
        {
            State = ConnectionState.Connected;
            StateChanged?.Invoke(this, State);
            return Task.CompletedTask;
        }
        public Task DisconnectAsync()
        {
            State = ConnectionState.Disconnected;
            StateChanged?.Invoke(this, State);
            return Task.CompletedTask;
        }
        public IReplyWaiter Expect(Func<TcastReply, bool> predicate) => throw new NotSupportedException();
        public Task SendAsync(string body, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task SendKeyAsync(TcastKey key, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
    private sealed class Discovery : IDiscoveryService
    {
        public event EventHandler<DiscoveryState>? StateChanged { add { } remove { } }
        public event EventHandler<DiscoveredDevice>? DeviceChanged { add { } remove { } }
        public event EventHandler<DiscoveredDevice>? DeviceLost { add { } remove { } }
        public event EventHandler<string>? UnrecognizedPacket { add { } remove { } }
        public DiscoveryState State => DiscoveryState.Stopped;
        public Task StartAsync(NetworkAdapter adapter, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task ScanAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task StopAsync() => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
