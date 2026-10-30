using MapleLib.WzLib;
using MapleLib.WzLib.WzProperties;
using MapleBench.Services;
using Microsoft.Extensions.Logging.Abstractions;
using System.Diagnostics;
using System.Reflection;
using MapleLib.WzLib.Util;
using MapleBench.Models;
using System.Collections;

int checks = 0;
void Check(bool value, string name) { if (!value) throw new Exception(name); checks++; Console.WriteLine("PASS " + name); }
Check(WzSizeLimits.Advance(0x7fffffff, 1) == 0x80000000, "cross signed 2 GiB boundary");
Check(WzSizeLimits.Advance(0xfffffffe, 1) == uint.MaxValue, "last unsigned address");
try { WzSizeLimits.Advance(uint.MaxValue, 1); throw new Exception("overflow accepted"); }
catch (WzCapacityException) { Check(true, "reject 4 GiB wrap"); }
try { WzSizeLimits.Advance(0, -1); throw new Exception("negative accepted"); }
catch (WzCapacityException) { Check(true, "reject negative block"); }
using (MemoryStream offsets = new())
{
    byte[] iv = WzTool.GetIvByMapleVersion(WzMapleVersion.BMS);
    WzHeader header = WzHeader.GetDefault();
    using (WzBinaryWriter writer = new(offsets, iv, true) { Header = header, Hash = 59192 })
    {
        offsets.Position = header.FStart;
        writer.WriteOffset(0x80000000);
        writer.WriteOffset(uint.MaxValue);
        try { writer.WriteOffset((long)uint.MaxValue + 1); throw new Exception("offset wrap accepted"); }
        catch (WzCapacityException) { Check(true, "offset encoder rejects wrap"); }
    }
    using MemoryStream encoded = new(offsets.ToArray());
    encoded.Position = header.FStart;
    using WzBinaryReader reader = new(encoded, iv) { Header = header, Hash = 59192 };
    Check(reader.ReadOffset() == 0x80000000 && reader.ReadOffset() == uint.MaxValue, "unsigned encrypted offsets round-trip");
}
string root = Path.Combine(Path.GetTempPath(), "maplebench-regression-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
string previous = Environment.CurrentDirectory;
Environment.CurrentDirectory = root;
try
{
    // Real serializer admission must fail before truncating a destination and
    // restore offsets so retry reads the original source addresses.
    using (WzFile oversized = new(777, WzMapleVersion.BMS))
    {
        for (int i = 0; i < 3; i++)
            oversized.WzDirectory.AddImage(new WzImage($"{i}.img") { Changed = false, BlockSize = 1_500_000_000, Offset = 123 + i });
        string untouched = Path.Combine(root, "untouched.wz");
        File.WriteAllText(untouched, "original");
        try { oversized.SaveToDisk(untouched, true); throw new Exception("oversize write accepted"); }
        catch (WzCapacityException) { Check(File.ReadAllText(untouched) == "original", "oversize save refuses before truncation"); }
        Check(oversized.WzDirectory.WzImages.Select(i => i.Offset).SequenceEqual(new long[] { 123, 124, 125 }), "failed writer restores source offsets");
    }
    // A sparse file exercises a real seek above signed Int32 without allocating
    // gigabytes of RAM or disk blocks.
    string sparse = Path.Combine(root, "seek.img");
    const long high = 0x80000100L;
    using (FileStream stream = File.Create(sparse))
    using (WzBinaryWriter writer = new(stream, WzTool.GetIvByMapleVersion(WzMapleVersion.BMS)))
    {
        stream.Position = high;
        WzImage image = new("high.img");
        image.AddProperty(new WzStringProperty("value", "past-2GiB"));
        image.SaveImage(writer);
    }
    using (WzImage image = new("high.img", File.OpenRead(sparse), WzMapleVersion.BMS) { Offset = high })
        Check(image.ParseImage() && image["value"].GetString() == "past-2GiB", "sparse image parse above 2 GiB");
    File.Delete(sparse);

    using WzFile file = new(777, WzMapleVersion.BMS) { Name = "Mob.wz" };
    file.WzDirectory.Name = "Mob.wz";
    for (int i = 0; i < 4; i++)
    {
        WzImage img = new($"{1000000+i}.img");
        img.AddProperty(new WzStringProperty("value", "content-" + i));
        img.BlockSize = 1_400_000_000; // synthetic planning weights; payloads stay small
        file.WzDirectory.AddImage(img);
    }
    using AssembledArchive assembled = new() { File = file, ArchiveName = "Mob", ArchiveDirectory = root,
        Images = 4, SourceBytes = 5_600_000_000 };
    ClientImportService service = new(NullLogger<ClientImportService>.Instance);
    MethodInfo write = typeof(ClientImportService).GetMethod("WriteFamily", BindingFlags.Instance | BindingFlags.NonPublic)!;
    ImportResult Convert(bool overwrite = false)
    {
        foreach (WzImage image in file.WzDirectory.WzImages) image.BlockSize = 1_400_000_000;
        return (ImportResult)write.Invoke(service,
            new object[] { assembled, Path.Combine(root, "Mob.wz"), new ImportRequest { SampleSize = 4, Overwrite = overwrite },
                true, Stopwatch.StartNew(), (Action<string,long,long>)((_,_,_) => {}), CancellationToken.None })!;
    }
    ImportResult result = Convert();
    Check(result.Outputs.Count == 2, "split oversized family into numbered siblings");
    Check(result.Outputs.Select(o => Path.GetFileName(o.Path)).SequenceEqual(new[] { "Mob.wz", "Mob001.wz" }), "classic family names");
    Check(result.Outputs.Sum(o => o.Images) == 4 && result.Sampled == 4, "all images verified across parts");
    Check(file.WzDirectory.WzImages.Count == 4, "restore source tree after staging");
    Check(file.WzDirectory.WzImages.All(i => i.Parent == file.WzDirectory), "restore source parents");
    string splitFolder = Path.Combine(root, "source-data", "Data", "Npc");
    Directory.CreateDirectory(splitFolder);
    File.WriteAllText(Path.Combine(splitFolder, "Npc.ini"), "LastWzIndex|0");
    using (WzFile raw = new(777, WzMapleVersion.BMS) { Name = "Npc.wz" })
    {
        raw.WzDirectory.Name = "Npc.wz";
        WzDirectory sub = new("nested", raw);
        raw.WzDirectory.AddDirectory(sub);
        WzImage img = new("1000000.img");
        img.AddProperty(new WzStringProperty("value", "raw-source"));
        sub.AddImage(img);
        raw.SaveToDisk(Path.Combine(splitFolder, "Npc_000.wz"), true);
    }
    ImportResult rawResult = service.Import(new ImportRequest { SourceFolder = Path.Combine(root, "source-data"),
        Archive = "Npc", OutputFolder = Path.Combine(root, "raw-target"), SampleSize = 8 });
    Check(rawResult.Images == 1 && rawResult.Sampled == 1 && rawResult.Outputs.Count == 1, "public importer detects and verifies nested raw WZ input");
    using (CancellationTokenSource canceled = new())
    {
        canceled.Cancel();
        try
        {
            write.Invoke(service, new object[] { assembled, Path.Combine(root, "Canceled.wz"), new ImportRequest(),
                true, Stopwatch.StartNew(), (Action<string,long,long>)((_,_,_) => {}), canceled.Token });
            throw new Exception("cancellation ignored");
        }
        catch (TargetInvocationException e) when (e.InnerException is OperationCanceledException)
        { Check(!File.Exists(Path.Combine(root, "Canceled.wz")), "cancel before staging publishes nothing"); }
    }

    using WzSessionService session = new(NullLogger<WzSessionService>.Instance);
    UndoService undo = new();
    PortService port = new(session, new WzEditService(session, undo),
        new StringPoolService(session, NullLogger<StringPoolService>.Instance), undo, NullLogger<PortService>.Instance);
    var files = (IDictionary)typeof(WzSessionService).GetField("_files", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(session)!;
    OpenFile Register(string id, string name, string client)
    {
        OpenFile f = new(id, name, Path.Combine(root, client, name), "wz") { WzFile = new(777, WzMapleVersion.BMS) { Name = name } };
        f.WzFile.WzDirectory.Name = name;
        files.Add(id, f);
        return f;
    }
    OpenFile source = Register("s", "Mob.wz", "source");
    OpenFile full = Register("a", "Mob.wz", "target");
    OpenFile free = Register("b", "Mob001.wz", "target");
    full.WzFile!.WzDirectory.AddImage(new WzImage("9999998.img") { BlockSize = 2_000_000_000 });
    full.WzFile.WzDirectory.AddImage(new WzImage("9999999.img") { BlockSize = 2_000_000_000 });
    free.WzFile!.WzDirectory.AddImage(new WzImage("1000001.img") { BlockSize = 100 });
    source.WzFile!.WzDirectory.AddImage(new WzImage("1000001.img") { BlockSize = 100 });
    source.WzFile.WzDirectory.AddImage(new WzImage("1000002.img") { BlockSize = 200_000_000 });
    Type portType = typeof(PortService);
    var spec = PortService.Kinds.Single(k => k.Kind == "mob");
    object index = portType.GetMethod("Index", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(port, new object[] { source, spec })!;
    object[] entries = ((IEnumerable)index.GetType().GetProperty("All")!.GetValue(index)!).Cast<object>().ToArray();
    object group = Activator.CreateInstance(portType.GetNestedType("ClientGroup", BindingFlags.NonPublic)!,
        "target", "target", root, new List<OpenFile> { full, free })!;
    Dictionary<string,long> reserved = new();
    Dictionary<string,OpenFile> containers = new();
    OpenFile Choose(object entry) => (OpenFile)portType.GetMethod("ArchiveFor", BindingFlags.Instance | BindingFlags.NonPublic)!
        .Invoke(port, new object?[] { entry, spec, group, full, containers, null })!;
    Check(Choose(entries[0]) == free, "existing ID stays in numbered sibling");
    Check(Choose(entries[1]) == free, "new image uses sibling with capacity");
    reserved = (Dictionary<string,long>)portType.GetField("_placementBytes", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(port)!;
    Check(reserved[free.Id] >= 200_000_000, "planned images reserve capacity");
    free.ReadOnly = true;
    try { Choose(entries[0]); throw new Exception("read-only owner accepted"); }
    catch (TargetInvocationException e) when (e.InnerException is InvalidOperationException)
    { Check(true, "read-only existing owner cannot create duplicate elsewhere"); }
    free.ReadOnly = false;
    Check(source.WzFile.WzDirectory.WzImages[1].DeepClone().BlockSize == 200_000_000, "clone retains planning size");

    byte[] before = File.ReadAllBytes(result.Outputs[0].Path);
    try { Convert(); throw new Exception("overwrite accepted"); }
    catch (TargetInvocationException e) when (e.InnerException is InvalidOperationException)
    { Check(File.ReadAllBytes(result.Outputs[0].Path).SequenceEqual(before), "collision leaves destination untouched"); }
    Check(!Directory.EnumerateFiles(root, "mbimport-*.wz").Any(), "staging cleaned after failure");
    File.Delete(Path.Combine(root, "Mob001.wz"));
    Directory.CreateDirectory(Path.Combine(root, "Mob001.wz"));
    try { Convert(true); throw new Exception("publication failure not injected"); }
    catch (TargetInvocationException e) when (e.InnerException is IOException)
    { Check(File.ReadAllBytes(result.Outputs[0].Path).SequenceEqual(before), "later-part failure rolls back first replacement"); }
    Directory.Delete(Path.Combine(root, "Mob001.wz"));
    Check(Directory.EnumerateFiles(root, "*.bak").Any(), "rollback retains recovery backup");
    ImportResult replaced = Convert(true);
    Check(replaced.Outputs[0].BackupPath != null && File.Exists(replaced.Outputs[0].BackupPath), "successful family replacement retains backup");

    OpenFile morphSource = Register("ms", "Morph.wz", "source");
    OpenFile morphTarget = Register("mt", "Morph.wz", "target");
    for (int i = 1; i <= 201; i++)
        morphSource.WzFile!.WzDirectory.AddImage(new WzImage($"{i:D7}.img"));
    PortPlanDto firstBatch = port.Plan(new PortPlanRequest { Kind = "morph", Scope = "archive", SourceFileId = "ms", TargetFileId = "mt" });
    Check(!firstBatch.Blocked && firstBatch.BatchEntries == 200 && firstBatch.NextBatchOffset == 200, "bulk plan keeps dependency scans within bounded batch");
    PortPlanDto lastBatch = port.Plan(new PortPlanRequest { Kind = "morph", Scope = "archive", SourceFileId = "ms", TargetFileId = "mt", BatchOffset = 200 });
    Check(!lastBatch.Blocked && lastBatch.BatchEntries == 1 && lastBatch.NextBatchOffset == null && lastBatch.TotalSourceEntries == 201,
        "continuation covers remaining entry without restart");
    Check(morphSource.WzFile!.WzDirectory.WzImages.All(i => i.BlockSize > 0), "in-memory images acquire measured serialization sizes");
    PortPlanDto smallBatch = port.Plan(new PortPlanRequest { Kind = "morph", Scope = "archive", SourceFileId = "ms", TargetFileId = "mt", BatchSize = 25 });
    Check(smallBatch.BatchEntries == 25 && smallBatch.NextBatchOffset == 25, "user can reduce dependency-heavy batch size");
    PortResultDto applied = port.Apply(new PortApplyRequest { Kind = "morph", Scope = "archive", SourceFileId = "ms", TargetFileId = "mt", BatchOffset = 200, Confirmed = true });
    Check(applied.Written == 1 && applied.Failed == 0 && morphTarget.WzFile!.WzDirectory.WzImages.Count == 1, "batch apply copies only its requested range");
    undo.Undo();
    Check(morphTarget.WzFile!.WzDirectory.WzImages.Count == 0, "batch remains undoable");


}
finally { Environment.CurrentDirectory = previous; Directory.Delete(root, true); }
Console.WriteLine($"{checks} checks passed.");
