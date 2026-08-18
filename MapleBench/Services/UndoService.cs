using MapleLib.WzLib;

namespace MapleBench.Services;

/// <summary>
/// The <see cref="WzImage"/>s one edit marked <c>Changed</c>, each with the flag
/// it carried beforehand.
///
/// Undo has to put these back or a fully-undone archive never returns to clean:
/// <c>Changed</c> is what makes a save re-serialise an image, and
/// <see cref="OpenFile.ToDto"/> reports any file holding a changed image as
/// dirty.  Restoring an image to a *recorded* previous value rather than to
/// <c>false</c> is the whole point — <c>Changed</c> is set from outside the undo
/// stack too.  MapleLib sets it on any add or remove into an image's own
/// property list (<c>WzImage.cs:305, 331, 352</c>), a cloned or newly created
/// image is born changed (<c>WzImage.cs:74, 210</c>), and an encryption-override
/// save force-marks the whole archive.  Clearing the flag blindly would throw
/// somebody else's unsaved work away; putting back what was there survives every
/// one of those cases.
/// </summary>
public sealed class ImageChangeLog
{
    // Reference identity, not equality: duplicate sibling names are ordinary in
    // WZ, so two genuinely different images must never collapse into one entry.
    private readonly Dictionary<WzImage, bool> _before = new(ReferenceEqualityComparer.Instance);

    /// <summary>
    /// Marks the image owning <paramref name="node"/> as changed, remembering
    /// the flag it had first.
    ///
    /// Only the first mark of a given image is kept: that is the value the edit
    /// *found*, whereas every later mark within the same edit reports a value
    /// the edit itself produced.
    /// </summary>
    public void Mark(WzObject node)
    {
        WzImage? image = node as WzImage ?? (node as WzImageProperty)?.ParentImage;
        if (image != null && !_before.ContainsKey(image))
        {
            // Read before MarkChanged, which parses the image and sets the flag.
            _before[image] = image.Changed;
        }
        WzNodeFactory.MarkChanged(node);
    }

    /// <summary>Puts every recorded flag back to what the edit found.</summary>
    internal void Restore()
    {
        foreach ((WzImage image, bool wasChanged) in _before)
            image.Changed = wasChanged;
    }

    /// <summary>Re-marks the same images after a redo.</summary>
    internal void Reapply()
    {
        foreach (WzImage image in _before.Keys)
            image.Changed = true;
    }

    /// <summary>The images this edit touched, for callers that must not disturb them.</summary>
    internal IEnumerable<WzImage> Images => _before.Keys;
}

/// <summary>
/// One reversible edit.  Undo/Redo are closures over live MapleLib objects, so
/// the history is only valid while those objects are alive — see
/// <see cref="UndoService.ClearForFile"/>, which is called when a file is saved
/// and reopened, and <see cref="UndoService.Clear"/> for the cases where the
/// whole session's trees are forfeit.
/// </summary>
public sealed class EditAction
{
    public required string Label { get; init; }
    public required Action Undo { get; init; }
    public required Action Redo { get; init; }

    /// <summary>Paths the UI should refresh after this action runs either way.</summary>
    public string[] AffectedPaths { get; init; } = Array.Empty<string>();

    /// <summary>
    /// True when replaying this action, in either direction, changes only
    /// property values — no node is added, removed, renamed, moved or reordered.
    ///
    /// It exists so an undo can say the narrower thing. Undoing anything at all
    /// used to declare the whole tree suspect, which discarded every browse list
    /// in the app: pressing Ctrl+Z after changing one number cost a five-second
    /// rebuild of the Mobs grid. Only <c>SetValue</c> and <c>SetValueMany</c>
    /// set this, and only because their undo closures call
    /// <c>WzNodeFactory.SetValue</c> and nothing else.
    ///
    /// Default false, deliberately: an action that forgets to declare itself is
    /// treated as structural, which costs a rebuild. The other way round costs
    /// a stale grid, and those are not comparable mistakes.
    /// </summary>
    public bool ValueOnly { get; init; }

    /// <summary>
    /// The images this edit marked changed.  Empty for structural edits that
    /// touch no image (adding a directory, moving an .img between folders).
    /// </summary>
    public ImageChangeLog Images { get; init; } = new();

