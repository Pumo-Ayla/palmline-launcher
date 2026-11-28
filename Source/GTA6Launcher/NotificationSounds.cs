using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Media;
using System.Xml.Linq;

internal enum LauncherNoticeKind { Installed, Preloading, Ready, Started, Progress, Unlocked, Confirmation, Failure, ControllerConnected, ControllerDisconnected }
internal enum NoticeTone { Silent, Tap, Chord, Rise, Pulse, Knock, Fall, Achievement, Link, Unlink }

internal sealed class NotificationSoundSettings {
    internal static readonly Dictionary<LauncherNoticeKind,string> EventLabels=new Dictionary<LauncherNoticeKind,string> {
        {LauncherNoticeKind.Installed,"安装完成"}, {LauncherNoticeKind.Preloading,"开始预载"},
        {LauncherNoticeKind.Ready,"预载完成"}, {LauncherNoticeKind.Started,"游戏启动成功"},
        {LauncherNoticeKind.Progress,"成就进度更新"}, {LauncherNoticeKind.Unlocked,"成就解锁"},
        {LauncherNoticeKind.Confirmation,"操作确认"}, {LauncherNoticeKind.Failure,"操作失败"},
        {LauncherNoticeKind.ControllerConnected,"手柄已连接"}, {LauncherNoticeKind.ControllerDisconnected,"手柄已断开"}
    };
    internal static readonly Dictionary<NoticeTone,string> ToneLabels=new Dictionary<NoticeTone,string> {
        {NoticeTone.Silent,"静音"}, {NoticeTone.Tap,"轻触音"}, {NoticeTone.Chord,"和弦音"},
        {NoticeTone.Rise,"上行双音"}, {NoticeTone.Pulse,"脉冲音"}, {NoticeTone.Knock,"轻叩音"},
        {NoticeTone.Fall,"下行双音"}, {NoticeTone.Achievement,"成就铃音"},
        {NoticeTone.Link,"设备连接音"}, {NoticeTone.Unlink,"设备断开音"}
    };
    static readonly Dictionary<LauncherNoticeKind,NoticeTone> defaults=new Dictionary<LauncherNoticeKind,NoticeTone> {
        {LauncherNoticeKind.Installed,NoticeTone.Chord}, {LauncherNoticeKind.Preloading,NoticeTone.Tap},
        {LauncherNoticeKind.Ready,NoticeTone.Rise}, {LauncherNoticeKind.Started,NoticeTone.Pulse},
        {LauncherNoticeKind.Progress,NoticeTone.Silent}, {LauncherNoticeKind.Unlocked,NoticeTone.Achievement},
        {LauncherNoticeKind.Confirmation,NoticeTone.Knock}, {LauncherNoticeKind.Failure,NoticeTone.Fall},
        {LauncherNoticeKind.ControllerConnected,NoticeTone.Link}, {LauncherNoticeKind.ControllerDisconnected,NoticeTone.Unlink}
    };
    readonly Dictionary<LauncherNoticeKind,NoticeTone> overrides=new Dictionary<LauncherNoticeKind,NoticeTone>();
    public bool Enabled { get; set; }=true;
    public NoticeTone this[LauncherNoticeKind kind] {
        get { return overrides.TryGetValue(kind,out var tone) ? tone : defaults[kind]; }
        set { if(value==defaults[kind]) overrides.Remove(kind); else overrides[kind]=value; }
    }
    public void Read(XElement element) {
        if(element==null) return;
        if(bool.TryParse((string)element.Attribute("enabled"),out var enabled)) Enabled=enabled;
        foreach(var kind in EventLabels.Keys) {
            var name=(string)element.Attribute(kind.ToString());
            if(Enum.TryParse(name,out NoticeTone tone) && tone.ToString()==name && ToneLabels.ContainsKey(tone)) this[kind]=tone;
        }
    }
    public XElement ToXml() {
        if(Enabled && overrides.Count==0) return null;
        var element=new XElement("notificationSounds",new XAttribute("enabled",Enabled));
        foreach(var pair in overrides) element.SetAttributeValue(pair.Key.ToString(),pair.Value.ToString());
        return element;
    }
}

internal sealed class NotificationSounds : IDisposable {
    readonly NotificationSoundSettings settings;
    readonly Action<string> log;
    readonly Dictionary<NoticeTone,SoundPlayer> players=new Dictionary<NoticeTone,SoundPlayer>();
    readonly Stopwatch sinceLast=new Stopwatch();
    SoundPlayer current;
    public NotificationSounds(NotificationSoundSettings settings,Action<string> log) {
        this.settings=settings;
        this.log=log;
    }
    public void Play(LauncherNoticeKind kind) {
        if(kind==LauncherNoticeKind.Progress && sinceLast.IsRunning && sinceLast.ElapsedMilliseconds<2000) return;
        Preview(settings[kind]);
    }
    public void Preview(NoticeTone tone) {
        if(!settings.Enabled || tone==NoticeTone.Silent) return;
        try {
            if(!players.TryGetValue(tone,out var player)) {
                string resource=tone==NoticeTone.Achievement ? "SteamAchievement.wav" : "Notification."+tone+".wav";
                var stream=typeof(NotificationSounds).Assembly.GetManifestResourceStream(resource);
                if(stream==null) throw new FileNotFoundException(resource);
                player=new SoundPlayer(stream);
                try { player.Load(); }
                catch { player.Dispose(); stream.Dispose(); throw; }
                players.Add(tone,player);
            }
            player.Play();
            current=player;
            sinceLast.Restart();
        } catch(Exception ex) when(ex is IOException || ex is InvalidOperationException || ex is TimeoutException ||
                ex is System.ComponentModel.Win32Exception) {
            log("Notification audio ("+tone+"): "+ex.Message);
        }
    }
    public void Stop() {
        current?.Stop();
        current=null;
    }
    public void Dispose() {
        Stop();
        foreach(var player in players.Values) {
            var stream=player.Stream;
            player.Dispose();
            stream?.Dispose();
        }
        players.Clear();
    }
}
