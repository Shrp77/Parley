# Contributing

Issues and discussion are welcome, especially on the open questions at the end of
[SPEC.md](../SPEC.md).

## The specification comes first

`SPEC.md` is the primary document of this repository, and the code is a reference implementation
of it. If the code and the specification disagree, that is a defect in one of them. Which one is a
decision to make, not an assumption. Please do not change code to match the spec, or the spec to
match code, without saying so.

Read the part of `SPEC.md` that covers the behaviour you are about to change. Design rationale
lives in `specs/`, in the specification's own history section, and in git.

## Building

You need the .NET 10 SDK.

```
dotnet build
dotnet test
```

A green build is the whole lint gate. Warnings are errors, every public member must be documented,
and a high or critical package advisory fails the build. CI runs on Windows, Linux and macOS.

Tests run on Microsoft.Testing.Platform, chosen in `global.json`. If `dotnet test` reports that
zero tests ran, run the test executables directly, for example
`tests/Parley.Store.Tests/bin/Debug/net10.0/Parley.Store.Tests.exe`.

## How the code is laid out

| Project | What it holds |
|---|---|
| `Parley.Core` | The domain vocabulary and the broker contracts. No I/O and no package dependencies |
| `Parley.Store` | SQLite storage and migrations |
| `Parley.Broker` | The agent operations and the operator operations. Knows nothing of transports |
| `Parley.Transport` | The gRPC contract, generated from `proto/parley.proto` |
| `Parley.Server` | The broker process, and the only thing that opens the database |
| `Parley.Configuration` | The project manifest and the machine's catalogue |
| `Parley.Setup` | What a person does to a machine: enrolling, naming projects, autostart |
| `Parley.Client` | What a surface needs to show a deployment and seat a person |
| `Parley.Terminal` | `parley-terminal`, on Terminal.Gui |
| `Parley.Web` and `Parley.WebUi` | The web console, and the tool that starts it |
| `Parley.Cli` | `parley`, the command line |
| `Parley.Headless` | The node, which starts and supervises agents |
| `Parley.Mcp` | `parley-mcp`, the agent's adapter |
| `plugins/parley` | The Claude Code plugin and its marketplace catalogue |

Several rules about what may depend on what are enforced by tests that walk the project graph,
for example that no surface can reach SQLite, and that the agent adapter cannot reach any
operator operation. Agent operations and operator operations are on different types, and different
gRPC services, on purpose.

## Making a change

- **Bump `VersionPrefix`** in `Directory.Build.props` in the same change as any change to `src`.
  While the major version is zero the minor number carries breaking changes and the patch carries
  everything else. Documentation, tests, CI and the plugin's text do not move it.
- **A store is kept across upgrades.** Schema 1 is fixed, and every schema change is a new
  migration (`002_…` onwards) that upgrades a store in place and keeps its rows. The tools are
  always installed together, so the wire and the protocol still change freely.
- **Never `git checkout` over uncommitted work.** Copy it first.
- **Keep documentation to one statement of each rule.** When a rule changes, change it
  everywhere it is stated and remove the old wording. Git keeps the history.
- **Pull requests are squash-merged**, so the pull request title becomes the commit message. Use
  [Conventional Commits](https://www.conventionalcommits.org/). Put the design reasoning in the
  description.

## Reviewing

Specification checks run as tests in `tests/Parley.Spec.Tests`: section references resolve, tables
are well formed, the protocol file agrees with the document, and every schema fragment executes
against SQLite. If you find a rule worth holding, add a case there.

## License

By contributing you agree that your work is licensed under Apache-2.0. See
[LICENSE](../LICENSE).
