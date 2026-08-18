using System.Globalization;

namespace MapleBench.Services;

/// <summary>
/// The arithmetic behind bulk value editing: "make these 312 nodes 50% stronger"
/// rather than "make these 312 nodes all say 150".
///
/// Why this exists at all. Rebalancing is the most-repeated numeric job in WZ
/// work, and it is almost never "set them all to the same number" -- it is
/// "every weapon in this range hits 15% harder", which preserves the spread the
/// original designer chose. The bulk-edit dialog could only write one literal to
/// every selected node, so the only way to scale a range was to visit each node
/// and retype it. Measured against the Steam classic client's Mob.wz:
/// "name:maxHP &gt;10000" matches 2,111 nodes. Retuning those by hand, at the
/// eight interactions the app costs to reach and edit one, is ~16,900
/// interactions. With this it is one expression, one preview and one undo step.
///
/// The grammar is deliberately tiny -- five operators and three clamps. A full
/// expression language would need variables, precedence and a parser to argue
/// about, and none of that is what the job asks for. Everything here is applied
/// to one node against that node's own current value.
///
///   =VALUE        set literally (also what a bare word with no operator means)
///   +N  -N        add / subtract
///   *N  /N        multiply / divide
///   +N%  -N%      relative change; "+50%" is exactly "*1.5"
///   round         nearest integer      floor / ceil
///   min N         raise anything below N up to N
///   max N         lower anything above N down to N
///   clamp A B     both at once
///
/// This class is pure and has no session, no locks and no I/O, so it is unit
/// testable and so the preview and the write cannot disagree -- see
/// <see cref="WzEditService.ComputeValues"/>, which runs this once per node and
/// then either writes the answers or throws them away.
/// </summary>
public static class ValueMath
{
    /// <summary>What one node's expression evaluated to, or why it could not.</summary>
    public readonly record struct Outcome(string? Value, string? Skipped)
    {
        public bool Ok => Skipped == null;
        public static Outcome Result(string value) => new(value, null);
        public static Outcome Skip(string reason) => new(null, reason);
    }

    /// <summary>
    /// True when the expression needs the node's current value to mean anything.
    ///
    /// The caller uses this to decide whether a node with an unreadable value is
    /// a skip or is fine: "=0" is happy to overwrite a string, "*2" is not.
    /// </summary>
    public static bool IsRelative(string expression) => Parse(expression).Kind != OpKind.Literal;

    /// <summary>Human-readable one-liner for the confirmation, e.g. "multiply by 1.5".</summary>
    public static string Describe(string expression)
    {
        Op op = Parse(expression);
        return op.Kind switch
        {
            OpKind.Literal  => $"set every value to '{op.Literal}'",
            OpKind.Add      => $"add {Num(op.A)}",
            OpKind.Subtract => $"subtract {Num(op.A)}",
            OpKind.Multiply => $"multiply by {Num(op.A)}",
            OpKind.Divide   => $"divide by {Num(op.A)}",
            OpKind.Percent  => $"change by {(op.A >= 0 ? "+" : "")}{Num(op.A)}%",
            OpKind.Round    => "round to the nearest whole number",
            OpKind.Floor    => "round down",
            OpKind.Ceil     => "round up",
            OpKind.Min      => $"raise anything below {Num(op.A)} up to {Num(op.A)}",
            OpKind.Max      => $"lower anything above {Num(op.A)} down to {Num(op.A)}",
            OpKind.Clamp    => $"clamp between {Num(op.A)} and {Num(op.B)}",
            _               => expression,
        };
    }

    /// <summary>
    /// Evaluates <paramref name="expression"/> for one node.
    /// </summary>
    /// <param name="current">The node's value as the DTO reports it; null for a node that has none.</param>
    /// <param name="type">WzPropertyType name ("Int", "Float", ...), used to decide rounding. May be null.</param>
    public static Outcome Apply(string expression, string? current, string? type)
    {
        Op op = Parse(expression);

        if (op.Kind == OpKind.Literal)
            return Outcome.Result(op.Literal);

        if (op.Kind == OpKind.Invalid)
            return Outcome.Skip(op.Literal);

        // A relative operation on something that is not a number is the failure
        // this guard exists for: without it "*2" on a mob's `mobType` string
        // ("1N") wrote the literal text "NaN" into the client, which loads and
        // then misbehaves in-game with no error anywhere.
        if (!TryParseNumber(current, out double value))
        {
            // Blank counts as "has no value", not as "'' is not a number".
            // Quoting emptiness back at the reader tells them nothing, and an
            // empty string is what a node with no value actually reports.
            return Outcome.Skip(string.IsNullOrWhiteSpace(current)
                ? "has no value"
                : $"'{Trim(current)}' is not a number");
        }

        double result = op.Kind switch
        {
            OpKind.Add      => value + op.A,
            OpKind.Subtract => value - op.A,
            OpKind.Multiply => value * op.A,
            OpKind.Divide   => value / op.A,
            OpKind.Percent  => value * (1 + op.A / 100),
            OpKind.Round    => Math.Round(value, MidpointRounding.AwayFromZero),
            OpKind.Floor    => Math.Floor(value),
            OpKind.Ceil     => Math.Ceiling(value),
            OpKind.Min      => Math.Max(value, op.A),
            OpKind.Max      => Math.Min(value, op.A),
            OpKind.Clamp    => Math.Clamp(value, Math.Min(op.A, op.B), Math.Max(op.A, op.B)),
            _               => double.NaN,
        };

        // Divide-by-zero is Infinity in IEEE arithmetic, not an exception, so
        // without this check "/0" would sail through and write "Infinity" --
        // which WzNodeFactory.SetValue would then reject per node, reporting a
        // pile of unexplained failures instead of one clear one.
        if (double.IsNaN(result) || double.IsInfinity(result))
            return Outcome.Skip(op.Kind == OpKind.Divide ? "cannot divide by zero" : "not a number");

        return Outcome.Result(Format(result, current, type));
    }

