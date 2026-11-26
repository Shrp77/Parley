# Contract: wire changes (proto/parley.proto, protocol 0.21)

## Nodes: the node's report

```proto
message BackgroundWork {
  int32 tasks = 1;
  // Each running task the runner listed, in its order, at most 200; `tasks` still counts them all (section 11.6).
  repeated RunningTask running = 2;
}

message RunningTask {
  string id = 1;              // the runner's task_id
  TaskKind kind = 2;
  string label = 3;           // the runner's description, cut and neutralised; empty when it gave none
}

enum TaskKind {
  TASK_KIND_OTHER = 0;
  TASK_KIND_AGENT = 1;        // a sub-agent
  TASK_KIND_COMMAND = 2;      // a background command
}
```

The broker re-checks every field (id grammar, label bound and neutralising) and drops an entry that fails. It does not
trust the node's cutting.

## Agents: the agent describes its own task

```proto
// The agent's own words for a background task it started, shown to the person only (section 11.6). Kept in memory until
// the agent's background work clears. The reply says nothing about whether the task is running.
rpc DescribeBackgroundTask(DescribeBackgroundTaskRequest) returns (DescribeBackgroundTaskReply);

message DescribeBackgroundTaskRequest {
  string agent = 1;           // the caller, as every Agents request names it
  string task_id = 2;
  string name = 3;            // at most 64 characters
  string description = 4;     // at most 300 characters; may be empty
  string link = 5;            // exchange #N, conversation:<id>, file:<name>, or https://...; may be empty
}

message DescribeBackgroundTaskReply {
  bool kept = 1;              // false only when the task id or the name was refused
  string refused = 2;         // why, when not kept
  bool name_cut = 3;
  bool description_cut = 4;
  string link_dropped = 5;    // why a link given was not kept; empty when none was given or it was kept
}
```

The caller is the attached agent, as with every `Agents` call. An agent can describe only its own tasks. The broker
refuses the person seat, Parley and a caller that is not attached, and every refusal is about what was sent, never
whether the task runs.

## The registry reply's budget

Every agent's row travels in one `RegistryReply`, so the broker spends at most 1 MiB on tasks across it (ids and
framing first, then words), in the order the rows go, no row's words taking more than an eighth of it. A task whose words do not fit travels by id and kind with empty
words, and a surface draws a plain stand-in; later tasks whose words fit keep theirs (`TaskWordBudget`).

## Operators: the registry row

```proto
message RegistryRow {
  ...
  int32 background_tasks = 19;                  // unchanged: the count
  repeated BackgroundTaskRow running_tasks = 23; // each running task, named, at most 200
}

message BackgroundTaskRow {
  string id = 1;
  TaskKind kind = 2;
  string name = 3;            // the agent's name, else the runner's label, else empty
  string description = 4;     // the agent's, or empty
  string link = 5;            // the agent's, in the canonical forms above, or empty
  bool said = 6;              // the agent named it itself, not only its runner
}
```

The agent roster (`ListAgents`) carries none of it. `AgentsCannotSeeTurnsTests` is extended to the new reply and roster
fields.
