using ZwiftPlayConsoleApp.Logging;
using ZwiftPlayConsoleApp.Utils;
using ZwiftPlayConsoleApp.Zap;
using ZwiftPlayConsoleApp.Zap.Crypto;
using ZwiftPlayConsoleApp.Zap.Proto;
using ZwiftPlayConsoleApp.Configuration;

namespace ZwiftPlayConsoleApp.BLE;

public class ZwiftPlayDevice : AbstractZapDevice
{
    
    //private readonly IZwiftLogger _logger;
    private int _batteryLevel;
    private ControllerNotification? _lastButtonState;
    private readonly Config _config;
    private byte[] _counterBuffer = new byte[4];
    private byte[] _payloadBuffer = new byte[1024]; // Adjust size as needed
    public ZwiftPlayDevice(IZwiftLogger logger, Config config) : base(logger)
    {
        _config = config;
        _logger.LogInfo($"ZwiftPlayDevice initialized with SendKeys: {config.SendKeys}, UseMapping: {config.UseMapping}");
        Console.WriteLine($"ZwiftPlayDevice initialized with SendKeys: {config.SendKeys}, UseMapping: {config.UseMapping}");
    }
    protected override void ProcessEncryptedData(byte[] bytes)
    {
        _logger.LogDebug($"Processing encrypted data length: {bytes.Length}");
        try
        {
            // Reuse buffers instead of creating new arrays
            Buffer.BlockCopy(bytes, 0, _counterBuffer, 0, _counterBuffer.Length);
            var counter = new ByteBuffer(_counterBuffer).ReadInt32();
            
            var payloadLength = bytes.Length - 4 - EncryptionUtils.MAC_LENGTH;
            Buffer.BlockCopy(bytes, 4, _payloadBuffer, 0, payloadLength);
            
            if (Debug)
                _logger.LogDebug($"Processing encrypted data: {Utils.Utils.ByteArrayToStringHex(bytes)}");
            if (Debug)
                _logger.LogDebug($"Counter bytes: {Utils.Utils.ByteArrayToStringHex(_counterBuffer)}");
            if (Debug)
                _logger.LogDebug($"Attempting payload extraction, length: {payloadLength}");

            var tagBytes = new byte[EncryptionUtils.MAC_LENGTH];
            Array.Copy(bytes, EncryptionUtils.MAC_LENGTH + payloadLength, tagBytes, 0, tagBytes.Length);
            if (Debug)
                _logger.LogDebug($"Attempting tag extraction, starting at index: {EncryptionUtils.MAC_LENGTH + payloadLength}");

            byte[] data;
            try
            {
                data = _zapEncryption.Decrypt(counter, _payloadBuffer.AsSpan(0, payloadLength).ToArray(), tagBytes);
            }
            catch (Exception ex)
            {
                _logger.LogError($"Decrypt failed - Counter: {counter}, Payload: {BitConverter.ToString(_payloadBuffer, 0, payloadLength)}, Tag: {BitConverter.ToString(tagBytes)}", ex);
                return;
            }
            if (Debug)
            _logger.LogDebug($"Decrypted data: {BitConverter.ToString(data)}");

            var type = data[0];
            var messageBytes = new byte[data.Length - 1];
            Array.Copy(data, 1, messageBytes, 0, messageBytes.Length);

            if (Debug)
                _logger.LogDebug($"Controller notification message type: {type}");
            switch (type)
            {
                case ZapConstants.CONTROLLER_NOTIFICATION_MESSAGE_TYPE:
                    _logger.LogInfo("Button state change detected");
                    ProcessButtonNotification(new ControllerNotification(messageBytes, new ConfigurableLogger(((ConfigurableLogger)_logger)._config, nameof(ControllerNotification))));
                    break;
                case ZapConstants.EMPTY_MESSAGE_TYPE:
                    if (Debug)
                        _logger.LogDebug("Empty Message");
                    break;
                case ZapConstants.BATTERY_LEVEL_TYPE:
                    var notification = new BatteryStatus(messageBytes);
                    if (_batteryLevel != notification.Level)
                    {
                        _batteryLevel = notification.Level;
                        _logger.LogInfo($"Battery level update: {_batteryLevel}");
                    }
                    break;
                default:
                    _logger.LogWarning($"Unprocessed - Type: {type} Data: {Utils.Utils.ByteArrayToStringHex(data)}");
                    break;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError("Decrypt failed", ex);
        }
    }
    private void ProcessButtonNotification(ControllerNotification notification)
    {

        if (_config.SendKeys)
        {
            var changes = notification.DiffChange(_lastButtonState);
            foreach (var change in changes)
            {
                KeyboardKeys.ProcessZwiftPlay(change);
            }
        }
        else
        {
            if (_lastButtonState == null)
            {
                _logger.LogInfo($"Controller: {notification}");
            }
            else
            {
                var diff = notification.Diff(_lastButtonState);
                if (!string.IsNullOrEmpty(diff))
                {
                    _logger.LogInfo($"Button: {diff}");
                    Console.WriteLine($"Button: {diff}");
                }
            }
        }

        _lastButtonState = notification;
    }
}