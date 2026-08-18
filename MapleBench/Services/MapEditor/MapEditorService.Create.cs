using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using MapleBench.Models;
using MapleBench.Services.MapModel;
using MapleLib.WzLib;
using MapleLib.WzLib.WzProperties;

namespace MapleBench.Services.MapEditor;

/// <summary>
/// Creating a map from scratch: a nine-digit id validated and collision-checked
/// across every open Map archive, the measured minimal shape scaffolded, the
/// image landed in the archive (and Map&lt;digit&gt; folder) where the client
/// keeps its maps, and a String.wz row written so the map has a name.
/// </summary>
public sealed partial class MapEditorService
{
    public MapCreateResultDto CreateMap(MapCreateRequest request)
    {
        int id = request.Id;
        if (id is < 100000000 or > 999999999)
        {
            throw new ArgumentException(
                $"'{id}' is not a nine-digit map id. A map lives at Map<first digit>/<id padded "
                + "to 9>.img, so the id decides both its folder and its name.");
        }

        string imageName = id.ToString(CultureInfo.InvariantCulture).PadLeft(9, '0') + ".img";
        string subDirName = "Map" + id.ToString(CultureInfo.InvariantCulture)[0];
        MapCreateResultDto result = new();

        OpenFile targetFile;
        lock (_session.Gate)
        {
            // Collision check across ALL Map archives — an id that exists
            // anywhere is taken, whichever archive would win the mount race.
            foreach ((OpenFile file, WzDirectory mapRoot, string rootPath) in MapDirectories())
            {
                foreach (WzDirectory sub in mapRoot.WzDirectories)
                {
                    if (sub.GetImageByName(imageName) != null)
                    {
                        string existing = WzPath.Child(WzPath.Child(rootPath, sub.Name), imageName);
                        string? name = _strings.GetMapName(id);
                        throw new InvalidOperationException(
                            $"Map {id}{(name != null ? $" ({name})" : "")} already exists at "
                            + $"{existing}. Creating it again would shadow or overwrite a real map; "
                            + "pick a free id.");
                    }
                }
            }

            // Land where the client keeps its maps: the mount-order-first archive
            // whose Map/ holds the right Map<digit> folder, else the first that
            // holds any map folder at all.
            (OpenFile File, WzDirectory MapRoot, string RootPath, WzDirectory? Sub)? target = null;
            foreach ((OpenFile file, WzDirectory mapRoot, string rootPath) in MapDirectories())
            {
                WzDirectory? sub = mapRoot.WzDirectories.FirstOrDefault(
                    d => string.Equals(d.Name, subDirName, StringComparison.OrdinalIgnoreCase));
                if (sub != null)
                {
                    target = (file, mapRoot, rootPath, sub);
                    break;
                }
                target ??= mapRoot.WzDirectories.Any(
                        d => d.Name.Length == 4 && d.Name.StartsWith("Map", StringComparison.Ordinal))
                    ? (file, mapRoot, rootPath, null)
                    : target;
            }
            if (target == null)
            {
                throw new InvalidOperationException(
                    "No open archive holds map images (Map/Map0..Map9). Open the client's Map "
                    + "family — copies, never the live client — before creating a map.");
            }

            targetFile = target.Value.File;
            if (targetFile.ReadOnly)
            {
                throw new InvalidOperationException(
                    $"'{targetFile.Name}' is reference-only, so a new map cannot land in it. "
                    + "Release it first.");
            }
            RefuseLiveClient(targetFile);

            WzDirectory sub2 = target.Value.Sub ?? CreateMapSubDirectory(target.Value, subDirName, result);

            WzImage image = WzNode.BuildImage(imageName, ScaffoldNodes());
            image.Changed = true;
            sub2.AddImage(image);

            result.Archive = targetFile.Name;
            result.Path = WzPath.Child(WzPath.Child(target.Value.RootPath, sub2.Name), imageName);
        }

        // Save the archive — collision-checked and scaffolded is not created
        // until it is on disk and verifiable.
        SaveResult saved = _save.Save(new SaveRequest { FileId = targetFile.Id });
        result.SavedTo = saved.SavedTo;
        result.BackupPath = saved.BackupPath;

        // The String.wz row: names live there and only there (info/mapName
        // exists on 20 maps and is wrong on all of them).
        WriteStringRow(id, request.Name, request.StreetName, result);

        result.Notes.Add(
            "Scaffolded with the measured minimal shape: universal info keys, layers 0-7, and the "
            + "empty containers real maps ship (foothold, life, ladderRope, portal, reactor, back). "
            + "No minimap yet — 939 geometry maps ship without one; regenerate it once there is "
            + "something to draw.");
        return result;
    }

