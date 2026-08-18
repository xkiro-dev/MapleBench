using MapleBench.Models;
using MapleBench.Services;

namespace MapleBench.Api;

public sealed class OpenFamilyRequest
{
    /// <summary>The folder the family's archives sit in.</summary>
    public string Folder { get; set; } = "";
    /// <summary>The family stem as detection reports it — "Skill".</summary>
    public string Name { get; set; } = "";
    /// <summary>
    /// True to merge archives that are already open instead of opening them from
    /// disk. The common case after "open a MapleStory folder", where all four
    /// Skill files are already parsed.
    /// </summary>
    public bool Adopt { get; set; }
    public string? MapleVersion { get; set; }
    public string? Iv { get; set; }
    public short? GameVersion { get; set; }
}

public sealed class PrecedenceRequest
{
    /// <summary>True to let the highest-numbered archive win duplicate names.</summary>
    public bool LastWins { get; set; }
}

/// <summary>
/// Browsing several physical archives as one tree.
///
/// The merged view is presentational: <c>/api/family/...</c> registers which
/// files belong together and answers listings for merged folders, and everything
/// else in the app carries on taking the real session path of the real file a
/// node lives in. That is why there is no save, no edit and no export here —
/// those endpoints already work, unchanged, because a merged tree never hands
/// out a path they have not always understood.
///
/// The one endpoint that is not about browsing is <c>/shadows</c>, and it is the
/// reason the rest exists. See <see cref="ArchiveFamilyService.Shadows"/>.
/// </summary>
public static class FamilyEndpoints
{
    public static void MapFamilies(this RouteGroupBuilder api)
    {
        // What families a folder holds, both shapes, open or not.
        api.MapGet("/family/detect", (string path, ArchiveFamilyService families) =>
            Results.Ok(families.Detect(path)));

        // What could be merged out of what is already open. Costs no disk at all,
        // which is what makes it safe to call on every file-list refresh.
        api.MapGet("/family/adoptable", (ArchiveFamilyService families) =>
            Results.Ok(families.Adoptable()));

        api.MapGet("/families", (ArchiveFamilyService families) =>
            Results.Ok(families.List()));

        api.MapPost("/family/open", (OpenFamilyRequest request, ArchiveFamilyService families,
                                     StringPoolService strings, IconService icons) =>
        {
            ArchiveFamilyDto family = request.Adopt
                ? families.Adopt(request.Folder, request.Name)
                : families.Open(request.Folder, request.Name, new OpenRequest
                {
                    Path = request.Folder,
                    MapleVersion = request.MapleVersion,
                    Iv = request.Iv,
                    GameVersion = request.GameVersion ?? -1,
                });

            // Opening a family can open archives, and both pools index by archive
            // name across every open file.
            strings.Invalidate();
            icons.Invalidate();
            return Results.Ok(family);
        });

        // Unmerge, not close. The archives stay open and stay editable; only the
        // extra root goes away. Closing them is what DELETE /api/files/{id} is
        // for, and conflating the two would make "I want the four trees back"
        // cost a re-read of 3 GB.
        api.MapDelete("/family/{familyId}", (string familyId, ArchiveFamilyService families) =>
            Results.Ok(new { unmerged = families.Unmerge(familyId) }));

        api.MapPost("/family/{familyId}/precedence",
            (string familyId, PrecedenceRequest request, ArchiveFamilyService families) =>
                Results.Ok(families.SetPrecedence(familyId, request.LastWins)));

        // The tree row chain for a real node path, so a reveal can expand its
        // ancestors in a tree where the ancestors are not the path's prefixes.
        // Empty for a path in no family, which is the answer "expand it the
        // ordinary way".
        api.MapGet("/family/locate", (string path, ArchiveFamilyService families) =>
            Results.Ok(families.Locate(path)));

        // Every image name that is in more than one member. The limit is on
        // entries returned, not on the scan: the totals in the response are the
        // real ones whatever the caller asks to see.
        //
        // 'compare' asks whether same-sized copies actually hold the same bytes.
        // Off by default because it parses those images and reads their canvases,
        // and on by choice because without it every same-sized pair comes back
        // "not-compared" — which the response says in as many words rather than
        // filing them with the ones proven identical.
        api.MapGet("/family/{familyId}/shadows",
            (string familyId, int? limit, bool? compare, ArchiveFamilyService families) =>
                Results.Ok(families.Shadows(familyId, limit ?? 200, compare ?? false)));
    }
}