    /// <summary>
    /// The entries a batch was composed from, or empty for a plain edit.
    ///
    /// A composite's own <see cref="Images"/> is empty by construction — the
    /// records live on the members, which the Undo and Redo closures capture.
    /// That is fine for replaying, and was wrong for anything asking "which
    /// images does this history refer to": <see cref="UndoService.ImagesInHistory"/>
    /// saw nothing for a batched edit, which is every mob card write and every
    /// bulk edit. Keeping the members reachable answers that without changing
    /// what a batch does when it runs.
    /// </summary>
    internal IReadOnlyList<EditAction> Members { get; init; } = Array.Empty<EditAction>();

    private string[]? _fileIds;

    /// <summary>
    /// The session files this action touches, derived from
    /// <see cref="AffectedPaths"/>.
    ///
    /// This is what makes the history per-archive: one entry can legitimately
    /// span two files (a cross-archive move), and every caller supplies real
    /// session paths, whose first segment is the file id.  An action recorded
    /// with no paths contributes to nobody's dirty state, which is why every
    /// call site sets them.
    /// </summary>
    public IReadOnlyList<string> FileIds =>
        _fileIds ??= AffectedPaths.Select(WzPath.FileId).Distinct(StringComparer.Ordinal).ToArray();

    /// <summary>
    /// Runs the undo, then restores the <c>Changed</c> flags it recorded.
    ///
    /// The order is load-bearing: the undo closures deliberately re-mark the
    /// images they touch (they are mutations like any other and the tree must
    /// stay consistent while they run), so restoring first would be immediately
    /// overwritten.
    /// </summary>
    internal void RunUndo()
    {
        Undo();
        Images.Restore();
    }

    internal void RunRedo()
    {
        Redo();
        Images.Reapply();
    }
}

/// <summary>
/// Bounded undo/redo history, shared across every open file.
///
/// It also owns the answer to "does this archive have unsaved work?".  That used
/// to be a one-way flag on <see cref="OpenFile"/>, so undoing every edit left the
/// file dirty forever — which is not cosmetic: the flag decides whether the close
/// prompt fires and whether a second launch may kill this process, and a save
/// then rewrote byte-identical images through every lossy re-serialisation path.
/// The model here is the stack position: a file is dirty while it has entries
/// that have not been undone, plus a sticky bit for edits that can no longer be
/// undone at all (evicted past the cap, dropped by <see cref="Clear"/>, or
/// declared non-undoable at edit time).
/// </summary>
public sealed class UndoService
{
    private const int MaxDepth = 250;

    private readonly LinkedList<EditAction> _undo = new();
    private readonly LinkedList<EditAction> _redo = new();
    private readonly object _gate = new();

    /// <summary>Open batches, innermost last. Non-null while a batch is active.</summary>
    private readonly List<(string Label, List<EditAction> Actions)> _batches = new();

    /// <summary>
    /// Per file: how many pushed entries touching it have not been undone.
    /// Zero (and unsealed) means the archive on disk and the tree in memory
    /// agree, so it may report clean.
    /// </summary>
    private readonly Dictionary<string, int> _pending = new(StringComparer.Ordinal);

    /// <summary>
    /// Files holding a change that no undo entry can take back any more.  They
    /// stay dirty until they are saved, whatever the stack says — forgetting this
    /// is how a file with real unsaved work would come to report clean.
    /// </summary>
    private readonly HashSet<string> _sealed = new(StringComparer.Ordinal);

    public void Record(EditAction action)
    {
        lock (_gate)
        {
            // Inside a batch, collect rather than push: writing one cash shop
            // item touches ~16 properties, so an unbatched bulk add of 16 items
            // would silently evict the entire history.
            if (_batches.Count > 0)
            {
                _batches[^1].Actions.Add(action);
                return;
            }

            Push(action);
        }
    }

    /// <summary>
    /// Groups everything recorded until the returned scope is disposed into a
    /// single undo entry. Batches nest; only the outermost one is pushed.
    /// </summary>
    public IDisposable Batch(string label)
    {
        lock (_gate)
            _batches.Add((label, new List<EditAction>()));
        return new BatchScope(this);
    }

