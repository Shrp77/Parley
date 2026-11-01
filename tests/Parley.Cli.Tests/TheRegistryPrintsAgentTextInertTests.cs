using Grpc.Core;
using Xunit;
using Wire = Parley.Transport;

namespace Parley.Cli.Tests;

/// <summary>
/// <c>parley registry</c> prints what an agent reported about itself inert, so a terminal shows it rather than obeys it.
/// </summary>
/// <remarks>
/// <para>
/// Lens 2 run 135: the workspace and adapter are what an adapter read from a launch environment the agent can edit, and
/// the listing printed them raw. A workspace holding <c>ESC ] 52</c> wrote the person's clipboard; one holding
/// <c>ESC [ 1A ESC [ 2K</c> moved the cursor up and erased the line they were reading.
/// </para>
/// <para>
/// <b>A broker answering by script rather than a store</b>, because attach now refuses such a row and a store written
/// before that still holds one: the listing must be safe whatever the registry says.
/// </para>
/// </remarks>
[Unit]
public sealed class TheRegistryPrintsAgentTextInertTests
{
    private const char Escape = (char)0x1B;

    [Fact]
    public async Task A_workspace_adapter_or_line_holding_an_escape_prints_none()
    {
        var reply = new Wire.RegistryReply
        {
            Rows =
            {
                new Wire.RegistryRow
                {
                    Name = "orders@alpha",
                    Adapter = $"mcp{Escape}[2K",
                    Tier = Wire.AgentTier.Pull,
                    Workspace = $"/srv/orders{Escape}]52;c;aGk={(char)0x07}{Escape}[1A{Escape}[2K",
                    Owns = $"Orders{Escape}[31m",
                    Presence = Wire.AgentPresence.Startable,
                },
            },
        };

        using var output = new StringWriter();

        var commands = new OperatorCommands(new Wire.Operators.OperatorsClient(new RegistryOnly(reply)), output);

        Assert.Equal(0, await commands.RegistryAsync(TestContext.Current.CancellationToken));

        var printed = output.ToString();

        Assert.DoesNotContain(Escape, printed);
        Assert.DoesNotContain((char)0x07, printed);

        // One for one, so what was there reads as having been there, and the path around it is the real one.
        Assert.Contains("/srv/orders?]52;c;aGk=??[1A?[2K", printed, StringComparison.Ordinal);
        Assert.Contains("mcp?[2K", printed, StringComparison.Ordinal);
        Assert.Contains("owns: Orders?[31m", printed, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("a\u2028b", "a?b")]
    [InlineData("a\u202Eb", "a?b")]
    [InlineData("a\r\nb", "a??b")]
    [InlineData("dépôts/注文 \U0001F600", "dépôts/注文 \U0001F600")]
    public void The_neutraliser_replaces_one_for_one_and_keeps_every_printable_character(string text, string printed) =>
        Assert.Equal(printed, Inert.Line(text));

    /// <summary>Answers the registry with a fixed reply and refuses everything else.</summary>
    private sealed class RegistryOnly(Wire.RegistryReply reply) : CallInvoker
    {
        public override AsyncUnaryCall<TResponse> AsyncUnaryCall<TRequest, TResponse>(
            Method<TRequest, TResponse> method, string? host, CallOptions options, TRequest request) =>
            method.Name == "DescribeRegistry"
                ? new AsyncUnaryCall<TResponse>(
                    Task.FromResult((TResponse)(object)reply),
                    Task.FromResult(new Metadata()),
                    () => Status.DefaultSuccess,
                    () => new Metadata(),
                    () => { })
                : throw new NotSupportedException(method.FullName);

        public override TResponse BlockingUnaryCall<TRequest, TResponse>(
            Method<TRequest, TResponse> method, string? host, CallOptions options, TRequest request) =>
            throw new NotSupportedException();

        public override AsyncServerStreamingCall<TResponse> AsyncServerStreamingCall<TRequest, TResponse>(
            Method<TRequest, TResponse> method, string? host, CallOptions options, TRequest request) =>
            throw new NotSupportedException();

        public override AsyncClientStreamingCall<TRequest, TResponse> AsyncClientStreamingCall<TRequest, TResponse>(
            Method<TRequest, TResponse> method, string? host, CallOptions options) =>
            throw new NotSupportedException();

        public override AsyncDuplexStreamingCall<TRequest, TResponse> AsyncDuplexStreamingCall<TRequest, TResponse>(
            Method<TRequest, TResponse> method, string? host, CallOptions options) =>
            throw new NotSupportedException();
    }
}
