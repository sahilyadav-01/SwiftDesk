using System.Data;
using System.Text.Json;
using Npgsql;
using RemoteDesk.Core;
using RemoteDesk.Protocol;
using RemoteDesk.Security;

namespace RemoteDesk.Signaling;

public static class PostgresSchemaInitializer
{
    public static async Task InitializeAsync(NpgsqlDataSource dataSource, CancellationToken cancellationToken = default)
    {
        const string sql = """
            CREATE TABLE IF NOT EXISTS devices (
                device_id VARCHAR(64) PRIMARY KEY,
                name VARCHAR(255) NOT NULL,
                platform VARCHAR(64) NOT NULL,
                secret_hash BYTEA NOT NULL,
                enrolled_at TIMESTAMPTZ NOT NULL,
                last_seen_at TIMESTAMPTZ NULL,
                agent_version VARCHAR(64) NULL,
                capabilities_json TEXT NULL
            );

            CREATE TABLE IF NOT EXISTS sessions (
                session_id VARCHAR(64) PRIMARY KEY,
                device_id VARCHAR(64) NOT NULL REFERENCES devices(device_id),
                operator_name VARCHAR(255) NOT NULL,
                requested_permissions INT NOT NULL,
                granted_permissions INT NOT NULL,
                state VARCHAR(32) NOT NULL,
                controller_ticket TEXT NULL,
                agent_ticket TEXT NULL,
                created_at TIMESTAMPTZ NOT NULL,
                expires_at TIMESTAMPTZ NOT NULL,
                ticket_expires_at TIMESTAMPTZ NULL
            );

            CREATE TABLE IF NOT EXISTS audit_logs (
                id BIGSERIAL PRIMARY KEY,
                event_type VARCHAR(64) NOT NULL,
                device_id VARCHAR(64) NULL,
                session_id VARCHAR(64) NULL,
                operator_name VARCHAR(255) NULL,
                details_json TEXT NULL,
                created_at TIMESTAMPTZ NOT NULL
            );

            CREATE INDEX IF NOT EXISTS idx_sessions_device_id ON sessions (device_id);
            CREATE INDEX IF NOT EXISTS idx_sessions_state ON sessions (state);
            CREATE INDEX IF NOT EXISTS idx_audit_logs_device ON audit_logs (device_id);
            CREATE INDEX IF NOT EXISTS idx_audit_logs_session ON audit_logs (session_id);
            """;

        await using var command = dataSource.CreateCommand(sql);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}

public sealed class PostgresDeviceStore(NpgsqlDataSource dataSource, TimeProvider timeProvider) : IDeviceStore
{
    private readonly NpgsqlDataSource _dataSource = dataSource;
    private readonly TimeProvider _timeProvider = timeProvider;

    public async Task<EnrolledDevice> EnrollAsync(string name, string platform, CancellationToken cancellationToken = default)
    {
        var secret = SecretHasher.CreateSecret();
        var secretHash = SecretHasher.Hash(secret);
        var enrolledAt = _timeProvider.GetUtcNow();

        for (var attempt = 0; attempt < 5; attempt++)
        {
            var id = $"dev_{Guid.NewGuid():N}";
            const string sql = """
                INSERT INTO devices (device_id, name, platform, secret_hash, enrolled_at)
                VALUES ($1, $2, $3, $4, $5)
                ON CONFLICT (device_id) DO NOTHING;
                """;

            await using var command = _dataSource.CreateCommand(sql);
            command.Parameters.AddWithValue(id);
            command.Parameters.AddWithValue(name);
            command.Parameters.AddWithValue(platform);
            command.Parameters.AddWithValue(secretHash);
            command.Parameters.AddWithValue(enrolledAt);

            var rows = await command.ExecuteNonQueryAsync(cancellationToken);
            if (rows > 0)
            {
                return new EnrolledDevice(id, secret);
            }
        }

        throw new InvalidOperationException("Unable to allocate a unique device identifier.");
    }