    private static WzDirectory CreateMapSubDirectory(
        (OpenFile File, WzDirectory MapRoot, string RootPath, WzDirectory? Sub) target,
        string subDirName, MapCreateResultDto result)
    {
        WzDirectory sub = new(subDirName, target.File.WzFile);
        target.MapRoot.AddDirectory(sub);
        result.Notes.Add($"'{target.File.Name}' had no {subDirName}/ folder; it was created.");
        return sub;
    }

    /// <summary>
    /// The minimal scaffold, from the measured corpus: the info keys present on
    /// essentially every geometry map with their sentinel/default values
    /// (returnMap/forcedReturn 999999999 = "none", version 10, mapMark "None" =
    /// the measured sentinel), layers 0-7 each with an info container, and the
    /// empty containers real maps ship — an empty container is data, not
    /// clutter (reactor ships empty on 9,560 maps).
    /// </summary>
    private static List<WzNode> ScaffoldNodes()
    {
        List<WzNode> nodes = new();

        WzNode info = WzNode.Container("info");
        info.Add(WzNode.Scalar("version", WzPropertyType.Int, 10));
        info.Add(WzNode.Scalar("cloud", WzPropertyType.Int, 0));
        info.Add(WzNode.Scalar("town", WzPropertyType.Int, 0));
        info.Add(WzNode.Scalar("returnMap", WzPropertyType.Int, 999999999));
        info.Add(WzNode.Scalar("forcedReturn", WzPropertyType.Int, 999999999));
        info.Add(WzNode.Scalar("mobRate", WzPropertyType.Float, 1.0));
        info.Add(WzNode.OfText("bgm", WzPropertyType.String, "Bgm09/DarkShadow"));
        info.Add(WzNode.OfText("mapMark", WzPropertyType.String, "None"));
        info.Add(WzNode.Scalar("fieldLimit", WzPropertyType.Int, 0));
        info.Add(WzNode.Scalar("swim", WzPropertyType.Int, 0));
        info.Add(WzNode.Scalar("fly", WzPropertyType.Int, 0));
        info.Add(WzNode.Scalar("noMapCmd", WzPropertyType.Int, 0));
        info.Add(WzNode.Scalar("hideMinimap", WzPropertyType.Int, 0));
        info.Add(WzNode.OfText("onFirstUserEnter", WzPropertyType.String, ""));
        info.Add(WzNode.OfText("onUserEnter", WzPropertyType.String, ""));
        info.Add(WzNode.Scalar("VRTop", WzPropertyType.Int, -700));
        info.Add(WzNode.Scalar("VRLeft", WzPropertyType.Int, -1000));
        info.Add(WzNode.Scalar("VRBottom", WzPropertyType.Int, 400));
        info.Add(WzNode.Scalar("VRRight", WzPropertyType.Int, 1000));
        nodes.Add(info);

        nodes.Add(WzNode.Container("back"));

        for (int layer = 0; layer <= 7; layer++)
        {
            WzNode layerNode = WzNode.Container(layer.ToString(CultureInfo.InvariantCulture));
            layerNode.Add(WzNode.Container("info"));
            nodes.Add(layerNode);
        }

        nodes.Add(WzNode.Container("life"));
        nodes.Add(WzNode.Container("ladderRope"));
        nodes.Add(WzNode.Container("foothold"));
        nodes.Add(WzNode.Container("portal"));
        nodes.Add(WzNode.Container("reactor"));
        return nodes;
    }

    #region The String.wz row

