namespace Parley.Core;

/// <summary>
/// What a conforming broker exposes to an operator console. Broker V1.
/// </summary>
/// <remarks>
/// <para>
/// This is the declaration of SPEC.md §10.2, and it is the capability list rather than a
/// description of one. A list in prose beside an interface is a second copy that drifts the
/// moment somebody adds a method; anything reading this already knows what a broker must do, and
/// the compiler fails whoever does not implement it.
/// </para>
/// <para>
/// <b>Clients adapt to the broker, not the broker to its clients.</b> What a console may do is
/// what this says and not what it happens to find on the far end. There are many clients and one
/// store, and a broker that bent to each of them would be several contracts wearing one name —
/// with the disagreement surfacing as one console showing something no other client can see.
/// <see cref="ImplementsAsync"/> is asked first for that reason: a contract whose version cannot
/// be discovered is one nobody can rely on, and without it a client cannot tell a broker of the
/// wrong version from one that is switched off.
/// </para>
/// <para>
/// <b>It carries no agent operation, and never will.</b> §10.1's surface is a different type for
/// a different caller; the two are kept apart structurally rather than by naming, because the
/// naming convention is what §10.2 opens by admitting is thin.
/// </para>
/// <para>
/// Declared here, in the domain project, because it is the one place a client and a server can
/// both see and it drags in nothing: no transport, no storage, no I/O. An implementation over
/// gRPC and an implementation over anything else are both implementations of this.
/// </para>
/// </remarks>
public interface IParleyBrokerClient
{
    /// <summary>Which protocol this broker speaks, which build it is, and whether its store has the shape that build expects.</summary>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>What the broker said about itself; the protocol is rendered as <see cref="ProtocolVersion"/> renders one.</returns>
    Task<BrokerIdentity> ImplementsAsync(CancellationToken cancellationToken = default);

    /// <summary>The whole registry, including the workspace each agent works in.</summary>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>Every registered agent.</returns>
    /// <remarks>
    /// Returns <see cref="AgentRecord"/> and not <see cref="AgentSummary"/>. The two differ by
    /// exactly what §10.1 withholds from an agent, which is why this operation is named
    /// <c>registry</c> in the document rather than <c>agents</c>.
    /// </remarks>
    Task<IReadOnlyList<AgentRecord>> RegistryAsync(CancellationToken cancellationToken = default);

    /// <summary>Which conversations this broker holds, most recent activity first.</summary>
    /// <param name="participant">Restrict to conversations this agent is party to, or null for all.</param>
    /// <param name="sinceUtc">Only those active at or after this instant, if given.</param>
    /// <param name="untilUtc">Only those active at or before this instant, if given.</param>
    /// <param name="pageToken">Where to resume, or null for the first page.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>One page of summaries, and the token for the next.</returns>
    /// <remarks>
    /// Exists because <see cref="ExportAsync"/> takes an identifier nothing else produces. The
    /// identifier is opaque and chosen by an agent (§5.0), so a person does not know one and
    /// cannot guess it.
    /// </remarks>
    /// <param name="includeArchived">
    /// Whether to list archived conversations as well. False omits them and reports how many were
    /// omitted, because an archived conversation goes on receiving messages.
    /// </param>
    Task<ConversationPage> ConversationsAsync(
        AgentName? participant = null,
        DateTimeOffset? sinceUtc = null,
        DateTimeOffset? untilUtc = null,
        string? pageToken = null,
        bool includeArchived = false,
        CancellationToken cancellationToken = default);

    /// <summary>Every message in a conversation, and the state of every exchange they belong to.</summary>
    /// <param name="conversationId">The conversation.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The messages and the exchange states, read at one instant.</returns>
    Task<ConversationExport> ExportAsync(
        string conversationId, CancellationToken cancellationToken = default);

    /// <summary>Who is waiting on whom.</summary>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>One edge per agent waiting on another.</returns>
    /// <remarks>
    /// So that an operator can see a discussion nobody is going to finish. Reads across pairs,
    /// which is what makes it an operator operation. Edges rather than
    /// <see cref="WaitForGraph"/>: that type answers whether a new question would close a cycle
    /// and is never read back, which is right for its purpose and useless for showing somebody
    /// what is stuck.
    /// </remarks>
    Task<IReadOnlyList<WaitingEdge>> WaitingAsync(CancellationToken cancellationToken = default);

