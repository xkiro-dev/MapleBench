# Higher-version imports and Daruma's 4 GiB WZ limit

Implemented locally on 2026-10-23. This describes current MapleBench source, not a deployed editor or a native v232 acceptance result.

## Capacity contract

DarumaLauncher's `Teresa232-master/Teresa232-master/Teresa/WzLimit.cpp` patches signed random seeks in the client and NameSpace.dll. WZ offsets remain unsigned 32-bit values. The editor's hard file ceiling is 4,294,967,295 bytes, not 2 GiB and not an unlimited 64-bit file. The 64-bit header option is a header-layout choice, not a larger offset field.

The writer now checks directory/image offset arithmetic and offset encoding. It computes the complete serialized layout before opening the destination for writing. Overflow fails explicitly instead of wrapping into another part of the file. Failed writes restore image source offsets so a retry reads the correct source addresses. Existing directory-size saturation is retained; an individual image still has a signed 32-bit block-size field.

Planning leaves 128 MiB below the hard file ceiling for serialization changes and directory overhead. These are estimates, not a promise of the eventual file size. The writer enforces the exact serialized boundary. Unsaved image clones retain their source size estimate. In-memory images without a WZ block size are measured through a counting serialization stream, without retaining another serialized payload buffer.

## Merging into an existing v232 client

Open the target client and its writable siblings, then open the newer client as a reference. Choose the source archive/folders and **Import every…**.

- Existing IDs are resolved across the whole target family and remain in their owning archive. Duplicate owners or a reference-only owner are refused rather than copied into another sibling.
- New whole images choose the least-filled writable sibling with estimated room, including reservations for earlier images in the same plan. Carried whole artwork uses the same allocator. Logical directory paths are preserved.
- Properties inside a shared `.img` remain in the same image. The importer does not split skill books or other shared images into duplicate physical images.
- A final capacity check includes planned dependencies and satellites. A full family requires more writable siblings or a smaller import. This path does not silently create files or relocate existing entries.
- Archive/folder imports are reviewed in batches of at most 200 entries and about 256 MiB of primary source data. The UI allows 25/50/100/200 entries. Every batch retains dependency scanning; large selections no longer bypass the scan simply because the full archive exceeds 400 entries.
- The existing 1 GiB clone/undo bound and 400-node dependency-closure bound remain per batch. Reduce the batch size for dependency-heavy content. A single entry with a very large dependency closure can still require focused work.
- Use **Save changes…** inside the import dialog, then **Preview next batch**. Saving releases the saved archives' undo state. Each batch is separately undoable until saved. Completed/unchanged batches expose the continuation; failures and unresolved results remain visible.

The API accepts `batchOffset` and `batchSize` on archive/folder plans and applies. Plans return `batchOffset`, `batchEntries`, `totalSourceEntries`, and nullable `nextBatchOffset`. Apply recomputes the plan. A batch is not the entire archive; API callers must follow the continuation explicitly.

## Split archive conversion

The existing `/api/import/archive` converter now produces `Mob.wz`, `Mob001.wz`, etc., instead of rejecting a family larger than one WZ. This is packaging conversion, not a merge into existing content.

It packs complete images with their logical folder paths. A candidate that exceeds the real serialized limit is divided and retried. All candidates are flushed, reopened, inventoried and sampled before any destination is replaced. The result's `outputs` lists every path, backup, byte count and image count; legacy `writtenTo` identifies the base file, and aggregate bytes/images cover the family.

Replacement requires the existing overwrite option, retains backups, and rolls back earlier replacements if a later replacement fails. Rollback failures name recovery paths. Publication is not crash-atomic across multiple files: retain backups and install/test the complete family together. Cancellation before publication installs nothing. Existing surplus numbered siblings cause a refusal, because leaving them mounted could expose stale duplicate content; use a separate output folder. Conversion is still subject to available disk and the existing source-assembly memory constraints.

## Validation and limits

Run from the repository root after installing .NET 10:

```sh
dotnet build MapleBench.sln -c Release
dotnet run --project tests/ImportRegression -c Release
node --input-type=module --check < MapleBench/wwwroot/js/port.js
node --input-type=module --check < MapleBench/wwwroot/js/import.js
```

The release build passed with zero warnings/errors; all 32 regression checks, both JavaScript syntax checks and `git diff --check` passed locally.

The regression runner uses the release assemblies and does not launch the Windows desktop host. It tests unsigned offset boundaries and encryption, an actual sparse-file image read above 2 GiB, pre-truncation overflow refusal, retry offsets, simulated large-family partitioning with verified output, raw nested-WZ import, cancellation, owner/capacity routing, staged collision refusal, later-part publication failure/rollback/backups, in-memory size measurement, batch continuation/apply/undo and configurable batch sizes.

Not claimed: Windows WebView interaction testing, a real multi-gigabyte newer-client/.ms conversion, native client family mounting, or universal higher-version-to-v232 compatibility. Newer canvas formats, client code, packets, skills, quests, maps and server behavior still require matching support. Existing compatibility/dependency checks remain; packaging success alone does not establish that all newer content works in v232. No game data, launcher binaries, deployment or database was changed by this work.

## Pending-release review — 2026-10-30

Independent review retained the WZ writer, staged family rollback and allocation behavior. Fixed batch navigation so a zero-write preview with unresolved conflicts or blocked dependencies cannot advance, and a failed preview clears any prior continuation. The batch-size control is disabled while preview/apply is in flight to keep the reviewed range stable.

Validation: .NET Release build passed with zero warnings/errors; all 32 import regressions passed. JavaScript syntax checks and four execution checks of the actual preview function covered unchanged, conflict, blocked-dependency and failed-preview outcomes. Native Windows/WebView and real multi-gigabyte client-family acceptance remain unverified.
