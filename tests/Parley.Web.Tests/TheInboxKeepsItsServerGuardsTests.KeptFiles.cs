using Xunit;

namespace Parley.Web.Tests;

/// <summary>Lens 3 sweep on 3b752970: the files a refused answer kept go back into the box they were sent from.</summary>
public sealed partial class TheInboxKeepsItsServerGuardsTests
{
    [Theory]
    [InlineData("Result", "t-20-request")]
    [InlineData("Clarify", "c-20")]
    public async Task Files_kept_from_a_refused_answer_go_back_only_into_the_box_they_were_sent_from(string handler, string box)
    {
        // A row has two boxes, the Result's and the Clarify's, and each takes files. Without the form compared, the file kept
        // from a refused Result came back in the Clarify box as well, one press from being named in a question instead.
        using var console = new TwoProjects();

        Asked(console.Alpha, "alpha", 20, Orders, "What alpha was asked.");
        console.Alpha.FailsOn = "/parley.v1.Agents/" + handler;

        using var client = console.CreateClient();

        var page = await client.GetStringAsync(new Uri("/Inbox", UriKind.Relative), Ct);

        using var form = new MultipartFormDataContent
        {
            { new StringContent(Token().Match(page).Groups[1].Value), "__RequestVerificationToken" },
            { new StringContent("alpha"), "For" },
            { new StringContent("20"), "Exchange" },
            { new StringContent("talk"), "Conversation" },
            { new StringContent("request"), "Ids" },
            { new StringContent("Words"), "Texts" },
            { new StringContent("blocked:conflict"), "Verdicts" },
            { new StringContent("A question"), "Clarification" },
            { new StringContent("alphas-screen.png"), "Attached" },
        };

        using var response = await client.PostAsync(new Uri("/Inbox?handler=" + handler, UriKind.Relative), form, Ct);
        var after = await response.Content.ReadAsStringAsync(Ct);

        var row = Assert.Single(
            after.Split("<details class=\"dgrid-row\" role=\"row\""),
            row => row.Contains("What alpha was asked.", StringComparison.Ordinal));

        Assert.Equal(1, row.Split("name=\"Attached\" value=\"alphas-screen.png\"").Length - 1);

        // And it is in the box it was sent from: a box draws its files just before its own text area.
        var chip = row.IndexOf("name=\"Attached\" value=\"alphas-screen.png\"", StringComparison.Ordinal);
        var next = row.IndexOf("<textarea", chip, StringComparison.Ordinal);

        Assert.StartsWith($"<textarea data-composer id=\"{box}\"", row[next..], StringComparison.Ordinal);
    }
}
