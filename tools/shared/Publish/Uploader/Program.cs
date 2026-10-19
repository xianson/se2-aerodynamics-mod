// Uploader: puts a folder staged by ../stage.py on the Steam Workshop the way SE2 2.4.0.95's ModHub does.
//
// Mirrors (decompiled 2.4.0.95):
//   ModHub   WorkshopComponent.PublishAsync            title/summary/tags from ModProject.modhub-metadata, + UGCModTypeTag,
//                                                      PreviewFilePath "thumb.jpg", Visiblity (ModHub: Public on a new item,
//                                                      the current one on an update), ModMetadata{Id, ProjectID, Version},
//                                                      the new item id written into the .vrgproj (UpdateModId, :215-218)
//   VRage.Steam SteamUGCServiceComponent
//            AllocateUGCItemAsync / UploadUGCDataAsync  CreateItem(consumer app, k_EWorkshopFileTypeFirst); one project GUID ->
//                                                      one item (FindItemByProjectIdAsync, IdExists / IdMismatch, :329-351);
//                                                      SteamUgcMetadata{Id, FullMetadata=ModMetadata, Author, IsReferenceToRemote
//                                                      false, LastLocallyUpdated} serialized by the game's serializer
//            UpdateWorkshopItem (:1186-1230)           StartItemUpdate, RemoveItemKeyValueTags + AddItemKeyValueTag("VRageProjectID",
//                                                      GUID with '_' for '-'), SetItemTitle, SetItemTags, SetItemVisibility,
//                                                      SetItemDescription, SetItemContent, SetItemPreview, SetItemMetadata,
//                                                      SubmitItemUpdate; then SubscribeItem
// Differences on purpose: Scripts/ ship (ModHub never copies them); a new item is UNLISTED by default, not Public; the
// item id is written into the STAGED .vrgproj and a write-back copy, never into the live mod folder; on a failed upload
// a just-created item is reported, not deleted.
//
// Usage: Uploader <stage out dir> [--create | --update <id> | --content-only <id>] [--visibility unlisted|private|friends|public]
//        [--title t] [--summary s] [--description-file f] [--tags a,b] [--change-note t | --change-note-file f]
//        [--no-subscribe] [--ignore-verdict] [--i-have-user-approval]
//        Uploader --subscribe-only <id> [--subscribe-only <id>]... [--i-have-user-approval]
//          subscribes the logged-in account to those items and waits (15 min cap) until Steam has them installed, then
//          prints each one's install folder - nothing is uploaded, no staged folder needed (a copy test of a release)
//   Dry run (prints every Steam call, Steam is NOT initialised) unless --i-have-user-approval is given.
using System.Diagnostics;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

const uint AppId = 1133870;   // GameApp: ApplicationId = 1133870u; no ConsumerApplicationId -> consumer = the app itself

string game2 = Environment.GetEnvironmentVariable("SE2_GAME2") ?? FindGame2();
AppDomain.CurrentDomain.AssemblyResolve += (s, e) =>
{
    var p = Path.Combine(game2, new System.Reflection.AssemblyName(e.Name).Name + ".dll");
    return File.Exists(p) ? Assembly.LoadFrom(p) : null;
};
try
{
    return Upload.Run(args, game2, AppId);
}
catch (UsageException ex)
{
    Console.Error.WriteLine("REFUSED: " + ex.Message);
    return 2;
}

static string FindGame2()
{
    var copy = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "OrbitalModBuild", "game2");
    return File.Exists(Path.Combine(copy, "VRage.Steam.dll")) ? copy : @"C:\Program Files (x86)\Steam\steamapps\common\SpaceEngineers2\Game2";
}

class UsageException(string m) : Exception(m);

static class Upload
{
    const int SteamTitleMax = 128, SteamDescriptionMax = 8000, SteamMetadataMax = 5000;   // k_cchPublishedDocument*Max, k_cchDeveloperMetadataMax
    const int ModHubNameMax = 50, ModHubSummaryMax = 250;                                // ModLimit
    const string KvKey = "VRageProjectID";                                               // SteamKV.VRAGE_PROJECT_ID

    // --subscribe-only: SubscribeItem + DownloadItem, then GetItemState until installed (not downloading, no update
    // pending), then GetItemInstallInfo. Exit 0 when every item is installed, 1 otherwise.
    static int SubscribeOnly(List<ulong> ids, bool dry, uint appId)
    {
        Console.WriteLine(dry
            ? "=== DRY RUN: no Steam call is made. Real subscribe: add --i-have-user-approval ==="
            : "=== SUBSCRIBE ONLY (--i-have-user-approval): nothing is uploaded ===");
        var steam = new SteamCalls(dry, appId);
        if (!dry) steam.Init();
        try
        {
            int bad = 0;
            foreach (var id in ids)
            {
                steam.SubscribeItem(id);
                string folder = steam.WaitInstalled(id, TimeSpan.FromMinutes(15));
                if (dry) continue;
                if (folder == null) { bad++; Console.WriteLine($"  item {id}: NOT installed"); }
                else Console.WriteLine($"  item {id}: installed at {folder}");
            }
            return bad == 0 ? 0 : 1;
        }
        finally { steam.Shutdown(); }
    }

