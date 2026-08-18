using System.Collections.Generic;

namespace MapleBench.Services.MapEditor;

/// <summary>
/// What the map editor can do right now, and out of which archives.
/// </summary>
public sealed class MapEditCapabilitiesDto
{
    /// <summary>Whether any open archive holds map images.</summary>
    public bool Available { get; set; }

    /// <summary>The archives the map list is drawn from.</summary>
    public List<string> MapArchives { get; set; } = new();

    /// <summary>Whether String.wz names are available for the picker.</summary>
    public bool Names { get; set; }

    /// <summary>Whether MapHelper.img portal icons resolve.</summary>
    public bool PortalIcons { get; set; }

    /// <summary>Whether the open session can resolve NPC spawn art.</summary>
    public bool NpcSprites { get; set; }

    /// <summary>Whether the open session can resolve mob spawn art.</summary>
    public bool MobSprites { get; set; }

    /// <summary>How many map images the picker can offer.</summary>
    public int MapCount { get; set; }
}

public sealed class MapListRowDto
{
    public int Id { get; set; }

    /// <summary>
    /// The map's name from String.wz — the measured source of names.
    /// <c>info/mapName</c> exists on 20 maps and disagrees with String.wz on
    /// every one of them, so it is never consulted here.
    /// </summary>
    public string? Name { get; set; }

    /// <summary>Session path of the image, ready for /api/mapedit/open.</summary>
    public string Path { get; set; } = "";

    /// <summary>Which archive the image is read from.</summary>
    public string Source { get; set; } = "";
}

public sealed class MapListDto
{
    public List<MapListRowDto> Maps { get; set; } = new();
    public int Total { get; set; }
    public bool Truncated { get; set; }
}

/// <summary>
/// One whole map, shaped for the renderer. Every entry carries its address —
/// the index path from the document root — so an edit can name the exact node
/// it means even when sibling names repeat (69 maps carry duplicate foothold
/// ids, and WZ has no uniqueness rule).
/// </summary>
public sealed class MapDocDto
{
    public string Path { get; set; } = "";
    public string ImageName { get; set; } = "";
    public int? MapId { get; set; }
    public string? Name { get; set; }

    /// <summary><c>info/link</c>, when this map is a link stub. A stub is not an
    /// empty map — 87 carry portals, 5 carry life — so the document is served in
    /// full alongside this.</summary>
    public string? Link { get; set; }
    public bool HasGeometry { get; set; }

    public string? Bgm { get; set; }
    public string? MapMark { get; set; }

    /// <summary>VRLeft/VRTop/VRRight/VRBottom, or the computed fallback.</summary>
    public MapBoundsDto? Bounds { get; set; }

    /// <summary>True when the map carries no VR and the bounds were computed
    /// from its geometry. 1,151 maps need this.</summary>
    public bool BoundsComputed { get; set; }

    public MapMiniMapDto? MiniMap { get; set; }

    public List<MapLayerDto> Layers { get; set; } = new();
    public List<MapBackDto> Backs { get; set; } = new();
    public List<MapFootholdDto> Footholds { get; set; } = new();
    public List<MapLadderDto> Ladders { get; set; } = new();
    public List<MapPortalDto> Portals { get; set; } = new();
    public List<MapLifeDto> Life { get; set; } = new();
    public List<MapReactorDto> Reactors { get; set; } = new();

    /// <summary>Every rectangle-shaped zone entry: <c>area</c>, <c>ToolTip</c>
    /// and the ten <see cref="MapleBench.Services.MapModel.MapNodeKinds.RectKinds"/>,
    /// each with x1/y1/x2/y2. Visible, selectable, resizable.</summary>
    public List<MapRectDto> Rects { get; set; } = new();

    /// <summary>
    /// The top-level nodes no typed surface names, carried verbatim. Shown so
    /// the tool states what it preserves rather than what it understands.
    /// </summary>
    public List<string> UnmodelledNames { get; set; } = new();
    public int UnmodelledCount { get; set; }

    /// <summary>
    /// Every distinct piece of art this map draws with, resolved to a session
    /// path the /api/canvas endpoint serves. Keyed by the reference string the
    /// entries carry in their <c>art</c> field.
    /// </summary>
    public Dictionary<string, MapArtDto> Art { get; set; } = new();

