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

    [Fact]
    public void RtcSessionDescription_ValidatesOffer()
    {
        var validOffer = new RtcSessionDescriptionMessage("offer", "v=0\r\no=- 0 0 IN IP4 127.0.0.1\r\ns=-\r\n");
        ProtocolValidation.Validate(validOffer);

        var emptyOffer = new RtcSessionDescriptionMessage("offer", " ");
        Assert.Throws<ArgumentException>(() => ProtocolValidation.Validate(emptyOffer));
    }

    [Fact]
    public void RtcIceCandidate_ValidatesCandidate()
    {
        var validCandidate = new RtcIceCandidateMessage("candidate:1 1 UDP 2122252543 192.168.1.100 50000 typ host", "video", 0);
        ProtocolValidation.Validate(validCandidate);

        var emptyCandidate = new RtcIceCandidateMessage("  ", null, null);
        Assert.Throws<ArgumentException>(() => ProtocolValidation.Validate(emptyCandidate));
    }

    [Fact]
    public void ScreenFrame_RoundTripsJson()
    {
        var frame = new ScreenFrameMessage(10, 1920, 1080, "Jpeg", "ZmFrZSBkYXRh", 1700000000000);
        var json = ProtocolJson.Serialize(frame);
        var deserialized = ProtocolJson.Deserialize<ScreenFrameMessage>(json);

        Assert.Equal(frame.Sequence, deserialized.Sequence);
        Assert.Equal(frame.Width, deserialized.Width);
        Assert.Equal(frame.Height, deserialized.Height);
        Assert.Equal(frame.PixelFormat, deserialized.PixelFormat);
        Assert.Equal(frame.Base64Data, deserialized.Base64Data);
    }
}

