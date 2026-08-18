using MapleLib.WzLib;
using MapleLib.WzLib.WzProperties;
using MapleBench.Models;

namespace MapleBench.Services;

/// <summary>
/// Every mutation the editor can perform, each recorded as a reversible
/// <see cref="EditAction"/>.  All work happens under
/// <see cref="WzSessionService.Gate"/> so a request never observes a half-applied
/// tree.
/// </summary>
public sealed class WzEditService
{
    private readonly WzSessionService _session;
    private readonly UndoService _undo;

    /// <summary>
    /// Optional, and optional on purpose: a session with no merged families is
    /// the normal one, every test constructs this service with two arguments,
    /// and a mutation that never sees a family path behaves identically either
    /// way.  When it is present, <see cref="EnsureWritable"/> can tell a merged
    /// path from a mistyped one and say so.
    /// </summary>
    private readonly ArchiveFamilyService? _families;

    public WzEditService(WzSessionService session, UndoService undo,
                         ArchiveFamilyService? families = null)
    {
        _session = session;
        _undo = undo;
        _families = families;
    }

    #region Values

    public NodeDto SetValue(string path, string? value)
    {
        lock (_session.Gate)
        {
            EnsureWritable(path);
            WzObject node = _session.Resolve(path);
            if (node is not WzImageProperty property)
                throw new InvalidOperationException("Only properties have editable values.");

            string? previous = WzNodeFactory.SetValue(property, value);
            ImageChangeLog images = new();
            images.Mark(property);
            MarkValueChanged(path);

            _undo.Record(new EditAction
            {
                Label = $"Set {property.Name}",
                AffectedPaths = new[] { path },
                Images = images,
                // Both closures only set a value on a property that already
                // exists, so replaying either way cannot move a node. See
                // EditAction.ValueOnly for what that buys.
                ValueOnly = true,
                Undo = () => { WzNodeFactory.SetValue(property, previous); WzNodeFactory.MarkChanged(property); },
                Redo = () => { WzNodeFactory.SetValue(property, value); WzNodeFactory.MarkChanged(property); },
            });

            return _session.ToDto(property, path);
        }
    }

    /// <summary>
    /// Applies an <see cref="ValueMath"/> expression to many nodes, each against
    /// its own current value.
    ///
    /// A dry run walks the identical path and throws the answers away, so the
    /// preview a user approves is produced by the code that does the writing.
    /// The alternative -- computing the preview on the client -- is how a tool
    /// ends up showing "15 → 23" and writing something else, and this one writes
    /// to game files.
    ///
    /// Everything is computed before anything is written. A node whose type
    /// rejects the result is reported as skipped and the rest still land; a node
    /// that is not a number at all never reaches the writer. The whole batch is
    /// one undo entry, because "make 312 mobs 50% tougher" is one decision and
    /// undoing it should be one Ctrl+Z rather than 312.
    /// </summary>
    public ComputeValuesResult ComputeValues(ComputeValuesRequest request)
    {
        lock (_session.Gate)
        {
            List<string> targets = request.Paths.Distinct().ToList();

            // Up front and per archive, exactly as SetValueMany does: a
            // selection can span files, and discovering half-way that the
            // second one is open read-only would leave the first one changed.
            foreach (string file in targets.Select(WzPath.FileId).Distinct())
                EnsureWritable(file);

            ComputeValuesResult result = new()
            {
                Description = ValueMath.Describe(request.Expression),
            };

            // The row and the property travel together so the write loop can
            // report a late failure on the row it belongs to. Matching them up
            // afterwards by value would mis-attribute whenever two nodes compute
            // to the same number, which for a bulk edit is the common case.
            List<(ComputedValueDto Row, WzImageProperty Property, string? Previous, string? Next)> plan = new();

            foreach (string path in targets)
            {
                ComputedValueDto row = new() { Path = path, Name = WzPath.Split(path).LastOrDefault() ?? path };
                result.Results.Add(row);

                if (_session.Resolve(path) is not WzImageProperty property)
                {
                    row.Skipped = "is not an editable property";
                    continue;
                }

                NodeDto dto = _session.ToDto(property, path);
                row.Name = dto.Name;
                row.Before = dto.Value;

                ValueMath.Outcome outcome = ValueMath.Apply(request.Expression, dto.Value, dto.Type);
                if (!outcome.Ok)
                {
                    row.Skipped = outcome.Skipped;
                    continue;
                }

                row.After = outcome.Value;
                plan.Add((row, property, dto.Value, outcome.Value));
            }

            // Counted from the plan, never from the request: a caller that asked
            // for 312 and got 40 must be told 40. The toast reads these numbers.
            result.Changed = plan.Count;
            result.Skipped = result.Results.Count - plan.Count;

            if (request.DryRun || plan.Count == 0)
                return result;

            ImageChangeLog images = new();
            List<(WzImageProperty Property, string? Previous, string? Next)> written = new();
            List<string> touched = new();

            foreach ((ComputedValueDto row, WzImageProperty property, string? previous, string? next) in plan)
            {
                try
                {
                    WzNodeFactory.SetValue(property, next);
                    images.Mark(property);
                    written.Add((property, previous, next));
                    touched.Add(row.Path);
                }
                catch (Exception ex)
                {
                    // The type refused the computed value after all. Report it on
                    // its own row and drop it from the count -- reporting 312
                    // changed when 40 landed is the one failure this method is
                    // shaped to prevent.
                    row.Skipped = ex.Message;
                    row.After = null;
                }
            }

            result.Changed = written.Count;
            result.Skipped = result.Results.Count - written.Count;
            result.Applied = written.Count > 0;

            if (written.Count == 0)
                return result;

            foreach (string edited in touched)
                MarkValueChanged(edited);

            _undo.Record(new EditAction
            {
                Label = $"{ValueMath.Describe(request.Expression)} ({written.Count} value{(written.Count == 1 ? "" : "s")})",
                AffectedPaths = touched.ToArray(),
                Images = images,
                ValueOnly = true,   // see SetValue
                Undo = () =>
                {
                    foreach ((WzImageProperty property, string? previous, _) in written)
                    {
                        WzNodeFactory.SetValue(property, previous);
                        WzNodeFactory.MarkChanged(property);
                    }
                },
                Redo = () =>
                {
                    foreach ((WzImageProperty property, _, string? next) in written)
                    {
                        WzNodeFactory.SetValue(property, next);
                        WzNodeFactory.MarkChanged(property);
                    }
                },
            });

            return result;
        }
    }

