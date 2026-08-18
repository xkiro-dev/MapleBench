using System.IO.Compression;
using System.Text;
using System.Text.Json;
using MapleLib.WzLib;
using MapleLib.WzLib.Serializer;

namespace MapleBench.Services;

/// <summary>
/// Classic <c>&lt;imgdir&gt;</c> XML export — the half of the edit loop that
/// reaches a server rather than a client.
///
/// v83 emulators (HeavenMS, Cosmic) do not read WZ archives at all; they read a
/// directory tree of XML dumped out of them.  So an edit saved back into
/// Etc.wz changes what the player sees and nothing the server believes, and the
/// two only agree again once the same tree has been written out here.
///
/// Sprite data is deliberately dropped (<c>exportBase64: false</c>): a server
/// never looks at a canvas, and base64 PNGs would turn a dump that is tens of
/// megabytes into one that is gigabytes.
/// </summary>
public sealed class XmlExportService
{
    /// <summary>
    /// Map.wz is ~1.5 GB across tens of thousands of images, and every one of
    /// them has to be parsed, written to disk and compressed before the first
    /// byte of the response exists.  A cap keeps a mistaken click on the wrong
    /// node from becoming a ten-minute unkillable request; the manifest says
    /// so when it bites.
    /// </summary>
    public const int DefaultMaxImages = 2000;
    public const int MaxImagesCeiling = 20000;

    /// <summary>
    /// A second bound, because image count alone does not predict size: a few
    /// hundred Map.wz images can outweigh every .img in Etc.wz put together.
    /// </summary>
    private const long MaxBytes = 512L * 1024 * 1024;

    private readonly WzSessionService _session;
    private readonly ILogger<XmlExportService> _log;

    public XmlExportService(WzSessionService session, ILogger<XmlExportService> log)
    {
        _session = session;
        _log = log;
    }

    /// <summary>
    /// One .img as a single XML document, named the way a server's dump names
    /// it (<c>Commodity.img.xml</c>) so it can be dropped straight in.
    /// </summary>
    public (byte[] Data, string FileName) ExportImage(string path)
    {
        string temp = NewTempDirectory();
        try
        {
            lock (_session.Gate)
            {
                WzObject node = _session.Resolve(path);
                if (node is not WzImage image)
                {
                    throw new InvalidOperationException(
                        "Only an .img can be exported as a single XML file. " +
                        "Use the ZIP export for a directory or a whole archive.");
                }

                string name = XmlFileName(image.Name);
                string file = Path.Combine(temp, name);
                NewSerializer().SerializeImage(image, file);
                return (File.ReadAllBytes(file), name);
            }
        }
        finally
        {
            TryDeleteDirectory(temp);
        }
    }

    /// <summary>
    /// Every .img beneath a directory or archive as XML in a ZIP, mirroring the
    /// WZ hierarchy so unzipping it over a server's <c>wz/</c> folder lands each
    /// file where the emulator already looks for it.
    /// </summary>
    public (byte[] Data, string FileName, int Images, bool Truncated) ExportTree(string path, int maxImages)
    {
        string temp = NewTempDirectory();
        try
        {
            Walk walk = new() { TempRoot = temp, MaxImages = maxImages };
            string rootName;

            lock (_session.Gate)
            {
                WzObject root = _session.Resolve(path);
                rootName = root.Name ?? "export";
                WriteTree(root, "", walk);
            }

            if (walk.Written.Count == 0)
                throw new InvalidOperationException("There are no images under this node.");

            // Compressing reads only files this call just wrote, so it happens
            // outside the gate: that lock is global and every other request —
            // including the tree the user is still clicking around in — queues
            // behind it.
            MemoryStream buffer = new();
            using (ZipArchive zip = new(buffer, ZipArchiveMode.Create, leaveOpen: true))
            {
                foreach ((string entryName, string filePath) in walk.Written)
                    zip.CreateEntryFromFile(filePath, entryName, CompressionLevel.Fastest);

                ZipArchiveEntry manifestEntry = zip.CreateEntry("manifest.json", CompressionLevel.Optimal);
                using Stream stream = manifestEntry.Open();
                JsonSerializer.Serialize(stream, new
                {
                    source = Uri.UnescapeDataString(path),
                    exported = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
                    count = walk.Written.Count,
                    truncated = walk.Truncated,
                    files = walk.Written.Select(w => w.EntryName),
                }, new JsonSerializerOptions { WriteIndented = true });
            }

            return (buffer.ToArray(), Sanitise(rootName) + "-xml.zip", walk.Written.Count, walk.Truncated);
        }
        finally
        {
            TryDeleteDirectory(temp);
        }
    }

