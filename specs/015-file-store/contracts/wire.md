# Contract: the wire (protocol 0.6)

Additions to `proto/parley.proto`. SPEC.md §10.1 and §10.2 are the authority; this is the shape they will describe.
Numbers are placeholders until the proto is edited; nothing existing is renumbered.

## Agents service (§10.1)

```proto
// Places a file from the agent's repository into a conversation's files. The first frame is a header; every
// later frame carries bytes. The conversation is assigned as a Request's is (section 5.0).
rpc PlaceFile(stream PlaceFileFrame) returns (PlaceFileReply);

// Streams one whole content of a file. The first frame is a header; later frames carry bytes. While the call
// is open the stored file is read-only (section 9). Cancelling the call ends the hold.
rpc FetchFile(FetchFileRequest) returns (stream FetchFileFrame);

// Reports that a fetch was written and verified, so the broker records it and change notices can follow.
// Refused for a fetch the broker did not stream to this agent, or a hash it did not stream.
rpc FetchedFile(FetchedFileRequest) returns (FetchedFileReply);

rpc ListFiles(ListFilesRequest) returns (FilesReply);

// Removes files the calling agent placed; each other name is refused, naming its placer.
rpc RemoveOwnFiles(RemoveFilesRequest) returns (RemoveFilesReply);
```

```proto
message PlaceFileFrame {
  oneof frame {
    PlaceFileHeader header = 1;
    bytes chunk = 2;                     // at most 256 KiB
  }
}

message PlaceFileHeader {
  string agent = 1;                      // the adapter's launch identity, as every Agents call carries
  string conversation_id = 2;            // empty: assigned by the broker
  string name = 3;                       // section 9 name rules
}

message PlaceFileReply {
  bool ok = 1;
  FileRejection rejection = 2;
  string conversation_id = 3;
  string name = 4;
  int64 size_bytes = 5;
  string sha256 = 6;
  PlaceOutcome outcome = 7;              // PLACED, REPLACED, ALREADY_CURRENT
  string previous_sha256 = 8;            // set on REPLACED
  string placed_by = 9;                  // on NAME_PLACED_BY_ANOTHER
  repeated string assignable_conversations = 10;
}

message FetchFileRequest {
  string agent = 1;
  string conversation_id = 2;            // empty: resolved when exactly one conversation holds the name
  string name = 3;
  string expect_sha256 = 4;              // empty: no expectation
}

message FetchFileFrame {
  oneof frame {
    SharedFileView header = 1;           // size, hash, placer of the content being streamed
    bytes chunk = 2;
    FileRefusal refusal = 3;             // sent instead of a header on refusal
  }
}

message FileRefusal {
  FileRef file = 1;
  FileRejection rejection = 2;
  string placed_by = 3;                  // on FILE_REJECTION_NAME_PLACED_BY_ANOTHER
  repeated string conversations = 4;     // on FILE_REJECTION_AMBIGUOUS_NAME: at most 200 conversations holding the
                                         // name; the caller repeats the call naming one
  int32 conversations_more = 5;          // how many more hold the name than are listed
}

message FetchedFileRequest {
  string agent = 1;
  string conversation_id = 2;
  string name = 3;
  string sha256 = 4;                     // what the adapter verified it wrote
}

message FetchedFileReply {
  bool ok = 1;
  FileRejection rejection = 2;           // NOT_FETCHED: no completed fetch of that content by this agent
}

message ListFilesRequest {
  string agent = 1;                      // empty on the operator service
  string conversation_id = 2;            // empty: every conversation
  int32 page_size = 3;                   // 0: 50; never more than 200 whatever is asked (research.md R17)
  string page_token = 4;                 // empty for the first page
}

message SharedFileView {
  string conversation_id = 1;
  string name = 2;
  int64 size_bytes = 3;
  string sha256 = 4;
  string placed_by = 5;                  // empty: the person
  string placed_utc = 6;
  string changed_utc = 7;
}

message FilesReply {
  repeated SharedFileView files = 1;     // one page, ordered by conversation then name key
  repeated ConversationFilesTotal totals = 2;   // the conversations on this page, each counted whole
  int64 total_bytes = 3;                 // every file in the location, or in the named conversation, whatever the page
  repeated FilePlacementCount placements = 4;   // operator service only
  string next_page_token = 5;            // empty at the end
  int32 total_files = 6;                 // the files total_bytes counts
}

message ConversationFilesTotal {
  string conversation_id = 1;
  int32 files = 2;                       // every file in the conversation, not only those on the page
  int64 bytes = 3;
}

message FilePlacementCount {
  string agent = 1;                      // kept after the agent is purged
  int64 placements = 2;                  // placements, and replacements with new content
  int64 bytes = 3;
  string last_utc = 4;
}

message RemoveFilesRequest {
  string agent = 1;                      // empty on the operator service
  repeated FileRef files = 2;            // at most 200; more is refused whole, removing nothing
}

message FileRef {
  string conversation_id = 1;            // empty: resolved when exactly one conversation holds the name
  string name = 2;
}

message RemoveFilesReply {
  repeated FileRef removed = 1;
  repeated FileRefusal refused = 2;      // not present, ambiguous, placed by another
  int64 bytes_freed = 3;
  FileRejection rejection = 4;           // TOO_MANY_NAMES: the request named more than 200; nothing was removed
}

enum FileRejection {
  FILE_REJECTION_UNSPECIFIED = 0;
  FILE_REJECTION_NONE = 1;
  FILE_REJECTION_BAD_NAME = 2;
  FILE_REJECTION_NAME_PLACED_BY_ANOTHER = 3;
  FILE_REJECTION_NO_SUCH_FILE = 4;
  FILE_REJECTION_AMBIGUOUS_NAME = 5;       // several conversations hold the name; nothing was done
  FILE_REJECTION_CONVERSATION_NOT_ASSIGNABLE = 6;
  FILE_REJECTION_CONVERSATION_CLOSED = 7;
  FILE_REJECTION_CHANGED = 8;            // expect_sha256 did not match, or the content changed under the stream
  FILE_REJECTION_INCOMPLETE = 9;         // the placement stream ended early
  FILE_REJECTION_NO_SPACE = 10;
  FILE_REJECTION_EXTENSION_NOT_ALLOWED = 11;  // only .md and .txt; code is placed as Name.cs.txt
  FILE_REJECTION_NOT_FETCHED = 12;       // FetchedFile named no completed fetch of that content by this agent
  FILE_REJECTION_TOO_MANY_NAMES = 13;    // a removal request named more than 200 files
}

enum PlaceOutcome {
  PLACE_OUTCOME_UNSPECIFIED = 0;
  PLACE_OUTCOME_PLACED = 1;              // a new name
  PLACE_OUTCOME_REPLACED = 2;            // new content under the placer's own name; previous_sha256 is set
  PLACE_OUTCOME_ALREADY_CURRENT = 3;     // identical content; nothing was written or counted
}
```

