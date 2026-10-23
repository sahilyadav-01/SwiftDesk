using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text.Json;
using RemoteDesk.Core;
using RemoteDesk.Security;

namespace RemoteDesk.PrivilegedHelper;

public sealed class PrivilegedRebootIpcServer(
    string pipeName,
    PrivilegedRebootEngine engine)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly string _pipeName = pipeName;
    private readonly PrivilegedRebootEngine _engine = engine ?? throw new ArgumentNullException(nameof(engine));

    public async Task RunAsync(CancellationToken cancellationToken = default)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            NamedPipeServerStream server;
            try
            {
                server = new NamedPipeServerStream(
                    _pipeName,
                    PipeDirection.InOut,
                    NamedPipeServerStream.MaxAllowedServerInstances,
                    PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous);
            }
            catch (Exception) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }

            try
            {
                await server.WaitForConnectionAsync(cancellationToken);
                _ = HandleConnectionAsync(server, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                server.Dispose();
                break;
            }
            catch
            {
                server.Dispose();
            }
        }
    }

    private async Task HandleConnectionAsync(NamedPipeServerStream stream, CancellationToken cancellationToken)
    {
        using (stream)
        {
            try
            {
                var request = await JsonSerializer.DeserializeAsync<PrivilegedRebootRequest>(
                    stream,
                    JsonOptions,
                    cancellationToken);

                PrivilegedRebootResponse response;
                if (request is null)
                {
                    response = PrivilegedRebootResponse.Failed("invalid_json", "Failed to deserialize reboot request.");
                }
                else
                {
                    response = _engine.ProcessRequest(request);
                }

                await JsonSerializer.SerializeAsync(stream, response, JsonOptions, cancellationToken);
                await stream.FlushAsync(cancellationToken);
            }
            catch
            {
                // Silently handle client disconnects to preserve daemon stability
            }
        }
    }
}

public sealed class PrivilegedRebootClient : IPrivilegedRebootClient
{
    public const string DefaultPipeName = "swiftdesk-privileged-reboot";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly string _pipeName;
    private readonly byte[] _secretKey;
    private readonly TimeProvider _timeProvider;

    public PrivilegedRebootClient(
        ReadOnlySpan<byte> secretKey,
        string pipeName = DefaultPipeName,
        TimeProvider? timeProvider = null)
    {
        _secretKey = secretKey.ToArray();
        _pipeName = pipeName;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public bool IsAvailable => _secretKey.Length >= 32;

    public async ValueTask<PrivilegedRebootResponse> RequestRebootAsync(
        string sessionId,
        string reason,
        CancellationToken cancellationToken = default)
    {
        if (!IsAvailable)
        {
            return PrivilegedRebootResponse.Failed("not_configured", "Privileged reboot helper secret key is not configured.");
        }

        var nonce = Base64Url.Encode(RandomNumberGenerator.GetBytes(16));
        var timestamp = _timeProvider.GetUtcNow().ToUnixTimeMilliseconds();
        var signature = PrivilegedRebootEngine.ComputeSignature(_secretKey, sessionId, timestamp, nonce, reason);

        var request = new PrivilegedRebootRequest(
            sessionId,
            reason,
            timestamp,
            nonce,
            signature);

        using var clientStream = new NamedPipeClientStream(".", _pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);

        try
        {
            await clientStream.ConnectAsync(3000, cancellationToken);
            await JsonSerializer.SerializeAsync(clientStream, request, JsonOptions, cancellationToken);
            await clientStream.FlushAsync(cancellationToken);

            var response = await JsonSerializer.DeserializeAsync<PrivilegedRebootResponse>(
                clientStream,
                JsonOptions,
                cancellationToken);

            return response ?? PrivilegedRebootResponse.Failed("empty_response", "Privileged reboot helper returned empty response.");
        }
        catch (TimeoutException)
        {
            return PrivilegedRebootResponse.Failed("ipc_timeout", "Privileged reboot helper service is not running or timed out.");
        }
        catch (IOException exception)
        {
            return PrivilegedRebootResponse.Failed("ipc_error", $"Failed to communicate with privileged reboot helper: {exception.Message}");
        }
    }
}
