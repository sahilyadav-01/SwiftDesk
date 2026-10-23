using RemoteDesk.Agent;
using RemoteDesk.Core;
using RemoteDesk.Protocol;
using RemoteDesk.Transport;

AgentOptions options;
try
{
    options = AgentOptions.Parse(args);
}
catch (ArgumentException exception)
{
    Console.Error.WriteLine(exception.Message);
    Console.Error.WriteLine("Usage: RemoteDesk.Agent [--server URL] [--device-id ID --device-secret SECRET] [--bootstrap-token TOKEN] [--download-directory PATH] [--auto-approve-files]");
    return 2;
}

using var shutdown = new CancellationTokenSource();
Console.CancelKeyPress += (_, eventArgs) =>
{
    eventArgs.Cancel = true;
    shutdown.Cancel();
};

var platform = PlatformAdapterFactory.Create();
var capabilities = await platform.ProbeAsync(shutdown.Token);
var availablePermissions = SessionPermission.None;
if (capabilities.ScreenCaptureAvailable)
{
    availablePermissions |= SessionPermission.ViewScreen;
}
if (capabilities.InputControlAvailable && capabilities.ScreenCaptureAvailable)
{
    availablePermissions |= SessionPermission.ControlInput;
}
if (capabilities.FileTransferAvailable && options.DownloadDirectory is not null)
{
    availablePermissions |= SessionPermission.TransferFiles;
}
if (capabilities.RebootAvailable)
{
    availablePermissions |= SessionPermission.RebootDevice;
}

using var httpClient = new HttpClient { BaseAddress = options.Server };
var api = new SignalingApiClient(httpClient);
var deviceId = options.DeviceId;
var deviceSecret = options.DeviceSecret;

if (string.IsNullOrWhiteSpace(deviceId) || string.IsNullOrWhiteSpace(deviceSecret))
{
    if (string.IsNullOrWhiteSpace(options.BootstrapToken))
    {
        Console.Error.WriteLine("Provide existing device credentials or --bootstrap-token for first enrollment.");
        return 2;
    }

    var enrollment = await api.EnrollAsync(
        options.BootstrapToken,
        new DeviceEnrollmentRequest(options.DeviceName, capabilities.Platform),
        shutdown.Token);
    deviceId = enrollment.DeviceId;
    deviceSecret = enrollment.DeviceSecret;
    Console.WriteLine("Enrollment succeeded. Store these values in an OS-protected secret store:");
    Console.WriteLine($"REMOTEDESK_DEVICE_ID={deviceId}");
    Console.WriteLine($"REMOTEDESK_DEVICE_SECRET={deviceSecret}");
}

Console.WriteLine($"RemoteDesk agent {deviceId} on {capabilities.Platform}");
Console.WriteLine($"Available permissions: {availablePermissions}");
foreach (var requirement in capabilities.Requirements)
{
    Console.WriteLine($"- {requirement}");
}

var activeSessions = new HashSet<Task>();
var handledRequests = new HashSet<string>(StringComparer.Ordinal);

try
{
    while (!shutdown.IsCancellationRequested)
    {
        await api.HeartbeatAsync(
            deviceId,
            deviceSecret,
            new HeartbeatRequest("0.1.0", capabilities),
            shutdown.Token);

        var pending = await api.GetPendingSessionsAsync(deviceId, deviceSecret, shutdown.Token);
        foreach (var request in pending.Where(item => handledRequests.Add(item.SessionId)))
        {
            var grantable = request.RequestedPermissions & availablePermissions;
            var approve = options.AutoApproveFiles
                && request.RequestedPermissions == SessionPermission.TransferFiles
                && grantable == SessionPermission.TransferFiles;

            if (!approve && !Console.IsInputRedirected)
            {
                Console.WriteLine();
                Console.WriteLine($"Connection request {request.SessionId}");
                Console.WriteLine($"Operator: {request.OperatorName}");
                Console.WriteLine($"Requested: {request.RequestedPermissions}");
                Console.WriteLine($"Grantable: {grantable}");
                Console.Write("Approve grantable permissions? [y/N] ");
                approve = string.Equals(Console.ReadLine()?.Trim(), "y", StringComparison.OrdinalIgnoreCase);
            }

            approve &= grantable != SessionPermission.None;
            var decision = await api.ApproveSessionAsync(
                deviceId,
                deviceSecret,
                request.SessionId,
                new ApproveSessionRequest(approve, approve ? grantable : SessionPermission.None),
                shutdown.Token);

            if (!approve || decision.AgentTicket is null)
            {
                Console.WriteLine($"Declined session {request.SessionId}.");
                continue;
            }

            Console.WriteLine($"Approved session {request.SessionId} with {grantable}.");
            var sessionTask = RunSessionAsync(
                api,
                platform,
                deviceId,
                request,
                grantable,
                decision.AgentTicket,
                options.DownloadDirectory,
                shutdown.Token);
            activeSessions.Add(sessionTask);
        }

        activeSessions.RemoveWhere(task => task.IsCompleted);
        await Task.Delay(TimeSpan.FromSeconds(3), shutdown.Token);
    }
}
catch (OperationCanceledException) when (shutdown.IsCancellationRequested)
{
    // Normal Ctrl+C shutdown.
}
finally
{
    await Task.WhenAll(activeSessions);
}

return 0;

static async Task RunSessionAsync(
    SignalingApiClient api,
    IRemotePlatformAdapter platform,
    string deviceId,
    PendingSessionRequest request,
    SessionPermission grantedPermissions,
    string ticket,
    string? downloadDirectory,
    CancellationToken cancellationToken)
{
    try
    {
        using var socket = await api.ConnectSessionAsync(request.SessionId, ticket, cancellationToken);
        var session = new AgentSession(
            deviceId,
            request,
            grantedPermissions,
            platform,
            downloadDirectory);
        await session.RunAsync(socket, cancellationToken);
    }
    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
    {
        // Application shutdown.
    }
    catch (Exception exception)
    {
        Console.Error.WriteLine($"Session {request.SessionId} ended: {exception.Message}");
    }
}
