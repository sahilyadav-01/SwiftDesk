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
var loadedPersistedIdentity = false;

if (string.IsNullOrWhiteSpace(deviceId) && string.IsNullOrWhiteSpace(deviceSecret))
{
    var persistedIdentity = DeviceIdentityStore.Load();
    if (persistedIdentity is not null)
    {
        deviceId = persistedIdentity.DeviceId;
        deviceSecret = persistedIdentity.DeviceSecret;
        loadedPersistedIdentity = true;
    }
}

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
    PersistIdentity(deviceId, deviceSecret);
    Console.WriteLine("Enrollment succeeded. This device now has a persistent SwiftDesk ID.");
}

if (loadedPersistedIdentity)
{
    try
    {
        await api.HeartbeatAsync(
            deviceId,
            deviceSecret,
            new HeartbeatRequest("0.1.0", capabilities),
            shutdown.Token);
    }
    catch (SignalingApiException exception) when (
        exception.StatusCode is System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.NotFound
        && !string.IsNullOrWhiteSpace(options.BootstrapToken))
    {
        Console.WriteLine("The saved ID is not registered on this server. Enrolling this device again…");
        var enrollment = await api.EnrollAsync(
            options.BootstrapToken,
            new DeviceEnrollmentRequest(options.DeviceName, capabilities.Platform),
            shutdown.Token);
        deviceId = enrollment.DeviceId;
        deviceSecret = enrollment.DeviceSecret;
        PersistIdentity(deviceId, deviceSecret);
    }
}

Console.WriteLine($"SwiftDesk ID: {deviceId}");
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
        try
        {
            await api.HeartbeatAsync(
                deviceId,
                deviceSecret,
                new HeartbeatRequest("0.1.0", capabilities),
                shutdown.Token);
        }
        catch (SignalingApiException exception) when (
            exception.StatusCode is System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.NotFound
            && !string.IsNullOrWhiteSpace(options.BootstrapToken))
        {
            // A non-persistent signaling node can lose its device registry after a
            // restart. Recover without terminating the Agent so the host becomes
            // reachable again and the Client can display its newly issued address.
            Console.WriteLine("The server no longer recognizes this device. Re-enrolling automaticallyâ€¦");
            var enrollment = await api.EnrollAsync(
                options.BootstrapToken,
                new DeviceEnrollmentRequest(options.DeviceName, capabilities.Platform),
                shutdown.Token);
            deviceId = enrollment.DeviceId;
            deviceSecret = enrollment.DeviceSecret;
            PersistIdentity(deviceId, deviceSecret);
            handledRequests.Clear();
            Console.WriteLine($"SwiftDesk ID refreshed: {deviceId}");
            continue;
        }

        var pending = await api.GetPendingSessionsAsync(deviceId, deviceSecret, shutdown.Token);
        foreach (var request in pending.Where(item => handledRequests.Add(item.SessionId)))
        {
            var grantable = request.RequestedPermissions & availablePermissions;
            var approve = options.AutoApproveFiles
                && request.RequestedPermissions == SessionPermission.TransferFiles
                && grantable == SessionPermission.TransferFiles;

            if (!approve)
            {
                approve = UserConsentPrompt.Request(request, grantable);
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

static void PersistIdentity(string deviceId, string deviceSecret)
{
    try
    {
        DeviceIdentityStore.Save(new LocalDeviceIdentity(deviceId, deviceSecret));
        Console.WriteLine($"Device identity saved for this user: {DeviceIdentityStore.GetIdentityPath()}");
    }
    catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
    {
        Console.Error.WriteLine($"Warning: device identity could not be saved: {exception.Message}");
        Console.Error.WriteLine("Store REMOTEDESK_DEVICE_ID and REMOTEDESK_DEVICE_SECRET securely before restarting the agent.");
        Console.WriteLine($"REMOTEDESK_DEVICE_ID={deviceId}");
        Console.WriteLine($"REMOTEDESK_DEVICE_SECRET={deviceSecret}");
    }
}

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
