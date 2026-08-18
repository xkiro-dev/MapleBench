using MapleLib.WzLib.WzProperties;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace MapleLib.WzLib
{
    /// <summary>
    /// Service for resolving _inlink and _outlink canvas references in WZ files.
    /// Resolves links in-place by embedding the actual bitmap data.
    /// </summary>
    public class WzLinkResolver
    {
        #region Properties
        /// <summary>
        /// Number of links successfully resolved
        /// </summary>
        public int LinksResolved { get; private set; }

        /// <summary>
        /// Number of links that failed to resolve
        /// </summary>
        public int LinksFailed { get; private set; }

        /// <summary>
        /// List of failed link paths for debugging
        /// </summary>
        public List<string> FailedLinks { get; } = new List<string>();
        #endregion

        #region Fields
        /// <summary>
        /// All loaded WZ files for the current category, used for cross-file outlink resolution
        /// </summary>
        private readonly List<WzFile> _categoryWzFiles = new List<WzFile>();

        /// <summary>
        /// Category name (e.g., "Mob", "Npc") for path matching
        /// </summary>
        private string _categoryName;

        /// <summary>
        /// Temporary storage for all images with a matching name during one
        /// outlink resolution -- every one of them, because which of several
        /// same-named images backs a link is decided by content, not position.
        /// </summary>
        private readonly List<WzImage> _currentCanvasImages = new List<WzImage>();

        /// <summary>
        /// Why the last outlink was REFUSED rather than merely unresolved, when it
        /// was: several same-named candidates offered different content and nothing
        /// identified which one the link means. Folded into the corresponding
        /// <see cref="FailedLinks"/> entry so the ambiguity is reported with both
        /// candidates named, instead of a silent coin flip on file order.
        /// </summary>
        private string _outlinkRefusalDetail;

        /// <summary>
        /// Why the last outlink merely MISSED, when it did: the family did not
        /// match, no archive held the image, the exact address held nothing.
        /// Kept apart from <see cref="_outlinkRefusalDetail"/> because a refusal
        /// is a decision ("two candidates, nothing distinguishes them") and a
        /// miss is an absence, and a repair driven by this resolver reports the
        /// two differently.
        /// </summary>
        private string _outlinkMissDetail;

        /// <summary>
        /// When set, only the EXACT property address counts — the progressive
        /// fallback strategies in <see cref="ResolvePropertyInImage"/> are
        /// disabled. They are documented bulk-extraction conveniences that
        /// resolve SOMETHING, not necessarily the right thing; a caller that is
        /// about to write pixels into an archive must never accept a guess.
        /// </summary>
        private bool _exactAddressOnly;

        /// <summary>The candidate the last successful outlink resolution chose.</summary>
        private OutlinkCandidate _lastChosen;

        /// <summary>
        /// When set, <see cref="TryResolveOutlink"/> stops after CHOOSING and
        /// writes nothing — the scan half of a repair, which must be able to
        /// report exactly what an apply would do without doing it.
        /// </summary>
        private bool _peekOnly;

        /// <summary>
        /// Newer clients store a '_hash' string beside a canvas describing its
        /// pixels; a link-placeholder canvas carries the SAME stamp as the picture
        /// it references (measured: v232's inline 521x394 frame and v233's 1x1
        /// placeholder for it carry an identical '_hash'). That makes it the one
        /// signal that can say which of several same-named canvases a link means.
        /// </summary>
        private const string HashPropertyName = "_hash";
        #endregion

        #region Public Methods
        /// <summary>
        /// Sets the WZ files for the current category to enable cross-file outlink resolution.
        /// Call this before resolving links in images.
        /// </summary>
        /// <param name="wzFiles">All WZ files for this category (e.g., Mob.wz, Mob001.wz, Mob2.wz)</param>
        /// <param name="categoryName">Category name (e.g., "Mob")</param>
        public void SetCategoryWzFiles(IEnumerable<WzFile> wzFiles, string categoryName)
        {
            _categoryWzFiles.Clear();
            if (wzFiles != null)
                _categoryWzFiles.AddRange(wzFiles);
            _categoryName = categoryName;
        }

        /// <summary>
        /// Resolves a single canvas property's _inlink/_outlink reference.
        /// Modifies the canvas in-place by embedding the linked image data and removing the link property.
        /// Uses direct compressed byte copy for efficiency (avoids bitmap decompression/recompression).
        /// </summary>
        /// <param name="canvas">The canvas property to resolve</param>
        /// <param name="inlinkOnly">If true, only resolve _inlink (faster, doesn't load external files)</param>
        /// <returns>True if a link was resolved, false if no link or resolution failed</returns>
        public static bool ResolveSingleCanvas(WzCanvasProperty canvas, bool inlinkOnly = false)
        {
            if (canvas == null)
                return false;

            bool hasInlink = canvas.ContainsInlinkProperty();
            bool hasOutlink = canvas.ContainsOutlinkProperty();

            if (!hasInlink && !hasOutlink)
                return false;

            // Skip _outlink if inlinkOnly is set (outlink requires loading external WZ files)
            if (inlinkOnly && !hasInlink)
                return false;

            try
            {
                // Get the linked target canvas
                WzImageProperty linkedTarget = canvas.GetLinkedWzImageProperty();

                // If resolution succeeded (returns different object than self)
                if (linkedTarget != null && linkedTarget != canvas && linkedTarget is WzCanvasProperty linkedCanvas)
                {
                    return CopyCanvasData(canvas, linkedCanvas, hasInlink, hasOutlink);
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[WzLinkResolver] Exception resolving canvas link: {ex.Message}");
            }

            return false;
        }

        /// <summary>
        /// Resolves all _inlink/_outlink references in a WzImage.
        /// Modifies the image in-place before serialization.
        /// </summary>
        /// <param name="image">The WzImage to process</param>
        /// <returns>Number of links resolved in this image</returns>
        public int ResolveLinksInImage(WzImage image)
        {
            if (image == null)
                return 0;

            // Parse image if not already parsed
            if (!image.Parsed)
            {
                image.ParseImage();
            }

            int resolvedCount = 0;

            // Recursively process all properties
            string imagePath = image.FullPath ?? image.Name;
            foreach (WzImageProperty prop in image.WzProperties)
            {
                resolvedCount += ResolveLinksInProperty(prop, imagePath);
            }

            if (resolvedCount > 0)
            {
                // Force WzImage.SaveImage to serialize the mutated property tree instead of
                // copying the original raw IMG bytes from the source reader.
                image.Changed = true;
            }

            return resolvedCount;
        }

        /// <summary>
        /// Resets the resolver statistics for a new extraction session
        /// </summary>
        public void Reset()
        {
            LinksResolved = 0;
            LinksFailed = 0;
            FailedLinks.Clear();
        }

        /// <summary>
        /// Resolves ONE canvas's <c>_outlink</c> in place — the linked picture's
        /// portable compressed bytes are written into the canvas and the
        /// <c>_outlink</c> property is dropped — accepting only the EXACT
        /// property address. The fallback strategies never run: they are
        /// bulk-extraction conveniences that resolve SOMETHING, and a caller
        /// writing pixels into an archive must never accept a guess.
        ///
        /// Candidates are still pooled from every archive of the category and
        /// chosen by <see cref="ChooseByContentIdentity"/>, so an ambiguous
        /// address or a shells-only address is REFUSED with the candidates
        /// named, exactly as the bulk path refuses. A failure files an entry in
        /// <see cref="FailedLinks"/> carrying that reason.
        /// </summary>
        /// <returns>True when the pixels landed and the _outlink is gone.</returns>
        public bool ResolveOutlinkExactInPlace(WzCanvasProperty canvas, string logPath)
        {
            if (canvas == null || !canvas.ContainsOutlinkProperty())
                return false;

            string value = (canvas[WzCanvasProperty.OutlinkPropertyName] as WzStringProperty)?.Value;
            _exactAddressOnly = true;
            try
            {
                if (TryResolveOutlink(canvas, value, logPath))
                {
                    LinksResolved++;
                    return true;
                }

                LinksFailed++;
                string entry = $"{logPath} (_outlink: {value})";
                string detail = _outlinkRefusalDetail ?? _outlinkMissDetail;
                if (!string.IsNullOrEmpty(detail))
                    entry += $" -- {detail}";
                FailedLinks.Add(entry);
                return false;
            }
            finally
            {
                _exactAddressOnly = false;
            }
        }

        /// <summary>
        /// Chooses the single canvas identity allows for one <c>_outlink</c>,
        /// WITHOUT writing anything — the scan half of a repair. Same rules as
        /// <see cref="ResolveOutlinkExactInPlace"/>: exact address only,
        /// shells and ambiguity refused, never a guess.
        /// </summary>
        /// <param name="canvas">The canvas carrying the _outlink.</param>
        /// <param name="portableBytes">The chosen picture's compressed bytes in
        /// portable (plain zlib) form — already converted under the source
        /// archive's own key, so they survive being carried anywhere.</param>
        /// <param name="failureDetail">Why nothing was chosen, when nothing was:
        /// a refusal names the candidates, a miss names what was absent.</param>
        /// <returns>The canvas the link means, or null.</returns>
        public WzCanvasProperty PeekOutlinkExact(WzCanvasProperty canvas, out byte[] portableBytes, out string failureDetail)
        {
            portableBytes = null;
            failureDetail = null;
            if (canvas == null || !canvas.ContainsOutlinkProperty())
            {
                failureDetail = "the canvas carries no _outlink at all.";
                return null;
            }

            string value = (canvas[WzCanvasProperty.OutlinkPropertyName] as WzStringProperty)?.Value;
            _exactAddressOnly = true;
            _peekOnly = true;
            try
            {
                if (TryResolveOutlink(canvas, value, canvas.FullPath ?? canvas.Name) && _lastChosen != null)
                {
                    portableBytes = _lastChosen.CompressedBytes;
                    return _lastChosen.Canvas;
                }

                failureDetail = _outlinkRefusalDetail ?? _outlinkMissDetail
                    ?? "the link did not resolve, and the resolver recorded no reason.";
                return null;
            }
            finally
            {
                _exactAddressOnly = false;
                _peekOnly = false;
            }
        }

        /// <summary>
        /// Whether the last failed outlink was a REFUSAL — a decision made on
        /// named candidates — rather than a plain miss.
        /// </summary>
        public bool LastOutlinkFailureWasRefusal => !string.IsNullOrEmpty(_outlinkRefusalDetail);
        #endregion

        #region Private Methods
        /// <summary>
        /// Copies canvas data from source to destination
        /// </summary>
        private static bool CopyCanvasData(WzCanvasProperty destCanvas, WzCanvasProperty srcCanvas, bool hasInlink, bool hasOutlink)
        {
            // Copy the compressed image data directly (avoids bitmap decompression/recompression)
            WzPngProperty sourcePng = srcCanvas.PngProperty;
            if (sourcePng == null)
                return false;

            // Use GetCompressedBytesForExtraction to convert listWz format to standard zlib format.
            // This is critical because SetCompressedBytes clears the wzReader reference,
            // so we must convert while the source still has access to the WzKey.
            byte[] compressedBytes = sourcePng.GetCompressedBytesForExtraction(false);
            return CopyCanvasData(destCanvas, srcCanvas, compressedBytes, hasInlink, hasOutlink);
        }

        /// <summary>
        /// Copies canvas data from source to destination, with the source's portable
        /// compressed bytes already in hand -- the outlink path reads them once to
        /// vet and compare candidates, and this overload keeps that read from being
        /// paid a second time for the winner.
        /// </summary>
        private static bool CopyCanvasData(WzCanvasProperty destCanvas, WzCanvasProperty srcCanvas, byte[] compressedBytes, bool hasInlink, bool hasOutlink)
        {
            WzPngProperty sourcePng = srcCanvas.PngProperty;
            WzPngProperty destPng = destCanvas.PngProperty;

            if (sourcePng == null || destPng == null)
                return false;

            if (compressedBytes == null || compressedBytes.Length == 0)
                return false;

            destPng.SetCompressedBytes(compressedBytes, sourcePng.Width, sourcePng.Height, sourcePng.Format);

            // Remove the link property
            if (hasInlink)
            {
                destCanvas.RemoveProperty(WzCanvasProperty.InlinkPropertyName);
            }
            if (hasOutlink)
            {
                destCanvas.RemoveProperty(WzCanvasProperty.OutlinkPropertyName);
            }
            return true;
        }

        /// <summary>
        /// Recursively resolves links in a property and its children.
        /// Optimized to skip property types that cannot contain canvas properties.
        /// </summary>
        private int ResolveLinksInProperty(WzImageProperty property, string parentPath)
        {
            if (property == null)
                return 0;

            // Early exit for property types that cannot contain canvas children
            // This is critical for performance - String.wz has thousands of string properties
            // that we don't need to traverse
            switch (property.PropertyType)
            {
                case WzPropertyType.String:
                case WzPropertyType.Short:
                case WzPropertyType.Int:
                case WzPropertyType.Long:
                case WzPropertyType.Float:
                case WzPropertyType.Double:
                case WzPropertyType.Sound:
                case WzPropertyType.Null:
                case WzPropertyType.PNG:
                case WzPropertyType.UOL:
                    return 0;
            }

            int resolvedCount = 0;
            string currentPath = $"{parentPath}/{property.Name}";

            // If this is a canvas property, check for links
            if (property is WzCanvasProperty canvas)
            {
                if (TryResolveCanvasLink(canvas, currentPath))
                {
                    resolvedCount++;
                }
            }

            // Recursively process child properties (only for container types)
            var children = property.WzProperties;
            if (children != null && children.Count > 0)
            {
                foreach (WzImageProperty child in children)
                {
                    resolvedCount += ResolveLinksInProperty(child, currentPath);
                }
            }

            return resolvedCount;
        }

        /// <summary>
        /// Attempts to resolve an _inlink or _outlink in a canvas property
        /// </summary>
        /// <param name="canvas">The canvas property to resolve</param>
        /// <param name="path">The path for logging purposes</param>
        /// <returns>True if a link was resolved, false otherwise</returns>
        private bool TryResolveCanvasLink(WzCanvasProperty canvas, string path)
        {
            bool hasInlink = canvas.ContainsInlinkProperty();
            bool hasOutlink = canvas.ContainsOutlinkProperty();

            if (!hasInlink && !hasOutlink)
                return false;

            string linkType = hasInlink ? "_inlink" : "_outlink";
            string linkValue = hasInlink
                ? ((WzStringProperty)canvas[WzCanvasProperty.InlinkPropertyName])?.Value ?? "unknown"
                : ((WzStringProperty)canvas[WzCanvasProperty.OutlinkPropertyName])?.Value ?? "unknown";

            // Try to resolve _inlink first (within same image)
            if (hasInlink)
            {
                if (ResolveSingleCanvas(canvas, inlinkOnly: true))
                {
                    LinksResolved++;
                    return true;
                }
                else
                {
                    // Inlink resolution failed
                    LinksFailed++;
                    FailedLinks.Add($"{path} ({linkType}: {linkValue})");
                    Debug.WriteLine($"[WzLinkResolver] Failed to resolve {linkType} at {path} -> {linkValue}");
                    return false;
                }
            }

            // Try to resolve _outlink across all loaded WZ files
            if (hasOutlink)
            {
                if (TryResolveOutlink(canvas, linkValue, path))
                {
                    LinksResolved++;
                    return true;
                }
                else
                {
                    // Outlink resolution failed -- and when it was a refusal rather
                    // than a miss, the entry says so and names the candidates.
                    LinksFailed++;
                    string entry = $"{path} ({linkType}: {linkValue})";
                    if (!string.IsNullOrEmpty(_outlinkRefusalDetail))
                        entry += $" -- {_outlinkRefusalDetail}";
                    FailedLinks.Add(entry);
                    Debug.WriteLine($"[WzLinkResolver] Failed to resolve {linkType} at {path} -> {linkValue}");
                    return false;
                }
            }

            return false;
        }

        /// <summary>
        /// Tries to resolve an outlink by searching across all loaded WZ files for the category
        /// </summary>
        /// <param name="canvas">The canvas with the outlink</param>
        /// <param name="outlinkPath">The outlink path (e.g., "Mob/8800141.img/attack1/0" or "Map/Back/_Canvas/snowyDarkrock.img/back/0")</param>
        /// <param name="logPath">Path for logging</param>
        /// <returns>True if resolved successfully</returns>
        private bool TryResolveOutlink(WzCanvasProperty canvas, string outlinkPath, string logPath)
        {
            _outlinkRefusalDetail = null;
            _outlinkMissDetail = null;
            _lastChosen = null;

            if (string.IsNullOrEmpty(outlinkPath) || _categoryWzFiles == null || _categoryWzFiles.Count == 0)
            {
                _outlinkMissDetail = "no archives are loaded for the category, so there is nothing to look in.";
                return false;
            }

            try
            {
                // Parse the outlink path
                // Format: "Category/[Subdirs/]ImageName.img/property/path"
                // e.g., "Mob/8800141.img/attack1/0" or "Item/Consume/0243.img/123/info"
                // For _Canvas: "Map/Back/_Canvas/snowyDarkrock.img/back/0" or "Map/_Canvas/MapHelper.img/mark/Hilla"
                string[] parts = outlinkPath.Split(new[] { '/' }, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length < 2)
                {
                    _outlinkMissDetail = "the link is too short to name an image at all.";
                    return false;
                }

                string linkCategory = parts[0]; // e.g., "Mob" or "Map"

                // Check if this outlink is within our category (we can only resolve same-category outlinks)
                if (!string.Equals(linkCategory, _categoryName, StringComparison.OrdinalIgnoreCase))
                {
                    _outlinkMissDetail = $"the link points into the '{linkCategory}' family and this resolver holds '{_categoryName}'.";
                    Debug.WriteLine($"[WzLinkResolver] Outlink to different category '{linkCategory}' cannot be resolved (current: {_categoryName})");
                    return false;
                }

                // Check if this is a _Canvas path
                bool isCanvasPath = outlinkPath.Contains("/_Canvas/", StringComparison.OrdinalIgnoreCase) ||
                                    outlinkPath.Contains("_Canvas/", StringComparison.OrdinalIgnoreCase);

                // Find the image name - look for .img in the path
                int imgIndex = -1;
                for (int i = 1; i < parts.Length; i++)
                {
                    if (parts[i].EndsWith(".img", StringComparison.OrdinalIgnoreCase))
                    {
                        imgIndex = i;
                        break;
                    }
                }

                if (imgIndex < 0)
                {
                    _outlinkMissDetail = "the link names no .img anywhere along its path.";
                    return false;
                }

                // Build subdirectory path (parts between category and image)
                // e.g., for "Item/Consume/0243.img", subdirPath = "Consume"
                // e.g., for "Map/Back/_Canvas/snowyDarkrock.img", subdirPath = "Back/_Canvas"
                string subdirPath = imgIndex > 1
                    ? string.Join("/", parts.Skip(1).Take(imgIndex - 1))
                    : null;

                string imageName = parts[imgIndex]; // e.g., "0243.img" or "snowyDarkrock.img"

                // Build the property path within the image
                string propertyPath = imgIndex + 1 < parts.Length
                    ? string.Join("/", parts.Skip(imgIndex + 1))
                    : null;

                // Collect EVERY image whose name matches, from every archive that can
                // hold it. The name decides nothing on its own: several archives of one
                // family really do carry the same image name with different content --
                // 40000.img in Skill.wz and Skill003.wz, and the same _Canvas image
                // name across several _Canvas_xxx.wz shards with different frames --
                // so which image backs this link is decided by CONTENT further down,
                // never by which file happened to be enumerated first.
                _currentCanvasImages.Clear();

                // For _Canvas paths, we need to search specifically in the _Canvas WZ files
                // The _Canvas WZ files contain the images directly at root level
                if (isCanvasPath)
                {
                    string targetCanvasFolderPath = NormalizePath(subdirPath);

                    // Extract the path after "_Canvas/" marker
                    // e.g., "Map/Back/_Canvas/snowyDarkrock.img/back/0" -> "snowyDarkrock.img/back/0"
                    string canvasMarker = "/_Canvas/";
                    int canvasMarkerIndex = outlinkPath.IndexOf(canvasMarker, StringComparison.OrdinalIgnoreCase);
                    string pathAfterCanvas = canvasMarkerIndex >= 0
                        ? outlinkPath.Substring(canvasMarkerIndex + canvasMarker.Length)
                        : null;

                    if (!string.IsNullOrEmpty(pathAfterCanvas))
                    {
                        // Parse the path after _Canvas to get image name and property path
                        string[] canvasParts = pathAfterCanvas.Split(new[] { '/' }, StringSplitOptions.RemoveEmptyEntries);
                        if (canvasParts.Length >= 1)
                        {
                            // First part should be the image name (e.g., "snowyDarkrock.img")
                            string canvasImageName = canvasParts[0];
                            if (canvasImageName.EndsWith(".img", StringComparison.OrdinalIgnoreCase))
                            {
                                imageName = canvasImageName;
                                // Property path is everything after the image name
                                propertyPath = canvasParts.Length > 1
                                    ? string.Join("/", canvasParts.Skip(1))
                                    : null;
                            }
                        }
                    }

                    foreach (var wzFile in _categoryWzFiles)
                    {
                        bool isCanvasWzFile = wzFile.FilePath?.Contains("_Canvas", StringComparison.OrdinalIgnoreCase) == true ||
                                              wzFile.Name?.Contains("_Canvas", StringComparison.OrdinalIgnoreCase) == true;

                        if (isCanvasWzFile && wzFile.WzDirectory != null)
                        {
                            string wzFilePath = NormalizePath(wzFile.FilePath ?? wzFile.Name);
                            if (!MatchesCanvasFolder(wzFilePath, _categoryName, targetCanvasFolderPath))
                            {
                                continue;
                            }

                            CollectImagesByName(wzFile.WzDirectory, imageName, _currentCanvasImages);
                        }
                    }

                    // The OTHER real layout of _Canvas art: a _Canvas DIRECTORY
                    // inside the family's own archives, rather than separate
                    // _Canvas_xxx.wz shards on disk. Measured on the v232 client:
                    // 'Character/Weapon/_Canvas/…' and 'Skill/_Canvas/50000.img'
                    // both live as directories of the mounted family archives,
                    // and the shard-only reading above finds nothing for them.
                    // The subdirectory path is walked exactly as the link writes
                    // it — including the '_Canvas' segment — from each archive's
                    // root, and whatever answers joins the same candidate pool:
                    // which layout backs the link is decided by content identity
                    // below, never by which layout was probed first.
                    if (!string.IsNullOrEmpty(subdirPath))
                    {
                        foreach (var wzFile in _categoryWzFiles)
                        {
                            if (wzFile?.WzDirectory == null)
                                continue;

                            var insideDirectory = FindImageInDirectory(wzFile.WzDirectory, imageName, subdirPath);
                            if (insideDirectory != null && !_currentCanvasImages.Contains(insideDirectory))
                                _currentCanvasImages.Add(insideDirectory);
                        }
                    }
                }
                else
                {
                    // Non-canvas paths -- every archive of the family is a candidate
                    // holder, not just the first one that answers to the name.
                    foreach (var wzFile in _categoryWzFiles)
                    {
                        if (wzFile?.WzDirectory == null)
                            continue;

                        if (!string.IsNullOrEmpty(subdirPath))
                        {
                            var img = FindImageInDirectory(wzFile.WzDirectory, imageName, subdirPath);
                            if (img != null && !_currentCanvasImages.Contains(img))
                                _currentCanvasImages.Add(img);
                        }
                        else
                        {
                            CollectImagesByName(wzFile.WzDirectory, imageName, _currentCanvasImages);
                        }
                    }
                }

                if (_currentCanvasImages.Count == 0)
                {
                    string fullImagePath = string.IsNullOrEmpty(subdirPath) ? imageName : $"{subdirPath}/{imageName}";
                    _outlinkMissDetail = $"no loaded archive of the {_categoryName} family holds an image named '{fullImagePath}'.";
                    Debug.WriteLine($"[WzLinkResolver] Could not find image '{fullImagePath}' in any loaded WZ file (isCanvas: {isCanvasPath})");
                    return false;
                }

                if (string.IsNullOrEmpty(propertyPath))
                {
                    _outlinkMissDetail = $"the link names the image '{imageName}' and nothing inside it, so there is no canvas to take pixels from.";
                    Debug.WriteLine($"[WzLinkResolver] Outlink has no property path, only image: {imageName}");
                    return false;
                }

                // Resolve the property path inside every name-matched image FIRST, and
                // only then decide whose pixels to copy. A shard that only holds an
                // empty placeholder for this name is not a candidate -- that is the
                // "frames split across shards" layout, and the shard actually holding
                // the frame is the one the link means.
                List<OutlinkCandidate> candidates = new List<OutlinkCandidate>();
                List<WzCanvasProperty> shells = null;
                foreach (var searchImage in _currentCanvasImages)
                {
                    if (searchImage == null)
                        continue;

                    // Parse image if needed
                    if (!searchImage.Parsed)
                        searchImage.ParseImage();

                    WzImageProperty found = ResolvePropertyInImage(searchImage, propertyPath, isCanvasPath, out int strategyRank);
                    if (!(found is WzCanvasProperty foundCanvas) || foundCanvas.PngProperty == null)
                        continue;

                    // A candidate that ITSELF links elsewhere is a shell: the bytes
                    // it stores are a placeholder, and the '_hash' stamp beside it
                    // describes the picture it POINTS AT, not those bytes. Letting
                    // it into the pool means the stamp rule can pick it as
                    // "provably right" and then copy the placeholder -- wrong
                    // pixels under a proven-right hash -- and when the real picture
                    // is also in the pool, the two "agree" on the stamp over
                    // different bytes and a resolvable link gets refused instead.
                    // A shell is never a pixel source; it is remembered only so a
                    // resolution that finds nothing BUT shells can say so.
                    if (foundCanvas.ContainsInlinkProperty() || foundCanvas.ContainsOutlinkProperty())
                    {
                        shells ??= new List<WzCanvasProperty>();
                        if (!shells.Contains(foundCanvas))
                            shells.Add(foundCanvas);
                        continue;
                    }

                    byte[] portableBytes = TryGetPortableBytes(foundCanvas);
                    if (portableBytes == null || portableBytes.Length == 0)
                        continue;

                    // The same canvas can be reachable from more than one collected
                    // image; it is one candidate, not two.
                    if (candidates.Exists(c => ReferenceEquals(c.Canvas, foundCanvas)))
                        continue;

                    candidates.Add(new OutlinkCandidate(
                        searchImage, foundCanvas, portableBytes, strategyRank, StoredHashOf(foundCanvas)));
                }

                if (candidates.Count == 0)
                {
                    if (shells != null)
                    {
                        // Not an anonymous miss: the address resolved, onto links.
                        // Following them from here would be guessing one hop
                        // deeper, so this refuses and names where each one points.
                        _outlinkRefusalDetail =
                            $"refused: every name-matched canvas answering '{outlinkPath}' is itself a link holding placeholder pixels, not the picture: {DescribeShells(shells)}";
                        Debug.WriteLine($"[WzLinkResolver] {_outlinkRefusalDetail}");
                        return false;
                    }

                    _outlinkMissDetail = $"{_currentCanvasImages.Count} name-matched image(s) were searched and none holds " +
                        $"'{propertyPath}'" + (_exactAddressOnly ? " at its exact address (fallback readings are disabled for a write)." : ".");
                    Debug.WriteLine($"[WzLinkResolver] Could not find property path '{propertyPath}' in image '{imageName}' (searched {_currentCanvasImages.Count} images)");
                    return false;
                }

                OutlinkCandidate chosen = ChooseByContentIdentity(canvas, candidates, outlinkPath);
                if (chosen == null)
                {
                    Debug.WriteLine($"[WzLinkResolver] {_outlinkRefusalDetail}");
                    return false;
                }

                _lastChosen = chosen;
                if (_peekOnly)
                    return true;

                return CopyCanvasData(canvas, chosen.Canvas, chosen.CompressedBytes, false, true);
            }
            catch (Exception ex)
            {
                _outlinkMissDetail = $"resolving the link threw: {ex.Message}";
                Debug.WriteLine($"[WzLinkResolver] Exception resolving outlink: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Searches for an image by name in a WZ directory, optionally within a specific subdirectory path
        /// </summary>
        /// <param name="directory">The root directory to search in</param>
        /// <param name="imageName">The image name (e.g., "0243.img")</param>
        /// <param name="subdirPath">Optional subdirectory path (e.g., "Consume" or "Pet/Special")</param>
        private WzImage FindImageInDirectory(WzDirectory directory, string imageName, string subdirPath = null)
        {
            if (directory == null)
                return null;

            // If subdirectory path is specified, navigate to it first
            if (!string.IsNullOrEmpty(subdirPath))
            {
                string[] subdirs = subdirPath.Split(new[] { '/' }, StringSplitOptions.RemoveEmptyEntries);
                WzDirectory current = directory;

                foreach (string subdir in subdirs)
                {
                    if (current.WzDirectories == null)
                        return null;

                    WzDirectory nextDir = null;
                    foreach (var dir in current.WzDirectories)
                    {
                        if (dir != null && string.Equals(dir.Name, subdir, StringComparison.OrdinalIgnoreCase))
                        {
                            nextDir = dir;
                            break;
                        }
                    }

                    if (nextDir == null)
                        return null; // Subdirectory not found

                    current = nextDir;
                }

                // Now search for the image in the target subdirectory
                if (current.WzImages != null)
                {
                    foreach (var image in current.WzImages)
                    {
                        if (image != null && string.Equals(image.Name, imageName, StringComparison.OrdinalIgnoreCase))
                            return image;
                    }
                }

                return null;
            }

            // No subdirectory specified - search recursively
            // Check images in this directory
            if (directory.WzImages != null)
            {
                foreach (var image in directory.WzImages)
                {
                    if (image != null && string.Equals(image.Name, imageName, StringComparison.OrdinalIgnoreCase))
                        return image;
                }
            }

            // Check subdirectories recursively
            if (directory.WzDirectories != null)
            {
                foreach (var subDir in directory.WzDirectories)
                {
                    if (subDir != null)
                    {
                        var found = FindImageInDirectory(subDir, imageName, null);
                        if (found != null)
                            return found;
                    }
                }
            }

            return null;
        }

        /// <summary>
        /// One same-named reading of an outlink's address: the image it was found
        /// in, the canvas the property path resolved to, the portable compressed
        /// bytes it holds, how precisely the path resolved (0 = the exact address,
        /// higher = a fallback strategy), and the '_hash' stamp stored beside the
        /// canvas, when the archive carries one.
        /// </summary>
        private sealed class OutlinkCandidate
        {
            public OutlinkCandidate(WzImage image, WzCanvasProperty canvasProperty, byte[] compressedBytes, int strategyRank, string storedHash)
            {
                Image = image;
                Canvas = canvasProperty;
                CompressedBytes = compressedBytes;
                StrategyRank = strategyRank;
                StoredHash = storedHash;
            }

            public WzImage Image { get; }
            public WzCanvasProperty Canvas { get; }
            public byte[] CompressedBytes { get; }
            public int StrategyRank { get; }
            public string StoredHash { get; }
        }

        /// <summary>
        /// Decides which of several same-named candidates a link actually means --
        /// by IDENTITY, never by position in a file list.
        ///
        /// The rule, in order:
        /// 1. The linking canvas's own '_hash' stamp names the picture it expects
        ///    (a placeholder carries the same stamp as the picture it references),
        ///    so a candidate stamped with that hash wins outright. Candidates
        ///    stamped as a DIFFERENT picture are the wrong generation's art and are
        ///    dropped; if every candidate is, the resolution is refused -- copying
        ///    provably-wrong pixels is worse than leaving the link unresolved.
        /// 2. Among what survives, an exact-address reading outranks a fallback
        ///    guess; only readings of equal precision go on to content comparison.
        /// 3. If all remaining candidates hold byte-identical content, the choice
        ///    cannot matter and the first is taken. Otherwise NOTHING distinguishes
        ///    them, and the resolution is refused with both candidates named --
        ///    a refusal that says why beats a silent coin flip.
        /// </summary>
        private OutlinkCandidate ChooseByContentIdentity(WzCanvasProperty destCanvas, List<OutlinkCandidate> candidates, string outlinkPath)
        {
            string expected = StoredHashOf(destCanvas);
            List<OutlinkCandidate> pool = candidates;

            if (!string.IsNullOrEmpty(expected))
            {
                var stampedRight = pool.FindAll(c => string.Equals(c.StoredHash, expected, StringComparison.OrdinalIgnoreCase));
                if (stampedRight.Count > 0)
                {
                    pool = stampedRight;
                }
                else
                {
                    // No candidate claims the expected picture. The stamped ones are
                    // provably a different picture; the unstamped ones are merely
                    // unverifiable, which is the weaker claim, so they stay.
                    var unstamped = pool.FindAll(c => string.IsNullOrEmpty(c.StoredHash));
                    if (unstamped.Count == 0)
                    {
                        _outlinkRefusalDetail =
                            $"refused: the link expects a picture stamped _hash '{expected}', and every name-matched canvas for '{outlinkPath}' is stamped as a different picture: {Describe(pool)}";
                        return null;
                    }
                    pool = unstamped;
                }
            }

            if (pool.Count > 1)
            {
                int bestRank = pool[0].StrategyRank;
                for (int i = 1; i < pool.Count; i++)
                {
                    if (pool[i].StrategyRank < bestRank)
                        bestRank = pool[i].StrategyRank;
                }
                pool = pool.FindAll(c => c.StrategyRank == bestRank);
            }

            if (pool.Count == 1)
                return pool[0];

            if (AllSameContent(pool))
                return pool[0];

            _outlinkRefusalDetail =
                $"refused: {pool.Count} same-named canvases answer '{outlinkPath}' with different content and nothing identifies which one the link means: {Describe(pool)}";
            return null;
        }

        /// <summary>
        /// Whether every candidate holds the same picture -- same dimensions, same
        /// format, same portable compressed bytes. When they do, which one is
        /// copied cannot matter; when they do not, somebody has to say so.
        /// </summary>
        private static bool AllSameContent(List<OutlinkCandidate> pool)
        {
            OutlinkCandidate first = pool[0];
            for (int i = 1; i < pool.Count; i++)
            {
                OutlinkCandidate other = pool[i];
                if (first.Canvas.PngProperty.Width != other.Canvas.PngProperty.Width ||
                    first.Canvas.PngProperty.Height != other.Canvas.PngProperty.Height ||
                    first.Canvas.PngProperty.Format != other.Canvas.PngProperty.Format)
                {
                    return false;
                }

                if (!first.CompressedBytes.AsSpan().SequenceEqual(other.CompressedBytes))
                    return false;
            }
            return true;
        }

        /// <summary>
        /// Names each candidate for a refusal message: where it lives, its size,
        /// and its stamp. The point of a refusal is that the user can see BOTH
        /// readings and decide; an unnamed refusal is just a different silence.
        /// </summary>
        private static string Describe(List<OutlinkCandidate> pool)
        {
            return string.Join("; ", pool.Select(c =>
                $"{c.Canvas.FullPath ?? c.Canvas.Name} ({c.Canvas.PngProperty.Width}x{c.Canvas.PngProperty.Height}, _hash {(string.IsNullOrEmpty(c.StoredHash) ? "absent" : c.StoredHash)})"));
        }

        /// <summary>
        /// Names each shell for a refusal message: where it lives and where its own
        /// link points, read as text -- the link is never followed from here.
        /// </summary>
        private static string DescribeShells(List<WzCanvasProperty> shells)
        {
            return string.Join("; ", shells.Select(shell =>
            {
                string inlink = (shell[WzCanvasProperty.InlinkPropertyName] as WzStringProperty)?.Value;
                string outlink = (shell[WzCanvasProperty.OutlinkPropertyName] as WzStringProperty)?.Value;
                string reference = inlink != null
                    ? $"_inlink '{inlink}'"
                    : $"_outlink '{outlink ?? "unreadable"}'";
                return $"{shell.FullPath ?? shell.Name} ({reference})";
            }));
        }

        /// <summary>
        /// The '_hash' stamp stored beside a canvas, or null when it carries none.
        /// </summary>
        private static string StoredHashOf(WzCanvasProperty canvasProperty)
        {
            string value = (canvasProperty?[HashPropertyName] as WzStringProperty)?.Value;
            return string.IsNullOrEmpty(value) ? null : value;
        }

        /// <summary>
        /// The candidate's compressed bytes in portable (plain zlib) form, or null
        /// when they cannot be read. A candidate without readable bytes cannot be
        /// copied OR compared, so it is no candidate at all -- the empty-placeholder
        /// shard case, which must keep falling through to the shard that has the
        /// frame.
        /// </summary>
        private static byte[] TryGetPortableBytes(WzCanvasProperty candidate)
        {
            try
            {
                return candidate.PngProperty?.GetCompressedBytesForExtraction(false);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[WzLinkResolver] Could not read canvas bytes for '{candidate.FullPath ?? candidate.Name}': {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Collects EVERY image with the given name under a directory, recursively.
        /// The plural is the point: <see cref="FindImageInDirectory"/> stops at the
        /// first name match, and a name occurring more than once is exactly the
        /// case the caller must decide by content rather than by position.
        /// </summary>
        private void CollectImagesByName(WzDirectory directory, string imageName, List<WzImage> results)
        {
            if (directory == null)
                return;

            if (directory.WzImages != null)
            {
                foreach (var image in directory.WzImages)
                {
                    if (image != null && string.Equals(image.Name, imageName, StringComparison.OrdinalIgnoreCase) &&
                        !results.Contains(image))
                    {
                        results.Add(image);
                    }
                }
            }

            if (directory.WzDirectories != null)
            {
                foreach (var subDir in directory.WzDirectories)
                {
                    if (subDir != null)
                        CollectImagesByName(subDir, imageName, results);
                }
            }
        }

        /// <summary>
        /// Resolves the property path inside ONE candidate image and reports how it
        /// was found: rank 0 is the exact address, and every fallback strategy
        /// ranks worse in the order it is tried. The rank exists because several
        /// same-named images can each offer SOMETHING -- a shard holding the exact
        /// path outranks a shard where only the flat-index guess produced a canvas
        /// -- and only readings of equal precision go on to content comparison.
        /// </summary>
        private WzImageProperty ResolvePropertyInImage(WzImage searchImage, string propertyPath, bool isCanvasPath, out int strategyRank)
        {
            strategyRank = 0;

            // First try the exact path
            WzImageProperty targetProperty = searchImage.GetFromPath(propertyPath);
            if (targetProperty != null || !isCanvasPath || _exactAddressOnly)
                return targetProperty;

            // If not found and this is a _Canvas path, the structure might be different
            // _Canvas WZ files use a simplified structure: "Anims/0/..." instead of "AnimSet/activated/LayerSlots/..."
            string[] pathParts = propertyPath.Split('/');
            string lastComponent = pathParts[pathParts.Length - 1];

            // Strategy 1: _Canvas files mirror the path structure but use "Anims/0" instead of "AnimSet"
            // Outlink: "AnimSet/activated/LayerSlots/Slot0/Segment1/AnimReference/8"
            // _Canvas: "Anims/0/stand/LayerSlots/Slot0/Segment0/AnimReference/8"
            // Issues: animation name may differ, segment name may differ (Segment0 vs Segment1)
            if (pathParts.Length > 2)
            {
                // Build the remaining path after the animation name
                // e.g., "LayerSlots/Slot0/Segment1/AnimReference/8"
                string remainingPath = string.Join("/", pathParts.Skip(2));

                // Generate alternative paths for segment name mismatches
                var pathsToTry = new List<string> { remainingPath };

                // If path contains SegmentN, also try Segment0, Segment:All, etc.
                if (remainingPath.Contains("Segment"))
                {
                    // Try Segment0 instead of SegmentN
                    var segment0Path = Regex.Replace(remainingPath, @"Segment\d+", "Segment0");
                    if (segment0Path != remainingPath)
                        pathsToTry.Add(segment0Path);

                    // Try Segment:All
                    var segmentAllPath = Regex.Replace(remainingPath, @"Segment[^/]+", "Segment:All");
                    if (segmentAllPath != remainingPath)
                        pathsToTry.Add(segmentAllPath);
                }

                var animsNode = searchImage["Anims"];
                if (animsNode != null && animsNode.WzProperties != null)
                {
                    foreach (var animSubdir in animsNode.WzProperties)
                    {
                        if (animSubdir.WzProperties == null) continue;

                        // Try each animation container
                        foreach (var animContainer in animSubdir.WzProperties)
                        {
                            // Try each path variant
                            foreach (var pathToTry in pathsToTry)
                            {
                                targetProperty = animContainer.GetFromPath(pathToTry);
                                if (targetProperty is WzCanvasProperty)
                                    break;
                                targetProperty = null;
                            }
                            if (targetProperty != null) break;
                        }
                        if (targetProperty != null) break;
                    }
                }

                if (targetProperty != null)
                {
                    strategyRank = 1;
                    return targetProperty;
                }
            }

            // Strategy 1b: Search for exact frame in AnimReference under any animation/segment
            if (int.TryParse(lastComponent, out int frameIdx))
            {
                var animsNode = searchImage["Anims"];
                if (animsNode != null && animsNode.WzProperties != null)
                {
                    foreach (var animSubdir in animsNode.WzProperties)
                    {
                        if (animSubdir.WzProperties == null) continue;
                        foreach (var animContainer in animSubdir.WzProperties)
                        {
                            // Navigate to AnimReference under any Segment
                            var layerSlots = animContainer["LayerSlots"];
                            var slot0 = layerSlots?["Slot0"];
                            if (slot0?.WzProperties != null)
                            {
                                foreach (var segment in slot0.WzProperties)
                                {
                                    var animRef = segment["AnimReference"];
                                    if (animRef?.WzProperties != null)
                                    {
                                        // Only use exact frame match - no fallbacks
                                        var exactFrame = animRef[lastComponent];
                                        if (exactFrame is WzCanvasProperty)
                                        {
                                            targetProperty = exactFrame;
                                            break;
                                        }
                                    }
                                }
                            }
                            if (targetProperty != null) break;
                        }
                        if (targetProperty != null) break;
                    }
                }

                if (targetProperty != null)
                {
                    strategyRank = 2;
                    return targetProperty;
                }
            }

            // Strategy 2: Try to find a canvas at the root level with that name
            targetProperty = searchImage[lastComponent];
            if (targetProperty != null)
            {
                strategyRank = 3;
                return targetProperty;
            }

            // Strategy 3: Try progressively shorter paths from the end
            // e.g., "AnimSet/stand/LayerSlots/Slot0/Segment0/AnimReference/0"
            // Try: "Segment0/AnimReference/0", then "AnimReference/0", then "0"
            for (int i = pathParts.Length - 2; i >= 0 && targetProperty == null; i--)
            {
                string partialPath = string.Join("/", pathParts.Skip(i));
                targetProperty = searchImage.GetFromPath(partialPath);
            }
            if (targetProperty != null)
            {
                strategyRank = 4;
                return targetProperty;
            }

            // Strategy 4: Search recursively for a canvas with the same name
            targetProperty = FindCanvasInImage(searchImage, lastComponent);
            if (targetProperty != null)
            {
                strategyRank = 5;
                return targetProperty;
            }

            // Strategy 5: For numeric names like "0", "1", search for any canvas
            // at the equivalent position in the image's own structure
            if (int.TryParse(lastComponent, out int frameIndex))
            {
                // Try to find any canvas at root level with that index
                var rootCanvas = FindCanvasByIndex(searchImage, frameIndex);
                if (rootCanvas != null)
                {
                    strategyRank = 6;
                    return rootCanvas;
                }
            }

            // Strategy 6: Try matching parent/child pattern anywhere in the image
            // e.g., for "AnimReference/7", search for any "AnimReference" that has child "7"
            if (pathParts.Length >= 2)
            {
                string parentName = pathParts[pathParts.Length - 2];
                targetProperty = FindCanvasWithParent(searchImage, parentName, lastComponent);
                if (targetProperty != null)
                {
                    strategyRank = 7;
                    return targetProperty;
                }
            }

            // Strategy 7: Get all canvases and find one that might match by index
            // This handles cases where _Canvas has flat structure with different naming
            if (int.TryParse(lastComponent, out int idx))
            {
                var allCanvases = GetAllCanvasesInImage(searchImage);
                if (idx < allCanvases.Count)
                {
                    strategyRank = 8;
                    return allCanvases[idx];
                }
            }

            return null;
        }

        private static string NormalizePath(string path)
        {
            return string.IsNullOrEmpty(path)
                ? string.Empty
                : path.Replace(Path.DirectorySeparatorChar, '/')
                      .Replace(Path.AltDirectorySeparatorChar, '/')
                      .Trim('/');
        }

        private static bool MatchesCanvasFolder(string wzFilePath, string categoryName, string canvasFolderPath)
        {
            if (string.IsNullOrEmpty(wzFilePath) || string.IsNullOrEmpty(categoryName))
            {
                return false;
            }

            if (string.IsNullOrEmpty(canvasFolderPath))
            {
                return wzFilePath.Contains("/_Canvas/", StringComparison.OrdinalIgnoreCase);
            }

            string normalizedCategory = NormalizePath(categoryName);
            string normalizedCanvasFolder = NormalizePath(canvasFolderPath);
            string expectedFolder = $"/{normalizedCategory}/{normalizedCanvasFolder}/";

            return wzFilePath.Contains(expectedFolder, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Recursively searches for a canvas property by name within an image.
        /// Used when the exact path in _Canvas files doesn't match the outlink path.
        /// </summary>
        /// <param name="image">The WzImage to search in</param>
        /// <param name="canvasName">The name of the canvas to find</param>
        /// <returns>The canvas property if found, null otherwise</returns>
        internal WzCanvasProperty FindCanvasInImage(WzImage image, string canvasName)
        {
            if (image == null || image.WzProperties == null)
                return null;

            // One guard per search, never one per resolver: the visited set is what
            // makes it correct and it must not outlive the tree it was built for.
            WzWalk walk = new WzWalk();
            foreach (var prop in image.WzProperties)
            {
                var result = FindCanvasInProperty(prop, canvasName, walk, 0);
                if (result != null)
                    return result;
            }

            return null;
        }

        /// <summary>
        /// Recursively searches for a canvas property by name within a property tree.
        ///
        /// Descends through <see cref="WzWalk"/>, which it did not before. This is a
        /// FALLBACK: it runs because the named path did not resolve, so the image it
        /// is handed is already malformed, and a malformed image is exactly where a
        /// UOL pointing at its own ancestor lives. The catch(Exception) the caller
        /// wraps all seven strategies in is no help at all here -- a self-referential
        /// link makes this a StackOverflowException, which nothing catches.
        /// </summary>
        private WzCanvasProperty FindCanvasInProperty(
            WzImageProperty property, string canvasName, WzWalk walk, int depth)
        {
            if (property == null)
                return null;

            // Check if this is the canvas we're looking for. Tested before the
            // descent, so a match is still a match at a node the walk will then
            // decline to enter.
            if (property is WzCanvasProperty canvas &&
                string.Equals(property.Name, canvasName, StringComparison.OrdinalIgnoreCase))
            {
                return canvas;
            }

            // Search children
            var children = walk.Into(property, depth);
            if (children != null)
            {
                foreach (var child in children)
                {
                    var result = FindCanvasInProperty(child, canvasName, walk, depth + 1);
                    if (result != null)
                        return result;
                }
            }

            return null;
        }

        /// <summary>
        /// Searches for a canvas property by numeric index within an image.
        /// Useful for finding frame canvases like "0", "1", "2" in animation sequences.
        /// </summary>
        /// <param name="image">The WzImage to search in</param>
        /// <param name="index">The numeric index to find</param>
        /// <returns>The canvas property if found, null otherwise</returns>
        private WzCanvasProperty FindCanvasByIndex(WzImage image, int index)
        {
            if (image == null || image.WzProperties == null)
                return null;

            string indexName = index.ToString();

            // First try direct child with that name
            var directChild = image[indexName];
            if (directChild is WzCanvasProperty directCanvas)
                return directCanvas;

            // Search through all properties for a canvas with that name
            foreach (var prop in image.WzProperties)
            {
                if (prop is WzCanvasProperty canvas && prop.Name == indexName)
                    return canvas;

                // Also check one level deep (common structure: subprop/0, subprop/1)
                var children = prop.WzProperties;
                if (children != null)
                {
                    foreach (var child in children)
                    {
                        if (child is WzCanvasProperty childCanvas && child.Name == indexName)
                            return childCanvas;
                    }
                }
            }

            return null;
        }

        /// <summary>
        /// Searches for a canvas property where the parent has a specific name and the canvas has a specific name.
        /// Useful for finding paths like "AnimReference/7" anywhere in the image structure.
        /// </summary>
        /// <param name="image">The WzImage to search in</param>
        /// <param name="parentName">The name of the parent property to match</param>
        /// <param name="childName">The name of the canvas child to find</param>
        /// <returns>The canvas property if found, null otherwise</returns>
        internal WzCanvasProperty FindCanvasWithParent(WzImage image, string parentName, string childName)
        {
            if (image == null || image.WzProperties == null)
                return null;

            WzWalk walk = new WzWalk();
            foreach (var prop in image.WzProperties)
            {
                var result = FindCanvasWithParentInProperty(prop, parentName, childName, walk, 0);
                if (result != null)
                    return result;
            }

            return null;
        }

        /// <summary>
        /// Recursively searches for a canvas with parent/child name pattern in a property tree.
        ///
        /// Descends through <see cref="WzWalk"/>, and reads the children ONCE: asking
        /// the guard a second time for the same node is asking a visited set whether
        /// it has seen the node it was just told about, and the answer is always yes.
        /// </summary>
        private WzCanvasProperty FindCanvasWithParentInProperty(
            WzImageProperty property, string parentName, string childName, WzWalk walk, int depth)
        {
            if (property == null)
                return null;

            var children = walk.Into(property, depth);
            if (children == null)
                return null;

            // Check if this property matches the parent name and has a canvas child with the child name
            if (string.Equals(property.Name, parentName, StringComparison.OrdinalIgnoreCase))
            {
                foreach (var child in children)
                {
                    if (child is WzCanvasProperty canvas &&
                        string.Equals(child.Name, childName, StringComparison.OrdinalIgnoreCase))
                    {
                        return canvas;
                    }
                }
            }

            // Continue searching in children
            foreach (var descendant in children)
            {
                var result = FindCanvasWithParentInProperty(descendant, parentName, childName, walk, depth + 1);
                if (result != null)
                    return result;
            }

            return null;
        }

        /// <summary>
        /// Gets all canvas properties in an image in a flat list.
        /// Useful as a fallback when path-based matching fails.
        /// </summary>
        /// <param name="image">The WzImage to search in</param>
        /// <returns>List of all canvas properties in the image</returns>
        internal List<WzCanvasProperty> GetAllCanvasesInImage(WzImage image)
        {
            var canvases = new List<WzCanvasProperty>();

            if (image == null || image.WzProperties == null)
                return canvases;

            WzWalk walk = new WzWalk();
            foreach (var prop in image.WzProperties)
            {
                CollectCanvasesFromProperty(prop, canvases, walk, 0);
            }

            return canvases;
        }

        /// <summary>
        /// Recursively collects all canvas properties from a property tree.
        ///
        /// Descends through <see cref="WzWalk"/>. The list this builds is INDEXED by
        /// its caller (strategy 7 takes <c>allCanvases[idx]</c>), so a link counted
        /// as structure did not merely cost time: it added the linked canvas a
        /// second time, shifted every index after it, and handed the resolver a
        /// canvas from somewhere else to copy pixels out of.
        /// </summary>
        private void CollectCanvasesFromProperty(
            WzImageProperty property, List<WzCanvasProperty> canvases, WzWalk walk, int depth)
        {
            if (property == null)
                return;

            if (property is WzCanvasProperty canvas)
            {
                canvases.Add(canvas);
            }

            var children = walk.Into(property, depth);
            if (children != null)
            {
                foreach (var child in children)
                {
                    CollectCanvasesFromProperty(child, canvases, walk, depth + 1);
                }
            }
        }

        /// <summary>
        /// Debug helper to show property tree structure
        /// </summary>
        private void ShowPropertyTree(WzImageProperty property, string indent, int maxDepth)
        {
            if (property == null || maxDepth <= 0) return;

            var children = property.WzProperties;
            if (children == null || children.Count == 0)
            {
                Debug.WriteLine($"{indent}{property.Name} ({property.PropertyType})");
                return;
            }

            Debug.WriteLine($"{indent}{property.Name}/");
            foreach (var child in children.Take(3))
            {
                ShowPropertyTree(child, indent + "  ", maxDepth - 1);
            }
            if (children.Count > 3)
            {
                Debug.WriteLine($"{indent}  ... and {children.Count - 3} more");
            }
        }
        #endregion
    }
}
