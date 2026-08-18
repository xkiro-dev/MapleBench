using System.Globalization;
using MapleBench.Models;
using MapleBench.Services;

namespace MapleBench.Api;

/// <summary>
/// The Skill editor's HTTP surface.
///
/// Kept out of <see cref="Endpoints"/> so the skill mode owns one file rather
/// than a region in a nine-hundred-line one; wired in with
/// <c>MapSkills(api)</c> alongside the other <c>Map*</c> calls.
///
/// The shapes mirror <c>/api/mob/*</c> deliberately, down to the parameter names:
/// a capabilities call the UI asks before it offers anything, a browse list, a
/// detail, a field write, and a bulk edit that is dry-run-first. Anything a user
/// learned about bulk-editing mobs is true here too.
/// </summary>
public static class SkillEndpoints
{
    public static void MapSkills(this RouteGroupBuilder api)
    {
        // What the mode can offer, before it offers it. The formula dialect is
        // part of the answer because the UI needs it to write a usable hint under
        // the formula editor, and hard-coding it there would let the two drift.
        api.MapGet("/skill/capabilities", (SkillService skills, StringPoolService strings) =>
            Results.Ok(new
            {
                available = skills.IsAvailable,
                names = strings.HasSource,
                formula = new
                {
                    variable = "x",
                    operators = SkillFormulaEvaluator.Operators,
                    functions = SkillFormulaEvaluator.Functions,
                    maxLength = SkillFormulaEvaluator.MaxLength,
                },
                bulkOps = new[] { "set", "add", "multiply", "percent" },
                rounding = new[] { "nearest", "floor", "none" },
                levelOps = new[] { "add", "clone", "remove", "rename" },
            }));

        api.MapGet("/skill/books", (string? fileId, SkillService skills) =>
            Results.Ok(skills.Books(fileId)));

        // 'book' narrows the list to one image, which is how the UI avoids paying
        // for all 4,846 skills of a client when the user has picked a job.
        // Paged when asked, whole when not -- see /api/mob/list.
        api.MapGet("/skill/list", (string? fileId, string? book, bool? names, int? offset, int? limit,
            SkillService skills, CancellationToken cancel) =>
        {
            if (offset is null && limit is null)
                return Results.Ok(skills.List(fileId, book, names ?? true, cancel));

            (SkillListDto page, int total) = skills.Page(
                fileId, book, names ?? true, offset ?? 0, limit ?? 200, cancel);
            return Results.Ok(new
            {
                skills = page.Skills,
                stats = page.Stats,
                truncated = page.Truncated,
                total,
                offset = offset ?? 0,
                limit = limit ?? 200,
            });
        });

        // `vars` carries values for free variables as "name=value" pairs joined
        // by ';' -- "x30=4;y=2". A query string rather than a POST because the
        // detail view is a GET and stays one: the values change what is computed
        // for display and change nothing in the archive.
        api.MapGet("/skill/detail", (string path, string? vars, SkillService skills) =>
            Results.Ok(skills.Detail(path, ParseVariables(vars))));

        api.MapPost("/skill/levels", (SkillLevelsWriteRequest request, SkillService skills) =>
            Results.Ok(skills.WriteLevels(request)));

        api.MapPost("/skill/level", (SkillLevelRequest request, SkillService skills) =>
            Results.Ok(skills.Level(request)));

        // Dry run by default in the DTO, so a client that forgets the flag previews
        // rather than writes.
        api.MapPost("/skill/expand-common", (SkillExpandRequest request, SkillService skills) =>
            Results.Ok(skills.ExpandCommon(request)));

        api.MapPost("/skill/bulk", (SkillBulkRequest request, SkillService skills) =>
            Results.Ok(skills.Bulk(request)));
    }

    /// <summary>
    /// Reads "x30=4;y=2" into a lookup. Anything unparseable is dropped rather
    /// than rejected: a half-typed value in the box should leave the rest of the
    /// table working, not 400 the whole request.
    /// </summary>
    private static IReadOnlyDictionary<string, double>? ParseVariables(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;

        Dictionary<string, double> values = new(StringComparer.OrdinalIgnoreCase);
        foreach (string pair in text.Split(';', StringSplitOptions.RemoveEmptyEntries
                                              | StringSplitOptions.TrimEntries))
        {
            int split = pair.IndexOf('=');
            if (split <= 0)
                continue;

            string name = pair[..split].Trim();
            if (name.Length == 0)
                continue;

            // Invariant, like every other number this app parses -- a comma-decimal
            // desktop must not read "1.5" as 15.
            if (double.TryParse(pair[(split + 1)..].Trim(), NumberStyles.Float,
                                CultureInfo.InvariantCulture, out double value))
                values[name] = value;
        }
        return values.Count > 0 ? values : null;
    }
}
