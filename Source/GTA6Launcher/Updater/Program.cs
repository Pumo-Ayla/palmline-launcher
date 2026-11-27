using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Threading;

internal static class Program
{
    static void DiscardPackage(string state)
    {
        foreach (var name in new[] { "ready", "ready.tmp", "download.part", "package.zip" })
            File.Delete(Path.Combine(state, name));
    }

    static Version Installed(string root)
    {
        string dll = Path.Combine(root, "Launcher.dll");
        return AssemblyName.GetAssemblyName(File.Exists(dll) ? dll : Path.Combine(root, "Launcher.exe")).Version;
    }

    static bool Own(Mutex mutex, int timeout)
    {
        try { return mutex.WaitOne(timeout); } catch (AbandonedMutexException) { return true; }
    }

    static void Restart(string root)
    {
        var entry = Path.Combine(root, "SRLauncher.exe");
        using (Process.Start(new ProcessStartInfo(File.Exists(entry) ? entry : Path.Combine(root, "Launcher.exe")) {
            WorkingDirectory = root, UseShellExecute = false })) { }
    }

    static void Recover(string root, string state, Process parent)
    {
        if (parent != null) parent.WaitForExit();
        using (var mutex = new Mutex(false, UpdateIdentity.MutexName(root))) {
            if (!Own(mutex, 30000)) return;
            try {
                var leases = UpdateIdentity.TryLockProfiles(root);
                if (leases == null) throw new IOException("Cannot recover during an active game session.");
                try {
                    var install = new PackageInstall(root);
                    install.Recover();
                    install.CleanScratch();
                    DiscardPackage(state);
                } finally { foreach (var lease in leases) lease.Dispose(); }
            } finally { mutex.ReleaseMutex(); }
        }
        Restart(root);
    }

    static int Install(string root, string state, Process parent, Version requested, Action<string> log)
    {
        string hash;
        if (UpdateIdentity.Ready(state, out hash) != requested || requested <= Installed(root))
            throw new InvalidDataException("No matching downloaded update.");
        var leases = UpdateIdentity.TryLockProfiles(root);
        if (leases == null) { Console.WriteLine("BUSY"); return 3; }
        try {
            // Hold the game locks across handoff. Losing the host before confirmation cancels installation.
            Console.WriteLine("READY");
            if (Console.ReadLine() != "INSTALL") return 2;
            if (parent != null && !parent.WaitForExit(30000)) return 2;
            using (var mutex = new Mutex(false, UpdateIdentity.MutexName(root))) {
                if (!Own(mutex, 0)) return 3;
                try {
                    var install = new PackageInstall(root);
                    try {
                        install.Apply(requested, hash);
                        log("Installed " + requested + ".");
                    } finally {
                        if (!File.Exists(Path.Combine(state, "transaction"))) {
                            DiscardPackage(state);
                            install.CleanScratch();
                        }
                    }
                } finally { mutex.ReleaseMutex(); }
            }
        } finally {
            foreach (var lease in leases) lease.Dispose();
        }
        return 0;
    }

    static int Main(string[] args)
    {
        if (args.Length == 0 || (args.Length == 2 && args[0] == "offline"))
            return OfflineUpgrade.Run(args.Length == 0 ? null : args[1]);
        if (args.Length != 3) return 2;
        string state = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
        if (!String.Equals(Path.GetFileName(state), UpdateIdentity.StateDirectory, StringComparison.OrdinalIgnoreCase)) return 2;
        string root = Path.GetDirectoryName(state);
        Action<string> log = message => File.AppendAllText(Path.Combine(state, "update.log"),
            DateTimeOffset.UtcNow.ToString("O") + " " + message + Environment.NewLine);
        try {
            var version = Version.Parse(args[2]);
            if (args[0] == "check") {
                using (var client = ReleasePackage.Client()) {
                    var asset = ReleasePackage.Check(client, version);
                    Console.WriteLine(asset == null ? "CURRENT" : "AVAILABLE " + asset.Version +
                        (asset.MinimumLauncher == null ? "" : " " + asset.MinimumLauncher));
                    if (asset != null && asset.HasHistory)
                        Console.WriteLine("COUNTS " + asset.NormalUpdates + " " + asset.CriticalUpdates);
                }
                return 0;
            }
            using (var access = File.Open(Path.Combine(state, "update.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None)) {
                if (args[0] == "download") {
                    using (var client = ReleasePackage.Client()) {
                        var asset = ReleasePackage.Check(client, Installed(root));
                        if (asset == null || asset.Version != version) throw new IOException("Release changed. Check for updates again.");
                        ReleasePackage.Download(client, state, asset, percent => Console.WriteLine("PROGRESS " + percent));
                    }
                    return 0;
                }
                if (args[0] != "install" && args[0] != "recover") return 2;
                Process parent = null;
                try {
                    parent = Process.GetProcessById(int.Parse(args[1]));
                    var handle = parent.Handle;
                } catch (ArgumentException) { } catch (InvalidOperationException) { }
                using (parent) {
                    if (args[0] == "recover") { Recover(root, state, parent); return 0; }
                    try {
                        int code = Install(root, state, parent, version, log);
                        if (code != 0) return code;
                    } catch {
                        // If the host already handed off, reopen the restored version after rollback.
                        if (parent != null && parent.HasExited && !File.Exists(Path.Combine(state, "transaction"))) Restart(root);
                        throw;
                    }
                }
            }
            Restart(root);
            return 0;
        } catch (Exception ex) {
            try { log(ex.ToString()); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            return 1;
        }
    }
}
