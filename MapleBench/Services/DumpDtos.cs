namespace MapleBench.Services;

/// <summary>
/// One thing the user can ask for, in the form the menu shows it.
///
/// The list is built from what a node IS rather than offered wholesale, because
/// an export menu that offers "animated GIF" on a string property teaches the
/// user that half the menu is a lie and the other half might be. Every entry
/// here is one this node can actually produce.
/// </summary>
public sealed class DumpFormatDto
{
    public string Id { get; set; } = "";
    public string Label { get; set; } = "";

    /// <summary>What it is for, and what it loses. Shown under the label.</summary>
    public string Note { get; set; } = "";

    /// <summary>
    /// "download" — a GET that returns a file; "job" — needs a folder on disk,
    /// runs with progress and can be cancelled.
    /// </summary>
    public string Kind { get; set; } = "download";

    /// <summary>Set for <c>Kind == "download"</c>.</summary>
    public string? Url { get; set; }

    /// <summary>Set for <c>Kind == "job"</c>: the value to POST as the format.</summary>
    public string? Job { get; set; }

    /// <summary>The one to offer first for this node kind.</summary>
    public bool Recommended { get; set; }
}

/// <summary>
/// A node that is a reference rather than the thing referenced.
///
/// Reported separately from the node's own description and never folded into
/// it. A link and its target are two nodes; exporting the target's pixels under
/// the link's name produces a file that is a plausible-looking lie about where
/// its contents came from, which is exactly the substitution this tool is
/// supposed to refuse. So the export offers both and makes the caller choose.
/// </summary>
public sealed class DumpLinkDto
{
    /// <summary>"uol", "_inlink" or "_outlink".</summary>
    public string Kind { get; set; } = "";

    /// <summary>The reference exactly as it is stored, unresolved.</summary>
    public string Text { get; set; } = "";

    /// <summary>Where it lands, when it lands anywhere.</summary>
    public string? TargetPath { get; set; }

    public string? TargetKind { get; set; }

    public bool Resolves { get; set; }

    /// <summary>Why it does not resolve, when it does not.</summary>
    public string? Note { get; set; }
}

/// <summary>What a node is, and therefore what can be got out of it.</summary>
public sealed class DumpTargetDto
{
    public string Path { get; set; } = "";
    public string Name { get; set; } = "";

    /// <summary>
    /// "Archive", "Directory", "Image", "Canvas", "Animation", "Sound",
    /// "Link", "Container" or "Value".
    /// </summary>
    public string Kind { get; set; } = "";

    /// <summary>The MapleLib type, for anyone who wants the exact answer.</summary>
    public string TypeName { get; set; } = "";

    /// <summary>A sentence about this node, shown above the format list.</summary>
    public string Note { get; set; } = "";

    public DumpLinkDto? Link { get; set; }

    public int Frames { get; set; }
    public int TotalMs { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }

    public string? CanvasFormat { get; set; }

    /// <summary>False when a canvas cannot hand back its stored bytes (a pure link).</summary>
    public bool HasRawBlob { get; set; }

    public int SoundMs { get; set; }
    public string? SoundExtension { get; set; }

    public List<DumpFormatDto> Formats { get; set; } = new();
}

/// <summary>
/// Something that did not come out, named.
///
/// Not a log line: this travels with the export, in the ZIP's report and in the
/// job's result, because "partial success" is the normal outcome of dumping
/// real client data and a dump that silently drops a frame is indistinguishable
/// from one that had nothing to drop.
/// </summary>
public sealed class DumpIssueDto
{
    public string Path { get; set; } = "";

    /// <summary>
    /// "canvas.undecodable", "sound.empty", "link.dangling", "name.collision",
    /// "name.sanitised", "node.unsupported", "budget.exhausted", "write.failed".
    /// </summary>
    public string Kind { get; set; } = "";

    public string Reason { get; set; } = "";
}

