using System.Globalization;

namespace MapleBench.Services;

/// <summary>
/// Turns one typed line into the predicates the search actually needs.
///
/// Why. <see cref="WzSearchService"/> matched a single needle against the name
/// OR the value, which makes the question people actually have unaskable.
/// Measured on the classic client with String.wz, Mob.wz and Skill.wz open:
/// searching "maxHP" returns 300 hits and stops early, and not one of them is a
/// maxHP property -- every hit is a `desc` or `h` string in String.wz whose
/// prose happens to contain the word. The mob stat you were looking for is not
/// on the first page, or the tenth.
///
/// The grammar is two optional prefixes and an optional comparison, and nothing
/// else. Anything unprefixed keeps meaning exactly what it meant before, so the
/// palette and the search box are not retrained:
///
///   maxHP                 as before: name or value contains "maxHP"
///   name:maxHP            the node is called maxHP
///   value:150             its value contains 150
///   value:&gt;10000          its value is a number greater than 10000
///   name:maxHP value:&gt;10000     both, ANDed
///   name:maxHP &gt;10000     shorthand: a bare comparison is a value comparison
///
/// Quoting with "..." lets a term contain spaces. Parsing lives on the server so
/// the palette, the search panel and any future caller cannot drift apart in
/// what they think the user typed.
/// </summary>
public static class SearchQuery
{
    public enum Compare { None, Greater, GreaterOrEqual, Less, LessOrEqual, Equal, NotEqual }

    public sealed class Parsed
    {
        /// <summary>Substring the node's name (or its String.wz display name) must contain.</summary>
        public string? Name { get; init; }
        /// <summary>Substring the node's value must contain, when <see cref="Op"/> is None.</summary>
        public string? Value { get; init; }
        /// <summary>Numeric comparison against the node's value.</summary>
        public Compare Op { get; init; }
        public double Operand { get; init; }
        /// <summary>The leftover text, matched the old way against name or value.</summary>
        public string? Free { get; init; }

        /// <summary>True when nothing at all was typed, so the caller can skip the walk.</summary>
        public bool IsEmpty => Name == null && Value == null && Free == null && Op == Compare.None;

        /// <summary>True when the query says something the old single-needle form could not.</summary>
        public bool IsStructured => Name != null || Value != null || Op != Compare.None;
    }

    public static Parsed Parse(string? query)
    {
        string? name = null, value = null, free = null;
        Compare op = Compare.None;
        double operand = 0;

        foreach (string token in Tokenize(query ?? ""))
        {
            if (StartsWith(token, "name:", out string namePart))
            {
                name = Combine(name, namePart);
                continue;
            }

            if (StartsWith(token, "value:", out string valuePart))
            {
                if (TryComparison(valuePart, out Compare found, out double bound))
                {
                    op = found;
                    operand = bound;
                }
                else
                {
                    value = Combine(value, valuePart);
                }
                continue;
            }

            // A bare ">10000" is unambiguous -- names are not compared
            // numerically -- so it does not need the value: prefix.
            if (TryComparison(token, out Compare bare, out double bareBound))
            {
                op = bare;
                operand = bareBound;
                continue;
            }

            free = Combine(free, token);
        }

        return new Parsed { Name = name, Value = value, Op = op, Operand = operand, Free = free };
    }

    /// <summary>Applies the numeric comparison to one value, false when it is not a number.</summary>
    public static bool Matches(Compare op, double operand, string? value)
    {
        if (op == Compare.None)
            return true;
        if (!double.TryParse((value ?? "").Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out double parsed))
            return false;

        return op switch
        {
            Compare.Greater        => parsed > operand,
            Compare.GreaterOrEqual => parsed >= operand,
            Compare.Less           => parsed < operand,
            Compare.LessOrEqual    => parsed <= operand,
            // Compared as numbers, not as text, so "15" and "15.0" agree --
            // which is the whole reason to spell it "value:=15" rather than
            // "value:15".
            Compare.Equal          => parsed == operand,
            Compare.NotEqual       => parsed != operand,
            _ => true,
        };
    }

    private static string Combine(string? existing, string addition) =>
        // Repeating a prefix ANDs nothing useful, so the last one wins rather
        // than producing a term that can never match.
        addition.Length == 0 ? (existing ?? "") : addition;

    private static bool StartsWith(string token, string prefix, out string rest)
    {
        if (token.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            rest = Unquote(token[prefix.Length..]);
            return true;
        }
        rest = "";
        return false;
    }

    private static bool TryComparison(string token, out Compare op, out double operand)
    {
        op = Compare.None;
        operand = 0;
        if (token.Length < 2)
            return false;

        // Two-character operators first: ">=" must not be read as ">" followed
        // by a "=10000" that fails to parse.
        (string symbol, Compare kind)[] operators =
        {
            (">=", Compare.GreaterOrEqual),
            ("<=", Compare.LessOrEqual),
            ("!=", Compare.NotEqual),
            (">",  Compare.Greater),
            ("<",  Compare.Less),
            ("=",  Compare.Equal),
        };

        foreach ((string symbol, Compare kind) in operators)
        {
            if (!token.StartsWith(symbol, StringComparison.Ordinal))
                continue;
            if (!double.TryParse(token[symbol.Length..].Trim(), NumberStyles.Float,
                                 CultureInfo.InvariantCulture, out operand))
                return false;
            op = kind;
            return true;
        }
        return false;
    }

    /// <summary>Splits on whitespace, keeping "quoted runs" together.</summary>
    private static List<string> Tokenize(string query)
    {
        List<string> tokens = new();
        int index = 0;
        while (index < query.Length)
        {
            while (index < query.Length && char.IsWhiteSpace(query[index]))
                index++;
            if (index >= query.Length)
                break;

            int start = index;
            bool quoted = false;
            while (index < query.Length && (quoted || !char.IsWhiteSpace(query[index])))
            {
                if (query[index] == '"')
                    quoted = !quoted;
                index++;
            }
            string token = query[start..index];
            if (token.Length > 0)
                tokens.Add(token);
        }
        return tokens;
    }

    private static string Unquote(string text)
    {
        string trimmed = text.Trim();
        if (trimmed.Length >= 2 && trimmed[0] == '"' && trimmed[^1] == '"')
            return trimmed[1..^1];
        return trimmed.Replace("\"", "");
    }
}
