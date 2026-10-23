using RemoteDesk.Core;
using RemoteDesk.Security;
using Xunit;

namespace RemoteDesk.Security.Tests;

public sealed class SessionTokenServiceTests
{
    private static readonly byte[] Key = Enumerable.Range(0, 32).Select(value => (byte)value).ToArray();

    [Fact]
    public void IssuedTicket_ValidatesForExpectedSessionAndRole()
    {
        var clock = new ManualTimeProvider(new DateTimeOffset(2026, 10, 23, 12, 0, 0, TimeSpan.Zero));
        var service = new SessionTokenService(Key, clock);
        var token = service.Issue(
            "ses_abc",
            "dev_abc",
            SessionRole.Controller,
            SessionPermission.ViewScreen,
            TimeSpan.FromMinutes(5));

        var result = service.Validate(token, SessionRole.Controller, "ses_abc");

        Assert.True(result.IsValid);
        Assert.Equal(SessionPermission.ViewScreen, result.Claims?.Permissions);
    }

    [Fact]
    public void TamperedTicket_IsRejected()
    {
        var service = new SessionTokenService(Key);
        var token = service.Issue(
            "ses_abc",
            "dev_abc",
            SessionRole.Agent,
            SessionPermission.TransferFiles,
            TimeSpan.FromMinutes(5));
        var replacement = token[^1] == 'A' ? 'B' : 'A';
        var tampered = token[..^1] + replacement;

        Assert.False(service.Validate(tampered).IsValid);
    }

    [Fact]
    public void ExpiredTicket_IsRejected()
    {
        var clock = new ManualTimeProvider(new DateTimeOffset(2026, 10, 23, 12, 0, 0, TimeSpan.Zero));
        var service = new SessionTokenService(Key, clock);
        var token = service.Issue(
            "ses_abc",
            "dev_abc",
            SessionRole.Agent,
            SessionPermission.TransferFiles,
            TimeSpan.FromMinutes(1));

        clock.Advance(TimeSpan.FromMinutes(2));

        Assert.False(service.Validate(token).IsValid);
    }

    private sealed class ManualTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        private DateTimeOffset _utcNow = utcNow;

        public override DateTimeOffset GetUtcNow() => _utcNow;

        public void Advance(TimeSpan amount) => _utcNow = _utcNow.Add(amount);
    }
}