    /// <summary>
    /// Applies the same value to many nodes at once.  Nodes that reject the
    /// value (wrong type) are reported rather than aborting the whole batch.
    /// </summary>
    public (List<NodeDto> Updated, List<string> Failed) SetValueMany(IEnumerable<string> paths, string? value)
    {
        lock (_session.Gate)
        {
            // Materialised: the guard below and the loop under it each enumerate,
            // and a lazy sequence would be consumed by the first pass.
            List<string> targets = paths.ToList();

            // Guarded up front, per archive: a multi-select can span files, and
            // failing half-way would leave the writable ones changed.
            foreach (string file in targets.Select(WzPath.FileId).Distinct())
                EnsureWritable(file);

            List<NodeDto> updated = new();
            List<string> failed = new();
            List<(WzImageProperty Property, string? Previous)> undoState = new();
            List<string> touched = new();
            ImageChangeLog images = new();

            foreach (string path in targets)
            {
                try
                {
                    if (_session.Resolve(path) is not WzImageProperty property)
                    {
                        failed.Add(path);
                        continue;
                    }
                    string? previous = WzNodeFactory.SetValue(property, value);
                    images.Mark(property);
                    undoState.Add((property, previous));
                    touched.Add(path);
                    updated.Add(_session.ToDto(property, path));
                }
                catch
                {
                    failed.Add(path);
                }
            }

            if (undoState.Count > 0)
            {
                // A multi-select can span archives. Marking only the first left
                // the second file looking clean in the title bar.
                //
                // Per edited path rather than per archive, because the browse
                // lists patch by image: told only "f11 changed" they would have
                // to rebuild the whole archive's list, which is the cost this
                // exists to avoid.
                foreach (string edited in touched)
                    MarkValueChanged(edited);
                _undo.Record(new EditAction
                {
                    Label = $"Set {undoState.Count} values",
                    AffectedPaths = touched.ToArray(),
                    Images = images,
                    ValueOnly = true,   // see SetValue
                    Undo = () =>
                    {
                        foreach ((WzImageProperty property, string? previous) in undoState)
                        {
                            WzNodeFactory.SetValue(property, previous);
                            WzNodeFactory.MarkChanged(property);
                        }
                    },
                    Redo = () =>
                    {
                        foreach ((WzImageProperty property, _) in undoState)
                        {
                            WzNodeFactory.SetValue(property, value);
                            WzNodeFactory.MarkChanged(property);
                        }
                    },
                });
            }
            return (updated, failed);
        }
    }

    #endregion

    #region Structure

    public NodeDto Rename(string path, string newName)
    {
        if (string.IsNullOrWhiteSpace(newName))
            throw new ArgumentException("A name is required.");

        lock (_session.Gate)
        {
            EnsureWritable(path);
            WzObject node = _session.Resolve(path);
            string oldName = node.Name ?? "";
            if (oldName == newName)
                return _session.ToDto(node, path);

            // Add checks for a sibling clash and Rename did not, so renaming
            // 'b' to 'a' beside an existing 'a' produced two 'a's — and the
            // path returned below has no occurrence suffix, so it resolved to
            // the *other* one. Every later edit through it hit the wrong node.
            EnsureNameIsFree(path, newName);

            SetName(node, newName);
            ImageChangeLog images = new();
            images.Mark(node);
            MarkFileDirty(path);

            string? parentPath = WzPath.Parent(path);
            string newPath = parentPath == null ? newName : WzPath.Child(parentPath, newName);

            _undo.Record(new EditAction
            {
                Label = $"Rename {oldName} to {newName}",
                AffectedPaths = new[] { parentPath ?? path },
                Images = images,
                Undo = () => { SetName(node, oldName); WzNodeFactory.MarkChanged(node); },
                Redo = () => { SetName(node, newName); WzNodeFactory.MarkChanged(node); },
            });

            return _session.ToDto(node, newPath);
        }
    }

    private static void SetName(WzObject node, string name) => node.Name = name;

