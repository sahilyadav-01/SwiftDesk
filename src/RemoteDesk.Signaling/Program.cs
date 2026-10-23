using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using Npgsql;
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

var postgresConn = builder.Configuration.GetConnectionString("Postgres")
    ?? builder.Configuration[$"{RemoteDeskServerOptions.SectionName}:PostgresConnection"];

if (!string.IsNullOrWhiteSpace(postgresConn))
{
    var dataSource = NpgsqlDataSource.Create(postgresConn);
    builder.Services.AddSingleton(dataSource);
    builder.Services.AddSingleton<IDeviceStore, PostgresDeviceStore>();
    builder.Services.AddSingleton<ISessionStore, PostgresSessionStore>();
    builder.Services.AddSingleton<IAuditStore, PostgresAuditStore>();
}
else
{
    builder.Services.AddSingleton<DeviceRegistry>();
    builder.Services.AddSingleton<IDeviceStore>(sp => sp.GetRequiredService<DeviceRegistry>());
    builder.Services.AddSingleton<SessionRegistry>();
    builder.Services.AddSingleton<ISessionStore>(sp => sp.GetRequiredService<SessionRegistry>());
    builder.Services.AddSingleton<IAuditStore, InMemoryAuditStore>();
}

builder.Services.AddSingleton<SessionRelayHub>();

var app = builder.Build();
var serverOptions = app.Services.GetRequiredService<RemoteDeskServerOptions>();

if (!string.IsNullOrWhiteSpace(postgresConn))
{
    var ds = app.Services.GetRequiredService<NpgsqlDataSource>();
    await PostgresSchemaInitializer.InitializeAsync(ds);
}

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

app.MapPost("/api/devices/enroll", async (
    HttpContext context,
    DeviceEnrollmentRequest request,
    IDeviceStore devices,
    IAuditStore audit) =>
{
    if (!MatchesSecret(context.Request.Headers["X-Bootstrap-Token"], serverOptions.BootstrapToken))
    {
        return Results.Unauthorized();
    }

    try
    {
        ProtocolValidation.RequireText(request.Name, ProtocolValidation.MaxDeviceNameLength, nameof(request.Name));
        ProtocolValidation.RequireText(request.Platform, 80, nameof(request.Platform));
        var enrolled = await devices.EnrollAsync(request.Name.Trim(), request.Platform.Trim(), context.RequestAborted);
        await audit.RecordEventAsync("DeviceEnrolled", enrolled.DeviceId, null, null, request.Name.Trim(), context.RequestAborted);
        return Results.Ok(new DeviceEnrollmentResponse(enrolled.DeviceId, enrolled.DeviceSecret));
    }
    catch (ArgumentException exception)
    {
        return Results.BadRequest(new ErrorResponse("invalid_request", exception.Message));
    }
});

app.MapPost("/api/devices/{deviceId}/heartbeat", async (
    HttpContext context,
    string deviceId,
    HeartbeatRequest request,
    IDeviceStore devices) =>
{
    if (!await devices.AuthenticateAsync(deviceId, context.Request.Headers["X-Device-Secret"], context.RequestAborted))
    {
        return Results.Unauthorized();
    }

    if (string.IsNullOrWhiteSpace(request.AgentVersion) || request.AgentVersion.Length > 40)
    {
        return Results.BadRequest(new ErrorResponse("invalid_request", "Agent version is invalid."));
    }

    await devices.UpdateHeartbeatAsync(deviceId, request.AgentVersion, request.Capabilities, context.RequestAborted);
    return Results.NoContent();
});

app.MapGet("/api/devices/{deviceId}/requests", async (
    HttpContext context,
    string deviceId,
    IDeviceStore devices,
    ISessionStore sessions) =>
{
    if (!await devices.AuthenticateAsync(deviceId, context.Request.Headers["X-Device-Secret"], context.RequestAborted))
    {
        return Results.Unauthorized();
    }

    var pending = await sessions.GetPendingForDeviceAsync(deviceId, context.RequestAborted);
    return Results.Ok(pending);
});

app.MapPost("/api/devices/{deviceId}/requests/{sessionId}", async (
    HttpContext context,
    string deviceId,
    string sessionId,
    ApproveSessionRequest request,
    IDeviceStore devices,
    ISessionStore sessions,
    IAuditStore audit) =>
{
    if (!await devices.AuthenticateAsync(deviceId, context.Request.Headers["X-Device-Secret"], context.RequestAborted))
    {
        return Results.Unauthorized();
    }

    try
    {
        var decision = await sessions.DecideAsync(deviceId, sessionId, request, context.RequestAborted);
        var eventType = request.Approved ? "SessionApproved" : "SessionDeclined";
        await audit.RecordEventAsync(eventType, deviceId, sessionId, null, $"Permissions: {request.GrantedPermissions}", context.RequestAborted);
        return Results.Ok(decision);
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

app.MapPost("/api/sessions", async (
    HttpContext context,
    CreateSessionRequest request,
    ISessionStore sessions,
    IAuditStore audit) =>
{
    if (!MatchesSecret(context.Request.Headers["X-Operator-Key"], serverOptions.OperatorKey))
    {
        return Results.Unauthorized();
    }

    try
    {
        var session = await sessions.CreateAsync(request, context.RequestAborted);
        await audit.RecordEventAsync("SessionCreated", request.DeviceId, session.SessionId, request.OperatorName, $"Requested: {request.RequestedPermissions}", context.RequestAborted);
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

app.MapGet("/api/sessions/{sessionId}", async (
    HttpContext context,
    string sessionId,
    ISessionStore sessions) =>
{
    if (!MatchesSecret(context.Request.Headers["X-Operator-Key"], serverOptions.OperatorKey))
    {
        return Results.Unauthorized();
    }

    try
    {
        var status = await sessions.GetStatusAsync(sessionId, context.RequestAborted);
        return Results.Ok(status);
    }
    catch (KeyNotFoundException exception)
    {
        return Results.NotFound(new ErrorResponse("not_found", exception.Message));
    }
});

app.MapGet("/api/audit", async (
    HttpContext context,
    IAuditStore audit,
    [FromQuery] string? deviceId,
    [FromQuery] string? sessionId,
    [FromQuery] int limit = 50) =>
{
    if (!MatchesSecret(context.Request.Headers["X-Operator-Key"], serverOptions.OperatorKey))
    {
        return Results.Unauthorized();
    }

    var events = await audit.GetEventsAsync(deviceId, sessionId, limit, context.RequestAborted);
    return Results.Ok(events);
});

app.Map("/ws/sessions/{sessionId}", async (
    HttpContext context,
    string sessionId,
    ISessionStore sessions,
    SessionTokenService tokenService,
    SessionRelayHub relay,
    IAuditStore audit) =>
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

    var validation = tokenService.Validate(ticket, null, sessionId);
    if (!validation.IsValid || validation.Claims is null)
    {
        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
        return;
    }

    using var socket = await context.WebSockets.AcceptWebSocketAsync();
    try
    {
        await sessions.SetStateAsync(sessionId, SessionState.Active, context.RequestAborted);
        await audit.RecordEventAsync("SessionConnected", validation.Claims.DeviceId, sessionId, null, $"Role: {validation.Claims.Role}");
        await relay.JoinAsync(sessionId, validation.Claims.Role, socket, context.RequestAborted);
    }
    catch (InvalidOperationException)
    {
        socket.Abort();
    }
    finally
    {
        await sessions.SetStateAsync(sessionId, SessionState.Disconnected);
        await audit.RecordEventAsync("SessionDisconnected", validation.Claims.DeviceId, sessionId, null, $"Role: {validation.Claims.Role}");
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