    public int UndoDepth { get; set; }
    public int RedoDepth { get; set; }
    public bool Dirty { get; set; }
}

public sealed class MapBoundsDto
{
    public int Left { get; set; }
    public int Top { get; set; }
    public int Right { get; set; }
    public int Bottom { get; set; }
}

public sealed class MapMiniMapDto
{
    public long? Width { get; set; }
    public long? Height { get; set; }
    public long? CenterX { get; set; }
    public long? CenterY { get; set; }

    /// <summary>Session path of the minimap picture, when it has one.</summary>
    public string? CanvasPath { get; set; }
}

public sealed class MapLayerDto
{
    /// <summary>The layer's numeric name. Read from the image — 749080500.img
    /// ships a layer 8, so this is never assumed to be 0-7.</summary>
    public int Index { get; set; }
    public int[] Addr { get; set; } = System.Array.Empty<int>();

    /// <summary>The tile set for this whole layer. Per-layer, not per-tile.</summary>
    public string? TS { get; set; }

    /// <summary>1 or 2; absent is not 1 and stays absent.</summary>
    public long? TSMag { get; set; }

    public List<MapTileDto> Tiles { get; set; } = new();
    public List<MapObjDto> Objs { get; set; } = new();

    /// <summary>Backgrounds nested inside this layer — 954090400.img has 26.
    /// Rendered with the root list; counted here so the layer panel can say so.</summary>
    public int LayerBackCount { get; set; }
}

public sealed class MapTileDto
{
    public int[] Addr { get; set; } = System.Array.Empty<int>();
    /// <summary>
    /// The tile entry's numeric WZ node name. This is the visual tie-breaker
    /// after the tile art's own z value; zM groups the tile with a platform and
    /// must not be used to reshuffle equal-depth art.
    /// </summary>
    public long Z { get; set; }
    public long X { get; set; }
    public long Y { get; set; }
    public string? U { get; set; }
    public long No { get; set; }
    public long ZM { get; set; }
    /// <summary>Key into <see cref="MapDocDto.Art"/>.</summary>
    public string? Art { get; set; }
}

public sealed class MapObjDto
{
    public int[] Addr { get; set; } = System.Array.Empty<int>();
    public string? OS { get; set; }
    public string? L0 { get; set; }
    public string? L1 { get; set; }
    public string? L2 { get; set; }
    /// <summary>The fourth path segment two maps carry.</summary>
    public string? L3 { get; set; }
    public long X { get; set; }
    public long Y { get; set; }
    public long Z { get; set; }
    public long ZM { get; set; }
    public long F { get; set; }
    /// <summary>True when the entry carries <c>spineAni</c> — Spine-rigged art
    /// this viewer does not animate. Drawn static, badged, never faked.</summary>
    public bool Spine { get; set; }
    public string? Art { get; set; }
}

public sealed class MapBackDto
{
    public int[] Addr { get; set; } = System.Array.Empty<int>();
    public string? BS { get; set; }
    public long No { get; set; }
    public long Ani { get; set; }
    public long X { get; set; }
    public long Y { get; set; }
    public long Cx { get; set; }
    public long Cy { get; set; }
    public long Rx { get; set; }
    public long Ry { get; set; }
    public long Type { get; set; }
    public long Front { get; set; }
    public long F { get; set; }
    public long A { get; set; }
    /// <summary>True for the 26 entries nested inside a layer on 954090400.img.</summary>
    public bool InLayer { get; set; }
    /// <summary>True for <c>ani = 2</c> or a <c>spineAni</c> child — Spine-rigged
    /// backgrounds stay static in this viewer, with a badge saying so.</summary>
    public bool Spine { get; set; }
    public string? Art { get; set; }
}

public sealed class MapFootholdDto
{
    public int[] Addr { get; set; } = System.Array.Empty<int>();
    /// <summary>The foothold's id — its node name. Not unique on 69 maps.</summary>
    public string Id { get; set; } = "";
    public string Layer { get; set; } = "";
    public string Group { get; set; } = "";
    public long X1 { get; set; }
    public long Y1 { get; set; }
    public long X2 { get; set; }
    public long Y2 { get; set; }
    public long Prev { get; set; }
    public long Next { get; set; }
}

public sealed class MapLadderDto
{
    public int[] Addr { get; set; } = System.Array.Empty<int>();
    public long X { get; set; }
    public long Y1 { get; set; }
    public long Y2 { get; set; }
    /// <summary>1 ladder, 0 rope.</summary>
    public long L { get; set; }
}

