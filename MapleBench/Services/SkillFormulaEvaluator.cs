using System.Globalization;

namespace MapleBench.Services;

/// <summary>
/// The variables a formula can see.
///
/// MapleStory's own evaluator does not work over a single variable. A
/// <c>common</c> block is a little namespace: every key in it is visible to
/// every other key, so <c>y = "3+x"</c> and <c>damage = "140+y"</c> is an
/// ordinary pair and not a broken formula. Anything the block does not define
/// is a free variable — the client supplies it from somewhere this editor
/// cannot see — and the honest thing to do with one is to ask for its value
/// rather than to refuse the formula or to quietly call it zero.
/// </summary>
public sealed class FormulaScope
{
    /// <summary>Nothing but <c>x</c>. What a bare expression is evaluated against.</summary>
    public static readonly FormulaScope Empty = new(null, null);

    /// <summary>
    /// Resolves every unknown name to 1, so a parse can be checked for
    /// well-formedness without caring whether the names have values yet.
    /// Records nothing and is shared, so it must stay stateless — hence the
    /// <see cref="_syntaxOnly"/> flag rather than a pre-filled dictionary.
    /// </summary>
    public static readonly FormulaScope Syntax = new(null, null) { _syntaxOnly = true };

    private readonly IReadOnlyDictionary<string, string>? _definitions;
    private readonly IReadOnlyDictionary<string, double>? _supplied;
    private bool _syntaxOnly;

    /// <summary>
    /// Names currently being resolved, so <c>y = "140+y"</c> is reported as
    /// self-reference instead of recursing until the stack ends the process.
    /// A v232 client contains exactly one such formula (skill 65000003).
    /// </summary>
    private readonly HashSet<string> _open = new(StringComparer.OrdinalIgnoreCase);

    private readonly SortedSet<string> _needed = new(StringComparer.OrdinalIgnoreCase);

    public FormulaScope(
        IReadOnlyDictionary<string, string>? definitions,
        IReadOnlyDictionary<string, double>? supplied)
    {
        _definitions = definitions;
        _supplied = supplied;
    }

    /// <summary>
    /// Free variables this scope was asked for and could not answer, in the
    /// order a person reads them. Populated as a side effect of evaluating, so
    /// the caller can turn "I need y" into an input box rather than an error.
    /// </summary>
    public IReadOnlyCollection<string> Needed => _needed;

    /// <summary>Whether this scope defines <paramref name="name"/> at all.</summary>
    public bool Defines(string name) => _definitions?.ContainsKey(name) == true;

    /// <summary>
    /// The text of a sibling definition, or null. Read-only on purpose: the
    /// scope owns its map so nothing downstream can change what a formula sees
    /// partway through a table.
    /// </summary>
    public string? DefinitionText(string name)
        => _definitions != null && _definitions.TryGetValue(name, out string? text) ? text : null;

    /// <summary>
    /// The value of a bare name. False means no value, and then
    /// <paramref name="why"/> says whether that is because nothing defines it
    /// (recoverable — ask the user) or because its definition is itself broken.
    /// </summary>
    internal bool TryResolve(string name, double level, out double value, out string? why)
    {
        value = 0;
        why = null;

        if (_supplied != null && _supplied.TryGetValue(name, out value))
            return true;

        if (_definitions != null && _definitions.TryGetValue(name, out string? definition))
        {
            if (!_open.Add(name))
            {
                why = $"'{name}' is defined in terms of itself, so it has no value.";
                return false;
            }
            try
            {
                double? inner = SkillFormulaEvaluator.EvaluateCore(definition, level, this, out string? innerError);
                if (inner == null)
                {
                    why = $"'{name}' cannot be worked out: {innerError}";
                    return false;
                }
                value = inner.Value;
                return true;
            }
            finally
            {
                _open.Remove(name);
            }
        }

        if (_syntaxOnly)
        {
            value = 1;
            return true;
        }

        _needed.Add(name);
        why = $"'{name}' has no value here. Nothing in this skill's 'common' block defines it, " +
              "so type a value for it and the table will fill in.";
        return false;
    }
}

