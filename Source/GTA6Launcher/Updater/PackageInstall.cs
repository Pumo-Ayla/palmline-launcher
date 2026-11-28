using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using System.Xml.Linq;

internal sealed class PackageInstall
{
    readonly string root, state, stage, backups, journal;
    const string Inventory = ".launcher-update/installed-files";
    const string Preferences = "XCL-VIctory-Lab/desktop/display-settings.xml";
    static readonly StringComparer Names = StringComparer.OrdinalIgnoreCase;
    static readonly string[] ManagedTrees = { "Input/", "Media/", "Runtime/", "Community/", "Sources/" };
    static readonly string[] ManagedFiles = {
        "SRLauncher.exe", "Launcher.exe", "Launcher.exe.config", "Launcher.dll", "Launcher.deps.json", "Launcher.runtimeconfig.json",
        "README.txt", "OriginalGame/game_scarlett_master.exe", "XCL-VIctory-Lab/launcher.py",
        "XCL-VIctory-Lab/prepare_startup.py", "XCL-VIctory-Lab/desktop/session.py", "XCL-VIctory-Lab/desktop/warmup_job.py",
        "XCL-VIctory-Lab/desktop/key_prompts.py"
    };
    static readonly Regex DeviceName = new Regex(@"^(CON|PRN|AUX|NUL|CONIN\$|CONOUT\$|(?:COM|LPT)[1-9¹²³])(?:\.|$)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    internal PackageInstall(string root)
    {
        string full = Path.GetFullPath(root);
        this.root = full.StartsWith(@"\\?\") ? full : full.StartsWith(@"\\") ?
            @"\\?\UNC\" + full.Substring(2) : @"\\?\" + full;
        state = Path.Combine(this.root, UpdateIdentity.StateDirectory);
        stage = Path.Combine(state, "stage");
        backups = Path.Combine(state, "rollback");
        journal = Path.Combine(state, "transaction");
    }

    static bool Owned(string path)
    {
        return ManagedFiles.Contains(path, Names) ||
            ManagedTrees.Any(prefix => path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
    }

    static void ValidateName(string path)
    {
        if (path.Split('/').Any(p => p.Length == 0 || p.EndsWith(".") || p.EndsWith(" ") ||
            p.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || DeviceName.IsMatch(p)))
            throw new InvalidDataException("Invalid update path: " + path);
    }

    static void RejectLink(string path)
    {
        try {
            if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("Unexpected reparse point: " + path);
        } catch (FileNotFoundException) { } catch (DirectoryNotFoundException) { }
    }

    static string Target(string parent, string path)
    {
        ValidateName(path);
        RejectLink(parent);
        foreach (string segment in path.Split('/')) {
            parent = Path.Combine(parent, segment);
            RejectLink(parent);
        }
        return parent;
    }

    internal void CleanScratch()
    {
        if (File.Exists(journal)) throw new IOException("Rollback is still pending.");
        foreach (var path in new[] { stage, backups }) {
            RejectLink(path);
            if (Directory.Exists(path)) Directory.Delete(path, true);
        }
    }

    string[] Extract(Version version, string expectedHash, string package)
    {
        var names = new HashSet<string>(Names);
        using (var input = File.OpenRead(package))
        using (var hash = SHA256.Create()) {
            string actual = BitConverter.ToString(hash.ComputeHash(input)).Replace("-", "");
            if (!Names.Equals(actual, expectedHash)) throw new InvalidDataException("Cached package SHA-256 mismatch.");
            input.Position = 0;
            using (var zip = new ZipArchive(input, ZipArchiveMode.Read)) {
                string prefix = zip.GetEntry("Launcher.exe") != null ? "" : "Launcher " + UpdateIdentity.DisplayVersion(version) + "/";
                long expanded = 0;
                if (zip.Entries.Count > 100000) throw new InvalidDataException("Too many ZIP entries.");
                foreach (var entry in zip.Entries) {
                    string path = entry.FullName.Replace('\\', '/');
                    if (!path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Unexpected ZIP root.");
                    path = path.Substring(prefix.Length);
                    if (path == "") continue;
                    ValidateName(path.TrimEnd('/'));
                    if (path.EndsWith("/")) continue;
                    if (!Owned(path) && !(Names.Equals(path, Preferences) && !File.Exists(Target(root, path)))) continue;
                    if (!names.Add(path)) throw new InvalidDataException("Duplicate package file: " + path);
                    if (entry.Length > 8L * 1024 * 1024 * 1024 - expanded) throw new InvalidDataException("Package exceeds extraction limit.");
                    expanded += entry.Length;
                    string target = Target(stage, path);
                    Directory.CreateDirectory(Path.GetDirectoryName(target));
                    using (var source = entry.Open())
                    using (var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None)) {
                        var buffer = new byte[81920];
                        long remaining = entry.Length;
                        int count;
                        while ((count = source.Read(buffer, 0, buffer.Length)) != 0) {
                            remaining -= count;
                            if (remaining < 0) throw new InvalidDataException("Invalid expanded size: " + path);
                            output.Write(buffer, 0, count);
                        }
                        if (remaining != 0) throw new InvalidDataException("Truncated ZIP entry: " + path);
                    }
                }
            }
        }
        foreach (string required in new[] { "Launcher.exe", "Launcher.dll", "Launcher.deps.json", "Launcher.runtimeconfig.json",
            "Community/projects.xml", "Community/adapter-info.exe", "Input/SDL3.dll", "Input/Palmline.Log.exe",
            "Runtime/DotNet/dotnet.exe", "Runtime/Python/python.exe", "XCL-VIctory-Lab/desktop/session.py",
            "XCL-VIctory-Lab/desktop/warmup_job.py" })
            if (!names.Contains(required)) throw new InvalidDataException("Incomplete package, missing " + required);
        XElement[] projects;
        using (var catalog = File.OpenRead(Target(stage, "Community/projects.xml")))
            projects = XElement.Load(catalog).Elements("project").ToArray();
        if (projects.Length == 0) throw new InvalidDataException("Empty project catalog.");
        foreach (var project in projects) {
            string folder = "Community/" + (string)project.Attribute("id") + "/";
            string runtime = folder + "runtime/" + (string)project.Attribute("commit") + "/";
            string id=(string)project.Attribute("id");
            var binaries=id=="xcl" ? new[]{"xcl.exe"} :
                id=="victory-nvidia" ? new[]{"victory.exe","preload.env"} :
                id=="cgbj" ? new[]{"victory.exe","mapped-vi/victory.exe"} : new[]{"victory.exe"};
            foreach (var file in binaries.Select(name=>runtime+name).Concat(new[]{folder+(string)project.Attribute("image")}))
                if (!names.Contains(file)) throw new InvalidDataException("Catalog file missing: " + file);
        }
        // Fusion's GetAssemblyName(path) rejects extended Windows paths. Read metadata without executing code.
        string dll = Target(stage, "Launcher.dll");
        if (new FileInfo(dll).Length > 128L * 1024 * 1024) throw new InvalidDataException("Unexpected launcher assembly size.");
        var assembly = Assembly.ReflectionOnlyLoad(File.ReadAllBytes(dll)).GetName();
        if (assembly.Name != "Launcher" || assembly.Version != version)
            throw new InvalidDataException("Packaged launcher version differs from the release tag.");
        if (names.Add("SRLauncher.exe"))
            File.Copy(Target(stage, "Launcher.exe"), Target(stage, "SRLauncher.exe"));
        return names.OrderBy(n => n, Names).ToArray();
    }

    internal void Apply(Version version, string digest)
    {
        ApplyPackage(version, digest, Target(state, "package.zip"));
    }

    internal void ApplyPackage(Version version, string digest, string package)
    {
        CleanScratch();
        string[] files = Extract(version, digest, package);
        var plan = new List<string>();
        string inventory = Target(root, Inventory);
        string stagedInventory = Target(stage, Inventory);
        Directory.CreateDirectory(Path.GetDirectoryName(stagedInventory));
        File.WriteAllLines(stagedInventory, files.Where(Owned));
        // Keep the old runtime usable until the new apphost and its dependencies are all installed.
        string[] entryPoints = { "Launcher.deps.json", "Launcher.runtimeconfig.json", "Launcher.dll", "Launcher.exe", "SRLauncher.exe" };
        foreach (string path in files.Except(entryPoints, Names).Concat(new[] { Inventory }).Concat(entryPoints))
            plan.Add((File.Exists(Target(root, path)) ? "=" : "+") + path);
        if (File.Exists(inventory)) {
            foreach (string obsolete in File.ReadAllLines(inventory).Except(files, Names)) {
                if (Owned(obsolete) && File.Exists(Target(root, obsolete))) plan.Add("-" + obsolete);
            }
        }

        using (var stream = new FileStream(journal + ".tmp", FileMode.Create, FileAccess.Write, FileShare.None))
        using (var writer = new StreamWriter(stream)) {
            foreach (string op in plan) writer.WriteLine(op);
            writer.Flush();
            stream.Flush(true);
        }
        File.Move(journal + ".tmp", journal);
        try {
            foreach (string op in plan) {
                string path = op.Substring(1), dest = Target(root, path), backup = Target(backups, path);
                Directory.CreateDirectory(Path.GetDirectoryName(dest));
                if (op[0] == '+') File.Move(Target(stage, path), dest);
                else {
                    Directory.CreateDirectory(Path.GetDirectoryName(backup));
                    if (op[0] == '-') File.Move(dest, backup);
                    else File.Replace(Target(stage, path), dest, backup);
                }
            }
            File.Delete(journal);
        } catch {
            Recover();
            throw;
        }
    }

    internal void Recover()
    {
        if (!File.Exists(journal)) return;
        foreach (string op in File.ReadAllLines(journal).Reverse()) {
            string path = op.Substring(1);
            if (!Owned(path) && !Names.Equals(path, Preferences) && !Names.Equals(path, Inventory))
                throw new InvalidDataException("Invalid rollback path.");
            string dest = Target(root, path), backup = Target(backups, path);
            if (File.Exists(backup)) {
                if (File.Exists(dest)) File.Replace(backup, dest, null);
                else File.Move(backup, dest);
            } else if (op[0] == '+' && !File.Exists(Target(stage, path))) File.Delete(dest);
        }
        File.Delete(journal);
    }
}
