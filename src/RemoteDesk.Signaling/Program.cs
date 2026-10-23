using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;
using RemoteDesk.Core;
using RemoteDesk.Protocol;
using RemoteDesk.Security;
using RemoteDesk.Signaling;

var builder = WebApplication.CreateBuilder(args);

builder.Services
    .AddOptions<RemoteDeskServerOptions>()
    .Bind(builder.Configuration.GetSection(RemoteDeskServerOptions.SectionName))
    .Validate(options => options.SigningKey.Length >= 32, "SigningKey must contain at least 32 characters.")
    .Validate(options => options.RequestLifetimeMinutes is > 0 and <= 30, "Request lifetime must be 1-30 minutes.")
    .Validate(options => options.TicketLifetimeMinutes is > 0 and <= 60, "Ticket lifetime must be 1-60 minutes.")
    .ValidateOnStart();

builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton(serviceProvider =>
{
    var options = serviceProvider.GetRequiredService<IOptions<RemoteDeskServerOptions>>().Value;
    return options;
});
builder.Services.AddSingleton(serviceProvider =>
{
    var options = serviceProvider.GetRequiredService<RemoteDeskServerOptions>();
    return new SessionTokenService(options.SigningKey, serviceProvider.GetRequiredService<TimeProvider>());
});
builder.Services.AddSingleton<DeviceRegistry>();
builder.Services.AddSingleton<SessionRegistry>();
builder.Services.AddSingleton<SessionRelayHub>();

var app = builder.Build();
var serverOptions = app.Services.GetRequiredService<RemoteDeskServerOptions>();

if (!app.Environment.IsDevelopment()
    && (serverOptions.BootstrapToken.StartsWith("local-", StringComparison.Ordinal)
        || serverOptions.OperatorKey.StartsWith("local-", StringComparison.Ordinal)
        || serverOptions.SigningKey.StartsWith("local-", StringComparison.Ordinal)))
{
    throw new InvalidOperationException("Development credentials cannot be used outside Development.");
}

app.UseWebSockets(new WebSocketOptions
{
    KeepAliveInterval = TimeSpan.FromSeconds(20),
    AllowedOrigins = { },
});

app.MapGet("/health", () => Results.Ok(new { status = "ok" }));

app.MapPost("/api/devices/enroll", (
    HttpContext context,
    DeviceEnrollmentRequest request,
    DeviceRegistry devices) =>
{
    if (!MatchesSecret(context.Request.Headers["X-Bootstrap-Token"], serverOptions.BootstrapToken))
    {
        return Results.Unauthorized();
    }

    try
    {
        ProtocolValidation.RequireText(request.Name, ProtocolValidation.MaxDeviceNameLength, nameof(request.Name));
        ProtocolValidation.RequireText(request.Platform, 80, nameof(request.Platform));
        var enrolled = devices.Enroll(request.Name.Trim(), request.Platform.Trim());
        return Results.Ok(new DeviceEnrollmentResponse(enrolled.DeviceId, enrolled.DeviceSecret));
    }
    catch (ArgumentException exception)
    {
        return Results.BadRequest(new ErrorResponse("invalid_request", exception.Message));
    }
});

app.MapPost("/api/devices/{deviceId}/heartbeat", (
    HttpContext context,
    string deviceId,
    HeartbeatRequest request,
    DeviceRegistry devices) =>
{
    if (!devices.Authenticate(deviceId, context.Request.Headers["X-Device-Secret"]))
    {
        return Results.Unauthorized();
    }

    if (string.IsNullOrWhiteSpace(request.AgentVersion) || request.AgentVersion.Length > 40)
    {
        return Results.BadRequest(new ErrorResponse("invalid_request", "Agent version is invalid."));
    }

    devices.UpdateHeartbeat(deviceId, request.AgentVersion, request.Capabilities);
    return Results.NoContent();
});