    public static int Run(string[] args, string game2, uint appId)
    {
        var o = Options.Parse(args);
        bool dry = !o.Approved;
        if (o.SubscribeOnly.Count > 0) return SubscribeOnly(o.SubscribeOnly, dry, appId);
        Console.WriteLine(dry
            ? "=== DRY RUN: no Steam call is made (Steam is not even initialised). Real upload: add --i-have-user-approval ==="
            : "=== REAL UPLOAD (--i-have-user-approval) ===");

        // ---- the staged folder ---------------------------------------------------------------------------------------
        string outDir = Path.GetFullPath(o.StageDir);
        string pubPath = Path.Combine(outDir, "publish.json");
        if (!File.Exists(pubPath)) throw new UsageException($"no {pubPath} - stage the mod with stage.py first");
        var pub = JsonNode.Parse(File.ReadAllText(pubPath))!.AsObject();
        string content = Path.Combine(outDir, "content");
        if (!Directory.Exists(content)) throw new UsageException("no staged content folder " + content);
        string projFile = (string)pub["project_file"]!;
        string stagedProj = Path.Combine(content, projFile);
        if (!File.Exists(stagedProj)) throw new UsageException("no staged " + stagedProj);
        var spj = JsonNode.Parse(File.ReadAllText(stagedProj))!["$Value"]!;
        if ((string)spj["Type"] != "Mod") throw new UsageException("the staged project is not of Type Mod (SteamProjectLocatorComponent refuses it)");
        Guid guid = Guid.Parse((string)spj["ProjectID"]!["Guid"]!);
        if (guid != Guid.Parse((string)pub["project_guid"]!)) throw new UsageException("publish.json and the staged .vrgproj disagree on the project GUID");
        var verdict = pub["gameverdict"];
        bool noScripts = ((int?)pub["scripts"] ?? 0) == 0;
        bool verdictOk = noScripts || (verdict != null && (bool?)verdict["ok"] == true);   // (content-only mods: nothing to compile)
        if (!verdictOk && !o.IgnoreVerdict)
            throw new UsageException("the staged folder has no passing GameVerdict (the game would not compile its scripts) - fix and re-stage, or --ignore-verdict");
        if (!Directory.Exists(Path.Combine(content, "Scripts"))) Warn("the staged folder has no Scripts/ - not a script mod?");
        foreach (var f in Directory.EnumerateFileSystemEntries(content, "*", SearchOption.AllDirectories))
            if (Path.GetFileName(f).StartsWith('.')) throw new UsageException("hidden entry in the staged folder (ModHub never uploads those): " + f);

        string title = o.Title ?? (string)pub["title"]!;
        string summary = o.Summary ?? (string)pub["summary"] ?? "";
        string description = o.DescriptionFile != null ? File.ReadAllText(o.DescriptionFile) : summary;
        var tags = (o.Tags ?? pub["tags"]!.AsArray().Select(t => (string)t!).ToList()).ToList();
        if (!tags.Contains("UGCModTypeTag")) tags.Add("UGCModTypeTag");   // ModTag.TypeTag, always appended
        tags = tags.Distinct().ToList();
        string thumb = (string)pub["thumb"] is string th ? Path.Combine(content, th) : null;
        if (thumb != null && (!File.Exists(thumb) || new FileInfo(thumb).Length >= 1024 * 1024)) throw new UsageException("thumb.jpg missing or >= 1 MB");
        if (thumb == null) Warn("no thumb.jpg: the item gets no preview image");
        if (title.Length > SteamTitleMax) throw new UsageException($"title is {title.Length} chars (Steam max {SteamTitleMax})");
        if (description.Length > SteamDescriptionMax) throw new UsageException($"description is {description.Length} chars (Steam max {SteamDescriptionMax})");
        if (title.Length > ModHubNameMax) Warn($"title is {title.Length} chars (ModHub allows {ModHubNameMax})");
        if (summary.Length > ModHubSummaryMax) Warn($"summary is {summary.Length} chars (ModHub allows {ModHubSummaryMax})");
        if (summary.Trim() is "" or "Mod description") Warn($"summary is the placeholder '{summary}'");
        Version version = (string)spj["Version"] is string vs && vs.Length > 0 ? Version.Parse(vs) : null;
        string changeNote = o.ChangeNoteFile != null ? File.ReadAllText(o.ChangeNoteFile) : (o.ChangeNote ?? "");   // the game sends ""

        ulong recordedId = (ulong?)pub["mod_id"] ?? 0;
        string mode = o.Mode ?? (recordedId != 0 ? "update" : "create");
        ulong itemId = o.ItemId ?? (mode == "create" ? 0 : recordedId);
        if (mode != "create" && itemId == 0) throw new UsageException($"--{mode} needs an item id");
        if (mode == "create" && recordedId != 0)
            throw new UsageException($"the project already records Workshop item {recordedId} (ProjectID.ModId): one project GUID = one item - use --update {recordedId}");

        Console.WriteLine($"staged:      {content}");
        Console.WriteLine($"mode:        {mode}{(itemId != 0 ? " item " + itemId : "")}");
        Console.WriteLine($"project:     {guid}  (KV tag {KvKey}={SteamGuid(game2, guid)})");
        Console.WriteLine($"title:       {title}");
        Console.WriteLine($"summary:     {summary}");
        Console.WriteLine($"tags:        {string.Join(", ", tags)}");
        Console.WriteLine($"files:       {pub["files"]} ({(long?)pub["bytes"] / 1048576.0:0.00} MB), scripts {pub["scripts"]}, GameVerdict {(verdictOk ? "OK" : "NOT OK")}");
        Console.WriteLine();

        var steam = new SteamCalls(dry, appId);
        if (!dry) steam.Init();
        ulong author = steam.Author();

        // ---- update / content-only: the item must be ours and carry our project GUID ------------------------------------
        string visibility = o.Visibility;
        if (mode != "create")
        {
            var info = steam.QueryItem(itemId);
            if (info != null)
            {
                if (info.Owner != author) throw new UsageException($"item {itemId} belongs to {info.Owner}, not to the logged-in user {author}");
                if (info.ProjectKv != null && TryParseSteamGuid(info.ProjectKv, out var kvGuid) && kvGuid != guid)   // InvalidModUploadReason.IdMismatch
                    throw new UsageException($"item {itemId} was recorded with project ID {info.ProjectKv}, the staged project is {guid}");
                if (info.ProjectKv == null) CheckNoOtherItem(steam, game2, guid, itemId);   // the game's else-branch (IdExists)
                visibility ??= info.Visibility;                                             // ModHub keeps the current visibility
            }
        }
        else
        {
            CheckNoOtherItem(steam, game2, guid, 0);
            visibility ??= "unlisted";   // user decision: unlisted (ModHub would force Public)
        }

        if (mode == "create")
        {
            itemId = steam.CreateItem();
            if (!dry) Console.WriteLine($"  -> created Workshop item {itemId}  https://steamcommunity.com/sharedfiles/filedetails/?id={itemId}");
        }
        ulong idForFiles = itemId != 0 ? itemId : 0;

        // ---- the staged .vrgproj records the item (ModHub's UpdateModId runs before the staging copy) -------------------
        PatchModId(stagedProj, idForFiles, dry, "staged .vrgproj");

        try
        {
            string metadata = BuildMetadata(game2, idForFiles, author, guid, version, title, summary, visibility, tags, thumb != null ? "thumb.jpg" : "");
            int metaBytes = Encoding.UTF8.GetByteCount(metadata);
            string bundle = (string)JsonNode.Parse(metadata)!["$Bundles"]?["VRage"];
            if (bundle != "2.4.0.95")
                Warn($"the game is VRage {bundle}, this tool replicates 2.4.0.95's ModHub/SteamUGCServiceComponent - re-check those classes after a game update");
            VerifyMetadata(game2, metadata, guid, idForFiles);
            if (metaBytes > SteamMetadataMax) throw new UsageException($"metadata is {metaBytes} bytes (Steam max {SteamMetadataMax}) - shorten the summary");
            string metaOut = Path.Combine(outDir, dry ? "uploader-dryrun-metadata.json" : "uploaded-metadata.json");
            File.WriteAllText(metaOut, metadata);
            Console.WriteLine($"  metadata ({metaBytes} bytes, the game's serializer, round-trip checked with the game's deserializer): {metaOut}");
            if (dry && idForFiles == 0) Console.WriteLine("  (dry run of a new item: Id / ModId / Author are 0 - the real run fills in the created id and your SteamID)");

            if (mode == "content-only")
            {
                var h = steam.StartItemUpdate(itemId);
                steam.SetItemContent(h, content);
                steam.Submit(h, changeNote);
            }
            else
            {
                // UpdateWorkshopItem, call for call
                var h = steam.StartItemUpdate(itemId);
                steam.RemoveItemKeyValueTags(h, KvKey);
                steam.AddItemKeyValueTag(h, KvKey, SteamGuid(game2, guid));
                steam.SetItemTitle(h, title);
                steam.SetItemTags(h, tags);
                steam.SetItemVisibility(h, visibility);
                if (!string.IsNullOrWhiteSpace(description)) steam.SetItemDescription(h, description);
                steam.SetItemContent(h, content);
                if (thumb != null) steam.SetItemPreview(h, thumb);
                steam.SetItemMetadata(h, metadata);
                steam.Submit(h, changeNote);
                if (!o.NoSubscribe) steam.SubscribeItem(itemId);   // UploadUGCDataAsync subscribes after the upload
            }
        }
        catch (Exception ex) when (mode == "create" && !dry && itemId != 0)
        {
            Console.Error.WriteLine($"UPLOAD FAILED after creating item {itemId}: {ex.Message}");
            Console.Error.WriteLine($"  The item exists (empty). Retry with --update {itemId}, or delete it on its Workshop page. Not deleted automatically.");
            WriteBack(pub, outDir, itemId, title, visibility, dry: false, failed: true);
            throw;
        }

        WriteBack(pub, outDir, itemId, title, visibility, dry, failed: false);
        if (!dry) steam.Shutdown();
        Console.WriteLine(dry ? "\nDRY RUN complete - nothing was sent to Steam." : "\nUPLOAD complete.");
        return 0;
    }

