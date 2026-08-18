using MapleLib.WzLib;
using MapleLib.WzLib.WzProperties;

namespace MapleBench.Services;

public sealed class AnimationFrameDto
{
    public int Index { get; set; }
    public string Name { get; set; } = "";
    public string Path { get; set; } = "";
    public int Width { get; set; }
    public int Height { get; set; }
    /// <summary>Origin as stored on the frame.</summary>
    public int OriginX { get; set; }
    public int OriginY { get; set; }
    /// <summary>Where to blit this frame inside the composed canvas.</summary>
    public int OffsetX { get; set; }
    public int OffsetY { get; set; }
    /// <summary>Milliseconds to hold this frame.</summary>
    public int Delay { get; set; }
}

public sealed class AnimationDto
{
    public bool IsAnimation { get; set; }
    public string Path { get; set; } = "";
    /// <summary>Composed canvas size that fits every frame at its anchor.</summary>
    public int Width { get; set; }
    public int Height { get; set; }
    public int AnchorX { get; set; }
    public int AnchorY { get; set; }
    public int TotalMs { get; set; }
    public List<AnimationFrameDto> Frames { get; set; } = new();
}

/// <summary>
/// Recognises the WZ animation convention -- a container whose children are
/// numerically named canvases -- and works out how to play it.
///
/// Frames in a MapleStory animation are cropped differently and are positioned
/// by their 'origin' vector, not by a shared top-left. Blitting them all at
/// (0,0) makes the sprite jitter, which is why every frame carries an explicit
/// offset here rather than leaving the client to guess.
/// </summary>
public sealed class AnimationService
{
    private readonly WzSessionService _session;

    public AnimationService(WzSessionService session)
    {
        _session = session;
    }

    public AnimationDto Describe(string path)
    {
        lock (_session.Gate)
        {
            WzObject node = _session.Resolve(path);
            AnimationDto result = new() { Path = path };

            // Frames are the numerically-named canvas children, in numeric order.
            List<(int Number, WzCanvasProperty Canvas, string Name)> frames = new();
            int occurrence = 0;
            Dictionary<string, int> seen = new(StringComparer.OrdinalIgnoreCase);

            foreach (WzObject child in _session.EnumerateChildren(node))
            {
                string name = child.Name ?? "";
                seen.TryGetValue(name, out occurrence);
                seen[name] = occurrence + 1;

                if (!int.TryParse(name, out int number))
                    continue;

                // A UOL frame points at a canvas elsewhere; follow it so linked
                // animations still play.
                WzImageProperty? resolved = child as WzImageProperty;
                if (resolved is WzUOLProperty uol)
                    resolved = uol.LinkValue as WzImageProperty;

                if (resolved is WzCanvasProperty canvas)
                    frames.Add((number, canvas, WzPath.Child(path, name, occurrence)));
            }

            // A single canvas is a picture, not an animation -- but the caller
            // still has to draw something, and a response of all zeroes gives it
            // nothing to size a viewport with. Report the one canvas's own
            // dimensions (or the node's, when the node is itself a canvas).
            if (frames.Count < 2)
            {
                WzCanvasProperty? only = frames.Count == 1
                    ? frames[0].Canvas
                    : node as WzCanvasProperty;

                if (only != null)
                {
                    System.Drawing.PointF onlyOrigin = SafeOrigin(only);
                    (result.Width, result.Height) = EffectiveSize(only);
                    result.AnchorX = (int)onlyOrigin.X;
                    result.AnchorY = (int)onlyOrigin.Y;
                    result.TotalMs = ReadDelay(only);
                }
                return result;
            }

            frames.Sort((a, b) => a.Number.CompareTo(b.Number));
            result.IsAnimation = true;

            // Pass 1: read each frame's size, origin and delay.
            List<AnimationFrameDto> dtos = new(frames.Count);
            foreach ((int number, WzCanvasProperty canvas, string framePath) in frames)
            {
                System.Drawing.PointF origin = SafeOrigin(canvas);
                (int width, int height) = EffectiveSize(canvas);
                dtos.Add(new AnimationFrameDto
                {
                    Index = number,
                    Name = number.ToString(),
                    Path = framePath,
                    Width = width,
                    Height = height,
                    OriginX = (int)origin.X,
                    OriginY = (int)origin.Y,
                    Delay = ReadDelay(canvas),
                });
            }

            // Pass 2: place every frame so their origins coincide.
            (result.AnchorX, result.AnchorY, result.Width, result.Height, result.TotalMs) =
                PlaceAtSharedOrigin(dtos);
            result.Frames = dtos;
            return result;
        }
    }

