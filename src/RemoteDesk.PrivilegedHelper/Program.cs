using System.Security.Cryptography;
using System.Text;
using RemoteDesk.PrivilegedHelper;

var pipeName = args.Length > 0 ? args[0] : PrivilegedRebootClient.DefaultPipeName;

// Load machine secret from restricted system location, or generate ephemeral local key
var keyPath = Path.Combine(
    Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
    "SwiftDesk",
    "reboot.key");

byte[] secretKey;
if (File.Exists(keyPath))
{
    secretKey = File.ReadAllBytes(keyPath);
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
            File.WriteAllBytes(keyPath, secretKey);
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

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    cts.Cancel();
};

await server.RunAsync(cts.Token);
Console.WriteLine("[SwiftDesk.PrivilegedHelper] Stopped.");
