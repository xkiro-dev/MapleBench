using MapleBench.Models;
using MapleBench.Services;

namespace MapleBench.Api;

/// <summary>
/// The HTTP surface for moving content between two open clients.
///
/// Kept out of <see cref="Endpoints"/> so this owns one file rather than a region
/// in a nine-hundred-line one; wired in with <c>MapTransfers(api)</c> alongside
/// the other <c>Map*</c> calls, exactly like <see cref="SkillEndpoints"/>.
///
/// The routes live under <c>/api/port/*</c> rather than <c>/api/transfer/*</c>
/// on purpose: <c>/api/node/transfer</c> already exists and means "move this node
/// there", which is one of the several copies a port is made of. Two names for
/// two different things is cheaper than one name that means both.
///
/// The shape mirrors <c>/api/mob/bulk</c> down to the vocabulary — a capabilities
/// call the UI asks before it offers anything, a preview, and an apply that
/// refuses to run without an explicit confirmation. Anything a user learned about
/// previewing a bulk edit is true here too.
/// </summary>
public static class TransferEndpoints
{
    public static void MapTransfers(this RouteGroupBuilder api)
    {
        // What can be ported, where from and where to, before anything is
        // offered. The kind catalog is part of the answer because the UI has to
        // name the archives each kind needs, and hard-coding those names in the
        // front end would let the two drift.
        api.MapGet("/port/capabilities", (PortService port) =>
            Results.Ok(port.Capabilities()));

        // The open archives grouped into clients. Split from capabilities because
        // it is the one thing that changes while the screen is up — opening the
        // target client's String.wz is the usual fix for a blocked part, and the
        // UI re-asks this rather than reloading the whole mode.
        api.MapGet("/port/clients", (PortService port) =>
            Results.Ok(port.Clients()));

        // What one open archive holds that could be ported, searched by name or id.
        //
        // A GET, and the only read here that takes a query, because it is the
        // thing a picker calls per keystroke. It answers with the same session
        // paths the plan resolves against, which is what separates it from
        // /api/db/search: that one answers with ids and leaves the browser to
        // guess a path from naming conventions (database.js derivePath), and a
        // guess that misses turns into a port that reports the entry missing
        // after the user has committed to it.
        api.MapGet("/port/entries", (string fileId, string? kind, string? q, int? limit, string? under, PortService port) =>
            Results.Ok(port.Entries(fileId, kind, q, limit, under)));

        // The dry run. A GET would be nicer to cache and impossible to use: the
        // selection is a list of session paths and there is no sane length limit
        // on a query string for it.
        api.MapPost("/port/plan", (PortPlanRequest request, PortService port) =>
            Results.Ok(port.Plan(request)));

        // The only route that writes. It takes the same request as the plan plus
        // `confirmed`, and it recomputes the plan itself rather than trusting the
        // one the client is looking at — see PortService.Apply for why that is
        // what makes the preview honest rather than decorative.
        api.MapPost("/port/apply", (PortApplyRequest request, PortService port) =>
            Results.Ok(port.Apply(request)));
    }
}
