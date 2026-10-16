using Microsoft.Data.Sqlite;
using Xunit;

namespace Parley.Store.Tests;

/// <summary>
/// That the facilitator's role is reserved in storage, and that its row cannot be withdrawn (SPEC.md §12.3).
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this is storage's to enforce.</b> Every door that could write the role — an attach, a seat, an
/// enrolment — refuses it with a sentence first. Principle V says that is not enough: a door written next year, or
/// by somebody else, would pass, and the thing that would be forged is the one sender every agent is asked by
/// without having asked for anything.
/// </para>
/// <para>
/// <b>Each refusal must fail against a schema without it</b>, where all four would be admitted.
/// </para>
/// </remarks>
public sealed class FacilitatorRowTests : IDisposable
{
    private readonly FacilitatorStore _store = new();

    public void Dispose() => _store.Dispose();

    [Fact]
    public void The_reserved_role_is_refused_to_any_other_adapter()
    {
        // The forgery itself: an agent registering under the facilitator's role on another machine.
        Assert.Throws<SqliteException>(() => _store.Execute(
            "INSERT INTO agents (name, adapter, tier, status) " +
            "VALUES ('parley-facilitator@elsewhere', 'claude-code', 'full', 'active');"));
    }

    [Fact]
    public void The_facilitators_adapter_is_refused_under_any_other_role()
    {
        // The other half: a row that carries the facilitator's adapter and so would read as Parley on every surface.
        Assert.Throws<SqliteException>(() => _store.Execute(
            "INSERT INTO agents (name, adapter, tier, status) " +
            "VALUES ('orders@elsewhere', 'parley-facilitator', 'pull', 'active');"));
    }

    [Fact]
    public void An_attach_cannot_turn_an_agent_into_the_facilitator_or_back()
    {
        // Registration updates configuration on every launch (§12.3), so an update is a route as well as an insert.
        Assert.Throws<SqliteException>(() => _store.Execute(
            "UPDATE agents SET adapter = 'parley-facilitator' WHERE name = 'orders@test';"));
        Assert.Throws<SqliteException>(() => _store.Execute(
            $"UPDATE agents SET adapter = 'claude-code' WHERE name = '{FacilitatorStore.Facilitator}';"));
    }

    [Fact]
    public void The_facilitator_cannot_be_retired()
    {
        Assert.Throws<SqliteException>(() => _store.Execute(
            $"UPDATE agents SET status = 'retired' WHERE name = '{FacilitatorStore.Facilitator}';"));

        Assert.Equal(1L, _store.Count(
            $"SELECT count(*) FROM agents WHERE name = '{FacilitatorStore.Facilitator}' AND status = 'active';"));
    }

    [Fact]
    public void The_facilitator_cannot_be_deleted()
    {
        Assert.Throws<SqliteException>(() => _store.Execute(
            $"DELETE FROM agents WHERE name = '{FacilitatorStore.Facilitator}';"));
    }

    [Fact]
    public void The_role_parley_is_an_ordinary_one()
    {
        // This repository registers as its own agent under `parley`, and enrolment turns a folder called Parley
        // into that role. Reserving it would refuse the repository that defines the protocol.
        _store.Execute(
            "INSERT INTO agents (name, adapter, tier, status) VALUES ('parley@test', 'claude-code', 'full', 'active');");

        Assert.Equal(1L, _store.Count("SELECT count(*) FROM agents WHERE name = 'parley@test';"));
    }
}
