using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using RemoteDesk.Core;
using RemoteDesk.Security;

namespace RemoteDesk.PrivilegedHelper;

public interface IRebootExecutor
{
    void ExecuteReboot(string sanitizedReason);
}

public sealed class SystemRebootExecutor : IRebootExecutor
{
    public void ExecuteReboot(string sanitizedReason)
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            var psi = new ProcessStartInfo
            {
                FileName = "shutdown.exe",
                Arguments = $"/r /t 2 /d p:4:1 /c \"{sanitizedReason}\"",
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using var proc = Process.Start(psi);
        }
        else if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            var psi = new ProcessStartInfo
            {
                FileName = "/sbin/shutdown",
                Arguments = $"-r +0 \"{sanitizedReason}\"",
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using var proc = Process.Start(psi);
        }
        else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            var psi = new ProcessStartInfo
            {
                FileName = "/sbin/shutdown",
                Arguments = $"-r now \"{sanitizedReason}\"",
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using var proc = Process.Start(psi);
        }
    }
}

public sealed class SimulatedRebootExecutor : IRebootExecutor
{
    public int RebootCallCount { get; private set; }
    public string? LastReason { get; private set; }

    public void ExecuteReboot(string sanitizedReason)
    {
        RebootCallCount++;
        LastReason = sanitizedReason;
    }
}

public sealed class PrivilegedRebootEngine
{
    private static readonly Regex UnsafeReasonCharsRegex = new(@"[\r\n;&|`$<>^]", RegexOptions.Compiled);
    private readonly byte[] _secretKey;
    private readonly IRebootExecutor _executor;
    private readonly TimeProvider _timeProvider;
    private readonly ConcurrentDictionary<string, long> _seenNonces = new();
    private readonly TimeSpan _cooldown;
    private long _lastRebootUnixMs;

    public PrivilegedRebootEngine(
        ReadOnlySpan<byte> secretKey,
        IRebootExecutor? executor = null,
        TimeProvider? timeProvider = null,
        TimeSpan? cooldown = null)
    {
        if (secretKey.Length < 32)
        {
            throw new ArgumentException("Machine reboot secret key must contain at least 32 bytes.", nameof(secretKey));
        }

        _secretKey = secretKey.ToArray();
        _executor = executor ?? new SystemRebootExecutor();
        _timeProvider = timeProvider ?? TimeProvider.System;
        _cooldown = cooldown ?? TimeSpan.FromSeconds(60);
    }

    public static string ComputeSignature(
        ReadOnlySpan<byte> secretKey,
        string sessionId,
        long timestampUnixMs,
        string nonce,
        string reason)
    {
        var payload = $"{sessionId}:{timestampUnixMs}:{nonce}:{reason}";
        var hash = HMACSHA256.HashData(secretKey, Encoding.UTF8.GetBytes(payload));
        return Base64Url.Encode(hash);
    }

    public PrivilegedRebootResponse ProcessRequest(PrivilegedRebootRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.SessionId))
        {
            return PrivilegedRebootResponse.Failed("invalid_session", "Session ID cannot be empty.");
        }

        if (string.IsNullOrWhiteSpace(request.Nonce) || request.Nonce.Length < 16)
        {
            return PrivilegedRebootResponse.Failed("invalid_nonce", "Nonce must be at least 16 characters.");
        }

        if (string.IsNullOrWhiteSpace(request.Signature))
        {
            return PrivilegedRebootResponse.Failed("missing_signature", "Cryptographic signature is missing.");
        }

        // 1. Sanitize Reason
        var rawReason = request.Reason ?? string.Empty;
        if (rawReason.Length > 200)
        {
            return PrivilegedRebootResponse.Failed("reason_too_long", "Reboot reason cannot exceed 200 characters.");
        }

        if (UnsafeReasonCharsRegex.IsMatch(rawReason))
        {
            return PrivilegedRebootResponse.Failed("invalid_reason", "Reboot reason contains illegal control or command injection characters.");
        }

        var sanitizedReason = rawReason.Trim();
        if (string.IsNullOrEmpty(sanitizedReason))
        {
            sanitizedReason = "SwiftDesk Operator Remote Reboot";
        }

        var nowMs = _timeProvider.GetUtcNow().ToUnixTimeMilliseconds();

        // 2. Freshness Check (30 seconds window)
        var deltaMs = Math.Abs(nowMs - request.TimestampUnixMs);
        if (deltaMs > 30_000)
        {
            return PrivilegedRebootResponse.Failed("expired_timestamp", "Request timestamp is outside allowed 30-second clock window.");
        }

        // 3. Replay Protection
        PruneOldNonces(nowMs);
        if (!_seenNonces.TryAdd(request.Nonce, request.TimestampUnixMs))
        {
            return PrivilegedRebootResponse.Failed("replay_detected", "Replay detected: nonce has already been used.");
        }

        // 4. Signature Verification
        var expectedSignature = ComputeSignature(_secretKey, request.SessionId, request.TimestampUnixMs, request.Nonce, request.Reason ?? string.Empty);
        byte[] suppliedSigBytes;
        try
        {
            suppliedSigBytes = Base64Url.Decode(request.Signature);
        }
        catch (FormatException)
        {
            return PrivilegedRebootResponse.Failed("invalid_signature_format", "Signature encoding is invalid base64url.");
        }

        var expectedSigBytes = Base64Url.Decode(expectedSignature);
        if (!CryptographicOperations.FixedTimeEquals(suppliedSigBytes, expectedSigBytes))
        {
            return PrivilegedRebootResponse.Failed("invalid_signature", "Cryptographic signature verification failed.");
        }

        // 5. Rate Limiting Check (reboot cooldown)
        var lastReboot = Interlocked.Read(ref _lastRebootUnixMs);
        if (lastReboot > 0 && (nowMs - lastReboot) < (long)_cooldown.TotalMilliseconds)
        {
            var remainingSec = (int)Math.Ceiling((((long)_cooldown.TotalMilliseconds) - (nowMs - lastReboot)) / 1000.0);
            return PrivilegedRebootResponse.Failed("rate_limited", $"Reboot cooldown active. Please wait {remainingSec}s before issuing another reboot.");
        }

        Interlocked.Exchange(ref _lastRebootUnixMs, nowMs);

        // 6. Execute Reboot
        _executor.ExecuteReboot(sanitizedReason);
        return PrivilegedRebootResponse.Succeeded($"Reboot initiated successfully for session '{request.SessionId}'.");
    }

    private void PruneOldNonces(long currentUnixMs)
    {
        // Keep nonces for 2 minutes to guarantee beyond clock skew window
        const long maxAgeMs = 120_000;
        foreach (var pair in _seenNonces)
        {
            if (currentUnixMs - pair.Value > maxAgeMs)
            {
                _seenNonces.TryRemove(pair.Key, out _);
            }
        }
    }
}