public sealed class MapPortalDto
{
    public int[] Addr { get; set; } = System.Array.Empty<int>();
    public string? Pn { get; set; }
    public long Pt { get; set; }
    public long X { get; set; }
    public long Y { get; set; }
    public long Tm { get; set; }
    public string? Tn { get; set; }
    public string? Script { get; set; }
}

public sealed class MapLifeDto
{
    public int[] Addr { get; set; } = System.Array.Empty<int>();
    public string? Id { get; set; }
    /// <summary><c>m</c> mob, <c>n</c> NPC.</summary>
    public string? Type { get; set; }
    public long X { get; set; }
    public long Y { get; set; }
    public long Cy { get; set; }
    public long Fh { get; set; }
    public long F { get; set; }
    public long Hide { get; set; }
    public long MobTime { get; set; }
    /// <summary>The category this spawn was listed under on the 25
    /// <c>life/isCategory</c> maps, or null for the ordinary shape.</summary>
    public string? Category { get; set; }
    /// <summary>Name from String.wz, when available.</summary>
    public string? Name { get; set; }
    /// <summary>Resolved stand/idle art from Npc.wz or Mob.wz. Null only when
    /// the life entry has no usable id/type; an unavailable archive is recorded
    /// as a missing entry in <see cref="MapDocDto.Art"/> so the UI can explain it.</summary>
    public string? Art { get; set; }
}

public sealed class MapReactorDto
{
    public int[] Addr { get; set; } = System.Array.Empty<int>();
    public string? Id { get; set; }
    public long X { get; set; }
    public long Y { get; set; }
    public long F { get; set; }
    public string? ReactorName { get; set; }
}

/// <summary>One rectangle-shaped zone entry (area, ToolTip, BuffZone, swimArea, ...).</summary>
public sealed class MapRectDto
{
    public int[] Addr { get; set; } = System.Array.Empty<int>();
    /// <summary>The top-level node name this entry came from — its kind.</summary>
    public string Kind { get; set; } = "";
    /// <summary>The entry's own node name (numbered, or named under area).</summary>
    public string Name { get; set; } = "";
    public long X1 { get; set; }
    public long Y1 { get; set; }
    public long X2 { get; set; }
    public long Y2 { get; set; }
}

/// <summary>One resolved piece of art.</summary>
public sealed class MapArtDto
{
    /// <summary>Session path for /api/canvas, or null when the reference does
    /// not resolve in the open session — reported, never guessed at.</summary>
    public string? Path { get; set; }
    public int W { get; set; }
    public int H { get; set; }
    /// <summary>Origin offset: draw at (x - ox, y - oy). For an animation these
    /// are the shared anchor and W/H the composed canvas that fits every frame —
    /// the same composition <c>AnimationService.PlaceAtSharedOrigin</c> makes.</summary>
    public int Ox { get; set; }
    public int Oy { get; set; }
    /// <summary>The art's own z, used for in-layer ordering of tiles.</summary>
    public int Z { get; set; }
    public bool Missing { get; set; }

    /// <summary>The animation frames, when the art is a numbered-frame list
    /// (2+ frames). Null for still art. Frames are pre-placed at the shared
    /// origin: draw frame i at (x - ox + frames[i].dx, y - oy + frames[i].dy).</summary>
    public List<MapArtFrameDto>? Frames { get; set; }
    /// <summary>Sum of the frame delays, ms.</summary>
    public int TotalMs { get; set; }
}

/// <summary>One frame of an animated map art, placed inside the composed
/// canvas by <c>AnimationService.PlaceAtSharedOrigin</c> — the one composition
/// rule every player and exporter in this app shares.</summary>
public sealed class MapArtFrameDto
{
    public string Path { get; set; } = "";
    public int W { get; set; }
    public int H { get; set; }
    /// <summary>Offset inside the composed canvas (anchor - this frame's origin).</summary>
    public int Dx { get; set; }
    public int Dy { get; set; }
    /// <summary>Milliseconds to hold this frame; missing/zero delays are 100 ms,
    /// the client's own fallback.</summary>
    public int Delay { get; set; }
}