    static void CheckNoOtherItem(SteamCalls steam, string game2, Guid guid, ulong self)
    {
        ulong other = steam.FindItemByProjectId(SteamGuid(game2, guid));
        if (other != 0 && other != self)
            throw new UsageException($"Workshop item {other} already carries project ID {guid}: one project GUID = one item (InvalidModUploadReason.IdExists) - use --update {other}");
    }

    static void Warn(string m) => Console.WriteLine("WARNING: " + m);

    // ---- the ModId the game writes: UGCItemId{Id = UGCHelper.GuidFromULong(id), ServiceName "Steam"}, ServiceId "Steam" ----
    static readonly Regex ProjectIdBlock = new("(\"ProjectID\"\\s*:\\s*\\{.*?\"ModId\"\\s*:\\s*)(null|\\{.*?\\})(\\s*,\\s*\"ServiceId\"\\s*:\\s*)\"[^\"]*\"", RegexOptions.Singleline);

    static string WithModId(string text, ulong id)
    {
        string nl = text.Contains("\r\n") ? "\r\n" : "\n";
        var m = ProjectIdBlock.Match(text);
        if (!m.Success) throw new UsageException("cannot find ProjectID.ModId in the .vrgproj");
        string indent = Regex.Match(text.Substring(0, m.Groups[2].Index), "([ \\t]*)\"ModId\"").Groups[1].Value;
        string modId = id == 0 ? "null" : "{" + nl + indent + "  \"Id\": \"" + GuidFromULong(id) + "\"," + nl + indent + "  \"ServiceName\": \"Steam\"" + nl + indent + "}";
        return text.Substring(0, m.Index) + m.Groups[1].Value + modId + m.Groups[3].Value + (id == 0 ? "\"\"" : "\"Steam\"") + text.Substring(m.Index + m.Length);
    }

