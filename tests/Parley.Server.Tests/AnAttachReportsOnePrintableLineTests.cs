using Grpc.Core;
using Xunit;
using Wire = Parley.Transport;

namespace Parley.Server.Tests;

/// <summary>
/// What an attach says about itself is refused when it is not one line of printable text (SPEC.md §4.1.1).
/// </summary>
/// <remarks>
/// Lens 2 run 135: an agent edited its own launch configuration so <c>PARLEY_WORKSPACE</c> held escape sequences, the
/// broker stored them, and the person's <c>parley registry</c> printed them raw — a clipboard write and a cursor moved up
/// over what they were reading. The refusal is at the attach, so nothing is stored for any surface to print.
/// </remarks>
[Integration]
public sealed class AnAttachReportsOnePrintableLineTests
{
    [Theory]
    [InlineData("workspace", "C:/repos/orders\u001b]52;c;aGk=\u0007", "U+001B")]
    [InlineData("adapter", "mcp\u2028orders@alpha  active", "U+2028")]
    [InlineData("session reference", "resume\u202Eredro", "U+202E")]
    public async Task An_attach_carrying_an_unprintable_character_is_refused_and_nothing_is_registered(
        string field, string value, string named)
    {
        var token = TestContext.Current.CancellationToken;

        await using var broker = await LoopbackBroker.StartAsync(token);

        var request = new Wire.AttachRequest
        {
            Agent = "orders@alpha",
            Adapter = "test",
            Tier = Wire.AgentTier.Pull,
            ProtocolVersion = Parley.Core.ProtocolVersion.Current,
            Workspace = "C:/repos/orders",
        };

        switch (field)
        {
            case "workspace": request.Workspace = value; break;
            case "adapter": request.Adapter = value; break;
            default: request.SessionRef = value; break;
        }

        using var stream = broker.Connect().Attach(request, cancellationToken: token);

        var refused = await Assert.ThrowsAsync<RpcException>(async () => await stream.ResponseStream.MoveNext(token));

        Assert.Equal(StatusCode.InvalidArgument, refused.StatusCode);
        Assert.Contains($"The {field} contains {named}", refused.Status.Detail, StringComparison.Ordinal);

        var registry = await broker.ConnectAsOperator().DescribeRegistryAsync(new Wire.Empty(), cancellationToken: token);

        Assert.DoesNotContain(registry.Rows, row => row.Name == "orders@alpha");
    }
}
