using System;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Runtime.InteropServices;
using System.Threading;

internal static class LogReader
{
    [DllImport("kernel32.dll")] static extern IntPtr GetConsoleWindow();
    static void Display(string line) {
        int start=line.IndexOf(" [", StringComparison.Ordinal);
        int end=start<0 ? -1 : line.IndexOf(']',start+2);
        if(end<0) { Console.WriteLine(line); return; }
        Console.Write(line.Substring(0,start+1));
        string tag=line.Substring(start+1,end-start);
        Console.ForegroundColor=tag=="[GAME]" ? ConsoleColor.Green :
            tag=="[EMULATOR]" ? ConsoleColor.Magenta : ConsoleColor.Cyan;
        Console.Write(tag);
        Console.ForegroundColor=ConsoleColor.Gray;
        Console.WriteLine(line.Substring(end+1));
    }

    static void Follow(string path, NamedPipeClientStream client) {
        int ended=0;
        var monitor=new Thread(() => {
            try { client.ReadByte(); } catch(IOException) { }
            finally { Interlocked.Exchange(ref ended,1); }
        }) { IsBackground=true };
        using(var stream=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.ReadWrite))
        using(var input=new StreamReader(stream,Encoding.UTF8)) {
            monitor.Start();
            char[] block=new char[8192];
            var pending=new StringBuilder();
            while(true) {
                bool final=Volatile.Read(ref ended)!=0;
                int count=input.Read(block,0,block.Length);
                if(count==0) {
                    if(final) break;
                    Thread.Sleep(75);
                    continue;
                }
                for(int i=0;i<count;i++) {
                    if(block[i]=='\n') {
                        Display(pending.ToString().TrimEnd('\r'));
                        pending.Clear();
                    } else pending.Append(block[i]);
                }
            }
            if(pending.Length!=0) Display(pending.ToString());
        }
    }
    static int Main(string[] args) {
        string id=args.Length==1 ? args[0] : "";
        Guid token;
        if(!Guid.TryParseExact(id,"N",out token)) return 1;
        try {
            Console.OutputEncoding=new UTF8Encoding(false);
            Console.BackgroundColor=ConsoleColor.Black; Console.ForegroundColor=ConsoleColor.Gray;
            Console.Title="Launcher | Live log";
            Console.Clear();
            using(var client=new NamedPipeClientStream(".","Palmline.Log."+id,PipeDirection.InOut))
            {
                client.Connect(5000);
                using(var writer=new BinaryWriter(client,Encoding.UTF8,true)) writer.Write(GetConsoleWindow().ToInt64());
                using(var reader=new BinaryReader(client,Encoding.UTF8,true)) Follow(reader.ReadString(),client);
            }
            Console.WriteLine("Press any key to close.");
            try { Console.ReadKey(true); } catch(InvalidOperationException) { }
            return 0;
        } catch(IOException) { return 1; } catch(TimeoutException) { return 1; }
    }
}