    static void PatchModId(string path, ulong id, bool dry, string what)
    {
        if (id == 0) { Console.WriteLine($"  [{(dry ? "dry-run" : "real")}] {what}: ProjectID.ModId <- the new item id (after CreateItem)"); return; }
        string text = File.ReadAllText(path), patched = WithModId(text, id);
        if (patched == text) { Console.WriteLine($"  {what}: ProjectID.ModId already {id}"); return; }
        if (dry) { Console.WriteLine($"  [dry-run] {what}: ProjectID.ModId <- {GuidFromULong(id)} (item {id})"); return; }
        File.WriteAllText(path, patched);
        Console.WriteLine($"  {what}: ProjectID.ModId <- {GuidFromULong(id)} (item {id})");
    }

    // WorkshopComponent.cs:215-218 writes the new id into the project's .vrgproj. Here: a COPY next to the staging (the live
    // mod folder is never written) + published.json; copy it over the live .vrgproj (or commit the same ModId block) yourself.
    static void WriteBack(JsonObject pub, string outDir, ulong id, string title, string visibility, bool dry, bool failed)
    {
        string live = Path.Combine((string)pub["mod_dir"]!, (string)pub["project_file"]!);
        if (dry)
        {
            Console.WriteLine($"  [dry-run] would write {Path.Combine(outDir, "published.json")} and {Path.Combine(outDir, "writeback", (string)pub["project_file"]!)}" +
                              " (the live .vrgproj with ProjectID.ModId set; the live file is never touched). Its ProjectID would read:");
            if (File.Exists(live))
            {
                string t = WithModId(File.ReadAllText(live), id != 0 ? id : 1);   // (1: a stand-in for the id CreateItem returns)
                var m = Regex.Match(t, "\"ProjectID\"\\s*:\\s*\\{.*?\"ServiceId\"[^\\n]*", RegexOptions.Singleline);
                foreach (var l in m.Value.Split('\n')) Console.WriteLine("      " + l.TrimEnd('\r'));
            }
            return;
        }
        var res = new JsonObject
        {
            ["item_id"] = id, ["url"] = $"https://steamcommunity.com/sharedfiles/filedetails/?id={id}", ["title"] = title,
            ["visibility"] = visibility, ["mod_id_guid"] = GuidFromULong(id), ["upload_failed"] = failed,
            ["at"] = DateTime.Now.ToString("s"), ["project_guid"] = (string)pub["project_guid"],
        };
        File.WriteAllText(Path.Combine(outDir, "published.json"), res.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        if (File.Exists(live))
        {
            Directory.CreateDirectory(Path.Combine(outDir, "writeback"));
            string wb = Path.Combine(outDir, "writeback", Path.GetFileName(live));
            File.WriteAllText(wb, WithModId(File.ReadAllText(live), id));
            Console.WriteLine($"  write-back: {wb}  -> copy over {live} and commit (keeps ModId = item {id}; one GUID = one item)");
        }
    }

    public static string GuidFromULong(ulong id)   // UGCHelper.GuidFromULong: bytes (0UL, id) little-endian
    {
        var b = new byte[16];
        BitConverter.GetBytes(id).CopyTo(b, 8);
        return new Guid(b).ToString();
    }

    static bool TryParseSteamGuid(string s, out Guid g) => Guid.TryParse(s.Replace('_', '-'), out g);   // SteamKV.TryParseGuid

    // ---- the game's own code for the KV value and the metadata string (VRage.Steam / VRage.Core / VRage.Library) ----------
    [MethodImpl(MethodImplOptions.NoInlining)]
    static string SteamGuid(string game2, Guid guid)
    {
        var kv = SteamAsm(game2).GetType("Keen.VRage.Steam.Data.SteamKV", true)!;
        return (string)kv.GetMethod("GuidToSteam", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null, [guid])!;
    }

    static Assembly _steamAsm;
    static Assembly SteamAsm(string game2) => _steamAsm ??= Assembly.LoadFrom(Path.Combine(game2, "VRage.Steam.dll"));

    [MethodImpl(MethodImplOptions.NoInlining)]
    static string BuildMetadata(string game2, ulong fileId, ulong author, Guid guid, Version version, string title, string summary,
                                string visibility, List<string> tags, string previewFile)
        => GameTypes.BuildMetadata(SteamAsm(game2), fileId, author, guid, version, title, summary, visibility, tags, previewFile);

    [MethodImpl(MethodImplOptions.NoInlining)]
    static void VerifyMetadata(string game2, string json, Guid guid, ulong fileId) => GameTypes.Verify(SteamAsm(game2), json, guid, fileId);
}

// Everything touching game types sits here, so it is JIT-compiled only after the AssemblyResolve hook is in place.
static class GameTypes
{
    const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;

    static bool _metadataPushed;

    // The serializers find their types through the VRage metadata context (the game pushes one at startup).
    static void EnsureMetadataContext(Assembly steam)
    {
        if (_metadataPushed) return;
        Keen.VRage.Library.Utils.Singleton<Keen.VRage.Library.Reflection.MetadataManager>.Instance.PushContext(
            typeof(Keen.VRage.Library.Serialization.SerializationHelper).Assembly, typeof(Keen.VRage.Core.Services.Data.ModMetadata).Assembly, steam);
        _metadataPushed = true;
    }

