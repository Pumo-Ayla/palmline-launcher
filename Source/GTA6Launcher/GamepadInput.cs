using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Media.Media3D;

internal struct PadState
{
    public static readonly string[] ButtonNames = { "A","B","X","Y","View","Guide","Menu","LS","RS","LB","RB","Up","Down","Left","Right","Share","RightPaddle1","LeftPaddle1","RightPaddle2","LeftPaddle2" };
    public uint Buttons;
    public bool ReportsPaddles;
    public double LX, LY, RX, RY, LT, RT;
    public int Button(int bit) { return bit < 0 ? 0 : (int)((Buttons >> bit) & 1); }
}

internal sealed class SensorOrientation
{
    public Quaternion Pose { get; private set; } = Quaternion.Identity;
    public bool Receiving { get { return gyroTime != 0; } }
    ulong gyroTime, accelTime;
    Vector3D? gravity;
    public void Reset() { gyroTime=accelTime=0; gravity=null; Pose=Quaternion.Identity; }

    public void Sample(int type, ulong timestamp, float x, float y, float z) {
        // SDL describes a pad held flat: +Y is up and +Z points toward the player.
        // In the face-on model +Z points out of the buttons and +Y toward the bumpers.
        var vector = new Vector3D(x,-z,y);
        double length = vector.Length;
        if (timestamp==0 || double.IsNaN(length) || double.IsInfinity(length)) return;
        if (type==2) {
            double dt=Elapsed(ref gyroTime,timestamp);
            if (dt==0 || length<.003) return;
            var next=Pose * new Quaternion(vector, length*dt*180/Math.PI);
            next.Normalize(); Pose=next;
        } else if (type==1 && length>8.4 && length<11.2) {
            // Gravity corrects pitch and roll; heading stays relative to the opening pose.
            double dt=Elapsed(ref accelTime,timestamp);
            var matrix=Matrix3D.Identity; matrix.Rotate(Pose);
            Vector3D up=matrix.Transform(vector/length);
            if (!gravity.HasValue) { gravity=up; return; }
            Vector3D axis=Vector3D.CrossProduct(up,gravity.Value);
            if (dt==0 || axis.LengthSquared<1e-8) return;
            double angle=Vector3D.AngleBetween(up,gravity.Value)*(1-Math.Exp(-1.6*dt));
            var next=new Quaternion(axis,angle)*Pose;
            next.Normalize(); Pose=next;
        }
    }
    static double Elapsed(ref ulong last, ulong now) {
        if(now<=last) return 0;
        double dt=last==0 ? 0 : (now-last)*1e-9;
        last=now;
        return dt<=.2 ? dt : 0;
    }
}

internal sealed class GamepadInput : IDisposable
{
    const uint GamepadSubsystem=0x2000, GamepadAdded=0x653, GamepadRemoved=0x654, SensorEvent=0x659;
    const int Accel=1, Gyro=2;
    const string Library="SDL3.dll";
    static IntPtr module;
    bool initialized;
    IntPtr pad;
    uint instance;
    readonly Dictionary<uint,string> connectedPads=new Dictionary<uint,string>();
    public event Action<bool,string> ConnectionChanged;
    public bool Available { get { return initialized; } }
    public string Name { get; private set; }
    public uint DeviceId { get { return instance; } }
    bool reportsPaddles;
    public bool GyroEnabled { get; private set; }
    public readonly SensorOrientation Orientation = new SensorOrientation();

    [StructLayout(LayoutKind.Explicit, Size=128)]
    struct Event {
        [FieldOffset(0)] public uint Type;
        [FieldOffset(8)] public ulong Time;
        [FieldOffset(16)] public uint Device;
        [FieldOffset(20)] public int Sensor;
        [FieldOffset(24)] public float X;
        [FieldOffset(28)] public float Y;
        [FieldOffset(32)] public float Z;
        [FieldOffset(40)] public ulong SensorTime;
    }
    [DllImport("kernel32.dll", CharSet=CharSet.Unicode, ExactSpelling=true)]
    static extern IntPtr LoadLibraryExW(string path,IntPtr file,uint flags);
    // Windows x64 uses a single native calling convention. SDL3 bool returns one byte.
    [DllImport(Library)] static extern byte SDL_InitSubSystem(uint flags);
    [DllImport(Library)] static extern void SDL_QuitSubSystem(uint flags);
    [DllImport(Library)] static extern byte SDL_SetHint(string name,string value);
    [DllImport(Library)] static extern byte SDL_PollEvent(out Event ev);
    [DllImport(Library)] static extern IntPtr SDL_GetGamepads(out int count);
    [DllImport(Library)] static extern void SDL_free(IntPtr ptr);
    [DllImport(Library)] static extern IntPtr SDL_OpenGamepad(uint id);
    [DllImport(Library)] static extern void SDL_CloseGamepad(IntPtr handle);
    [DllImport(Library)] static extern byte SDL_GamepadConnected(IntPtr handle);
    [DllImport(Library)] static extern IntPtr SDL_GetGamepadNameForID(uint id);
    [DllImport(Library)] static extern IntPtr SDL_GetJoystickNameForID(uint id);
    [DllImport(Library)] static extern int SDL_GetRealGamepadTypeForID(uint id);
    [DllImport(Library)] static extern byte SDL_GamepadHasButton(IntPtr handle,int button);
    [DllImport(Library)] static extern byte SDL_GetGamepadButton(IntPtr handle,int button);
    [DllImport(Library)] static extern short SDL_GetGamepadAxis(IntPtr handle,int axis);
    [DllImport(Library)] static extern byte SDL_GamepadHasSensor(IntPtr handle,int type);
    [DllImport(Library)] static extern byte SDL_SetGamepadSensorEnabled(IntPtr handle,int type,byte enabled);

