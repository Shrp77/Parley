using System.Diagnostics;
using Parley.Core;
using Xunit;
using Wire = Parley.Transport;

namespace Parley.Server.Tests;

/// <summary>
/// That a sweep puts the file location right against the store: a tampered file quarantined and its holders told, a
/// missing one recorded gone, anything no row names deleted, and what it cannot move or delete raised to the person
/// (SPEC.md §9.5).
/// </summary>
/// <remarks>
/// Nothing but Parley should change the location. Each case is something else doing so: an agent's machine with the
/// folder open, a person's file browser, or a program meaning harm, editing a file so its changes spread to every agent
/// that fetches it. The link cases hold that a sweep deletes a link as a link and never what it reaches.
/// </remarks>
[Integration]
public sealed class TheSweepPutsTheLocationRightTests : IDisposable
{
    private const string Orders = "orders@alpha";
    private const string Billing = "billing@alpha";

    private readonly string _base = Directory.CreateTempSubdirectory("parley-sweep-").FullName;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static byte[] Text(string text) => System.Text.Encoding.UTF8.GetBytes(text);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_base, recursive: true);
        }
        catch (Exception cleanup) when (cleanup is IOException or UnauthorizedAccessException)
        {
        }
    }

    [Fact]
    public async Task A_tampered_file_is_quarantined_and_its_fetcher_and_placer_are_told()
    {
        await using var broker = await LoopbackBroker.StartAsync(Ct);
        await using var billing = await AttachedClient.AttachAsync(broker, Billing, cancellationToken: Ct);
        await using var orders = await AttachedClient.AttachAsync(broker, Orders, cancellationToken: Ct);
        var placed = await FileWire.PlaceAsync(billing.Client, Billing, "terms.md", Text("net 30"), "pricing", Ct);
        _ = await FileWire.FetchAndReportAsync(orders.Client, Orders, "terms.md", "pricing", Ct);

        // Same size, other bytes: what a program spreading its own text would write.
        var path = Path.Combine(broker.Runtime.Files.FolderPath("pricing"), "terms.md");
        await File.WriteAllTextAsync(path, "net 99", Ct);

        await broker.Runtime.Sweeper.SweepAsync(Ct);

        var operators = broker.ConnectAsOperator();
        var problem = Assert.Single((await operators.ListFileProblemsAsync(new Wire.ListFileProblemsRequest(), cancellationToken: Ct)).Problems);
        Assert.Equal(Wire.FileProblemKind.Quarantined, problem.Kind);
        Assert.Equal(placed.Sha256, problem.Sha256Placed);
        Assert.Equal("net 99", await File.ReadAllTextAsync(Path.Combine(broker.Runtime.Files.Quarantine, problem.MovedTo), Ct));
        Assert.False(File.Exists(path));
        Assert.Empty((await operators.ListSharedFilesAsync(new Wire.ListFilesRequest(), cancellationToken: Ct)).Files);

        foreach (var told in new[] { orders, billing })
        {
            var notice = Assert.Single((await told.Client.DrainInboxAsync(new Wire.DrainInboxRequest { Agent = told.Name }, cancellationToken: Ct)).FileNotices);
            Assert.Equal("terms.md", notice.Name);
            Assert.Equal(problem.Sha256Found, notice.Sha256Found);
        }
    }

    [Fact]
    public async Task A_tampered_file_under_the_longest_name_that_fits_is_quarantined_and_both_are_told()
    {
        // Found by review, lens 1 run 62: the quarantine name added 20 characters to the placed one, so a name over 235
        // could not be moved, the sweep raised it as locked, and neither the fetcher nor the placer was ever told.
        await using var broker = await LoopbackBroker.StartAsync(Ct);
        await using var billing = await AttachedClient.AttachAsync(broker, Billing, cancellationToken: Ct);
        await using var orders = await AttachedClient.AttachAsync(broker, Orders, cancellationToken: Ct);
        var name = new string('a', SharedFileName.CharactersLeftIn("pricing") - 3) + ".md";
        _ = await FileWire.PlaceAsync(billing.Client, Billing, name, Text("net 30"), "pricing", Ct);
        _ = await FileWire.FetchAndReportAsync(orders.Client, Orders, name, "pricing", Ct);

        await File.WriteAllTextAsync(Path.Combine(broker.Runtime.Files.FolderPath("pricing"), name), "net 99", Ct);
        await broker.Runtime.Sweeper.SweepAsync(Ct);

        var operators = broker.ConnectAsOperator();
        var problem = Assert.Single((await operators.ListFileProblemsAsync(new Wire.ListFileProblemsRequest(), cancellationToken: Ct)).Problems);
        Assert.Equal(Wire.FileProblemKind.Quarantined, problem.Kind);
        Assert.Equal("net 99", await File.ReadAllTextAsync(Path.Combine(broker.Runtime.Files.Quarantine, problem.MovedTo), Ct));

        foreach (var told in new[] { orders, billing })
        {
            var notice = Assert.Single((await told.Client.DrainInboxAsync(new Wire.DrainInboxRequest { Agent = told.Name }, cancellationToken: Ct)).FileNotices);
            Assert.Equal(name, notice.Name);
        }
    }

    [Fact]
    public void Two_long_names_alike_up_to_the_cut_are_quarantined_apart_and_each_fits()
    {
        var now = new DateTimeOffset(2026, 10, 29, 12, 0, 0, TimeSpan.Zero);
        var room = SharedFileName.CharactersLeftIn("pricing");
        var one = SharedFileName.Create(new string('a', room - 4) + "1.md");
        var two = SharedFileName.Create(new string('a', room - 4) + "2.md");

        var first = Path.GetFileName(FileLocationStore.QuarantineName("pricing", one, now));
        var second = Path.GetFileName(FileLocationStore.QuarantineName("pricing", two, now));

        Assert.NotEqual(first, second);
        Assert.True(first.Length <= SharedFileName.MaxPathCharacters, first);
        Assert.EndsWith(".md", first, StringComparison.Ordinal);
        Assert.Equal("pricing/20261029T120000000Z-terms.md", FileLocationStore.QuarantineName("pricing", SharedFileName.Create("terms.md"), now));
    }

    [Fact]
    public async Task A_fetch_of_a_tampered_file_is_refused_as_tampered_and_starts_a_sweep()
    {
        await using var broker = await LoopbackBroker.StartAsync(Ct);
        await using var billing = await AttachedClient.AttachAsync(broker, Billing, cancellationToken: Ct);
        await using var orders = await AttachedClient.AttachAsync(broker, Orders, cancellationToken: Ct);
        await FileWire.PlaceAsync(billing.Client, Billing, "terms.md", Text("net 30"), "pricing", Ct);
        await File.WriteAllTextAsync(Path.Combine(broker.Runtime.Files.FolderPath("pricing"), "terms.md"), "net 99", Ct);

        using var call = orders.Client.FetchFile(
            new Wire.FetchFileRequest { Agent = Orders, Name = "terms.md", ConversationId = "pricing" }, cancellationToken: Ct);
        Wire.FileRefusal? refusal = null;

        while (await call.ResponseStream.MoveNext(Ct))
        {
            refusal ??= call.ResponseStream.Current.FrameCase == Wire.FetchFileFrame.FrameOneofCase.Refusal ? call.ResponseStream.Current.Refusal : null;
        }

        await broker.Runtime.Sweeper.IdleAsync();

        Assert.Equal(Wire.FileRejection.Tampered, refusal!.Rejection);
        Assert.Contains("no longer reliable", refusal.Detail, StringComparison.Ordinal);
        Assert.Equal(Wire.FileProblemKind.Quarantined, Assert.Single(
            (await broker.ConnectAsOperator().ListFileProblemsAsync(new Wire.ListFileProblemsRequest(), cancellationToken: Ct)).Problems).Kind);
    }

    [Fact]
    public async Task Everything_no_row_names_is_deleted_and_every_recorded_folder_and_file_kept()
    {
        await using var broker = await LoopbackBroker.StartAsync(Ct);
        await using var billing = await AttachedClient.AttachAsync(broker, Billing, cancellationToken: Ct);
        await FileWire.PlaceAsync(billing.Client, Billing, "terms.md", Text("net 30"), "pricing", Ct);
        await FileWire.PlaceAsync(billing.Client, Billing, "gone.md", Text("x"), "empty", Ct);
        _ = await billing.Client.RemoveOwnFilesAsync(
            new Wire.RemoveFilesRequest { Agent = Billing, Files = { new Wire.FileRef { ConversationId = "empty", Name = "gone.md" } } },
            cancellationToken: Ct);

        var root = broker.Runtime.Files.Root;
        var pricing = broker.Runtime.Files.FolderPath("pricing");
        await File.WriteAllTextAsync(Path.Combine(pricing, "copied.md"), "put there by hand", Ct);
        Directory.CreateDirectory(Path.Combine(pricing, "sub"));
        var stray = Directory.CreateDirectory(Path.Combine(root, "my-notes")).FullName;
        await File.WriteAllTextAsync(Path.Combine(stray, "draft.md"), "nobody's in Parley", Ct);
        await File.WriteAllTextAsync(Path.Combine(root, "loose.txt"), "at the top", Ct);

        await broker.Runtime.Sweeper.SweepAsync(Ct);

        Assert.True(File.Exists(Path.Combine(pricing, "terms.md")));
        Assert.True(Directory.Exists(broker.Runtime.Files.FolderPath("empty")), "an empty recorded folder was deleted");
        Assert.False(File.Exists(Path.Combine(pricing, "copied.md")));
        Assert.False(Directory.Exists(Path.Combine(pricing, "sub")));
        Assert.False(Directory.Exists(stray));
        Assert.False(File.Exists(Path.Combine(root, "loose.txt")));
        Assert.Empty((await broker.ConnectAsOperator().ListFileProblemsAsync(new Wire.ListFileProblemsRequest(), cancellationToken: Ct)).Problems);
    }

    [Fact]
    public async Task A_folder_nothing_records_is_deleted_when_the_broker_starts()
    {
        var store = Path.Combine(_base, "alpha.db");
        string stray;

        await using (var first = await StartAsync(store))
        {
            stray = Directory.CreateDirectory(Path.Combine(first.Runtime.Files.Root, "my-notes")).FullName;
            await File.WriteAllTextAsync(Path.Combine(stray, "draft.md"), "nobody's in Parley", Ct);
        }

        await using (await StartAsync(store))
        {
            Assert.False(Directory.Exists(stray));
        }
    }

    [Fact]
    public async Task A_missing_file_is_recorded_gone_and_its_fetcher_told()
    {
        await using var broker = await LoopbackBroker.StartAsync(Ct);
        await using var billing = await AttachedClient.AttachAsync(broker, Billing, cancellationToken: Ct);
        await using var orders = await AttachedClient.AttachAsync(broker, Orders, cancellationToken: Ct);
        await FileWire.PlaceAsync(billing.Client, Billing, "terms.md", Text("net 30"), "pricing", Ct);
        _ = await FileWire.FetchAndReportAsync(orders.Client, Orders, "terms.md", "pricing", Ct);
        File.Delete(Path.Combine(broker.Runtime.Files.FolderPath("pricing"), "terms.md"));

        await broker.Runtime.Sweeper.SweepAsync(Ct);

        var notice = Assert.Single((await orders.Client.DrainInboxAsync(new Wire.DrainInboxRequest { Agent = Orders }, cancellationToken: Ct)).FileNotices);
        Assert.Equal(string.Empty, notice.Sha256Found);

        // Its placer too: something other than Parley took it, and the placer can place it again.
        Assert.Equal("terms.md", Assert.Single(
            (await billing.Client.DrainInboxAsync(new Wire.DrainInboxRequest { Agent = Billing }, cancellationToken: Ct)).FileNotices).Name);
        Assert.Empty((await broker.ConnectAsOperator().ListSharedFilesAsync(new Wire.ListFilesRequest(), cancellationToken: Ct)).Files);
    }

    [Fact]
    public async Task A_list_that_finds_a_file_missing_tells_its_placer()
    {
        // The list's own look, before any sweep it starts: the notice is raised by the list.
        await using var broker = await LoopbackBroker.StartAsync(Ct);
        await using var billing = await AttachedClient.AttachAsync(broker, Billing, cancellationToken: Ct);
        await FileWire.PlaceAsync(billing.Client, Billing, "terms.md", Text("net 30"), "pricing", Ct);
        File.Delete(Path.Combine(broker.Runtime.Files.FolderPath("pricing"), "terms.md"));

        Assert.Empty((await broker.ConnectAsOperator().ListSharedFilesAsync(new Wire.ListFilesRequest(), cancellationToken: Ct)).Files);

        Assert.Equal("terms.md", Assert.Single(
            (await billing.Client.DrainInboxAsync(new Wire.DrainInboxRequest { Agent = Billing }, cancellationToken: Ct)).FileNotices).Name);
    }

    [Fact]
    public async Task A_file_another_program_holds_is_raised_once_and_dismissed_by_the_person()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "only Windows lets a program hold a file against others");

        await using var broker = await LoopbackBroker.StartAsync(Ct);
        await using var billing = await AttachedClient.AttachAsync(broker, Billing, cancellationToken: Ct);
        await FileWire.PlaceAsync(billing.Client, Billing, "terms.md", Text("net 30"), "pricing", Ct);
        var stray = Path.Combine(broker.Runtime.Files.FolderPath("pricing"), "copied.md");
        await File.WriteAllTextAsync(stray, "put there by hand", Ct);
        var operators = broker.ConnectAsOperator();

        using (File.Open(stray, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            await broker.Runtime.Sweeper.SweepAsync(Ct);
            await broker.Runtime.Sweeper.SweepAsync(Ct);
        }

        var problem = Assert.Single((await operators.ListFileProblemsAsync(new Wire.ListFileProblemsRequest(), cancellationToken: Ct)).Problems);
        Assert.Equal(Wire.FileProblemKind.Locked, problem.Kind);
        Assert.Equal("pricing/copied.md", problem.Path);
        Assert.Equal("pricing", problem.ConversationId);

        Assert.True((await operators.DismissFileProblemAsync(new Wire.DismissFileProblemRequest { Id = problem.Id }, cancellationToken: Ct)).Changed);
        Assert.False((await operators.DismissFileProblemAsync(new Wire.DismissFileProblemRequest { Id = problem.Id }, cancellationToken: Ct)).Changed);
        Assert.Empty((await operators.ListFileProblemsAsync(new Wire.ListFileProblemsRequest(), cancellationToken: Ct)).Problems);
        Assert.Single((await operators.ListFileProblemsAsync(new Wire.ListFileProblemsRequest { IncludeDismissed = true }, cancellationToken: Ct)).Problems);

        // Let go now, so the next sweep deletes it.
        await broker.Runtime.Sweeper.SweepAsync(Ct);
        Assert.False(File.Exists(stray));
    }

    [Fact]
    public async Task A_tampered_file_another_program_holds_is_raised_and_still_never_served()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "only Windows lets a program hold a file against others");

        await using var broker = await LoopbackBroker.StartAsync(Ct);
        await using var billing = await AttachedClient.AttachAsync(broker, Billing, cancellationToken: Ct);
        await FileWire.PlaceAsync(billing.Client, Billing, "terms.md", Text("net 30"), "pricing", Ct);
        var path = Path.Combine(broker.Runtime.Files.FolderPath("pricing"), "terms.md");
        await File.WriteAllTextAsync(path, "net 99", Ct);

        // Held so it can be read but not moved: the hashing finds it changed, the move to quarantine cannot happen.
        using (File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            await broker.Runtime.Sweeper.SweepAsync(Ct);
        }

        var problem = Assert.Single((await broker.ConnectAsOperator().ListFileProblemsAsync(new Wire.ListFileProblemsRequest(), cancellationToken: Ct)).Problems);
        Assert.Equal(Wire.FileProblemKind.Locked, problem.Kind);
        Assert.True(File.Exists(path));

        // Still recorded and still there, and never served: a fetch hashes what it streams.
        await using var orders = await AttachedClient.AttachAsync(broker, Orders, cancellationToken: Ct);
        using var call = orders.Client.FetchFile(
            new Wire.FetchFileRequest { Agent = Orders, Name = "terms.md", ConversationId = "pricing" }, cancellationToken: Ct);
        Wire.FileRefusal? refusal = null;

        while (await call.ResponseStream.MoveNext(Ct))
        {
            refusal ??= call.ResponseStream.Current.FrameCase == Wire.FetchFileFrame.FrameOneofCase.Refusal ? call.ResponseStream.Current.Refusal : null;
        }

        Assert.Equal(Wire.FileRejection.Tampered, refusal?.Rejection);
    }

    [Fact]
    public async Task A_link_put_in_the_quarantine_directory_takes_no_file_outside()
    {
        // The quarantine is the person's and Parley deletes nothing there, so a link put in it is refused rather than
        // removed: the tampered file stays where it is, never served, and the person is told.
        await using var broker = await LoopbackBroker.StartAsync(Ct);
        await using var billing = await AttachedClient.AttachAsync(broker, Billing, cancellationToken: Ct);
        await FileWire.PlaceAsync(billing.Client, Billing, "terms.md", Text("net 30"), "pricing", Ct);
        var path = Path.Combine(broker.Runtime.Files.FolderPath("pricing"), "terms.md");
        await File.WriteAllTextAsync(path, "net 99", Ct);
        var outside = Directory.CreateDirectory(Path.Combine(_base, "outside")).FullName;
        Directory.CreateDirectory(broker.Runtime.Files.Quarantine);
        Junction(Path.Combine(broker.Runtime.Files.Quarantine, "pricing"), outside);

        await broker.Runtime.Sweeper.SweepAsync(Ct);

        Assert.Empty(Directory.EnumerateFileSystemEntries(outside));
        Assert.True(File.Exists(path));
        Assert.Equal(Wire.FileProblemKind.Locked, Assert.Single(
            (await broker.ConnectAsOperator().ListFileProblemsAsync(new Wire.ListFileProblemsRequest(), cancellationToken: Ct)).Problems).Kind);

        Directory.Delete(Path.Combine(broker.Runtime.Files.Quarantine, "pricing"));
    }

    [Fact]
    public void A_link_left_in_the_location_is_removed_without_its_target()
    {
        var (location, outside) = Prepare();
        Junction(Path.Combine(location.Root, "linked"), outside);
        var folder = Directory.CreateDirectory(Path.Combine(location.Root, "pricing")).FullName;
        Junction(Path.Combine(folder, "deep"), outside);

        Assert.Empty(location.DeleteUnrecorded(Kept(("pricing", []))));

        Assert.False(Directory.Exists(Path.Combine(location.Root, "linked")));
        Assert.False(Directory.Exists(Path.Combine(folder, "deep")));
        Assert.True(File.Exists(Path.Combine(outside, "precious.txt")), "a link's target lost its contents");
    }

    [Fact]
    public void A_recorded_folder_that_is_a_link_is_removed_without_its_target()
    {
        var (location, outside) = Prepare();
        Junction(Path.Combine(location.Root, "pricing"), outside);

        Assert.Empty(location.DeleteUnrecorded(Kept(("pricing", ["precious.txt"]))));

        Assert.False(Directory.Exists(Path.Combine(location.Root, "pricing")));
        Assert.True(File.Exists(Path.Combine(outside, "precious.txt")), "the junction's target lost its contents");
    }

    [Fact]
    public void A_hard_link_put_in_a_folder_is_removed_and_the_file_it_shares_kept()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "makes a hard link with mklink");

        var (location, outside) = Prepare();
        var folder = Directory.CreateDirectory(Path.Combine(location.Root, "pricing")).FullName;
        Run("mklink", $"/H \"{Path.Combine(folder, "copied.md")}\" \"{Path.Combine(outside, "precious.txt")}\"");

        Assert.Empty(location.DeleteUnrecorded(Kept(("pricing", []))));

        Assert.Equal("not Parley's", File.ReadAllText(Path.Combine(outside, "precious.txt")));
    }

    [Theory]
    [InlineData("0.", false)]
    [InlineData("copied.", false)]
    [InlineData("sub ", true)]
    [InlineData("sub.", true)]
    public void An_entry_whose_name_Windows_trims_is_deleted_and_the_placed_file_beside_it_kept(string name, bool directory)
    {
        // Windows drops a trailing dot or space from a path not written as \\?\, so such an entry could be neither read
        // nor deleted.
        Assert.SkipUnless(OperatingSystem.IsWindows(), "only Windows trims a name");

        var (location, _) = Prepare();
        var folder = Directory.CreateDirectory(Path.Combine(location.Root, "pricing")).FullName;
        File.WriteAllText(Path.Combine(folder, "a.md"), "placed");
        var odd = @"\\?\" + Path.Combine(folder, name);

        if (directory)
        {
            Directory.CreateDirectory(odd);
            File.WriteAllText(odd + @"\inside.md", "x");
        }
        else
        {
            File.WriteAllText(odd, "x");
        }

        Assert.Empty(location.DeleteUnrecorded(Kept(("pricing", ["a.md"]))));

        Assert.Equal(["a.md"], Directory.EnumerateFileSystemEntries(@"\\?\" + folder).Select(Path.GetFileName));
    }

    [Theory]
    [InlineData("stock.")]
    [InlineData("stock ")]
    public void A_stray_whose_name_Windows_trims_does_not_take_a_recorded_folder_with_it(string stray)
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows drops a trailing dot or space from a path");

        var (location, _) = Prepare();
        var live = Directory.CreateDirectory(Path.Combine(location.Root, "stock")).FullName;
        File.WriteAllText(Path.Combine(live, "stock.md"), "live stock levels");
        Directory.CreateDirectory(@"\\?\" + Path.Combine(location.Root, stray));

        Assert.Empty(location.DeleteUnrecorded(Kept(("stock", ["stock.md"]))));

        Assert.True(File.Exists(Path.Combine(live, "stock.md")), "the live conversation's file was deleted by the sweep");
        Assert.False(Directory.Exists(@"\\?\" + Path.Combine(location.Root, stray)));
    }

    [Fact]
    public void A_name_changed_only_in_case_is_taken_for_the_recorded_one_when_its_spelling_is_not_beside_it()
    {
        // On every disk: where case is not told apart the variant is the recorded entry, and where it is, the recorded
        // spelling is absent, so the variant is still taken for it. What the sweep then records gone is decided by its
        // own look at the recorded path, not here.
        var (location, _) = Prepare();
        var folder = Directory.CreateDirectory(Path.Combine(location.Root, "STOCK")).FullName;
        File.WriteAllText(Path.Combine(folder, "STOCK.MD"), "live stock levels");

        Assert.Empty(location.DeleteUnrecorded(Kept(("stock", ["stock.md"]))));

        Assert.True(File.Exists(Path.Combine(folder, "STOCK.MD")));
    }

    [Fact]
    public void A_name_differing_only_in_case_beside_the_recorded_spelling_is_another_entry_and_goes()
    {
        var (location, _) = Prepare();
        var folder = Directory.CreateDirectory(Path.Combine(location.Root, "stock")).FullName;
        File.WriteAllText(Path.Combine(folder, "stock.md"), "live stock levels");
        var tellsCaseApart = !File.Exists(Path.Combine(folder, "STOCK.MD"));

        if (tellsCaseApart)
        {
            File.WriteAllText(Path.Combine(folder, "STOCK.MD"), "somebody else's");
        }

        Assert.Empty(location.DeleteUnrecorded(Kept(("stock", ["stock.md"]))));

        Assert.Equal("live stock levels", File.ReadAllText(Path.Combine(folder, "stock.md")));
        Assert.Equal(["stock.md"], Directory.EnumerateFiles(folder).Select(Path.GetFileName));
    }

    [Fact]
    public void A_location_that_is_a_junction_is_replaced_and_nothing_it_reaches_is_touched()
    {
        // A link is never entered (SPEC.md §9.5), the location itself included: what it reaches may be anybody's.
        var real = Directory.CreateDirectory(Path.Combine(_base, "real-location")).FullName;
        File.WriteAllText(Path.Combine(Directory.CreateDirectory(Path.Combine(real, "stock")).FullName, "stock.md"), "x");
        Directory.CreateDirectory(Path.Combine(real, "gone"));
        var linked = Path.Combine(_base, "alpha.db.files");
        Junction(linked, real);
        var location = new FileLocationStore(linked, Path.Combine(_base, "alpha.db.files-incoming"));
        location.Prepare();

        Assert.Empty(location.DeleteUnrecorded(Kept(("stock", ["stock.md"]))));

        Assert.True(File.Exists(Path.Combine(real, "stock", "stock.md")));
        Assert.True(Directory.Exists(Path.Combine(real, "gone")));
        Assert.True(Directory.Exists(linked));
        Assert.Null(new DirectoryInfo(linked).LinkTarget);
        Assert.False(File.Exists(Path.Combine(linked, "stock", "stock.md")));
    }

    private static Dictionary<string, IReadOnlySet<string>> Kept(params (string Folder, string[] Names)[] folders) =>
        folders.ToDictionary(
            f => f.Folder,
            f => (IReadOnlySet<string>)f.Names.ToHashSet(StringComparer.Ordinal),
            StringComparer.Ordinal);

    private (FileLocationStore Location, string Outside) Prepare()
    {
        var outside = Directory.CreateDirectory(Path.Combine(_base, "outside")).FullName;
        File.WriteAllText(Path.Combine(outside, "precious.txt"), "not Parley's");

        var location = new FileLocationStore(Path.Combine(_base, "alpha.db.files"), Path.Combine(_base, "alpha.db.files-incoming"));
        location.Prepare();

        return (location, outside);
    }

    private static void Junction(string link, string target)
    {
        if (!OperatingSystem.IsWindows())
        {
            Directory.CreateSymbolicLink(link, target);
            return;
        }

        Run("mklink", $"/J \"{link}\" \"{target}\"");
        Assert.True(new DirectoryInfo(link).LinkTarget is not null, "no junction was made");
    }

    private static void Run(string command, string arguments)
    {
        using var made = Process.Start(new ProcessStartInfo("cmd.exe", $"/c {command} {arguments}")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        })!;
        made.WaitForExit();
        Assert.Equal(0, made.ExitCode);
    }

    private static async Task<ParleyServer> StartAsync(string store)
    {
        var runtime = BrokerRuntime.Open(ProjectName.Create("test"), store, Parley.Broker.BrokerOptions.Default, TimeProvider.System);

        return await ParleyServer.StartAsync(runtime, "http://127.0.0.1:0", Ct);
    }
}