    public static string BuildMetadata(Assembly steam, ulong fileId, ulong author, Guid guid, Version version, string title, string summary,
                                       string visibility, List<string> tags, string previewFile)
    {
        EnsureMetadataContext(steam);
        var svc = (Keen.VRage.Library.Utils.StringId)"Steam";   // GameServiceNames.ServiceSteam
        var itemId = new Keen.VRage.Core.Services.Data.UGCItemId(Keen.VRage.Core.Services.UGCHelper.GuidFromULong(fileId), svc);
        // WorkshopComponent.PublishAsync's description + UploadUGCDataAsync's ProjectID rewrite
        var description = new Keen.VRage.Core.Services.Data.UGCBaseMetadata
        {
            Title = title,
            Description = summary,
            Visiblity = visibility switch
            {
                "public" => Keen.VRage.Core.Services.Data.UGCVisiblity.Public,
                "friends" => Keen.VRage.Core.Services.Data.UGCVisiblity.FriendsOnly,
                "private" => Keen.VRage.Core.Services.Data.UGCVisiblity.Private,
                _ => Keen.VRage.Core.Services.Data.UGCVisiblity.Unlisted,
            },
            Tags = tags.ToArray(),
            PreviewFilePath = previewFile,
        };
#pragma warning disable CS0618 // the game sets ModId itself (UploadUGCDataAsync)
        var projectId = new Keen.VRage.Core.Project.VRageProjectID { Guid = guid, ModId = itemId, ServiceId = svc.ToString() };
#pragma warning restore CS0618
        var meta = new Keen.VRage.Core.Services.Data.ModMetadata { Id = itemId, ProjectID = projectId, Version = version, BaseMetadata = description };
        var t = steam.GetType("Keen.VRage.Steam.Data.SteamUgcMetadata", true)!;
        object sm = Activator.CreateInstance(t, nonPublic: true)!;
        Set(t, sm, "Id", fileId);
        Set(t, sm, "FullMetadata", meta);
        Set(t, sm, "Author", author);
        Set(t, sm, "LastLocallyUpdated", DateTime.Now);
        Set(t, sm, "IsReferenceToRemote", false);   // the value SetItemMetadata gets (the upload-cache copy's)
        // SteamUgcMetadata.Serialize -> SerializationHelper.SerializeAsync(new SerializationContext(fs, "Steam UGC Metadata {id}"), value)
        var ser = typeof(Keen.VRage.Library.Serialization.SerializationHelper).GetMethods(BindingFlags.Public | BindingFlags.Static)
            .Single(m => m.Name == "Serialize" && m.IsGenericMethodDefinition && m.GetParameters().Length == 3
                         && m.GetParameters()[0].ParameterType == typeof(Keen.VRage.Library.Serialization.SerializationContext))
            .MakeGenericMethod(t);
        using var ms = new MemoryStream();
        ser.Invoke(null, [new Keen.VRage.Library.Serialization.SerializationContext(ms, $"Steam UGC Metadata {fileId}"), sm,
                          Keen.VRage.Library.Serialization.SerializerFormat.Json]);
        return Encoding.UTF8.GetString(ms.ToArray());
    }

    // What the downloading game does with the string: SteamUgcMetadata.Deserialize, then TryGetModProjectID for the KV tag.
    public static void Verify(Assembly steam, string json, Guid guid, ulong fileId)
    {
        EnsureMetadataContext(steam);
        var t = steam.GetType("Keen.VRage.Steam.Data.SteamUgcMetadata", true)!;
        var de = typeof(Keen.VRage.Library.Serialization.SerializationHelper).GetMethods(BindingFlags.Public | BindingFlags.Static)
            .Single(m => m.Name == "Deserialize" && m.IsGenericMethodDefinition && m.GetParameters().Length == 2
                         && m.GetParameters()[0].ParameterType == typeof(Keen.VRage.Library.Serialization.SerializationContext))
            .MakeGenericMethod(t);
        object back = de.Invoke(null, [new Keen.VRage.Library.Serialization.SerializationContext(new MemoryStream(Encoding.UTF8.GetBytes(json)), "Steam UGC Metadata"),
                                       Keen.VRage.Library.Serialization.SerializerFormat.Json])!;
        if ((ulong)t.GetProperty("Id", All)!.GetValue(back)! != fileId) throw new UsageException("metadata round trip: Id changed");
        if (t.GetProperty("FullMetadata", All)!.GetValue(back) is not Keen.VRage.Core.Services.Data.ModMetadata mm)
            throw new UsageException("metadata round trip: FullMetadata is not a ModMetadata");
        if (mm.ProjectID.Guid != guid) throw new UsageException("metadata round trip: ProjectID.Guid changed");
        var ext = steam.GetType("Keen.VRage.Steam.Extensions.SteamHelperExtensions", true)!;
        var pid = ext.GetMethod("TryGetModProjectID", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null, [back]);
        if (pid is not Keen.VRage.Core.Project.VRageProjectID p || p.Guid != guid)
            throw new UsageException("metadata round trip: the game's TryGetModProjectID does not find the project (no VRageProjectID KV tag would be written)");
    }

    static void Set(Type t, object o, string name, object v)
    {
        var p = t.GetProperty(name, All);
        if (p != null) { p.SetValue(o, v); return; }
        var f = t.GetField(name, All) ?? throw new MissingMemberException(t.FullName, name);
        f.SetValue(o, v);
    }
}

class ItemInfo
{
    public ulong Owner;
    public string Visibility;
    public string ProjectKv;
    public string Title;
}

// Every Steam call goes through here: printed always, executed only on a real run.
class SteamCalls(bool dry, uint appId)
{
    readonly Steamworks.AppId_t _app = new(appId);
    bool _init;

    void Log(string call) => Console.WriteLine($"  [{(dry ? "dry-run" : "steam")}] {call}");
    static string Q(string s) => s == null ? "null" : "\"" + (s.Length > 70 ? s[..70].Replace("\n", "\\n").Replace("\r", "") + $"...\" ({s.Length} chars)" : s.Replace("\n", "\\n").Replace("\r", "") + "\"");

