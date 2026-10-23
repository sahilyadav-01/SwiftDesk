namespace RemoteDesk.Agent;

internal sealed record AgentOptions(
    Uri Server,
    string DeviceName,
    string? DeviceId,
    string? DeviceSecret,
    string? BootstrapToken,
    string? DownloadDirectory,
    bool AutoApproveFiles)
{
    public static AgentOptions Parse(string[] args)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var switches = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var index = 0; index < args.Length; index++)
        {
            if (!args[index].StartsWith("--", StringComparison.Ordinal))
            {
                throw new ArgumentException($"Unexpected argument: {args[index]}");
            }

            var key = args[index][2..];
            if (index + 1 < args.Length && !args[index + 1].StartsWith("--", StringComparison.Ordinal))
            {
                values[key] = args[++index];
            }
            else
            {
                switches.Add(key);
            }
        }

        var serverText = Get(values, "server", "REMOTEDESK_SERVER") ?? "http://localhost:5080";
        if (!Uri.TryCreate(serverText, UriKind.Absolute, out var server)
            || server.Scheme is not ("http" or "https"))
        {
            throw new ArgumentException("--server must be an absolute HTTP or HTTPS URL.");
        }

        var deviceName = Get(values, "name", "REMOTEDESK_DEVICE_NAME") ?? Environment.MachineName;
        return new AgentOptions(
            server,
            deviceName,
            Get(values, "device-id", "REMOTEDESK_DEVICE_ID"),
            Get(values, "device-secret", "REMOTEDESK_DEVICE_SECRET"),
            Get(values, "bootstrap-token", "REMOTEDESK_BOOTSTRAP_TOKEN"),
            Get(values, "download-directory", "REMOTEDESK_DOWNLOAD_DIRECTORY"),
            switches.Contains("auto-approve-files"));
    }

    private static string? Get(
        IReadOnlyDictionary<string, string> values,
        string argumentName,
        string environmentName) =>
        values.GetValueOrDefault(argumentName)
        ?? Environment.GetEnvironmentVariable(environmentName);
}

