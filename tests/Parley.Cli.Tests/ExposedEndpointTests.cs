using Xunit;

namespace Parley.Cli.Tests;

/// <summary>
/// Which endpoints the broker treats as reaching this machine only, and therefore which ones it
/// warns about on every start.
/// </summary>
/// <remarks>
/// <para>
/// <b>The rule is tested rather than the log line, and that is deliberate.</b> Proving the warning
/// end to end means starting a broker on a non-loopback address, which opens a real listening
/// socket on whatever network the machine running the suite is attached to — on a developer's
/// laptop, on a CI runner, on every run. The exposure this warns about is not a thing to create
/// in order to check that it is reported.
/// </para>
/// <para>
/// What is left is the decision, and the decision is the security-relevant half: every endpoint
/// below either keeps the deployment on one machine or does not, and getting that boundary wrong
/// is what would make the warning silent on exactly the configuration that needs it.
/// </para>
/// </remarks>
[Integration]
public sealed class ExposedEndpointTests
{
    [Theory]
    [InlineData("http://127.0.0.1:19100")]
    [InlineData("http://localhost:19100")]
    [InlineData("http://LOCALHOST:19100")]
    // The whole of 127/8 is loopback, not just .0.0.1 — a deployment moved onto 127.0.0.2 to free
    // a port is still unreachable from anywhere else, and warning about it would teach whoever
    // reads the log to ignore the warning.
    [InlineData("http://127.0.0.2:19100")]
    [InlineData("http://127.99.4.7:19100")]
    [InlineData("http://[::1]:19100")]
    public void An_endpoint_that_reaches_this_machine_only_is_not_warned_about(string endpoint) =>
        Assert.True(
            ServeCommand.OnlyThisMachine(endpoint),
            $"'{endpoint}' reaches this machine only and would have been warned about");

    [Theory]
    // The spelling somebody reaches for, and the worst of them: every interface the machine has
    // now and every one it ever joins afterwards.
    [InlineData("http://0.0.0.0:19100")]
    [InlineData("http://[::]:19100")]
    // A private network the protocol permits. Still warned about: permitted is not the same as
    // free, and all three services share the port.
    [InlineData("http://192.168.1.50:19100")]
    [InlineData("http://10.4.0.9:19100")]
    // A name rather than an address. Kestrel binds every interface for one of these, so it is the
    // first case disguised as something narrower.
    [InlineData("http://my-box:19100")]
    [InlineData("http://broker.internal.example:19100")]
    // Routable, and the case where silence would be worst.
    [InlineData("http://203.0.113.7:19100")]
    public void An_endpoint_anything_else_could_reach_is_warned_about(string endpoint) =>
        Assert.False(
            ServeCommand.OnlyThisMachine(endpoint),
            $"'{endpoint}' can be reached from off this machine and would not have been warned about");

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("19100")]
    [InlineData("not an address at all")]
    [InlineData("://127.0.0.1")]
    public void An_endpoint_nothing_can_parse_is_left_to_the_failure_that_names_it(string endpoint) =>
        // Counted as this machine only, which reads backwards until you ask what the alternative
        // says. An endpoint no parser understands is one no broker will bind, so the start fails a
        // few lines later with a message about the endpoint being unusable. Warning about exposure
        // as well would put a second, wrong diagnosis above the right one — and it would be the
        // alarming one, so it is the one that would be acted on.
        Assert.True(
            ServeCommand.OnlyThisMachine(endpoint),
            $"'{endpoint}' parses as nothing and should be left to the bind failure to report");
}
