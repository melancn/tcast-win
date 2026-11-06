using System.Net;
using System.Net.Sockets;

namespace TCast.Infrastructure.Connection;

public interface IConnectionCoordinator : IAsyncDisposable
{
    event EventHandler<ConnectionState>? StateChanged;
    event EventHandler<Exception>? Faulted;
    ITcastConnection Connection { get; }
    ConnectionState State { get; }
    IPAddress? Target { get; }
    ConnectionSession? Session { get; }
    Task ConnectAsync(IPAddress address, CancellationToken cancellationToken = default);
    Task DisconnectAsync();
}

public sealed class ConnectionCoordinator : IConnectionCoordinator
{
    private readonly ITcastConnection _connection;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private bool _disposed;

    public ConnectionCoordinator(ITcastConnection connection)
    {
        ArgumentNullException.ThrowIfNull(connection);
        _connection = connection;
        _connection.StateChanged += OnConnectionStateChanged;
        _connection.Faulted += OnConnectionFaulted;
    }

    public event EventHandler<ConnectionState>? StateChanged;
    public event EventHandler<Exception>? Faulted;
    public ITcastConnection Connection => _connection;
    public ConnectionState State => _connection.State;
    public ConnectionSession? Session => _connection.Session;
    public IPAddress? Target { get; private set; }

    public async Task ConnectAsync(IPAddress address, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(address);
        if (address.AddressFamily != AddressFamily.InterNetwork)
        {
            throw new ArgumentException("需要电视的 IPv4 地址", nameof(address));
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_connection.State != ConnectionState.Disconnected)
            {
                throw new InvalidOperationException("请先显式断开当前连接，再连接其他设备");
            }

            Target = address;
            await _connection.ConnectAsync(address, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task DisconnectAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            await _connection.DisconnectAsync().ConfigureAwait(false);
            Target = null;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _connection.StateChanged -= OnConnectionStateChanged;
            _connection.Faulted -= OnConnectionFaulted;
            await _connection.DisconnectAsync().ConfigureAwait(false);
            Target = null;
        }
        finally
        {
            _gate.Release();
        }
    }

    private void OnConnectionStateChanged(object? sender, ConnectionState state) =>
        StateChanged?.Invoke(this, state);

    private void OnConnectionFaulted(object? sender, Exception error) =>
        Faulted?.Invoke(this, error);
}
