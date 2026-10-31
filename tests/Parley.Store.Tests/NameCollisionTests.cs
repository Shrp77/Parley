using Microsoft.Data.Sqlite;
using Parley.Core;
using Xunit;

namespace Parley.Store.Tests;

/// <summary>
/// A name belongs to one repository, per SPEC.md §4.1.1.
/// </summary>
/// <remarks>
/// <para>
/// Two checkouts on one machine agree about the role, agree about the machine, and therefore
/// agree about the whole name. Without a check they share a registry row and an identity, and
/// nothing anywhere looks wrong: messages for one arrive at whichever happens to be running,
/// answers come back under a name that means two things, and the symptom a person eventually
/// sees is a peer receiving replies about a repository it never asked about.
/// </para>
/// <para>
/// The workspace is the only fact that distinguishes them, which is why it is the one this
/// reads.
/// </para>
/// </remarks>
[Integration]
public sealed class NameCollisionTests : IDisposable
{
    private static readonly AgentName Docs = AgentName.Create("docs@test");

    private readonly SqliteConnection _connection = ParleyDatabase.OpenInMemory();
    private readonly AgentRepository _agents;

    public NameCollisionTests() => _agents = new AgentRepository(_connection);

    public void Dispose() => _connection.Dispose();

    private Task Register(string? workspace) =>
        _agents.RegisterIfAbsentAsync(
            new AgentRecord(Docs, "mcp", AgentTier.Pull, workspace, null, null, false,
                AgentStatus.Active),
            TestContext.Current.CancellationToken);

    [Fact]
    public async Task An_unregistered_name_is_held_by_nobody()
    {
        Assert.Null(await _agents.WorkspaceOfAsync(Docs, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task The_repository_that_registered_a_name_is_the_one_reported()
    {
        await Register("C:/repo-one");

        Assert.Equal(
            "C:/repo-one",
            await _agents.WorkspaceOfAsync(Docs, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task A_second_repository_does_not_take_the_name_over()
    {
        // RegisterIfAbsentAsync writes nothing when the name is known, so the second launch does
        // not overwrite the first -- it simply shares it. That silence is the failure, and the
        // reason the server checks rather than relying on this method to refuse: this method
        // cannot tell "already registered by me" from "already registered by somebody else".
        await Register("C:/repo-one");
        await Register("C:/repo-two");

        Assert.Equal(
            "C:/repo-one",
            await _agents.WorkspaceOfAsync(Docs, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task A_registration_with_no_workspace_cannot_be_distinguished()
    {
        // The honest limit. A launch that records no workspace leaves nothing to compare, so a
        // second one claiming the same name is admitted -- and the check says so rather than
        // guessing. Asserted so that a future change which starts guessing has to change a test
        // that explains why it should not.
        await Register(workspace: null);

        Assert.Null(await _agents.WorkspaceOfAsync(Docs, TestContext.Current.CancellationToken));
    }
}