    private void EndBatch()
    {
        lock (_gate)
        {
            if (_batches.Count == 0)
                return;

            (string label, List<EditAction> actions) = _batches[^1];
            _batches.RemoveAt(_batches.Count - 1);
            if (actions.Count == 0)
                return;

            EditAction composite = new()
            {
                Label = actions.Count == 1 ? actions[0].Label : $"{label} ({actions.Count} changes)",
                AffectedPaths = actions.SelectMany(a => a.AffectedPaths).Distinct().ToArray(),
                // Undo runs in reverse so nested inserts unwind in the order
                // they were applied.  RunUndo, not Undo: each member restores its
                // own Changed flags, and reverse order is exactly what makes
                // overlapping snapshots of the same image unwind correctly.
                Undo = () => { for (int i = actions.Count - 1; i >= 0; i--) actions[i].RunUndo(); },
                Redo = () => { foreach (EditAction action in actions) action.RunRedo(); },
                Members = actions,
                // A batch is value-only when every member is. One structural
                // member makes the whole replay structural, which is why this is
                // All and not Any -- a bulk edit that also created a missing
                // node has to invalidate properly.
                ValueOnly = actions.All(a => a.ValueOnly),
            };

            if (_batches.Count > 0)
            {
                _batches[^1].Actions.Add(composite);
                return;
            }

            Push(composite);
        }
    }

    /// <summary>
    /// Pushes onto the undo stack and keeps the per-file counters in step.
    /// Both push sites go through here so the counters cannot drift from the
    /// stack, which is the one way this model can silently lie.
    /// </summary>
    private void Push(EditAction action)
    {
        _undo.AddLast(action);
        foreach (string fileId in action.FileIds)
            Bump(fileId, +1);

        while (_undo.Count > MaxDepth)
        {
            EditAction evicted = _undo.First!.Value;
            _undo.RemoveFirst();
            // Past the cap the edit can never be taken back, so its archive is
            // dirty until it is saved however much of the rest is undone.
            foreach (string fileId in evicted.FileIds)
            {
                Bump(fileId, -1);
                _sealed.Add(fileId);
            }
        }
        _redo.Clear();
    }

    private void Bump(string fileId, int delta)
    {
        _pending.TryGetValue(fileId, out int count);
        count += delta;
        if (count <= 0)
            _pending.Remove(fileId);
        else
            _pending[fileId] = count;
    }

    private sealed class BatchScope : IDisposable
    {
        private readonly UndoService _owner;
        private bool _closed;

        public BatchScope(UndoService owner) => _owner = owner;

        public void Dispose()
        {
            if (_closed)
                return;
            _closed = true;
            _owner.EndBatch();
        }
    }

    public EditAction? Undo()
    {
        lock (_gate)
        {
            if (_undo.Count == 0)
                return null;

            EditAction action = _undo.Last!.Value;
            try
            {
                action.RunUndo();
            }
            catch
            {
                // The closures hold live MapleLib objects, so an undo can fail if
                // the tree moved underneath it. Dropping the entry here would
                // leave the tree half-reverted with no way to retry, so the
                // history goes and the caller is told plainly.
                //
                // SealAll first: half-reverted is still changed, and every file
                // that had pending work must keep reporting dirty.
                SealAll();
                _undo.Clear();
                _redo.Clear();
                throw new InvalidOperationException(
                    "That change could not be undone — the nodes it referred to have moved or been " +
                    "replaced. The undo history has been cleared. Nothing on disk was touched.");
            }

            _undo.RemoveLast();
            foreach (string fileId in action.FileIds)
                Bump(fileId, -1);
            _redo.AddLast(action);
            return action;
        }
    }

    public EditAction? Redo()
    {
        lock (_gate)
        {
            if (_redo.Count == 0)
                return null;

            EditAction action = _redo.Last!.Value;
            try
            {
                action.RunRedo();
            }
            catch
            {
                SealAll();
                _undo.Clear();
                _redo.Clear();
                throw new InvalidOperationException(
                    "That change could not be redone — the nodes it referred to have moved or been " +
                    "replaced. The undo history has been cleared. Nothing on disk was touched.");
            }

            _redo.RemoveLast();
            foreach (string fileId in action.FileIds)
                Bump(fileId, +1);
            // No cap check: this entry was on the stack a moment ago, so putting
            // it back cannot take the history over the limit.
            _undo.AddLast(action);
            return action;
        }
    }

    /// <summary>True while <paramref name="fileId"/> has work that is not on disk.</summary>
    public bool HasUnsavedEdits(string fileId)
    {
        lock (_gate)
            return _sealed.Contains(fileId) || _pending.ContainsKey(fileId);
    }