    public GamepadInput() {
        if(module==IntPtr.Zero) {
            string directory=Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
            module=LoadLibraryExW(Path.Combine(directory,"Input",Library),IntPtr.Zero,0x1100);
        }
        if(module==IntPtr.Zero) return;
        SDL_SetHint("SDL_JOYSTICK_ALLOW_BACKGROUND_EVENTS","1");
        initialized=SDL_InitSubSystem(GamepadSubsystem)!=0;
        // SDL queues add events for pads present at initialization. Remember them before subscribing.
        Pump();
    }
    public void Pump() {
        if(!initialized) return;
        Event ev;
        while(SDL_PollEvent(out ev)!=0) {
            if(ev.Type==GamepadAdded && !connectedPads.ContainsKey(ev.Device)) {
                string name=DeviceName(ev.Device);
                connectedPads.Add(ev.Device,name);
                ConnectionChanged?.Invoke(true,name);
            } else if(ev.Type==GamepadRemoved && connectedPads.TryGetValue(ev.Device,out var removed)) {
                connectedPads.Remove(ev.Device);
                ConnectionChanged?.Invoke(false,removed);
            } else if(GyroEnabled && ev.Type==SensorEvent && ev.Device==instance)
                Orientation.Sample(ev.Sensor,ev.SensorTime!=0 ? ev.SensorTime : ev.Time,ev.X,ev.Y,ev.Z);
        }
        if(pad!=IntPtr.Zero && SDL_GamepadConnected(pad)==0) ClosePad();
    }
    public int Discover(bool open) {
        if(!initialized) return 0;
        int count;
        IntPtr list=SDL_GetGamepads(out count);
        if(list==IntPtr.Zero) return 0;
        try {
            if(open && pad==IntPtr.Zero) {
                for(int i=0; i<count && pad==IntPtr.Zero; i++) {
                    uint id=unchecked((uint)Marshal.ReadInt32(list,4*i));
                    pad=SDL_OpenGamepad(id);
                    if(pad==IntPtr.Zero) continue;
                    instance=id;
                    Name=DeviceName(id);
                    reportsPaddles=false;
                    for(int button=16;button<=19;button++) reportsPaddles |= SDL_GamepadHasButton(pad,button)!=0;
                    GyroEnabled=SDL_GamepadHasSensor(pad,Gyro)!=0 && SDL_SetGamepadSensorEnabled(pad,Gyro,1)!=0;
                    if(GyroEnabled && SDL_GamepadHasSensor(pad,Accel)!=0) SDL_SetGamepadSensorEnabled(pad,Accel,1);
                }
            }
            return count;
        } finally { SDL_free(list); }
    }
    static string DeviceName(uint id) {
        return ModelName(ReadUtf8(SDL_GetJoystickNameForID(id)),ReadUtf8(SDL_GetGamepadNameForID(id)),SDL_GetRealGamepadTypeForID(id));
    }
    internal static string ModelName(string device,string mapped,int type) {
        bool xbox=type==2 || type==3;
        foreach(var candidate in new[]{device,mapped}) {
            var name=(candidate ?? "").Trim();
            if(name.Length==0) continue;
            switch(name.ToLowerInvariant()) {
                case "xbox controller": case "xbox wireless controller": case "xbox gamepad": xbox=true; continue;
                case "gamepad": case "controller": case "wireless controller": case "generic gamepad":
                case "unknown": case "unknown controller": case "xinput controller": case "xinput gamepad": continue;
            }
            return name;
        }
        return xbox ? "XBOX手柄" : "未知手柄";
    }
    static string ReadUtf8(IntPtr p) {
        if(p==IntPtr.Zero) return null;
        int size=0;
        while(Marshal.ReadByte(p,size)!=0) size++;
        byte[] bytes=new byte[size];
        Marshal.Copy(p,bytes,0,size);
        return Encoding.UTF8.GetString(bytes);
    }
    double Stick(int axis) {
        int raw=SDL_GetGamepadAxis(pad,axis);
        double value=raw/(raw<0 ? 32768.0 : 32767.0);
        return Math.Abs(value)<.045 ? 0 : value;
    }
    public PadState Read() {
        var state=new PadState { ReportsPaddles=reportsPaddles };
        if(pad==IntPtr.Zero) return state;
        for(int i=0;i<PadState.ButtonNames.Length;i++)
            if(SDL_GetGamepadButton(pad,i)!=0) state.Buttons |= 1u<<i;
        state.LX=Stick(0); state.LY=-Stick(1); state.RX=Stick(2); state.RY=-Stick(3);
        state.LT=Math.Max(0,SDL_GetGamepadAxis(pad,4)/32767.0);
        state.RT=Math.Max(0,SDL_GetGamepadAxis(pad,5)/32767.0);
        return state;
    }
    public void ClosePad() {
        if(pad!=IntPtr.Zero) {
            if(GyroEnabled) {
                SDL_SetGamepadSensorEnabled(pad,Gyro,0);
                if(SDL_GamepadHasSensor(pad,Accel)!=0) SDL_SetGamepadSensorEnabled(pad,Accel,0);
            }
            SDL_CloseGamepad(pad); pad=IntPtr.Zero;
        }
        Name=null; instance=0; reportsPaddles=false; GyroEnabled=false; Orientation.Reset();
    }
    public void Dispose() {
        ClosePad();
        if(initialized) { SDL_QuitSubSystem(GamepadSubsystem); initialized=false; }
    }
}
