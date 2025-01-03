using InTheHand.Bluetooth;
using ZwiftPlayConsoleApp.BLE;
using ZwiftPlayConsoleApp.Configuration;
using ZwiftPlayConsoleApp.Zap;
using Microsoft.Extensions.DependencyInjection;
using ZwiftPlayConsoleApp.Logging;
using ZwiftPlayConsoleApp.Utils;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;

namespace ZwiftPlayConsoleApp;

public class Program
{
    public static async Task Main(string[] args)
    {
        var validArgPrefixes = new[] { "--SendKeys", "--UseMapping", "--MappingFile=", "--help" };
        var invalidArgs = args.Where(arg => !validArgPrefixes.Any(prefix => arg.StartsWith(prefix))).ToList();

        Console.WriteLine("Starting Zwift Play Console App...");
        Console.WriteLine($"Current Directory: {Directory.GetCurrentDirectory()}");
        
        var config = new Config();
        Console.WriteLine("\nCommand line parameters:");
        Console.WriteLine($"SendKeys: {config.SendKeys}");
        Console.WriteLine($"UseMapping: {config.UseMapping}");
        Console.WriteLine($"MappingFilePath: {config.MappingFilePath}");

        if (args.Contains("--help"))
        {
            Console.WriteLine("ZwiftPlay Console App - Command Line Parameters:");
            Console.WriteLine("--SendKeys                    Enable keyboard input simulation");
            Console.WriteLine("--UseMapping                  Enable custom key mapping from TPVirtual.json");
            Console.WriteLine("--MappingFile=<filename>      Specify the custom key mapping file");
            Console.WriteLine("Hint:");
            Console.WriteLine("  --UseMapping implicit sets --SendKeys");
            Console.WriteLine("  --MappingFile implicit sets --UseMapping and --SendKeys");
            Console.WriteLine("Example: --MappingFile=custom.json");
            Environment.Exit(0);
        }

        if (invalidArgs.Any())
        {
            Console.WriteLine($"Unknown command line parameter(s): {string.Join(", ", invalidArgs)}");
            Console.WriteLine($"Valid parameters are: {string.Join(", ", validArgPrefixes)}");
            Console.WriteLine("Use --help for more information");
            Environment.Exit(1);
        }

        if (args.Any(arg => arg.StartsWith("--MappingFile=")))
        {
            var mappingArg = args.First(arg => arg.StartsWith("--MappingFile="));
            var fileName = mappingArg.Split('=')[1];
            
            if (string.IsNullOrWhiteSpace(fileName))
            {
                Console.WriteLine("Error: --MappingFile requires a filename parameter");
                Console.WriteLine("Example: --MappingFile=custom.json");
                Environment.Exit(1);
            }

            config.MappingFilePath = fileName;
            config.LoadMappingFile();
            Console.WriteLine($"\nLoaded mapping file: {config.MappingFilePath}");
            Console.WriteLine($"Mapped buttons: {config.KeyboardMapping.ButtonToKeyMap.Count}");
            config.UseMapping = true;
            config.SendKeys = true;
        }
        else if (args.Contains("--UseMapping"))
        {
            config.UseMapping = true;
            config.SendKeys = true;
        }
        else if (args.Contains("--SendKeys"))
        {
            config.SendKeys = true;
        }
        Console.WriteLine("\nConfiguration files:");
        Console.WriteLine($"AppSettings.json: {File.Exists("Configuration/AppSettings.json")}");
        Console.WriteLine($"Mapping file: {File.Exists(config.MappingFilePath)}");

        var services = new ServiceCollection();
        ConfigureServices(services, config);

        var serviceProvider = services.BuildServiceProvider();
        var settings = serviceProvider.GetService<IOptions<AppSettings>>()?.Value;
        
        Console.WriteLine("\nApp Settings:");
        Console.WriteLine($"Default Task Delay: {settings?.DefaultTaskDelay} ms");
        Console.WriteLine($"Exit: {(!config.SendKeys ? $"Press {settings?.QuitKey} to quit" : "Press Ctrl-C to exit")}");
        Console.WriteLine($"Scan Timeout: {settings?.DefaultScanTimeoutMs / 1000.0:F0} s");
        Console.WriteLine($"Required Devices: {settings?.DefaultRequiredDeviceCount}");
        Console.WriteLine($"Connection Timeout: {settings?.DefaultConnectionTimeoutMs / 1000.0:F0} s");
        Console.WriteLine("\nStarting device scanning...\n");

        using var app = serviceProvider.GetRequiredService<App>();
        await app.RunAsync();
    }
    private static void ConfigureServices(IServiceCollection services, Config config)
    {
        ValidateConfiguration(config);

        // 1. Configuration setup
        IConfiguration configuration = new ConfigurationBuilder()
            .SetBasePath(Directory.GetCurrentDirectory())
            .AddJsonFile("Configuration/AppSettings.json", optional: false)
            .Build();

        // 2. Logging setup
        var loggingConfig = new LoggingConfig 
        {
            EnableBleManagerLogging = false,
            EnableZwiftDeviceLogging = false,
            EnableControllerNotificationLogging = false,
            EnableKeyboardKeysLogging = false,
            EnableAppLogging = false
        };

        // 3. Register configurations
        services.AddSingleton(config);
        services.AddSingleton(loggingConfig);
        services.Configure<AppSettings>(configuration.GetSection("AppSettings"));

        // 4. Register loggers
        var logger = new ConfigurableLogger(loggingConfig, nameof(App));
        services.AddSingleton<IZwiftLogger>(logger);

        // 5. Initialize components
        KeyboardKeys.Initialize(config, logger);

        // 6. Register application services
        services.AddSingleton<App>();
        services.AddSingleton<ZwiftPlayDevice>();
    }    
    private static void ValidateConfiguration(Config config)
    {
        if (config.UseMapping && string.IsNullOrEmpty(config.MappingFilePath))
        {
            throw new ArgumentException("Mapping file path required when UseMapping is enabled");
        }
    }
}