public sealed class MapPortalIconDto
{
    /// <summary>The <c>pt</c> value this icon is for — the child's ORDER in
    /// MapHelper.img/portal/editor, which is measured to not match any
    /// alphabetical or documented order.</summary>
    public int Pt { get; set; }
    public string Name { get; set; } = "";
    public string? Path { get; set; }
    public int W { get; set; }
    public int H { get; set; }
    public int Ox { get; set; }
    public int Oy { get; set; }
}

#region Requests

public sealed class MapOpenRequest
{
    /// <summary>Session path of the map image.</summary>
    public string Path { get; set; } = "";
}

public sealed class MapEditRequest
{
    public string Path { get; set; } = "";
    public MapEditOp Op { get; set; } = new();
}

/// <summary>
/// One edit. <c>Kind</c> decides which fields matter:
/// <list type="bullet">
/// <item><c>setValue</c> — <c>Addr</c> + <c>Value</c>: writes a scalar through
/// the type-preserving setters. A String stays a String, a Short a Short.</item>
/// <item><c>move</c> — <c>Addr</c> + <c>X</c>/<c>Y</c>: writes an entry's
/// <c>x</c>/<c>y</c> children through SetNumber.</item>
/// <item><c>moveFoothold</c> — <c>Addr</c> + <c>Vertex</c> (1 or 2) +
/// <c>X</c>/<c>Y</c>: moves one endpoint and keeps coincident linked endpoints
/// coincident. Never touches prev/next.</item>
/// <item><c>moveLife</c> — <c>Addr</c> + <c>X</c>/<c>Y</c> (the drop point):
/// re-anchors the spawn to the foothold under the drop point, recomputing
/// <c>fh</c> and <c>cy</c> from that foothold's own geometry — the same rule
/// placement uses — and keeps <c>x</c>/<c>y</c>/<c>rx0</c>/<c>rx1</c>
/// consistent. Refused when nothing lies below.</item>
/// <item><c>delete</c> — <c>Addr</c>: removes the node, undoably.</item>
/// </list>
/// </summary>
public sealed class MapEditOp
{
    public string Kind { get; set; } = "";
    public int[] Addr { get; set; } = System.Array.Empty<int>();
    public string? Value { get; set; }
    public long? X { get; set; }
    public long? Y { get; set; }
    public int? Vertex { get; set; }

    /// <summary>Batch delta for moveMany / duplicate — world-pixel offsets.</summary>
    public long? Dx { get; set; }
    public long? Dy { get; set; }

    /// <summary>Field name for setField — matched ordinally, never trimmed.</summary>
    public string? Name { get; set; }

    /// <summary>Rect corners for setRect.</summary>
    public long? X1 { get; set; }
    public long? Y1 { get; set; }
    public long? X2 { get; set; }
    public long? Y2 { get; set; }

    /// <summary>The entries a batch op addresses, each with the kind the
    /// client selected it as — the kind decides the move semantics (a life
    /// spawn re-anchors, a foothold drags its linked endpoints along).</summary>
    public List<MapEditTargetDto>? Items { get; set; }
}

/// <summary>One entry of a batch edit.</summary>
public sealed class MapEditTargetDto
{
    public int[] Addr { get; set; } = System.Array.Empty<int>();
    public string Kind { get; set; } = "";
}

public sealed class MapDocRequest
{
    public string Path { get; set; } = "";
}

#endregion

#region Results

public sealed class MapEditResultDto
{
    /// <summary>True when addresses may have shifted and the client must
    /// re-fetch the document rather than patch its local copy.</summary>
    public bool Structural { get; set; }

    /// <summary>The nodes this op changed, with their new positions — a
    /// foothold vertex move can legally move several footholds' endpoints.</summary>
    public List<MapMovedDto> Moved { get; set; } = new();

    /// <summary>Things that happened alongside the edit and must not be silent
    /// — "re-anchored to foothold 14; cy recomputed as 275".</summary>
    public List<string> Notes { get; set; } = new();

    /// <summary>Addresses of nodes a structural op created (an inserted
    /// vertex's new foothold, duplicates), so the client can re-select them
    /// after its re-fetch.</summary>
    public List<int[]> Placed { get; set; } = new();

    public int UndoDepth { get; set; }
    public int RedoDepth { get; set; }
    public bool Dirty { get; set; }
}