    /// <summary>What the size cap refused, per agent and message type.</summary>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The counts.</returns>
    Task<IReadOnlyList<SizeRefusal>> RefusalsAsync(CancellationToken cancellationToken = default);

    /// <summary>What clarification actually cost.</summary>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The report.</returns>
    Task<ClarifyReport> ClarificationsAsync(CancellationToken cancellationToken = default);

    /// <summary>What Results have said: outcomes by responder, blockers by kind, sources, and closures.</summary>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The report.</returns>
    Task<ResultsReport> ResultsAsync(CancellationToken cancellationToken = default);

    /// <summary>Work in front of the person: open exchanges gone quiet or owed by a retired agent, and cycles.</summary>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The flags, derived at the moment of reading.</returns>
    /// <remarks>
    /// Nothing ends a piece of work but its participants or its person (§6, §14), so an exchange
    /// that stops moving is shown here for the person to act on.
    /// </remarks>
    Task<FlagReport> FlagsAsync(CancellationToken cancellationToken = default);

    /// <summary>Withdraw an agent, so nothing routes to it and it is not listed.</summary>
    /// <param name="agent">Who to retire.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>Whether anything changed.</returns>
    Task<bool> RetireAsync(AgentName agent, CancellationToken cancellationToken = default);

    /// <summary>Withdraw a retirement.</summary>
    /// <param name="agent">Who to reinstate.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>Whether anything changed.</returns>
    Task<bool> ReinstateAsync(AgentName agent, CancellationToken cancellationToken = default);

    /// <summary>Record that a person accepted a statement of what an agent owns.</summary>
    /// <param name="agent">Whose line it is.</param>
    /// <param name="acceptance">What was agreed, and how they arrived at it.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>Whether it was recorded, and what peers read now beside what was accepted.</returns>
    /// <remarks>
    /// <b>It does not write the registry's <c>owns</c>.</b> That is filled from the launch
    /// environment when the agent attaches (§12.3), so an accepted line takes effect on the agent's
    /// next start and this returns both halves so a surface can say the two differ. No operation
    /// accepts an <c>owns</c> line from the agent it describes, and this one records a person's
    /// decision rather than making one.
    /// </remarks>
    Task<OwnershipRecord> RecordOwnershipAsync(
        AgentName agent, OwnershipAcceptance acceptance, CancellationToken cancellationToken = default);

    /// <summary>The Auto Accept switch, and every agent's latest question from Parley (§10.2 <c>facilitation</c>).</summary>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>What a person reviews ownership from.</returns>
    Task<FacilitationReport> FacilitationAsync(CancellationToken cancellationToken = default);

    /// <summary>Turn the project's Auto Accept switch on or off (§10.2 <c>set_auto_accept</c>).</summary>
    /// <param name="on">Whether answers become lines with no person involved.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The switch as now set.</returns>
    Task<bool> SetAutoAcceptAsync(bool on, CancellationToken cancellationToken = default);

    /// <summary>Have Parley ask an agent what it owns, as a new question (§10.2 <c>ask_again</c>).</summary>
    /// <param name="agent">Who to ask.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>Whether it was asked, and why not.</returns>
    /// <remarks>Needs no seat: Parley sends it, not the person.</remarks>
    Task<FacilitationAction> AskAgainAsync(AgentName agent, CancellationToken cancellationToken = default);

    /// <summary>Send a person's reply to an agent that asked Parley something back (§10.2 <c>reply_to_asked_back</c>).</summary>
    /// <param name="agent">The agent that asked back.</param>
    /// <param name="text">The person's words, recorded as theirs.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>Whether it was sent, and why not.</returns>
    Task<FacilitationAction> ReplyToAskedBackAsync(
        AgentName agent, string text, CancellationToken cancellationToken = default);

