namespace RemoteDesk.PrivilegedHelper;

using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

public static class Program
{
    public static async Task Main(string[] args)
    {
        var builder = Host.CreateApplicationBuilder(args);

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            builder.Services.AddWindowsService(options =>
            {
                options.ServiceName = "SwiftDeskPrivilegedHelper";
            });
        }

        var pipeName = args.Length > 0 ? args[0] : PrivilegedRebootClient.DefaultPipeName;
        builder.Services.AddSingleton(new PrivilegedRebootServiceOptions(pipeName));
        builder.Services.AddHostedService<PrivilegedRebootWorker>();

        var host = builder.Build();
        await host.RunAsync().ConfigureAwait(false);
    }
}

public sealed record PrivilegedRebootServiceOptions(string PipeName);

public sealed class PrivilegedRebootWorker(PrivilegedRebootServiceOptions options) : BackgroundService
{
    private readonly PrivilegedRebootServiceOptions _options = options;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var pipeName = _options.PipeName;

        // Load machine secret from restricted system location, or generate ephemeral local key
        var keyPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "SwiftDesk",
            "reboot.key");

        byte[] secretKey;
        if (File.Exists(keyPath))
        {
            secretKey = await File.ReadAllBytesAsync(keyPath, stoppingToken).ConfigureAwait(false);
        }
        else
        {
            secretKey = RandomNumberGenerator.GetBytes(32);
            try
            {
                var dir = Path.GetDirectoryName(keyPath);
                if (!string.IsNullOrEmpty(dir))
                {
                    Directory.CreateDirectory(dir);
                    await File.WriteAllBytesAsync(keyPath, secretKey, stoppingToken).ConfigureAwait(false);
                }
            }
            catch
            {
                // Fallback to memory-only key if directory cannot be written
            }
        }

        Console.WriteLine($"[SwiftDesk.PrivilegedHelper] Starting reboot daemon on pipe '{pipeName}'...");
        var engine = new PrivilegedRebootEngine(secretKey);
        var server = new PrivilegedRebootIpcServer(pipeName, engine);

        try
        {
            await server.RunAsync(stoppingToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Graceful shutdown
        }
        finally
        {
            Console.WriteLine("[SwiftDesk.PrivilegedHelper] Stopped.");
        }
    }
}