/// <summary>The undo and redo stacks by label, top first — the history panel.</summary>
public sealed class MapHistoryDto
{
    public List<string> Undo { get; set; } = new();
    public List<string> Redo { get; set; } = new();
}

public sealed class MapMovedDto
{
    public int[] Addr { get; set; } = System.Array.Empty<int>();
    public long X1 { get; set; }
    public long Y1 { get; set; }
    public long X2 { get; set; }
    public long Y2 { get; set; }
}

public sealed class MapUndoResultDto
{
    public string? Applied { get; set; }
    public int UndoDepth { get; set; }
    public int RedoDepth { get; set; }
    public bool Dirty { get; set; }
}

public sealed class MapSaveResultDto
{
    public string SavedTo { get; set; } = "";
    public string? BackupPath { get; set; }
    public long ArchiveBytes { get; set; }
    public double Seconds { get; set; }

    /// <summary>The write was read back from the saved archive and its bytes
    /// are exactly the model's bytes — the same claim MapRoundTrip makes.</summary>
    public bool Verified { get; set; }

    /// <summary>Structural differences between the saved image and the model,
    /// when verification failed. Empty on success.</summary>
    public List<string> Differences { get; set; } = new();

    /// <summary>How many top-level nodes outside the editor's vocabulary were
    /// carried through unchanged.</summary>
    public int UnmodelledCarried { get; set; }

    /// <summary>True when the document's undo/redo history survived the save —
    /// the saved file is the new clean baseline, and undoing past it makes the
    /// document dirty again. False only when a carried payload could not be
    /// pinned in memory; <see cref="HistoryNote"/> then says which and why.</summary>
    public bool HistoryKept { get; set; }
    public string? HistoryNote { get; set; }
}

/// <summary>The map editor's unsaved work, for the topbar dirty chip — the
/// documents live here, not in the session tree, so the session's own change
/// summary cannot see them.</summary>
public sealed class MapEditChangesDto
{
    public List<MapEditChangeRowDto> Docs { get; set; } = new();
    /// <summary>Total edits (undo depth) across dirty documents.</summary>
    public int EditCount { get; set; }
    public int DirtyDocs { get; set; }
}

public sealed class MapEditChangeRowDto
{
    public string Path { get; set; } = "";
    public string ImageName { get; set; } = "";
    public string? Name { get; set; }
    public int UndoDepth { get; set; }
    public bool Dirty { get; set; }
}

/// <summary>The raw view of one node, for the inspector.</summary>
public sealed class MapNodeDto
{
    public int[] Addr { get; set; } = System.Array.Empty<int>();
    /// <summary>The name exactly as stored — a trailing space is a different
    /// key and is neither trimmed nor folded anywhere in this editor.</summary>
    public string Name { get; set; } = "";
    public string Type { get; set; } = "";
    public string? Value { get; set; }
    public bool HasChildren { get; set; }
    public int ChildCount { get; set; }
    public List<MapNodeDto> Children { get; set; } = new();
}

#endregion

#region Palette

/// <summary>One placeable piece of art out of a Tile/Obj/Back set.</summary>
public sealed class MapPaletteEntryDto
{
    /// <summary>Tile variant — one of the 11 measured values (bsc, enH0, …).</summary>
    public string? U { get; set; }
    public long No { get; set; }
    public string? L0 { get; set; }
    public string? L1 { get; set; }
    public string? L2 { get; set; }
    /// <summary>The fourth object path segment two shipping maps use.</summary>
    public string? L3 { get; set; }
    /// <summary>For backgrounds: 0 = still (back/), 1 = animated (ani/),
    /// 2 = spine (spine/).</summary>
    public long Ani { get; set; }

    /// <summary>Session path of the drawable node, for /api/mapedit/thumb.</summary>
    public string? ThumbPath { get; set; }
    public int W { get; set; }
    public int H { get; set; }
    public int Ox { get; set; }
    public int Oy { get; set; }
    /// <summary>Frame count when the leaf is an animation (1 = still).</summary>
    public int Frames { get; set; }

    /// <summary>Whether the art carries its own foothold geometry — the
    /// measured mechanism auto-generated footholds are built from.</summary>
    public bool HasFoothold { get; set; }
}

