# Tasks: 037 Images in the conversation, a Media view, and recipient chips

## Phase 1: Foundation (blocks US1 and US2)

- [x] T001 `ReadSharedFile` and `ReadSharedFileRequest` in `proto/parley.proto` on `Operators`
- [x] T002 Split `FileWork.FetchAsync` into one stream core; `ReadAsync` for the person records nothing, refuses an empty conversation (`src/Parley.Server/FileWork.cs`), handler in `OperatorsService.cs`
- [x] T003 `SharedFileRead` and `ImageSignature` in `src/Parley.Core`; `ReadSharedFileAsync` on `IParleyBrokerClient` and `BrokerClient`, stopping at the limit
- [x] T004 `Placements` in `src/Parley.Client/Files`: read a conversation's files whole, interleave by time, Media order
- [x] T005 [P] Tests: server read streams, refuses, records no fetch; `ImageSignature` unit tests; `Placements` unit tests

## Phase 2: US1 Images in the transcript (P1)

- [x] T006 Detail page reads the conversation's files with the export; `_Conversation.cshtml` draws placements before their turn (image thumbnail link, file tile)
- [x] T007 Image handler on `Conversations/Media` with the headers and refusals of the contract
- [x] T008 Full view dialog and the not-readable fallback in the page script; styles in `_Layout.cshtml`
- [x] T009 Terminal: `Reading.Lines` takes placements; Shell reads files with the export for the open transcript
- [x] T010 Tests: handler headers, 404/413/415, no state change; transcript markup holds a placement in order; terminal draws the line

## Phase 3: US2 Media (P2)

- [x] T011 Media page (grid and list, newest first, Show in conversation); link from the conversation's actions
- [x] T012 Terminal `Media.cs` and `Alt+e` in Conversations and Transcript; Open reads a copy when the files are elsewhere (`Files.cs`)
- [x] T013 Tests: Media page; terminal Media list; command tables still build

## Phase 4: US3 Recipient chips (P3)

- [x] T014 The To line draws chips with a remove button; removing unticks; ticking adds (`Detail.cshtml` script, styles)
- [x] T015 Test: the To line's markup carries what the script reads

## Phase 5: Spec, version, gate

- [x] T016 SPEC.md §9.5 new rule and recorded reduction; §10.2 `read_shared_file`; protocol 0.15; sweep the old wording (Files remarks, composer comment)
- [x] T017 `VersionPrefix` 0.57.0; CLAUDE.md state line
- [x] T018 `dotnet build`; unit tests; smoke run; hygiene grep
