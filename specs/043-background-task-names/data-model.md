# Data model: Named background tasks

Nothing here is stored. Every entity lives in memory: in the node's process, or in the broker's beside the count it
already keeps (SPEC.md §11.6).

## RunningTask (node to broker)

One task the runner lists in `background_tasks_changed`.

| Field | Type | Rule |
|---|---|---|
| `Id` | string | The runner's `task_id`. 1 to 64 characters of letters, digits, `-` and `_`; a task with any other id is counted and not named. |
| `Kind` | enum | `Agent` (`local_agent`), `Command` (`local_bash`), `Other` (any other `task_type`). |
| `Label` | string? | The runner's `description`, cut to 64 characters and neutralised (`BackgroundTaskText.Name`). Absent when the runner gave none. |

A report lists at most 200 tasks with their ids and labels; the count still says how many there are in all.

## TaskDescription (agent to broker)

What an agent says about one of its own tasks (`describe_background_task`).

| Field | Type | Rule |
|---|---|---|
| `TaskId` | string | As `RunningTask.Id`. Refused otherwise. |
| `Name` | string | Required. 1 to 64 characters after neutralising. |
| `Description` | string? | Up to 300 characters after neutralising. |
| `Link` | TaskLink? | Kept only when it is one of the kinds below; otherwise dropped and the reply says so. |

## TaskLink

| Kind | Written by the agent as | Rule |
|---|---|---|
| `Exchange` | `exchange #N` or `#N` | N a positive whole number. |
| `Conversation` | `conversation:<id>` | `<id>` matches `ConversationName`. |
| `File` | `file:<name>` | `<name>` matches the placed-file name grammar. |
| `Web` | `https://host/...` | Absolute, `https` only, a host, no user info, at most 500 characters. |

## BackgroundTask (broker to operator surfaces)

What a registry row carries for each running task: the node's report and the agent's description merged.

| Field | From |
|---|---|
| `Id` | The node. |
| `Kind` | The node. |
| `Name` | The agent's name for it, else the runner's label, else absent (surfaces draw "Background task"). |
| `Description` | The agent's, or absent. |
| `Link` | The agent's, or absent. |

Ordered as the runner listed them. `TurnStanding.BackgroundTasks` stays the count and may exceed the list's length.

## Lifetimes (broker)

| Event | Running list | Descriptions |
|---|---|---|
| Node reports a new list | Replaced. | Kept. Only those whose id is listed are shown. |
| Node reports no tasks | Cleared. | Cleared. |
| Node leaves, or a new process is launched under the name | Cleared. | Cleared. |
| Broker restarts | Gone. | Gone. |
| An agent describes a 201st id | Unchanged. | The oldest description goes. |