/// <summary>
/// One palette set row, picture-first: the representative thumbnail is what the
/// set looks like at rest, so the set list is a grid of pictures rather than a
/// list of names. Null <see cref="ThumbPath"/> is honest — no drawable art
/// resolved — and the client renders that as a visible state, never silently.
/// </summary>
public sealed class MapPaletteSetDto
{
    public string Name { get; set; } = "";
    public string Path { get; set; } = "";
    /// <summary>The physical archive the set came out of — "Map2.wz".</summary>
    public string Source { get; set; } = "";
    /// <summary>Session path of a representative drawable (the set's first
    /// entry), for /api/mapedit/thumb. Null when nothing in the set decodes.</summary>
    public string? ThumbPath { get; set; }
}

public sealed class MapPaletteEntriesDto
{
    public string Kind { get; set; } = "";
    public string Set { get; set; } = "";
    public string Path { get; set; } = "";
    public List<MapPaletteEntryDto> Entries { get; set; } = new();
    public int Total { get; set; }
    public bool Truncated { get; set; }
}

/// <summary>A mob/NPC row for the life palette, named from String.wz.</summary>
public sealed class MapLifeRowDto
{
    public int Id { get; set; }
    /// <summary><c>m</c> or <c>n</c> — the value life/&lt;i&gt;/type stores.</summary>
    public string Type { get; set; } = "";
    public string? Name { get; set; }
    /// <summary>Session path of a stand frame, when Mob.wz / Npc.wz is open.
    /// Null is honest: the picker still works by name and id.</summary>
    public string? IconPath { get; set; }
}

public sealed class MapLifePaletteDto
{
    public bool NamesAvailable { get; set; }
    public bool IconsAvailable { get; set; }
    public List<MapLifeRowDto> Rows { get; set; } = new();
    public bool Truncated { get; set; }
}

public sealed class MapReactorRowDto
{
    public string Id { get; set; } = "";
    public string? IconPath { get; set; }
}

public sealed class MapReactorPaletteDto
{
    public bool Available { get; set; }
    /// <summary>Why the reactor palette is empty, when it is — stated rather
    /// than shown as a blank grid.</summary>
    public string? Reason { get; set; }
    public List<MapReactorRowDto> Rows { get; set; } = new();
    public bool Truncated { get; set; }
}

#endregion

#region Placement

/// <summary>
/// One placement. <c>Kind</c> decides which fields matter: tile (layer, set, u,
/// no), obj (layer, set, l0-l3), back (set, no, ani, backType, front), portal
/// (pn, pt, tm, tn), life (lifeType, id, fh optional), reactor (id).
/// </summary>
public sealed class MapPlaceRequest
{
    public string Path { get; set; } = "";
    public string Kind { get; set; } = "";
    public long X { get; set; }
    public long Y { get; set; }

    /// <summary>Target layer number for tile/obj.</summary>
    public int? Layer { get; set; }
    /// <summary>tS / oS / bS depending on kind.</summary>
    public string? Set { get; set; }
    public string? U { get; set; }
    public long? No { get; set; }
    public string? L0 { get; set; }
    public string? L1 { get; set; }
    public string? L2 { get; set; }
    public string? L3 { get; set; }
    public bool Flip { get; set; }

    /// <summary>For tiles: permission to WRITE the layer's tS when the layer has
    /// no tiles yet (never silently — the note says it happened). A layer that
    /// already has tiles under a different tS is refused regardless; changing it
    /// re-skins them and goes through set-layer-ts with its own confirmation.</summary>
    public bool AdoptLayerTs { get; set; }

    public long? Ani { get; set; }
    public long? BackType { get; set; }
    public long? Front { get; set; }

    public string? Pn { get; set; }
    public long? Pt { get; set; }
    public long? Tm { get; set; }
    public string? Tn { get; set; }
    public string? Script { get; set; }

    /// <summary><c>m</c> or <c>n</c>.</summary>
    public string? LifeType { get; set; }
    /// <summary>Life/reactor id — written as String, the measured type.</summary>
    public string? Id { get; set; }
    /// <summary>Foothold id to anchor life to; when absent the foothold below
    /// the drop point is found and cy computed from it.</summary>
    public long? Fh { get; set; }
    public long? MobTime { get; set; }

    /// <summary>For <c>kind = "ladderRope"</c>: the bottom y (<c>Y</c> is the
    /// top), 1 = ladder / 0 = rope, and <c>uf</c> (1 = climbable off the top).
    /// <c>Layer</c> becomes <c>page</c>. The written child order is the shape
    /// every sampled shipping entry uses: l, uf, x, y1, y2, page — all Int.</summary>
    public long? Y2 { get; set; }
    public long? L { get; set; }
    public long? Uf { get; set; }
}

