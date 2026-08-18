using System.Diagnostics;
using System.Text.RegularExpressions;
using MapleLib.WzLib;
using MapleLib.WzLib.Util;
using MapleLib.WzLib.WzProperties;

namespace MapleBench.Services;

/* ============================================================================
   CANVAS FORMAT REPAIR — putting back together a value this app took apart

   WHAT HAPPENED

   A canvas record on disk carries five things: width, height, a format, a
   magnification, and a compressed blob of pixels. The client reads the first
   four as four independent numbers and lays out a row as
   `ceil(width >> mag) << (format & 0xFF)`.

   Until 3cf73c7 this library wrote a canvas's format as TWO numbers: the low
   byte into the format field and the high bits into the field after it. It read
   it back the same way, so MapleLib agreed with itself perfectly and every
   round-trip test passed. The client does not read it back that way. DXT5,
   format 2050, went out as (2, 8) — format 2 at magnification 8 — and the
   client shifted a 564-pixel sprite down to two pixels, or to none.

   The writer was fixed. The archives were not. `C:\MapleStory\232\Skill.wz`
   still holds 147 canvases in that shape, all under `000.img/skill/`. This
   repairs them: it re-joins `format + (mag << 8)` back into the format field,
   zeroes the magnification, and then decodes the pixels to prove it worked.

   WHAT MAKES THIS HARD, AND THE RULE THAT SOLVES IT

   `mag` is a real field with real uses. A pair like (2, 4) is EXACTLY what a
   genuine format-2 canvas held at quarter scale looks like, and it is also
   exactly what a split DXT3 (1026) looks like. The pair alone cannot tell them
   apart, and the three pairs most at risk — (1,1)=257, (1,2)=513, (2,4)=1026 —
   are precisely the magnifications 1, 2 and 4 that occur in ordinary data.

   So the pair is only a CANDIDATE FILTER. The decision is made by the payload:

       genuine     : the pixels are stored at the reduced size, so the blob
                     inflates to GetDecodedSize(format, w >> mag, h >> mag)
       split value : the pixels are stored at full size in the JOINED format,
                     so the blob inflates to GetDecodedSize(joined, w, h)

   Those two never coincide. For every candidate pair the ratio between them is
   fixed and greater than one — 4x for (1,1), 16x for (1,2), 64x for (2,4),
   256x for (2,8), 1024x for (2,16) — so one measurement separates them with no
   judgement call in the middle. A canvas is repaired only when the blob
   inflates to the joined size and to neither reading of the stored format.

   That is why <see cref="WzPngProperty.InflatedLength"/> exists: everything
   else in the library sizes a buffer FROM the format, which is the field under
   suspicion. This measures the payload independently.

   WHAT THE REPAIR CANNOT GIVE BACK

   The split writer put `format >> 8` INTO the magnification field, so if a
   canvas had a real magnification before it was rewritten, that number was
   overwritten and is gone. Re-joining recovers the format and has to assume the
   magnification was zero.

   That assumption is safe on this client and it is measured, not hoped: of
   3,329,293 canvases across all 22 mounted archives, exactly 285 carry a
   non-zero magnification — the 283 broken ones, and two genuinely scaled
   backgrounds in Map001.wz that this never touches. An archive whose art is
   routinely stored scaled would need the original consulted instead, and the
   scan reports the pair census so that is visible before anything is written.

   THREE THINGS THIS DELIBERATELY WILL NOT DO

   1. It does not trust the pair. A canvas whose blob cannot be inflated is
      reported Undecidable and left alone. "I could not look" is not "it is
      broken", and a repair applied to a genuinely scaled canvas would destroy
      art that works today.
   2. It does not write to the archive it read. Apply produces a NEW file beside
      the source and hands back the exact command to install it, backup first.
      The archive the user plays from is never touched by this code.
   3. It does not report a repair it has not decoded. After the save the output
      is reopened from disk and every repaired canvas is decoded to a bitmap of
      the declared size. A repair that will not decode is counted as a failure,
      not as a repair.
   ============================================================================ */

/// <summary>What the evidence says about one canvas carrying a non-zero magnification.</summary>
public enum CanvasMagVerdict
{
    /// <summary>The blob is full-size in the joined format. A split whole value, and repairable.</summary>
    Split = 0,

    /// <summary>The blob matches a reading of the stored format. A real magnification; leave it alone.</summary>
    Genuine = 1,

    /// <summary>The blob matches nothing, or could not be inflated at all. Reported, never repaired.</summary>
    Undecidable = 2,
}

/// <summary>
/// One canvas with a non-zero magnification, and every number the verdict was
/// reached from. Deliberately verbose: the whole point is that the caller can
/// check the arithmetic instead of taking the verdict on faith.
/// </summary>
public sealed record CanvasMagCase(
    string Archive,
    string Path,
    string Image,
    string Inside,
    int Width,
    int Height,
    int StoredFormat,
    int StoredMag,
    int JoinedFormat,
    int RowWidth,
    int CompressedBytes,
    long InflatedBytes,
    long JoinedExpects,
    long StoredExpectsFullSize,
    long StoredExpectsShrunk,
    CanvasMagVerdict Verdict,
    string Why);

/// <summary>How many canvases carry each (format, mag) pair, with one example path.</summary>
public sealed record CanvasPairCount(int Format, int Mag, long Count, string Example);

/// <summary>
/// A property NAMED like a magnification — `mag`, `tSMag` — with its values.
///
/// Here because it is the thing the canvas field is confused with. These are
/// ordinary int properties sitting beside a canvas (a minimap's `mag`, a tile
/// set's `tSMag`); they are not the canvas record's magnification field and
/// this repair never looks at them. Counted so that claim is measured rather
/// than asserted.
/// </summary>
public sealed record NamedMagCount(string Name, long Count, string Values, string Example);

