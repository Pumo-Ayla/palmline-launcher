using System;
using System.IO;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Threading;

internal static class OfflineUpgrade
{
    // This entry point lives next to the release ZIP and never launches the game or the GUI.
    internal static int Run(string directory)
    {
        bool interactive = directory == null;
        try {
            if (interactive) {
                Console.Write("Enter the existing launcher folder and press Enter: ");
                directory = Console.ReadLine();
            }
            if (String.IsNullOrWhiteSpace(directory)) return 2;
            string root = new DirectoryInfo(directory.Trim().Trim('"')).FullName.TrimEnd(Path.DirectorySeparatorChar);
            string exe = Path.Combine(root, "Launcher.exe"), dll = Path.Combine(root, "Launcher.dll");
            if (!File.Exists(exe) || !File.Exists(Path.Combine(root, "Community", "projects.xml")))
                throw new InvalidDataException("The selected folder is missing the launcher or its emulator catalog.");
            var installed = AssemblyName.GetAssemblyName(File.Exists(dll) ? dll : exe);
            if (installed.Name != "Launcher") throw new InvalidDataException("Unrecognized launcher installation.");

            string[] archives = Directory.GetFiles(AppDomain.CurrentDomain.BaseDirectory, "Launcher-*.zip");
            if (archives.Length != 1) throw new InvalidDataException("Place one full Launcher ZIP and its .sha256 file next to the upgrade tool.");
            string archive = archives[0], name = Path.GetFileName(archive);
            var version = ReleasePackage.ReadVersion(Path.GetFileNameWithoutExtension(name));
            if (version == null || version <= installed.Version)
                throw new InvalidDataException("The package must be newer than the installed version " + installed.Version.ToString(3) + ".");
            var checksum = Regex.Match(File.ReadAllText(archive + ".sha256").Trim(),
                @"\A([a-fA-F0-9]{64})  " + Regex.Escape(name) + @"\z");
            if (!checksum.Success) throw new InvalidDataException("Invalid package checksum file.");

            using (var mutex = new Mutex(false, UpdateIdentity.MutexName(root))) {
                bool acquired;
                try { acquired = mutex.WaitOne(0); } catch (AbandonedMutexException) { acquired = true; }
                if (!acquired) throw new IOException("Close the running launcher before updating.");
                try {
                    string state = Path.Combine(root, UpdateIdentity.StateDirectory);
                    Directory.CreateDirectory(state);
                    if ((File.GetAttributes(state) & FileAttributes.ReparsePoint) != 0)
                        throw new IOException("The update state directory must not be a reparse point.");
                    using (File.Open(Path.Combine(state, "update.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None)) {
                        var leases = UpdateIdentity.TryLockProfiles(root);
                        if (leases == null) throw new IOException("Close the running game before updating.");
                        try {
                            var installer = new PackageInstall(root);
                            installer.Recover();
                            Console.WriteLine("Verifying and installing " + version.ToString(3) + "…");
                            try { installer.ApplyPackage(version, checksum.Groups[1].Value, archive); }
                            finally { if (!File.Exists(Path.Combine(state, "transaction"))) installer.CleanScratch(); }
                            foreach (string cached in new[] { "ready", "ready.tmp", "download.part", "package.zip" })
                                File.Delete(Path.Combine(state, cached));
                        } finally { foreach (var lease in leases) lease.Dispose(); }
                    }
                } finally { mutex.ReleaseMutex(); }
            }
            Console.WriteLine("Upgrade complete. Open SRLauncher.exe in the original folder.");
            return 0;
        } catch (Exception error) {
            Console.Error.WriteLine(error.GetBaseException().Message);
            return 1;
        } finally {
            if (interactive && !Console.IsInputRedirected) { Console.WriteLine("Press Enter to exit."); Console.ReadLine(); }
        }
    }
}
