using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

internal static class UpdateIdentity
{
    internal const string Repository = "Pumo-Ayla/series-relay-launcher";
    internal const string StateDirectory = ".launcher-update";

    internal static Version Ready(string state, out string hash)
    {
        hash = null;
        string marker = Path.Combine(state, "ready");
        if (!File.Exists(marker) || !File.Exists(Path.Combine(state, "package.zip"))) return null;
        string[] lines = File.ReadAllLines(marker);
        Version version;
        if (lines.Length != 2 || !Version.TryParse(lines[0], out version) ||
            !Regex.IsMatch(lines[1], @"\A[a-fA-F0-9]{64}\z")) return null;
        hash = lines[1];
        return version;
    }

    // The game worker holds this lock even when its launcher window has closed.
    internal static List<FileStream> TryLockProfiles(string root)
    {
        var leases = new List<FileStream>();
        string profiles = Path.Combine(root, "XCL-VIctory-Lab", "profiles");
        try {
            if (Directory.Exists(profiles))
                foreach (string dir in Directory.GetDirectories(profiles)) {
                    string path = Path.Combine(dir, "integrated-run.lock");
                    if (File.Exists(path)) leases.Add(File.Open(path, FileMode.Open, FileAccess.Read, FileShare.None));
                }
            return leases;
        } catch (Exception ex) {
            foreach (var lease in leases) lease.Dispose();
            if (ex is IOException || ex is UnauthorizedAccessException) return null;
            throw;
        }
    }

    internal static string MutexName(string root)
    {
        using (var hash = SHA256.Create())
            return @"Local\GrandTheftAutoVI.DesktopLauncher." +
                BitConverter.ToString(hash.ComputeHash(Encoding.UTF8.GetBytes(root.ToUpperInvariant())));
    }
}
