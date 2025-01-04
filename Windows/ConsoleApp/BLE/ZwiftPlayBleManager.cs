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
    private static GattCharacteristic? _asyncCharacteristic;
    private static GattCharacteristic? _syncRxCharacteristic;
    private static GattCharacteristic? _syncTxCharacteristic;
    private const int MINIMUM_PROCESS_INTERVAL_MS = 16; // ~60Hz
    private const int CONNECTION_INTERVAL = 11; // 1.25ms units
    private readonly ConcurrentQueue<(string source, byte[] value)> _characteristicQueue = new();
    private readonly PeriodicTimer _processingTimer;

    public ZwiftPlayBleManager(BluetoothDevice device, bool isLeft, IZwiftLogger logger, Config config)
    {
        _device = device;
        _isLeft = isLeft;
        _logger = new ConfigurableLogger(((ConfigurableLogger)logger)._config, nameof(ZwiftPlayBleManager));
        _config = config;
        _zapDevice = new ZwiftPlayDevice(new ConfigurableLogger(((ConfigurableLogger)logger)._config, nameof(ZwiftPlayDevice)), config);
        _processingTimer = new PeriodicTimer(TimeSpan.FromMilliseconds(MINIMUM_PROCESS_INTERVAL_MS));
        Task.Run(ProcessCharacteristicBatch);
    }

    private async Task ProcessCharacteristicBatch()
    {
        while (!_isDisposed)
        {
            await _processingTimer.WaitForNextTickAsync().ConfigureAwait(false);
            
            var batch = new List<(string source, byte[] value)>();
            while (_characteristicQueue.TryDequeue(out var item))
            {
                batch.Add(item);
            }

            if (batch.Count > 0)
            {
                foreach (var (source, value) in batch)
                {
                    ProcessCharacteristic(source, value);
                }
            }
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
    public async Task ConnectAsync()
    {
        try
        {
            _isDisposed = false;  // Reset disposal state
            var gatt = _device.Gatt;
            await gatt.ConnectAsync();

            if (gatt.IsConnected)
            {
                await SetConnectionParameters(gatt);
                _zapDevice.ResetEncryption();
                _logger.LogInfo($"Connected {(_isLeft ? "Left" : "Right")} controller");
                await RegisterCharacteristics(gatt);

                if (_syncRxCharacteristic != null)
                {
                    var handshakeData = _zapDevice.BuildHandshakeStart();
                    _logger.LogDebug($"Sending handshake data: {BitConverter.ToString(handshakeData)}");
                    await _syncRxCharacteristic.WriteValueWithResponseAsync(handshakeData);
                    _logger.LogInfo("Handshake initiated");
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError("Connection failed", ex);
            throw;
        }
    }
    private async Task RegisterCharacteristics(RemoteGattServer gatt)
    {
        _logger.LogDebug("Starting characteristic registration");
        
        // Initial connection stabilization
        await Task.Delay(2000);
        
        var zapService = await gatt.GetPrimaryServiceAsync(ZapBleUuids.ZWIFT_CUSTOM_SERVICE_UUID);
        if (zapService == null)
        {
            _logger.LogError($"ZAP service {ZapBleUuids.ZWIFT_CUSTOM_SERVICE_UUID} not found");
            throw new InvalidOperationException("Required ZAP service not found on device");
        }

        _logger.LogDebug("ZAP service found, discovering characteristics...");
        
        // Get all characteristics at once and cache them
        var characteristics = await zapService.GetCharacteristicsAsync();
        
        _asyncCharacteristic = characteristics.FirstOrDefault(c => c.Uuid == ZapBleUuids.ZWIFT_ASYNC_CHARACTERISTIC_UUID);
        _syncRxCharacteristic = characteristics.FirstOrDefault(c => c.Uuid == ZapBleUuids.ZWIFT_SYNC_RX_CHARACTERISTIC_UUID);
        _syncTxCharacteristic = characteristics.FirstOrDefault(c => c.Uuid == ZapBleUuids.ZWIFT_SYNC_TX_CHARACTERISTIC_UUID);

        if (_asyncCharacteristic == null || _syncRxCharacteristic == null || _syncTxCharacteristic == null)
        {
            _logger.LogError("Missing required characteristics");
            _logger.LogDebug($"Found characteristics: {string.Join(", ", characteristics.Select(c => c.Uuid))}");
            throw new InvalidOperationException("Required BLE characteristics not found");
        }

        await _asyncCharacteristic.StartNotificationsAsync();
        _asyncCharacteristic.CharacteristicValueChanged += OnAsyncCharacteristicChanged;

        await _syncTxCharacteristic.StartNotificationsAsync();
        _syncTxCharacteristic.CharacteristicValueChanged += OnSyncTxCharacteristicChanged;

        _logger.LogInfo("Characteristic registration completed successfully");
    }   
    public void Dispose()
    {
        lock (_lock)
        {
            if (_isDisposed) return;

            if (_asyncCharacteristic != null)
            {
                _asyncCharacteristic.CharacteristicValueChanged -= (sender, eventArgs) =>
                    ProcessCharacteristic("Async", eventArgs.Value);
            }
            if (_syncTxCharacteristic != null)
            {
                _syncTxCharacteristic.CharacteristicValueChanged -= (sender, eventArgs) =>
                    ProcessCharacteristic("Sync Tx", eventArgs.Value);
            }

            if (_device?.Gatt != null && _device.Gatt.IsConnected)
            {
                _device.Gatt.Disconnect();
            }

            _isDisposed = true;
            GC.SuppressFinalize(this);
        }
    }
    private long _lastProcessTicks = 0;
    private const int MINIMUM_PROCESS_INTERVAL_TICKS = (int)(TimeSpan.TicksPerMillisecond * MINIMUM_PROCESS_INTERVAL_MS);
    private void ProcessCharacteristic(string source, byte[] value)
    {
        if (_isDisposed) return;

        var currentTicks = DateTime.UtcNow.Ticks;
        if (Interlocked.Read(ref _lastProcessTicks) + MINIMUM_PROCESS_INTERVAL_TICKS > currentTicks)
            return;
        
        Interlocked.Exchange(ref _lastProcessTicks, currentTicks);

        _logger.LogDebug($"Processing {source} characteristic: {BitConverter.ToString(value)}");
        _zapDevice.ProcessCharacteristic(source, value);
    }
    private void OnAsyncCharacteristicChanged(object? sender, GattCharacteristicValueChangedEventArgs e)
    {
        OnCharacteristicChanged("Async", e.Value);
    }
    private void OnSyncTxCharacteristicChanged(object? sender, GattCharacteristicValueChangedEventArgs e)
    {
        OnCharacteristicChanged("Sync Tx", e.Value);
    }
    private void OnCharacteristicChanged(string source, byte[] value)
    {
        _characteristicQueue.Enqueue((source, value));
    }
}