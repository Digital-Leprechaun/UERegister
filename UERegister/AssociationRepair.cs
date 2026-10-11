using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Principal;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32;

namespace UERegister
{
    public static class AssociationRepair
    {
        [DllImport("shlwapi.dll", CharSet = CharSet.Unicode)]
        private static extern int AssocQueryString(uint flags, uint value, string association, string verb, StringBuilder text, ref uint length);
        public static bool IsVerbAvailable(string verb)
        {
            uint length = 32768;
            var text = new StringBuilder((int)length);
            return AssocQueryString(0, 2, ".uproject", verb, text, ref length) == 0 && File.Exists(text.ToString());
        }
        private static readonly string[] Names = { "UnrealVersionSelector.exe", "UnrealVersionSelector-Win64-Shipping.exe", "UnrealVersionSelector-Win64-Development.exe" };
        public static string Find(IEnumerable<string> roots)
        {
            foreach (string root in roots.Where(p => !string.IsNullOrWhiteSpace(p)).Distinct(StringComparer.OrdinalIgnoreCase))
                foreach (string directory in new[] { Path.Combine(root, "Engine", "Binaries", "Win64"), root })
                    foreach (string name in Names)
                    {
                        string file = Path.Combine(directory, name);
                        if (File.Exists(file)) return Path.GetFullPath(file);
                    }
            return null;
        }
        public static IEnumerable<string> LauncherRoots()
        {
            foreach (RegistryView view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
            {
                using (var machine = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view))
                using (var key = machine.OpenSubKey(@"SOFTWARE\EpicGames\Unreal Engine"))
                {
                    string root = key?.GetValue("INSTALLDIR") as string;
                    if (!string.IsNullOrWhiteSpace(root)) yield return Path.Combine(root, "Launcher");
                }
            }
            foreach (string root in new[] { Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles) })
                if (!string.IsNullOrEmpty(root)) yield return Path.Combine(root, "Epic Games", "Launcher");
        }
        public static ProcessStartInfo StartInfo(string selector)
        {
            return new ProcessStartInfo {
                FileName = selector, Arguments = "/fileassociations", WorkingDirectory = Path.GetDirectoryName(selector),
                UseShellExecute = true, Verb = "runas", WindowStyle = ProcessWindowStyle.Normal
            };
        }
        public static int Run(string selector, bool dryRun, bool nonInteractive, TextWriter output, Func<ProcessStartInfo, int> launch = null, Func<string, bool> verify = null)
        {
            if (selector == null)
            {
                output.WriteLine("UnrealVersionSelector was not found. Supply an engine root or selector executable:");
                output.WriteLine("  UERegister associations W:\\UnrealEngine");
                output.WriteLine("For a source engine, build it with:");
                output.WriteLine("  Engine\\Build\\BatchFiles\\Build.bat UnrealVersionSelector Win64 Shipping");
                return 1;
            }
            output.WriteLine((dryRun ? "Would repair" : "Repairing") + " .uproject associations using: " + selector);
            if (dryRun) return 0;
            using (var identity = WindowsIdentity.GetCurrent())
                if (nonInteractive && !new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator))
                {
                    output.WriteLine("Association repair requires elevation. Run interactively to approve UAC, or use an elevated terminal for noninteractive mode.");
                    return 2;
                }
            try
            {
                int result;
                if (launch != null) result = launch(StartInfo(selector));
                else using (var process = Process.Start(StartInfo(selector)))
                {
                    if (process == null) throw new IOException("UnrealVersionSelector did not start.");
                    process.WaitForExit(); result = process.ExitCode;
                }
                if (result != 0) { output.WriteLine("UnrealVersionSelector could not repair the associations (exit " + result + ")."); return 1; }
                var check = verify ?? IsVerbAvailable;
                var missing = new[] { "open", "run", "rungenproj", "switchversion" }.Where(verb => !check(verb)).ToArray();
                if (missing.Length > 0)
                {
                    output.WriteLine("UnrealVersionSelector completed, but Windows still cannot resolve these .uproject actions: " + string.Join(", ", missing));
                    output.WriteLine("A per-user default application (for example a text editor) may override the Unreal registration.");
                    output.WriteLine("Check Settings > Apps > Default apps > .uproject and select Unreal Engine, then run this command again.");
                    return 1;
                }
                output.WriteLine("Done. .uproject file associations repaired.");
                return 0;
            }
            catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
            { output.WriteLine("Association repair cancelled."); return 0; }
        }
    }
}