/// <summary>
/// Evaluates the little arithmetic language MapleStory writes into a skill's
/// <c>common</c> block, where every per-level value is an expression over the
/// level variable <c>x</c> — <c>"235+3*x"</c>, <c>"6+d(x/10)"</c>,
/// <c>"min(x,6)-min(x,3)"</c>.
///
/// It is a hand-written recursive-descent parser on purpose. There is no
/// <c>eval</c>, no expression-compiler, no scripting package and no way to reach
/// anything outside this file: the input is attacker-controlled in the sense that
/// it comes out of whatever archive the user opened, and a WZ editor has no
/// business turning a client's data into code it runs. The whole grammar is
/// fifteen lines, so nothing is lost by writing it out.
///
/// The dialect is wider than any single client uses, deliberately. A v232
/// client's Skill.wz needs only <c>+ - * /</c>, parentheses, decimals, unary
/// minus, <c>x</c>, and <c>u/d/min/max</c>. Other clients and other regions
/// also write:
///
///   <c>140+y</c>            — a reference to another key in the same block
///   <c>200+4*x30</c>        — a free variable the client supplies
///   <c>log70(x)*20</c>      — a logarithm with its base in the function name
///   <c>5+10*(x-1)%</c>      — a trailing '%' marking the result as a percentage
///
/// All four are accepted, because an editor that refuses a formula the game
/// itself runs is wrong about the game, not the other way round.
///
/// What it will not do is answer 0 for something it could not work out. A
/// silently zeroed damage formula is indistinguishable from a real 0, and a
/// level table full of plausible zeros is the exact failure the quality bar
/// calls out. Every failure comes back as a null value plus the text of what
/// went wrong and where — and for the one recoverable case, a free variable,
/// the name is handed back so the caller can ask for it.
/// </summary>
public static class SkillFormulaEvaluator
{
    /// <summary>
    /// The longest expression the parser will look at. The longest real one in a
    /// v232 client is 18 characters (<c>"min(x,10)-min(x,9)"</c>); this is a
    /// backstop against a hand-edited archive, not a real limit.
    /// </summary>
    public const int MaxLength = 512;

    /// <summary>
    /// Nesting limit. Recursive descent means parenthesis depth is stack depth,
    /// so without this a file containing 100,000 open brackets is a process-killing
    /// StackOverflowException, which no try/catch in the request pipeline can
    /// contain. Real expressions nest one level deep.
    /// </summary>
    private const int MaxDepth = 24;

    /// <summary>
    /// How far a name may resolve through other names. <c>a = b</c>, <c>b = c</c>,
    /// and so on: self-reference is caught exactly by <see cref="FormulaScope"/>,
    /// but a long chain still costs stack, and each link is a full parse.
    /// </summary>
    private const int MaxResolutionDepth = 16;

    [ThreadStatic]
    private static int _resolutionDepth;

    /// <summary>
    /// The named functions the dialect defines, for /skill/capabilities. <c>log&lt;base&gt;</c> is also accepted but cannot be
    /// listed here, because the base is part of the name: log2, log70, log100
    /// are each a distinct identifier. See <see cref="Parser.Apply"/>.
    /// </summary>
    public static readonly string[] Functions = { "u", "d", "min", "max", "log<base>" };

    /// <summary>The operators the dialect defines, for /skill/capabilities.</summary>
    public const string Operators = "+ - * / % ( )";

    /// <summary>
    /// The value of <paramref name="expression"/> at skill level
    /// <paramref name="level"/>, or null with <paramref name="error"/> set.
    ///
    /// Null and error are the same event seen from two sides; a caller that
    /// ignores the error still cannot mistake a failure for a value, because
    /// there is no value.
    /// </summary>
    public static double? Evaluate(string? expression, double level, out string? error)
        => Evaluate(expression, level, FormulaScope.Empty, out error);

    /// <inheritdoc cref="Evaluate(string?, double, out string?)"/>
    /// <param name="scope">
    /// The other names in reach — the sibling keys of the same <c>common</c>
    /// block, plus any values the user has supplied for free variables.
    /// </param>
    public static double? Evaluate(string? expression, double level, FormulaScope scope, out string? error)
    {
        _resolutionDepth = 0;
        return EvaluateCore(expression, level, scope, out error);
    }