## Inbox (§4.2, §5.0)

```proto
message InboxReply {
  repeated DeliveredMessage messages = 1;
  repeated ClosurePoll closure_polls = 2;
  repeated FileNotice file_notices = 3;  // not messages; nothing is owed back; at most 200, oldest change first
  int32 file_notices_waiting = 4;        // still waiting after this reply, counted as Notice counts them
}

message FileNotice {
  string conversation_id = 1;
  string name = 2;
  string sha256_had = 3;
  string sha256_now = 4;                 // empty: the file is gone
  int64 size_bytes = 5;
  string changed_utc = 6;
}

message Notice {                         // the Waiting reply
  int32 messages_waiting = 1;
  int32 closure_polls_waiting = 2;
  int32 file_notices_waiting = 3;
}
```

## Operators service (§10.2)

```proto
rpc ListSharedFiles(ListFilesRequest) returns (FilesReply);
rpc RemoveFiles(RemoveFilesRequest) returns (RemoveFilesReply);   // any placer
rpc PruneFiles(Empty) returns (PruneFilesReply);
rpc DescribeFileLocation(FileLocationRequest) returns (FileLocationReply);
```

```proto
message PruneFilesReply {
  int32 files_removed = 1;
  int64 bytes_freed = 2;
}

message FileLocationRequest {
  string conversation_id = 1;            // empty: the whole location
  string name = 2;                       // empty: the folder
}

message FileLocationReply {
  bool ok = 1;
  FileRefusal refusal = 2;               // when not ok: no such file, or an ambiguous name with its conversations
  string machine = 3;                    // the broker's machine name (section 4.1.1)
  string path = 4;                       // absolute, on that machine
}
```

## Behaviour the messages cannot show

- The broker computes every `sha256`; a placing adapter's own hash is never sent.
- `PlaceFile` stages in `<store>.files-incoming/`, beside the location, and commits only when the stream completes; the row insert is where a concurrent
  placement of the same new name loses (unique index).
- `FetchFile` holds the file read-only for the life of the call and ends with `FILE_REJECTION_CHANGED` if the streamed
  bytes' hash differs from the header's.
- `FetchedFile` is required before a fetch counts: a fetch the adapter could not verify records nothing, and a report
  naming a fetch the broker did not stream to that agent, or a hash it did not stream, is refused
  (`FILE_REJECTION_NOT_FETCHED`).
- Nothing about a file raises a wake.
- `ListFiles` and `ListSharedFiles` bound the page whatever `page_size` asks (50 by default, 200 at most), and
  `DrainInbox` returns at most 200 file notices whatever `limit` asks; the inbox `limit` bounds messages only. Leftover
  notices are not re-woken: they wait for the next check (research.md R7, R17).
- An ambiguity refusal lists at most 200 conversations with `conversations_more`, and `RemoveFiles` and `RemoveOwnFiles`
  refuse a request of more than 200 names before removing anything (research.md R17).
- A conversation's `conversation_folders` row is written with its first file and never changed; a rename in the folder
  updates the file's row in place (data-model.md).
