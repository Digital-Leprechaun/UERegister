using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Web.Script.Serialization;

namespace UERegister
{
    public enum EngineState { Ready, SourceOnly, Missing, Unavailable, Invalid }
    public sealed class EngineInfo
    {
        public string Id { get; set; }
        public string Root { get; set; }
        public string Version { get; set; }
        public string Source { get; set; }
        public string State { get; set; }
    }
    public static class EngineDiscovery
    {
        public static string Normalize(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("An engine path cannot be empty.");
            string full = Path.GetFullPath(path);
            return full.Length > Path.GetPathRoot(full).Length ? full.TrimEnd('\\', '/') : full;
        }
        public static bool SamePath(string left, string right)
        {
            try { return string.Equals(Normalize(left), Normalize(right), StringComparison.OrdinalIgnoreCase); }
            catch (Exception ex) when (ex is ArgumentException || ex is NotSupportedException || ex is IOException) { return false; }
        }
        public static string CanonicalDirectory(string path)
        {
            string full = Normalize(path);
            try
            {
                string root = Path.GetPathRoot(full);
                string current = root.Length >= 2 && root[1] == ':' ? root.ToUpperInvariant() : root;
                foreach (string part in full.Substring(root.Length).Split(new[] { '\\', '/' }, StringSplitOptions.RemoveEmptyEntries))
                {
                    // Enumeration returns the name stored on disk, unlike GetFullPath.
                    // Keep the caller's drive/share rather than resolving junction targets.
                    string match = Directory.GetDirectories(current, part).FirstOrDefault();
                    if (match == null) return full;
                    current = Path.Combine(current, Path.GetFileName(match));
                }
                return Normalize(current);
            }
            catch (IOException) { return full; }
            catch (UnauthorizedAccessException) { return full; }
        }
        public static string ReadVersion(string root)
        {
            try
            {
                string path = Path.Combine(root, "Engine", "Build", "Build.version");
                if (File.Exists(path))
                {
                    var fields = new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(File.ReadAllText(path));
                    if (fields == null) throw new FormatException("Engine version metadata must be an object.");
                    var version = new List<string>();
                    foreach (string name in new[] { "MajorVersion", "MinorVersion", "PatchVersion" })
                    {
                        object value;
                        if (!fields.TryGetValue(name, out value) || !(value is int) || (int)value < 0)
                            throw new FormatException("Invalid engine version field: " + name);
                        version.Add(((int)value).ToString());
                    }
                    return string.Join(".", version);
                }
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is ArgumentException || ex is KeyNotFoundException || ex is FormatException || ex is InvalidOperationException) { }
            try
            {
                string path = Path.Combine(root, "Engine", "Source", "Runtime", "Launch", "Resources", "Version.h");
                if (!File.Exists(path)) return "";
                string header = File.ReadAllText(path);
                var matches = new[] { "MAJOR", "MINOR", "PATCH" }.Select(p => Regex.Match(header, @"(?m)^\s*#\s*define\s+ENGINE_" + p + @"_VERSION\s+(\d+)")).ToArray();
                return matches.All(m => m.Success) ? string.Join(".", matches.Select(m => m.Groups[1].Value)) : "";
            }
            catch (IOException) { return ""; }
            catch (UnauthorizedAccessException) { return ""; }
        }
        public static EngineState Inspect(string path)
        {
            string root;
            try { root = Normalize(path); }
            catch (Exception ex) when (ex is ArgumentException || ex is NotSupportedException || ex is IOException) { return EngineState.Invalid; }
            try
            {
                if ((File.GetAttributes(root) & FileAttributes.Directory) == 0) return EngineState.Invalid;
                foreach (string editor in new[] { "UnrealEditor", "UE4Editor" })
                    foreach (string suffix in new[] { ".exe", "-Win64-Debug.exe", "-Win64-DebugGame.exe" })
                        if (File.Exists(Path.Combine(root, "Engine", "Binaries", "Win64", editor + suffix))) return EngineState.Ready;
                if (Directory.Exists(Path.Combine(root, "Engine", "Source")) && ReadVersion(root).Length > 0) return EngineState.SourceOnly;
                // Enumerate to distinguish an inaccessible tree from an empty one.
                Directory.GetDirectories(root);
                return EngineState.Invalid;
            }
            catch (Exception ex) when (ex is DirectoryNotFoundException || ex is FileNotFoundException)
            {
                try { File.GetAttributes(Path.GetPathRoot(root)); return EngineState.Missing; }
                catch (Exception) { return EngineState.Unavailable; }
            }
            catch (IOException) { return EngineState.Unavailable; }
            catch (UnauthorizedAccessException) { return EngineState.Unavailable; }
        }
        public sealed class Manifest { public List<Entry> InstallationList { get; set; } }
        public sealed class Entry { public string AppName { get; set; } public string InstallLocation { get; set; } }
        public static List<EngineInfo> ReadLauncher(string file)
        {
            var result = new List<EngineInfo>();
            if (!File.Exists(file)) return result;
            var data = new JavaScriptSerializer().Deserialize<Manifest>(File.ReadAllText(file));
            if (data == null || data.InstallationList == null) throw new InvalidDataException("Launcher manifest has no InstallationList.");
            foreach (var entry in data.InstallationList)
            {
                if (entry == null || string.IsNullOrWhiteSpace(entry.InstallLocation) || string.IsNullOrEmpty(entry.AppName)) continue;
                bool uefn = entry.AppName.Equals("Fortnite_Studio", StringComparison.OrdinalIgnoreCase);
                if (!uefn && !entry.AppName.StartsWith("UE_", StringComparison.OrdinalIgnoreCase)) continue;
                string root = Normalize(entry.InstallLocation);
                var state = Inspect(root);
                if (uefn && File.Exists(Path.Combine(root, "FortniteGame", "Binaries", "Win64", "UnrealEditorFortnite-Win64-Shipping.exe"))) state = EngineState.Ready;
                if (state == EngineState.Invalid && !uefn) continue; // exclude UE_-named plugins/content
                if (result.Any(e => SamePath(e.Root, root))) continue;
                result.Add(new EngineInfo { Id = uefn ? "UEFN" : entry.AppName.Substring(3), Root = root,
                    Version = ReadVersion(root), Source = "Epic Launcher", State = state.ToString() });
            }
            return result;
        }
        public static List<string> Scan(IEnumerable<string> paths, Action<string> warning, Action<string> progress = null)
        {
            var roots = paths.Select(Normalize).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            // Validate every supplied root before discovering anything.
            foreach (string root in roots)
                if (!Directory.Exists(root)) throw new ArgumentException("Search path does not exist or is inaccessible: " + root);
            var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var found = new List<string>();
            var pending = new Stack<string>(roots.Reverse());
            while (pending.Count > 0)
            {
                string directory = pending.Pop();
                if (!visited.Add(directory)) continue;
                progress?.Invoke(directory);
                try
                {
                    if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0) { warning("Skipped junction/link: " + directory); continue; }
                    if (File.Exists(Path.Combine(directory, "do.not.scan.ued"))) continue;
                    var state = Inspect(directory);
                    if (state == EngineState.Ready || state == EngineState.SourceOnly) { found.Add(directory); continue; }
                    foreach (string child in Directory.GetDirectories(directory).OrderByDescending(p => p, StringComparer.OrdinalIgnoreCase))
                    {
                        string name = Path.GetFileName(child);
                        if (new[] { ".git", ".vs", "Binaries", "Intermediate", "Saved", "DerivedDataCache", "node_modules" }.Contains(name, StringComparer.OrdinalIgnoreCase)) continue;
                        pending.Push(child);
                    }
                }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
                { warning("Skipped " + directory + ": " + ex.Message); }
            }
            return found;
        }
    }
}