    /// <summary>
    /// The body of <see cref="Evaluate(string?, double, FormulaScope, out string?)"/>,
    /// re-entered when a name resolves to another expression. Separate only so
    /// that the recursion counter is reset once per top-level call and not once
    /// per link in a chain of names.
    /// </summary>
    internal static double? EvaluateCore(string? expression, double level, FormulaScope scope, out string? error)
    {
        error = null;

        if (string.IsNullOrWhiteSpace(expression))
        {
            error = "The formula is empty.";
            return null;
        }
        if (expression.Length > MaxLength)
        {
            error = $"The formula is {expression.Length} characters long; the limit is {MaxLength}.";
            return null;
        }
        if (++_resolutionDepth > MaxResolutionDepth)
        {
            _resolutionDepth--;
            error = $"These formulas refer to each other more than {MaxResolutionDepth} deep.";
            return null;
        }

        try
        {
            Parser parser = new(expression, level, scope);
            double value;
            try
            {
                value = parser.ParseExpression(0);
                parser.SkipSpace();
                if (!parser.AtEnd)
                    throw new FormulaException($"Unexpected '{parser.Current}' at position {parser.Position + 1}.");
            }
            catch (FormulaException ex)
            {
                error = $"{ex.Message} In: {expression}";
                return null;
            }

            // Infinity and NaN reach here from a division the parser could not refuse
            // in advance -- 1/(x-3) at level 3, say, which is legal arithmetic right up
            // to the moment it is not. Reporting them as values would put "Infinity" or
            // "NaN" in a cell the user then tries to write back.
            if (double.IsNaN(value) || double.IsInfinity(value))
            {
                error = $"The formula does not produce a number at level {level.ToString(CultureInfo.InvariantCulture)}. In: {expression}";
                return null;
            }
            return value;
        }
        finally
        {
            _resolutionDepth--;
        }
    }

    /// <summary>
    /// Whether the expression is well-formed — parseable as arithmetic —
    /// without caring whether every name in it has a value yet.
    ///
    /// This is the question a column asks: <c>"140+y"</c> is a formula whether
    /// or not <c>y</c> is defined, and calling it a syntax error because a
    /// variable is missing would file a perfectly good expression under
    /// "broken". Checked at level 1, the only level every skill certainly has.
    /// </summary>
    public static bool IsValid(string? expression, out string? error)
    {
        if (string.IsNullOrEmpty(expression))
            return Evaluate(expression, 1, FormulaScope.Syntax, out error) != null;

        if (TryRecall(_syntaxCache, expression, out string? remembered))
        {
            error = remembered;
            return remembered == null;
        }

        bool valid = Evaluate(expression, 1, FormulaScope.Syntax, out error) != null;
        Remember(_syntaxCache, expression, valid ? null : error ?? "");
        return valid;
    }

    /// <summary>
    /// Whether an expression parses, by expression text. Null value means it does.
    ///
    /// Safe to cache by construction: the key IS the input, and the answer is
    /// taken against <see cref="FormulaScope.Syntax"/>, which resolves every
    /// name the same way for ever. Nothing about the archive can change it — so
    /// unlike almost everything else in this app, it needs no generation stamp
    /// and cannot go stale.
    ///
    /// Worth having because the skill list runs this over every string entry of
    /// every skill's <c>common</c> block: about 50,000 parses on a v232 client,
    /// over a few hundred distinct expressions, on every list build.
    /// </summary>
    private static readonly Dictionary<string, string?> _syntaxCache = new(StringComparer.Ordinal);

    /// <summary>Bare names by expression text; same reasoning as <see cref="_syntaxCache"/>.</summary>
    private static readonly Dictionary<string, string[]> _namesCache = new(StringComparer.Ordinal);

    /// <summary>
    /// Past this the caches are cleared rather than grown. A client has a few
    /// hundred distinct expressions; this only bites on an archive full of
    /// hand-written one-offs, where dropping the memo is the right answer anyway.
    /// </summary>
    private const int MaxCachedExpressions = 4096;

