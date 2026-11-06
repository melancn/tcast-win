using System.Net;
using TCast.Infrastructure.Connection;
using TCast.Protocol.Commands;
using TCast.Protocol.Responses;
using Xunit;

namespace TCast.Infrastructure.Tests;

public sealed class ConnectionCoordinatorTests
{
    private sealed class FakeConnection : ITcastConnection
    {
        public int ConnectCount;
        public int DisconnectCount;
        public TaskCompletionSource? ConnectGate;
        public event EventHandler<ConnectionState>? StateChanged;
        public event EventHandler<(ConnectionSession Session, TcastReply Reply)>? ReplyReceived { add { } remove { } }
        public event EventHandler<Exception>? Faulted;
        public ConnectionState State { get; private set; } = ConnectionState.Disconnected;
        public ConnectionSession? Session => State == ConnectionState.Disconnected ? null : new ConnectionSession(ConnectCount);
        public ConnectedInfo? Info => null;

        public async Task ConnectAsync(IPAddress address, CancellationToken cancellationToken = default)
        {
            ConnectCount++;
            State = ConnectionState.Connecting;
            StateChanged?.Invoke(this, State);
            if (ConnectGate is not null)
            {
                await ConnectGate.Task.WaitAsync(cancellationToken);
            }

            State = ConnectionState.Connected;
            StateChanged?.Invoke(this, State);
        }

        public void Fail(Exception error) => Faulted?.Invoke(this, error);
        public IReplyWaiter Expect(Func<TcastReply, bool> predicate) => throw new NotSupportedException();
        public Task SendAsync(string body, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task SendKeyAsync(TcastKey key, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task DisconnectAsync()
        {
            DisconnectCount++;
            State = ConnectionState.Disconnected;
            StateChanged?.Invoke(this, State);
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    [Fact]
    public async Task SwitchingTargetRequiresExplicitDisconnect()
    {
        await using var connection = new FakeConnection();
        await using var coordinator = new ConnectionCoordinator(connection);
        await coordinator.ConnectAsync(IPAddress.Parse("192.168.1.10"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => coordinator.ConnectAsync(IPAddress.Parse("192.168.1.20")));
        Assert.Equal(1, connection.ConnectCount);
        Assert.Equal(0, connection.DisconnectCount);
        Assert.Equal(IPAddress.Parse("192.168.1.10"), coordinator.Target);
        await coordinator.DisconnectAsync();
        await coordinator.ConnectAsync(IPAddress.Parse("192.168.1.20"));
        Assert.Equal(2, connection.ConnectCount);
        Assert.Equal(IPAddress.Parse("192.168.1.20"), coordinator.Target);
    }

    [Fact]
    public async Task DisconnectClearsTarget()
    {
        await using var connection = new FakeConnection();
        await using var coordinator = new ConnectionCoordinator(connection);
        await coordinator.ConnectAsync(IPAddress.Loopback);
        await coordinator.DisconnectAsync();
        Assert.Null(coordinator.Target);
        Assert.Equal(ConnectionState.Disconnected, coordinator.State);
    }

    [Fact]
    public async Task RejectsIpv6Target()
    {
        await using var connection = new FakeConnection();
        await using var coordinator = new ConnectionCoordinator(connection);
        await Assert.ThrowsAsync<ArgumentException>(() => coordinator.ConnectAsync(IPAddress.IPv6Loopback));
        Assert.Equal(0, connection.ConnectCount);
    }

    [Fact]
    public async Task ForwardsStateAndFaults()
    {
        await using var connection = new FakeConnection();
        await using var coordinator = new ConnectionCoordinator(connection);
        ConnectionState? observed = null;
        Exception? fault = null;
        coordinator.StateChanged += (_, state) => observed = state;
        coordinator.Faulted += (_, error) => fault = error;
        await coordinator.ConnectAsync(IPAddress.Loopback);
        var expected = new IOException("test");
        connection.Fail(expected);
        Assert.Equal(ConnectionState.Connected, observed);
        Assert.Same(expected, fault);
    }

    [Fact]
    public async Task ConcurrentConnectDoesNotOverwriteTargetDuringHandshake()
    {
        await using var connection = new FakeConnection { ConnectGate = new(TaskCreationOptions.RunContinuationsAsynchronously) };
        await using var coordinator = new ConnectionCoordinator(connection);
        Task first = coordinator.ConnectAsync(IPAddress.Loopback);
        Task second = coordinator.ConnectAsync(IPAddress.Parse("127.0.0.2"));
        Assert.Equal(IPAddress.Loopback, coordinator.Target);
        connection.ConnectGate.SetResult();
        await first;
        await Assert.ThrowsAsync<InvalidOperationException>(() => second);
        Assert.Equal(1, connection.ConnectCount);
    }
}
