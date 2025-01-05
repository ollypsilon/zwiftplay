using InTheHand.Bluetooth;
using ZwiftPlayConsoleApp.Logging;
using ZwiftPlayConsoleApp.Zap;
using ZwiftPlayConsoleApp.Configuration;
using System.Collections.Concurrent;
using ZwiftPlayConsoleApp.Utils;

namespace ZwiftPlayConsoleApp.BLE;
public partial class ZwiftPlayBleManager : IDisposable
{
    private readonly ZwiftPlayDevice _zapDevice;
    private readonly BluetoothDevice _device;
    private readonly bool _isLeft;
    private readonly IZwiftLogger _logger;
    private bool _isDisposed;
    private readonly object _lock = new();
    private readonly Config _config;
    private GattCharacteristic? _asyncCharacteristic;
    private GattCharacteristic? _syncRxCharacteristic;
    private GattCharacteristic? _syncTxCharacteristic;
    private const int CONNECTION_INTERVAL = 11; // 1.25ms units
    private readonly ConcurrentQueue<(string source, byte[] value)> _characteristicQueue = new();
    private readonly PeriodicTimer _processingTimer;
    private const int PROCESSING_INTERVAL_MS = 16;
    public ZwiftPlayBleManager(BluetoothDevice device, bool isLeft, IZwiftLogger logger, Config config)
    {
        _device = device;
        _isLeft = isLeft;
        _logger = new ConfigurableLogger(((ConfigurableLogger)logger)._config, nameof(ZwiftPlayBleManager));
        _config = config;
        _stateManager = new ConnectionStateManager(_logger);
        _zapDevice = new ZwiftPlayDevice(new ConfigurableLogger(((ConfigurableLogger)logger)._config, nameof(ZwiftPlayDevice)), config);
        _processingTimer = new PeriodicTimer(TimeSpan.FromMilliseconds(PROCESSING_INTERVAL_MS));
        Task.Run(() => StartProcessingTimer(CancellationToken.None));
    }
    private readonly SemaphoreSlim _processingSemaphore = new(1, 1);
    private async Task StartProcessingTimer(CancellationToken ct)
    {
        //_logger.LogDebug("Starting processing timer");
        while (await _processingTimer.WaitForNextTickAsync(ct))
        {
            //_logger.LogDebug($"Timer tick, queue empty: {_characteristicQueue.IsEmpty}");
            if (_characteristicQueue.IsEmpty)
            {
                await Task.Delay(100, ct); // Reduced polling when idle
            }
            else
            {
                await ProcessCharacteristicBatch(ct);
            }
        }
    }
    private async Task ProcessCharacteristicBatch(CancellationToken ct)
    {
        try 
        {
            await _processingSemaphore.WaitAsync(ct);
            try
            {
                //_logger.LogDebug($"Processing timer tick, queue size: {_characteristicQueue.Count}");

                var batch = new List<(string source, byte[] value)>();
                while (batch.Count < 100 && _characteristicQueue.TryDequeue(out var item))
                {
                    //_logger.LogDebug($"Dequeued item from {item.source}");
                    batch.Add(item);
                }

                if (batch.Count > 0)
                {
                    //_logger.LogDebug($"Processing batch of {batch.Count} items");
                    foreach (var (source, value) in batch)
                    {
                        ProcessCharacteristic(source, value);
                    }
                }
            }
            finally
            {
                _processingSemaphore.Release();
            }
        }
        catch (OperationCanceledException)
        {
            // Normal cancellation
        }
    }
    private static async Task SetConnectionParameters(RemoteGattServer gatt)
    {
        var service = await gatt.GetPrimaryServiceAsync(GenericBleUuids.GENERIC_ACCESS_SERVICE_UUID);
        if (service == null) return;

        var characteristic = await service.GetCharacteristicAsync(GenericBleUuids.PREFERRED_CONNECTION_PARAMS_CHARACTERISTIC_UUID);
        if (characteristic == null || !characteristic.Properties.HasFlag(GattCharacteristicProperties.Write)) return;

        var buffer = new ByteBuffer();
        buffer.WriteInt16(CONNECTION_INTERVAL);
        buffer.WriteInt16(CONNECTION_INTERVAL * 2);
        buffer.WriteInt16(0);
        buffer.WriteInt16(300);

        await characteristic.WriteValueWithResponseAsync(buffer.ToArray());
    }
    private enum ConnectionState
    {
        Disconnected,
        Connecting,
        Connected,
        Disconnecting
    }

    // Add a connection state manager
    private sealed class ConnectionStateManager(IZwiftLogger logger)
    {
        private volatile int _state = (int)ConnectionState.Disconnected;
        private readonly IZwiftLogger _logger = logger;

