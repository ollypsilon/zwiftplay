using ZwiftPlayConsoleApp.BLE;
using ZwiftPlayConsoleApp.Zap.Proto;
using ZwiftPlayConsoleApp.Utils;

namespace ZwiftPlayConsoleApp.Logging

{
    public interface IZwiftLogger
    {
        void LogDebug(string message);
        void LogInfo(string message);
        void LogWarning(string message);
        void LogError(string message);
        void LogError(string message, Exception? ex = null);
    }

    public class LoggingConfig
    {
        public bool EnableBleManagerLogging { get; set; }
        public bool EnableZwiftDeviceLogging { get; set; }
        public bool EnableControllerNotificationLogging { get; set; }
        public bool EnableKeyboardKeysLogging { get; set; }
        public bool EnableAppLogging { get; set; }
    }

    public class ConfigurableLogger : IZwiftLogger
    {
        internal readonly LoggingConfig _config;
        private readonly string _className;

        public ConfigurableLogger(LoggingConfig config, string className = "")
        {
            _config = config;
            _className = className;
        }

        public void LogDebug(string message)
        {
            if (ShouldLog()) 
                Console.WriteLine($"[DEBUG] {_className}: {message}");
        }

        public void LogInfo(string message)
        {
            if (ShouldLog())
                Console.WriteLine($"[INFO] {_className}: {message}");
        }

        public void LogWarning(string message)
        {
            if (ShouldLog())
                Console.WriteLine($"[WARN] {_className}: {message}");
        }

        public void LogError(string message)
        {
            if (ShouldLog())
                Console.WriteLine($"[ERROR] {_className}: {message}");
        }

        public void LogError(string message, Exception? ex = null)
        {
            if (ShouldLog())
                Console.WriteLine($"[ERROR] {_className}: {message} - {ex}");
        }

        private bool ShouldLog()
        {
            return _className switch
            {
                nameof(ZwiftPlayBleManager) => _config.EnableBleManagerLogging,
                nameof(ZwiftPlayDevice) => _config.EnableZwiftDeviceLogging,
                nameof(ControllerNotification) => _config.EnableControllerNotificationLogging,
                nameof(KeyboardKeys) => _config.EnableKeyboardKeysLogging,
                nameof(App) => _config.EnableAppLogging,
                _ => false
            };
        }
    }
}