    public async Task<bool> ExistsAsync(string deviceId, CancellationToken cancellationToken = default)
    {
        const string sql = "SELECT 1 FROM devices WHERE device_id = $1 LIMIT 1;";
        await using var command = _dataSource.CreateCommand(sql);
        command.Parameters.AddWithValue(deviceId);

        var result = await command.ExecuteScalarAsync(cancellationToken);
        return result is not null;
    }

    public async Task<bool> AuthenticateAsync(string deviceId, string? secret, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(secret))
        {
            return false;
        }

        const string sql = "SELECT secret_hash FROM devices WHERE device_id = $1 LIMIT 1;";
        await using var command = _dataSource.CreateCommand(sql);
        command.Parameters.AddWithValue(deviceId);

        var hash = await command.ExecuteScalarAsync(cancellationToken) as byte[];
        return hash is not null && SecretHasher.Verify(secret, hash);
    }

    public async Task<bool> UpdateHeartbeatAsync(
        string deviceId,
        string agentVersion,
        PlatformCapabilityReport capabilities,
        CancellationToken cancellationToken = default)
    {
        var now = _timeProvider.GetUtcNow();
        var capsJson = JsonSerializer.Serialize(capabilities);

        const string sql = """
            UPDATE devices
            SET last_seen_at = $1, agent_version = $2, capabilities_json = $3
            WHERE device_id = $4;
            """;

        await using var command = _dataSource.CreateCommand(sql);
        command.Parameters.AddWithValue(now);
        command.Parameters.AddWithValue(agentVersion);
        command.Parameters.AddWithValue(capsJson);
        command.Parameters.AddWithValue(deviceId);

        var rows = await command.ExecuteNonQueryAsync(cancellationToken);
        return rows > 0;
    }

    public async Task<DeviceSnapshot?> FindAsync(string deviceId, CancellationToken cancellationToken = default)
    {
        const string sql = """
            SELECT device_id, name, platform, enrolled_at, last_seen_at, agent_version, capabilities_json
            FROM devices
            WHERE device_id = $1
            LIMIT 1;
            """;

        await using var command = _dataSource.CreateCommand(sql);
        command.Parameters.AddWithValue(deviceId);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        var capsJson = reader.IsDBNull(6) ? null : reader.GetString(6);
        var capabilities = capsJson is null ? null : JsonSerializer.Deserialize<PlatformCapabilityReport>(capsJson);

        return new DeviceSnapshot(
            reader.GetString(0),
            reader.GetString(1),
            reader.GetString(2),
            reader.GetFieldValue<DateTimeOffset>(3),
            reader.IsDBNull(4) ? null : reader.GetFieldValue<DateTimeOffset>(4),
            reader.IsDBNull(5) ? null : reader.GetString(5),
            capabilities);
    }

    public async Task<IReadOnlyList<DeviceSnapshot>> ListAsync(CancellationToken cancellationToken = default)
    {
        const string sql = """
            SELECT device_id, name, platform, enrolled_at, last_seen_at, agent_version, capabilities_json
            FROM devices
            ORDER BY enrolled_at DESC;
            """;

        await using var command = _dataSource.CreateCommand(sql);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);

        var results = new List<DeviceSnapshot>();
        while (await reader.ReadAsync(cancellationToken))
        {
            var capsJson = reader.IsDBNull(6) ? null : reader.GetString(6);
            var capabilities = capsJson is null ? null : JsonSerializer.Deserialize<PlatformCapabilityReport>(capsJson);

            results.Add(new DeviceSnapshot(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetFieldValue<DateTimeOffset>(3),
                reader.IsDBNull(4) ? null : reader.GetFieldValue<DateTimeOffset>(4),
                reader.IsDBNull(5) ? null : reader.GetString(5),
                capabilities));
        }

        return results;
    }
}