    public void Init()
    {
        Log("SteamAPI.InitEx()   (steam_appid.txt = " + appId + ", next to the exe)");
        var r = Steamworks.SteamAPI.InitEx(out string err);
        if (r != Steamworks.ESteamAPIInitResult.k_ESteamAPIInitResult_OK) throw new UsageException($"SteamAPI.InitEx: {r} {err} (is Steam running and logged in?)");
        _init = true;
        if (!Steamworks.SteamApps.BIsSubscribedApp(_app)) throw new UsageException($"the logged-in Steam user does not own app {appId}");
    }

    public void Shutdown() { if (_init) Steamworks.SteamAPI.Shutdown(); }

    public ulong Author()
    {
        if (dry) { Log("SteamUser.GetSteamID()   -> the uploader's SteamID (Author in the metadata; 0 in this dry run)"); return 0; }
        ulong id = Steamworks.SteamUser.GetSteamID().m_SteamID;
        Log($"SteamUser.GetSteamID() -> {id}");
        return id;
    }

    T Wait<T>(Steamworks.SteamAPICall_t call, string what, TimeSpan timeout, Action progress = null) where T : struct
    {
        T result = default; bool done = false, ioFail = false;
        var cr = Steamworks.CallResult<T>.Create((r, fail) => { result = r; ioFail = fail; done = true; });
        cr.Set(call);
        var sw = Stopwatch.StartNew();
        var last = TimeSpan.Zero;
        while (!done)
        {
            Steamworks.SteamAPI.RunCallbacks();
            Thread.Sleep(50);
            if (progress != null && sw.Elapsed - last > TimeSpan.FromSeconds(2)) { progress(); last = sw.Elapsed; }
            if (sw.Elapsed > timeout) throw new TimeoutException(what + " timed out");
        }
        if (ioFail) throw new IOException(what + ": IO failure");
        return result;
    }

    public ulong FindItemByProjectId(string kvValue)
    {
        Log($"SteamUGC.CreateQueryAllUGCRequest(k_EUGCQuery_RankedByLastUpdatedDate, k_EUGCMatchingUGCType_Items, {appId}, {appId}, page 1)");
        Log("SteamUGC.SetAllowCachedResponse(q, 0); SteamUGC.SetReturnOnlyIDs(q, true)");
        Log($"SteamUGC.AddRequiredKeyValueTag(q, \"VRageProjectID\", \"{kvValue}\"); SteamUGC.SendQueryUGCRequest(q)   (refuse if any item has it)");
        if (dry) return 0;
        var q = Steamworks.SteamUGC.CreateQueryAllUGCRequest(Steamworks.EUGCQuery.k_EUGCQuery_RankedByLastUpdatedDate,
            Steamworks.EUGCMatchingUGCType.k_EUGCMatchingUGCType_Items, _app, _app, 1u);
        try
        {
            Steamworks.SteamUGC.SetAllowCachedResponse(q, 0u);
            Steamworks.SteamUGC.SetReturnOnlyIDs(q, true);
            Steamworks.SteamUGC.AddRequiredKeyValueTag(q, "VRageProjectID", kvValue);
            var r = Wait<Steamworks.SteamUGCQueryCompleted_t>(Steamworks.SteamUGC.SendQueryUGCRequest(q), "SendQueryUGCRequest", TimeSpan.FromMinutes(1));
            if (r.m_eResult != Steamworks.EResult.k_EResultOK) throw new IOException("query: " + r.m_eResult);
            if (r.m_unNumResultsReturned == 0) { Log(" -> no item has this project ID"); return 0; }
            Steamworks.SteamUGC.GetQueryUGCResult(q, 0, out var d);
            Log($" -> item {d.m_nPublishedFileId.m_PublishedFileId} has this project ID");
            return d.m_nPublishedFileId.m_PublishedFileId;
        }
        finally { Steamworks.SteamUGC.ReleaseQueryUGCRequest(q); }
    }

    public ItemInfo QueryItem(ulong id)
    {
        Log($"SteamUGC.CreateQueryUGCDetailsRequest([{id}]); SetReturnMetadata; SetReturnKeyValueTags; SetAllowCachedResponse(0); SendQueryUGCRequest");
        Log("  -> check: owner == you, KV VRageProjectID == the staged GUID (IdMismatch), keep its current visibility unless --visibility");
        if (dry) return null;
        var q = Steamworks.SteamUGC.CreateQueryUGCDetailsRequest([new Steamworks.PublishedFileId_t(id)], 1);
        try
        {
            Steamworks.SteamUGC.SetReturnMetadata(q, true);
            Steamworks.SteamUGC.SetReturnKeyValueTags(q, true);
            Steamworks.SteamUGC.SetAllowCachedResponse(q, 0u);
            var r = Wait<Steamworks.SteamUGCQueryCompleted_t>(Steamworks.SteamUGC.SendQueryUGCRequest(q), "SendQueryUGCRequest", TimeSpan.FromMinutes(1));
            if (r.m_eResult != Steamworks.EResult.k_EResultOK || r.m_unNumResultsReturned == 0) throw new UsageException($"item {id} not found ({r.m_eResult})");
            Steamworks.SteamUGC.GetQueryUGCResult(q, 0, out var d);
            if (d.m_eResult != Steamworks.EResult.k_EResultOK) throw new UsageException($"item {id}: {d.m_eResult}");
            var info = new ItemInfo
            {
                Owner = d.m_ulSteamIDOwner, Title = d.m_rgchTitle,
                Visibility = d.m_eVisibility switch
                {
                    Steamworks.ERemoteStoragePublishedFileVisibility.k_ERemoteStoragePublishedFileVisibilityPublic => "public",
                    Steamworks.ERemoteStoragePublishedFileVisibility.k_ERemoteStoragePublishedFileVisibilityFriendsOnly => "friends",
                    Steamworks.ERemoteStoragePublishedFileVisibility.k_ERemoteStoragePublishedFileVisibilityPrivate => "private",
                    _ => "unlisted",
                },
            };
            uint n = Steamworks.SteamUGC.GetQueryUGCNumKeyValueTags(q, 0);
            for (uint k = 0; k < n; k++)
                if (Steamworks.SteamUGC.GetQueryUGCKeyValueTag(q, 0, k, out string key, 256, out string val, 256) && key == "VRageProjectID") info.ProjectKv = val;
            Log($" -> '{info.Title}', owner {info.Owner}, {info.Visibility}, VRageProjectID={info.ProjectKv ?? "(none)"}");
            return info;
        }
        finally { Steamworks.SteamUGC.ReleaseQueryUGCRequest(q); }
    }

