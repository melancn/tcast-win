using System.IO;
using System.Net;
using TCast.Desktop.Services;
using TCast.Desktop.ViewModels;
using TCast.Infrastructure.Connection;
using TCast.Infrastructure.Diagnostics;
using TCast.Protocol.Commands;
using TCast.Protocol.Responses;
using Xunit;

namespace TCast.Desktop.Tests;

public sealed class RemoteAndDiagnosticsTests
{
    [Fact]
    public async Task LongPressRepeatsOnlyRepeatableKeys()
    {
        await using var connection = new FakeConnection();
        await using var coordinator = new ConnectionCoordinator(connection);
        var remote = new RemoteViewModel(coordinator, new InlineDispatcher(), new Dialogs());

        // 未连接时不能开始长按。
        Assert.False(remote.StartRepeat(TcastKey.Up));
        await coordinator.ConnectAsync(IPAddress.Loopback);

        // 只有方向与音量可重复；确认、电源等每次都是显式单击。
        Assert.False(remote.StartRepeat(TcastKey.Ok));
        Assert.False(remote.StartRepeat(TcastKey.Power));
        Assert.False(remote.StartRepeat(TcastKey.SubMenu));
        Assert.True(RemoteViewModel.IsRepeatable(TcastKey.VolumeUp));
        Assert.True(RemoteViewModel.IsRepeatable(TcastKey.Left));

        Assert.True(remote.StartRepeat(TcastKey.Up));
        Assert.Equal(TcastKey.Up, remote.RepeatKey);
        // 已在重复中时，再次按下不叠加第二个重复循环。
        Assert.False(remote.StartRepeat(TcastKey.Down));
        Assert.Equal(TcastKey.Up, remote.RepeatKey);

        remote.StopRepeat();
        Assert.Null(remote.RepeatKey);
        Assert.True(remote.StartRepeat(TcastKey.VolumeDown));
        remote.StopRepeat();
    }

    [Fact]
    public async Task LongPressStopsOnDisconnect()
    {
        await using var connection = new FakeConnection();
        await using var coordinator = new ConnectionCoordinator(connection);
        var remote = new RemoteViewModel(coordinator, new InlineDispatcher(), new Dialogs());
        await coordinator.ConnectAsync(IPAddress.Loopback);
        Assert.True(remote.StartRepeat(TcastKey.Right));

        await coordinator.DisconnectAsync();
        Assert.Null(remote.RepeatKey);
        Assert.False(remote.StartRepeat(TcastKey.Right));
    }

    [Fact]
    public async Task UnresponsiveKeepsControlsEnabledBecauseTheLinkIsStillOpen()
    {
        await using var connection = new FakeConnection();
        await using var coordinator = new ConnectionCoordinator(connection);
        var remote = new RemoteViewModel(coordinator, new InlineDispatcher(), new Dialogs());
        await coordinator.ConnectAsync(IPAddress.Loopback);
        connection.MarkUnresponsive();
        Assert.True(RemoteViewModel.IsSendable(ConnectionState.Unresponsive));
        Assert.True(remote.SendKeyCommand.CanExecute(TcastKey.Ok));
        await remote.SendKeyCommand.ExecuteAsync(TcastKey.Ok);
        Assert.Equal("149>>15", Assert.Single(connection.Sent));
    }

    [Fact]
    public async Task DiagnosticsPageAppendsFiltersAndExportsRedactedEntries()
    {
        var service = new DiagnosticsService(TimeProvider.System, capacity: 10);
        service.Write(DiagnosticLevel.Info, "Connection", "← 159>>… (7 字段)", session: 3);

        var dialogs = new Dialogs();
        var vm = new DiagnosticsViewModel(service, new InlineDispatcher(), dialogs);
        Assert.Single(vm.Entries);
        Assert.False(vm.IsEmpty);

        // 后台线程写入的事件切回 UI 线程后追加，正文已在写入前脱敏。
        service.Write(DiagnosticLevel.Warning, "Apk", "任务 http://10.0.0.2:9999/apk/0123456789abcdef token=leak",
            taskKey: "key-1");
        Assert.Equal(2, vm.Entries.Count);
        Assert.DoesNotContain(vm.Entries, row => row.Message.Contains("leak", StringComparison.Ordinal));

        vm.Filter = "connection";
        vm.ApplyFilterCommand.Execute(null);
        Assert.Single(vm.Entries);
        Assert.Equal("session 3", vm.Entries[0].Scope);

        vm.Filter = string.Empty;
        vm.ApplyFilterCommand.Execute(null);
        Assert.Equal(2, vm.Entries.Count);

        string path = Path.Combine(Path.GetTempPath(), $"tcast-export-{Guid.NewGuid():N}.txt");
        dialogs.Export = new FileExportRequest(path, CancellationToken.None);
        try
        {
            await vm.ExportCommand.ExecuteAsync(null);
            Assert.True(File.Exists(path));
            string content = File.ReadAllText(path);
            Assert.Contains("session=3", content);
            Assert.Contains("key=key-1", content);
            Assert.DoesNotContain("leak", content);
            Assert.Contains("已导出", vm.StatusText);
        }
        finally
        {
            File.Delete(path);
        }

        vm.ClearViewCommand.Execute(null);
        Assert.True(vm.IsEmpty);
        Assert.Equal("0 / 500", vm.CountText);
    }

    private sealed class InlineDispatcher : IUiDispatcher
    {
        public void Post(Action action) => action();
    }

    private sealed class Dialogs : IUiDialogs
    {
        public FileExportRequest? Export { get; set; }
        public string? PickApkFile() => null;
        public FileExportRequest? PickDiagnosticsExportFile() => Export;
        public bool Confirm(string title, string message) => true;
        public void ShowError(string title, string message) { }
    }

    private sealed class FakeConnection : ITcastConnection
    {
        public List<string> Sent { get; } = [];
        public event EventHandler<ConnectionState>? StateChanged;
        public event EventHandler<(ConnectionSession Session, TcastReply Reply)>? ReplyReceived { add { } remove { } }
        public event EventHandler<Exception>? Faulted { add { } remove { } }
        public ConnectionState State { get; private set; }
        public ConnectionSession? Session { get; private set; }
        public ConnectedInfo? Info => null;

        public Task ConnectAsync(IPAddress address, CancellationToken cancellationToken = default)
        {
            Session = new((Session?.Id ?? 0) + 1);
            State = ConnectionState.Connected;
            StateChanged?.Invoke(this, State);
            return Task.CompletedTask;
        }

        public void MarkUnresponsive()
        {
            State = ConnectionState.Unresponsive;
            StateChanged?.Invoke(this, State);
        }

        public IReplyWaiter Expect(Func<TcastReply, bool> predicate) => throw new NotSupportedException();

        public Task SendAsync(string body, CancellationToken cancellationToken = default)
        {
            Sent.Add(body);
            return Task.CompletedTask;
        }

        public Task SendKeyAsync(TcastKey key, CancellationToken cancellationToken = default) =>
            SendAsync(TcastCommands.Key(key), cancellationToken);

        public Task DisconnectAsync()
        {
            State = ConnectionState.Disconnected;
            Session = null;
            StateChanged?.Invoke(this, State);
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