/// <summary>Where a dump came from, carried inside every export.</summary>
public sealed class DumpProvenanceDto
{
    public string Tool { get; set; } = "MapleBench";
    public string Node { get; set; } = "";
    public string Archive { get; set; } = "";

    /// <summary>The archive's path on disk, so a dump can be traced to a client.</summary>
    public string ArchiveFile { get; set; } = "";

    public string Exported { get; set; } = "";

    /// <summary>True when the archive held unsaved edits when this was dumped.</summary>
    public bool ArchiveHadUnsavedEdits { get; set; }
}

/// <summary>What one export produced, and what it did not.</summary>
public sealed class DumpResultDto
{
    public DumpProvenanceDto Provenance { get; set; } = new();
    public string Format { get; set; } = "";
    public int Files { get; set; }
    public long Bytes { get; set; }

    /// <summary>Canvases written, sounds written, values written — for a tree dump.</summary>
    public int Canvases { get; set; }
    public int Sounds { get; set; }
    public int Nodes { get; set; }

    /// <summary>
    /// Links met and NOT followed, which is the default. Counted rather than
    /// listed one by one when there are thousands; the first few hundred are in
    /// <see cref="Issues"/> when they dangle.
    /// </summary>
    public int LinksRecorded { get; set; }

    public bool Truncated { get; set; }
    public string? TruncatedReason { get; set; }

    public List<DumpIssueDto> Issues { get; set; } = new();

    /// <summary>Where it landed, for a dump to disk.</summary>
    public string? OutputPath { get; set; }
}

/// <summary>Poll shape for a dump to disk. Mirrors ImportProgressDto deliberately.</summary>
public sealed class DumpProgressDto
{
    /// <summary>"idle", "running", "done", "cancelled" or "failed".</summary>
    public string State { get; set; } = "idle";
    public string Stage { get; set; } = "";
    public string Node { get; set; } = "";

    /// <summary>The node being written right now, so a stall has a location.</summary>
    public string Current { get; set; } = "";

    public long Done { get; set; }

    /// <summary>0 while it is not yet known — a WZ tree is not counted before it is walked.</summary>
    public long Total { get; set; }

    public double Seconds { get; set; }
    public bool Cancelling { get; set; }
    public string? Message { get; set; }

    public DumpResultDto? Result { get; set; }
    public DateTime? FinishedUtc { get; set; }
}

/// <summary>A request to dump a subtree to a folder on disk.</summary>
public sealed class DumpJobRequest
{
    public string Path { get; set; } = "";

    /// <summary>The folder to create the dump inside. A subfolder is made in it.</summary>
    public string OutputDir { get; set; } = "";

    /// <summary>"tree" (PNG + audio + JSON sidecars) or "png" (canvases only).</summary>
    public string Format { get; set; } = "tree";

    /// <summary>
    /// Follow <c>_inlink</c>/<c>_outlink</c>/UOL and write the target's pixels
    /// under the referring node's name. Off by default, and every file written
    /// this way is marked as resolved in the sidecar.
    /// </summary>
    public bool ResolveLinks { get; set; }

    /// <summary>Write into a folder that already has files in it.</summary>
    public bool AllowNonEmpty { get; set; }

    public int MaxNodes { get; set; }
}

/// <summary>
/// What a dump to disk would refuse, answered before anything is written.
///
/// <see cref="Overridable"/> is the part worth having: two of the refusals are
/// "this looks wrong, but you may know better" and the rest are not, and the
/// caller must not have to tell them apart by reading the prose.
/// </summary>
public sealed class DumpPreflightDto
{
    public bool Ok { get; set; }

    /// <summary>Null when <see cref="Ok"/>; otherwise the reason, in full.</summary>
    public string? Refusal { get; set; }

    /// <summary>True when re-sending with <c>allowNonEmpty</c> would go through.</summary>
    public bool Overridable { get; set; }

    /// <summary>Where the dump would land. Set whenever it is known.</summary>
    public string? OutputPath { get; set; }
}