    /// <summary>
    /// One pending edit on one archive: what it was called, which of its nodes
    /// live in that archive, and whether it left any image carrying
    /// <c>Changed</c>.
    /// </summary>
    /// <param name="Label">The undo label, as the history shows it.</param>
    /// <param name="Paths">
    /// The edit's affected paths that belong to this archive, deduplicated. A
    /// batch carries every member's, so a delete of three images reports three.
    /// </param>
    /// <param name="MarkedAnImage">
    /// True when this edit is already visible through <c>WzImage.Changed</c>, so
    /// a caller listing dirty images has counted it once already.
    /// </param>
    public readonly record struct PendingEdit(string Label, string[] Paths, bool MarkedAnImage);

    /// <summary>
    /// The edits sitting on <paramref name="fileId"/> that have not been undone,
    /// newest first.
    ///
    /// This exists because <c>WzImage.Changed</c> cannot report the half of the
    /// work that is structural. Deleting an image, adding a directory, renaming
    /// or moving one — none of them leaves any image dirty, so
    /// <see cref="OpenFile.CountDirtyImages"/> answers 0 while
    /// <see cref="OpenFile.Dirty"/> is true, and <c>/api/changes</c> served a
    /// file with "0 changed images" and an empty list for real unsaved work.
    /// That is the ambiguous zero this codebase keeps finding, in the one panel
    /// a user reads before closing: 0 meaning "nothing pending" and 0 meaning
    /// "nothing this counter can see" are not the same answer.
    ///
    /// Only the undo side, and open batches. A redo entry describes a change
    /// that has already been taken back, so counting it would report work the
    /// archive no longer holds; an open batch's members are not pushed yet but
    /// their changes are already in the tree.
    ///
    /// It is not a complete account on its own, and the caller must not read it
    /// as one — an edit past <see cref="MaxDepth"/>, or one recorded with no
    /// undo entry at all (see <see cref="SealFile"/>), leaves the archive dirty
    /// with nothing here to show for it. That case is what <c>Dirty</c> with an
    /// empty result means, and it has to be said rather than shown as a zero.
    /// </summary>
    public IReadOnlyList<PendingEdit> PendingEdits(string fileId)
    {
        List<PendingEdit> found = new();
        lock (_gate)
        {
            for (LinkedListNode<EditAction>? node = _undo.Last; node != null; node = node.Previous)
                Take(node.Value);

            foreach ((_, List<EditAction> actions) in _batches)
            {
                for (int i = actions.Count - 1; i >= 0; i--)
                    Take(actions[i]);
            }
        }
        return found;

        void Take(EditAction action)
        {
            if (!action.FileIds.Contains(fileId, StringComparer.Ordinal))
                return;

            found.Add(new PendingEdit(
                action.Label,
                action.AffectedPaths
                    .Where(p => string.Equals(WzPath.FileId(p), fileId, StringComparison.Ordinal))
                    .Distinct(StringComparer.Ordinal)
                    .ToArray(),
                MarkedAnImage(action)));
        }

        // A batch's own log is empty by construction -- the records live on its
        // members -- so asking only the composite would call every batched edit
        // structural. See EditAction.Members.
        static bool MarkedAnImage(EditAction action) =>
            action.Images.Images.Any() || action.Members.Any(MarkedAnImage);
    }

    /// <summary>
    /// Records that a file was changed by something no undo entry covers, so it
    /// must report dirty until it is saved.
    ///
    /// The case this exists for is a canvas replace whose original pixels could
    /// not be read: there is nothing to restore, so no entry is recorded and the
    /// user is told at edit time that this one cannot be undone.  Without this
    /// the change would exist in the tree while the file claimed to be clean.
    /// </summary>
    public void SealFile(string fileId)
    {
        lock (_gate)
            _sealed.Add(fileId);
    }

    /// <summary>
    /// Drops only <paramref name="fileId"/>'s history, and marks it clean.
    ///
    /// This is what a save needs.  The blanket <see cref="Clear"/> was used for
    /// it, and the justification ("SaveToDisk unparses the tree") is true only of
    /// the archive being saved — with 20-40 archives open, which is the headline
    /// workflow, saving Etc.wz threw away the undo history of every unsaved edit
    /// in Map.wz, String.wz and the rest.
    ///
    /// An entry that also touches another archive still has to go: half of it
    /// refers to objects the save has just released, so it can never be replayed
    /// safely.  The other archive keeps its change and is sealed, because that
    /// change is now un-undoable rather than absent.
    /// </summary>
    public void ClearForFile(string fileId)
    {
        lock (_gate)
        {
            DropEntries(_undo, fileId, sealOthers: true);
            // Redo entries were already undone, so the changes they describe are
            // not in any tree — dropping them dirties nothing.
            DropEntries(_redo, fileId, sealOthers: false);

            _pending.Remove(fileId);
            _sealed.Remove(fileId);

            // Open batches too. They were the one place a reference to the closed
            // archive survived this call: a batch that has been started and not
            // yet ended is not in _undo or _redo, so nothing above touched it,
            // and its actions hold live WzImage and WzImageProperty objects out
            // of the tree that is about to be disposed. Two things then read
            // them — ImagesInHistory, which hands disposed images to the memory
            // sweep as things to protect, and EndBatch, which would push a
            // composite onto the undo stack whose closures run against a torn
            // down tree.
            //
            // Dropped whole rather than filtered: a batch is a unit, and half of
            // one is not something that can be undone. In practice a batch is
            // open only for the duration of a single request, so a close landing
            // inside one is already an abnormal moment.
            foreach ((_, List<EditAction> actions) in _batches)
                actions.RemoveAll(action => action.FileIds.Contains(fileId, StringComparer.Ordinal));
        }
    }

