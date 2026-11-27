using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Threading;

// Only the index embedded in this launcher build supplies destination paths and hashes.
internal sealed class ComponentLibrary : IDisposable
{
    internal sealed class Entry
    {
        internal string Group, Path, Digest, Resource;
        internal long Size;
    }

    internal static readonly Entry[] Index = ReadIndex();
    readonly string root;
    ZipArchive archive;

    internal ComponentLibrary(string root) { this.root = Path.GetFullPath(root); }

    internal void RestoreStartupFiles() {
        foreach (var entry in Index.Where(e =>
            (e.Group=="metadata" && !File.Exists(Path.Combine(root,e.Path))) ||
            (e.Group=="python" && e.Path.StartsWith("XCL-VIctory-Lab/desktop/",StringComparison.Ordinal))))
            Ensure(entry,CancellationToken.None);

        // Legacy agents replace the original apphost, but know nothing about its new public filename.
        if (!File.Exists(Path.Combine(root,"Launcher.dll"))) return;
        string inventory=Target(".launcher-update/installed-files");
        if (!File.Exists(inventory) || File.ReadLines(inventory).Contains("SRLauncher.exe",StringComparer.OrdinalIgnoreCase)) return;
        string apphost=Target("Launcher.exe"), entryPoint=Target("SRLauncher.exe");
        bool exists=File.Exists(entryPoint);
        if (exists && new FileInfo(apphost).Length==new FileInfo(entryPoint).Length) {
            using (var current=File.OpenRead(entryPoint))
            using (var expected=File.OpenRead(apphost))
                if (Digest(current)==Digest(expected)) return;
        }
        string temp=entryPoint+"."+Guid.NewGuid().ToString("N")+".tmp";
        try {
            File.Copy(apphost,temp);
            if (exists) File.Replace(temp,entryPoint,null); else File.Move(temp,entryPoint);
        } finally { File.Delete(temp); }
    }

    static Entry[] ReadIndex() {
        var rows = new List<Entry>();
        using (var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("ComponentIndex.tsv"))
        using (var reader = new StreamReader(stream)) {
            string line;
            while ((line = reader.ReadLine()) != null) {
                var fields = line.Split('\t');
                rows.Add(new Entry { Group=fields[0], Path=fields[1], Size=long.Parse(fields[2]), Digest=fields[3] });
            }
        }
        var assembly=Assembly.GetExecutingAssembly();
        foreach(string name in assembly.GetManifestResourceNames().Where(n=>n.StartsWith("Recovery.",StringComparison.Ordinal))) {
            using(var file=assembly.GetManifestResourceStream(name))
                rows.Add(new Entry { Group="launcher", Path="Input/"+name.Substring("Recovery.".Length),
                    Resource=name, Size=file.Length, Digest=Digest(file) });
        }
        return rows.ToArray();
    }

    string Target(string relative) {
        var path = root;
        foreach (var part in relative.Split('/')) {
            path = Path.Combine(path, part);
            try {
                if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                    throw new IOException(Ui.Text("组件路径指向了重解析点。"));
            } catch (FileNotFoundException) { } catch (DirectoryNotFoundException) { }
        }
        return path;
    }

    static string Digest(Stream stream) {
        using (var hash = SHA256.Create())
            return BitConverter.ToString(hash.ComputeHash(stream)).Replace("-", "").ToLowerInvariant();
    }

    static bool Matches(Stream stream, Entry entry) => stream.Length==entry.Size && Digest(stream)==entry.Digest;

    Stream OpenPayload(Entry entry) {
        if(entry.Resource!=null) return Assembly.GetExecutingAssembly().GetManifestResourceStream(entry.Resource);
        if (archive == null) {
            string source = Target("Runtime/Components/Library.zip");
            if (!File.Exists(source)) throw new FileNotFoundException(Ui.Text("内置组件库缺失，请恢复启动器安装包。"));
            archive = ZipFile.OpenRead(source);
        }
        var payload = archive.GetEntry(entry.Digest);
        if (payload == null || payload.Length != entry.Size)
            throw new InvalidDataException(Ui.Text("组件库与启动器版本不匹配或已损坏。"));
        return payload.Open();
    }

    internal bool Ensure(Entry entry, CancellationToken cancel, Action onRestore=null) {
        cancel.ThrowIfCancellationRequested();
        var target = Target(entry.Path);
        bool exists = File.Exists(target);
        if (exists) {
            using (var file = new FileStream(target, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete))
                if (Matches(file, entry)) return false;
        }
        onRestore?.Invoke();
        using (var input = OpenPayload(entry)) {
            Directory.CreateDirectory(Path.GetDirectoryName(target));
            string temp = target + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try {
                using (var output = new FileStream(temp, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None)) {
                    var buffer = new byte[81920];
                    int n;
                    while ((n=input.Read(buffer,0,buffer.Length)) > 0) {
                        cancel.ThrowIfCancellationRequested();
                        if (output.Length + n > entry.Size) throw new InvalidDataException();
                        output.Write(buffer,0,n);
                    }
                    output.Position = 0;
                    if (!Matches(output,entry)) throw new InvalidDataException(Ui.Text("组件校验失败，已保留原文件。"));
                    output.Flush(true);
                }
                cancel.ThrowIfCancellationRequested();
                if (exists) File.Replace(temp,target,null); else File.Move(temp,target);
                return true;
            } finally { File.Delete(temp); }
        }
    }

    public void Dispose() { archive?.Dispose(); }
}