/// <summary>What a read-only scan found. Nothing has been written.</summary>
public sealed record CanvasRepairScan(
    string Path,
    DateTimeOffset StartedUtc,
    double Seconds,
    IReadOnlyList<string> Archives,
    long Images,
    long Canvases,
    long CanvasesWithMag,
    int Split,
    int Genuine,
    int Undecidable,
    IReadOnlyList<CanvasPairCount> Pairs,
    IReadOnlyList<CanvasMagCase> Cases,
    IReadOnlyList<NamedMagCount> NamedMagProperties,
    IReadOnlyList<string> Notes);

/// <summary>What an apply did, measured against the file it wrote and reopened.</summary>
public sealed record CanvasRepairResult(
    string Source,
    string Output,
    double Seconds,
    int Considered,
    int Repaired,
    int LeftAlone,
    int Decoded,
    int FailedToDecode,
    int BystandersDecoded,
    int BystandersFailed,
    IReadOnlyList<CanvasMagCase> Repairs,
    IReadOnlyList<string> Failures,
    IReadOnlyList<string> Notes,
    string InstallCommand,
    RepairInputCheck? Input = null,
    RepairLedgerFile? Ledger = null);

public sealed class CanvasRepairOptions
{
    /// <summary>A single .wz archive, or a folder to scan every mounted archive in.</summary>
    public string Path { get; set; } = "";

    /// <summary>Encryption, when known — "BMS", "GMS", "EMS". Empty means detect.</summary>
    public string? MapleVersion { get; set; }

    /// <summary>Patch version, when known. 0 means detect it with the encryption.</summary>
    public short GameVersion { get; set; }

    /// <summary>How many cases to return per verdict. The counts are always complete.</summary>
    public int MaxCases { get; set; } = 500;

    /// <summary>
    /// Where an apply writes. Empty means "&lt;source&gt;.repaired.wz" beside the
    /// source. It is never the source: see <see cref="Apply"/>.
    /// </summary>
    public string? Output { get; set; }

    /// <summary>
    /// An apply refuses without this. The scan is free and reversible; writing
    /// two gigabytes is neither, so it is not something a stray poll can do.
    /// </summary>
    public bool Confirm { get; set; }

    /// <summary>
    /// Write even though another kind of repair has already built a whole
    /// archive from this exact input.
    ///
    /// Separate from <see cref="Confirm"/> because it answers a different
    /// question. Confirm asks whether a caller meant to write at all; this asks
    /// whether the caller has seen that the result cannot be installed ALONGSIDE
    /// that other archive — both are complete copies of the source, so
    /// installing the second silently reverts the first — and wants a separate
    /// variant anyway. The composing answer is to run this pass against the
    /// other pass's output, and <see cref="RepairLedger"/> names it.
    /// </summary>
    public bool AcceptSeparateRepairs { get; set; }
}

public sealed class CanvasRepairProgress
{
    public string State { get; set; } = "idle";     // idle | scanning | repairing | saving | verifying | done | failed | cancelled
    public string Phase { get; set; } = "";
    public string Archive { get; set; } = "";
    public long ImagesDone { get; set; }
    public long CanvasesDone { get; set; }
    public int Found { get; set; }
    public double Seconds { get; set; }
    public string? Error { get; set; }
}

public sealed class CanvasFormatRepairService
{
    private readonly WarmupService _warmup;
    private readonly object _gate = new();
    private readonly CanvasRepairProgress _progress = new();
    private CancellationTokenSource? _cancel;
    private CanvasRepairScan? _scan;
    private CanvasRepairResult? _result;

    /// <summary>
    /// The formats a canvas can legitimately be in — the ones this library can
    /// size and decode. A joined value outside this set is not a format that
    /// was split; it is a coincidence, and nothing is repaired on a coincidence.
    /// </summary>
    private static readonly int[] DecodableFormats = { 1, 2, 3, 257, 513, 517, 1026, 2050, 4098 };

    /// <summary>
    /// What the shipping client's own decoder accepts, from the whitelist in
    /// IWzCanvas::Init. Anything else returns E_INVALIDARG and aborts the whole
    /// .img load rather than skipping one frame — so a repair that lands one of
    /// these is worth saying out loud, even though the format on disk is then
    /// honest.
    /// </summary>
    private static readonly int[] ClientAcceptedFormats = { 1, 2, 257, 513, 1026, 2050, 2304 };

    private static readonly Regex MountedName = new(@"^([A-Za-z]+?)(\d*)$", RegexOptions.Compiled);

    /// <summary>Archives smaller than this are stubs. Handing one to the directory parser overflows the stack.</summary>
    private const long StubCeiling = 64 * 1024;

    public CanvasFormatRepairService(WarmupService warmup) => _warmup = warmup;

    public CanvasRepairProgress Snapshot()
    {
        lock (_gate)
            return new CanvasRepairProgress
            {
                State = _progress.State,
                Phase = _progress.Phase,
                Archive = _progress.Archive,
                ImagesDone = _progress.ImagesDone,
                CanvasesDone = _progress.CanvasesDone,
                Found = _progress.Found,
                Seconds = _progress.Seconds,
                Error = _progress.Error,
            };
    }

    /// <summary>Whether a run is in flight. Advisory: <see cref="StartScan"/> is the race-free ask.</summary>
    public bool Busy
    {
        get { lock (_gate) return _progress.State is "scanning" or "repairing" or "saving" or "verifying"; }
    }

    public CanvasRepairScan? LastScan() { lock (_gate) return _scan; }
    public CanvasRepairResult? LastResult() { lock (_gate) return _result; }

    public void Cancel() { lock (_gate) _cancel?.Cancel(); }

    /// <summary>
    /// Reserves the service SYNCHRONOUSLY and then scans on a background thread.
    ///
    /// The reservation has to happen on the caller's thread. The obvious shape —
    /// <c>Task.Run(() =&gt; Scan(options))</c> and hand back a snapshot — is what
    /// these endpoints did, and it is wrong in exactly one case that matters:
    /// with a run already in flight, <see cref="Begin"/>'s refusal is thrown
    /// INSIDE the task where nothing sees it, and the response carries the OTHER
    /// run's progress — a 200 that is indistinguishable from acceptance while
    /// the request has been dropped on the floor. Found in the donor restore by
    /// driving its endpoints; it was still here, and this is the same fix.
    /// </summary>
    public CanvasRepairProgress StartScan(CanvasRepairOptions options)
    {
        CancellationTokenSource cancel = Begin("scanning");
        IDisposable activity = _warmup.HoldForeground();
        _ = Task.Run(() =>
        {
            using (activity)
            {
                try { RunScan(cancel, options); }
                catch { /* the snapshot carries it */ }
            }
        });
        return Snapshot();
    }

