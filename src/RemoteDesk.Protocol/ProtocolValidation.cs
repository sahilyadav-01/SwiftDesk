using System.Globalization;
using System.Text.RegularExpressions;
using RemoteDesk.Core;

namespace RemoteDesk.Protocol;

public static partial class ProtocolValidation
{
    public const int MaxDeviceNameLength = 80;
    public const int MaxOperatorNameLength = 80;
    public const int MaxReasonLength = 500;
    public const long DefaultMaxFileBytes = 2L * 1024 * 1024 * 1024;

    public static void Validate(CreateSessionRequest request)
    {
        RequireIdentifier(request.DeviceId, nameof(request.DeviceId));
        RequireText(request.OperatorName, MaxOperatorNameLength, nameof(request.OperatorName));

        if (!SessionPermissions.IsValid(request.RequestedPermissions)
            || request.RequestedPermissions == SessionPermission.None)
        {
            throw new ArgumentException("At least one known permission must be requested.", nameof(request));
        }

        if (request.RequestedPermissions.HasFlag(SessionPermission.ControlInput)
            && !request.RequestedPermissions.HasFlag(SessionPermission.ViewScreen))
        {
            throw new ArgumentException("Input control requires screen viewing.", nameof(request));
        }
    }

    public static void Validate(InputCommand command)
    {
        if (!double.IsFinite(command.NormalizedX) || command.NormalizedX is < 0 or > 1
            || !double.IsFinite(command.NormalizedY) || command.NormalizedY is < 0 or > 1)
        {
            throw new ArgumentOutOfRangeException(nameof(command), "Pointer coordinates must be finite and normalized.");
        }

        if (command.Key is { Length: > 64 })
        {
            throw new ArgumentException("Key value is too long.", nameof(command));
        }
    }

    public static void Validate(FileTransferOffer offer, long maxFileBytes = DefaultMaxFileBytes)
    {
        RequireIdentifier(offer.TransferId, nameof(offer.TransferId));
        RequireSafeFileName(offer.FileName);

        if (offer.FileSize is < 0 || offer.FileSize > maxFileBytes)
        {
            throw new ArgumentOutOfRangeException(nameof(offer), $"File exceeds the {maxFileBytes.ToString(CultureInfo.InvariantCulture)} byte limit.");
        }

        if (!Sha256Regex().IsMatch(offer.Sha256))
        {
            throw new ArgumentException("SHA-256 must be 64 hexadecimal characters.", nameof(offer));
        }
    }

    public static void RequireSafeFileName(string fileName)
    {
        RequireText(fileName, 255, nameof(fileName));

        if (fileName is "." or ".."
            || Path.IsPathRooted(fileName)
            || !string.Equals(Path.GetFileName(fileName), fileName, StringComparison.Ordinal)
            || fileName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            throw new ArgumentException("Only a plain file name is allowed.", nameof(fileName));
        }
    }

    public static void RequireIdentifier(string value, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 128 || !IdentifierRegex().IsMatch(value))
        {
            throw new ArgumentException("Identifier contains unsupported characters.", parameterName);
        }
    }

    public static void RequireText(string value, int maxLength, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > maxLength || value.Any(char.IsControl))
        {
            throw new ArgumentException($"Text must contain 1 to {maxLength} printable characters.", parameterName);
        }
    }

    [GeneratedRegex("^[A-Za-z0-9_-]+$", RegexOptions.CultureInvariant)]
    private static partial Regex IdentifierRegex();

    [GeneratedRegex("^[A-Fa-f0-9]{64}$", RegexOptions.CultureInvariant)]
    private static partial Regex Sha256Regex();
}
