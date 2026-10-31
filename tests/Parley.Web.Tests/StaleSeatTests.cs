using Xunit;

namespace Parley.Web.Tests;

/// <summary>
/// That the frame never marks the person here when nothing holds their seat.
/// </summary>
/// <remarks>
/// <para>
/// Found in use, on the first restart after an upgrade: the frame believed a cookie remembering a typed name and drew
/// that name with a live mark beside it, while the inbox one click away said nobody was seated. The cookie is gone
/// — the broker provides the seat and every page takes it — but the property this held is unchanged: <b>the
/// mark means a stream is open now</b>, and a console whose broker cannot be reached has none.
/// </para>
/// </remarks>
[Integration]
public sealed class StaleSeatTests
{
    [Fact]
    public async Task Nothing_held_is_not_somebody_being_present()
    {
        // DeadBrokerConsole points at a port nothing listens on, so no seat can be taken — the state a restarted or
        // unreachable broker leaves a console in.
        using var console = new DeadBrokerConsole();
        using var client = console.CreateStubbornClient();

        var body = await client.GetStringAsync(new Uri("/", UriKind.Relative), TestContext.Current.CancellationToken);

        // Non-vacuous: the page has to have rendered the frame at all before its absence of a mark means anything.
        Assert.Contains("Projects", body, StringComparison.Ordinal);

        // Matched on the element rather than the class, which the inlined stylesheet also names.
        Assert.DoesNotContain("<span class=\"seat-dot\"", body, StringComparison.Ordinal);
        Assert.Contains("not connected", body, StringComparison.Ordinal);
    }
}
