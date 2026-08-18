using System.Globalization;
using MapleLib.WzLib;
using MapleLib.WzLib.WzProperties;

namespace MapleBench.Services;

/// <summary>
/// Creates and mutates WZ property values from the strings the browser sends.
/// All parsing is invariant-culture so a German locale doesn't turn "1.5" into 15.
/// </summary>
public static class WzNodeFactory
{
    /// <summary>Property types the UI offers when adding a child.</summary>
    public static readonly string[] CreatableProperties =
    {
        "Int", "Short", "Long", "Float", "Double", "String",
        "SubProperty", "Vector", "UOL", "Canvas", "Convex", "Null",
    };

    public static WzImageProperty Create(string type, string name, string? value)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("A name is required.");

        switch (type.ToLowerInvariant())
        {
            case "int":
                return new WzIntProperty(name, ParseInt(value));
            case "short":
                return new WzShortProperty(name, (short)ParseLong(value, short.MinValue, short.MaxValue));
            case "long":
                return new WzLongProperty(name, ParseLong(value, long.MinValue, long.MaxValue));
            case "float":
                return new WzFloatProperty(name, ParseFloat(value));
            case "double":
                return new WzDoubleProperty(name, ParseDouble(value));
            case "string":
                return new WzStringProperty(name, value ?? "");
            case "uol":
                return new WzUOLProperty(name, value ?? "");
            case "null":
                return new WzNullProperty(name);
            case "subproperty":
            case "sub":
                return new WzSubProperty(name);
            case "convex":
                return new WzConvexProperty(name);
            case "vector":
            {
                (int x, int y) = ParseVector(value);
                return new WzVectorProperty(name, x, y);
            }
            case "canvas":
            {
                WzCanvasProperty canvas = new(name);
                // A canvas with no PNG cannot be written back, so seed a 1x1.
                // WzPngProperty.SetValue compresses immediately, so the new
                // property needs no reader or parent link of its own.
                WzPngProperty png = new();
                using (System.Drawing.Bitmap blank = new(1, 1))
                    png.SetValue(blank);
                canvas.PngProperty = png;
                return canvas;
            }
            default:
                throw new ArgumentException(
                    $"'{type}' cannot be created. Supported: {string.Join(", ", CreatableProperties)}.");
        }
    }

    /// <summary>
    /// Writes a new value into an existing property, keeping its type.
    /// Returns the previous value rendered as a string, for undo.
    /// </summary>
    public static string? SetValue(WzImageProperty property, string? value)
    {
        switch (property)
        {
            case WzIntProperty p:
            {
                string old = p.Value.ToString(CultureInfo.InvariantCulture);
                p.Value = ParseInt(value);
                return old;
            }
            case WzShortProperty p:
            {
                string old = p.Value.ToString(CultureInfo.InvariantCulture);
                p.Value = (short)ParseLong(value, short.MinValue, short.MaxValue);
                return old;
            }
            case WzLongProperty p:
            {
                string old = p.Value.ToString(CultureInfo.InvariantCulture);
                p.Value = ParseLong(value, long.MinValue, long.MaxValue);
                return old;
            }
            case WzFloatProperty p:
            {
                string old = p.Value.ToString("R", CultureInfo.InvariantCulture);
                p.Value = ParseFloat(value);
                return old;
            }
            case WzDoubleProperty p:
            {
                string old = p.Value.ToString("R", CultureInfo.InvariantCulture);
                p.Value = ParseDouble(value);
                return old;
            }
            case WzStringProperty p:
            {
                string? old = p.Value;
                p.Value = value ?? "";
                return old;
            }
            case WzUOLProperty p:
            {
                string? old = p.Value;
                p.Value = value ?? "";
                return old;
            }
            case WzVectorProperty p:
            {
                string old = $"{p.X?.Value ?? 0}, {p.Y?.Value ?? 0}";
                (int x, int y) = ParseVector(value);
                // Mutates the existing X/Y in place; assigning fresh WzIntProperty
                // instances is not possible from outside MapleLib because the
                // Parent setter is internal.
                p.SetValue(new System.Drawing.Point(x, y));
                return old;
            }
            case WzNullProperty:
                // Nothing to store; accepted so bulk edits don't hard-fail.
                return null;
            default:
                throw new InvalidOperationException(
                    $"'{property.PropertyType}' values are not editable as text. " +
                    "Use the image or binary replace action instead.");
        }
    }

    private static int ParseInt(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return 0;
        if (int.TryParse(value.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int result))
            return result;
        // Accept values that overflow int but were clearly meant as numbers.
        if (long.TryParse(value.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out long wide))
            throw new ArgumentException($"{wide} does not fit in an Int property (max {int.MaxValue}). Use a Long.");
        throw new ArgumentException($"'{value}' is not a whole number.");
    }

    private static long ParseLong(string? value, long min, long max)
    {
        if (string.IsNullOrWhiteSpace(value))
            return 0;
        if (!long.TryParse(value.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out long result))
            throw new ArgumentException($"'{value}' is not a whole number.");
        if (result < min || result > max)
            throw new ArgumentException($"{result} is out of range ({min} to {max}).");
        return result;
    }

    private static float ParseFloat(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return 0f;
        if (!float.TryParse(value.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out float result))
            throw new ArgumentException($"'{value}' is not a number.");
        return result;
    }

    private static double ParseDouble(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return 0d;
        if (!double.TryParse(value.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out double result))
            throw new ArgumentException($"'{value}' is not a number.");
        return result;
    }

    /// <summary>Accepts "12, 34", "12 34" and "(12,34)".</summary>
    private static (int X, int Y) ParseVector(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return (0, 0);

        string cleaned = value.Trim().Trim('(', ')');
        string[] parts = cleaned.Split(new[] { ',', ' ', ';' }, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 2
            || !int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out int x)
            || !int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int y))
        {
            throw new ArgumentException($"'{value}' is not a vector. Use the form 'x, y'.");
        }
        return (x, y);
    }

    /// <summary>
    /// The property collection of any container node, or null for leaves.
    /// WzImage, WzSubProperty, WzConvexProperty and WzCanvasProperty all qualify.
    /// </summary>
    public static WzPropertyCollection? GetPropertyCollection(WzObject node) => node switch
    {
        WzImage image => image.WzProperties,
        WzCanvasProperty canvas => canvas.WzProperties,
        WzSubProperty sub => sub.WzProperties,
        WzConvexProperty convex => convex.WzProperties,
        _ => null,
    };

    /// <summary>
    /// Marks the WzImage owning <paramref name="node"/> as changed so it gets
    /// re-serialised on save.
    ///
    /// The parse must happen first: <c>WzImage.ParseImage</c> short-circuits when
    /// <c>Changed</c> is already set, so flagging an unparsed image dirty and
    /// then saving would write it out with zero properties.
    /// </summary>
    public static void MarkChanged(WzObject node)
    {
        WzImage? image = node as WzImage ?? (node as WzImageProperty)?.ParentImage;
        if (image == null)
            return;

        // The parse result is checked, not discarded. ParseImage returns false
        // without throwing for a header it does not recognise, leaving the image
        // with zero properties -- and setting Changed on that state is what made
        // SaveImage write the image out empty. SaveImage refuses now, but by then
        // the user has been told an edit landed on an image that cannot hold it,
        // and the failure surfaces at save time instead of at edit time.
        if (!image.Parsed && !image.ParseImage())
        {
            throw new InvalidOperationException(
                $"'{image.Name}' could not be read, so it cannot be edited. " +
                "Its contents are intact on disk; nothing was changed.");
        }
        image.Changed = true;

        // The other half of the invalidation, and the half that is easy to miss.
        // InvalidateResolution covers every edit that changes which node a path
        // names; this covers the edits that change what a node CONTAINS while its
        // shape stays put -- SetValue, SetCanvasImage, and every undo/redo closure
        // that puts pixels back. Those move the content hash and move no path, so
        // nothing else would drop the digest.
        WzContentHasher.ClearCache();
    }
}