    /// <summary>Reserves synchronously, then applies on a background thread. See <see cref="StartScan"/>.</summary>
    public CanvasRepairProgress StartApply(CanvasRepairOptions options)
    {
        Confirmed(options);
        CancellationTokenSource cancel = Begin("repairing");
        IDisposable activity = _warmup.HoldForeground();
        _ = Task.Run(() =>
        {
            using (activity)
            {
                try { RunApply(cancel, options); }
                catch { /* the snapshot carries it */ }
            }
        });
        return Snapshot();
    }

    private static void Confirmed(CanvasRepairOptions options)
    {
        if (!options.Confirm)
            throw new InvalidOperationException(
                "A repair writes a new archive of the same size as the source. Pass confirm=true.");
    }

    /* ====================================================================
       DETECT
       ==================================================================== */

    /// <summary>
    /// Reads. Writes nothing, opens nothing for writing, and leaves every
    /// archive it touched exactly as it found it.
    /// </summary>
    public CanvasRepairScan Scan(CanvasRepairOptions options) => RunScan(Begin("scanning"), options);

    private CanvasRepairScan RunScan(CancellationTokenSource cancel, CanvasRepairOptions options)
    {
        Stopwatch clock = Stopwatch.StartNew();
        DateTimeOffset started = DateTimeOffset.UtcNow;

        try
        {
            List<string> archives = Discover(options.Path);
            if (archives.Count == 0)
                throw new InvalidOperationException($"No mountable .wz archive at {options.Path}.");

            (WzMapleVersion version, short gameVersion, string how) = Encryption(archives, options);

            List<string> notes = new() { how };
            Tally tally = new();

            foreach (string path in archives)
            {
                cancel.Token.ThrowIfCancellationRequested();
                string stem = System.IO.Path.GetFileNameWithoutExtension(path);
                lock (_gate) { _progress.Archive = stem; _progress.Phase = "reading"; }

                WzFile file = new(path, gameVersion, version);
                try
                {
                    WzFileParseStatus status = file.ParseWzFile();
                    if (status != WzFileParseStatus.Success)
                    {
                        notes.Add($"{stem}.wz could not be opened ({status.GetErrorDescription()}); " +
                                  "nothing in it was examined.");
                        continue;
                    }
                    Walk(file.WzDirectory, stem, "", tally, cancel.Token, repair: null);
                }
                finally
                {
                    try { file.Dispose(); } catch { /* closing a read-only handle */ }
                }
            }

            notes.AddRange(Conclusions(tally));

            CanvasRepairScan scan = new(
                options.Path, started, clock.Elapsed.TotalSeconds,
                archives.Select(System.IO.Path.GetFileName).Select(n => n ?? "").ToList(),
                tally.Images, tally.Canvases, tally.WithMag,
                tally.Cases.Count(c => c.Verdict == CanvasMagVerdict.Split),
                tally.Cases.Count(c => c.Verdict == CanvasMagVerdict.Genuine),
                tally.Cases.Count(c => c.Verdict == CanvasMagVerdict.Undecidable),
                tally.PairCounts(),
                tally.Cases
                     .GroupBy(c => c.Verdict)
                     .SelectMany(g => g.Take(Math.Max(1, options.MaxCases)))
                     .OrderBy(c => (int)c.Verdict).ThenBy(c => c.Path, StringComparer.Ordinal)
                     .ToList(),
                tally.NamedMags(),
                notes);

            lock (_gate)
            {
                _scan = scan;
                _progress.State = "done";
                _progress.Phase = "";
                _progress.Seconds = clock.Elapsed.TotalSeconds;
                _progress.Found = scan.Split;
            }
            return scan;
        }
        catch (OperationCanceledException)
        {
            lock (_gate) { _progress.State = "cancelled"; _progress.Seconds = clock.Elapsed.TotalSeconds; }
            throw;
        }
        catch (Exception ex)
        {
            lock (_gate) { _progress.State = "failed"; _progress.Error = ex.Message; _progress.Seconds = clock.Elapsed.TotalSeconds; }
            throw;
        }
        finally
        {
            lock (_gate) { _cancel?.Dispose(); _cancel = null; }
        }
    }

    /* ====================================================================
       REPAIR
       ==================================================================== */

    /// <summary>
    /// Re-joins the split value in every canvas the evidence convicts, writes a
    /// NEW archive, reopens it from disk and decodes every repaired canvas.
    ///
    /// The source archive is opened read-only and is not written to under any
    /// argument. There is no in-place branch to reach: the output path is
    /// checked against the source and the call is refused if they are the same
    /// file. That is deliberate — the archive on the other end of this is the
    /// one the user plays from, and a repair that damages it further has cost
    /// more than the damage it fixed.
    /// </summary>
    public CanvasRepairResult Apply(CanvasRepairOptions options)
    {
        Confirmed(options);
        return RunApply(Begin("repairing"), options);
    }