    /// <summary>
    /// Writes <c>String.wz/Map.img/&lt;region&gt;/&lt;id&gt;</c> with mapName /
    /// streetName. The region folder is chosen by measurement rather than
    /// guesswork: the folder whose existing rows are numerically nearest the new
    /// id. When String.wz is not open the row is skipped and SAID — a map with
    /// no String row renders and runs (440 geometry maps ship that way); it just
    /// has no name in game or picker.
    /// </summary>
    private void WriteStringRow(int id, string? name, string? streetName, MapCreateResultDto result)
    {
        OpenFile? stringFile = null;
        lock (_session.Gate)
        {
            foreach (OpenFile file in Ordered(_session.SelectRoleSources("String")))
            {
                if ((file.LooseImage != null
                        && file.LooseImage.Name.Equals("Map.img", StringComparison.OrdinalIgnoreCase))
                    || _session.RoleRoot(file, "String")?.GetImageByName("Map.img") != null)
                {
                    stringFile = file;
                    break;
                }
            }

            if (stringFile == null)
            {
                result.Notes.Add(
                    "String.wz is not open, so no name row was written. The map is fully playable "
                    + "without one (440 shipped geometry maps have none) but shows no name; open "
                    + "String.wz and create the row to fix that.");
                return;
            }
            if (stringFile.ReadOnly)
            {
                result.Notes.Add(
                    $"'{stringFile.Name}' is reference-only, so no name row was written there.");
                return;
            }
            RefuseLiveClient(stringFile);

            WzImage mapImg = stringFile.LooseImage
                ?? _session.RoleRoot(stringFile, "String")!.GetImageByName("Map.img")!;
            WzSessionService.EnsureParsed(mapImg);

            // Measured: region folders are editorial, not id-derived — their id
            // ranges overlap heavily and 'etc' is the catch-all (5,617 rows,
            // spanning to 999999998). A custom map's row goes to 'etc'; when a
            // client has no 'etc', the folder whose rows numerically surround
            // the id is the fallback.
            WzImageProperty? bestRegion = mapImg.WzProperties.FirstOrDefault(
                p => string.Equals(p.Name, "etc", StringComparison.OrdinalIgnoreCase)
                     && p.WzProperties != null);
            if (bestRegion == null)
            {
                long bestDistance = long.MaxValue;
                foreach (WzImageProperty region in mapImg.WzProperties)
                {
                    if (region.WzProperties == null)
                        continue;
                    foreach (WzImageProperty row in region.WzProperties)
                    {
                        if (!long.TryParse(row.Name, NumberStyles.None, CultureInfo.InvariantCulture, out long rowId))
                            continue;
                        long distance = Math.Abs(rowId - id);
                        if (distance < bestDistance)
                        {
                            bestDistance = distance;
                            bestRegion = region;
                        }
                    }
                }
            }
            if (bestRegion?.WzProperties == null)
            {
                result.Notes.Add(
                    "String.wz/Map.img has no region folder to file the name row under; the row "
                    + "was not written.");
                return;
            }

            string rowName = id.ToString(CultureInfo.InvariantCulture);
            if (bestRegion.WzProperties.Any(p => string.Equals(p.Name, rowName, StringComparison.Ordinal)))
            {
                result.Notes.Add(
                    $"String.wz/Map.img/{bestRegion.Name}/{rowName} already exists; it was left "
                    + "alone rather than overwritten.");
                result.StringRegion = bestRegion.Name;
                return;
            }

            WzSubProperty newRow = new(rowName);
            newRow.WzProperties.Add(new WzStringProperty("mapName", name ?? ""));
            newRow.WzProperties.Add(new WzStringProperty("streetName", streetName ?? ""));
            bestRegion.WzProperties.Add(newRow);
            mapImg.Changed = true;
            result.StringRegion = bestRegion.Name;
        }

        SaveResult saved = _save.Save(new SaveRequest { FileId = stringFile.Id });
        result.StringRowWritten = true;
        result.StringSavedTo = saved.SavedTo;

        // The pool caches names; a row it has not seen keeps the picker blank.
        _strings.Invalidate();
    }

    #endregion
}
