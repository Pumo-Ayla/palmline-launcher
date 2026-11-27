using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows.Input;

internal static class GameKeyPrompts
{
    public static string InstallModule(string root) {
        var assembly=typeof(GameKeyPrompts).Assembly;
        string directory=Path.Combine(root,"Runtime","KeyPrompts",assembly.ManifestModule.ModuleVersionId.ToString("N"));
        Directory.CreateDirectory(directory);
        string path=Path.Combine(directory,"key_prompts.py");
        using(var resource=assembly.GetManifestResourceStream("Palmline.KeyPrompts.py"))
        using(var bytes=new MemoryStream()) {
            resource.CopyTo(bytes);
            byte[] module=bytes.ToArray();
            if(File.Exists(path) && File.ReadAllBytes(path).SequenceEqual(module)) return path;
            string pending=Path.Combine(directory,Guid.NewGuid().ToString("N")+".tmp");
            try {
                File.WriteAllBytes(pending,module);
                try { File.Move(pending,path); }
                catch(IOException) when(File.Exists(path)) {
                    if(!File.ReadAllBytes(path).SequenceEqual(module)) File.Replace(pending,path,null);
                }
            } finally { File.Delete(pending); }
        }
        return path;
    }
    static int VirtualKey(string id) {
        switch(id) {
            case "Mouse.Left":return 1; case "Mouse.Right":return 2; case "Mouse.Middle":return 4;
            case "Mouse.XButton1":return 5; case "Mouse.XButton2":return 6;
            case "Wheel.Up":return 0xe8; case "Wheel.Down":return 0xff;
        }
        Key key;
        return id!=null && id.StartsWith("Key.",StringComparison.Ordinal) && Enum.TryParse(id.Substring(4),out key)
            ? KeyInterop.VirtualKeyFromKey(key) : 0;
    }

    static string Keycap(int vk) {
        switch(vk) {
            case 9:return "Tab";
            case 1:return "LMB"; case 2:return "RMB"; case 4:return "MMB";
            case 5:return "Mouse 4"; case 6:return "Mouse 5";
            case 0xe8:return "Wheel Up"; case 0xff:return "Wheel Down";
            case 8:return "Backspace"; case 13:return "Enter"; case 27:return "Esc"; case 32:return "Space";
            case 16:return "Shift"; case 17:return "Ctrl"; case 18:return "Alt";
        }
        if(vk>=0x30 && vk<=0x39) return ((char)vk).ToString();
        return KeyInterop.KeyFromVirtualKey(vk).ToString();
    }

    public static string Command(bool preset,string[] profile,bool show) {
        string command=preset ? "PLAY\tGTA6_PRESET" : "PLAY\tKEYBOARD";
        var custom=new Dictionary<string,int>();
        if(!preset) {
            if(profile==null || profile.Length!=MappingStep.All.Length) return command;
            var padKeys=profile.TakeWhile((_,i)=>MappingStep.All[i].Id!="Share").Select(VirtualKey).ToArray();
            if(padKeys.Any(key=>key<=0 || key>255)) return command;
            for(int i=0;i<padKeys.Length;i++) custom.Add(MappingStep.All[i].Id,padKeys[i]);
            command+="\tPAD="+string.Join(",",padKeys.Select(k=>k.ToString("X2")));
        }
        if(!show) return command;
        return WithPrompts(command,(layer,control,preferred)=>
            preset ? GtaViPreset.PromptKey(layer,control,preferred) : custom[control]);
    }

    static string WithPrompts(string command,Func<string,string,int,int> resolve) {
        var lines=new List<string>();
        void Bind(string actions,string layer,string control,int preferred) {
            int key=resolve(layer,control,preferred);
            if(key!=0) foreach(var action in actions.Split(' ')) lines.Add("INPUT_"+action+"="+Keycap(key));
        }
        Bind("ATTACK","foot","RT",1);
        Bind("AIM CONTEXT_LT","foot","LT",2);
        Bind("VEH_ACCELERATE VEH_CAR_ACCELERATE","drive","RT",'W');
        Bind("VEH_BRAKE","drive","LT",'S');
        Bind("VEH_HANDBRAKE","drive","A",32);
        Bind("VEH_HORN","drive","LS",'E');
        Bind("JUMP CONTEXT_A","foot","A",32);
        Bind("GAME_MENU_ACCEPT FRONTEND_ACCEPT","foot","A",13);
        Bind("FRONTEND_CANCEL CONTEXT_B","foot","B",8);
        Bind("FRONTEND_PAUSE","foot","Menu",27);
        Bind("MAP","foot","View",'M');
        Bind("SPRINT","foot","LS",16);
        Bind("COVER","foot","RB",'Q');
        Bind("SELECT_WEAPON","foot","LB",9);
        Bind("LOOK_BEHIND","foot","RS",'C');
        Bind("MELEE_ATTACK RELOAD","foot","X",'R');
        Bind("ENTER","foot","Y",'F');
        Bind("CONTEXT_Y","foot","Y",'E');
        Bind("PHONE","foot","Up",'T');
        Bind("GAME_MENU_UP CELLPHONE_UP","foot","Up",0x26);
        Bind("GAME_MENU_LEFT","foot","Left",0x25);
        Bind("GAME_MENU_RIGHT","foot","Right",0x27);
        Bind("GAME_MENU_DOWN","foot","Down",0x28);
        lines.Add("INPUT_MOVE_UP_ONLY=W");
        return command+"\tPROMPTS="+Convert.ToBase64String(Encoding.ASCII.GetBytes(string.Join("\n",lines)));
    }
}
