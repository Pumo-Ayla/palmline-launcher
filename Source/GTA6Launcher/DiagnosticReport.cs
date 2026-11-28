using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Win32;

// Report inputs are launcher-owned diagnostics. Never crawl an imported game, a save or a cache.
internal sealed class DiagnosticReport
{
    readonly string current, root, lab, runs;
    static readonly Regex sessionName = new Regex(@"^Launcher-\d{8}-\d{6}-\d{3}-[0-9a-f]{8}\.log$", RegexOptions.IgnoreCase);
    static readonly Regex runName = new Regex(@"^Desktop-\d{8}-\d{6}-\d{6}-[a-zA-Z0-9_-]+$");
    static readonly Regex runEvent = new Regex(@"^\d{4}-\d\d-\d\d [0-9:.]+ [+-]\d\d:\d\d \[LAUNCHER\] RUN \| (.+)$");
    static readonly Regex links = new Regex(@"(?:magnet:\?|(?:https?|udp)://)[^\s""<>]+", RegexOptions.IgnoreCase);
    static readonly Regex infoHash = new Regex(@"(?:[?&])xt=(urn:(?:btih:[a-z0-9]+|btmh:[a-f0-9]+))(?=&|$)", RegexOptions.IgnoreCase);
    static readonly Regex credentials = new Regex(@"(\b(?:authorization|proxy-authorization|cookie|set-cookie)\s*:\s*)[^\r\n]+|(\b(?:access_token|refresh_token|api_key|passkey|password)\s*=\s*)[^\s&;]+", RegexOptions.IgnoreCase);
    static readonly string userFolder = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    internal DiagnosticReport(string session, string destination) {
        current = Path.GetFullPath(session);
        root = Path.GetDirectoryName(Path.GetDirectoryName(current));
        lab = Path.Combine(root, "XCL-VIctory-Lab");
        runs = Path.Combine(lab, "runs");
        var target = Path.GetFullPath(destination);
        if ((string.Equals(Path.GetDirectoryName(target), Path.GetDirectoryName(current), StringComparison.OrdinalIgnoreCase)
                && sessionName.IsMatch(Path.GetFileName(target))) || Below(target, lab) ||
            Below(target, Path.Combine(root, ".launcher-update")))
            throw new IOException("Choose a report file outside the original diagnostic inputs.");
    }