    /// <summary>
    /// Rejects a rename that would collide with a sibling.  Duplicate sibling
    /// names are addressable through the "#n" occurrence suffix, but a rename
    /// hands back a plain path, so allowing the collision here would silently
    /// point the caller at the wrong node.
    /// </summary>
    private void EnsureNameIsFree(string path, string newName)
    {
        string? parentPath = WzPath.Parent(path);
        if (parentPath == null)
            return; // renaming a file root; there are no siblings to clash with

        WzObject? parent = _session.TryResolve(parentPath);
        if (parent == null)
            return;

        foreach (WzObject sibling in _session.EnumerateChildren(parent))
        {
            if (string.Equals(sibling.Name, newName, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException($"'{newName}' already exists here.");
        }
    }

    public NodeDto Add(AddNodeRequest request)
    {
        lock (_session.Gate)
        {
            EnsureWritable(request.Path);
            WzObject parent = _session.Resolve(request.Path);

            if (parent is WzDirectory directory)
                return AddToDirectory(directory, request);

            WzPropertyCollection? collection = WzNodeFactory.GetPropertyCollection(parent);
            if (collection == null)
                throw new InvalidOperationException(
                    $"'{parent.Name}' cannot contain children. Add to a Property, Canvas, Convex or .img instead.");

            if (collection.FindByName(request.Name) != null)
                throw new InvalidOperationException($"'{request.Name}' already exists here.");

            WzImageProperty created = WzNodeFactory.Create(request.Type, request.Name, request.Value);

            // A Convex can only hold extended properties, and the WZ format gives
            // it no way to say otherwise: WzConvexProperty.WriteValue declares a
            // child count of just its extended children and then writes the bodies
            // of all of them, so a scalar added here is written out and then
            // skipped by the reader, which stops after `count` children and jumps
            // to the end of the block. The node was gone after the next save with
            // nothing reporting it -- not at edit time, not at save time (the
            // verification compares image sizes and checksums, not property
            // trees), and not at reopen.
            //
            // WzConvexProperty.AddProperty guards against exactly this, but
            // nothing here went through it: GetPropertyCollection hands back the
            // raw collection and this method adds to it directly. Refusing is the
            // honest answer -- the alternative is accepting an edit that cannot
            // survive being written.
            if (parent is WzConvexProperty && created is not WzExtended)
            {
                throw new InvalidOperationException(
                    $"A Convex node can only hold Vector, Canvas, Sound, SubProperty, Convex or UOL " +
                    $"children, so '{request.Type}' cannot be added to '{parent.Name}'. " +
                    "Nothing was changed.");
            }

            collection.Add(created);
            ImageChangeLog images = new();
            images.Mark(parent);
            MarkFileDirty(request.Path);

            string childPath = WzPath.Child(request.Path, request.Name);
            _undo.Record(new EditAction
            {
                Label = $"Add {request.Name}",
                AffectedPaths = new[] { request.Path },
                Images = images,
                Undo = () => { collection.Remove(created); WzNodeFactory.MarkChanged(parent); },
                Redo = () => { collection.Add(created); WzNodeFactory.MarkChanged(parent); },
            });

            return _session.ToDto(created, childPath);
        }
    }

    private NodeDto AddToDirectory(WzDirectory directory, AddNodeRequest request)
    {
        OpenFile file = _session.GetFileForPath(request.Path);

        if (request.Type.Equals("Directory", StringComparison.OrdinalIgnoreCase))
        {
            if (directory.GetDirectoryByName(request.Name) != null)
                throw new InvalidOperationException($"A directory named '{request.Name}' already exists here.");

            // Cloning the IV/version hash from the parent archive is required or
            // the new directory writes with the wrong encryption on save.
            WzDirectory created = file.WzFile != null
                ? new WzDirectory(request.Name, file.WzFile)
                : new WzDirectory(request.Name);
            directory.AddDirectory(created);
            // Through MarkFileDirty, not `file.Dirty = true`: adding a node is
            // precisely the case the resolution cache cannot survive on its own. Its
            // entries are re-verified by parent and name, so a stale one is caught --
            // but a *new sibling shadowing a cached name* looks valid to that check.
            // The collision guards above are per-kind, so a directory may legally be
            // added beside an image of the same name, and every later edit through
            // that path would land on whichever the cache already held.
            MarkFileDirty(request.Path);

            _undo.Record(new EditAction
            {
                Label = $"Add directory {request.Name}",
                AffectedPaths = new[] { request.Path },
                Undo = () => directory.RemoveDirectory(created),
                Redo = () => directory.AddDirectory(created),
            });
            return _session.ToDto(created, WzPath.Child(request.Path, request.Name));
        }

        if (request.Type.Equals("Image", StringComparison.OrdinalIgnoreCase))
        {
            string name = request.Name.EndsWith(".img", StringComparison.OrdinalIgnoreCase)
                ? request.Name
                : request.Name + ".img";
            if (directory.GetImageByName(name) != null)
                throw new InvalidOperationException($"An image named '{name}' already exists here.");

            WzImage created = new(name);
            // A fresh image has no backing reader; marking it parsed keeps the
            // tree from trying to lazily read one.
            created.MarkWzImageAsParsed();
            // No ImageChangeLog for this flag: undo removes the image from the
            // directory outright, so it stops being counted rather than needing
            // its flag put back.
            created.Changed = true;
            directory.AddImage(created);
            MarkFileDirty(request.Path);

            _undo.Record(new EditAction
            {
                Label = $"Add image {name}",
                AffectedPaths = new[] { request.Path },
                Undo = () => directory.RemoveImage(created),
                Redo = () => directory.AddImage(created),
            });
            return _session.ToDto(created, WzPath.Child(request.Path, name));
        }

        throw new InvalidOperationException(
            "A directory can only contain images and other directories. Use type 'Image' or 'Directory'.");
    }

    public int Delete(IEnumerable<string> paths)
    {
        List<string> resolvedPaths = paths.ToList();
        lock (_session.Gate)
        {
            // Refused before anything is resolved: a reference-only archive must
            // come out of this call untouched, not merely un-flagged.
            foreach (string file in resolvedPaths.Select(WzPath.FileId).Distinct())
                EnsureWritable(file);

            // Every path is resolved to an object BEFORE anything is removed.
            //
            // WZ allows duplicate sibling names, and paths address them by
            // occurrence ("delay", "delay#1", "delay#2"). Those indices shift
            // the instant an earlier sibling goes, so resolving as we delete
            // would remove the wrong nodes and silently skip others.
            List<(string Path, WzObject Node, string ParentPath, WzObject Parent)> targets = new();
            foreach (string path in resolvedPaths.Distinct())
            {
                WzObject? node = _session.TryResolve(path);
                if (node == null)
                    continue;

                string parentPath = WzPath.Parent(path)
                    ?? throw new InvalidOperationException("Close the file instead of deleting its root.");
                targets.Add((path, node, parentPath, _session.Resolve(parentPath)));
            }

            // Deepest-first so removing a parent doesn't strand a queued child.
            targets = targets.OrderByDescending(t => t.Path.Count(c => c == '/')).ToList();

            // Every property target's collection is resolved before ANY node is
            // removed.
            //
            // The removal loop below used to reach this lookup per target and
            // throw from inside it, with the undo entry recorded only once the
            // loop had finished -- so a batch whose third member could not be
            // removed left the first two gone and nothing on the undo stack
            // covering them. A deletion outside the undo batch is the one failure
            // in this editor a user cannot recover from, and it does not become
            // acceptable because the trigger is currently hard to reach.
            //
            // Only the collection is resolved here, never an index: indices shift
            // as siblings are removed, so those still have to be read at the
            // moment of removal. The collection object itself does not move.
            Dictionary<string, WzPropertyCollection> collections = new(StringComparer.Ordinal);
            foreach ((string path, WzObject node, _, WzObject parent) in targets)
            {
                if (node is not WzImageProperty)
                    continue;

                collections[path] = WzNodeFactory.GetPropertyCollection(parent)
                    ?? throw new InvalidOperationException(
                        $"'{parent.Name}' holds no property list, so '{node.Name}' cannot be removed " +
                        "from it. Nothing was deleted.");
            }

            List<Action> undoSteps = new();
            List<Action> redoSteps = new();
            List<string> affected = new();
            ImageChangeLog images = new();
            int removed = 0;

            foreach ((string path, WzObject node, string parentPath, WzObject parent) in targets)
            {
                affected.Add(parentPath);

                switch (node)
                {
                    case WzImageProperty property:
                    {
                        // Resolved in the pre-pass above; by here it cannot fail.
                        WzPropertyCollection collection = collections[path];
                        int index = collection.IndexOf(property);
                        undoSteps.Add(() =>
                        {
                            if (index >= 0 && index <= collection.Count)
                                collection.Insert(index, property);
                            else
                                collection.Add(property);
                            WzNodeFactory.MarkChanged(parent);
                        });
                        redoSteps.Add(() => { collection.Remove(property); WzNodeFactory.MarkChanged(parent); });
                        collection.Remove(property);
                        images.Mark(parent);
                        break;
                    }
                    case WzImage image when parent is WzDirectory dir:
                        undoSteps.Add(() => dir.AddImage(image));
                        redoSteps.Add(() => dir.RemoveImage(image));
                        dir.RemoveImage(image);
                        break;

                    case WzDirectory subdir when parent is WzDirectory dir:
                        undoSteps.Add(() => dir.AddDirectory(subdir));
                        redoSteps.Add(() => dir.RemoveDirectory(subdir));
                        dir.RemoveDirectory(subdir);
                        break;

                    default:
                        continue;
                }
                MarkFileDirty(path);
                removed++;
            }

            if (removed > 0)
            {
                _undo.Record(new EditAction
                {
                    Label = removed == 1 ? "Delete node" : $"Delete {removed} nodes",
                    AffectedPaths = affected.Distinct().ToArray(),
                    Images = images,
                    // Undo in reverse so re-inserted indices line up.
                    Undo = () => { for (int i = undoSteps.Count - 1; i >= 0; i--) undoSteps[i](); },
                    Redo = () => { foreach (Action step in redoSteps) step(); },
                });
            }
            return removed;
        }
    }

    /// <summary>
    /// Clones nodes next to their originals, appending " copy" / " copy 2" as
    /// needed.  This is the fast path for "make another one like this".
    /// </summary>
    public List<NodeDto> Duplicate(IEnumerable<string> paths)
    {
        List<string> resolvedPaths = paths.ToList();
        lock (_session.Gate)
        {
            // Refused before anything is resolved: a reference-only archive must
            // come out of this call untouched, not merely un-flagged.
            foreach (string file in resolvedPaths.Select(WzPath.FileId).Distinct())
                EnsureWritable(file);

            List<NodeDto> created = new();
            List<Action> undoSteps = new();
            List<Action> redoSteps = new();
            List<string> affected = new();
            ImageChangeLog images = new();

            // Resolved up front: adding a clone shifts the occurrence index of
            // every later duplicate-named sibling, so paths must not be
            // re-resolved once the first insert has happened.
            List<(WzObject Node, string ParentPath, WzObject Parent)> targets = new();
            foreach (string path in resolvedPaths.Distinct())
            {
                WzObject node = _session.Resolve(path);
                string parentPath = WzPath.Parent(path)
                    ?? throw new InvalidOperationException("The root cannot be duplicated.");
                targets.Add((node, parentPath, _session.Resolve(parentPath)));
            }

            foreach ((WzObject node, string parentPath, WzObject parent) in targets)
            {
                affected.Add(parentPath);

                switch (node)
                {
                    case WzImageProperty property:
                    {
                        WzPropertyCollection collection = WzNodeFactory.GetPropertyCollection(parent)
                            ?? throw new InvalidOperationException($"'{parent.Name}' has no property list.");
                        WzImageProperty clone = property.DeepClone();
                        clone.Name = UniqueName(property.Name ?? "item", n => collection.FindByName(n) != null);
                        collection.Add(clone);
                        images.Mark(parent);
                        undoSteps.Add(() => { collection.Remove(clone); WzNodeFactory.MarkChanged(parent); });
                        redoSteps.Add(() => { collection.Add(clone); WzNodeFactory.MarkChanged(parent); });
                        created.Add(_session.ToDto(clone, WzPath.Child(parentPath, clone.Name)));
                        break;
                    }
                    case WzImage image when parent is WzDirectory dir:
                    {
                        WzImage clone = image.DeepClone();
                        clone.Name = UniqueImageName(image.Name ?? "new.img", n => dir.GetImageByName(n) != null);
                        clone.Changed = true;
                        dir.AddImage(clone);
                        undoSteps.Add(() => dir.RemoveImage(clone));
                        redoSteps.Add(() => dir.AddImage(clone));
                        created.Add(_session.ToDto(clone, WzPath.Child(parentPath, clone.Name)));
                        break;
                    }
                    case WzDirectory subdir when parent is WzDirectory dir:
                    {
                        WzDirectory clone = subdir.DeepClone();
                        clone.Name = UniqueName(subdir.Name ?? "folder", n => dir.GetDirectoryByName(n) != null);
                        dir.AddDirectory(clone);
                        undoSteps.Add(() => dir.RemoveDirectory(clone));
                        redoSteps.Add(() => dir.AddDirectory(clone));
                        created.Add(_session.ToDto(clone, WzPath.Child(parentPath, clone.Name)));
                        break;
                    }
                }
                MarkFileDirty(parentPath);
            }

            if (created.Count > 0)
            {
                _undo.Record(new EditAction
                {
                    Label = created.Count == 1 ? "Duplicate node" : $"Duplicate {created.Count} nodes",
                    AffectedPaths = affected.Distinct().ToArray(),
                    Images = images,
                    Undo = () => { for (int i = undoSteps.Count - 1; i >= 0; i--) undoSteps[i](); },
                    Redo = () => { foreach (Action step in redoSteps) step(); },
                });
            }
            return created;
        }
    }

    /// <summary>
    /// Copy or move nodes under a new parent — the backing operation for
    /// drag-and-drop and for cut/copy/paste across files.
    /// </summary>
    public List<NodeDto> Transfer(TransferRequest request)
    {
        lock (_session.Gate)
        {
            EnsureWritable(request.TargetPath);

            // The source end only when this is a move, because only a move
            // changes it. A copy reads the source and clones; the source tree is
            // not touched, its images are not marked, and the file is not marked
            // dirty (see the `if (request.Move)` guards below and in MarkDirty).
            //
            // Checking it unconditionally broke the one workflow read-only exists
            // for. "Open the old client for reference and port a change out of it"
            // is the reason for the flag -- and porting is a copy, so every port
            // out of a locked archive was refused. It surfaced per-part, deep in
            // PortService's catch, as an error about the *source* being open for
            // reference only, on an operation that was never going to write to it.
            // Opening a split client read-only makes that the normal case rather
            // than an unlucky one: there is no writable way to open it at all.
            if (request.Move)
            {
                foreach (string file in request.Paths.Select(WzPath.FileId).Distinct())
                    EnsureWritable(file);
            }

            WzObject target = _session.Resolve(request.TargetPath);
            List<NodeDto> results = new();
            List<Action> undoSteps = new();
            List<Action> redoSteps = new();
            ImageChangeLog images = new();

            // Resolve and validate everything before touching the tree.
            //
            // The loop below mutates as it goes; throwing from inside it would
            // leave some nodes moved and some not, with no undo entry recorded
            // for the ones that were. Occurrence indices shift on every insert
            // too, so re-resolving mid-loop would target the wrong siblings.
            List<(string Path, WzObject Node)> queued = new();
            foreach (string path in request.Paths.Distinct())
            {
                if (request.TargetPath == path || request.TargetPath.StartsWith(path + "/", StringComparison.Ordinal))
                    throw new InvalidOperationException("A node cannot be moved inside itself.");

                WzObject candidate = _session.Resolve(path);
                bool acceptable = candidate is WzImageProperty
                    ? WzNodeFactory.GetPropertyCollection(target) != null
                    : candidate is WzImage && target is WzDirectory;
                if (!acceptable)
                {
                    throw new InvalidOperationException(
                        $"'{candidate.Name}' cannot be placed inside '{target.Name}'.");
                }

                // The same trap Add() guards against, reached by a different
                // door. A Convex answers GetPropertyCollection, so the check
                // above waves a scalar through -- but WzConvexProperty.WriteValue
                // declares a child count of only its *extended* children and then
                // writes the bodies of all of them, so a scalar in there is
                // written out and then skipped by the reader, which stops after
                // `count` children and jumps to the end of the block.
                //
                // The node is simply gone after the next save, with nothing
                // reporting it: not at edit time, not at save time (verification
                // compares image sizes and checksums, not property trees), and
                // not at reopen. Refusing before anything moves is the only
                // honest answer -- and it has to be here as well as in Add(),
                // because Ctrl+V and drag-and-drop both arrive through Transfer.
                if (target is WzConvexProperty && candidate is not WzExtended)
                {
                    throw new InvalidOperationException(
                        $"A Convex node can only hold Vector, Canvas, Sound, SubProperty, Convex or UOL " +
                        $"children, so '{candidate.Name}' cannot be placed inside '{target.Name}'. " +
                        "Nothing was changed.");
                }

                queued.Add((path, candidate));
            }

            // Where the next property lands when the caller asked for a
            // position. Carried across the loop and stepped on each insert so a
            // multi-node drop keeps the order it was dragged in; without that,
            // every node would be inserted at the same index and the group would
            // arrive reversed.
            int insertAt = request.Index ?? 0;

            foreach ((string path, WzObject node) in queued)
            {

                if (node is WzImageProperty property)
                {
                    WzPropertyCollection collection = WzNodeFactory.GetPropertyCollection(target)
                        ?? throw new InvalidOperationException(
                            $"'{target.Name}' cannot contain properties.");

                    // Cloning even for a move keeps the source tree intact until
                    // the removal below succeeds, and detaches cross-file parents.
                    WzImageProperty copy = property.DeepClone();
                    string name = property.Name ?? "item";
                    WzImageProperty? clash = collection.FindByName(name);
                    WzImageProperty? overwritten = null;
                    int overwrittenIndex = -1;
                    if (clash != null)
                    {
                        if (request.Overwrite)
                        {
                            // Remembered before it goes, and put back by undo.
                            // Without this the overwrite destroyed the node
                            // outright: undo removed the copy and left nothing in
                            // its place, so the original was gone from the session
                            // with no way to retrieve it -- and the archive then
                            // reported clean, because the entry's pending count
                            // had returned to zero, so the close prompt did not
                            // even fire.
                            overwritten = clash;
                            overwrittenIndex = collection.IndexOf(clash);
                            collection.Remove(clash);
                        }
                        else
                        {
                            name = UniqueName(name, n => collection.FindByName(n) != null);
                        }
                    }
                    copy.Name = name;
                    if (request.Index is not null)
                        collection.Insert(Math.Clamp(insertAt++, 0, collection.Count), copy);
                    else
                        collection.Add(copy);
                    // Read back rather than assumed: Insert clamps, and the
                    // Overwrite branch above may have removed a node ahead of
                    // this one. Redo re-inserts here, so a wrong number would put
                    // the node somewhere the original edit never placed it.
                    int landedAt = collection.IndexOf(copy);
                    images.Mark(target);

                    // Recorded before the copy's own steps: undo runs the list in
                    // reverse, so this ordering removes the copy first and only
                    // then re-inserts what it displaced.
                    if (overwritten != null)
                    {
                        WzImageProperty restore = overwritten;
                        int restoreIndex = overwrittenIndex;
                        undoSteps.Add(() =>
                        {
                            if (restoreIndex >= 0 && restoreIndex <= collection.Count)
                                collection.Insert(restoreIndex, restore);
                            else
                                collection.Add(restore);
                            WzNodeFactory.MarkChanged(target);
                        });
                        redoSteps.Add(() => { collection.Remove(restore); WzNodeFactory.MarkChanged(target); });
                    }

                    undoSteps.Add(() => { collection.Remove(copy); WzNodeFactory.MarkChanged(target); });
                    // Insert, not Add: when the drop asked for a position, an
                    // appending redo would silently move the node to the end of
                    // its new parent. For a plain append landedAt is the last
                    // slot, so this is the same operation.
                    redoSteps.Add(() =>
                    {
                        collection.Insert(Math.Clamp(landedAt, 0, collection.Count), copy);
                        WzNodeFactory.MarkChanged(target);
                    });

                    if (request.Move)
                    {
                        WzObject sourceParent = _session.Resolve(WzPath.Parent(path)!);
                        WzPropertyCollection sourceCollection = WzNodeFactory.GetPropertyCollection(sourceParent)!;
                        int index = sourceCollection.IndexOf(property);
                        sourceCollection.Remove(property);
                        images.Mark(sourceParent);
                        undoSteps.Add(() =>
                        {
                            if (index >= 0 && index <= sourceCollection.Count)
                                sourceCollection.Insert(index, property);
                            else
                                sourceCollection.Add(property);
                            WzNodeFactory.MarkChanged(sourceParent);
                        });
                        redoSteps.Add(() => { sourceCollection.Remove(property); WzNodeFactory.MarkChanged(sourceParent); });
                    }

                    results.Add(_session.ToDto(copy, WzPath.Child(request.TargetPath, name)));
                }
                else if (node is WzImage image && target is WzDirectory targetDir)
                {
                    WzImage copy = image.DeepClone();
                    copy.Name = targetDir.GetImageByName(image.Name) != null && !request.Overwrite
                        ? UniqueImageName(image.Name, n => targetDir.GetImageByName(n) != null)
                        : image.Name;
                    copy.Changed = true;

                    WzImage? clash = targetDir.GetImageByName(copy.Name);
                    if (clash != null && request.Overwrite)
                    {
                        // See the property branch: without an undo step the
                        // overwritten image is destroyed permanently and the
                        // archive reports clean afterwards. Recorded before the
                        // copy's own step so the reverse replay removes the copy
                        // first.
                        WzImage restore = clash;
                        targetDir.RemoveImage(restore);
                        undoSteps.Add(() => targetDir.AddImage(restore));
                        redoSteps.Add(() => targetDir.RemoveImage(restore));
                    }

                    targetDir.AddImage(copy);
                    undoSteps.Add(() => targetDir.RemoveImage(copy));
                    redoSteps.Add(() => targetDir.AddImage(copy));

                    if (request.Move && _session.Resolve(WzPath.Parent(path)!) is WzDirectory sourceDir)
                    {
                        sourceDir.RemoveImage(image);
                        undoSteps.Add(() => sourceDir.AddImage(image));
                        redoSteps.Add(() => sourceDir.RemoveImage(image));
                    }
                    results.Add(_session.ToDto(copy, WzPath.Child(request.TargetPath, copy.Name)));
                }
                else
                {
                    throw new InvalidOperationException(
                        $"'{node.Name}' cannot be placed inside '{target.Name}'.");
                }

                // The source archive is only modified when the node LEAVES it.
                //
                // This marked it dirty either way, so copying a mob out of a
                // 721 MB Mob.wz left that archive claiming unsaved work with
                // zero changed images: the close prompt fires, and saying yes
                // rewrites a gigabyte to change nothing. `images.Mark` above
                // already draws this same distinction correctly -- only the file
                // flag did not.
                //
                // The resolution cache is dropped either way, because the
                // TARGET's shape changed and a cached path there could now name
                // a different node.
                if (request.Move)
                    MarkFileDirty(path);
                else
                    _session.InvalidateResolution();
            }

            _session.GetFileForPath(request.TargetPath).Dirty = true;

            if (results.Count > 0)
            {
                _undo.Record(new EditAction
                {
                    // Named, not counted, when there is one of them. This label
                    // is what the undo button's tooltip and the toast after a
                    // drag both say, and "Move 1 nodes" tells the user neither
                    // what moved nor where it went -- which is the whole question
                    // after an accidental drop.
                    Label = DescribeTransfer(request.Move, results, target.Name ?? "?"),
                    // A copy leaves the source untouched, so it must not appear
                    // here. EditAction.FileIds is derived from these paths and
                    // is what SyncDirty writes back on undo and redo -- with the
                    // source listed, undoing a copy re-dirtied an archive that
                    // had never been written to, which is the same false alarm
                    // the guard above removes, arriving one step later.
                    AffectedPaths = (request.Move
                            ? request.Paths.Select(p => WzPath.Parent(p) ?? p).Append(request.TargetPath)
                            : new[] { request.TargetPath }.AsEnumerable())
                        .Distinct().ToArray(),
                    Images = images,
                    Undo = () => { for (int i = undoSteps.Count - 1; i >= 0; i--) undoSteps[i](); },
                    Redo = () => { foreach (Action step in redoSteps) step(); },
                });
            }
            return results;
        }
    }

    private static string DescribeTransfer(bool move, List<NodeDto> results, string targetName)
    {
        string verb = move ? "Move" : "Copy";
        return results.Count == 1
            ? $"{verb} {results[0].Name} into {targetName}"
            : $"{verb} {results.Count} nodes into {targetName}";
    }

    /// <summary>Moves a property to a new index among its siblings.</summary>
    public void Reorder(string path, int newIndex)
    {
        lock (_session.Gate)
        {
            EnsureWritable(path);
            WzObject node = _session.Resolve(path);
            if (node is not WzImageProperty property)
                throw new InvalidOperationException("Only properties can be reordered.");

            WzObject parent = _session.Resolve(WzPath.Parent(path)!);
            WzPropertyCollection collection = WzNodeFactory.GetPropertyCollection(parent)
                ?? throw new InvalidOperationException("This node's parent does not hold an ordered list.");
            int oldIndex = collection.IndexOf(property);
            if (oldIndex < 0)
                throw new InvalidOperationException("Node is no longer in its parent.");

            newIndex = Math.Clamp(newIndex, 0, collection.Count - 1);
            if (newIndex == oldIndex)
                return;

            Move(collection, oldIndex, newIndex);
            ImageChangeLog images = new();
            images.Mark(parent);
            MarkFileDirty(path);

            _undo.Record(new EditAction
            {
                // The position, not just the name: "Reorder delay" is
                // indistinguishable from the three other times this session that
                // a node called 'delay' was nudged, and the undo tooltip is the
                // only place the user gets to check before pressing Ctrl+Z.
                // One-based, because the row numbers beside it are.
                Label = $"Move {property.Name} to position {newIndex + 1}",
                AffectedPaths = new[] { WzPath.Parent(path)! },
                Images = images,
                Undo = () => { Move(collection, newIndex, oldIndex); WzNodeFactory.MarkChanged(parent); },
                Redo = () => { Move(collection, oldIndex, newIndex); WzNodeFactory.MarkChanged(parent); },
            });
        }
    }

    private static void Move(WzPropertyCollection collection, int from, int to)
    {
        WzImageProperty item = collection[from];
        collection.RemoveAt(from);
        collection.Insert(Math.Clamp(to, 0, collection.Count), item);
    }

    #endregion

    #region Binary payloads

    /// <summary>Replaces a canvas's pixels, keeping its sub-properties intact.</summary>
    public NodeDto SetCanvasImage(string path, System.Drawing.Bitmap bitmap)
    {
        lock (_session.Gate)
        {
            EnsureWritable(path);
            if (_session.Resolve(path) is not WzCanvasProperty canvas)
                throw new InvalidOperationException("This node is not a canvas.");

            WzPngProperty? existing = canvas.PngProperty;

            // Snapshot the compressed bytes rather than the object: mutating the
            // existing WzPngProperty in place keeps whatever parent linkage
            // MapleLib set up when the canvas was parsed.
            byte[]? previousBytes = null;
            int previousWidth = 0, previousHeight = 0;
            WzPngFormat previousFormat = WzPngFormat.Format1;
            if (existing != null)
            {
                try
                {
                    previousBytes = existing.GetCompressedBytes(true);
                    previousWidth = existing.Width;
                    previousHeight = existing.Height;
                    previousFormat = existing.Format;
                }
                catch
                {
                    // Unreadable source pixels: the replace still works, but this
                    // particular change won't be undoable.
                    previousBytes = null;
                }
            }

            WzPngProperty target = existing ?? new WzPngProperty();

            // Encode into the format the canvas already had rather than letting
            // MapleLib re-detect one from the pixels. Its detection turns a
            // 16-aligned RGB565 image into the block-averaged Format517 and
            // cannot write BC7 at all, both of which destroy the sprite.
            string? formatWarning = null;
            if (existing != null && previousBytes != null)
            {
                MbPngCodec.EncodeResult encoded = MbPngCodec.Encode(bitmap, previousFormat);
                target.SetCompressedBytes(encoded.Compressed, bitmap.Width, bitmap.Height, encoded.Format);
                formatWarning = encoded.Warning;
            }
            else
            {
                // A brand new canvas has no format to preserve.
                target.SetValue(bitmap);
            }

            if (existing == null)
                canvas.PngProperty = target;

            ImageChangeLog images = new();
            images.Mark(canvas);
            MarkFileDirty(path);

            NodeDto dto = _session.ToDto(canvas, path);
            dto.Warning = JoinWarnings(
                RecordCanvasReplace(path, canvas, target, existing, previousBytes,
                    previousWidth, previousHeight, previousFormat, images),
                JoinWarnings(
                    // `previousBytes != null`, not `existing != null`: without the
                    // original blob previousFormat was never read off the canvas
                    // and still holds its Format1 default, so comparing against it
                    // reports a change from a format the canvas never had — which
                    // makes the honest format warnings untrustworthy too.
                    formatWarning ?? DescribeFormatChange(previousFormat, target.Format, previousBytes != null),
                    DescribeStaleHash(canvas)));
            return dto;
        }
    }

    /// <summary>
    /// Records the undo entry for a canvas replace, or says why there isn't one.
    /// Returns a warning to show the user, or null when the edit is undoable.
    ///
    /// The case that has to be named rather than papered over is a canvas whose
    /// old pixels could not be read: <c>GetCompressedBytes</c> throws on the
    /// zero-length blob an '_inlink' placeholder carries, and there is then
    /// nothing to put back.  This used to record an entry whose Undo body was an
    /// empty <c>if</c>.  <see cref="UndoService.Undo"/> saw no exception, popped
    /// it, and the endpoint answered <c>applied: "Replace image"</c> — so the
    /// user was told the change had been reverted while the new pixels were still
    /// on screen and still headed for the file.  Refusing to record, and saying
    /// so at the moment of the edit, is the version that can be acted on.
    ///
    /// A canvas that had no <c>PngProperty</c> at all is treated the same way,
    /// deliberately.  A faithful undo would have to put the absence back, and a
    /// canvas with a null PngProperty throws inside MapleLib's writer
    /// (<c>WzCanvasProperty.WriteValue</c> dereferences it) where the save
    /// preflight's null-conditional probe does not look.  Nothing in the editor
    /// creates that shape — <c>WzNodeFactory</c> seeds every new canvas with a
    /// 1x1 PNG — so declining is cheap, and re-introducing a state that fails
    /// mid-write is not.
    /// </summary>
    private string? RecordCanvasReplace(
        string path, WzCanvasProperty canvas, WzPngProperty target,
        WzPngProperty? existing, byte[]? previousBytes,
        int previousWidth, int previousHeight, WzPngFormat previousFormat, ImageChangeLog images)
    {
        if (previousBytes == null)
        {
            // No entry means nothing on the undo stack can carry this file back
            // to clean, and the change is real and unsaved — so say so to the
            // dirty model directly.
            _undo.SealFile(WzPath.FileId(path));
            return existing == null
                ? "This canvas held no pixels of its own, so adding them cannot be undone. " +
                  "Close the file without saving if you want it back as it was."
                : "The pixels this canvas held could not be read, so this replacement cannot be undone. " +
                  "Close the file without saving if you need them back.";
        }

        byte[] restoreBytes = previousBytes;
        byte[] newBytes = target.GetCompressedBytes(true);
        int newWidth = target.Width, newHeight = target.Height;
        WzPngFormat newFormat = target.Format;

        _undo.Record(new EditAction
        {
            Label = "Replace image",
            AffectedPaths = new[] { path },
            Images = images,
            Undo = () =>
            {
                target.SetCompressedBytes(restoreBytes, previousWidth, previousHeight, previousFormat);
                WzNodeFactory.MarkChanged(canvas);
            },
            Redo = () =>
            {
                target.SetCompressedBytes(newBytes, newWidth, newHeight, newFormat);
                WzNodeFactory.MarkChanged(canvas);
            },
        });
        return null;
    }

    /// <summary>
    /// Newer clients store a '_hash' beside a canvas that describes its pixels.
    /// Replacing the pixels leaves it describing the old ones.
    ///
    /// Nothing here recomputes it, and deliberately so: the value is a 64-hex
    /// digest whose exact input is not documented anywhere in MapleLib, and a
    /// confidently-wrong hash is worse than an obviously-stale one. Saying so is
    /// the honest option until someone establishes what it covers.
    /// </summary>
    private static string? DescribeStaleHash(WzCanvasProperty canvas)
    {
        WzImageProperty? hash = canvas.WzProperties?.FindByName("_hash");
        if (hash == null)
            return null;

        return "This canvas has a '_hash' field describing its old pixels, and it has not been " +
               "updated — nothing here knows how the client computes it. If the client rejects " +
               "the sprite, delete '_hash' and try again.";
    }

    private static string? JoinWarnings(string? first, string? second)
    {
        if (string.IsNullOrEmpty(first))
            return string.IsNullOrEmpty(second) ? null : second;
        return string.IsNullOrEmpty(second) ? first : first + Environment.NewLine + Environment.NewLine + second;
    }

    /// <summary>
    /// MapleLib re-detects the PNG surface format from the replacement pixels
    /// rather than keeping the original, and it has no encoder for BC7 at all.
    /// The dangerous case is Format513 -> Format517: both are 16-bit RGB565, but
    /// 517 stores a single colour per 16x16 block, so a large background silently
    /// becomes a mosaic. Rather than hide that, say what happened.
    /// </summary>
    private static string? DescribeFormatChange(WzPngFormat before, WzPngFormat after, bool hadOriginal)
    {
        if (!hadOriginal || before == after)
            return null;

        string detail = after switch
        {
            WzPngFormat.Format517 =>
                "Format 517 stores one colour per 16x16 block, so fine detail will be lost. " +
                "Resize the image so its width or height is not a multiple of 16 to avoid this.",
            WzPngFormat.Format2 when before == WzPngFormat.Format4098 =>
                "MapleLib can read BC7 but cannot write it, so this canvas was stored uncompressed. " +
                "The file will be noticeably larger.",
            _ => "Re-encoding changes the stored bytes and can lose a little colour precision.",
        };

        return $"The image format changed from {before} to {after}. {detail}";
    }

    #endregion

    #region Undo plumbing

    public EditAction? Undo()
    {
        lock (_session.Gate)
        {
            EditAction? action = null;
            try
            {
                action = _undo.Undo();
                if (action != null)
                    SyncDirty(action.FileIds);
                return action;
            }
            finally { InvalidateFor(action); }
        }
    }

    public EditAction? Redo()
    {
        lock (_session.Gate)
        {
            EditAction? action = null;
            try
            {
                action = _undo.Redo();
                if (action != null)
                    SyncDirty(action.FileIds);
                return action;
            }
            finally { InvalidateFor(action); }
        }
    }

    /// <summary>
    /// Invalidates as much as the replayed action actually justifies.
    ///
    /// The general case has to be the sledgehammer: an undo re-inserts removed
    /// nodes at their old index, which can put a duplicate name back in front of
    /// a cached one, and the resolution cache verifies parentage and name but
    /// cannot see that it is now the *second* node of that name.
    ///
    /// A value-only action cannot do that — nothing is inserted, so no name can
    /// come to shadow another — and saying so is worth a lot: pressing Ctrl+Z
    /// after changing one number used to rebuild every browse list in the app,
    /// five seconds for the Mobs grid alone.
    ///
    /// Anything unrecognised, including a null action, takes the sledgehammer.
    /// </summary>
    private void InvalidateFor(EditAction? action)
    {
        if (action is { ValueOnly: true, AffectedPaths.Length: > 0 })
        {
            foreach (string path in action.AffectedPaths)
                _session.NoteValueChanged(path);
            return;
        }
        _session.InvalidateResolution();
    }

    /// <summary>
    /// Puts each affected archive's dirty flag back in step with the undo stack.
    ///
    /// This is the half of the model that lets a file return to clean.
    /// <see cref="MarkFileDirty"/> only ever sets the flag, so before this an
    /// archive stayed dirty forever once anything had been edited, however much
    /// of it was undone.  That is not a stray asterisk: the flag decides whether
    /// the close prompt fires, whether a relaunch is allowed to kill this
    /// process, and whether a save rewrites byte-identical images through every
    /// lossy re-serialisation path.
    ///
    /// The answer comes from <see cref="UndoService"/> rather than from counting
    /// here, because only it knows the parts that are not on the stack: entries
    /// evicted past the 250 cap, entries dropped by a save, and edits that were
    /// deliberately not recorded.  All of those leave the file dirty.
    /// </summary>
    private void SyncDirty(IEnumerable<string> fileIds)
    {
        foreach (string fileId in fileIds)
        {
            try { _session.GetFile(fileId).Dirty = _undo.HasUnsavedEdits(fileId); }
            catch (KeyNotFoundException) { /* closed since the edit was recorded */ }
        }
    }

    #endregion

    /// <summary>
    /// Flags the owning archive as changed — and drops the resolution cache.
    ///
    /// Every mutation in this service calls this, which is exactly the property
    /// wanted: the cache is fail-safe (a stale entry is re-verified against the
    /// live tree and evicted), so the only thing invalidation has to catch is a
    /// sibling coming to *shadow* a cached name, and every way that can happen
    /// passes through here. Over-invalidating costs one re-walk of index probes;
    /// under-invalidating would land an edit on the wrong node.
    /// </summary>
    private void MarkFileDirty(string path)
    {
        _session.InvalidateResolution();
        try { _session.GetFileForPath(path).Dirty = true; }
        catch (KeyNotFoundException) { /* file closed mid-edit; nothing to flag */ }
    }

    /// <summary>
    /// The same, for an edit that changed a value and nothing else.
    ///
    /// Split out from <see cref="MarkFileDirty"/> because the two statements are
    /// genuinely different: "the tree moved" and "this one number is new". Only
    /// <c>SetValue</c> and <c>SetValueMany</c> may use this, and only because
    /// <c>WzNodeFactory.SetValue</c> mutates the property in place — every other
    /// mutation in this service can change which node a path names, and must
    /// keep saying so.
    ///
    /// The saving is not small. Before it, one keystroke committed anywhere in
    /// the app discarded every browse list in every section, and the next visit
    /// to Mobs re-parsed 2,742 images.
    /// </summary>
    private void MarkValueChanged(string path)
    {
        _session.NoteValueChanged(path);
        try { _session.GetFileForPath(path).Dirty = true; }
        catch (KeyNotFoundException) { /* file closed mid-edit; nothing to flag */ }
    }

    /// <summary>
    /// Refuses the edit when the owning archive is open for reference only.
    ///
    /// Called at the *top* of every mutation, not at the bottom next to
    /// <see cref="MarkFileDirty"/>: the point is that nothing was changed, and a
    /// check that runs after the tree has already been touched would leave the
    /// archive modified and merely un-flagged.
    /// </summary>
    private void EnsureWritable(string path)
    {
        // A merged family is a view over several archives, so "this node" is not
        // a thing that can be written -- the writable node is the one inside
        // whichever physical file holds it.  Refused here with the reason rather
        // than left to Resolve, which would fail a moment later with "no open
        // file with id 'g1'": true, and no help at all to somebody who was
        // looking at a folder in the tree.
        if (_families?.IsFamilyPath(path) == true)
        {
            throw new InvalidOperationException(
                "That folder is the merged view of several archives, not a node in one of them, so nothing " +
                "was changed. Open the folder and act on the item itself -- every item in a merged tree " +
                "belongs to exactly one file, and the row says which.");
        }

        OpenFile file;
        try { file = _session.GetFileForPath(path); }
        catch (KeyNotFoundException) { return; }

        if (file.ReadOnly)
        {
            throw new InvalidOperationException(
                $"'{file.Name}' is open for reference only, so nothing was changed. " +
                "Unlock it in the Files panel if you meant to edit it.");
        }
    }

    private static string UniqueName(string baseName, Func<string, bool> taken)
    {
        string candidate = baseName + " copy";
        int counter = 2;
        while (taken(candidate))
            candidate = $"{baseName} copy {counter++}";
        return candidate;
    }

    private static string UniqueImageName(string baseName, Func<string, bool> taken)
    {
        string stem = baseName.EndsWith(".img", StringComparison.OrdinalIgnoreCase)
            ? baseName[..^4]
            : baseName;
        string candidate = stem + " copy.img";
        int counter = 2;
        while (taken(candidate))
            candidate = $"{stem} copy {counter++}.img";
        return candidate;
    }
}