    private void DropEntries(LinkedList<EditAction> stack, string fileId, bool sealOthers)
    {
        LinkedListNode<EditAction>? node = stack.First;
        while (node != null)
        {
            LinkedListNode<EditAction> next = node.Next!;
            if (node.Value.FileIds.Contains(fileId, StringComparer.Ordinal))
            {
                if (sealOthers)
                {
                    foreach (string other in node.Value.FileIds)
                    {
                        if (string.Equals(other, fileId, StringComparison.Ordinal))
                            continue;
                        Bump(other, -1);
                        _sealed.Add(other);
                    }
                }
                stack.Remove(node);
            }
            node = next;
        }
    }

    /// <summary>
    /// Drops the whole history, for the failure paths where every tree in the
    /// session is forfeit.
    ///
    /// Note what it does *not* do: make anything clean.  The entries go, but the
    /// edits they described are still sitting in the trees, so every file that
    /// had pending work keeps reporting dirty — it simply can no longer be
    /// undone.  Prefer <see cref="ClearForFile"/> wherever only one archive was
    /// released.
    /// </summary>
    public void Clear()
    {
        lock (_gate)
        {
            SealAll();
            _undo.Clear();
            _redo.Clear();
            _batches.Clear();
        }
    }

    private void SealAll()
    {
        foreach (string fileId in _pending.Keys)
            _sealed.Add(fileId);
        _pending.Clear();
    }

    /// <summary>
    /// Every image the history still holds live references into.
    ///
    /// <see cref="EditAction.Undo"/> and <see cref="EditAction.Redo"/> are
    /// closures over MapleLib objects, not over paths, so an entry can only be
    /// replayed while the objects it captured are the ones still hanging off the
    /// tree. <see cref="ImageMemoryService"/> asks this before releasing
    /// anything: unparsing an image replaces every property under it, and a redo
    /// of an already-undone edit would then write into the detached copy — no
    /// error, no effect, and the archive marked dirty for a change that is not
    /// there.
    ///
    /// The undo direction is covered by the <c>Changed</c> flag on its own (an
    /// entry that has not been undone leaves its images changed, and changed
    /// images are never released), but the redo stack is not — its entries have
    /// had their flags restored, which is exactly what makes those images look
    /// safe to release.
    /// </summary>
    public HashSet<WzImage> ImagesInHistory()
    {
        // Reference identity, matching ImageChangeLog: duplicate sibling names
        // are ordinary in WZ, so two different images must not collapse into one.
        HashSet<WzImage> images = new(ReferenceEqualityComparer.Instance);
        lock (_gate)
        {
            foreach (EditAction action in _undo.Concat(_redo))
                Collect(action, images);

            // Batches that are still open: their members are recorded but not
            // yet pushed, and they refer to images just as much.
            foreach ((_, List<EditAction> actions) in _batches)
            {
                foreach (EditAction action in actions)
                    Collect(action, images);
            }
        }
        return images;

        static void Collect(EditAction action, HashSet<WzImage> into)
        {
            foreach (WzImage image in action.Images.Images)
                into.Add(image);
            // A batch keeps its records on its members; see EditAction.Members.
            foreach (EditAction member in action.Members)
                Collect(member, into);
        }
    }

    public (string? NextUndo, string? NextRedo, int UndoDepth, int RedoDepth) Peek()
    {
        lock (_gate)
        {
            return (_undo.Last?.Value.Label, _redo.Last?.Value.Label, _undo.Count, _redo.Count);
        }
    }
}
