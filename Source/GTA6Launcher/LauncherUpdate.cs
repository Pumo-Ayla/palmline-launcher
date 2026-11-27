using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;

internal static class LauncherUpdate
{
    internal static bool IsInstallation(string root)
    {
        return String.Equals(Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location), root, StringComparison.OrdinalIgnoreCase);
    }

    internal static bool Start(string root)
    {
        if (!IsInstallation(root) || !File.Exists(Path.Combine(root, UpdateIdentity.StateDirectory, "transaction"))) return false;
        using (StartAgent(root, "recover", Assembly.GetExecutingAssembly().GetName().Version)) { }
        return true;
    }

    internal static Process StartAgent(string root, string command, Version version)
    {
        if (!IsInstallation(root)) throw new InvalidOperationException("Updates require the installed launcher.");
        var assembly = Assembly.GetExecutingAssembly();
        string state = Path.Combine(root, UpdateIdentity.StateDirectory);
        Directory.CreateDirectory(state);
        if ((File.GetAttributes(state) & FileAttributes.ReparsePoint) != 0)
            throw new IOException("Invalid update directory.");
        string agent;
        using (var resource = assembly.GetManifestResourceStream("Palmline.Update.exe"))
        using (var hash = SHA256.Create()) {
            string fingerprint = BitConverter.ToString(hash.ComputeHash(resource)).Replace("-", "");
            agent = Path.Combine(state, "agent-" + fingerprint + ".exe");
            // Run the embedded helper outside the payload, so it can replace the entire package.
            if (File.Exists(agent)) {
                using (var prior = File.OpenRead(agent))
                    if (!String.Equals(BitConverter.ToString(hash.ComputeHash(prior)).Replace("-", ""), fingerprint))
                        throw new InvalidDataException("Update helper checksum mismatch.");
            } else {
                resource.Position = 0;
                string temp = agent + ".tmp";
                using (var output = File.Create(temp)) resource.CopyTo(output);
                File.Move(temp, agent);
            }
        }
        if (!File.Exists(agent + ".config")) {
            using (var config = assembly.GetManifestResourceStream("Palmline.Update.exe.config"))
            using (var output = File.Create(agent + ".config.tmp")) config.CopyTo(output);
            File.Move(agent + ".config.tmp", agent + ".config");
        }
        using (var host = Process.GetCurrentProcess())
        return Process.Start(new ProcessStartInfo {
            FileName = agent, WorkingDirectory = state, UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = command != "recover", RedirectStandardInput = command == "install",
            Arguments = command + " " + host.Id + " " + version
        });
    }
}
