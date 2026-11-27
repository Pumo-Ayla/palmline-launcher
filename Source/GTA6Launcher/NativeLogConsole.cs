using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

internal sealed class NativeLogConsole
{
    readonly NamedPipeServerStream pipe;
    readonly Process host;
    long windowHandle;
    public IntPtr WindowHandle { get { return new IntPtr(Interlocked.Read(ref windowHandle)); } }
    [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr window);
    [DllImport("user32.dll")] static extern bool IsIconic(IntPtr window);
    [DllImport("user32.dll")] static extern bool ShowWindowAsync(IntPtr window,int command);
    [StructLayout(LayoutKind.Sequential,CharSet=CharSet.Unicode)]
    struct Startup {
        public int size; public string reserved,desktop,title;
        public int x,y,width,height,columns,rows,fill,flags;
        public short show,reservedBytes; public IntPtr reservedData,input,output,error;
    }
    [StructLayout(LayoutKind.Sequential)]
    struct ProcessInfo { public IntPtr process,thread; public int id,threadId; }
    [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true)]
    static extern bool CreateProcess(string app,StringBuilder args,IntPtr security,IntPtr threadSecurity,
        bool inherit,uint flags,IntPtr environment,string cwd,ref Startup startup,out ProcessInfo process);
    [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr handle);
    public bool Open { get { return !host.HasExited; } }

    public NativeLogConsole(string logFile) {
        string id=Guid.NewGuid().ToString("N");
        pipe=new NamedPipeServerStream("Palmline.Log."+id,PipeDirection.InOut,1,PipeTransmissionMode.Byte,PipeOptions.Asynchronous);
        try {
            var directory=Path.Combine(Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location),"Input");
            var helper=Path.Combine(directory,"Palmline.Log.exe");
            if(!File.Exists(helper)) throw new FileNotFoundException(null,helper);
            var conhost=Path.Combine(Environment.SystemDirectory,"conhost.exe");
            var command=new StringBuilder("\""+conhost+"\" \""+helper+"\" "+id);
            var startup=new Startup {size=Marshal.SizeOf(typeof(Startup))};
            ProcessInfo info;
            if(!CreateProcess(conhost,command,IntPtr.Zero,IntPtr.Zero,false,0x10,IntPtr.Zero,directory,ref startup,out info))
                throw new Win32Exception(Marshal.GetLastWin32Error());
            try { host=Process.GetProcessById(info.id); }
            finally { CloseHandle(info.thread); CloseHandle(info.process); }
        } catch { pipe.Dispose(); throw; }
        host.Exited+=delegate { pipe.Dispose(); };
        host.EnableRaisingEvents=true;
        new Thread(() => Connect(logFile)) { IsBackground=true }.Start();
    }
    void Connect(string logFile) {
        try {
            pipe.WaitForConnection();
            using(var reader=new BinaryReader(pipe,Encoding.UTF8,true))
                Interlocked.Exchange(ref windowHandle,reader.ReadInt64());
            using(var writer=new BinaryWriter(pipe,Encoding.UTF8,true)) {
                writer.Write(logFile);
                writer.Flush();
            }
        } catch(IOException) { } catch(ObjectDisposedException) { } catch(InvalidOperationException) { }
    }
    public void Activate() {
        var window=WindowHandle;
        if(window==IntPtr.Zero) return;
        if(IsIconic(window)) ShowWindowAsync(window,9);
        SetForegroundWindow(window);
    }
    public void Finish() { pipe.Dispose(); }
}