    /// <summary>Discard an answer waiting for a person (§10.2 <c>discard_answer</c>).</summary>
    /// <param name="agent">Whose answer.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>Whether it was discarded, and why not.</returns>
    Task<FacilitationAction> DiscardAnswerAsync(AgentName agent, CancellationToken cancellationToken = default);

    /// <summary>Delete an agent and every conversation it was party to.</summary>
    /// <param name="agent">Who to purge.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>What was destroyed.</returns>
    /// <remarks>
    /// Destroys its peers' side of those conversations too, and cannot be undone. A client that
    /// offers this without first saying what it will destroy has offered the wrong thing.
    /// </remarks>
    Task<PurgeOutcome> PurgeAsync(AgentName agent, CancellationToken cancellationToken = default);

    /// <summary>Close a conversation regardless of its participants' votes.</summary>
    /// <param name="conversationId">The conversation.</param>
    /// <param name="reason">Why, told to every participant holding open work; or null.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>Whether anything changed.</returns>
    /// <remarks>
    /// How the person ends work (§5.0). Every exchange still open ends <c>closed</c>, and every
    /// participant holding one is sent the broker's <c>closed</c> Result in the same write (§14).
    /// </remarks>
    Task<bool> ForceConcludeAsync(
        string conversationId, string? reason = null, CancellationToken cancellationToken = default);

    /// <summary>Apply the retention policy now rather than waiting for the sweep.</summary>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>What was removed, and what the floor held back.</returns>
    Task<PruneResult> PruneAsync(CancellationToken cancellationToken = default);

    /// <summary>Put a conversation out of the way.</summary>
    /// <param name="conversationId">The conversation.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>Whether anything changed.</returns>
    /// <remarks>
    /// <para>
    /// <b>Nothing but the listing changes.</b> Messages are still delivered, exchanges still open
    /// and close, and no agent is told. It is an operator's filing decision, made durable so every
    /// operator and every client sees the same one rather than each keeping its own.
    /// </para>
    /// <para>
    /// <b>A message into an archived conversation does not un-archive it.</b> That was decided
    /// rather than left to fall out: the alternative lets any agent reverse an operator's decision
    /// by sending something, which is the one direction of authority this protocol does not have.
    /// What stops a busy conversation hiding is <see cref="ConversationPage.ArchivedOmitted"/>,
    /// not the state changing underneath somebody.
    /// </para>
    /// </remarks>
    Task<bool> ArchiveAsync(string conversationId, CancellationToken cancellationToken = default);

    /// <summary>Return an archived conversation to the ordinary listing.</summary>
    /// <param name="conversationId">The conversation.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>Whether anything changed.</returns>
    Task<bool> UnarchiveAsync(string conversationId, CancellationToken cancellationToken = default);

    /// <summary>Delete a conversation and everything belonging to it.</summary>
    /// <param name="conversationId">The conversation.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>What was destroyed, per table.</returns>
    /// <remarks>
    /// <para>
    /// <b>Every participant's half goes</b>, including peers who did nothing but take part. That
    /// is not collateral being tolerated; it is what deleting a conversation means, since one with
    /// a side removed is a record that misleads whoever reads it next — the argument
    /// <see cref="PurgeOutcome"/> makes about an agent, applied to a narrower subject.
    /// </para>
    /// <para>
    /// <b>An open exchange inside it is not closed: it ceases to exist</b>, so
    /// the agent waiting on it is never told anything at all. That is a different outcome from
    /// every other way an exchange can end and the one an operator is least likely to predict, so
    /// a surface offering this without first naming it has warned nobody.
    /// </para>
    /// </remarks>
    Task<DestroyOutcome> DestroyAsync(
        string conversationId, CancellationToken cancellationToken = default);