    private static void Remember<T>(Dictionary<string, T> cache, string key, T value)
    {
        lock (cache)
        {
            if (cache.Count >= MaxCachedExpressions)
                cache.Clear();
            cache[key] = value;
        }
    }

    private static bool TryRecall<T>(Dictionary<string, T> cache, string key, out T value)
    {
        lock (cache) return cache.TryGetValue(key, out value!);
    }

    /// <summary>
    /// Whether the expression actually varies with the level.
    ///
    /// This is what separates a formula from a constant that merely happens to be
    /// stored as text — a v232 client writes <c>range = "400"</c> as a string in
    /// the same block as <c>damage = "235+3*x"</c>, and presenting the first as a
    /// per-level formula would be dishonest about where the number comes from.
    ///
    /// Identifier-aware rather than a substring test: <c>"max(x,3)"</c> contains
    /// an 'x' inside 'max' too, and <c>IndexOf('x')</c> cannot tell them apart —
    /// nor can it tell <c>x</c> from <c>x30</c>, which is a different variable
    /// entirely and does not by itself make a formula level-varying.
    /// </summary>
    public static bool ReferencesLevel(string? expression)
        => ReferencesLevel(expression, FormulaScope.Empty);

    /// <inheritdoc cref="ReferencesLevel(string?)"/>
    /// <param name="scope">
    /// Used to follow names: <c>damage = "y*2"</c> varies with the level when
    /// <c>y</c> does, and treating it as a constant would print level 1's number
    /// down the whole column.
    /// </param>
    public static bool ReferencesLevel(string? expression, FormulaScope scope)
        => ReferencesLevel(expression, scope, 0);

    private static bool ReferencesLevel(string? expression, FormulaScope scope, int depth)
    {
        if (string.IsNullOrEmpty(expression) || depth > MaxResolutionDepth)
            return false;

        foreach (string name in Names(expression))
        {
            if (name is "x" or "X")
                return true;
            // Follows only names the block defines, and the depth cap ends a
            // cycle -- "y = y" answers false rather than running forever.
            if (scope.Defines(name) && ReferencesLevel(scope.DefinitionText(name), scope, depth + 1))
                return true;
        }
        return false;
    }

    /// <summary>
    /// Every bare name in the expression — variables, not function calls, and
    /// not duplicated. <c>"min(x,y)+y"</c> yields x and y.
    /// </summary>
    public static IReadOnlyList<string> Names(string? expression)
    {
        if (string.IsNullOrEmpty(expression))
            return Array.Empty<string>();

        if (TryRecall(_namesCache, expression, out string[] remembered))
            return remembered;

        string[] names = ScanNames(expression).ToArray();
        Remember(_namesCache, expression, names);
        return names;
    }

    private static IEnumerable<string> ScanNames(string expression)
    {
        HashSet<string> seen = new(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < expression.Length; i++)
        {
            if (!IsIdentifierStart(expression[i]))
                continue;

            int start = i;
            while (i < expression.Length && IsIdentifierPart(expression[i]))
                i++;

            // A name immediately followed by '(' is a function, not a variable.
            int after = i;
            while (after < expression.Length && expression[after] == ' ')
                after++;
            bool isCall = after < expression.Length && expression[after] == '(';

            string name = expression[start..i];
            i--;

            if (!isCall && seen.Add(name))
                yield return name;
        }
    }

    /// <summary>
    /// The free variables in <paramref name="expression"/> — the names that are
    /// neither <c>x</c> nor defined by <paramref name="scope"/>, and so have to
    /// come from the user before the column can show numbers.
    /// </summary>
    public static IReadOnlyList<string> FreeNames(string? expression, FormulaScope scope)
    {
        List<string> free = new();
        Walk(expression, 0);
        return free;

        void Walk(string? text, int depth)
        {
            if (depth > MaxResolutionDepth)
                return;   // a cycle; the evaluator names it properly, this just stops
            foreach (string name in Names(text))
            {
                if (name is "x" or "X")
                    continue;
                if (scope.Defines(name))
                {
                    Walk(scope.DefinitionText(name), depth + 1);
                    continue;
                }
                if (!free.Contains(name, StringComparer.OrdinalIgnoreCase))
                    free.Add(name);
            }
        }
    }

