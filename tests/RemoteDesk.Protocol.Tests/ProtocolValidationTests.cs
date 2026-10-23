using RemoteDesk.Core;
using RemoteDesk.Protocol;
using Xunit;

namespace RemoteDesk.Protocol.Tests;

public sealed class ProtocolValidationTests
{
    [Fact]
    public void ControlEnvelope_RoundTrips()
    {
        var input = new InputCommand(RemoteInputKind.PointerMove, 0.25, 0.75, null);
        var envelope = new ControlEnvelope(
            ControlMessageKind.Input,
            "ses_abc",
            42,
            ProtocolJson.Serialize(input));

        var actual = ProtocolJson.Deserialize<ControlEnvelope>(ProtocolJson.Serialize(envelope));

        Assert.Equal(envelope, actual);
        Assert.Equal(input, ProtocolJson.Deserialize<InputCommand>(actual.PayloadJson));
    }

    [Theory]
    [InlineData("../secret.txt")]
    [InlineData("folder/file.txt")]
    [InlineData("folder\\file.txt")]
    [InlineData("..")]
    public void FileOffer_RejectsUnsafeName(string fileName)
    {
        var offer = new FileTransferOffer(
            "tx_abc",
            fileName,
            10,
            new string('a', 64));

        Assert.Throws<ArgumentException>(() => ProtocolValidation.Validate(offer));
    }

    [Fact]
    public void Input_RejectsCoordinatesOutsideNormalizedRange()
    {
        var command = new InputCommand(RemoteInputKind.PointerMove, 1.1, 0.5, null);
        Assert.Throws<ArgumentOutOfRangeException>(() => ProtocolValidation.Validate(command));
    }

    [Fact]
    public void ControlInput_RequiresViewScreen()
    {
        var request = new CreateSessionRequest("dev_abc", "Operator", SessionPermission.ControlInput);
        Assert.Throws<ArgumentException>(() => ProtocolValidation.Validate(request));
    }
}

