using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Web.Script.Serialization;

namespace UERegister
{
    public sealed class Options
    {
        public string Command;
        public readonly List<string> Arguments = new List<string>();
        public bool DryRun, Yes, Json, Replace, NonInteractive;
        public static Options Parse(string[] args)
        {
            var options = new Options { Command = args.Length == 0 ? "help" : args[0].ToLowerInvariant() };
            var aliases = new Dictionary<string, string> { { "l", "list" }, { "ls", "list" }, { "u", "update" }, { "c", "clean" },
                { "f", "fullscan" }, { "r", "remove" }, { "a", "associations" }, { "--help", "help" }, { "-h", "help" }, { "/?", "help" } };
            if (aliases.ContainsKey(options.Command)) options.Command = aliases[options.Command];
            if (!new[] { "help", "list", "update", "clean", "fullscan", "remove", "backup", "restore", "associations" }.Contains(options.Command))
                throw new ArgumentException("Unknown command: " + options.Command);
            bool positionalOnly = false;
            foreach (string arg in args.Skip(1))
            {
                if (arg == "--" && !positionalOnly) { positionalOnly = true; continue; }
                if (positionalOnly || !arg.StartsWith("--", StringComparison.Ordinal)) options.Arguments.Add(arg);
                else switch (arg)
                {
                    case "--dry-run": options.DryRun = true; break;
                    case "--yes": options.Yes = true; break;
                    case "--json": options.Json = true; break;
                    case "--replace": options.Replace = true; break;
                    case "--non-interactive": options.NonInteractive = true; break;
                    default: throw new ArgumentException("Unknown option: " + arg);
                }
            }
            if (options.Json && options.Command != "list") throw new ArgumentException("--json is supported by list only.");
            if (options.Replace && !new[] { "update", "fullscan", "restore" }.Contains(options.Command)) throw new ArgumentException("--replace is supported by update, fullscan and restore only.");
            if (new[] { "list", "clean", "help" }.Contains(options.Command) && options.Arguments.Count != 0) throw new ArgumentException("This command does not accept paths or IDs.");
            if (options.Command == "remove" && options.Arguments.Count > 1) throw new ArgumentException("remove accepts one ID.");
            if (options.Command == "associations" && options.Arguments.Count > 1) throw new ArgumentException("associations accepts one optional engine root or selector executable.");
            if (new[] { "backup", "restore" }.Contains(options.Command) && options.Arguments.Count != 1) throw new ArgumentException(options.Command + " requires one backup filename.");
            return options;
        }
    }
    public sealed class CommandRunner
    {
        private readonly IBuildRegistry registry;
        private readonly TextReader input;
        private readonly TextWriter output;
        private readonly TextWriter error;
        private readonly string manifest, backupDirectory;
        private Options options;
        public CommandRunner(IBuildRegistry registry, TextReader input, TextWriter output, TextWriter error, string manifest, string backupDirectory)
        { this.registry = registry; this.input = input; this.output = output; this.error = error; this.manifest = manifest; this.backupDirectory = backupDirectory; }
        public int Run(string[] args, bool inputRedirected = false)
        {
            try
            {
                options = Options.Parse(args);
                options.NonInteractive |= inputRedirected;
                if (!options.Json) Header(output);
                if (options.Command == "help") { Help(output); return 0; }
                var current = registry.Read();
                switch (options.Command)
                {
                    case "associations":
                        string selector;
                        if (options.Arguments.Count == 1)
                        {
                            string path = Path.GetFullPath(options.Arguments[0]);
                            selector = File.Exists(path) && Path.GetFileName(path).StartsWith("UnrealVersionSelector", StringComparison.OrdinalIgnoreCase) &&
                                Path.GetExtension(path).Equals(".exe", StringComparison.OrdinalIgnoreCase) ? path : AssociationRepair.Find(new[] { path });
                            if (selector == null) throw new ArgumentException("No UnrealVersionSelector executable found at: " + path);
                        }
                        else selector = AssociationRepair.Find(AssociationRepair.LauncherRoots().Concat(current.Values).Concat(new[] { Environment.CurrentDirectory }));
                        return AssociationRepair.Run(selector, options.DryRun, options.NonInteractive, output);
                    case "list":
                        ListBuilds(current, options.Json);
                        return 0;
                    case "update": case "fullscan":
                        bool scanError = false;
                        int progressWidth = 0;
                        var roots = EngineDiscovery.Scan(options.Arguments.Count == 0 ? new[] { Environment.CurrentDirectory } : options.Arguments.ToArray(),
                            message => {
                                output.Write(new string(' ', progressWidth) + "\r");
                                error.WriteLine(message);
                                if (!message.StartsWith("Skipped junction/link:")) scanError = true;
                            },
                            folder => {
                                string message = "Scanning " + folder;
                                progressWidth = Math.Max(progressWidth, message.Length);
                                output.Write(message.PadRight(progressWidth) + "\r");
                                output.Flush();
                            });
                        output.WriteLine((scanError ? "Scan incomplete." : "Done.").PadRight(progressWidth));
                        var plan = RegistrationPlanner.Plan(roots, current, EngineDiscovery.ReadLauncher(manifest), options.Replace);
                        foreach (var item in plan) output.WriteLine("{0}register '{1}' @ {2}", options.DryRun ? "Would " : "Will ", item.Id, item.Root);
                        if (!options.DryRun && plan.Count > 0) { Backup(current); RegistrationPlanner.Apply(plan, registry); }
                        output.WriteLine("{0} engine(s) found; {1} registration change(s).", roots.Count, plan.Count);
                        if (scanError) { error.WriteLine("Scan incomplete. Cleanup was not run."); ListBuilds(registry.Read(), false); return 1; }
                        if (options.Command == "fullscan")
                        {
                            if (options.DryRun) foreach (var item in plan) current[item.Id] = item.Root;
                            int result = Clean(options.DryRun ? current : registry.Read());
                            ListBuilds(registry.Read(), false);
                            return result;
                        }
                        ListBuilds(registry.Read(), false);
                        return 0;
                    case "clean": return Clean(current);
                    case "remove":
                        string id = options.Arguments.FirstOrDefault();
                        if (id == null)
                        {
                            if (options.NonInteractive) throw new ArgumentException("Noninteractive remove requires an ID.");
                            foreach (var entry in current) output.WriteLine("{0} @ {1}", entry.Key, entry.Value);
                            output.Write("Build ID to remove (empty cancels): "); id = input.ReadLine();
                            if (string.IsNullOrEmpty(id)) return 0;
                        }
                        if (!current.ContainsKey(id)) throw new ArgumentException("No registered build with ID '" + id + "'.");
                        if (options.DryRun) { output.WriteLine("Would remove '{0}' @ {1}", id, current[id]); return 0; }
                        if (!Confirm("Remove '" + id + "' @ " + current[id] + "?")) return options.NonInteractive ? 2 : 0;
                        Backup(current); registry.Delete(id); return 0;
                    case "backup":
                        if (options.DryRun) output.WriteLine("Would save registry backup: " + options.Arguments[0]);
                        else BackupStore.Save(options.Arguments[0], current);
                        return 0;
                    case "restore":
                        var saved = BackupStore.Load(options.Arguments[0]);
                        var changes = new List<Registration>();
                        foreach (var entry in saved)
                        {
                            string existing;
                            if (current.TryGetValue(entry.Key, out existing))
                            {
                                if (existing == entry.Value || EngineDiscovery.SamePath(existing, entry.Value)) continue;
                                if (!options.Replace) throw new InvalidDataException("Restore conflicts with ID '" + entry.Key + "'. Use --replace to overwrite that ID.");
                            }
                            changes.Add(new Registration { Id = entry.Key, Root = entry.Value });
                        }
                        foreach (var item in changes) output.WriteLine("Restore '{0}' @ {1}", item.Id, item.Root);
                        if (options.DryRun || changes.Count == 0) return 0;
                        if (!Confirm("Restore these " + changes.Count + " entries (other IDs will be kept)?")) return options.NonInteractive ? 2 : 0;
                        Backup(current); RegistrationPlanner.Apply(changes, registry); return 0;
                }
                return 2;
            }
            catch (ArgumentException ex) { error.WriteLine(ex.Message); return 2; }
            catch (Exception ex) { error.WriteLine("Failed: " + ex.Message); return 1; }
        }
        private void ListBuilds(IDictionary<string, string> current, bool json)
        {
            var engines = EngineDiscovery.ReadLauncher(manifest);
            engines.AddRange(current.OrderBy(e => e.Key, StringComparer.OrdinalIgnoreCase).Select(e => new EngineInfo {
                Id = e.Key, Root = e.Value, Version = EngineDiscovery.ReadVersion(e.Value), Source = "Registered build", State = EngineDiscovery.Inspect(e.Value).ToString() }));
            if (json) { output.WriteLine(new JavaScriptSerializer().Serialize(engines)); return; }
            int nameWidth = Math.Max("Name or GUID".Length, engines.Select(e => e.Id.Length).DefaultIfEmpty(0).Max());
            int typeWidth = Math.Max("Type".Length, engines.Select(e => e.Source.Length).DefaultIfEmpty(0).Max());
            output.WriteLine();
            output.WriteLine("Name or GUID".PadRight(nameWidth) + "\t\t" + "Type".PadRight(typeWidth) + "\t\tLocation");
            output.WriteLine(new string('-', nameWidth) + "\t\t" + new string('-', typeWidth) + "\t\t" + new string('-', "Location".Length));
            foreach (var engine in engines)
                output.WriteLine(engine.Id.PadRight(nameWidth) + "\t\t" + engine.Source.PadRight(typeWidth) + "\t\t" + engine.Root);
            if (engines.Count == 0) output.WriteLine("No engine builds found.");
        }
        private static void Header(TextWriter output)
        {
            output.WriteLine("=====================================");
            output.WriteLine(" UERegister - " + typeof(CommandRunner).Assembly.GetName().Version);
            output.WriteLine(" (C) 2022-2026 Joseph Wilcox");
            output.WriteLine(" https://www.assetpackgames.com");
            output.WriteLine("=====================================");
            output.WriteLine();
        }
        private void Backup(IDictionary<string, string> current)
        {
            string path = Path.Combine(backupDirectory, DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fffffff") + "-" + Guid.NewGuid().ToString("N") + ".json");
            BackupStore.Save(path, current);
            output.WriteLine("Registry backup: " + path);
        }
        private bool Confirm(string prompt)
        {
            if (options.Yes) return true;
            if (options.NonInteractive) { error.WriteLine("Confirmation required; use --yes or --dry-run."); return false; }
            output.Write(prompt + " [y/N] ");
            return string.Equals(input.ReadLine()?.Trim(), "y", StringComparison.OrdinalIgnoreCase);
        }
        private int Clean(Dictionary<string, string> current)
        {
            var removals = new List<string>();
            bool needsConfirmation = false;
            foreach (var entry in current.OrderBy(e => e.Key, StringComparer.OrdinalIgnoreCase))
            {
                var state = EngineDiscovery.Inspect(entry.Value);
                output.WriteLine("{0}: '{1}' @ {2}", state, entry.Key, entry.Value);
                // Never equate offline/inaccessible paths or an uncompiled source tree with an orphan.
                if (state != EngineState.Missing) continue;
                if (options.DryRun) { output.WriteLine("Would offer removal: " + entry.Key); continue; }
                if (Confirm("Remove missing engine '" + entry.Key + "' @ " + entry.Value + "?")) removals.Add(entry.Key);
                else if (options.NonInteractive) needsConfirmation = true;
            }
            if (removals.Count > 0)
            {
                Backup(current);
                foreach (string id in removals)
                    if (EngineDiscovery.Inspect(current[id]) == EngineState.Missing) registry.Delete(id);
            }
            return needsConfirmation ? 2 : 0;
        }
        public static void Help(TextWriter output)
        {
            output.WriteLine("Usage: UERegister <command> [arguments] [options]");
            output.WriteLine("  l|ls|list [--json]                 List launcher and registered builds");
            output.WriteLine("  u|update [root ...]                Recursively register custom engines (default: current directory)");
            output.WriteLine("  c|clean                           Offer removal of missing engines; preserve offline/source builds");
            output.WriteLine("  f|fullscan [root ...]              Update, then clean");
            output.WriteLine("  r|remove <id>                     Remove one registration, not engine files");
            output.WriteLine("  a|associations [engine or exe]    Repair .uproject file associations (may request UAC)");
            output.WriteLine("  backup <file.json>                Save all custom build registrations");
            output.WriteLine("  restore <file.json>               Merge saved registrations; preserve other IDs");
            output.WriteLine();
            output.WriteLine("Options: --dry-run, --yes, --non-interactive, --replace (update/restore collisions)");
            output.WriteLine("Registry: HKCU\\Software\\Epic Games\\Unreal Engine\\Builds (no administrator required).");
            output.WriteLine("Engine registration changes are backed up to %LOCALAPPDATA%\\UERegister\\Backups.");
        }
    }
    internal static class Program
    {
        private static int Main(string[] args)
        {
            // Set noninteractive mode after parsing so the -- separator remains intact.
            return new CommandRunner(new BuildRegistry(), Console.In, Console.Out, Console.Error,
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Epic", "UnrealEngineLauncher", "LauncherInstalled.dat"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "UERegister", "Backups")).Run(args, Console.IsInputRedirected);
        }
    }
}
