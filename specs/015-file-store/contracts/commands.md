# Contract: what agents and people type

## Agent tools (`Parley.Mcp`)

Every path is inside the agent's repository root (`research.md` R9). No tool returns file contents.

A `conversation_id` left out is searched for across the whole project. When several conversations hold the name, the tool
does nothing and answers: *"`schema.md` is in more than one conversation: `tax-inclusive-pricing`, `refund-rounding`.
Which one do you want? Call again with `conversation_id`."* The CLI's `open` and `remove` answer the person the same way. At most 200 conversations are named, followed by
*"and N more"* when there are more.

| Tool | Arguments | Returns |
|---|---|---|
| `parley_file_place` | `path` (in the repository), `name` (optional, defaults to the file's own name; must end in `.md` or `.txt` — place code as `PriceSnapshot.cs.txt`), `conversation_id` (optional) | conversation, name, size, sha256, `placed` / `replaced` (with previous sha256) / `already_current`, or a refusal |
| `parley_file_fetch` | `name`, `conversation_id` (optional), `to` (path in the repository), `expect_sha256` (optional), `replace` (default false) | path, size, sha256, placed_by, `written` / `already_current` (with a warning when the broker refused to record the fetch, so no notice will follow), or a refusal |
| `parley_file_list` | `conversation_id` (optional), `page_size` (optional; 50 by default, at most 200), `page_token` (optional) | a page of files: conversation, name, size, sha256, placed_by, changed_utc; totals for the conversations on the page and for everything listed; `next_page_token` when there is more |
| `parley_file_remove` | `names`: list of file names, at most 200; `conversation_id` (optional, applies to every name; without it each name is looked for across the location) | removed (conversation and name), refused (with reason, and the placer when another placed it), `bytes_freed`; more than 200 names is refused whole as `toomanynames` |

`parley_inbox` additionally returns `file_notices` — at most 200 per check, oldest change first, whatever `limit` says
about messages — each with conversation, name, `sha256_had`, `sha256_now` or `gone: true`, size and time;
`file_notices_waiting` when more are waiting; and a note: *"A file you fetched has changed or gone. Nothing is owed back; fetch it again if
it matters to what you are working on."* When `file_notices_waiting` is not zero the note adds: *"N more are waiting;
call `parley_inbox` again for them."*

## Command line (`parley`)

| Command | Does |
|---|---|
| `parley files list [--conversation <id>]` | Every file with conversation, name, size, sha256 (first 12 characters, `--full` for all 64), placed by, changed; totals per conversation and overall; placements per agent. Follows every page itself and prints one report. "The file location is empty." when it is. |
| `parley files remove <name>... [--conversation <id>]` | Describes each file and its size, asks for confirmation (`--yes` to skip), removes, reports removed / not present / ambiguous. More than 200 names in one command is refused before anything is asked or removed. |
| `parley files prune` | Says how many files and how much space, asks for the project's name typed back (`--yes-remove-every-file` to skip), removes everything, reports. |
| `parley files open <name>... [--conversation <id>]` | On the broker's machine, opens each file with its registered editor (only `.md` and `.txt` files exist in the location); elsewhere prints `<name> is on machine <X>, at <Y>`. |
| `parley files folder [--conversation <id>]` | On the broker's machine, opens the conversation's folder (or the whole location) in the file browser; elsewhere prints `The files are on machine <X>, in <Y>`. |
| `parley waiting <agent> --with-files` (a bare role is that role on this machine) | Prints a third count, the file change notices waiting, after the messages and closure polls. Without the flag the line keeps its two counts, because a waiting hook installed before files existed splits it on its first space. The flag is refused on every other command. |

Every command takes `--project <name>` like the existing operator commands. Output is flattened through the same
line-safety every operator report uses: names are other participants' text.

## Plugin commands (`plugins/parley/commands/`)

`files-list.md`, `files-remove.md`, `files-prune.md`, `files-open.md`, `files-folder.md`. Each carries
`disable-model-invocation: true` — a person types it, a model cannot invoke it — and runs the matching `parley files`
command with the arguments given, showing its output. `files-remove` and `files-prune` pass the confirmation through to the
person rather than answering it.

## Terminal (`parley-terminal`)

A **Files** dialog, opened from a project (whole location) or a conversation (its files):

| Key | Action | Door |
|---|---|---|
| (list) | name, size, short hash, placed by, changed; totals; every page fetched in turn | — |
| `o` | open the selected file | remote shows machine and path |
| `f` | open the folder | remote shows machine and path |
| `d` | remove the selected files | description of files and size, then a confirmation choice |
| `P` | prune every file | description, then the project's name typed back |

`SurfaceTests` holds `RemoveFilesAsync` to `FilesRemoving.cs` and `PruneFilesAsync` to `FilesPruning.cs`, each with its
door text. Launching goes through a `Shell` seam so no test opens an editor or a file browser.

## Web UI (`parley-web-ui`)

A **Files** page per project, filterable by conversation, with the same columns and totals, a page at a time with a link to the next as the
Agents page has; forms for remove (with a
confirmation step naming files and size) and prune (the project's name typed back); **Open** and **Open folder** buttons
that launch on the machine running the web UI, or show the machine and path when that is not the broker's machine
(owner's decision).