    /// <summary>
    /// Depth-first over directories, writing each image to its mirrored place
    /// under the temp root.  Stops at either cap and records that it did.
    /// </summary>
    private void WriteTree(WzObject node, string relative, Walk walk)
    {
        if (walk.Written.Count >= walk.MaxImages || walk.Bytes >= MaxBytes)
        {
            walk.Truncated = true;
            return;
        }

        switch (node)
        {
            case WzImage image:
                WriteImage(image, relative, walk);
                break;

            case WzDirectory dir:
                foreach (WzDirectory sub in dir.WzDirectories)
                {
                    WriteTree(sub, Combine(relative, Sanitise(sub.Name)), walk);
                    if (walk.Truncated)
                        return;
                }
                foreach (WzImage image in dir.WzImages)
                {
                    WriteTree(image, relative, walk);
                    if (walk.Truncated)
                        return;
                }
                break;

            default:
                throw new InvalidOperationException(
                    "Only an archive, a directory or an .img can be exported as server XML.");
        }
    }

    private void WriteImage(WzImage image, string relative, Walk walk)
    {
        string entryName = Combine(relative, XmlFileName(image.Name));
        string file = Path.Combine(walk.TempRoot, entryName.Replace('/', Path.DirectorySeparatorChar));

        try
        {
            // The serialiser un-parses images it had to parse itself, which is
            // what keeps a whole-archive dump from holding the whole archive in
            // memory.  It is safe against the undo history because an image with
            // pending edits reports Changed, which counts as already parsed and
            // is therefore left alone.
            NewSerializer().SerializeImage(image, file);
        }
        catch (Exception ex)
        {
            // One image nobody can decrypt must not cost the other 1,999.
            _log.LogDebug(ex, "Skipping {Image} during XML export", image.Name);
            TryDelete(file);
            return;
        }

        long bytes = 0;
        try { bytes = new FileInfo(file).Length; } catch { /* counted as free */ }

        walk.Bytes += bytes;
        walk.Written.Add((entryName, file));
    }

    /// <summary>
    /// Indented with real line breaks rather than HaRepacker's one-line default:
    /// these dumps get diffed, grepped and hand-patched, and a 40 MB single line
    /// is none of those things.
    /// </summary>
    private static WzClassicXmlSerializer NewSerializer() =>
        new(2, LineBreak.Windows, exportbase64: false);

    /// <summary>"Commodity.img" -> "Commodity.img.xml", the name a server dump uses.</summary>
    private static string XmlFileName(string? name)
    {
        string cleaned = Sanitise(name ?? "image");
        return cleaned.EndsWith(".xml", StringComparison.OrdinalIgnoreCase) ? cleaned : cleaned + ".xml";
    }

    private static string Combine(string relative, string name) =>
        relative.Length == 0 ? name : relative + "/" + name;

    /// <summary>
    /// Shared with <see cref="ExportService"/> rather than reimplemented.
    ///
    /// This one filtered invalid characters only, which leaves ".." intact — and
    /// unlike the image export, these names reach a real <c>Path.Combine</c>
    /// against the temp root before they reach the ZIP, so a node called ".."
    /// wrote outside the temp directory on the server as well as escaping the
    /// user's extraction directory.
    /// </summary>
    private static string Sanitise(string? segment)
        => ExportService.Sanitise(segment ?? "");

    /// <summary>
    /// MapleLib's serialisers write to disk paths, not streams, so a dump has to
    /// touch the filesystem before it can become a response body.
    /// </summary>
    private static string NewTempDirectory()
    {
        string path = Path.Combine(Path.GetTempPath(), "MapleBench", "xml-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static void TryDeleteDirectory(string path)
    {
        try { Directory.Delete(path, recursive: true); } catch { /* best effort */ }
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); } catch { /* best effort */ }
    }

    /// <summary>State for one tree export: what has been written, and the caps.</summary>
    private sealed class Walk
    {
        public required string TempRoot { get; init; }
        public required int MaxImages { get; init; }
        public List<(string EntryName, string FilePath)> Written { get; } = new();
        public long Bytes;
        public bool Truncated;
    }
}