    public ulong CreateItem()
    {
        Log($"SteamUGC.CreateItem({appId}, k_EWorkshopFileTypeFirst)   (SteamUGCServiceComponent.CreateWorkshopItem)");
        if (dry) return 0;
        var r = Wait<Steamworks.CreateItemResult_t>(Steamworks.SteamUGC.CreateItem(_app, Steamworks.EWorkshopFileType.k_EWorkshopFileTypeFirst), "CreateItem", TimeSpan.FromMinutes(1));
        if (r.m_eResult != Steamworks.EResult.k_EResultOK) throw new IOException("CreateItem: " + r.m_eResult);
        if (r.m_bUserNeedsToAcceptWorkshopLegalAgreement) Console.WriteLine("  NOTE: accept the Steam Workshop legal agreement, or the item stays hidden");
        return r.m_nPublishedFileId.m_PublishedFileId;
    }

    public Steamworks.UGCUpdateHandle_t StartItemUpdate(ulong id)
    {
        Log($"SteamUGC.StartItemUpdate({appId}, {(id == 0 ? "<new item id>" : id)}) -> handle");
        return dry ? default : Steamworks.SteamUGC.StartItemUpdate(_app, new Steamworks.PublishedFileId_t(id));
    }

    void Must(bool ok, string what) { if (!ok) throw new IOException(what + " returned false"); }

    public void RemoveItemKeyValueTags(Steamworks.UGCUpdateHandle_t h, string key)
    { Log($"SteamUGC.RemoveItemKeyValueTags(handle, {Q(key)})"); if (!dry) Steamworks.SteamUGC.RemoveItemKeyValueTags(h, key); }
    public void AddItemKeyValueTag(Steamworks.UGCUpdateHandle_t h, string key, string v)
    { Log($"SteamUGC.AddItemKeyValueTag(handle, {Q(key)}, {Q(v)})"); if (!dry) Steamworks.SteamUGC.AddItemKeyValueTag(h, key, v); }
    public void SetItemTitle(Steamworks.UGCUpdateHandle_t h, string v)
    { Log($"SteamUGC.SetItemTitle(handle, {Q(v)})"); if (!dry) Must(Steamworks.SteamUGC.SetItemTitle(h, v), "SetItemTitle"); }
    public void SetItemTags(Steamworks.UGCUpdateHandle_t h, List<string> v)
    { Log($"SteamUGC.SetItemTags(handle, [{string.Join(", ", v.Select(Q))}])"); if (!dry) Must(Steamworks.SteamUGC.SetItemTags(h, v), "SetItemTags"); }
    public void SetItemVisibility(Steamworks.UGCUpdateHandle_t h, string v)
    {
        var e = v switch
        {
            "public" => Steamworks.ERemoteStoragePublishedFileVisibility.k_ERemoteStoragePublishedFileVisibilityPublic,
            "friends" => Steamworks.ERemoteStoragePublishedFileVisibility.k_ERemoteStoragePublishedFileVisibilityFriendsOnly,
            "private" => Steamworks.ERemoteStoragePublishedFileVisibility.k_ERemoteStoragePublishedFileVisibilityPrivate,
            _ => Steamworks.ERemoteStoragePublishedFileVisibility.k_ERemoteStoragePublishedFileVisibilityUnlisted,
        };
        Log($"SteamUGC.SetItemVisibility(handle, {e})");
        if (!dry) Must(Steamworks.SteamUGC.SetItemVisibility(h, e), "SetItemVisibility");
    }
    public void SetItemDescription(Steamworks.UGCUpdateHandle_t h, string v)
    { Log($"SteamUGC.SetItemDescription(handle, {Q(v)})"); if (!dry) Steamworks.SteamUGC.SetItemDescription(h, v); }
    public void SetItemContent(Steamworks.UGCUpdateHandle_t h, string dir)
    {
        long n = Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories).LongCount();
        Log($"SteamUGC.SetItemContent(handle, {Q(dir)})   ({n} files)");
        if (!dry) Must(Steamworks.SteamUGC.SetItemContent(h, dir), "SetItemContent");
    }
    public void SetItemPreview(Steamworks.UGCUpdateHandle_t h, string file)
    { Log($"SteamUGC.SetItemPreview(handle, {Q(file)})   ({new FileInfo(file).Length} bytes)"); if (!dry) Must(Steamworks.SteamUGC.SetItemPreview(h, file), "SetItemPreview"); }
    public void SetItemMetadata(Steamworks.UGCUpdateHandle_t h, string v)
    { Log($"SteamUGC.SetItemMetadata(handle, <SteamUgcMetadata JSON, {Encoding.UTF8.GetByteCount(v)} bytes>)"); if (!dry) Must(Steamworks.SteamUGC.SetItemMetadata(h, v), "SetItemMetadata"); }