    /// <summary>
    /// Whether the expression reaches itself through the block — <c>y = "140+y"</c>,
    /// the one genuinely broken formula in a stock v232 client (skill 65000003).
    ///
    /// Asked separately from evaluating because the answer is the same at every
    /// level: a cycle is a property of the block, not of level 7. Without it the
    /// column falls back to printing the expression text where a number belongs,
    /// which reads as "the value is the string 140+y" rather than "this cannot
    /// be computed".
    /// </summary>
    public static bool HasCycle(string? expression, FormulaScope scope)
    {
        HashSet<string> open = new(StringComparer.OrdinalIgnoreCase);
        return Walk(expression, 0);

        bool Walk(string? text, int depth)
        {
            if (depth > MaxResolutionDepth)
                return true;   // too deep to be anything else
            foreach (string name in Names(text))
            {
                if (name is "x" or "X" || !scope.Defines(name))
                    continue;
                if (!open.Add(name))
                    return true;
                try
                {
                    if (Walk(scope.DefinitionText(name), depth + 1))
                        return true;
                }
                finally { open.Remove(name); }
            }
            return false;
        }
    }

    /// <summary>
    /// Whether the text was meant as arithmetic at all.
    ///
    /// A <c>common</c> block is not exclusively formulas: a v232 client stores
    /// <c>4311003/common/action = "slashStorm2"</c>, the name of an animation,
    /// right beside <c>damage = "150+2*x"</c>. Both are strings and only one is an
    /// expression, so a parse failure alone cannot tell "this formula is broken"
    /// from "this was never a formula".
    ///
    /// The test is the presence of an operator, which is what an expression has
    /// and a name does not. Over the 16,698 <c>common</c> strings in a v232
    /// client it separates the two cases exactly: <c>"slashStorm2"</c> is text
    /// and <c>"140+y"</c> is arithmetic. A name that happened to contain a
    /// hyphen would be called a formula too, and the cost of that is showing the
    /// parser's message instead of the text; it is never a silently invented
    /// number, which is the failure that matters.
    /// </summary>
    public static bool LooksLikeFormula(string? text)
    {
        if (string.IsNullOrEmpty(text))
            return false;

        foreach (char c in text)
        {
            if (c is '+' or '-' or '*' or '/' or '%' or '(' or ')')
                return true;
        }
        return false;
    }

    private static bool IsIdentifierStart(char c) => char.IsAsciiLetter(c) || c == '_';
    private static bool IsIdentifierPart(char c) => char.IsAsciiLetterOrDigit(c) || c == '_';

    /// <summary>Carries a parse failure out of the recursive walk. Never escapes this file.</summary>
    private sealed class FormulaException : Exception
    {
        public FormulaException(string message) : base(message) { }
    }

    /// <summary>
    /// The grammar, in the order the methods below implement it:
    ///
    ///   expression := term (('+' | '-') term)*
    ///   term       := unary (('*' | '/' | '%') unary | '%')*
    ///   unary      := ('+' | '-') unary | primary
    ///   primary    := number | name | name '(' expression (',' expression)* ')' | '(' expression ')'
    ///
    /// Left-associative and precedence-correct by construction, which is the
    /// reason to write it this way rather than as a flat scan: <c>20000+1000*d(x/2)</c>
    /// is a real expression from the client and a scan that applied operators in
    /// reading order would give it a wildly different value with no sign of it.
    ///
    /// The one oddity is '%', which is both an infix operator and a postfix
    /// marker. Which one it is depends on what follows it, and nothing else in
    /// the grammar needs lookahead. See <see cref="ParseTerm"/>.
    /// </summary>
    private sealed class Parser
    {
        private readonly string _text;
        private readonly double _x;
        private readonly FormulaScope _scope;
        private int _pos;

        public Parser(string text, double x, FormulaScope scope)
        {
            _text = text;
            _x = x;
            _scope = scope;
        }

        public int Position => _pos;
        public bool AtEnd => _pos >= _text.Length;
        public char Current => _pos < _text.Length ? _text[_pos] : '\0';