    /// <summary>
    /// Renders the result the way this node's type will accept it.
    ///
    /// The rounding decision is by declared type first and by the node's own
    /// current value second. Type first because an Int node handed "22.5" is
    /// rejected by WzNodeFactory.SetValue, and a bulk edit that silently skips
    /// half its targets because the multiplier produced fractions is the exact
    /// "reports success over work that did not happen" failure. Current value
    /// second so that a node whose type we were not told, but which holds "15",
    /// still gets "23" rather than "22.5".
    /// </summary>
    private static string Format(double result, string? current, string? type)
    {
        bool integral = type switch
        {
            "Int" or "Short" or "Long" or "UnsignedShort" => true,
            "Float" or "Double" => false,
            // No type, or one we do not model: follow what is already there.
            _ => current != null && !current.Contains('.') && !current.Contains(','),
        };

        if (integral)
            return Math.Round(result, MidpointRounding.AwayFromZero).ToString("0", CultureInfo.InvariantCulture);

        // "R" would give 1.5000000000000002 for 1.5; 6 decimals is past anything
        // a WZ float carries and drops the binary-representation noise.
        return Math.Round(result, 6).ToString("0.######", CultureInfo.InvariantCulture);
    }

    private static bool TryParseNumber(string? text, out double value)
    {
        value = 0;
        if (string.IsNullOrWhiteSpace(text))
            return false;
        return double.TryParse(text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out value);
    }

    private static string Num(double value) =>
        value.ToString("0.######", CultureInfo.InvariantCulture);

    private static string Trim(string value) =>
        value.Length <= 24 ? value : value[..24] + "...";

    private enum OpKind { Literal, Invalid, Add, Subtract, Multiply, Divide, Percent, Round, Floor, Ceil, Min, Max, Clamp }

    private readonly record struct Op(OpKind Kind, double A, double B, string Literal);

    /// <summary>
    /// Reads one expression. Anything not recognised is a literal, deliberately:
    /// the box this comes from used to accept only literals, so a user who types
    /// what they have always typed must keep getting what they always got.
    /// </summary>
    private static Op Parse(string expression)
    {
        string text = (expression ?? "").Trim();
        if (text.Length == 0)
            return new Op(OpKind.Literal, 0, 0, "");

        // Explicit literal. Escapes anything that would otherwise be read as an
        // operator, which is the only way to write the literal string "*2".
        if (text[0] == '=')
            return new Op(OpKind.Literal, 0, 0, text[1..].Trim());

        string lower = text.ToLowerInvariant();
        switch (lower)
        {
            case "round": return new Op(OpKind.Round, 0, 0, text);
            case "floor": return new Op(OpKind.Floor, 0, 0, text);
            case "ceil" or "ceiling": return new Op(OpKind.Ceil, 0, 0, text);
        }

        foreach ((string word, OpKind kind) in new[] { ("min", OpKind.Min), ("max", OpKind.Max) })
        {
            if (!lower.StartsWith(word + " ", StringComparison.Ordinal))
                continue;
            return TryParseNumber(text[(word.Length + 1)..], out double bound)
                ? new Op(kind, bound, 0, text)
                : new Op(OpKind.Invalid, 0, 0, $"{word} needs a number, as in '{word} 1'");
        }

        if (lower.StartsWith("clamp ", StringComparison.Ordinal))
        {
            string[] parts = text[6..].Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (parts.Length == 2 && TryParseNumber(parts[0], out double low) && TryParseNumber(parts[1], out double high))
                return new Op(OpKind.Clamp, low, high, text);
            return new Op(OpKind.Invalid, 0, 0, "clamp needs two numbers, as in 'clamp 1 999'");
        }

        char head = text[0];
        if (head is '+' or '-' or '*' or '/')
        {
            string rest = text[1..].Trim();
            bool percent = rest.EndsWith('%');
            if (percent)
                rest = rest[..^1].TrimEnd();

            if (!TryParseNumber(rest, out double operand))
            {
                // A bare "-" or a "+abc" is far more likely a literal the user
                // meant than a typo'd operator -- "-1" as a value is ordinary in
                // WZ data -- so only a well-formed operand claims the operator.
                return new Op(OpKind.Literal, 0, 0, text);
            }

            if (percent)
            {
                // Percentages only make sense as a relative change. "*50%" would
                // read two ways (half, or +50%?), so it is not offered.
                if (head is '*' or '/')
                    return new Op(OpKind.Invalid, 0, 0, "% works with + and - only, as in '+15%'");
                return new Op(OpKind.Percent, head == '-' ? -operand : operand, 0, text);
            }

            return head switch
            {
                '+' => new Op(OpKind.Add, operand, 0, text),
                '-' => new Op(OpKind.Subtract, operand, 0, text),
                '/' => new Op(OpKind.Divide, operand, 0, text),
                _   => new Op(OpKind.Multiply, operand, 0, text),
            };
        }

        return new Op(OpKind.Literal, 0, 0, text);
    }
}
