using Microsoft.Data.Sqlite;
using Xunit;

namespace Parley.Store.Tests;

/// <summary>
/// That every invariant SPEC.md §9.3 claims for the file store is refused by storage itself.
/// </summary>
/// <remarks>
/// <para>
/// <b>Written as the malicious case, not as a reading of the DDL.</b> Each probe writes the row a broker bug —
/// or a second implementation sharing the database — would write, and asserts SQLite refuses it. The
/// repository never writes these, which is exactly why they have to be tried here: a guard nothing exercises is
/// a guard nobody knows works.
/// </para>
/// <para>
/// Each "admitted" case sits beside a refused one on purpose. A constraint that refused everything would pass
/// every refusal test, so the neighbouring legal row is what shows the refusal is the rule and not an accident.
/// </para>
/// </remarks>
public sealed class FileStoreConstraintTests : IDisposable
{
    private const string Stamp = "2026-09-14T00:00:00.0000000Z";
    private const string Hash = "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855";
    private const string Other = "1111111111111111111111111111111111111111111111111111111111111111";

    private readonly SqliteConnection _connection = ParleyDatabase.OpenInMemory();

    public FileStoreConstraintTests()
    {
        Execute($"""
            INSERT INTO nodes (machine, connected, first_seen_utc) VALUES ('test', 1, '{Stamp}') ON CONFLICT DO NOTHING;
            INSERT INTO agents (name, adapter, tier, status) VALUES
                ('orders@test', 'test', 'full', 'active'),
                ('billing@test', 'test', 'full', 'active');
            INSERT INTO conversations (id, state, opened_utc) VALUES
                ('pricing', 'OPEN', '{Stamp}'),
                ('Pricing', 'OPEN', '{Stamp}'),
                ('refunds', 'OPEN', '{Stamp}');
            INSERT INTO conversation_folders (conversation_id, folder, created_utc) VALUES
                ('pricing', 'pricing', '{Stamp}'),
                ('refunds', 'refunds', '{Stamp}');
            """);
    }

    public void Dispose() => _connection.Dispose();

    [Fact]
    public void A_well_formed_file_is_admitted()
    {
        File("pricing", "PRICE-SNAPSHOT.MD", "price-snapshot.md");

        Assert.Equal(1L, Count("shared_files"));
    }

    [Fact]
    public void Two_names_with_one_key_in_a_conversation_are_refused()
    {
        File("pricing", "README.MD", "Readme.md");

        Refused(() => File("pricing", "README.MD", "README.md"));
    }

    [Fact]
    public void The_same_name_in_two_conversations_is_two_files()
    {
        File("pricing", "NOTES.MD", "notes.md");
        File("refunds", "NOTES.MD", "notes.md");

        Assert.Equal(2L, Count("shared_files"));
    }

    [Theory]
    [InlineData("PriceSnapshot.cs")]
    [InlineData("run.bat")]
    [InlineData("../escape.txt")]
    [InlineData("docs\\notes.md")]
    [InlineData("a:b.md")]
    [InlineData("what?.md")]
    [InlineData("say\"hi\".md")]
    [InlineData("a|b.md")]
    [InlineData(".hidden.md")]
    [InlineData(" notes.md")]
    [InlineData("con.md")]
    [InlineData("LPT1.txt")]
    [InlineData("con.notes.md")]
    [InlineData("a.m")]
    [InlineData("a b.md")]
    [InlineData("a+b.md")]
    [InlineData("a~b.md")]
    [InlineData("Übersicht.md")]
    public void A_name_breaking_a_rule_is_refused(string name)
    {
        Refused(() => File("pricing", name.ToUpperInvariant(), name));
    }

    [Fact]
    public void A_name_with_a_control_character_is_refused()
    {
        // Built at run time: a raw control character in the source is invisible to a reader and to a diff.
        foreach (var code in new[] { 0x01, 0x09, 0x0A, 0x1F, 0x7F })
        {
            var name = "notes" + (char)code + ".md";

            Refused(() => File("pricing", name.ToUpperInvariant(), name));
        }
    }

