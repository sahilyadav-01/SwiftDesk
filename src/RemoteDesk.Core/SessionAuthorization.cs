namespace RemoteDesk.Core;

public static class SessionAuthorization
{
    public static SessionPermission Grant(
        SessionPermission requested,
        SessionPermission devicePolicy,
        bool attendedApproval,
        bool unattendedAccessEnabled)
    {
        if (!SessionPermissions.IsValid(requested) || requested == SessionPermission.None)
        {
            throw new ArgumentOutOfRangeException(nameof(requested), "At least one known permission is required.");
        }

        if (!attendedApproval && !unattendedAccessEnabled)
        {
            return SessionPermission.None;
        }

        var granted = requested & devicePolicy;

        // Reboot is intentionally never implied by broad unattended access.
        if (!devicePolicy.HasFlag(SessionPermission.RebootDevice))
        {
            granted &= ~SessionPermission.RebootDevice;
        }

        return granted;
    }

    public static void Demand(SessionPermission granted, SessionPermission required)
    {
        if (!SessionPermissions.IsSubsetOf(required, granted))
        {
            throw new UnauthorizedAccessException($"The session does not grant {required}.");
        }
    }
}