    public void Submit(Steamworks.UGCUpdateHandle_t h, string note)
    {
        Log($"SteamUGC.SubmitItemUpdate(handle, {Q(note)})   -> SubmitItemUpdateResult_t");
        if (dry) return;
        var r = Wait<Steamworks.SubmitItemUpdateResult_t>(Steamworks.SteamUGC.SubmitItemUpdate(h, note), "SubmitItemUpdate", TimeSpan.FromHours(2), () =>
        {
            var st = Steamworks.SteamUGC.GetItemUpdateProgress(h, out ulong done, out ulong total);
            Console.WriteLine($"    {st} {done / 1048576.0:0.0}/{total / 1048576.0:0.0} MB");
        });
        if (r.m_eResult != Steamworks.EResult.k_EResultOK) throw new IOException("SubmitItemUpdate: " + r.m_eResult);
        if (r.m_bUserNeedsToAcceptWorkshopLegalAgreement) Console.WriteLine("  NOTE: accept the Steam Workshop legal agreement, or the item stays hidden");
    }

    public string WaitInstalled(ulong id, TimeSpan timeout)
    {
        Log($"SteamUGC.DownloadItem({id}, true); poll SteamUGC.GetItemState / GetItemDownloadInfo until k_EItemStateInstalled; GetItemInstallInfo");
        if (dry) return null;
        var f = new Steamworks.PublishedFileId_t(id);
        Steamworks.SteamUGC.DownloadItem(f, true);
        var sw = Stopwatch.StartNew(); var last = TimeSpan.Zero;
        while (sw.Elapsed < timeout)
        {
            Steamworks.SteamAPI.RunCallbacks();
            var st = (Steamworks.EItemState)Steamworks.SteamUGC.GetItemState(f);
            bool installed = (st & Steamworks.EItemState.k_EItemStateInstalled) != 0;
            bool busy = (st & (Steamworks.EItemState.k_EItemStateDownloading | Steamworks.EItemState.k_EItemStateDownloadPending | Steamworks.EItemState.k_EItemStateNeedsUpdate)) != 0;
            if (installed && !busy && Steamworks.SteamUGC.GetItemInstallInfo(f, out ulong size, out string folder, 1024, out uint ts))
            {
                Log($" -> {id} installed: {folder} ({size / 1048576.0:0.0} MB, timestamp {ts})");
                return folder;
            }
            if (sw.Elapsed - last > TimeSpan.FromSeconds(5))
            {
                Steamworks.SteamUGC.GetItemDownloadInfo(f, out ulong done, out ulong total);
                Console.WriteLine($"    {id}: state {st}, {done / 1048576.0:0.0}/{total / 1048576.0:0.0} MB");
                last = sw.Elapsed;
            }
            Thread.Sleep(200);
        }
        return null;
    }

    public void SubscribeItem(ulong id)
    {
        Log($"SteamUGC.SubscribeItem({(id == 0 ? "<new item id>" : id)})   (UploadUGCDataAsync subscribes the uploader)");
        if (dry) return;
        var r = Wait<Steamworks.RemoteStorageSubscribePublishedFileResult_t>(Steamworks.SteamUGC.SubscribeItem(new Steamworks.PublishedFileId_t(id)), "SubscribeItem", TimeSpan.FromMinutes(1));
        if (r.m_eResult != Steamworks.EResult.k_EResultOK) Console.WriteLine("  subscribe: " + r.m_eResult + " (not fatal)");
    }
}

class Options
{
    public string StageDir, Mode, Visibility, Title, Summary, DescriptionFile, ChangeNote, ChangeNoteFile;
    public ulong? ItemId;
    public List<string> Tags;
    public List<ulong> SubscribeOnly = new();
    public bool Approved, NoSubscribe, IgnoreVerdict;

    public static Options Parse(string[] a)
    {
        var o = new Options();
        for (int i = 0; i < a.Length; i++)
        {
            string Next() => i + 1 < a.Length ? a[++i] : throw new UsageException(a[i] + " needs a value");
            switch (a[i])
            {
                case "--create": o.Mode = "create"; break;
                case "--update": o.Mode = "update"; o.ItemId = ulong.Parse(Next()); break;
                case "--content-only": o.Mode = "content-only"; o.ItemId = ulong.Parse(Next()); break;
                case "--visibility":
                    o.Visibility = Next().ToLowerInvariant();
                    if (o.Visibility is not ("unlisted" or "private" or "friends" or "public")) throw new UsageException("--visibility unlisted|private|friends|public");
                    break;
                case "--title": o.Title = Next(); break;
                case "--summary": o.Summary = Next(); break;
                case "--description-file": o.DescriptionFile = Next(); break;
                case "--tags": o.Tags = Next().Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList(); break;
                case "--change-note": o.ChangeNote = Next(); break;
                case "--change-note-file": o.ChangeNoteFile = Next(); break;
                case "--no-subscribe": o.NoSubscribe = true; break;
                case "--subscribe-only": o.SubscribeOnly.Add(ulong.Parse(Next())); break;
                case "--ignore-verdict": o.IgnoreVerdict = true; break;
                case "--dry-run": break;   // the default
                case "--i-have-user-approval": o.Approved = true; break;
                default:
                    if (a[i].StartsWith("--") || o.StageDir != null) throw new UsageException("unknown argument " + a[i]);
                    o.StageDir = a[i];
                    break;
            }
        }
        if (o.StageDir == null && o.SubscribeOnly.Count == 0) throw new UsageException("usage: Uploader <stage out dir> [--create | --update <id> | --content-only <id>] [--visibility unlisted|private|friends|public] [--title t] [--summary s] [--description-file f] [--tags a,b] [--change-note t | --change-note-file f] [--no-subscribe] [--ignore-verdict] [--i-have-user-approval]");
        if (a.Contains("--dry-run")) o.Approved = false;   // an explicit --dry-run always wins
        return o;
    }
}