public sealed class MapPlaceResultDto
{
    /// <summary>Address of the node that was created.</summary>
    public int[] Placed { get; set; } = System.Array.Empty<int>();
    public bool Structural { get; set; } = true;
    /// <summary>Things that happened alongside the write and must not be silent
    /// — "layer 3 had no tS; it now carries grassySoil".</summary>
    public List<string> Notes { get; set; } = new();
    public int UndoDepth { get; set; }
    public int RedoDepth { get; set; }
    public bool Dirty { get; set; }
}

public sealed class MapPointDto
{
    public long X { get; set; }
    public long Y { get; set; }
}

public sealed class MapFootholdChainRequest
{
    public string Path { get; set; } = "";
    /// <summary>The layer number the chain belongs to — footholds are
    /// layer-scoped (chains cross layers 5 times in the whole client).</summary>
    public int Layer { get; set; }
    public List<MapPointDto> Points { get; set; } = new();
}

public sealed class MapAutoFootholdRequest
{
    public string Path { get; set; } = "";
    /// <summary>Address of a placed tile or object whose art carries foothold
    /// geometry.</summary>
    public int[] Addr { get; set; } = System.Array.Empty<int>();
}

public sealed class MapLayerTsRequest
{
    public string Path { get; set; } = "";
    public int Layer { get; set; }
    public string Ts { get; set; } = "";
    /// <summary>Required when the layer already has tiles: changing tS re-skins
    /// every one of them. The consequence is stated BEFORE the write.</summary>
    public bool ConfirmReskin { get; set; }
}

#endregion

#region Minimap

public sealed class MapMinimapPlanDto
{
    public bool HasMiniMap { get; set; }
    public bool HasCanvas { get; set; }

    /// <summary>True when this map's minimap canvas is itself an _outlink —
    /// regenerating replaces the link with this map's own picture.</summary>
    public bool CanvasIsLink { get; set; }
    public string? LinkTarget { get; set; }

    /// <summary>Maps whose miniMap/canvas _outlinks INTO this map's canvas.
    /// Overwriting it changes what every one of them draws.</summary>
    public List<string> Sharers { get; set; } = new();
    public int SharerCount { get; set; }
    /// <summary>False when the scan could not run (no Map archives open).</summary>
    public bool SharersScanned { get; set; }

    /// <summary>What a regeneration would write.</summary>
    public long Width { get; set; }
    public long Height { get; set; }
    public long CenterX { get; set; }
    public long CenterY { get; set; }
    public int CanvasW { get; set; }
    public int CanvasH { get; set; }
    public int Mag { get; set; }
}

public sealed class MapMinimapRegenRequest
{
    public string Path { get; set; } = "";
    /// <summary>Acknowledges replacing a canvas that is an _outlink.</summary>
    public bool ConfirmDetachLink { get; set; }
    /// <summary>Acknowledges overwriting a canvas other maps share.</summary>
    public bool ConfirmSharedOverwrite { get; set; }
}

public sealed class MapMinimapResultDto
{
    public bool Structural { get; set; } = true;
    public int CanvasW { get; set; }
    public int CanvasH { get; set; }
    public long Width { get; set; }
    public long Height { get; set; }
    public long CenterX { get; set; }
    public long CenterY { get; set; }
    public List<string> Notes { get; set; } = new();
    public int UndoDepth { get; set; }
    public int RedoDepth { get; set; }
    public bool Dirty { get; set; }
}

#endregion

#region Create

public sealed class MapCreateRequest
{
    /// <summary>The nine-digit map id.</summary>
    public int Id { get; set; }
    public string? Name { get; set; }
    public string? StreetName { get; set; }
}

public sealed class MapCreateResultDto
{
    /// <summary>Session path of the new image, ready for /api/mapedit/open.</summary>
    public string Path { get; set; } = "";
    public string Archive { get; set; } = "";
    public string SavedTo { get; set; } = "";
    public string? BackupPath { get; set; }

    public bool StringRowWritten { get; set; }
    public string? StringRegion { get; set; }
    public string? StringSavedTo { get; set; }

    public List<string> Notes { get; set; } = new();
}

#endregion