    /// <summary>
    /// The composition rule itself, on its own so every caller that plays or
    /// exports an animation places frames the same way — the dumper's GIF/APNG
    /// routes go through <see cref="Describe"/> and the generation chooser's
    /// preview calls this directly on nodes it read outside the session.
    ///
    /// The shared anchor is the largest origin on each axis, so the per-frame
    /// offset (anchor - origin) is always &gt;= 0 and no frame needs negative
    /// coordinates. Mutates each frame's OffsetX/OffsetY in place and returns
    /// the composed canvas: anchor, size that fits every frame at its offset,
    /// and total duration.
    /// </summary>
    public static (int AnchorX, int AnchorY, int Width, int Height, int TotalMs)
        PlaceAtSharedOrigin(IReadOnlyList<AnimationFrameDto> frames)
    {
        if (frames.Count == 0) return (0, 0, 0, 0, 0);

        int anchorX = frames.Max(f => f.OriginX);
        int anchorY = frames.Max(f => f.OriginY);

        foreach (AnimationFrameDto frame in frames)
        {
            frame.OffsetX = anchorX - frame.OriginX;
            frame.OffsetY = anchorY - frame.OriginY;
        }

        return (anchorX, anchorY,
                frames.Max(f => f.OffsetX + f.Width),
                frames.Max(f => f.OffsetY + f.Height),
                frames.Sum(f => f.Delay));
    }

    /// <summary>
    /// Reads a frame's own metadata — size, origin, delay — the way the session
    /// path does, for canvases reached outside the session. The link-following
    /// half of <see cref="EffectiveSize"/> is deliberately NOT here: a chooser
    /// previewing donor art reads nodes whose links resolve against a different
    /// family mount than the session's, and a preview must not silently draw
    /// whatever a link happens to reach.
    /// </summary>
    public static AnimationFrameDto DescribeCanvas(WzCanvasProperty canvas, string name)
    {
        System.Drawing.PointF origin = SafeOrigin(canvas);
        return new AnimationFrameDto
        {
            Name = name,
            Width = canvas.PngProperty?.Width ?? 0,
            Height = canvas.PngProperty?.Height ?? 0,
            OriginX = (int)origin.X,
            OriginY = (int)origin.Y,
            Delay = ReadDelay(canvas),
        };
    }

    /// <summary>
    /// Finds canvases anywhere beneath a node so the inspector can show what a
    /// branch contains without the user expanding down to each leaf.
    ///
    /// Breadth-first and capped: the point is a quick contact sheet, not a full
    /// enumeration of an .img holding thousands of sprites.
    /// </summary>
    public List<AnimationFrameDto> CollectCanvases(string path, int limit = 60, int maxVisit = 4000, int maxDepth = 8)
        => CollectCanvases(path, out _, limit, maxVisit, maxDepth);

