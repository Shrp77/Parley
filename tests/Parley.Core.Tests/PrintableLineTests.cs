using Xunit;

namespace Parley.Core.Tests;

/// <summary>
/// What an attach may report about itself, and the one character rule it shares with an ownership line.
/// </summary>
/// <remarks>
/// Lens 2 run 135: a workspace holding escape sequences was stored and printed raw to the person. The rule is the
/// ownership line's, held in one place, so the two cannot drift.
/// </remarks>
[Unit]
public sealed class PrintableLineTests
{
    [Theory]
    [InlineData("C:/repos/orders\u001b]52;c;aGk=\u0007", "U+001B at position 16")]
    [InlineData("/srv/orders\u001b[1A\u001b[2K", "U+001B at position 12")]
    [InlineData("/srv/orders\u2028billing@alpha  active", "U+2028 at position 12")]
    [InlineData("/srv/\u202Esredro", "U+202E at position 6")]
    [InlineData("/srv/or\u200Bders", "U+200B at position 8")]
    [InlineData("/srv/\U0001F600/or\u0085ders", "U+0085 at position 10")]
    public void A_workspace_that_could_rewrite_the_line_is_refused_saying_what_and_where(string workspace, string said)
    {
        Assert.False(PrintableLine.IsReportable("workspace", workspace, out var error));
        Assert.StartsWith("The workspace contains ", error, StringComparison.Ordinal);
        Assert.Contains(said, error, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("C:\\Users\\Ada Lovelace\\repos\\orders")]
    [InlineData("/home/ada/dépôts/注文/orders 😀")]
    public void An_ordinary_path_in_any_script_is_reportable(string? workspace)
    {
        Assert.True(PrintableLine.IsReportable("workspace", workspace, out var error));
        Assert.Null(error);
    }

    [Theory]
    [InlineData("orders\u001bbilling")]
    [InlineData("orders\u2029billing")]
    [InlineData("orders\uFEFFbilling")]
    [InlineData("orders and billing")]
    [InlineData("orders \U0001F600 billing")]
    public void An_attach_and_an_ownership_line_are_held_to_the_same_rule(string text) =>
        Assert.Equal(OwnershipStatement.IsValid(text, out _), PrintableLine.IsReportable("workspace", text, out _));
}