    /// <summary>Ask the node that can launch an agent to do so now.</summary>
    /// <param name="agent">The agent.</param>
    /// <param name="because">Why, for whoever reads the node's log.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>Whether a node accepted the request.</returns>
    /// <remarks>
    /// <para>
    /// <b>This causes a launch and never a registration.</b> The launched adapter registers the one
    /// agent it attaches, by §12.3's ordinary route and under the identity §13 requires its launch
    /// to fix. Writing a registration here instead would be the forgery §12.3 forbids wearing an
    /// operator's clothes: a registration is a claim that an agent exists and has spoken, and only
    /// the agent can make it.
    /// </para>
    /// <para>
    /// It follows that an agent nothing has ever registered can still be started, because what
    /// makes the role reachable is the node's catalogue rather than the registry. That is the whole
    /// of why enrolling a repository need not leave an agent nobody can reach until somebody opens
    /// that directory by hand.
    /// </para>
    /// </remarks>
    Task<bool> StartAsync(
        AgentName agent, string because, CancellationToken cancellationToken = default);

    /// <summary>Place a file as the person, into a conversation that exists (SPEC.md §10.2 <c>place_persons_file</c>).</summary>
    /// <param name="conversationId">The conversation.</param>
    /// <param name="name">The name to place it under; characters outside the set become <c>_</c> (§9.5).</param>
    /// <param name="content">The bytes, read to their end.</param>
    /// <param name="cancellationToken">Cancels the call; nothing is left behind.</param>
    /// <returns>What the placement came to.</returns>
    /// <remarks>
    /// The person's, because no agent places as anybody but itself. A file in the location enters only by placement:
    /// one copied into a folder by hand is never listed, so this is how the person hands an agent a file. A placed
    /// file is never changed, so a new version goes under a new name.
    /// </remarks>
    Task<PersonPlacement> PlacePersonsFileAsync(
        string conversationId, string name, Stream content, CancellationToken cancellationToken = default);

    /// <summary>List a page of the file location (SPEC.md §9.5).</summary>
    /// <param name="conversationId">One conversation's files, or <see langword="null"/> for every one.</param>
    /// <param name="pageSize">Files per page; zero or less for the broker's default. The broker bounds it.</param>
    /// <param name="pageToken">The token a previous page returned, or <see langword="null"/> for the first.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The page, and the placement counts per agent.</returns>
    /// <remarks>
    /// Named apart from §10.1's <c>file_list</c>, because the person is given what an agent is not: how many
    /// placements, and how many bytes, each agent has made — the evidence a size limit would be set from.
    /// </remarks>
    Task<SharedFileListing> ListSharedFilesAsync(
        string? conversationId = null,
        int pageSize = 0,
        string? pageToken = null,
        CancellationToken cancellationToken = default);

    /// <summary>Remove files, whichever agent placed them.</summary>
    /// <param name="files">At most <see cref="FileLimits.NamesPerRemoval"/> files.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>What was removed and what was refused.</returns>
    /// <remarks>
    /// The person's, because it removes files other agents placed and no agent may (§9.5). A surface offering this
    /// says which files and how much space before it calls, and takes a deliberate confirmation.
    /// </remarks>
    Task<RemoveFilesOutcome> RemoveFilesAsync(
        IReadOnlyList<FileRef> files, CancellationToken cancellationToken = default);

    /// <summary>Remove every file in the location.</summary>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>How many files went, and how much space.</returns>
    /// <remarks>
    /// Named apart from <see cref="PruneAsync"/>, which applies retention to messages and never touches a file. A
    /// surface offering this says how many files and how much space first, and takes the project's name typed back.
    /// </remarks>
    Task<PruneFilesOutcome> PruneFilesAsync(CancellationToken cancellationToken = default);

    /// <summary>Say where the location, a conversation's folder or a file is, on the broker's machine.</summary>
    /// <param name="conversationId">The conversation, or <see langword="null"/>.</param>
    /// <param name="name">The file, or <see langword="null"/> for a folder.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The machine and path, or why there is none.</returns>
    /// <remarks>
    /// What lets a surface open a file in place: on the broker's machine it launches, and anywhere else it names the
    /// machine and the path and launches nothing, because the file is not there to open (§10.2).
    /// </remarks>
    Task<FileLocationAnswer> DescribeFileLocationAsync(
        string? conversationId = null,
        string? name = null,
        CancellationToken cancellationToken = default);

