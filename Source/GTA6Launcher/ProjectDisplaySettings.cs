using System;
using System.Globalization;
using System.IO;
using System.Xml;
using System.Xml.Linq;

internal sealed class ProjectDisplaySettings
{
    static readonly TimeZoneInfo PacificZone = TimeZoneInfo.FindSystemTimeZoneById("Pacific Standard Time");
    public string Language { get; set; }=Ui.DefaultLanguage;
    public const string DefaultLauncherName="SRLauncher";
    public const string FullLauncherName="Series Relay Launcher";
    public string LauncherName { get; set; }=DefaultLauncherName;
    public bool ShowFps { get; set; } = true;
    public bool DlssSuperResolution { get; set; }
    public string DlssQuality { get; set; }="quality";
    public bool UseKeyPrompts { get; set; }
    public bool RepositoryNoticeSeen { get; set; }
    public int FrameMultiplier { get; set; }

    public static ProjectDisplaySettings Load(string path) {
        var settings = new ProjectDisplaySettings();
        try {
            if (File.Exists(path)) {
                var xml = XElement.Load(path);
                settings.Language=Ui.Normalize((string)xml.Attribute("language"));
                var name=((string)xml.Attribute("launcherName"))?.Trim();
                if(!string.IsNullOrEmpty(name) && name!=FullLauncherName) settings.LauncherName=name;
                bool value;
                // Older settings can predate the repository notice.
                settings.RepositoryNoticeSeen=!bool.TryParse((string)xml.Attribute("repositoryNoticeSeen"),out value) || value;
                if (bool.TryParse((string)xml.Attribute("useKeyPrompts"), out value)) settings.UseKeyPrompts = value;
                if (bool.TryParse((string)xml.Attribute("showFps"), out value)) settings.ShowFps = value;
                if (bool.TryParse((string)xml.Attribute("dlssSuperResolution"), out value)) settings.DlssSuperResolution = value;
                string quality=(string)xml.Attribute("dlssQuality");
                if(quality=="balanced" || quality=="performance") settings.DlssQuality=quality;
                int multiplier;
                if(int.TryParse((string)xml.Attribute("dlssFrameMultiplier"),out multiplier) && multiplier>=2 && multiplier<=6)
                    settings.FrameMultiplier=multiplier;
            }
        } catch (IOException) { }
          catch (UnauthorizedAccessException) { }
          catch (XmlException) { }
        return settings;
    }

    public void Save(string path) {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path)));
        string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try {
            new XElement("display", new XAttribute("showFps", ShowFps),new XAttribute("language",Language),
                new XAttribute("repositoryNoticeSeen",RepositoryNoticeSeen),
                LauncherName==DefaultLauncherName ? null : new XAttribute("launcherName",LauncherName),
                new XAttribute("dlssSuperResolution", DlssSuperResolution),
                new XAttribute("dlssQuality",DlssQuality),
                new XAttribute("useKeyPrompts",UseKeyPrompts),new XAttribute("dlssFrameMultiplier",FrameMultiplier)).Save(temporary);
            if (File.Exists(path)) File.Replace(temporary, path, null);
            else File.Move(temporary, path);
        } finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    public static string FormatPullTime(string timestamp) {
        DateTimeOffset instant;
        if (!DateTimeOffset.TryParse(timestamp, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out instant))
            return Ui.Text("拉取时间未知");
        var time = TimeZoneInfo.ConvertTime(instant, PacificZone);
        string abbreviation = PacificZone.IsDaylightSavingTime(instant) ? "PDT" : "PST";
        return Ui.Format("拉取于 {0}（太平洋时间 · {1}）",time.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),abbreviation);
    }
}