    static bool Below(string path, string directory) =>
        path.StartsWith(directory + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

    static bool Unavailable(Exception e) => e is IOException || e is UnauthorizedAccessException ||
        e is System.Security.SecurityException || e is System.ComponentModel.Win32Exception;

    internal static string Shareable(string line) {
        if (line == null) return "";
        if (userFolder.Length > 0 && line.IndexOf(userFolder, StringComparison.OrdinalIgnoreCase) >= 0)
            line = Regex.Replace(line, Regex.Escape(userFolder) + @"(?=[\\/]|$|[""\s])", "%USERPROFILE%", RegexOptions.IgnoreCase);
        if (line.Contains(":") || line.Contains("="))
            line = credentials.Replace(line, "$1$2[redacted]");
        if (line.IndexOf("://", StringComparison.Ordinal) < 0 && line.IndexOf("magnet:?", StringComparison.OrdinalIgnoreCase) < 0) return line;
        return links.Replace(line, m => {
            if (m.Value.StartsWith("magnet:", StringComparison.OrdinalIgnoreCase)) {
                var hash = infoHash.Match(m.Value);
                return hash.Success ? hash.Groups[1].Value : "[magnet]";
            }
            return Uri.TryCreate(m.Value, UriKind.Absolute, out var uri) ? uri.Scheme + "://" + uri.Host + "/…" : "[URL]";
        });
    }

    internal void Write(TextWriter output, long length, string context, string assets, string exe) {
        output.WriteLine("SRLauncher diagnostic report v1");
        output.WriteLine("Exported: " + DateTimeOffset.Now.ToString("O"));
        output.WriteLine("Launcher assembly: " + Assembly.GetExecutingAssembly().GetName().Version);
        output.WriteLine("Installation: " + Shareable(root));
        output.WriteLine("Current selection at export: " + Shareable(context));
        Section(output, "System and installed runtimes");
        SystemInfo(output, assets, exe);
        Section(output, "Selected game inputs at export");
        if (!string.IsNullOrEmpty(exe)) Identify(output, exe, true);
        if (!string.IsNullOrEmpty(assets)) {
            foreach (var name in new[]{"MicrosoftGame.Config","common.rpf","shaders.rpf","levels.rpf","textures.rpf"})
                Identify(output, Path.Combine(assets, name), name=="MicrosoftGame.Config");
        } else output.WriteLine("[no installed game selected]");
        Section(output, "Current launcher session");
        Session(output, current, length);
        Section(output, "Previous launcher session (before this startup)");
        try {
            string prior = Directory.EnumerateFiles(Path.GetDirectoryName(current), "Launcher-*.log")
                .Where(p => sessionName.IsMatch(Path.GetFileName(p)) && string.CompareOrdinal(p, current) < 0)
                .OrderByDescending(p => p, StringComparer.Ordinal).FirstOrDefault(p => !DuplicateStartup(p));
            if (prior != null) Session(output, prior, long.MaxValue);
            else output.WriteLine("[no previous session]");
        } catch (Exception e) when (Unavailable(e)) { output.WriteLine(Shareable(e.Message)); }
        Section(output, "Latest shared diagnostics and configuration (see each file timestamp)");
        foreach (var relative in new[] { ".launcher-update/update.log", "Community/projects.xml",
            "XCL-VIctory-Lab/desktop/gpu-check.log", "XCL-VIctory-Lab/desktop/display-settings.xml" })
            CopyText(output, Path.Combine(root, relative), long.MaxValue);
        foreach (var id in new[] { "cgbj", "victory-nvidia", "xcl" })
            foreach (var name in new[] { "compatibility.log", "render.xml" })
                CopyText(output, Path.Combine(lab, "profiles", id, name), long.MaxValue);
        output.WriteLine();
        output.WriteLine("END OF REPORT");
    }

    static void Identify(TextWriter output, string path, bool fingerprint) {
        try {
            var file = new FileInfo(path);
            if (!file.Exists) { output.WriteLine(Shareable(path) + " [missing]"); return; }
            output.WriteLine($"{Shareable(path)} | bytes={file.Length} | modifiedUtc={file.LastWriteTimeUtc:O}");
            if (fingerprint) using (var input = file.Open(FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                using (var hash = SHA256.Create())
                    output.WriteLine("SHA256=" + BitConverter.ToString(hash.ComputeHash(input)).Replace("-", ""));
        } catch (Exception e) when (Unavailable(e) || e is ArgumentException || e is NotSupportedException) { output.WriteLine(Shareable(e.Message)); }
    }

    static void Section(TextWriter output, string name) { output.WriteLine(); output.WriteLine("=== " + name + " ==="); }

    static bool DuplicateStartup(string path) {
        try {
            if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) return true;
            using (var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            using (var reader = new StreamReader(input, Encoding.UTF8)) {
                reader.ReadLine();
                return reader.ReadLine()?.EndsWith("[LAUNCHER] Another launcher instance is open.", StringComparison.Ordinal) == true;
            }
        } catch(Exception e) when(Unavailable(e)) { return false; }
    }

    void Session(TextWriter output, string file, long limit) {
        var directories = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        CopyText(output, file, limit, line => {
            if (line.IndexOf("[LAUNCHER] RUN | ", StringComparison.Ordinal) < 0) return;
            var match = runEvent.Match(line);
            if (!match.Success) return;
            // Use only a validated run ID inside this installation, including after a portable folder is moved.
            string id = match.Groups[1].Value.Split('\\', '/').Last();
            if (runName.IsMatch(id) && seen.Add(id)) directories.Add(id);
        });
        if (directories.Count == 0) output.WriteLine("[no engine run recorded for this session]");
        foreach (var id in directories) {
            Section(output, "Engine run " + id);
            string directory = Path.Combine(runs, id);
            foreach (string name in new[] { "progress-state.txt", "prewarm.log", "runtime.log", "achievements.log", "game-pid.txt" })
                CopyText(output, Path.Combine(directory, name), long.MaxValue);
            string report = Path.Combine(directory, "report.json");
            if (File.Exists(report)) CopyText(output, report, long.MaxValue);
            try {
                if (Directory.Exists(directory) && (File.GetAttributes(directory) & FileAttributes.ReparsePoint) == 0)
                    foreach (string path in Directory.GetFiles(directory, "*.log").OrderBy(p => p, StringComparer.OrdinalIgnoreCase)) {
                        string name = Path.GetFileName(path);
                        if (!new[]{"prewarm.log","runtime.log","achievements.log"}.Contains(name, StringComparer.OrdinalIgnoreCase))
                            CopyText(output, path, long.MaxValue);
                    }
            } catch (Exception e) when (Unavailable(e)) { output.WriteLine("[additional engine logs unavailable] "+Shareable(e.Message)); }
        }
    }

    // Bound reads to the length at open even if the game is still appending to the log.
    sealed class Snapshot : Stream {
        readonly Stream source;
        long remaining;
        internal Snapshot(Stream source, long length) { this.source = source; remaining = length; }
        public override int Read(byte[] buffer, int offset, int count) {
            int read = source.Read(buffer, offset, (int)Math.Min(count, remaining));
            remaining -= read;
            return read;
        }
        public override bool CanRead => true;
        public override bool CanWrite => false;
        public override bool CanSeek => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    void CopyText(TextWriter output, string file, long limit, Action<string> observe = null) {
        file = Path.GetFullPath(file);
        output.WriteLine();
        output.WriteLine("--- " + file.Substring(root.Length).TrimStart('\\', '/') + " ---");
        try {
            for (string part = file; Below(part, root); part = Path.GetDirectoryName(part))
                if ((File.GetAttributes(part) & FileAttributes.ReparsePoint) != 0) {
                    output.WriteLine("[skipped linked diagnostic path]"); return;
                }
            using (var input = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete)) {
                long bytes = Math.Min(limit, input.Length);
                output.WriteLine("LastWriteUtc=" + File.GetLastWriteTimeUtc(file).ToString("O") + " SnapshotBytes=" + bytes);
                using (var reader = new StreamReader(new Snapshot(input, bytes), Encoding.UTF8, true, 16384)) {
                    string line;
                    while ((line = reader.ReadLine()) != null) { observe?.Invoke(line); output.WriteLine(Shareable(line)); }
                }
            }
        } catch (FileNotFoundException) { output.WriteLine("[not recorded]"); }
          catch (DirectoryNotFoundException) { output.WriteLine("[not recorded]"); }
          catch (Exception e) when (Unavailable(e)) { output.WriteLine("[unavailable] " + Shareable(e.Message)); }
    }

    void SystemInfo(TextWriter output, string assets, string exe) {
        try {
            using (var os = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion"))
            using (var cpu = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\CentralProcessor\0")) {
                output.WriteLine($"Windows: {os?.GetValue("EditionID")} {os?.GetValue("DisplayVersion")} build {os?.GetValue("CurrentBuildNumber")}.{os?.GetValue("UBR")}; OS64={Environment.Is64BitOperatingSystem}; Process64={Environment.Is64BitProcess}");
                output.WriteLine($"CPU: {cpu?.GetValue("ProcessorNameString")}; logical processors={Environment.ProcessorCount}");
            }
            using (var adapters = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}"))
                if (adapters != null)
                    foreach (string key in adapters.GetSubKeyNames().Where(n => Regex.IsMatch(n, @"^\d{4}$")))
                        using (var gpu = adapters.OpenSubKey(key))
                            output.WriteLine($"Display driver {key}: {gpu?.GetValue("DriverDesc")} | {gpu?.GetValue("ProviderName")} | {gpu?.GetValue("DriverVersion")} | {gpu?.GetValue("DriverDate")}");
        } catch (Exception e) when (Unavailable(e)) { output.WriteLine(Shareable(e.Message)); }
        var memory = new MemoryStatus { Size = (uint)Marshal.SizeOf(typeof(MemoryStatus)) };
        if (GlobalMemoryStatusEx(ref memory)) output.WriteLine($"Physical memory (bytes): total={memory.TotalPhysical} available={memory.AvailablePhysical}");
        foreach (string volume in new[] { root, Path.GetTempPath(), assets, exe }.Where(p => !string.IsNullOrEmpty(p))
                .Select(Path.GetPathRoot).Where(p => !string.IsNullOrEmpty(p)).Distinct(StringComparer.OrdinalIgnoreCase)) {
            try { var drive = new DriveInfo(volume); output.WriteLine($"Volume {volume}: {drive.DriveFormat}, free={drive.AvailableFreeSpace} bytes"); }
            catch (Exception e) when (Unavailable(e) || e is ArgumentException) { output.WriteLine(Shareable(e.Message)); }
        }
        foreach (var relative in new[] { "Runtime/DotNet/dotnet.exe", "Runtime/Python/python.exe", "Runtime/Transfers/Relay.Transfer.exe", "Runtime/Native/dxcompiler.dll" }) {
            string path = Path.Combine(root, relative);
            try { output.WriteLine(relative + ": " + (File.Exists(path) ? FileVersionInfo.GetVersionInfo(path).FileVersion : "[missing]")); }
            catch (Exception e) when (Unavailable(e)) { output.WriteLine(Shareable(e.Message)); }
        }
        try {
            foreach (string folder in new[] { "host/fxr", "shared/Microsoft.NETCore.App", "shared/Microsoft.WindowsDesktop.App" }) {
                string path = Path.Combine(root, "Runtime/DotNet", folder);
                output.WriteLine(folder + ": " + (Directory.Exists(path) ? string.Join(", ", Directory.GetDirectories(path).Select(Path.GetFileName)) : "[missing]"));
            }
        } catch (Exception e) when (Unavailable(e)) { output.WriteLine(Shareable(e.Message)); }
        GpuProbe(output);
    }

    [StructLayout(LayoutKind.Sequential)]
    struct MemoryStatus {
        public uint Size, Load;
        public ulong TotalPhysical, AvailablePhysical, TotalPageFile, AvailablePageFile, TotalVirtual, AvailableVirtual, AvailableExtended;
    }
    [DllImport("kernel32.dll")] static extern bool GlobalMemoryStatusEx(ref MemoryStatus status);

    void GpuProbe(TextWriter output) {
        try {
            string path = Path.Combine(root, "Community", "adapter-info.exe");
            var manifest = ComponentLibrary.Index.FirstOrDefault(e => e.Path == "Community/adapter-info.exe");
            using (var file = File.OpenRead(path))
            using (var sha = SHA256.Create()) {
                if (manifest == null || !string.Equals(BitConverter.ToString(sha.ComputeHash(file)).Replace("-", ""), manifest.Digest, StringComparison.OrdinalIgnoreCase)) {
                    output.WriteLine("[GPU probe checksum mismatch]"); return;
                }
                using (var probe = Process.Start(new ProcessStartInfo(path, "--gpu") {
                    WorkingDirectory = Path.GetDirectoryName(path), UseShellExecute = false, CreateNoWindow = true,
                    RedirectStandardOutput = true, RedirectStandardError = true, StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8
                })) {
                    var preferred = probe.StandardOutput.ReadToEndAsync();
                    var features = probe.StandardError.ReadToEndAsync();
                    if (!probe.WaitForExit(5000)) { probe.Kill(); output.WriteLine("[GPU probe timed out]"); return; }
                    output.WriteLine("DXGI exit=" + probe.ExitCode + "; preferred=" + preferred.GetAwaiter().GetResult().Trim());
                    output.WriteLine(features.GetAwaiter().GetResult().Trim());
                }
            }
        } catch (Exception e) when (Unavailable(e) || e is InvalidOperationException) { output.WriteLine("[GPU probe unavailable] " + Shareable(e.Message)); }
    }
}
