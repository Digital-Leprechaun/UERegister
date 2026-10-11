using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.Win32;
using System.Web.Script.Serialization;

namespace UERegister
{
    public interface IBuildRegistry
    {
        Dictionary<string, string> Read();
        void Set(string id, string path);
        void Delete(string id);
    }
    public sealed class BuildRegistry : IBuildRegistry
    {
        private const string KeyPath = @"Software\Epic Games\Unreal Engine\Builds";
        public Dictionary<string, string> Read()
        {
            var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            using (var user = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, RegistryView.Registry64))
            using (var key = user.OpenSubKey(KeyPath))
                if (key != null)
                    foreach (string name in key.GetValueNames())
                    {
                        if (key.GetValueKind(name) != RegistryValueKind.String)
                            throw new InvalidDataException("Unsupported registry value type for '" + name + "'. No changes made.");
                        result.Add(name, (string)key.GetValue(name));
                    }
            return result;
        }
        public void Set(string id, string path)
        {
            using (var user = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, RegistryView.Registry64))
            using (var key = user.CreateSubKey(KeyPath)) key.SetValue(id, path, RegistryValueKind.String);
        }
        public void Delete(string id)
        {
            using (var user = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, RegistryView.Registry64))
            using (var key = user.OpenSubKey(KeyPath, true)) if (key != null) key.DeleteValue(id, false);
        }
    }
    public sealed class Registration
    {
        public string Id { get; set; }
        public string Root { get; set; }
        public bool PersistKey { get; set; }
    }
    public static class RegistrationPlanner
    {
        public static void ValidateId(string id)
        {
            if (string.IsNullOrWhiteSpace(id) || id.Length > 255 || id.Any(char.IsControl) || id != id.Trim())
                throw new InvalidDataException("Engine ID must be nonempty, one line, and at most 255 characters.");
        }
        public static List<Registration> Plan(IEnumerable<string> roots, IDictionary<string, string> current, IEnumerable<EngineInfo> launcher, bool replace)
        {
            var planned = new List<Registration>();
            var reserved = new Dictionary<string, string>(current, StringComparer.OrdinalIgnoreCase);
            var requested = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var scannedRoots = new List<string>();
            foreach (string root in roots.Select(EngineDiscovery.CanonicalDirectory).Distinct(StringComparer.OrdinalIgnoreCase))
            {
                if (launcher.Any(e => EngineDiscovery.SamePath(root, e.Root))) continue;
                scannedRoots.Add(root);
                string keyFile = Path.Combine(root, "UERegister.key");
                if (!File.Exists(keyFile)) keyFile = Path.Combine(root, "UE4Register.key");
                string id;
                bool persist = false;
                if (File.Exists(keyFile)) id = File.ReadAllText(keyFile).Trim();
                else
                {
                    id = current.Where(e => EngineDiscovery.SamePath(e.Value, root)).Select(e => e.Key)
                        .OrderBy(e => e, StringComparer.OrdinalIgnoreCase).FirstOrDefault();
                    if (id == null) { id = Guid.NewGuid().ToString("B"); persist = true; }
                }
                ValidateId(id);
                string previous;
                string requestedRoot;
                if (requested.TryGetValue(id, out requestedRoot) && !EngineDiscovery.SamePath(requestedRoot, root))
                    throw new InvalidDataException("Two scanned engines request the same ID '" + id + "'. Fix their key files before retrying.");
                requested[id] = root;
                if (reserved.TryGetValue(id, out previous) && !EngineDiscovery.SamePath(previous, root) && !replace)
                    throw new InvalidDataException("ID collision: '" + id + "' already points to " + previous + ". Use --replace only to intentionally move that ID.");
                reserved[id] = root;
                if (!current.TryGetValue(id, out previous) || !string.Equals(previous, root, StringComparison.Ordinal))
                    planned.Add(new Registration { Id = id, Root = root, PersistKey = persist });
            }
            // Refresh spelling for compatibility aliases too, without removing their IDs.
            foreach (var entry in current)
            {
                string root = scannedRoots.FirstOrDefault(p => EngineDiscovery.SamePath(p, entry.Value));
                if (root == null || planned.Any(change => change.Id.Equals(entry.Key, StringComparison.OrdinalIgnoreCase))) continue;
                if (!string.Equals(entry.Value, root, StringComparison.Ordinal))
                    planned.Add(new Registration { Id = entry.Key, Root = root });
            }
            return planned;
        }
        public static void Apply(IEnumerable<Registration> plan, IBuildRegistry registry)
        {
            foreach (var item in plan)
            {
                if (item.PersistKey)
                {
                    // Never overwrite a key file that appeared after planning.
                    using (var stream = new FileStream(Path.Combine(item.Root, "UERegister.key"), FileMode.CreateNew, FileAccess.Write))
                    using (var writer = new StreamWriter(stream)) writer.WriteLine(item.Id);
                }
                registry.Set(item.Id, item.Root); // Existing aliases deliberately remain registered.
            }
        }
    }
    public sealed class RegistryBackup
    {
        public int SchemaVersion { get; set; }
        public Dictionary<string, string> Builds { get; set; }
    }
    public static class BackupStore
    {
        public static void Save(string path, IDictionary<string, string> builds)
        {
            string full = Path.GetFullPath(path);
            Directory.CreateDirectory(Path.GetDirectoryName(full));
            using (var file = new FileStream(full, FileMode.CreateNew, FileAccess.Write))
            using (var writer = new StreamWriter(file))
                writer.Write(new JavaScriptSerializer().Serialize(new RegistryBackup { SchemaVersion = 1, Builds = new Dictionary<string, string>(builds) }));
        }
        public static Dictionary<string, string> Load(string path)
        {
            var backup = new JavaScriptSerializer().Deserialize<RegistryBackup>(File.ReadAllText(path));
            if (backup == null || backup.SchemaVersion != 1 || backup.Builds == null) throw new InvalidDataException("Invalid UERegister backup.");
            var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var entry in backup.Builds)
            {
                RegistrationPlanner.ValidateId(entry.Key);
                if (entry.Value == null) throw new InvalidDataException("Invalid backup path.");
                result.Add(entry.Key, entry.Value);
            }
            return result;
        }
    }
}