app.MapGet("/api/devices/{deviceId}/requests", (
    HttpContext context,
    string deviceId,
    DeviceRegistry devices,
    SessionRegistry sessions) =>
{
    if (!devices.Authenticate(deviceId, context.Request.Headers["X-Device-Secret"]))
    {
        return Results.Unauthorized();
    }

    return Results.Ok(sessions.GetPendingForDevice(deviceId));
});

app.MapPost("/api/devices/{deviceId}/requests/{sessionId}", (
    HttpContext context,
    string deviceId,
    string sessionId,
    ApproveSessionRequest request,
    DeviceRegistry devices,
    SessionRegistry sessions) =>
{
    if (!devices.Authenticate(deviceId, context.Request.Headers["X-Device-Secret"]))
    {
        return Results.Unauthorized();
    }

    try
    {
        return Results.Ok(sessions.Decide(deviceId, sessionId, request));
    }
    catch (KeyNotFoundException exception)
    {
        return Results.NotFound(new ErrorResponse("not_found", exception.Message));
    }
    catch (UnauthorizedAccessException)
    {
        return Results.Unauthorized();
    }
    catch (Exception exception) when (exception is ArgumentException or InvalidOperationException)
    {
        return Results.BadRequest(new ErrorResponse("invalid_decision", exception.Message));
    }
});

app.MapPost("/api/sessions", (
    HttpContext context,
    CreateSessionRequest request,
    SessionRegistry sessions) =>
{
    if (!MatchesSecret(context.Request.Headers["X-Operator-Key"], serverOptions.OperatorKey))
    {
        return Results.Unauthorized();
    }

    try
    {
        var session = sessions.Create(request);
        return Results.Ok(new CreateSessionResponse(session.SessionId, session.State, session.ExpiresAt));
    }
    catch (KeyNotFoundException exception)
    {
        return Results.NotFound(new ErrorResponse("device_not_found", exception.Message));
    }
    catch (ArgumentException exception)
    {
        return Results.BadRequest(new ErrorResponse("invalid_request", exception.Message));
    }
});

app.MapGet("/api/sessions/{sessionId}", (
    HttpContext context,
    string sessionId,
    SessionRegistry sessions) =>
{
    if (!MatchesSecret(context.Request.Headers["X-Operator-Key"], serverOptions.OperatorKey))
    {
        return Results.Unauthorized();
    }

    try
    {
        return Results.Ok(sessions.GetStatus(sessionId));
    }
    catch (KeyNotFoundException exception)
    {
        return Results.NotFound(new ErrorResponse("not_found", exception.Message));
    }
});

app.Map("/ws/sessions/{sessionId}", async (
    HttpContext context,
    string sessionId,
    SessionRegistry sessions,
    SessionRelayHub relay) =>
{
    if (!context.WebSockets.IsWebSocketRequest)
    {
        context.Response.StatusCode = StatusCodes.Status400BadRequest;
        return;
    }

    var authorization = context.Request.Headers.Authorization.ToString();
    var ticket = authorization.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)
        ? authorization["Bearer ".Length..].Trim()
        : string.Empty;
    var validation = sessions.ValidateTicket(sessionId, ticket);
    if (!validation.IsValid || validation.Claims is null)
    {
        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
        return;
    }

    using var socket = await context.WebSockets.AcceptWebSocketAsync();
    try
    {
        sessions.MarkActive(sessionId);
        await relay.JoinAsync(sessionId, validation.Claims.Role, socket, context.RequestAborted);
    }
    catch (InvalidOperationException)
    {
        socket.Abort();
    }
    finally
    {
        sessions.MarkDisconnected(sessionId);
    }
});

app.Run();

static bool MatchesSecret(string? supplied, string expected)
{
    if (string.IsNullOrEmpty(supplied) || string.IsNullOrEmpty(expected))
    {
        return false;
    }

    var suppliedBytes = Encoding.UTF8.GetBytes(supplied);
    var expectedBytes = Encoding.UTF8.GetBytes(expected);
    return suppliedBytes.Length == expectedBytes.Length
        && CryptographicOperations.FixedTimeEquals(suppliedBytes, expectedBytes);
}

public partial class Program;