        public bool TrySetState(ConnectionState expectedState, ConnectionState newState)
        {
            var currentState = (ConnectionState)_state;
            if (currentState != expectedState) 
            {
                _logger.LogDebug($"Invalid state transition from {currentState} to {newState}");
                return false;
            }
            
            Interlocked.Exchange(ref _state, (int)newState);
            _logger.LogDebug($"State changed: {newState}");
            return true;
        }

        public ConnectionState CurrentState => (ConnectionState)_state;
    }

    private readonly ConnectionStateManager _stateManager;
    private readonly SemaphoreSlim _connectionLock = new(1, 1);
    private readonly CancellationTokenSource _processingCts = new();

    public async Task ConnectAsync()
    {
        if (_stateManager.CurrentState != ConnectionState.Disconnected)
        {
            return;
        }

        await _connectionLock.WaitAsync().ConfigureAwait(false);
        try
        {
            _stateManager.TrySetState(ConnectionState.Disconnected, ConnectionState.Connecting);
        
            if (await TryConnectWithRetry().ConfigureAwait(false))
            {
                var gatt = _device.Gatt;
                await SetConnectionParameters(gatt).ConfigureAwait(false);
                _zapDevice.ResetEncryption();
                _logger.LogInfo($"Connected {(_isLeft ? "Left" : "Right")} controller");
                await RegisterCharacteristics(gatt).ConfigureAwait(false);

                if (_syncRxCharacteristic != null)
                {
                    var handshakeData = _zapDevice.BuildHandshakeStart();
                    _logger.LogDebug($"Sending handshake data: {BitConverter.ToString(handshakeData)}");
                    await _syncRxCharacteristic.WriteValueWithResponseAsync(handshakeData).ConfigureAwait(false);
                    _logger.LogInfo("Handshake initiated");
                }
                _stateManager.TrySetState(ConnectionState.Connecting, ConnectionState.Connected);
                
                // Start processing with cancellation token
                _ = ProcessCharacteristicBatch(_processingCts.Token);
            }
            else
            {
                throw new InvalidOperationException("Failed to connect after multiple attempts");
            }
        }
        catch (Exception ex)
        {
            _stateManager.TrySetState(_stateManager.CurrentState, ConnectionState.Disconnected);
            _logger.LogError("Connection failed", ex);
            throw;
        }
        finally
        {
            _connectionLock.Release();
        }
    }
    private async ValueTask RegisterCharacteristics(RemoteGattServer gatt)
    {
        _logger.LogDebug("Starting characteristic registration");
    
        // Ensure connection is stable
        await Task.Delay(1000).ConfigureAwait(false);
    
        if (!gatt.IsConnected)
        {
            await gatt.ConnectAsync().ConfigureAwait(false);
            await Task.Delay(500).ConfigureAwait(false);
        }
    
        // Get all services and verify ZAP service
        var services = await gatt.GetPrimaryServicesAsync();
        _logger.LogDebug($"Found {services.Count} services");
    
        var zapService = services.FirstOrDefault(s => s.Uuid == ZapBleUuids.ZWIFT_CUSTOM_SERVICE_UUID);
        if (zapService == null)
        {
            _logger.LogError($"ZAP service {ZapBleUuids.ZWIFT_CUSTOM_SERVICE_UUID} not found");
            throw new InvalidOperationException("Required ZAP service not found on device");
        }

        // Get all characteristics at once
        var characteristics = await zapService.GetCharacteristicsAsync();
    
        // Log all discovered characteristics
        foreach (var characteristic in characteristics)
        {
            _logger.LogDebug($"Found characteristic: {characteristic.Uuid}");
        }

        // Match characteristics by UUID
        _asyncCharacteristic = characteristics.FirstOrDefault(c => c.Uuid == ZapBleUuids.ZWIFT_ASYNC_CHARACTERISTIC_UUID);
        _syncRxCharacteristic = characteristics.FirstOrDefault(c => c.Uuid == ZapBleUuids.ZWIFT_SYNC_RX_CHARACTERISTIC_UUID);
        _syncTxCharacteristic = characteristics.FirstOrDefault(c => c.Uuid == ZapBleUuids.ZWIFT_SYNC_TX_CHARACTERISTIC_UUID);

        // Verify all required characteristics are found
        if (_asyncCharacteristic == null || _syncRxCharacteristic == null || _syncTxCharacteristic == null)
        {
            var missingCharacteristics = new List<string>();
            if (_asyncCharacteristic == null) missingCharacteristics.Add($"Async ({ZapBleUuids.ZWIFT_ASYNC_CHARACTERISTIC_UUID})");
            if (_syncRxCharacteristic == null) missingCharacteristics.Add($"Sync RX ({ZapBleUuids.ZWIFT_SYNC_RX_CHARACTERISTIC_UUID})");
            if (_syncTxCharacteristic == null) missingCharacteristics.Add($"Sync TX ({ZapBleUuids.ZWIFT_SYNC_TX_CHARACTERISTIC_UUID})");
        
            var errorMessage = $"Missing characteristics: {string.Join(", ", missingCharacteristics)}";
            _logger.LogError(errorMessage);
            throw new InvalidOperationException(errorMessage);
        }

        // Enable notifications
        await _asyncCharacteristic.StartNotificationsAsync().ConfigureAwait(false);
        await _syncTxCharacteristic.StartNotificationsAsync().ConfigureAwait(false);

        _asyncCharacteristic.CharacteristicValueChanged += OnAsyncCharacteristicChanged;
        _syncTxCharacteristic.CharacteristicValueChanged += OnSyncTxCharacteristicChanged;
    
        _logger.LogInfo("Characteristic registration completed successfully");
    }
    public void Dispose()
    {
        lock (_lock)
        {
            if (_isDisposed) return;

            _processingCts.Cancel(); // Cancel ongoing processing
            
            // Force immediate cleanup
            if (_device?.Gatt != null && _device.Gatt.IsConnected)
            {
                _device.Gatt.Disconnect();
            }

            // Cleanup event handlers immediately
            if (_asyncCharacteristic != null)
            {
                _asyncCharacteristic.CharacteristicValueChanged -= OnAsyncCharacteristicChanged;
                _asyncCharacteristic = null;
            }
            if (_syncTxCharacteristic != null)
            {
                _syncTxCharacteristic.CharacteristicValueChanged -= OnSyncTxCharacteristicChanged;
                _syncTxCharacteristic = null;
            }

            _processingTimer.Dispose();
            _connectionLock.Dispose();
            _processingCts.Dispose();

            _isDisposed = true;
            GC.SuppressFinalize(this);
        }
    }
    private void ProcessCharacteristic(string source, byte[] value)
    {
        if (_isDisposed || value.Length == 0) return;

        //_logger.LogDebug($"Processing {source} characteristic: {BitConverter.ToString(value)}");
        //_logger.LogDebug($"Forwarding to ZwiftPlayDevice - {source} characteristic: {BitConverter.ToString(value)}");
        _zapDevice.ProcessCharacteristic(source, value);
    }
    private void OnAsyncCharacteristicChanged(object? sender, GattCharacteristicValueChangedEventArgs e)
    {
        var bytes = new byte[e.Value.Length];
        e.Value.CopyTo(bytes, 0);
        EnqueueCharacteristic("Async", bytes);
    }
    private void OnSyncTxCharacteristicChanged(object? sender, GattCharacteristicValueChangedEventArgs e)
    {
        var bytes = new byte[e.Value.Length];
        e.Value.CopyTo(bytes, 0);
        EnqueueCharacteristic("Sync Tx", bytes);
    }
    private const int MAX_QUEUE_SIZE = 1000;
    private void EnqueueCharacteristic(string source, byte[] value)
    {
        if (_characteristicQueue.Count < MAX_QUEUE_SIZE)
        {
            // Only enqueue if the last message was different
            if (_characteristicQueue.IsEmpty || !CompareLastMessage(source, value))
            {
                _characteristicQueue.Enqueue((source, value));
                //_logger.LogDebug($"Enqueued new {source} message: {BitConverter.ToString(value)}");
            }
        }
        else
        {
            _logger.LogWarning("Characteristic queue full, dropping packet");
        }
    }
    private bool CompareLastMessage(string source, byte[] value)
    {
        if (_characteristicQueue.TryPeek(out var last))
        {
            return last.source == source && last.value.SequenceEqual(value);
        }
        return false;
    }
    private const int MaxRetryAttempts = 3;
    private const int RetryDelayMs = 1000;
    private async ValueTask<bool> TryConnectWithRetry()
    {
        for (int attempt = 0; attempt < MaxRetryAttempts; attempt++)
        {
            try
            {
                var gatt = _device.Gatt;
                await gatt.ConnectAsync().ConfigureAwait(false);
                return gatt.IsConnected;
            }            catch (Exception ex) when (attempt < MaxRetryAttempts - 1)
            {
                _logger.LogWarning($"Connection attempt {attempt + 1} failed: {ex.Message}");
                await Task.Delay(RetryDelayMs).ConfigureAwait(false);
            }
        }
        return false;
    }
}