    /// <summary>List the project's roadmaps.</summary>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>One summary per roadmap.</returns>
    /// <remarks>
    /// The list a person reaches roadmaps from (SPEC.md §5.7). Named apart from nothing on the agent side,
    /// because no agent operation reads a roadmap and none may be added (principle 2.12).
    /// </remarks>
    Task<IReadOnlyList<RoadmapSummary>> RoadmapsAsync(CancellationToken cancellationToken = default);

    /// <summary>Read one roadmap whole.</summary>
    /// <param name="id">The roadmap.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The roadmap, or <see langword="null"/> when there is none by that identifier.</returns>
    /// <remarks>
    /// <b>Facts and never states.</b> Each live item carries <see cref="ItemFacts"/>, which a surface turns into
    /// where the item stands, so that a view costs this read, the wait-for edges and the registry however many
    /// items there are, and so that no state is stored for a second reader to disagree with.
    /// </remarks>
    Task<Roadmap?> RoadmapAsync(long id, CancellationToken cancellationToken = default);

    /// <summary>Read the proposals agents have made.</summary>
    /// <param name="state">Which to read; waiting ones by default, and <see langword="null"/> for every state.</param>
    /// <param name="headings">
    /// Whether to read headings only: each title cut to <see cref="RoadmapProposal.HeadingLength"/>, its item count, and
    /// no items.
    /// </param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The proposals, newest first.</returns>
    /// <remarks>
    /// Headings are what a count, a list or a refresh reads: roadmap text has no bound, so whole proposals are as large as
    /// an agent makes them, and a heading is small whatever it wrote. <see cref="ProposalAsync"/> reads one whole.
    /// </remarks>
    Task<IReadOnlyList<RoadmapProposal>> ProposalsAsync(
        ProposalState? state = ProposalState.Waiting, bool headings = false, CancellationToken cancellationToken = default);

    /// <summary>Count the proposals agents have made.</summary>
    /// <param name="state">Which to count; waiting ones by default, and <see langword="null"/> for every state.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>How many there are.</returns>
    /// <remarks>
    /// Reads no proposal, because nothing bounds how many an agent makes and a count is read on every refresh. A list of
    /// headings carries the newest <see cref="RoadmapProposal.HeadingLimit"/>; this says how many there are in all.
    /// </remarks>
    Task<int> ProposalCountAsync(ProposalState? state = ProposalState.Waiting, CancellationToken cancellationToken = default);

    /// <summary>Read one proposal whole, in whatever state it is.</summary>
    /// <param name="id">The proposal.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The proposal, or <see langword="null"/> when there is none by that identifier.</returns>
    Task<RoadmapProposal?> ProposalAsync(long id, CancellationToken cancellationToken = default);

    /// <summary>Turn a proposal into a roadmap, or a revision of one.</summary>
    /// <param name="id">The proposal.</param>
    /// <param name="content">The person's final content, which may be the proposal's own.</param>
    /// <param name="targetRoadmap">The roadmap it revises, or <see langword="null"/> for a new one.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>What it came to, with a sentence when it was refused.</returns>
    /// <remarks>
    /// Validates the whole content (<see cref="RoadmapRules"/>), and records whether it was confirmed as proposed
    /// or edited by comparing it with the stored proposal — never from a flag the caller passes, which would be
    /// right only as often as somebody remembered to set it.
    /// </remarks>
    Task<RoadmapChange> ConfirmProposalAsync(
        long id, RoadmapContent content, long? targetRoadmap, CancellationToken cancellationToken = default);

    /// <summary>Decline a waiting proposal.</summary>
    /// <param name="id">The proposal.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>What it came to.</returns>
    Task<RoadmapChange> DeclineProposalAsync(long id, CancellationToken cancellationToken = default);

    /// <summary>Change a confirmed roadmap: reword, reorder, add or remove items.</summary>
    /// <param name="id">The roadmap.</param>
    /// <param name="revision">The revision the caller read, so two surfaces editing cannot overwrite each other.</param>
    /// <param name="content">The final content.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>What it came to, with a sentence when it was refused.</returns>
    Task<RoadmapChange> EditRoadmapAsync(
        long id, int revision, RoadmapContent content, CancellationToken cancellationToken = default);
}