        public void SkipSpace()
        {
            while (_pos < _text.Length && char.IsWhiteSpace(_text[_pos]))
                _pos++;
        }

        public double ParseExpression(int depth)
        {
            Guard(depth);
            double left = ParseTerm(depth);

            while (true)
            {
                SkipSpace();
                char op = Current;
                if (op != '+' && op != '-')
                    return left;

                _pos++;
                double right = ParseTerm(depth);
                left = op == '+' ? left + right : left - right;
            }
        }

        private double ParseTerm(int depth)
        {
            Guard(depth);
            double left = ParseUnary(depth);

            while (true)
            {
                SkipSpace();
                char op = Current;

                // '%' with nothing to divide into is the percent sign, not the
                // modulo operator: "5+10*(x-1)%" is a real formula and means
                // 95 percent at level 10, not 95 modulo nothing. It marks the
                // unit and leaves the number alone -- a WZ percentage field
                // stores 95 for 95%, so dividing by 100 here would write 0.95
                // into a field the client reads as a whole number.
                if (op == '%' && !StartsValue(_pos + 1))
                {
                    _pos++;
                    continue;
                }

                if (op != '*' && op != '/' && op != '%')
                    return left;

                _pos++;
                double right = ParseUnary(depth);

                // Refused rather than allowed to become Infinity. The caller would
                // reject the Infinity anyway, but the message it could give is
                // "not a number" -- naming the divide-by-zero says which part of
                // the expression to look at.
                if (op != '*' && right == 0)
                    throw new FormulaException($"Division by zero at position {_pos}.");

                left = op switch
                {
                    '*' => left * right,
                    '/' => left / right,
                    _ => left % right,
                };
            }
        }

        /// <summary>
        /// Whether a value could begin at <paramref name="from"/>, skipping
        /// spaces. The only lookahead in the parser, and it exists solely to
        /// tell infix '%' from postfix '%'.
        /// </summary>
        private bool StartsValue(int from)
        {
            while (from < _text.Length && char.IsWhiteSpace(_text[from]))
                from++;
            if (from >= _text.Length)
                return false;
            char c = _text[from];
            return char.IsAsciiDigit(c) || c == '.' || c == '(' || c == '-' || c == '+' || IsIdentifierStart(c);
        }

        private double ParseUnary(int depth)
        {
            Guard(depth);
            SkipSpace();

            if (Current == '-')
            {
                _pos++;
                return -ParseUnary(depth + 1);
            }
            if (Current == '+')
            {
                _pos++;
                return ParseUnary(depth + 1);
            }
            return ParsePrimary(depth);
        }

        private double ParsePrimary(int depth)
        {
            Guard(depth);
            SkipSpace();

            if (AtEnd)
                throw new FormulaException("The formula ends where a value was expected.");

            if (Current == '(')
            {
                _pos++;
                double inner = ParseExpression(depth + 1);
                SkipSpace();
                if (Current != ')')
                    throw new FormulaException($"A '(' at position {_pos} was never closed.");
                _pos++;
                return inner;
            }

            if (char.IsAsciiDigit(Current) || Current == '.')
                return ParseNumber();

            if (IsIdentifierStart(Current))
                return ParseName(depth);

            throw new FormulaException($"Unexpected '{Current}' at position {_pos + 1}.");
        }

        private double ParseNumber()
        {
            int start = _pos;
            while (_pos < _text.Length && (char.IsAsciiDigit(_text[_pos]) || _text[_pos] == '.'))
                _pos++;

            ReadOnlySpan<char> literal = _text.AsSpan(start, _pos - start);

            // InvariantCulture, always. The desktop culture decides what '.' means,
            // and on a comma-decimal machine the framework parse of "0.35" either
            // fails or -- worse, depending on the overload -- reads 35. Every
            // number in a WZ archive is invariant-formatted, so every number read
            // out of one has to be parsed that way; WzNodeFactory makes the same
            // choice on the way back in.
            if (!double.TryParse(literal, NumberStyles.Float, CultureInfo.InvariantCulture, out double value))
                throw new FormulaException($"'{literal.ToString()}' is not a number (position {start + 1}).");
            return value;
        }

