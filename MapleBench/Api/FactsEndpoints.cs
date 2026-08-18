using MapleBench.Services;

namespace MapleBench.Api;

/// <summary>
/// Facts about a folder's children that cost too much to put in
/// <c>/api/inspect</c>.
///
/// Kept out of <see cref="Endpoints"/> for the same reason the skill and domain
/// surfaces are, and wired in with <c>MapFacts(api)</c> alongside the other
/// <c>Map*</c> calls.
///
/// The contract this has with the client is the same one thumbnails have: the
/// child table paints from <c>/api/inspect</c> immediately and asks for this
/// afterwards, because answering it means parsing every child image — 5.7s for
/// Character.wz/Cap's 3,331 of them on a v232 client. Nothing in the UI may
/// wait for it.
/// </summary>
public static class FactsEndpoints
{
    public static void MapFacts(this RouteGroupBuilder api)
    {
        // Under /node/ rather than at the top level because the path parameter
        // means the same thing here as it does for /node, /node/value and
        // /node/types: one session path, one node.
        api.MapGet("/node/facts", (string path, NodeFactsService facts, CancellationToken cancel) =>
            Results.Ok(facts.For(path, cancel)));
    }
}
