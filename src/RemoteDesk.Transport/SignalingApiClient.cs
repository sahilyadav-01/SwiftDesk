using System.Net;
using System.Net.Http.Json;
using System.Net.WebSockets;
using RemoteDesk.Protocol;

namespace RemoteDesk.Transport;

public sealed class SignalingApiException(HttpStatusCode statusCode, string message) : Exception(message)
{
    public HttpStatusCode StatusCode { get; } = statusCode;
}

public sealed class SignalingApiClient(HttpClient httpClient)
{
    private readonly HttpClient _httpClient = httpClient;

    public Task<DeviceEnrollmentResponse> EnrollAsync(
        string bootstrapToken,
        DeviceEnrollmentRequest request,
        CancellationToken cancellationToken = default) =>
        SendAsync<DeviceEnrollmentRequest, DeviceEnrollmentResponse>(
            HttpMethod.Post,
            "/api/devices/enroll",
            request,
            "X-Bootstrap-Token",
            bootstrapToken,
            cancellationToken);

    public Task HeartbeatAsync(
        string deviceId,
        string deviceSecret,
        HeartbeatRequest request,
        CancellationToken cancellationToken = default) =>
        SendWithoutResponseAsync(
            HttpMethod.Post,
            $"/api/devices/{Uri.EscapeDataString(deviceId)}/heartbeat",
            request,
            "X-Device-Secret",
            deviceSecret,
            cancellationToken);

    public Task<IReadOnlyList<PendingSessionRequest>> GetPendingSessionsAsync(
        string deviceId,
        string deviceSecret,
        CancellationToken cancellationToken = default) =>
        SendAsync<object, IReadOnlyList<PendingSessionRequest>>(
            HttpMethod.Get,
            $"/api/devices/{Uri.EscapeDataString(deviceId)}/requests",
            null,
            "X-Device-Secret",
            deviceSecret,
            cancellationToken);

    public Task<ApproveSessionResponse> ApproveSessionAsync(
        string deviceId,
        string deviceSecret,
        string sessionId,
        ApproveSessionRequest request,
        CancellationToken cancellationToken = default) =>
        SendAsync<ApproveSessionRequest, ApproveSessionResponse>(
            HttpMethod.Post,
            $"/api/devices/{Uri.EscapeDataString(deviceId)}/requests/{Uri.EscapeDataString(sessionId)}",
            request,
            "X-Device-Secret",
            deviceSecret,
            cancellationToken);

    public Task<CreateSessionResponse> CreateSessionAsync(
        string operatorTokenOrKey,
        CreateSessionRequest request,
        CancellationToken cancellationToken = default)
    {
        var (header, value) = ResolveOperatorHeader(operatorTokenOrKey);
        return SendAsync<CreateSessionRequest, CreateSessionResponse>(
            HttpMethod.Post,
            "/api/sessions",
            request,
            header,
            value,
            cancellationToken);
    }

    public Task<SessionStatusResponse> GetSessionStatusAsync(
        string operatorTokenOrKey,
        string sessionId,
        CancellationToken cancellationToken = default)
    {
        var (header, value) = ResolveOperatorHeader(operatorTokenOrKey);
        return SendAsync<object, SessionStatusResponse>(
            HttpMethod.Get,
            $"/api/sessions/{Uri.EscapeDataString(sessionId)}",
            null,
            header,
            value,
            cancellationToken);
    }

    private static (string Header, string Value) ResolveOperatorHeader(string tokenOrKey)
    {
        if (string.IsNullOrWhiteSpace(tokenOrKey))
        {
            return ("X-Operator-Key", string.Empty);
        }

        if (tokenOrKey.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            return ("Authorization", tokenOrKey);
        }

        if (tokenOrKey.Contains('.'))
        {
            return ("Authorization", $"Bearer {tokenOrKey}");
        }

        return ("X-Operator-Key", tokenOrKey);
    }

    public async Task<ClientWebSocket> ConnectSessionAsync(
        string sessionId,
        string ticket,
        CancellationToken cancellationToken = default)
    {
        var baseUri = _httpClient.BaseAddress
            ?? throw new InvalidOperationException("HttpClient.BaseAddress is required.");
        var builder = new UriBuilder(new Uri(baseUri, $"/ws/sessions/{Uri.EscapeDataString(sessionId)}"))
        {
            Scheme = baseUri.Scheme == Uri.UriSchemeHttps ? "wss" : "ws",
        };

        var socket = new ClientWebSocket();
        socket.Options.SetRequestHeader("Authorization", $"Bearer {ticket}");
        try
        {
            await socket.ConnectAsync(builder.Uri, cancellationToken);
            return socket;
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    private async Task SendWithoutResponseAsync<TRequest>(
        HttpMethod method,
        string path,
        TRequest? body,
        string headerName,
        string headerValue,
        CancellationToken cancellationToken)
    {
        using var request = CreateRequest(method, path, body, headerName, headerValue);
        using var response = await _httpClient.SendAsync(request, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
    }

    private async Task<TResponse> SendAsync<TRequest, TResponse>(
        HttpMethod method,
        string path,
        TRequest? body,
        string headerName,
        string headerValue,
        CancellationToken cancellationToken)
    {
        using var request = CreateRequest(method, path, body, headerName, headerValue);
        using var response = await _httpClient.SendAsync(request, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        return await response.Content.ReadFromJsonAsync<TResponse>(ProtocolJson.Options, cancellationToken)
            ?? throw new InvalidDataException("The signaling server returned an empty response.");
    }

    private static HttpRequestMessage CreateRequest<TRequest>(
        HttpMethod method,
        string path,
        TRequest? body,
        string headerName,
        string headerValue)
    {
        var request = new HttpRequestMessage(method, path);
        request.Headers.TryAddWithoutValidation(headerName, headerValue);
        if (body is not null)
        {
            request.Content = JsonContent.Create(body, options: ProtocolJson.Options);
        }

        return request;
    }

    private static async Task EnsureSuccessAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        var error = await response.Content.ReadFromJsonAsync<ErrorResponse>(ProtocolJson.Options, cancellationToken);
        throw new SignalingApiException(
            response.StatusCode,
            error?.Message ?? $"Signaling request failed with HTTP {(int)response.StatusCode}.");
    }
}