    private CanvasRepairResult RunApply(CancellationTokenSource cancel, CanvasRepairOptions options)
    {
        Stopwatch clock = Stopwatch.StartNew();

        try
        {
            /* Inside the try, not before it. The reservation is taken on the
               caller's thread now, so anything that throws on the way to the
               write has to release it — a refusal that left the service reading
               "repairing" for ever would answer every later request with a 409
               and look exactly like a hung run. */
            string source = System.IO.Path.GetFullPath(options.Path);
            if (!File.Exists(source))
                throw new InvalidOperationException(
                    $"{source} is not a file. A repair runs against one archive, not a folder — " +
                    "scan the folder first to see which archives need one.");

            string output = string.IsNullOrWhiteSpace(options.Output)
                ? System.IO.Path.Combine(
                    System.IO.Path.GetDirectoryName(source) ?? ".",
                    System.IO.Path.GetFileNameWithoutExtension(source) + ".repaired.wz")
                : System.IO.Path.GetFullPath(options.Output!);

            if (string.Equals(source, output, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException(
                    "The repair writes a new archive; it never edits the one it read. " +
                    "Give an output path that is not the source.");

            /* What has already been done to this file, before anything is read
               out of it. A ledger that does not describe these bytes, or another
               pass that has already built a whole archive from this same input,
               stops the run here — see RepairLedger for the accident that is. */
            RepairInputCheck input = RepairLedger.Inspect(source, RepairLedger.CanvasFormatPass);
            if (input.Verdict == RepairInputVerdict.Stale)
                throw new InvalidOperationException(input.Why);
            if (input.ConflictsWith.Count > 0 && !options.AcceptSeparateRepairs)
                throw new InvalidOperationException(input.Why);

            (WzMapleVersion version, short gameVersion, string how) =
                Encryption(new List<string> { source }, options);

            List<string> notes = new() { how, input.Why };
            List<string> failures = new();
            Tally tally = new();
            List<(WzImage Image, WzPngProperty Png, CanvasMagCase Case)> repairs = new();

            string stem = System.IO.Path.GetFileNameWithoutExtension(source);
            WzFile file = new(source, gameVersion, version);
            try
            {
                WzFileParseStatus status = file.ParseWzFile();
                if (status != WzFileParseStatus.Success)
                    throw new InvalidOperationException(
                        $"{stem}.wz could not be opened: {status.GetErrorDescription()}");

                lock (_gate) { _progress.Archive = stem; _progress.Phase = "finding"; }
                Walk(file.WzDirectory, stem, "", tally, cancel.Token, repairs);

                int considered = tally.Cases.Count;
                int repaired = repairs.Count;

                if (repaired == 0)
                {
                    notes.Add("Nothing was written: no canvas in this archive carries a split whole " +
                              "value. That is a result, not a no-op — the scan examined " +
                              $"{tally.Canvases:N0} canvases and {tally.WithMag:N0} of them carry a " +
                              "non-zero magnification.");
                    CanvasRepairResult empty = new(source, "", clock.Elapsed.TotalSeconds,
                        considered, 0, considered, 0, 0, 0, 0,
                        Array.Empty<CanvasMagCase>(), failures, notes, "", input, input.Ledger);
                    lock (_gate) { _result = empty; _progress.State = "done"; }
                    return empty;
                }

                /* The join itself. Two assignments, and they are the whole
                   repair: the value goes back into the field that holds it, and
                   the field that was carrying the other half goes back to zero.
                   The blob is not touched — it was always correct, and it is
                   what proved which format it was in. */
                foreach ((WzImage _, WzPngProperty png, CanvasMagCase one) in repairs)
                {
                    png.Format = (WzPngFormat)one.JoinedFormat;
                    png.Mag = 0;
                }

                foreach (WzImage changed in repairs.Select(r => r.Image).Distinct())
                    changed.Changed = true;

                lock (_gate) { _progress.Phase = "saving"; _progress.State = "saving"; }

                string temp = output + ".partial";
                if (File.Exists(temp)) File.Delete(temp);

                /* UNKNOWN, and no override IV, so the key comes from the
                   archive's own version and is the one it was read with.
                   Anything else — a named version, a pinned IV — makes MapleLib
                   mark every image changed and re-serialise the whole archive,
                   which puts lossy round-trip risk on art nobody touched. As it
                   is, only the images holding a repair are rewritten and the
                   rest are copied across as the bytes they already were.

                   Measured on the user's Skill.wz: 2,006,108,588 bytes in,
                   2,006,109,720 out. The difference is 1,132 = 4 x 283, which
                   is exactly what 283 canvases cost when a one-byte format and
                   a one-byte mag become a five-byte format and a one-byte zero.
                   Nothing else in two gigabytes moved. */
                file.SaveToDisk(temp, null, WzMapleVersion.UNKNOWN, null);
                file.Dispose();

                if (File.Exists(output)) File.Delete(output);
                File.Move(temp, output);

                notes.Add($"{repaired} canvas{(repaired == 1 ? "" : "es")} rejoined in " +
                          $"{repairs.Select(r => r.Case.Image).Distinct().Count()} image(s); " +
                          "every other image was copied across byte for byte.");

                /* Verify on the SAVED AND REOPENED archive. Checking the tree
                   still in memory would only prove the two assignments above
                   ran, which was never in doubt. */
                lock (_gate) { _progress.Phase = "verifying"; _progress.State = "verifying"; }
                (int decoded, int bystanders, int bystandersBroke, List<string> broke) =
                    Verify(output, version, gameVersion, repairs.Select(r => r.Case).ToList(), cancel.Token);
                failures.AddRange(broke);

                notes.Add($"{bystanders:N0} canvases that were NOT repaired but share a rewritten image " +
                          $"were decoded as well, and {bystandersBroke:N0} of them failed. Marking an " +
                          "image changed rewrites all of it, so those are what this repair actually put " +
                          "at risk.");

                foreach (CanvasMagCase one in repairs.Select(r => r.Case)
                             .Where(c => !ClientAcceptedFormats.Contains(c.JoinedFormat))
                             .DistinctBy(c => c.JoinedFormat))
                    notes.Add($"Format {one.JoinedFormat} is the honest value for these pixels, but it is " +
                              "not in the client's own decoder whitelist " +
                              $"({string.Join(", ", ClientAcceptedFormats)}). The archive now says what the " +
                              "data is; whether this client can draw it is a separate question.");

                /* Record what this archive now carries, keyed on the content of
                   what was read and what was written. The next pass reads this
                   and composes; a pass that reads the pristine source instead is
                   told that this output already exists and that installing both
                   would revert one of them. */
                lock (_gate) { _progress.Phase = "recording"; }
                RepairLedgerFile ledger = RepairLedger.Record(
                    input, RepairLedger.CanvasFormatPass, output, repaired,
                    repairs.Select(r => r.Case.Path).ToList(), notes);
                notes.Add($"{System.IO.Path.GetFileName(output)} now carries a ledger of " +
                          $"{ledger.Passes.Count} repair pass(es) — " +
                          $"{string.Join(", ", ledger.Passes.Select(p => $"{p.Pass} ({p.Changed:N0})"))} — " +
                          $"all of them from {System.IO.Path.GetFileName(ledger.Origin)}. Install THIS file " +
                          "rather than any earlier one: each is a whole archive, so the last one copied wins.");

                string install = InstallCommand(ledger.Origin, output);

                CanvasRepairResult result = new(
                    source, output, clock.Elapsed.TotalSeconds,
                    considered, repaired, considered - repaired,
                    decoded, repaired - decoded, bystanders, bystandersBroke,
                    repairs.Select(r => r.Case).ToList(), failures, notes, install, input, ledger);

                lock (_gate)
                {
                    _result = result;
                    _progress.State = "done";
                    _progress.Phase = "";
                    _progress.Found = repaired;
                    _progress.Seconds = clock.Elapsed.TotalSeconds;
                }
                return result;
            }
            finally
            {
                try { file.Dispose(); } catch { /* already closed by the save */ }
            }
        }
        catch (OperationCanceledException)
        {
            lock (_gate) { _progress.State = "cancelled"; _progress.Seconds = clock.Elapsed.TotalSeconds; }
            throw;
        }
        catch (Exception ex)
        {
            lock (_gate) { _progress.State = "failed"; _progress.Error = ex.Message; _progress.Seconds = clock.Elapsed.TotalSeconds; }
            throw;
        }
        finally
        {
            lock (_gate) { _cancel?.Dispose(); _cancel = null; }
        }
    }

    /// <summary>
    /// Reopens what was written and decodes the pixels — of the canvases that
    /// were repaired, and of every other canvas in the same images.
    ///
    /// The second half is the part that is easy to leave out and expensive to
    /// have left out. Repairing a canvas marks its whole image changed, and a
    /// changed image is re-serialised from the parsed tree rather than copied
    /// across as bytes. So the blast radius of this repair is not 283 canvases,
    /// it is every canvas in the images those 283 live in — on the user's
    /// Skill.wz, 283 repairs inside an image holding 4,203 canvases. Verifying
    /// only the repairs would report a clean run while 3,920 bystanders that had
    /// nothing to do with the bug went through a round trip unchecked.
    ///
    /// A bystander that will not decode is not counted as a repair failure,
    /// because it may not have decoded before either. It is reported as its own
    /// number so the difference is visible instead of averaged away.
    /// </summary>
    private static (int Decoded, int BystandersDecoded, int BystandersFailed, List<string> Failures) Verify(
        string output, WzMapleVersion version, short gameVersion,
        IReadOnlyList<CanvasMagCase> repairs, CancellationToken token)
    {
        int decoded = 0;
        int bystandersDecoded = 0;
        int bystandersFailed = 0;
        List<string> failures = new();

        WzFile back = new(output, gameVersion, version);
        try
        {
            WzFileParseStatus status = back.ParseWzFile();
            if (status != WzFileParseStatus.Success)
            {
                failures.Add($"The repaired archive will not reopen: {status.GetErrorDescription()}. " +
                             "Nothing in it has been verified; do not install it.");
                return (0, 0, 0, failures);
            }

            foreach (IGrouping<string, CanvasMagCase> byImage in repairs.GroupBy(c => c.Image))
            {
                token.ThrowIfCancellationRequested();
                WzImage? image = FindImage(back.WzDirectory, byImage.Key);
                if (image == null)
                {
                    failures.Add($"{byImage.Key} is not in the repaired archive at all.");
                    continue;
                }
                if (!image.Parsed && !image.ParseImage())
                {
                    failures.Add($"{byImage.Key} will not parse in the repaired archive.");
                    continue;
                }

                foreach (CanvasMagCase one in byImage)
                {
                    if (image.GetFromPath(one.Inside) is not WzCanvasProperty canvas
                        || canvas.PngProperty is not WzPngProperty png)
                    {
                        failures.Add($"{one.Path} is not a canvas in the repaired archive.");
                        continue;
                    }

                    if ((int)png.Format != one.JoinedFormat || png.Mag != 0)
                    {
                        failures.Add($"{one.Path} came back as format {(int)png.Format} mag {png.Mag}, " +
                                     $"not {one.JoinedFormat} mag 0.");
                        continue;
                    }

                    // The pixels, not the fields. This is the only step that
                    // could not have been faked by the two assignments above.
                    System.Drawing.Bitmap? bitmap = null;
                    try
                    {
                        bitmap = png.GetImage(false);
                        if (bitmap == null)
                            failures.Add($"{one.Path} declares format {one.JoinedFormat} and decodes to nothing.");
                        else if (bitmap.Width != one.Width || bitmap.Height != one.Height)
                            failures.Add($"{one.Path} decoded to {bitmap.Width}x{bitmap.Height}, " +
                                         $"not the {one.Width}x{one.Height} it declares.");
                        else if (png.InflatedLength() != one.JoinedExpects)
                            failures.Add($"{one.Path} inflates to {png.InflatedLength()} bytes, " +
                                         $"not the {one.JoinedExpects} format {one.JoinedFormat} needs.");
                        else
                            decoded++;
                    }
                    catch (Exception ex)
                    {
                        failures.Add($"{one.Path} threw while decoding: {ex.Message}");
                    }
                    finally
                    {
                        bitmap?.Dispose();
                    }
                }

                // Everything else in an image this repair caused to be
                // rewritten. Linked canvases are skipped, not failed: a canvas
                // whose pixels come from an _inlink or an _outlink is SUPPOSED
                // to have no blob of its own, and counting those as failures
                // would bury a real one in thousands of false ones.
                HashSet<string> repaired = byImage.Select(c => c.Inside).ToHashSet(StringComparer.Ordinal);
                foreach ((string where, WzCanvasProperty canvas) in Canvases(image, ""))
                {
                    token.ThrowIfCancellationRequested();
                    if (repaired.Contains(where)) continue;
                    if (canvas.PngProperty is not WzPngProperty other) continue;
                    if (canvas[WzCanvasProperty.InlinkPropertyName] != null
                        || canvas[WzCanvasProperty.OutlinkPropertyName] != null) continue;
                    if (other.CompressedLength <= 0) continue;

                    System.Drawing.Bitmap? drawn = null;
                    try
                    {
                        drawn = other.GetImage(false);
                        if (drawn != null && drawn.Width == other.Width && drawn.Height == other.Height)
                            bystandersDecoded++;
                        else
                        {
                            bystandersFailed++;
                            if (bystandersFailed <= 20)
                                failures.Add($"{byImage.Key}/{where} was not repaired but shares a " +
                                             "rewritten image, and it does not decode to the size it declares.");
                        }
                    }
                    catch (Exception ex)
                    {
                        bystandersFailed++;
                        if (bystandersFailed <= 20)
                            failures.Add($"{byImage.Key}/{where} was not repaired but shares a rewritten " +
                                         $"image, and it threw while decoding: {ex.Message}");
                    }
                    finally
                    {
                        drawn?.Dispose();
                    }
                }

                try { image.UnparseImage(); } catch { /* nothing left to free */ }
            }
        }
        finally
        {
            try { back.Dispose(); } catch { /* read-only handle */ }
        }

        return (decoded, bystandersDecoded, bystandersFailed, failures);
    }

    /// <summary>Every canvas in an image, with its path relative to the image.</summary>
    private static IEnumerable<(string Path, WzCanvasProperty Canvas)> Canvases(
        WzObject node, string prefix)
    {
        WzPropertyCollection? children = node switch
        {
            WzImage image => image.WzProperties,
            // Never through a UOL: its children are the resolved node's, which
            // live somewhere else and are not this image's to verify.
            WzUOLProperty => null,
            WzImageProperty property => property.WzProperties,
            _ => null,
        };
        if (children == null) yield break;

        foreach (WzImageProperty child in children)
        {
            string here = prefix.Length == 0 ? child.Name : prefix + "/" + child.Name;
            if (child is WzCanvasProperty canvas)
                yield return (here, canvas);
            foreach ((string deeper, WzCanvasProperty found) in Canvases(child, here))
                yield return (deeper, found);
        }
    }

    /// <summary>
    /// The image at a slash-separated path under a directory.
    ///
    /// Not <c>WzFile.GetObjectFromPath</c>: that routes through the global
    /// <c>WzFileManager</c>, which a service opening its own file has never
    /// populated, and it returns null rather than saying that is why.
    /// </summary>
    private static WzImage? FindImage(WzDirectory? dir, string rel)
    {
        if (dir == null) return null;
        string[] parts = rel.Split('/');
        for (int i = 0; i < parts.Length - 1; i++)
        {
            dir = dir.WzDirectories.FirstOrDefault(
                d => string.Equals(d.Name, parts[i], StringComparison.OrdinalIgnoreCase));
            if (dir == null) return null;
        }
        return dir.WzImages.FirstOrDefault(
            m => string.Equals(m.Name, parts[^1], StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// The command that installs an output, backup first.
    ///
    /// <paramref name="installAs"/> is the archive in the live client this
    /// replaces — which is NOT the source once repairs compose. A pass reading
    /// `Skill.restored.wz` still produces a `Skill.wz`, and naming the source
    /// here printed a command that backed up and overwrote a file the client
    /// does not have. The ledger's origin is the file at the head of the chain,
    /// so it is what the whole chain is going to be installed as.
    /// </summary>
    private static string InstallCommand(string installAs, string output)
    {
        string name = System.IO.Path.GetFileName(installAs);
        string stamp = DateTimeOffset.Now.ToString("yyyyMMdd_HHmmss");
        string live = @"C:\MapleStory\232\" + name;
        return
            $"Copy-Item -LiteralPath '{live}' -Destination 'C:\\MapleStory\\232\\{System.IO.Path.GetFileNameWithoutExtension(name)}_beforeCanvasRepair_{stamp}.wz'; " +
            $"if ($?) {{ Copy-Item -LiteralPath '{output}' -Destination '{live}' -Force }}";
    }

    /* ====================================================================
       THE WALK
       ==================================================================== */

    private void Walk(WzDirectory dir, string stem, string prefix, Tally tally,
                      CancellationToken token,
                      List<(WzImage Image, WzPngProperty Png, CanvasMagCase Case)>? repair)
    {
        foreach (WzImage image in dir.WzImages)
        {
            token.ThrowIfCancellationRequested();
            string rel = prefix.Length == 0 ? image.Name : prefix + "/" + image.Name;
            tally.Images++;
            if ((tally.Images & 0x1FF) == 0)
                lock (_gate) { _progress.ImagesDone = tally.Images; _progress.CanvasesDone = tally.Canvases; }

            bool wasParsed = image.Parsed;
            bool keep = false;
            try
            {
                if (!image.Parsed && !image.Changed && !image.ParseImage())
                    continue;

                int before = repair?.Count ?? 0;
                foreach (WzImageProperty property in image.WzProperties)
                    Property(property, image, stem, rel, $"{stem}.wz/{rel}", tally, repair);
                keep = repair != null && repair.Count > before;
            }
            catch (Exception)
            {
                // An image that throws while being read is the auditor's
                // finding, not this one's. Nothing here can repair it.
            }
            finally
            {
                // The memory bound. Without it a 2 GB archive is a 2 GB heap.
                // An image holding a repair stays: unparsing it would clear the
                // properties the two assignments are about to be made on.
                if (!keep && !wasParsed && !image.Changed && image.BlockSize > 0 && image.Offset > 0)
                {
                    try { image.UnparseImage(); } catch { /* nothing left to free */ }
                }
            }
        }

        foreach (WzDirectory sub in dir.WzDirectories)
            Walk(sub, stem, prefix.Length == 0 ? sub.Name : prefix + "/" + sub.Name, tally, token, repair);
    }

    private void Property(WzImageProperty property, WzImage image, string stem, string rel, string path,
                          Tally tally,
                          List<(WzImage Image, WzPngProperty Png, CanvasMagCase Case)>? repair)
    {
        string here = path + "/" + property.Name;

        switch (property)
        {
            case WzCanvasProperty canvas:
                Canvas(canvas, image, stem, rel, here, tally, repair);
                break;

            case WzUOLProperty:
                // Never descend a UOL. WzUOLProperty returns the RESOLVED node's
                // children, so a walker that descends one is walking another
                // node's canvases and would repair them twice, or repair them in
                // an archive this call is not writing.
                return;

            case WzIntProperty number:
                if (property.Name.Equals("mag", StringComparison.OrdinalIgnoreCase)
                    || property.Name.EndsWith("Mag", StringComparison.Ordinal))
                    tally.NamedMag(property.Name, number.Value, here);
                break;

            case WzShortProperty small:
                if (property.Name.Equals("mag", StringComparison.OrdinalIgnoreCase)
                    || property.Name.EndsWith("Mag", StringComparison.Ordinal))
                    tally.NamedMag(property.Name, small.Value, here);
                break;
        }

        foreach (WzImageProperty child in property.WzProperties
                     ?? (IEnumerable<WzImageProperty>)Array.Empty<WzImageProperty>())
            Property(child, image, stem, rel, here, tally, repair);
    }

    private void Canvas(WzCanvasProperty canvas, WzImage image, string stem, string rel, string path,
                        Tally tally,
                        List<(WzImage Image, WzPngProperty Png, CanvasMagCase Case)>? repair)
    {
        tally.Canvases++;
        if (canvas.PngProperty is not WzPngProperty png) return;

        int format = (int)png.Format;
        int mag = png.Mag;
        tally.Pair(format, mag, path);

        // Magnification zero is the whole of an ordinary client. Nothing to
        // decide, and nothing to read the blob for.
        if (mag == 0) return;
        tally.WithMag++;

        string root = stem + ".wz/" + rel;
        string inside = path.Length > root.Length ? path.Substring(root.Length + 1) : "";
        CanvasMagCase one = Judge(stem, path, rel, inside, png, format, mag);
        tally.Cases.Add(one);

        if (one.Verdict == CanvasMagVerdict.Split && repair != null)
        {
            repair.Add((image, png, one));
            lock (_gate) _progress.Found = repair.Count;
        }
    }

    /// <summary>
    /// The decision, and every number it rests on.
    ///
    /// Three sizes are computed and the blob is measured against all three:
    /// what the joined format needs at full size, what the stored format needs
    /// at full size, and what the stored format needs at the reduced size a
    /// real magnification implies. Only a blob that matches the first and
    /// neither of the others is convicted.
    /// </summary>
    internal static CanvasMagCase Judge(string stem, string path, string image, string inside,
                                        WzPngProperty png, int format, int mag)
    {
        int width = png.Width, height = png.Height;
        int joined = format + (mag << 8);
        int rowWidth = mag < 32 ? width >> mag : 0;
        int compressed = png.CompressedLength;

        long JoinedSize() => ((WzPngFormat)joined).GetDecodedSize(width, height);
        long StoredFull() => ((WzPngFormat)format).GetDecodedSize(width, height);
        long StoredShrunk() => ((WzPngFormat)format)
            .GetDecodedSize(Math.Max(1, mag < 32 ? width >> mag : 0), Math.Max(1, mag < 32 ? height >> mag : 0));

        long joinedExpects = DecodableFormats.Contains(joined) ? JoinedSize() : -1;
        long storedFull = DecodableFormats.Contains(format) ? StoredFull() : -1;
        long storedShrunk = DecodableFormats.Contains(format) ? StoredShrunk() : -1;

        CanvasMagCase Case(CanvasMagVerdict verdict, long inflated, string why) => new(
            stem, path, image, inside, width, height, format, mag, joined, rowWidth, compressed,
            inflated, joinedExpects, storedFull, storedShrunk, verdict, why);

        if (joinedExpects < 0)
            return Case(CanvasMagVerdict.Genuine, -1,
                $"format {format} with mag {mag} joins to {joined}, which is not a format this " +
                "library can decode — so it is not a split value, whatever else it is.");

        // Only now is the blob read. A whole client is millions of canvases and
        // almost none of them get this far.
        long inflated = png.InflatedLength();

        if (inflated < 0)
            return Case(CanvasMagVerdict.Undecidable, -1,
                "the blob could not be inflated, so nothing here can say which format its pixels are " +
                "in. Left alone: not looking is not the same as finding it broken.");

        bool matchesJoined = inflated == joinedExpects;
        bool matchesStoredFull = storedFull >= 0 && inflated == storedFull;
        bool matchesStoredShrunk = storedShrunk >= 0 && inflated == storedShrunk;

        if (matchesJoined && !matchesStoredFull && !matchesStoredShrunk)
            return Case(CanvasMagVerdict.Split, inflated,
                $"the blob inflates to {inflated:N0} bytes, exactly what format {joined} needs for " +
                $"{width}x{height}. Format {format} would need {storedFull:N0} at that size or " +
                $"{storedShrunk:N0} at the {Math.Max(1, rowWidth)}px a mag of {mag} implies, and it is " +
                $"neither. The pixels are {joined}; the field was split across format and mag.");

        if (matchesStoredShrunk)
            return Case(CanvasMagVerdict.Genuine, inflated,
                $"the blob inflates to {inflated:N0} bytes, which is format {format} at the " +
                $"{Math.Max(1, rowWidth)}px a mag of {mag} implies. A real magnification. Untouched.");

        if (matchesStoredFull)
            return Case(CanvasMagVerdict.Genuine, inflated,
                $"the blob inflates to {inflated:N0} bytes, which is format {format} at full size. " +
                "The format field is describing the pixels correctly, so there is nothing joined here.");

        return Case(CanvasMagVerdict.Undecidable, inflated,
            $"the blob inflates to {inflated:N0} bytes, which is none of the three sizes this pair " +
            $"could mean ({joinedExpects:N0} as {joined}, {storedFull:N0} as {format} full size, " +
            $"{storedShrunk:N0} as {format} shrunk). Reported, not repaired.");
    }

    /* ====================================================================
       PLUMBING
       ==================================================================== */

    private CancellationTokenSource Begin(string state)
    {
        lock (_gate)
        {
            if (_progress.State is "scanning" or "repairing" or "saving" or "verifying")
                throw new InvalidOperationException("A canvas repair is already running.");
            _cancel = new CancellationTokenSource();
            _progress.State = state;
            _progress.Phase = "";
            _progress.Archive = "";
            _progress.ImagesDone = 0;
            _progress.CanvasesDone = 0;
            _progress.Found = 0;
            _progress.Seconds = 0;
            _progress.Error = null;
            return _cancel;
        }
    }

    private static List<string> Discover(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return new List<string>();

        if (File.Exists(path))
            return new List<string> { System.IO.Path.GetFullPath(path) };

        if (!Directory.Exists(path))
            return new List<string>();

        return Directory.GetFiles(path, "*.wz")
            .Where(f => MountedName.IsMatch(System.IO.Path.GetFileNameWithoutExtension(f)))
            .Where(f => new FileInfo(f).Length >= StubCeiling)
            .OrderBy(f => System.IO.Path.GetFileName(f), StringComparer.OrdinalIgnoreCase)
            .Select(System.IO.Path.GetFullPath)
            .ToList();
    }

    private static (WzMapleVersion, short, string) Encryption(List<string> archives, CanvasRepairOptions options)
    {
        if (!string.IsNullOrWhiteSpace(options.MapleVersion)
            && Enum.TryParse(options.MapleVersion, true, out WzMapleVersion told))
            return (told, options.GameVersion != 0 ? options.GameVersion : (short)-1,
                    $"Encryption {told} was supplied by the caller, not detected.");

        // Never detect from a stub: parsing a 6 KB placeholder with each
        // candidate key walks a length field that is noise and overflows the
        // stack, which cannot be caught.
        const long MinimumRealArchive = 1L << 20;
        string probe = archives.Where(a => new FileInfo(a).Length >= MinimumRealArchive)
                               .OrderBy(a => new FileInfo(a).Length)
                               .FirstOrDefault()
                       ?? archives.OrderByDescending(a => new FileInfo(a).Length).First();

        WzMapleVersion version = WzTool.DetectMapleVersion(probe, out short gameVersion);
        return (version, gameVersion,
                $"Encryption {version} and game version {gameVersion} detected from " +
                $"{System.IO.Path.GetFileName(probe)}.");
    }

    /// <summary>
    /// What the scan is entitled to conclude, said in the report rather than
    /// left for the reader to infer. A detector that only lists what it found
    /// cannot be checked; one that says what it examined and found clean can.
    /// </summary>
    private static IEnumerable<string> Conclusions(Tally tally)
    {
        yield return $"{tally.Canvases:N0} canvases examined in {tally.Images:N0} images. " +
                     $"{tally.WithMag:N0} carry a non-zero magnification in the canvas record; the " +
                     "rest carry zero, which cannot be a split value.";

        int split = tally.Cases.Count(c => c.Verdict == CanvasMagVerdict.Split);
        int genuine = tally.Cases.Count(c => c.Verdict == CanvasMagVerdict.Genuine);
        int undecidable = tally.Cases.Count(c => c.Verdict == CanvasMagVerdict.Undecidable);

        yield return $"Of those, {split:N0} were convicted by their payload, {genuine:N0} proved to be " +
                     $"real magnifications and {undecidable:N0} could not be decided and were left alone.";

        if (tally.NamedMagTotal > 0)
            yield return $"Separately, {tally.NamedMagTotal:N0} properties NAMED like a magnification " +
                         $"({string.Join(", ", tally.NamedMagNames.OrderBy(n => n, StringComparer.Ordinal))}) " +
                         "were counted. Those are ordinary int properties beside a canvas — a minimap's " +
                         "`mag`, a tile set's `tSMag` — not the canvas record's magnification field. " +
                         "This repair never reads them and never writes them.";
    }

    /* ====================================================================
       COUNTERS
       ==================================================================== */

    private sealed class Tally
    {
        public long Images;
        public long Canvases;
        public long WithMag;
        public readonly List<CanvasMagCase> Cases = new();

        private readonly Dictionary<(int, int), (long Count, string Example)> _pairs = new();
        private readonly Dictionary<string, (long Count, SortedSet<long> Values, string Example)> _named = new(StringComparer.Ordinal);

        public long NamedMagTotal { get; private set; }
        public IEnumerable<string> NamedMagNames => _named.Keys;

        public void Pair(int format, int mag, string example)
        {
            if (_pairs.TryGetValue((format, mag), out (long Count, string Example) at))
                _pairs[(format, mag)] = (at.Count + 1, at.Example);
            else
                _pairs[(format, mag)] = (1, example);
        }

        public void NamedMag(string name, long value, string example)
        {
            NamedMagTotal++;
            if (_named.TryGetValue(name, out (long Count, SortedSet<long> Values, string Example) at))
            {
                at.Values.Add(value);
                _named[name] = (at.Count + 1, at.Values, at.Example);
            }
            else
            {
                _named[name] = (1, new SortedSet<long> { value }, example);
            }
        }

        public List<CanvasPairCount> PairCounts() => _pairs
            .Select(p => new CanvasPairCount(p.Key.Item1, p.Key.Item2, p.Value.Count, p.Value.Example))
            .OrderByDescending(p => p.Count).ThenBy(p => p.Format).ThenBy(p => p.Mag)
            .ToList();

        public List<NamedMagCount> NamedMags() => _named
            .Select(n => new NamedMagCount(
                n.Key, n.Value.Count,
                string.Join(", ", n.Value.Values.Take(24)) + (n.Value.Values.Count > 24 ? ", …" : ""),
                n.Value.Example))
            .OrderByDescending(n => n.Count).ThenBy(n => n.Name, StringComparer.Ordinal)
            .ToList();
    }
}
