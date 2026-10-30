using System.Diagnostics;
using MapleLib.WzLib;
using MapleLib.WzLib.Util;

namespace MapleBench.Services;

public sealed record ImportOutput(string Path, string? BackupPath, long Bytes, int Images);

public sealed partial class ClientImportService
{
    private sealed record FamilyImage(WzImage Image, WzDirectory Parent, string[] Folders);
    private sealed record Candidate(string Temp, long Bytes, int Images, int Sampled);

    private ImportResult WriteFamily(AssembledArchive assembled, string destination,
        ImportRequest request, bool save64Bit, Stopwatch clock,
        Action<string, long, long> report, CancellationToken cancel)
    {
        string folder = Path.GetDirectoryName(destination)!;
        string stem = Path.GetFileNameWithoutExtension(destination);
        List<FamilyImage> images = new();
        List<string[]> emptyDirectories = new();
        void Walk(WzDirectory dir, string[] path)
        {
            if (path.Length > 0 && dir.WzImages.Count == 0 && dir.WzDirectories.Count == 0)
                emptyDirectories.Add(path);
            foreach (WzImage image in dir.WzImages.OrderBy(i => i.Name, StringComparer.Ordinal))
                images.Add(new(image, dir, path));
            foreach (WzDirectory child in dir.WzDirectories.OrderBy(d => d.Name, StringComparer.Ordinal))
                Walk(child, [.. path, child.Name]);
        }
        Walk(assembled.File.WzDirectory, []);
        List<Candidate> candidates = new();
        List<ImportOutput> installed = new();
        List<string> scratch = new();
        string run = Guid.NewGuid().ToString("N");
        try
        {
            // Initial packing is an estimate. Each write computes actual sizes;
            // oversized candidates are bisected and retried before publication.
            List<FamilyImage> group = new();
            long bytes = 4096;
            foreach (FamilyImage image in images)
            {
                long weight = ArchiveCapacity.ImageBytes(image.Image) + 1024L;
                if (group.Count > 0 && bytes + weight > WzSizeLimits.PlanningFileBytes)
                {
                    Write(group);
                    group = new();
                    bytes = 4096;
                }
                group.Add(image);
                bytes += weight;
            }
            if (group.Count > 0) Write(group);
            if (candidates.Count > 1000)
                throw new InvalidOperationException("This conversion needs more than 1000 classic WZ parts.");

            string Target(int index) => index == 0 ? destination
                : Path.Combine(folder, $"{stem}{index:000}.wz");
            HashSet<string> targets = Enumerable.Range(0, candidates.Count).Select(Target)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            // Old surplus parts would keep old images mounted alongside the new
            // family. Never silently leave them behind or delete user data.
            foreach (string old in Directory.EnumerateFiles(folder, "*.wz"))
            {
                string name = Path.GetFileNameWithoutExtension(old);
                if (name.Length == stem.Length + 3 && name.StartsWith(stem, StringComparison.OrdinalIgnoreCase)
                    && name[stem.Length..].All(char.IsDigit) && !targets.Contains(old))
                    throw new InvalidOperationException($"Existing sibling '{old}' would be left over. " +
                        "Convert into a separate empty folder and review the complete family.");
            }
            foreach (string target in targets)
                if (File.Exists(target) && !request.Overwrite)
                    throw new InvalidOperationException($"'{target}' exists. Enable replacement or choose an empty output folder.");

            cancel.ThrowIfCancellationRequested();
            // All parts have been verified before the first replacement. Keep
            // backups and roll back earlier replacements if a later one fails.
            for (int i = 0; i < candidates.Count; i++)
            {
                string target = Target(i);
                Candidate candidate = candidates[i];
                string? backup = null;
                if (File.Exists(target))
                {
                    // Recheck here: a file may have appeared since preflight.
                    if (!request.Overwrite) throw new IOException($"'{target}' appeared during conversion.");
                    backup = target + $".{run}.bak";
                    File.Replace(candidate.Temp, target, backup, ignoreMetadataErrors: true);
                }
                else File.Move(candidate.Temp, target);
                installed.Add(new(target, backup, candidate.Bytes, candidate.Images));
            }
            List<string> warnings = new(assembled.Warnings)
            {
                "Packaging conversion preserves source content; it does not make newer game features compatible with v232. " +
                "Use the dependency-aware importer to merge selected content into an existing client."
            };
            if (candidates.Count > 1)
                warnings.Add($"Wrote {candidates.Count} numbered files. Install and test the complete family together.");
            return new ImportResult
            {
                Archive = assembled.ArchiveName, WrittenTo = destination,
                BackupPath = installed[0].BackupPath, Outputs = installed,
                Bytes = installed.Sum(p => p.Bytes), SourceBytes = assembled.SourceBytes,
                Images = assembled.Images, Directories = assembled.Directories,
                ImagesReserialised = assembled.ImagesReserialised, SourceFilesRead = assembled.SourceFilesRead,
                Sampled = candidates.Sum(p => p.Sampled), Seconds = clock.Elapsed.TotalSeconds, Warnings = warnings,
            };
        }
        catch (Exception original)
        {
            List<string> recovery = new();
            foreach (ImportOutput output in installed.AsEnumerable().Reverse())
            {
                try
                {
                    if (output.BackupPath != null) File.Copy(output.BackupPath, output.Path, overwrite: true);
                    else File.Delete(output.Path);
                }
                catch (Exception ex) { recovery.Add($"{output.Path} (backup: {output.BackupPath}): {ex.Message}"); }
            }
            if (recovery.Count > 0)
                throw new IOException("Family publication failed and some files require recovery: " +
                    string.Join("; ", recovery), original);
            throw;
        }
        finally
        {
            foreach (string temp in scratch) TryDelete(temp);
        }

        void Write(List<FamilyImage> batch)
        {
            cancel.ThrowIfCancellationRequested();
            using WzFile part = new(ClassicGameVersion, WzMapleVersion.BMS) { Name = stem + ".wz" };
            part.WzDirectory.Name = part.Name;
            WzDirectory DirectoryAt(string[] path)
            {
                WzDirectory dir = part.WzDirectory;
                foreach (string segment in path)
                    dir = dir.GetDirectoryByName(segment) ?? AddChildDirectory(dir, segment, part);
                return dir;
            }
            List<(FamilyImage Entry, long Offset)> moved = new();
            string temp = Path.Combine(folder, $"mbimport-{Guid.NewGuid():N}.wz");
            scratch.Add(temp);
            bool tooLarge = false;
            try
            {
                if (candidates.Count == 0)
                    foreach (string[] path in emptyDirectories) DirectoryAt(path);
                foreach (FamilyImage entry in batch)
                {
                    moved.Add((entry, entry.Image.Offset));
                    entry.Parent.RemoveImage(entry.Image);
                    DirectoryAt(entry.Folders).AddImage(entry.Image);
                }
                Inventory expected = TakeInventory(part.WzDirectory);
                Dictionary<string, string> sampled = Sample(part.WzDirectory, expected,
                    request.SampleSize ?? DefaultSampleSize);
                report($"Writing WZ part {candidates.Count + 1} ({batch.Count:N0} images)",
                    candidates.Sum(p => p.Images), assembled.Images);
                try { part.SaveToDisk(temp, save64Bit, WzMapleVersion.BMS); }
                catch (WzCapacityException) { tooLarge = true; }
                if (!tooLarge)
                {
                    using (FileStream durable = new(temp, FileMode.Open, FileAccess.ReadWrite, FileShare.Read))
                        durable.Flush(flushToDisk: true);
                    long length = new FileInfo(temp).Length;
                    if (length == 0 || length > WzSizeLimits.MaximumFileBytes)
                        throw new IOException("The writer produced an invalid WZ length.");
                    cancel.ThrowIfCancellationRequested();
                    report($"Verifying WZ part {candidates.Count + 1}", candidates.Sum(p => p.Images), assembled.Images);
                    List<string> defects = Verify(temp, expected, sampled, save64Bit);
                    if (defects.Count > 0)
                        throw new IOException("Converted WZ did not verify: " + string.Join("; ", defects));
                    candidates.Add(new(temp, length, batch.Count, sampled.Count));
                }
            }
            finally
            {
                // Never dispose a shared source reader with the temporary tree.
                // Restore source offsets: SaveToDisk assigns output offsets even
                // when it later fails, and retries must still read the source.
                foreach ((FamilyImage entry, long offset) in moved)
                {
                    ((WzDirectory)entry.Image.Parent).RemoveImage(entry.Image);
                    entry.Image.Offset = offset;
                    entry.Parent.AddImage(entry.Image);
                }
            }
            if (tooLarge)
            {
                TryDelete(temp);
                if (batch.Count == 1)
                    throw new IOException($"'{batch[0].Image.Name}' cannot fit in one WZ. An individual image cannot be split.");
                int half = batch.Count / 2;
                Write(batch.GetRange(0, half));
                Write(batch.GetRange(half, batch.Count - half));
            }
        }
    }
}