public sealed class PostgresSessionStore(
    NpgsqlDataSource dataSource,
    IDeviceStore devices,
    SessionTokenService tokenService,
    TimeProvider timeProvider,
    RemoteDeskServerOptions options) : ISessionStore
{
    private readonly NpgsqlDataSource _dataSource = dataSource;
    private readonly IDeviceStore _devices = devices;
    private readonly SessionTokenService _tokenService = tokenService;
    private readonly TimeProvider _timeProvider = timeProvider;
    private readonly RemoteDeskServerOptions _options = options;

    public async Task<SessionSnapshot> CreateAsync(CreateSessionRequest request, CancellationToken cancellationToken = default)
    {
        ProtocolValidation.Validate(request);
        if (!await _devices.ExistsAsync(request.DeviceId, cancellationToken))
        {
            throw new KeyNotFoundException("Target device was not found.");
        }

        var now = _timeProvider.GetUtcNow();
        var expiresAt = now.AddMinutes(_options.RequestLifetimeMinutes);

        for (var attempt = 0; attempt < 5; attempt++)
        {
            var id = $"ses_{Guid.NewGuid():N}";
            const string sql = """
                INSERT INTO sessions (
                    session_id, device_id, operator_name, requested_permissions,
                    granted_permissions, state, created_at, expires_at
                )
                VALUES ($1, $2, $3, $4, $5, $6, $7, $8)
                ON CONFLICT (session_id) DO NOTHING;
                """;

            await using var command = _dataSource.CreateCommand(sql);
            command.Parameters.AddWithValue(id);
            command.Parameters.AddWithValue(request.DeviceId);
            command.Parameters.AddWithValue(request.OperatorName);
            command.Parameters.AddWithValue((int)request.RequestedPermissions);
            command.Parameters.AddWithValue(0);
            command.Parameters.AddWithValue(SessionState.Pending.ToString());
            command.Parameters.AddWithValue(now);
            command.Parameters.AddWithValue(expiresAt);

            var rows = await command.ExecuteNonQueryAsync(cancellationToken);
            if (rows > 0)
            {
                return new SessionSnapshot(
                    id,
                    request.DeviceId,
                    request.OperatorName,
                    request.RequestedPermissions,
                    SessionPermission.None,
                    SessionState.Pending,
                    now,
                    expiresAt);
            }
        }

        throw new InvalidOperationException("Unable to allocate a unique session identifier.");
    }

    public async Task<IReadOnlyList<PendingSessionRequest>> GetPendingForDeviceAsync(string deviceId, CancellationToken cancellationToken = default)
    {
        var now = _timeProvider.GetUtcNow();
        const string sql = """
            SELECT session_id, operator_name, requested_permissions, expires_at
            FROM sessions
            WHERE device_id = $1 AND state = 'Pending' AND expires_at > $2
            ORDER BY created_at ASC;
            """;

        await using var command = _dataSource.CreateCommand(sql);
        command.Parameters.AddWithValue(deviceId);
        command.Parameters.AddWithValue(now);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var results = new List<PendingSessionRequest>();
        while (await reader.ReadAsync(cancellationToken))
        {
            results.Add(new PendingSessionRequest(
                reader.GetString(0),
                reader.GetString(1),
                (SessionPermission)reader.GetInt32(2),
                reader.GetFieldValue<DateTimeOffset>(3)));
        }

        return results;
    }

    public async Task<ApproveSessionResponse> DecideAsync(
        string deviceId,
        string sessionId,
        ApproveSessionRequest decision,
        CancellationToken cancellationToken = default)
    {
        var now = _timeProvider.GetUtcNow();

        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var tx = await connection.BeginTransactionAsync(IsolationLevel.RepeatableRead, cancellationToken);

        const string selectSql = """
            SELECT device_id, operator_name, requested_permissions, state, expires_at
            FROM sessions
            WHERE session_id = $1
            FOR UPDATE;
            """;

        string currentDeviceId;
        int requestedPerms;
        string currentState;
        DateTimeOffset expiresAt;

        await using (var selectCmd = new NpgsqlCommand(selectSql, connection, tx))
        {
            selectCmd.Parameters.AddWithValue(sessionId);
            await using var reader = await selectCmd.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken))
            {
                throw new KeyNotFoundException("Session was not found.");
            }

            currentDeviceId = reader.GetString(0);
            requestedPerms = reader.GetInt32(2);
            currentState = reader.GetString(3);
            expiresAt = reader.GetFieldValue<DateTimeOffset>(4);
        }

        if (currentDeviceId != deviceId)
        {
            throw new UnauthorizedAccessException("Session belongs to a different device.");
        }

        if (now >= expiresAt || currentState == SessionState.Expired.ToString())
        {
            const string expireSql = "UPDATE sessions SET state = 'Expired' WHERE session_id = $1;";
            await using var expireCmd = new NpgsqlCommand(expireSql, connection, tx);
            expireCmd.Parameters.AddWithValue(sessionId);
            await expireCmd.ExecuteNonQueryAsync(cancellationToken);
            await tx.CommitAsync(cancellationToken);
            throw new InvalidOperationException("Session has expired.");
        }

        if (currentState != SessionState.Pending.ToString())
        {
            throw new InvalidOperationException($"Session is already {currentState}.");
        }

        if (!decision.Approved)
        {
            const string declineSql = "UPDATE sessions SET state = 'Declined' WHERE session_id = $1;";
            await using var declineCmd = new NpgsqlCommand(declineSql, connection, tx);
            declineCmd.Parameters.AddWithValue(sessionId);
            await declineCmd.ExecuteNonQueryAsync(cancellationToken);
            await tx.CommitAsync(cancellationToken);
            return new ApproveSessionResponse(SessionState.Declined, null, null);
        }

        var requested = (SessionPermission)requestedPerms;
        if (!SessionPermissions.IsSubsetOf(decision.GrantedPermissions, requested))
        {
            throw new ArgumentException("Granted permissions must be a subset of requested permissions.");
        }

        if (decision.GrantedPermissions == SessionPermission.None)
        {
            throw new ArgumentException("Approved sessions must grant at least one permission.");
        }

        var ticketLifetime = TimeSpan.FromMinutes(_options.TicketLifetimeMinutes);
        var controllerTicket = _tokenService.IssueTicket(
            sessionId,
            deviceId,
            SessionRole.Controller,
            decision.GrantedPermissions,
            ticketLifetime);

        var agentTicket = _tokenService.IssueTicket(
            sessionId,
            deviceId,
            SessionRole.Agent,
            decision.GrantedPermissions,
            ticketLifetime);

        var ticketExpiresAt = now.Add(ticketLifetime);

        const string updateSql = """
            UPDATE sessions
            SET state = 'Approved', granted_permissions = $1, controller_ticket = $2,
                agent_ticket = $3, ticket_expires_at = $4
            WHERE session_id = $5;
            """;

        await using (var updateCmd = new NpgsqlCommand(updateSql, connection, tx))
        {
            updateCmd.Parameters.AddWithValue((int)decision.GrantedPermissions);
            updateCmd.Parameters.AddWithValue(controllerTicket);
            updateCmd.Parameters.AddWithValue(agentTicket);
            updateCmd.Parameters.AddWithValue(ticketExpiresAt);
            updateCmd.Parameters.AddWithValue(sessionId);
            await updateCmd.ExecuteNonQueryAsync(cancellationToken);
        }

        await tx.CommitAsync(cancellationToken);

        return new ApproveSessionResponse(
            SessionState.Approved,
            decision.GrantedPermissions,
            agentTicket);
    }

    public async Task<SessionStatusResponse> GetStatusAsync(string sessionId, CancellationToken cancellationToken = default)
    {
        var now = _timeProvider.GetUtcNow();
        const string sql = """
            SELECT session_id, device_id, operator_name, requested_permissions,
                   granted_permissions, state, controller_ticket, expires_at, ticket_expires_at
            FROM sessions
            WHERE session_id = $1
            LIMIT 1;
            """;

        await using var command = _dataSource.CreateCommand(sql);
        command.Parameters.AddWithValue(sessionId);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            throw new KeyNotFoundException("Session was not found.");
        }

        var stateStr = reader.GetString(5);
        var state = Enum.Parse<SessionState>(stateStr);
        var expiresAt = reader.GetFieldValue<DateTimeOffset>(7);
        var ticketExpiresAt = reader.IsDBNull(8) ? (DateTimeOffset?)null : reader.GetFieldValue<DateTimeOffset>(8);

        if (state == SessionState.Pending && now >= expiresAt)
        {
            state = SessionState.Expired;
        }
        else if (ticketExpiresAt.HasValue && now >= ticketExpiresAt.Value && state != SessionState.Active)
        {
            state = SessionState.Expired;
        }

        return new SessionStatusResponse(
            reader.GetString(0),
            state,
            (SessionPermission)reader.GetInt32(4),
            reader.IsDBNull(6) ? null : reader.GetString(6));
    }

    public async Task<bool> SetStateAsync(string sessionId, SessionState state, CancellationToken cancellationToken = default)
    {
        const string sql = "UPDATE sessions SET state = $1 WHERE session_id = $2;";
        await using var command = _dataSource.CreateCommand(sql);
        command.Parameters.AddWithValue(state.ToString());
        command.Parameters.AddWithValue(sessionId);

        var rows = await command.ExecuteNonQueryAsync(cancellationToken);
        return rows > 0;
    }
}

