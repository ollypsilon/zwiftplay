using InTheHand.Bluetooth;
using ZwiftPlayConsoleApp.Logging;
using ZwiftPlayConsoleApp.Zap;
using ZwiftPlayConsoleApp.Configuration;
using System.Collections.Concurrent;

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
    private DateTime _lastProcessTime = DateTime.MinValue;
    private const int MINIMUM_PROCESS_INTERVAL_MS = 16; // ~60Hz
    private readonly ConcurrentQueue<(string source, byte[] value)> _characteristicQueue = new();
    private readonly Thread _processingThread;
    public ZwiftPlayBleManager(BluetoothDevice device, bool isLeft, IZwiftLogger logger, Config config)
    {
        _device = device;
        _isLeft = isLeft;
        _logger = new ConfigurableLogger(((ConfigurableLogger)logger)._config, nameof(ZwiftPlayBleManager));
        _config = config;
        _zapDevice = new ZwiftPlayDevice(new ConfigurableLogger(((ConfigurableLogger)logger)._config, nameof(ZwiftPlayDevice)), config);
        _processingThread = new Thread(ProcessCharacteristicQueue) 
        { 
            IsBackground = true 
        };
        _processingThread.Start();
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

        var zapService = await gatt.GetPrimaryServiceAsync(ZapBleUuids.ZWIFT_CUSTOM_SERVICE_UUID);
        if (zapService == null)
        {
            _logger.LogError("ZAP service not found");
            return;
        }

        _asyncCharacteristic = await zapService.GetCharacteristicAsync(ZapBleUuids.ZWIFT_ASYNC_CHARACTERISTIC_UUID);
        _syncRxCharacteristic = await zapService.GetCharacteristicAsync(ZapBleUuids.ZWIFT_SYNC_RX_CHARACTERISTIC_UUID);
        _syncTxCharacteristic = await zapService.GetCharacteristicAsync(ZapBleUuids.ZWIFT_SYNC_TX_CHARACTERISTIC_UUID);

        if (_asyncCharacteristic != null)
        {
            await _asyncCharacteristic.StartNotificationsAsync();
            _asyncCharacteristic.CharacteristicValueChanged += (sender, eventArgs) =>
            {
                _logger.LogDebug($"Async characteristic value changed: {BitConverter.ToString(eventArgs.Value)}");
                ProcessCharacteristic("Async", eventArgs.Value);
            };
        }

        if (_syncTxCharacteristic != null)
        {
            await _syncTxCharacteristic.StartNotificationsAsync();
            _syncTxCharacteristic.CharacteristicValueChanged += (sender, eventArgs) =>
            {
                _logger.LogDebug($"Sync Tx characteristic value changed: {BitConverter.ToString(eventArgs.Value)}");
                ProcessCharacteristic("Sync Tx", eventArgs.Value);
            };
        }

        _logger.LogInfo("Characteristic registration completed");
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
    private void ProcessCharacteristic(string source, byte[] value)
    {
        if (_isDisposed) return;

        // Add throttling
        var now = DateTime.UtcNow;
        if ((now - _lastProcessTime).TotalMilliseconds < MINIMUM_PROCESS_INTERVAL_MS)
            return;
            
        _lastProcessTime = now;

        _logger.LogDebug($"Processing {source} characteristic: {BitConverter.ToString(value)}");
        _zapDevice.ProcessCharacteristic(source, value);
    }

    private void OnAsyncCharacteristicChanged(object sender, GattCharacteristicValueChangedEventArgs e)
    {
        ProcessCharacteristic("Async", e.Value);
    }

    private void OnSyncTxCharacteristicChanged(object sender, GattCharacteristicValueChangedEventArgs e)
    {
        ProcessCharacteristic("Sync Tx", e.Value);
    }
    private void OnCharacteristicChanged(string source, byte[] value)
    {
        _characteristicQueue.Enqueue((source, value));
    }

    private void ProcessCharacteristicQueue()
    {
        while (!_isDisposed)
        {
            if (_characteristicQueue.TryDequeue(out var item))
            {
                ProcessCharacteristic(item.source, item.value);
            }
            Thread.Sleep(1); // Prevent tight loop
        }
    }
}