using MapleBench.Services.MapEditor;

namespace MapleBench.Api;

/// <summary>
/// The map editor's HTTP surface. Everything stateful lives in
/// <see cref="MapEditorService"/>; these are thin routes in the shape the rest
/// of the app uses, inside the /api group so the shared error filter turns
/// refusals into readable toasts — a refused map open IS the banner text.
/// </summary>
public static class MapEditorEndpoints
{
    public static void MapMapEditor(this RouteGroupBuilder api)
    {
        RouteGroupBuilder group = api.MapGroup("/mapedit");

        group.MapGet("/capabilities", (MapEditorService maps) =>
            Results.Ok(maps.Capabilities()));

        group.MapGet("/maps", (string? q, int? limit, MapEditorService maps) =>
            Results.Ok(maps.ListMaps(q, Math.Clamp(limit ?? 200, 1, 2000))));

        // Open goes through MapRoundTrip.LoadVerified and nothing else. A map
        // the model refuses arrives at the client as an error with the model's
        // own reason — there is no partial open to fall back to.
        group.MapPost("/open", (MapOpenRequest request, MapEditorService maps) =>
            Results.Ok(maps.Open(request.Path)));

        group.MapGet("/doc", (string path, MapEditorService maps) =>
            Results.Ok(maps.GetDoc(path)));

        group.MapPost("/close", (MapDocRequest request, MapEditorService maps) =>
        {
            maps.Close(request.Path);
            return Results.NoContent();
        });

        group.MapPost("/edit", (MapEditRequest request, MapEditorService maps) =>
            Results.Ok(maps.Edit(request)));

        group.MapPost("/undo", (MapDocRequest request, MapEditorService maps) =>
            Results.Ok(maps.UndoRedo(request.Path, redo: false)));

        group.MapPost("/redo", (MapDocRequest request, MapEditorService maps) =>
            Results.Ok(maps.UndoRedo(request.Path, redo: true)));

        // The inspector's raw node view. addr is the index path from the
        // document root, comma-separated — names cannot address a node because
        // sibling names legally repeat.
        group.MapGet("/node", (string path, string addr, MapEditorService maps) =>
            Results.Ok(maps.InspectNode(path, ParseAddr(addr))));

        group.MapPost("/save", (MapDocRequest request, MapEditorService maps) =>
            Results.Ok(maps.Save(request.Path)));

        group.MapGet("/portal-icons", (MapEditorService maps) =>
            Results.Ok(maps.PortalIcons()));

        // The undo/redo stacks by label, top first — the history panel names
        // what Ctrl+Z is about to take back before it does.
        group.MapGet("/history", (string path, MapEditorService maps) =>
            Results.Ok(maps.History(path)));

        // The hover preview: the full animation behind a palette entry (frame
        // paths, composed offsets, measured delays) or the still's own meta.
        group.MapGet("/palette/preview", (string path, MapEditorService maps) =>
            Results.Ok(maps.PalettePreview(path)));

        // The editor's unsaved work, for the topbar dirty chip — these edits
        // live in editor documents, not the session tree, so /api/changes
        // cannot see them and the chip would otherwise say "nothing unsaved"
        // over real work.
        group.MapGet("/changes", (MapEditorService maps) =>
            Results.Ok(maps.Changes()));

        // ---- Phase 3: palette, placement, foothold authoring, minimap, create.

        group.MapGet("/palette/sets", (string kind, MapEditorService maps) =>
            Results.Ok(maps.PaletteSets(kind)));

        group.MapGet("/palette/entries", (string kind, string path, int? limit, MapEditorService maps) =>
            Results.Ok(maps.PaletteEntries(kind, path, Math.Clamp(limit ?? 1500, 1, 5000))));

        group.MapGet("/palette/life", (string? q, string? type, int? limit, MapEditorService maps) =>
            Results.Ok(maps.LifePalette(q, type ?? "m", Math.Clamp(limit ?? 60, 1, 200))));

        group.MapGet("/palette/reactors", (string? q, int? limit, MapEditorService maps) =>
            Results.Ok(maps.ReactorPalette(q, Math.Clamp(limit ?? 120, 1, 500))));

        // Thumbnails, content-addressed by the canvas's own _hash. An immutable
        // cache header is only safe BECAUSE the key is the content.
        group.MapGet("/thumb", (string path, MapEditorService maps, HttpResponse response) =>
        {
            (byte[] Png, string? Hash)? thumb = maps.Thumb(path);
            if (thumb == null)
                return Results.NotFound();
            if (thumb.Value.Hash != null)
                response.Headers.CacheControl = "public, max-age=31536000, immutable";
            return Results.Bytes(thumb.Value.Png, "image/png");
        });

        group.MapPost("/place", (MapPlaceRequest request, MapEditorService maps) =>
            Results.Ok(maps.Place(request)));

        group.MapPost("/foothold-chain", (MapFootholdChainRequest request, MapEditorService maps) =>
            Results.Ok(maps.AddFootholdChain(request)));

        group.MapPost("/auto-foothold", (MapAutoFootholdRequest request, MapEditorService maps) =>
            Results.Ok(maps.AutoFoothold(request)));

        group.MapPost("/set-layer-ts", (MapLayerTsRequest request, MapEditorService maps) =>
            Results.Ok(maps.SetLayerTs(request)));

        // The plan runs the sharer scan and states the consequences; the
        // regenerate endpoint refuses without the confirmations the plan named.
        group.MapGet("/minimap/plan", (string path, MapEditorService maps) =>
            Results.Ok(maps.MinimapPlan(path)));

        group.MapPost("/minimap/regenerate", (MapMinimapRegenRequest request, MapEditorService maps) =>
            Results.Ok(maps.MinimapRegenerate(request)));

        group.MapPost("/create", (MapCreateRequest request, MapEditorService maps) =>
            Results.Ok(maps.CreateMap(request)));
    }

    private static int[] ParseAddr(string addr)
    {
        string[] parts = addr.Split(',', StringSplitOptions.RemoveEmptyEntries);
        int[] result = new int[parts.Length];
        for (int i = 0; i < parts.Length; i++)
        {
            if (!int.TryParse(parts[i], out result[i]))
                throw new ArgumentException($"'{addr}' is not an address (comma-separated indexes).");
        }
        return result;
    }
}