public sealed class PostgresAuditStore(NpgsqlDataSource dataSource, TimeProvider timeProvider) : IAuditStore
{
    private readonly NpgsqlDataSource _dataSource = dataSource;
    private readonly TimeProvider _timeProvider = timeProvider;

    public async Task RecordEventAsync(
        string eventType,
        string? deviceId,
        string? sessionId,
        string? operatorName,
        string? detailsJson = null,
        CancellationToken cancellationToken = default)
    {
        const string sql = """
            INSERT INTO audit_logs (event_type, device_id, session_id, operator_name, details_json, created_at)
            VALUES ($1, $2, $3, $4, $5, $6);
            """;

        await using var command = _dataSource.CreateCommand(sql);
        command.Parameters.AddWithValue(eventType);
        command.Parameters.AddWithValue((object?)deviceId ?? DBNull.Value);
        command.Parameters.AddWithValue((object?)sessionId ?? DBNull.Value);
        command.Parameters.AddWithValue((object?)operatorName ?? DBNull.Value);
        command.Parameters.AddWithValue((object?)detailsJson ?? DBNull.Value);
        command.Parameters.AddWithValue(_timeProvider.GetUtcNow());

        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<AuditEvent>> GetEventsAsync(
        string? deviceId = null,
        string? sessionId = null,
        int limit = 50,
        CancellationToken cancellationToken = default)
    {
        var boundedLimit = Math.Clamp(limit, 1, 500);
        var sql = """
            SELECT id, event_type, device_id, session_id, operator_name, details_json, created_at
            FROM audit_logs
            WHERE ($1 IS NULL OR device_id = $1)
              AND ($2 IS NULL OR session_id = $2)
            ORDER BY created_at DESC
            LIMIT $3;
            """;

        await using var command = _dataSource.CreateCommand(sql);
        command.Parameters.AddWithValue((object?)deviceId ?? DBNull.Value);
        command.Parameters.AddWithValue((object?)sessionId ?? DBNull.Value);
        command.Parameters.AddWithValue(boundedLimit);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var results = new List<AuditEvent>();
        while (await reader.ReadAsync(cancellationToken))
        {
            results.Add(new AuditEvent(
                reader.GetInt64(0),
                reader.GetString(1),
                reader.IsDBNull(2) ? null : reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetString(3),
                reader.IsDBNull(4) ? null : reader.GetString(4),
                reader.IsDBNull(5) ? null : reader.GetString(5),
                reader.GetFieldValue<DateTimeOffset>(6)));
        }

        return results;
    }
}
