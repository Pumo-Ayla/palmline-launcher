using System;
using System.Globalization;
using System.IO;
using System.Text;

internal sealed class SessionLog : IDisposable
{
    readonly object gate = new object();
    readonly StreamWriter writer;
    bool closed;
    public readonly string FileName;

    public SessionLog(string root) {
        string folder = Directory.CreateDirectory(Path.Combine(root, "Logs")).FullName;
        FileName = Path.Combine(folder, "Launcher-" + DateTime.Now.ToString("yyyyMMdd-HHmmss-fff", CultureInfo.InvariantCulture)
            + "-" + Guid.NewGuid().ToString("N").Substring(0, 8) + ".log");
        writer = new StreamWriter(new FileStream(FileName, FileMode.CreateNew, FileAccess.Write, FileShare.Read),
            new UTF8Encoding(false)) { AutoFlush = true };
        Write("LAUNCHER", "Launcher started. Build " + typeof(SessionLog).Assembly.GetName().Version
            + "; process=" + System.Diagnostics.Process.GetCurrentProcess().Id + "; root=" + root);
    }

    public void Write(string source, string message) {
        lock (gate) {
            if (closed) return;
            string prefix = DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm:ss.fff zzz", CultureInfo.InvariantCulture)
                + " [" + source + "] ";
            string entry = (message ?? "").Replace("\r\n", "\n").Replace('\r', '\n');
            writer.WriteLine(prefix + entry.Replace("\n", Environment.NewLine + prefix));
        }
    }

    public void Export(string destination) {
        Export(destination, "");
    }

    public void Export(string destination, string context, string assets=null, string exe=null) {
        var report = new DiagnosticReport(FileName, destination);
        string temporary = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try {
            long length;
            lock (gate) length = new FileInfo(FileName).Length;
            using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            using (var text = new StreamWriter(output, new UTF8Encoding(false)))
                report.Write(text, length, context, assets, exe);
            if (File.Exists(destination)) File.Replace(temporary, destination, null);
            else File.Move(temporary, destination);
        } finally { File.Delete(temporary); }
    }

    public void Dispose() {
        lock (gate) {
            if (closed) return;
            Write("LAUNCHER", "Launcher closed.");
            closed = true;
            writer.Dispose();
        }
    }
}
