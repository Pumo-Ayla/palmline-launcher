using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Windows;

internal static class Ui
{
    static readonly string[] languages={"zh-CN","en-US","zh-TW","ja-JP","ko-KR"};
    static readonly Dictionary<string,string[]> strings=Load("Strings.tsv");
    static readonly Dictionary<string,string[]> chapters=LoadChapters();
    public const string DefaultLanguage="en-US";
    static int column=Array.IndexOf(languages,DefaultLanguage);
    public static string Language { get; private set; }=DefaultLanguage;
    static Dictionary<string,string[]> Load(string resource) {
        var result=new Dictionary<string,string[]>(StringComparer.Ordinal);
        using(var stream=Assembly.GetExecutingAssembly().GetManifestResourceStream(resource))
        using(var reader=new StreamReader(stream)) {
            string line;
            while((line=reader.ReadLine())!=null) {
                var words=line.Split('\t');
                if(words.Length!=languages.Length) throw new InvalidDataException("Invalid launcher translation: "+line);
                result.Add(words[0],words);
            }
        }
        return result;
    }
    public static string Normalize(string code) { return Array.IndexOf(languages,code)>=0 ? code : DefaultLanguage; }
    public static void Apply(string code) {
        Language=Normalize(code);
        CultureInfo.CurrentUICulture=CultureInfo.GetCultureInfo(Language);
        column=Array.IndexOf(languages,Language);
        foreach(var entry in strings) Application.Current.Resources["ui."+entry.Key]=entry.Value[column];
    }
    public static string Text(string source) {
        string[] words;
        return source!=null && strings.TryGetValue(source,out words) ? words[column] : source;
    }
    public static string Localized(string[] translations) { return translations[column]; }
    public static string Format(string source,params object[] values) {
        return string.Format(CultureInfo.GetCultureInfo(Language),Text(source),values);
    }
    public static string ChapterTitle(string title) {
        return chapters.TryGetValue(ChapterKey(title),out var row) ? row[column] : title;
    }
    static Dictionary<string,string[]> LoadChapters() {
        var names=new Dictionary<string,string[]>(StringComparer.OrdinalIgnoreCase);
        foreach(var row in Load("GtaViChapters.tsv").Values)
            foreach(var translation in row) names[ChapterKey(translation)]=row;
        return names;
    }
    static string ChapterKey(string title) {
        return Regex.Replace(title,@"[\s,，、!！]+","").Replace("T**s","Tits");
    }
    public static string Status(string source) {
        return Status(source,column);
    }
    public static string EnglishStatus(string source) { return Status(source,1); }
    static string Status(string source,int language) {
        if(language==0 || string.IsNullOrEmpty(source)) return source;
        if(strings.TryGetValue(source,out var whole)) return whole[language];
        var parts=source.Split(new[]{" · "},StringSplitOptions.None);
        for(int i=0;i<parts.Length;i++) {
            string part=parts[i];
            var count=Regex.Match(part,@"^(\d+) (个着色器跳过|条管线未通过)$");
            var exit=Regex.Match(part,@"^预热进程退出码 (-?\d+)，请查看 prewarm\.log$");
            if(count.Success || exit.Success) {
                string key=count.Success ? "{0} "+count.Groups[2].Value : "预热进程退出码 {0}，请查看 prewarm.log";
                parts[i]=string.Format(strings[key][language],(count.Success ? count : exit).Groups[1].Value);
            } else if(strings.TryGetValue(part,out var words)) parts[i]=words[language];
        }
        return string.Join(" · ",parts);
    }
}