    [Fact]
    public void A_key_that_is_not_upper_case_is_refused()
    {
        Refused(() => File("pricing", "notes.md", "notes.md"));
    }

    [Fact]
    public void A_key_that_is_another_names_is_refused()
    {
        // Upper case, and a different file's key: a second name for one file, which the key exists to prevent.
        Refused(() => File("pricing", "OTHER.MD", "notes.md"));
    }

    [Theory]
    [InlineData("abc")]
    [InlineData("E3B0C44298FC1C149AFBF4C8996FB92427AE41E4649B934CA495991B7852B855")]
    [InlineData("zzb0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855")]
    public void A_hash_that_is_not_64_lowercase_hex_is_refused(string hash)
    {
        Refused(() => File("pricing", "NOTES.MD", "notes.md", sha256: hash));
    }

    [Fact]
    public void A_placer_that_is_not_registered_is_refused_and_the_person_is_admitted()
    {
        Refused(() => File("pricing", "A.MD", "a.md", placedBy: "nobody@test"));

        File("pricing", "B.MD", "b.md", placedBy: null);

        Assert.Equal(1L, Count("shared_files"));
    }

    [Fact]
    public void Who_placed_a_file_and_when_cannot_be_rewritten()
    {
        File("pricing", "NOTES.MD", "notes.md");

        Refused(() => Execute("UPDATE shared_files SET placed_by = 'orders@test';"));
        Refused(() => Execute("UPDATE shared_files SET placed_by = NULL;"));
        Refused(() => Execute("UPDATE shared_files SET placed_utc = '2027-01-01T00:00:00.0000000Z';"));
    }

    [Fact]
    public void A_file_cannot_move_to_another_conversation()
    {
        File("pricing", "NOTES.MD", "notes.md");

        Refused(() => Execute("UPDATE shared_files SET conversation_id = 'refunds';"));
    }

    [Fact]
    public void A_replacement_and_a_rename_in_place_are_admitted_and_keep_the_placer()
    {
        File("pricing", "NOTES.MD", "notes.md");

        Execute($"UPDATE shared_files SET sha256 = '{Other}', size_bytes = 99, changed_utc = '{Stamp}';");
        Execute("UPDATE shared_files SET name = 'renamed.md', name_key = 'RENAMED.MD';");

        Assert.Equal("billing@test|renamed.md", Row("SELECT placed_by || '|' || name FROM shared_files;"));
    }

    [Fact]
    public void A_file_whose_conversation_has_no_folder_is_refused()
    {
        // 'Pricing' is a conversation with no folder row.
        Refused(() => File("Pricing", "NOTES.MD", "notes.md"));
    }

    [Fact]
    public void A_path_inside_the_location_longer_than_255_characters_is_refused_on_insert_and_on_rename()
    {
        // 'pricing' is 7 characters, so a name may be 255 - 7 - 1 = 247.
        var fits = new string('n', 247 - 3) + ".md";
        var over = new string('n', 248 - 3) + ".md";

        File("pricing", fits.ToUpperInvariant(), fits);
        Refused(() => File("pricing", over.ToUpperInvariant(), over));
        Refused(() => Execute($"UPDATE shared_files SET name = '{over}', name_key = '{over.ToUpperInvariant()}';"));
    }

    [Fact]
    public void Two_conversations_cannot_have_folders_differing_only_in_case()
    {
        Refused(() => Execute($"INSERT INTO conversation_folders VALUES ('Pricing', 'PRICING', '{Stamp}');"));

        Execute($"INSERT INTO conversation_folders VALUES ('Pricing', 'Pricing~2', '{Stamp}');");
    }

    [Theory]
    [InlineData("pricing~1")]
    [InlineData("pricing~02")]
    [InlineData("pricing~")]
    [InlineData("pricing~x")]
    [InlineData("other")]
    [InlineData("pricing/x")]
    [InlineData("PRICING")]
    public void A_folder_that_is_neither_the_identifier_nor_it_with_a_suffix_of_two_or_more_is_refused(string folder)
    {
        Execute("DELETE FROM conversation_folders WHERE conversation_id = 'pricing';");

        Refused(() => Execute($"INSERT INTO conversation_folders VALUES ('pricing', '{folder}', '{Stamp}');"));
    }

