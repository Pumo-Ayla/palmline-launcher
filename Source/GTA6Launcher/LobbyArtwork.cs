using System;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Drawing=System.Drawing;
using Imaging=System.Drawing.Imaging;

namespace Launcher.Layout {
    public sealed class LobbyArtwork : Grid {
        internal const double EdgeBleed=.025;
        readonly Image front=new Image { Stretch=Stretch.UniformToFill };
        readonly Image back=new Image { Stretch=Stretch.UniformToFill };
        readonly DispatcherTimer timer=new DispatcherTimer();
        string[] files=Array.Empty<string>();
        Drawing.Image gif;
        MemoryStream bytes;
        int index,frame,count;
        byte[] delays;
        DateTime switchAt;

        public LobbyArtwork() {
            ClipToBounds=true; Children.Add(back); Children.Add(front);
            double scale=1/(1-2*EdgeBleed);
            var bleed=new ScaleTransform(scale,scale);
            bleed.Freeze();
            foreach(var layer in new[]{back,front}) {
                layer.RenderTransformOrigin=new Point(.5,.5);
                layer.RenderTransform=bleed;
            }
            timer.Tick+=delegate { Advance(); };
            IsVisibleChanged+=delegate { if(IsVisible && files.Length>0) Show(false); else Stop(); };
            Unloaded+=delegate { Stop(); };
        }
        public static void Check(string path) {
            using(var stream=File.OpenRead(path))
            using(var img=Drawing.Image.FromStream(stream,false,true)) {
                if((long)img.Width*img.Height>40000000) throw new InvalidDataException(Ui.Text("图片尺寸过大"));
                if(img.RawFormat.Guid==Imaging.ImageFormat.Gif.Guid &&
                    (long)img.GetFrameCount(Imaging.FrameDimension.Time)*img.Width*img.Height>260000000)
                    throw new InvalidDataException(Ui.Text("动图尺寸或帧数过大"));
            }
        }
        public void SetFiles(string[] paths) {
            Stop(); files=paths; index=0; front.Source=back.Source=null;
            if(IsVisible && files.Length>0) Show(false);
        }
        void Stop() {
            timer.Stop(); gif?.Dispose(); gif=null; bytes?.Dispose(); bytes=null;
        }
        void Show(bool fade) {
            Stop(); back.Source=front.Source;
            string path=files[index];
            try {
                if(Path.GetExtension(path).Equals(".gif",StringComparison.OrdinalIgnoreCase)) {
                    bytes=new MemoryStream(File.ReadAllBytes(path)); gif=Drawing.Image.FromStream(bytes);
                    count=gif.GetFrameCount(Imaging.FrameDimension.Time); frame=0;
                    delays=gif.PropertyIdList.Contains(0x5100) ? gif.GetPropertyItem(0x5100).Value : null;
                    DrawFrame();
                } else front.Source=DesktopLauncher.DecodeCardImage(path,1600);
            } catch(Exception e) when(e is IOException || e is ArgumentException || e is NotSupportedException || e is UnauthorizedAccessException || e is FormatException || e is OutOfMemoryException) {
                Stop(); front.Source=null;
            }
            front.BeginAnimation(OpacityProperty,fade && SystemParameters.ClientAreaAnimation ? new DoubleAnimation(0,1,TimeSpan.FromMilliseconds(520)) : null);
            switchAt=DateTime.UtcNow.AddSeconds(5);
            if((gif!=null && count>1) || files.Length>1) { SetInterval(); timer.Start(); }
        }
        void SetInterval() {
            int ms=gif!=null && count>1 ? (delays!=null && delays.Length>=4*(frame+1) ? Math.Max(20,Math.Min(60000,BitConverter.ToInt32(delays,frame*4)*10)) : 100) : 5000;
            timer.Interval=TimeSpan.FromMilliseconds(ms);
        }
        void Advance() {
            if(files.Length>1 && DateTime.UtcNow>=switchAt) { index=(index+1)%files.Length; Show(true); }
            else if(gif!=null && count>1) { frame=(frame+1)%count; DrawFrame(); SetInterval(); }
        }
        void DrawFrame() {
            gif.SelectActiveFrame(Imaging.FrameDimension.Time,frame);
            using(var bitmap=new Drawing.Bitmap(gif.Width,gif.Height,Imaging.PixelFormat.Format32bppPArgb)) {
                using(var dc=Drawing.Graphics.FromImage(bitmap)) dc.DrawImageUnscaled(gif,0,0);
                var data=bitmap.LockBits(new Drawing.Rectangle(0,0,bitmap.Width,bitmap.Height),Imaging.ImageLockMode.ReadOnly,bitmap.PixelFormat);
                try {
                    var source=BitmapSource.Create(bitmap.Width,bitmap.Height,96,96,PixelFormats.Pbgra32,null,data.Scan0,data.Stride*bitmap.Height,data.Stride);
                    source.Freeze(); front.Source=source;
                } finally { bitmap.UnlockBits(data); }
            }
        }
    }
}
