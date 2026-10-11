using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Web.Script.Serialization;
using UERegister;

internal static class RegressionTests
{
    private static int checks;
    private static void Check(bool value, string description) { if (!value) throw new Exception(description); checks++; Console.WriteLine("PASS: " + description); }
    private static void Reject(Action action, string description) { bool rejected = false; try { action(); } catch { rejected = true; } Check(rejected, description); }
    private sealed class MemoryRegistry : IBuildRegistry
    {
        public Dictionary<string,string> Values = new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase);
        public int Writes;
        public Dictionary<string,string> Read() { return new Dictionary<string,string>(Values, StringComparer.OrdinalIgnoreCase); }
        public void Set(string id, string path) { Values[id] = path; Writes++; }
        public void Delete(string id) { Values.Remove(id); Writes++; }
    }
    private static void Write(string path, string value = "") { Directory.CreateDirectory(Path.GetDirectoryName(path)); File.WriteAllText(path, value); }
    private static string Engine(string root, string relative, string editor = "UnrealEditor.exe")
    { string path = EngineDiscovery.Normalize(Path.Combine(root, relative)); Write(Path.Combine(path, "Engine", "Binaries", "Win64", editor)); return path; }
    private static int Main()
    {
        string root = Path.Combine(Path.GetTempPath(), "UERegister-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try { Run(root); Console.WriteLine(checks + " checks passed."); return 0; }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
        finally { Directory.Delete(root, true); }
    }
    private static void Run(string root)
    {
        string renameRoot = Engine(root, "rename-fixture");
        var renameRegistry = new MemoryRegistry();
        renameRegistry.Values["OriginalName"] = renameRoot;
        string renameKey = Path.Combine(renameRoot, "UERegister.key");
        Write(renameKey, "UpdatedName");
        var renameRunner = new CommandRunner(renameRegistry, new StringReader(""), new StringWriter(), new StringWriter(),
            Path.Combine(root, "no-launcher-manifest"), Path.Combine(root, "rename-backups"));
        Check(renameRunner.Run(new[] { "u", renameRoot }) == 0 && renameRegistry.Values["UpdatedName"] == renameRoot,
            "u registers the changed key-file name for an existing engine");
        Check(renameRegistry.Values["OriginalName"] == renameRoot, "renaming retains the old ID for existing project associations");
        Write(renameKey, "RenamedAgain");
        Check(renameRunner.Run(new[] { "u", renameRoot }) == 0 && renameRegistry.Values["RenamedAgain"] == renameRoot,
            "u rereads the key file on subsequent name changes");
        int renameWrites = renameRegistry.Writes;
        Check(renameRunner.Run(new[] { "u", renameRoot }) == 0 && renameRegistry.Writes == renameWrites,
            "u makes no registry changes when the key-file name already matches");
        string actualPath = EngineDiscovery.CanonicalDirectory(renameRoot);
        renameRegistry.Values["UpdatedName"] = renameRoot.ToUpperInvariant().Replace('\\', '/');
        renameRegistry.Values["RenamedAgain"] = renameRoot.ToUpperInvariant();
        Check(renameRunner.Run(new[] { "u", renameRoot.ToUpperInvariant(), "--dry-run" }) == 0 &&
            renameRegistry.Values["RenamedAgain"] != actualPath, "path spelling dry-run preserves registry values");
        Check(renameRunner.Run(new[] { "u", renameRoot.ToUpperInvariant() }) == 0 &&
            renameRegistry.Values["RenamedAgain"] == actualPath && renameRegistry.Values["UpdatedName"] == actualPath,
            "u corrects on-disk casing and separators for engine ID and aliases");
        renameWrites = renameRegistry.Writes;
        Check(renameRunner.Run(new[] { "u", renameRoot.ToUpperInvariant() }) == 0 && renameRegistry.Writes == renameWrites,
            "canonical path updates remain idempotent with differently cased input");
        Directory.Delete(renameRoot, true);
        var launcher = new List<EngineInfo>();
        var registry = new MemoryRegistry();
        string first = Engine(root, "deep/a/b/Source Engine");
        string second = Engine(root, "Other", "UE4Editor-Win64-Debug.exe");
        Check(EngineDiscovery.Scan(new[] { first }, s => { }).SequenceEqual(new[] { first }), "scan tests supplied engine root itself");
        var scan = EngineDiscovery.Scan(new[] { root, first }, s => { });
        Check(scan.Count == 2 && scan.Contains(first) && scan.Contains(second), "recursive scan deduplicates overlapping roots and supports Debug editor");
        Reject(() => EngineDiscovery.Scan(new[] { first, Path.Combine(root,"missing") }, s => { }), "invalid supplied path fails without scanning current directory");
        Write(Path.Combine(root,"skip","do.not.scan.ued")); Engine(root,"skip/hidden");
        Check(EngineDiscovery.Scan(new[] { root }, s => { }).Count == 2, "scan honors exclusion marker");
        string source = Path.Combine(root,"uncompiled");
        Directory.CreateDirectory(Path.Combine(source,"Engine","Source"));
        Write(Path.Combine(source,"Engine","Build","Build.version"), "{\"MajorVersion\":5,\"MinorVersion\":8,\"PatchVersion\":2}");
        Check(EngineDiscovery.Inspect(source) == EngineState.SourceOnly && EngineDiscovery.ReadVersion(source) == "5.8.2", "uncompiled source and version metadata detected");
        Write(Path.Combine(second,"Engine","Source","Runtime","Launch","Resources","Version.h"), "#define ENGINE_MAJOR_VERSION 4\n#define ENGINE_MINOR_VERSION 27\n#define ENGINE_PATCH_VERSION 2");
        Check(EngineDiscovery.ReadVersion(second) == "4.27.2", "version header fallback matches UEDesktop");
        registry.Values["{existing-id}"] = first;
        registry.Values["OldAlias"] = first;
        Check(RegistrationPlanner.Plan(new[] { first }, registry.Read(), launcher, false).Count == 0, "existing engine without key keeps all existing IDs");
        Write(Path.Combine(first,"UERegister.key"), "Friendly");
        var plan = RegistrationPlanner.Plan(new[] { first }, registry.Read(), launcher, false);
        RegistrationPlanner.Apply(plan, registry);
        Check(registry.Values.Count == 3 && registry.Values["OldAlias"] == first, "friendly registration preserves existing project aliases");
        Check(RegistrationPlanner.Plan(new[] { first }, registry.Read(), launcher, false).Count == 0, "repeat update is idempotent");
        Write(Path.Combine(second,"UERegister.key"), "Friendly");
        Reject(() => RegistrationPlanner.Plan(new[] { second }, registry.Read(), launcher, false), "different path cannot silently take an ID");
        Reject(() => RegistrationPlanner.Plan(new[] { first, second }, registry.Read(), launcher, true), "duplicate discovered IDs rejected even with replace and no-op first engine");
        Check(RegistrationPlanner.Plan(new[] { second }, registry.Read(), launcher, true).Single().Root == second, "explicit replacement permits relocating a selected ID");
        Write(Path.Combine(second,"UERegister.key"), "");
        Reject(() => RegistrationPlanner.Plan(new[] { second }, registry.Read(), launcher, false), "empty key rejected before mutations");
        File.Delete(Path.Combine(second,"UERegister.key"));
        Write(Path.Combine(second,"UE4Register.key"), "LegacyName\r\n");
        Check(RegistrationPlanner.Plan(new[] { second }, registry.Read(), launcher, false).Single().Id == "LegacyName", "legacy key remains supported");
        File.Delete(Path.Combine(second,"UE4Register.key"));
        plan = RegistrationPlanner.Plan(new[] { second }, registry.Read(), launcher, false);
        Guid id;
        Check(Guid.TryParse(plan.Single().Id, out id) && plan.Single().PersistKey, "new engine receives persistent GUID");
        RegistrationPlanner.Apply(plan, registry);
        Check(File.ReadAllText(Path.Combine(second,"UERegister.key")).Trim() == plan.Single().Id && RegistrationPlanner.Plan(new[] { second }, registry.Read(), launcher, false).Count == 0, "generated key round-trips without changing ID");
        var manifest = Path.Combine(root,"LauncherInstalled.dat");
        string plugin = Path.Combine(root,"plugin"); Directory.CreateDirectory(plugin);
        Write(manifest, new JavaScriptSerializer().Serialize(new { InstallationList = new[] {
            new { AppName="UE_5.8", InstallLocation=first }, new { AppName="UE_Plugin", InstallLocation=plugin },
            new { AppName="UE_5.8", InstallLocation=first }, new { AppName="OtherGame", InstallLocation=second } } }));
        launcher = EngineDiscovery.ReadLauncher(manifest);
        Check(launcher.Count == 1 && launcher[0].Root == first, "launcher discovery filters store content and duplicates");
        Check(RegistrationPlanner.Plan(new[] { first }, new Dictionary<string,string>(), launcher, false).Count == 0, "launcher engine excluded from custom registration");
        string backups = Path.Combine(root,"backups");
        string saved = Path.Combine(root,"snapshot.json");
        BackupStore.Save(saved, registry.Read());
        Check(BackupStore.Load(saved).Count == registry.Values.Count, "registry backup round-trip retains aliases");
        Reject(() => BackupStore.Save(saved, registry.Read()), "backup never overwrites previous recovery file");
        string badBackup = Path.Combine(root,"bad.json"); Write(badBackup,"{\"SchemaVersion\":99,\"Builds\":{}}");
        Reject(() => BackupStore.Load(badBackup), "unsupported backup rejected");
        var text = new StringWriter(); var errors = new StringWriter();
        var runner = new CommandRunner(registry, new StringReader(""), text, errors, manifest, backups);
        Check(runner.Run(new[] {"update", source,"--dry-run"}) == 0 && !File.Exists(Path.Combine(source,"UERegister.key")) && !Directory.Exists(backups), "dry-run changes neither registry, keys nor backups");
        Check(runner.Run(new[] {"update",Path.Combine(root,"absent")}) == 2, "invalid root produces argument failure");
        int priorWrites = registry.Writes;
        Check(runner.Run(new[] {"remove","Friendly","--non-interactive"}) == 2 && registry.Writes == priorWrites, "noninteractive remove requires explicit confirmation");
        Check(runner.Run(new[] {"remove","Friendly","--yes"}) == 0 && !registry.Values.ContainsKey("Friendly") && Directory.GetFiles(backups).Length == 1, "confirmed removal backs up before deleting only selected alias");
        Check(runner.Run(new[] {"restore",saved,"--yes"}) == 0 && registry.Values.ContainsKey("Friendly"), "restore recovers removed alias");
        registry.Values["Missing"] = Path.Combine(root,"gone"); registry.Values["Source"] = source;
        registry.Values["Invalid"] = plugin;
        Check(runner.Run(new[] {"clean","--yes"}) == 0 && !registry.Values.ContainsKey("Missing") && registry.Values.ContainsKey("Source") && registry.Values.ContainsKey("Invalid"), "cleanup removes missing engines but preserves source and ambiguous invalid folders");
        text.GetStringBuilder().Clear();
        Check(runner.Run(new[] {"ls","--json"}) == 0 && new JavaScriptSerializer().Deserialize<List<EngineInfo>>(text.ToString()).Count > 0, "ls alias produces clean machine-readable JSON");
        Check(runner.Run(new[] {"bogus"}) == 2 && runner.Run(new[] {"list","--bogus"}) == 2, "unknown commands and options fail");
        registry.Values["Friendly"] = second;
        priorWrites = registry.Writes;
        Check(runner.Run(new[] {"restore",saved,"--yes"}) == 1 && registry.Writes == priorWrites, "restore collisions fail before any registry changes");
        Check(runner.Run(new[] {"restore",saved,"--yes","--replace"}) == 0 && registry.Values["Friendly"] == first, "restore explicitly replaces conflicting IDs");
        var cancelRunner = new CommandRunner(registry, new StringReader("\n"), new StringWriter(), errors, manifest, backups);
        priorWrites = registry.Writes;
        Check(cancelRunner.Run(new[] {"remove","Friendly"}) == 0 && registry.Writes == priorWrites, "empty interactive confirmation cancels without writes");
        string unavailableDrive = Enumerable.Range('D', 'Z' - 'D' + 1).Select(c => ((char)c) + @":\")
            .FirstOrDefault(d => !Directory.GetLogicalDrives().Contains(d, StringComparer.OrdinalIgnoreCase));
        if (unavailableDrive != null)
        {
            registry.Values["Offline"] = unavailableDrive + "Engine";
            Check(EngineDiscovery.Inspect(registry.Values["Offline"]) == EngineState.Unavailable && runner.Run(new[] {"clean","--yes"}) == 0 && registry.Values.ContainsKey("Offline"),
                "unavailable drive is preserved by unattended cleanup");
        }
        string blockedBackups = Path.Combine(root,"file-not-directory"); File.WriteAllText(blockedBackups,"fixture");
        var blockedRunner = new CommandRunner(registry, new StringReader(""), new StringWriter(), errors, manifest, blockedBackups);
        priorWrites = registry.Writes;
        Check(blockedRunner.Run(new[] {"remove","Friendly","--yes"}) == 1 && registry.Writes == priorWrites, "backup failure prevents registry mutation");
        Write(manifest,"{}"); priorWrites = registry.Writes;
        Check(runner.Run(new[] {"update",source}) == 1 && registry.Writes == priorWrites, "missing launcher list is an error rather than permission to register retail builds");
        Write(manifest,"not JSON"); priorWrites = registry.Writes;
        Check(runner.Run(new[] {"update",source}) != 0 && registry.Writes == priorWrites, "unreadable launcher inventory prevents registering retail engines by accident");
        string selector = Path.Combine(source, "Engine", "Binaries", "Win64", "UnrealVersionSelector-Win64-Shipping.exe");
        Write(selector);
        Check(AssociationRepair.Find(new[] { source }) == selector, "source-build Shipping selector discovered without Launcher");
        Check(Options.Parse(new[] { "A" }).Command == "associations" && Options.Parse(new[] { "Associations" }).Command == "associations",
            "association command accepts short and long case-insensitive names");
        var start = AssociationRepair.StartInfo(selector);
        Check(start.FileName == selector && start.Arguments == "/fileassociations" && start.UseShellExecute && start.Verb == "runas",
            "association repair invokes Epic selector with elevation and correct argument");
        bool launched = false;
        Check(AssociationRepair.Run(selector, true, true, text, info => { launched = true; return 0; }) == 0 && !launched,
            "association dry-run does not launch or request elevation");
        Check(AssociationRepair.Run(selector, false, false, text, info => 0, verb => true) == 0 &&
            AssociationRepair.Run(selector, false, false, text, info => 1) == 1, "selector success and failure propagated");
        Check(AssociationRepair.Run(selector, false, false, text, info => { throw new System.ComponentModel.Win32Exception(1223); }) == 0,
            "UAC cancellation handled without failure");
        Check(AssociationRepair.Run(selector, false, false, text, info => 0, verb => verb == "open") == 1,
            "selector success cannot conceal a user-choice override blocking generation");
        Check(AssociationRepair.Run(null, true, false, text) == 1, "missing selector explains source-build instructions");
        Check(runner.Run(new[] { "a", source, "--dry-run" }) == 0 && registry.Writes == priorWrites,
            "association command dry-run leaves engine registrations unchanged");
        var help = new StringWriter(); CommandRunner.Help(help);
        Check(help.ToString().Contains(Environment.NewLine + Environment.NewLine + "Options:") && !help.ToString().Contains("Exit codes:"),
            "help separates commands and options and omits exit-code explanation");
    }
}