    [Fact]
    public void A_folder_keeps_its_name()
    {
        Refused(() => Execute("UPDATE conversation_folders SET folder = 'pricing~2' WHERE conversation_id = 'pricing';"));
    }

    [Fact]
    public void A_notice_saying_nothing_changed_is_refused_and_a_gone_notice_is_admitted()
    {
        Refused(() => Notice(sha256Now: Hash, size: 1));

        Notice(sha256Now: null, size: null);

        Assert.Equal(1L, Count("file_notices"));
    }

    [Fact]
    public void A_notice_that_is_gone_carries_no_size_and_one_that_changed_carries_one()
    {
        Refused(() => Notice(sha256Now: null, size: 5));
        Refused(() => Notice(sha256Now: Other, size: null));
    }

    [Fact]
    public void Two_notices_for_one_agent_and_file_are_refused()
    {
        Notice(sha256Now: Other, size: 1);

        Refused(() => Notice(sha256Now: null, size: null));
    }

    [Fact]
    public void A_fetch_record_for_a_conversation_that_does_not_exist_is_refused()
    {
        Refused(() => Execute($"""
            INSERT INTO file_fetches (agent, conversation_id, name_key, name, sha256, fetched_utc)
            VALUES ('orders@test', 'nowhere', 'NOTES.MD', 'notes.md', '{Hash}', '{Stamp}');
            """));
    }

    [Fact]
    public void Fetch_records_and_notices_go_with_their_agent()
    {
        Execute($"""
            INSERT INTO file_fetches (agent, conversation_id, name_key, name, sha256, fetched_utc)
            VALUES ('orders@test', 'pricing', 'NOTES.MD', 'notes.md', '{Hash}', '{Stamp}');
            """);
        Notice(sha256Now: Other, size: 1);

        Execute("DELETE FROM agents WHERE name = 'orders@test';");

        Assert.Equal(0L, Count("file_fetches"));
        Assert.Equal(0L, Count("file_notices"));
    }

    private void File(string conversation, string key, string name, string sha256 = Hash, string? placedBy = "billing@test")
    {
        using var command = _connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO shared_files
                (conversation_id, name_key, name, size_bytes, sha256, placed_by, placed_utc, changed_utc, modified_ticks)
            VALUES ($conversation, $key, $name, 0, $sha256, $placedBy, $stamp, $stamp, 0);
            """;
        command.Parameters.AddWithValue("$conversation", conversation);
        command.Parameters.AddWithValue("$key", key);
        command.Parameters.AddWithValue("$name", name);
        command.Parameters.AddWithValue("$sha256", sha256);
        command.Parameters.AddWithValue("$placedBy", (object?)placedBy ?? DBNull.Value);
        command.Parameters.AddWithValue("$stamp", Stamp);
        command.ExecuteNonQuery();
    }

    private void Notice(string? sha256Now, long? size)
    {
        using var command = _connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO file_notices
                (agent, conversation_id, name_key, name, sha256_had, sha256_now, size_bytes, changed_utc)
            VALUES ('orders@test', 'pricing', 'NOTES.MD', 'notes.md', $had, $now, $size, $stamp);
            """;
        command.Parameters.AddWithValue("$had", Hash);
        command.Parameters.AddWithValue("$now", (object?)sha256Now ?? DBNull.Value);
        command.Parameters.AddWithValue("$size", (object?)size ?? DBNull.Value);
        command.Parameters.AddWithValue("$stamp", Stamp);
        command.ExecuteNonQuery();
    }

    private static void Refused(Action write) => Assert.Throws<SqliteException>(write);

    private void Execute(string sql)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private long Count(string table)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = $"SELECT count(*) FROM {table};";
        return (long)command.ExecuteScalar()!;
    }

    private string Row(string sql)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = sql;
        return (string)command.ExecuteScalar()!;
    }
}