public partial class App : IDisposable
{
    private bool _disposed;
    private readonly IZwiftLogger _logger;
    private readonly Config _config;
    private readonly AppSettings _settings;
    private readonly Dictionary<string, ZwiftPlayBleManager> _bleManagers = new();
    private readonly HashSet<string> _connectedDevices = new();
    private readonly CancellationTokenSource _scanCts = new();

    public App(IZwiftLogger logger, Config config, IOptions<AppSettings> settings)
    {
        _logger = logger;
        _config = config;
        _settings = settings.Value;
    }

    public async Task RunAsync()
    {
        if (_disposed)
        {
            throw new ObjectDisposedException(nameof(App));
        }

        Console.CancelKeyPress += (sender, eventArgs) =>
        {
            eventArgs.Cancel = true;
            Console.WriteLine("Shutting down gracefully...");
            _scanCts.Cancel();
            CleanupResources();
            Environment.Exit(0);
        };

        var available = await Bluetooth.GetAvailabilityAsync();
        
        if (!available)
        {
            _logger.LogError("Bluetooth not available");
            throw new ArgumentException("Bluetooth required");
        }

        SetupBluetoothHandler();

        await Task.WhenAll(
            RunScanningLoop(),
            HandleUserInput()
        );
        
        CleanupResources();
    }
      private void SetupBluetoothHandler()
      {
          ThrowIfDisposed();
          Bluetooth.AdvertisementReceived += HandleAdvertisementReceived;
      }
    private async void HandleAdvertisementReceived(object? sender, BluetoothAdvertisingEvent scanResult)
    {
        if (scanResult?.ManufacturerData != null && 
            scanResult.ManufacturerData.ContainsKey(ZapConstants.ZWIFT_MANUFACTURER_ID) && 
            scanResult.ManufacturerData[ZapConstants.ZWIFT_MANUFACTURER_ID] != null)
        {
            await HandleDeviceDiscovered(scanResult);
        }
    }
      private async Task HandleDeviceDiscovered(BluetoothAdvertisingEvent scanResult)
      {
          ThrowIfDisposed();
          if (scanResult?.Device == null || 
              scanResult.ManufacturerData == null || 
              !scanResult.ManufacturerData.ContainsKey(ZapConstants.ZWIFT_MANUFACTURER_ID) ||
              scanResult.ManufacturerData[ZapConstants.ZWIFT_MANUFACTURER_ID] == null ||
              scanResult.ManufacturerData[ZapConstants.ZWIFT_MANUFACTURER_ID].Length == 0)
          {
              return;
          }

          var manufacturerData = scanResult.ManufacturerData[ZapConstants.ZWIFT_MANUFACTURER_ID];
          var isLeft = manufacturerData[0] == ZapConstants.RC1_LEFT_SIDE;
          var deviceKey = $"{(isLeft ? "Left" : "Right")}_{scanResult.Device.Id}";

          if (!_connectedDevices.Contains(deviceKey))
          {
              _logger.LogInfo($"Found {(isLeft ? "Left" : "Right")} controller");
              Console.WriteLine($"Found {(isLeft ? "Left" : "Right")} controller");
              var manager = new ZwiftPlayBleManager(scanResult.Device, isLeft, _logger, _config);
              _bleManagers[deviceKey] = manager;
              _connectedDevices.Add(deviceKey);

              using var cts = new CancellationTokenSource(_settings.DefaultConnectionTimeoutMs);
              await manager.ConnectAsync();
          }
      }    

