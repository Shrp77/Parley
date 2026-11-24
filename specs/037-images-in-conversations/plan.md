# Implementation Plan: Images in the conversation, a Media view, and recipient chips

**Branch**: `037-images-in-conversations`, from `035-material-3-web-ui` at a9f45e47 | **Date**: 2026-11-08 | **Spec**: [spec.md](spec.md)
**Input**: Feature specification from `specs/037-images-in-conversations/spec.md`

## Summary

A placed image shows in the conversation's transcript at its placement time and opens full size. A Media view lists a
conversation's images as a grid and its other files as a list. The composer's To line shows chips that remove an agent
in one click. The console needs another participant's bytes to show an image, including when the broker is on another
machine. So the broker gains one operator read, `ReadSharedFile`, that streams a file and records nothing. The console
serves an image only after checking its leading bytes, with a policy that lets nothing in it run. The terminal shows
each placement as a transcript line and gets the same Media list. It uses the new read to open a copy when the broker's
files are not on this machine. SPEC.md §9.5 replaces the old rule and records the reduction in the control.

## Technical Context

**Language/Version**: C# on .NET 10
**Primary Dependencies**: ASP.NET Core Razor Pages (web), Terminal.Gui v2 (terminal), gRPC (transport). No new package.
**Storage**: none. No schema change, no migration. Placements already record placer and time.
**Testing**: xUnit v3 on MTP. Server tests over loopback for the read; client unit tests for the signature check and
the transcript interleave; web tests over the scripted broker for the image handler's headers and refusals and for
Media; terminal tests through the harness for the Media list and the transcript line.
**Target Platform**: Windows, Linux, macOS
**Project Type**: protocol reference implementation with two operator surfaces
**Performance Goals**: a conversation page reads one file listing alongside its export; images load lazily (SC-005)
**Constraints**: serving changes nothing (FR-014); the followed region stays digest-stable (no per-render values in
image markup); every terminal key declared in a table; one message view on the web
**Scale/Scope**: up to a few hundred files per conversation (one listing page holds 200; the reads page through)

## Constitution Check

| Principle | Holds? | How |
|---|---|---|
| I. The specification is the product | Yes | SPEC.md §9.5 and §10.2 change in the same change as the code, protocol 0.14 → 0.15. |
| II. No authority between agents | Yes | Nothing new is done to or by an agent. |
| III. Identity fixed by the launch | Yes | The read is an operator operation; it takes no identity. |
| IV. Terseness | Yes | No message grows. An image is a file, not a payload; the read is bounded by the 20 MB inline limit on the console's side. |
| V. Invariants in storage | Yes | No new invariant; no schema change. |
| Agent and operator operations apart | Yes | `ReadSharedFile` is on `Operators` only. Agents keep `FetchFile`, which records a fetch. |
| Payloads are inert | Yes | A placement's place in the transcript comes from its record, never from a message's words. |
| Recorded reductions in a control | Yes | §9.5 records the change, what still holds, what answers it, and what remains. |

No violations. Re-checked after Phase 1: unchanged.

## Project Structure

```text
specs/037-images-in-conversations/
├── spec.md  plan.md  research.md  data-model.md  quickstart.md
├── contracts/interfaces.md
├── checklists/requirements.md
└── tasks.md
```

### Source Code (touched)

```text
proto/parley.proto                      ReadSharedFile on Operators (reuses FetchFileFrame)
src/Parley.Core/IParleyBrokerClient.cs  ReadSharedFileAsync; SharedFileRead record (Core/SharedFileRead.cs)
src/Parley.Core/ImageSignature.cs       which names are images, and whether bytes start like one (pure)
src/Parley.Server/FileWork.cs           FetchAsync split: one stream core, agent fetch and operator read on it
src/Parley.Server/OperatorsService.cs   the handler
src/Parley.Client/BrokerClient.cs       ReadSharedFileAsync, buffered up to a limit
src/Parley.Client/Files/Placements.cs   a conversation's files read whole; interleave by time; Media ordering
src/Parley.Client/Rendering/Reading.cs  placement lines in the shared transcript layout (terminal)
src/Parley.Web/Pages/Conversations/Media.cshtml(.cs)   the Media page and the Image handler
src/Parley.Web/Pages/Conversations/_Conversation.cshtml placements in the transcript
src/Parley.Web/Pages/Conversations/Detail.cshtml(.cs)  reads files; full view dialog; Media link; To chips
src/Parley.Web/Pages/Shared/_Layout.cshtml             styles for thumbnails, grid, dialog, chips
src/Parley.Terminal/Media.cs            the Media list; ShellCommands 'e' in Conversations and Transcript
src/Parley.Terminal/Files.cs            Open falls back to a read copy when the files are on another machine
src/Parley.Terminal/Shell.cs            reads files with the export; passes them to the transcript
SPEC.md                                 §9.5, §10.2, version
Directory.Build.props                   0.57.0
```

**Structure Decision**: existing projects only. The derivations (interleave, ordering, signature) live once in Core or
Client so the two screens agree.

## Complexity Tracking

None.