    /// <summary>
    /// As above, but says whether any of the three caps stopped the sweep early.
    ///
    /// Callers that publish a result -- the ZIP export's manifest -- cannot work
    /// this out from the returned count: the visit and depth caps can both fire
    /// long before the entry cap, which made a partial export look complete.
    ///
    /// It covers all four ways the sweep can come back short -- the visit cap, the
    /// depth cap, the entry cap, and a subtree that threw on the way in -- because
    /// a caller cannot act on a flag that covers only three of them.
    /// </summary>
    public List<AnimationFrameDto> CollectCanvases(
        string path, out bool truncated, int limit = 60, int maxVisit = 4000, int maxDepth = 8)
    {
        lock (_session.Gate)
        {
            WzObject root = _session.Resolve(path);
            List<AnimationFrameDto> found = new();

            Queue<(WzObject Node, string Path, int Depth)> queue = new();
            queue.Enqueue((root, path, 0));
            int visited = 0;
            bool stopped = false;

            while (queue.Count > 0 && found.Count < limit)
            {
                (WzObject node, string nodePath, int depth) = queue.Dequeue();

                // Guard against sweeping an entire archive from a directory node.
                // Breaking on depth is safe rather than lossy: the queue is FIFO,
                // so everything left in it is at least this deep.
                if (++visited > maxVisit || depth > maxDepth)
                {
                    stopped = true;
                    break;
                }

                Dictionary<string, int> seen = new(StringComparer.OrdinalIgnoreCase);
                List<WzObject> children;
                try { children = _session.EnumerateChildren(node).ToList(); }
                catch
                {
                    // A branch this sweep could not read is a branch it did not
                    // examine, and the caller has to be able to tell that from a
                    // branch that held nothing. Without this the flag below said
                    // "complete" for a sweep that skipped a whole subtree -- and
                    // that flag is written verbatim into the exported ZIP's
                    // manifest as 'truncated', where it is the only thing telling
                    // a user whether the dump is all of their sprites.
                    stopped = true;
                    continue;
                }

                foreach (WzObject child in children)
                {
                    string name = child.Name ?? "";
                    seen.TryGetValue(name, out int occurrence);
                    seen[name] = occurrence + 1;
                    string childPath = WzPath.Child(nodePath, name, occurrence);

                    // A UOL can be the drawable leaf itself. Item.wz uses this
                    // for ranked/variant items whose info/icon points at the
                    // first item's canvas (for example 02431008 -> 02431007).
                    // Keep the UOL's own session path in the DTO so callers can
                    // render and address the node the user actually selected;
                    // WzRenderService follows the link when it decodes it.
                    WzCanvasProperty? canvas = child as WzCanvasProperty;
                    if (canvas == null && child is WzUOLProperty uol)
                    {
                        try { canvas = uol.LinkValue as WzCanvasProperty; }
                        catch { /* a broken UOL is not drawable */ }
                    }

                    if (canvas != null)
                    {
                        if (found.Count >= limit)
                        {
                            stopped = true;
                            break;
                        }
                        System.Drawing.PointF origin = SafeOrigin(canvas);
                        (int width, int height) = EffectiveSize(canvas);
                        found.Add(new AnimationFrameDto
                        {
                            Name = name,
                            Path = childPath,
                            Width = width,
                            Height = height,
                            OriginX = (int)origin.X,
                            OriginY = (int)origin.Y,
                            Delay = ReadDelay(canvas),
                        });
                        // A canvas can still hold sub-properties, but they are
                        // metadata rather than more sprites.
                        continue;
                    }

                    // Do not descend into unparsed images from a directory sweep;
                    // that would parse the whole archive to draw a thumbnail strip.
                    if (child is WzImage image && node is WzDirectory && !image.Parsed)
                        continue;

                    queue.Enqueue((child, childPath, depth + 1));
                }
            }

            // Anything still queued is something the caps stopped us reaching.
            truncated = stopped || queue.Count > 0;
            return found;
        }
    }

    /// <summary>
    /// The drawn size of a canvas, following '_inlink'/'_outlink'.
    ///
    /// Linked frames store a 1x1 (or empty) placeholder PNG and keep the real
    /// pixels elsewhere — most of Mob.wz is built this way. Reading the
    /// placeholder's dimensions would size the composed animation canvas to a
    /// single pixel and scatter every frame.
    ///
    /// What makes a canvas a placeholder is the '_inlink'/'_outlink' child, not
    /// its size: a 1x1 canvas with no link is a genuine one-pixel sprite (and is
    /// exactly what WzNodeFactory seeds a new canvas with), and it has no other
    /// size to report anyway. Conversely a linked canvas is drawn from its link
    /// whatever its own dimensions claim — <see cref="WzRenderService"/> always
    /// follows the link — so the size has to be read from the same place.
    /// </summary>
    private static (int Width, int Height) EffectiveSize(WzCanvasProperty canvas)
    {
        int width = canvas.PngProperty?.Width ?? 0;
        int height = canvas.PngProperty?.Height ?? 0;

        try
        {
            if (!canvas.ContainsInlinkProperty() && !canvas.ContainsOutlinkProperty())
                return (width, height);

            if (canvas.GetLinkedWzImageProperty() is WzCanvasProperty linked
                && !ReferenceEquals(linked, canvas)
                && linked.PngProperty is { Width: > 0, Height: > 0 })
            {
                return (linked.PngProperty.Width, linked.PngProperty.Height);
            }
        }
        catch { /* broken link: fall back to what the placeholder claims */ }

        return (width, height);
    }

    /// <summary>
    /// MapleLib's origin helper throws when the property is missing, so read it
    /// defensively — plenty of frames have no origin at all.
    /// </summary>
    private static System.Drawing.PointF SafeOrigin(WzCanvasProperty canvas)
    {
        try
        {
            if (canvas[WzCanvasProperty.OriginPropertyName] is WzVectorProperty vector)
                return new System.Drawing.PointF(vector.X?.Value ?? 0, vector.Y?.Value ?? 0);
        }
        catch { /* fall through to the origin-less default */ }
        return System.Drawing.PointF.Empty;
    }

    /// <summary>
    /// Frame duration in ms.  Clients treat a missing or zero delay as 100 ms,
    /// so matching that keeps playback looking like the game.
    /// </summary>
    private static int ReadDelay(WzCanvasProperty canvas)
    {
        try
        {
            if (canvas[WzCanvasProperty.AnimationDelayPropertyName] is { } delay)
            {
                int value = delay.GetInt();
                if (value > 0)
                    return value;
            }
        }
        catch { /* no usable delay property */ }
        return 100;
    }
}
