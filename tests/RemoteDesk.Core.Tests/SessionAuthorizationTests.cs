using RemoteDesk.Core;
using Xunit;

namespace RemoteDesk.Core.Tests;

public sealed class SessionAuthorizationTests
{
    [Fact]
    public void Grant_ReturnsIntersectionOfRequestAndPolicy()
    {
        var result = SessionAuthorization.Grant(
            SessionPermission.ViewScreen | SessionPermission.ControlInput | SessionPermission.RebootDevice,
            SessionPermission.ViewScreen | SessionPermission.ControlInput,
            attendedApproval: true,
            unattendedAccessEnabled: false);

        Assert.Equal(SessionPermission.ViewScreen | SessionPermission.ControlInput, result);
    }

    [Fact]
    public void Grant_DeniesUnattendedSessionWhenOwnerDidNotEnableIt()
    {
        var result = SessionAuthorization.Grant(
            SessionPermission.ViewScreen,
            SessionPermission.ViewScreen,
            attendedApproval: false,
            unattendedAccessEnabled: false);

        Assert.Equal(SessionPermission.None, result);
    }

    [Fact]
    public void Demand_RejectsPermissionThatWasNotGranted()
    {
        Assert.Throws<UnauthorizedAccessException>(() =>
            SessionAuthorization.Demand(SessionPermission.ViewScreen, SessionPermission.ControlInput));
    }
}