        private double ParseName(int depth)
        {
            int start = _pos;
            while (_pos < _text.Length && IsIdentifierPart(_text[_pos]))
                _pos++;
            string name = _text[start.._pos];

            SkipSpace();
            if (Current != '(')
            {
                // 'x' is the level and always wins, so a block that happens to
                // define a key called 'x' cannot shadow it out from under the
                // rest of the table.
                if (name is "x" or "X")
                    return _x;

                // Everything else is a variable: a sibling key of the same
                // 'common' block, a value the user typed, or a free name the
                // client fills in and this editor has to be told about.
                if (_scope.TryResolve(name, _x, out double value, out string? why))
                    return value;

                throw new FormulaException(why ?? $"'{name}' has no value (position {start + 1}).");
            }

            _pos++;   // consume '('
            List<double> args = new(2);
            SkipSpace();
            if (Current == ')')
            {
                _pos++;
            }
            else
            {
                while (true)
                {
                    args.Add(ParseExpression(depth + 1));
                    SkipSpace();
                    if (Current == ',')
                    {
                        _pos++;
                        continue;
                    }
                    if (Current == ')')
                    {
                        _pos++;
                        break;
                    }
                    throw new FormulaException($"Expected ',' or ')' at position {_pos + 1}.");
                }
            }

            return Apply(name, args, start);
        }

        private static double Apply(string name, List<double> args, int at)
        {
            switch (name)
            {
                case "u" or "U":
                    Expect(name, args, 1, at);
                    return Math.Ceiling(args[0]);
                case "d" or "D":
                    Expect(name, args, 1, at);
                    return Math.Floor(args[0]);
                case "min":
                    Expect(name, args, 2, at);
                    return Math.Min(args[0], args[1]);
                case "max":
                    Expect(name, args, 2, at);
                    return Math.Max(args[0], args[1]);
                default:
                    // logN(v) -- log of v to base N, with the base written into the
                    // function name: "log70(x)*20" is genuine MapleStory and appears
                    // in clients this one does not resemble. The base is part of the
                    // identifier rather than an argument, so it cannot be a normal
                    // entry in the table above; anything matching log<digits> is
                    // handled here instead.
                    //
                    // Deliberately not restricted to bases seen in one client: the
                    // grammar is "log" followed by a number, and inventing a
                    // whitelist would just move the refusal somewhere less obvious.
                    if (name.Length > 3
                        && name.StartsWith("log", StringComparison.OrdinalIgnoreCase)
                        && double.TryParse(name.AsSpan(3), NumberStyles.Float,
                                           CultureInfo.InvariantCulture, out double logBase))
                    {
                        Expect(name, args, 1, at);
                        if (logBase <= 0 || Math.Abs(logBase - 1) < double.Epsilon)
                            throw new FormulaException(
                                $"'{name}' has a base of {logBase.ToString(CultureInfo.InvariantCulture)}, " +
                                $"which has no logarithm (position {at + 1}).");
                        if (args[0] <= 0)
                            throw new FormulaException(
                                $"'{name}' cannot take the log of {args[0].ToString(CultureInfo.InvariantCulture)} " +
                                $"(position {at + 1}). Only values above zero have one.");
                        return Math.Log(args[0], logBase);
                    }

                    throw new FormulaException(
                        $"'{name}' is not a function this understands (position {at + 1}). " +
                        $"Known functions: {string.Join(", ", Functions)} (a base written into the " +
                        "name, such as log70). " +
                        "The formula is still stored exactly as written and still saves — " +
                        "only the computed level values below cannot be worked out.");
            }
        }

        private static void Expect(string name, List<double> args, int count, int at)
        {
            if (args.Count != count)
                throw new FormulaException(
                    $"'{name}' takes {count} argument{(count == 1 ? "" : "s")} but was given {args.Count} (position {at + 1}).");
        }

        private static void Guard(int depth)
        {
            if (depth > MaxDepth)
                throw new FormulaException($"The formula nests more than {MaxDepth} levels deep.");
        }
    }
}
