# MapleBench

MapleBench is a Windows WZ editor and dependency-aware client importer for MapleStory. Browse and edit archive data, preview visual assets, work with common game-data sections, and move content between client versions with verified saves.

Made by Kiro.

> [!IMPORTANT]
> Always work on copies of game data and keep an independent backup. MapleBench verifies its output before replacing a destination and normally creates timestamped backups, but no editor can protect against every damaged source file, storage failure, or interrupted write.

## Features

- Browse and edit WZ archive trees.
- Preview canvas data, pixels, linked images, and animations.
- Search and replace values across loaded archives.
- Work with mobs, NPCs, skills, strings, cash-shop data, and game-data search.
- Import selected content from classic WZ or split Data clients after reviewing its names, effects, sounds, set data, and referenced art.
- Save through a temporary candidate file that is flushed, reopened, and verified before it replaces the destination.

## Large imports into Daruma v232

New images are distributed across writable WZ siblings instead of concentrating in one file; existing entries stay in their original archive. Each WZ supports Daruma's unsigned 4 GiB address range, with headroom during import planning.

Whole-archive imports now use reviewed batches with dependency checks. Use **Save changes…** and **Preview next batch** in the import dialog; choose a smaller batch for content with many dependencies. The split archive converter can also produce numbered output files. See [capacity, workflow and validation details](docs/import-capacity.md).

## Requirements

- Windows 10 or Windows 11
- [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0) when building from source
- Microsoft Edge WebView2 Runtime (normally included with current Windows installations)

## Run from source

```powershell
dotnet restore MapleBench.sln
dotnet run --project MapleBench
```

To open the interface in the default browser instead of the desktop window:

```powershell
dotnet run --project MapleBench -- --browser
```

## Build

```powershell
dotnet build MapleBench.sln -c Release
```

Create a self-contained Windows build in `dist/standalone`:

```powershell
dotnet publish MapleBench/MapleBench.csproj `
  -c Release `
  -r win-x64 `
  --self-contained true `
  -p:PublishSingleFile=true `
  -p:IncludeNativeLibrariesForSelfExtract=true `
  -p:IncludeAllContentForSelfExtract=true `
  -p:StaticWebAssetsEnabled=false `
  -p:EnableCompressionInSingleFile=true `
  -p:PublishReadyToRun=true `
  -p:PublishReadyToRunComposite=false `
  -p:SatelliteResourceLanguages=en `
  -o dist/standalone
```

## Saving and data safety

Edits remain in memory until you save. For supported WZ saves, MapleBench writes a sibling candidate file, flushes it to disk, reopens and verifies it, and only then swaps it into place. When a destination is replaced, timestamped backups are retained by default. Split-client reference files are treated as read-only, and `.ms` containers are not overwritten.

These checks reduce risk; they are not a substitute for a separate backup. Keep original archives outside the working directory until you have tested the edited client.

## Repository layout

- `MapleBench` — the desktop host, web interface, application services, and embedded assets
- `dependencies/MapleLib` — the WZ parsing, serialization, cryptography, and packet dependency

NuGet package dependencies are restored automatically by the .NET SDK.

MapleBench is built on [MapleLib](https://github.com/lastbattle/MapleLib). See the repository history for the full contributor record.

## License

This project is licensed under the [GNU General Public License v3.0](LICENSE).

MapleStory and related names and assets are trademarks or copyrighted works of Nexon and their respective owners. MapleBench is an independent project and is not affiliated with or endorsed by Nexon.