      private async Task RunScanningLoop()
      {
          ThrowIfDisposed();
          using var scanTimeoutCts = new CancellationTokenSource(TimeSpan.FromMilliseconds(_settings.DefaultScanTimeoutMs));
          int lastDeviceCount = 0;
    
          _logger.LogInfo($"Starting scan for {_settings.DefaultRequiredDeviceCount} devices with {_settings.DefaultScanTimeoutMs/1000.0:F0} s timeout");
          Console.WriteLine($"Starting scan for {_settings.DefaultRequiredDeviceCount} devices with {_settings.DefaultScanTimeoutMs/1000.0:F0} s timeout");

          while (!scanTimeoutCts.Token.IsCancellationRequested)
          {
              if (_bleManagers.Count != lastDeviceCount)
              {
                  _logger.LogInfo($"Scanning - Connected {_bleManagers.Count}/{_settings.DefaultRequiredDeviceCount}");
                  Console.WriteLine($"Scanning - Connected {_bleManagers.Count}/{_settings.DefaultRequiredDeviceCount}");
                  lastDeviceCount = _bleManagers.Count;
              }
        
              if (_bleManagers.Count >= _settings.DefaultRequiredDeviceCount)
              {
                  _logger.LogInfo("Required device count reached");
                  Console.WriteLine("Required device count reached");
                  break;
              }

              await Bluetooth.RequestLEScanAsync(new BluetoothLEScanOptions());

              try
              {
                  using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(_scanCts.Token, scanTimeoutCts.Token);
                  await Task.Delay(_settings.DefaultTaskDelay, linkedCts.Token);
              }
              catch (OperationCanceledException)
              {
                  var reason = scanTimeoutCts.IsCancellationRequested ? "timeout" : "user cancellation";
                  _logger.LogInfo($"Scanning stopped: {reason}");
                  Console.WriteLine($"Scanning stopped: {reason}");
            
                  if (reason == "timeout")
                  {
                      _logger.LogInfo("Exiting due to scan timeout");
                      Console.WriteLine("Exiting due to scan timeout");
                      Environment.Exit(1);
                  }
                  break;
              }
          }
      }

      private async Task HandleUserInput()
      {
          ThrowIfDisposed();
          while (true) // Keep the task running
          {
              try
              {
                  if (Console.KeyAvailable && !_config.SendKeys)
                  {
                  var key = Console.ReadKey(true);
                  if (key.KeyChar.ToString() == _settings.QuitKey)
                  {
                      _logger.LogInfo("Shutting down...");
                      Console.WriteLine("Shutting down...");
                      _scanCts.Cancel();
                      Environment.Exit(0);
                  }
                  }
                  await Task.Delay(1);
              }
              catch (InvalidOperationException)
              {
              await Task.Delay(1000);
              }
          }
      }

    private void CleanupResources()
    {
        foreach (var manager in _bleManagers.Values)
        {
            manager.Dispose();
        }
        _scanCts.Dispose();
    }

    protected virtual void Dispose(bool disposing)
    {
        if (_disposed)
        {
            return;
        }

        if (disposing)
        {
            _scanCts.Cancel();
            
            foreach (var manager in _bleManagers.Values)
            {
                manager?.Dispose();
            }
            _bleManagers.Clear();
            _connectedDevices.Clear();
            _scanCts.Dispose();

            Bluetooth.AdvertisementReceived -= HandleAdvertisementReceived;
        }

        _disposed = true;
    }

    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    ~App()
    {
        Dispose(false);
    }

    private void ThrowIfDisposed()
    {
        if (_disposed)
        {
            throw new ObjectDisposedException(nameof(App));
        }
    }
}