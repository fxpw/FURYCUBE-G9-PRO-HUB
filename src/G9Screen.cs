// Host-side monochrome artwork and widget renderer. This file performs no USB
// operations and does not claim that a frame was shown on the physical mouse.
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Web.Script.Serialization;

namespace Furycube.G9.Screen {
    public sealed class MonoFrame {
        public int Width {get;private set;}
        public int Height {get;private set;}
        // Page-major: Buffer[(y/8)*Width+x], bit(y%8). A set bit is lit.
        public byte[] Buffer {get;private set;}
        public int PageCount {get {return (Height+7)/8;}}
        public MonoFrame(int width,int height) {
            ValidateGeometry(width,height);Width=width;Height=height;
            Buffer=new byte[checked(width*((height+7)/8))];
        }
        public MonoFrame(int width,int height,byte[] buffer):this(width,height) {
            if(buffer==null||buffer.Length!=Buffer.Length)throw new ArgumentException("Frame buffer length does not match geometry","buffer");
            Array.Copy(buffer,Buffer,buffer.Length);ClearUnusedBits();
        }
        public static void ValidateGeometry(int width,int height) {
            if(width<1||height<1||width>4096||height>4096||(long)width*height>4194304)throw new ArgumentOutOfRangeException("width","Screen geometry must be positive and contain at most4194304pixels");
        }
        void Check(int x,int y) {if(x<0||x>=Width||y<0||y>=Height)throw new ArgumentOutOfRangeException("x","Pixel lies outside the screen");}
        public bool GetPixel(int x,int y) {Check(x,y);return (Buffer[(y/8)*Width+x]&(1<<(y&7)))!=0;}
        public void SetPixel(int x,int y,bool lit) {
            Check(x,y);int i=(y/8)*Width+x,mask=1<<(y&7);
            Buffer[i]=lit?(byte)(Buffer[i]|mask):(byte)(Buffer[i]&~mask);
        }
        public void ClearUnusedBits() {
            int remainder=Height&7;if(remainder==0)return;
            byte mask=(byte)((1<<remainder)-1);int start=(PageCount-1)*Width;
            for(int x=0;x<Width;x++)Buffer[start+x]&=mask;
        }
        public void Fill(bool lit) {for(int i=0;i<Buffer.Length;i++)Buffer[i]=lit?(byte)255:(byte)0;ClearUnusedBits();}
        public void Invert() {for(int i=0;i<Buffer.Length;i++)Buffer[i]=(byte)~Buffer[i];ClearUnusedBits();}
        public MonoFrame Clone() {return new MonoFrame(Width,Height,Buffer);}
        public Bitmap ToBitmap(int scale) {
            if(scale<1||scale>32||(long)Width*Height*scale*scale>16777216)throw new ArgumentOutOfRangeException("scale");
            int w=Width*scale,h=Height*scale;var image=new Bitmap(w,h,PixelFormat.Format32bppArgb);
            BitmapData bits=image.LockBits(new Rectangle(0,0,w,h),ImageLockMode.WriteOnly,PixelFormat.Format32bppArgb);
            try {
                int[] row=new int[w];
                for(int y=0;y<Height;y++) {
                    for(int x=0;x<Width;x++){int color=GetPixel(x,y)?unchecked((int)0xffffffff):unchecked((int)0xff000000);for(int s=0;s<scale;s++)row[x*scale+s]=color;}
                    for(int s=0;s<scale;s++)Marshal.Copy(row,0,IntPtr.Add(bits.Scan0,(y*scale+s)*bits.Stride),row.Length);
                }
            } finally {image.UnlockBits(bits);}
            return image;
        }
        public Bitmap ToBitmap() {return ToBitmap(1);}
    }

    public enum ResizeMode {Fit,Crop,Stretch}
    public sealed class ImageOptions {
        public ResizeMode Resize {get;set;}
        public int Threshold {get;set;}
        public bool Dither {get;set;}
        public bool Invert {get;set;}
        public bool BackgroundLit {get;set;}
        public ImageOptions() {Resize=ResizeMode.Fit;Threshold=128;}
        public void Validate() {
            if(!Enum.IsDefined(typeof(ResizeMode),Resize)||Threshold<0||Threshold>255)throw new InvalidDataException("Invalid image resize mode or threshold");
        }
    }
    public static class ImageConverter {
        static double Luma(Color c,bool backgroundLit) {
            double a=c.A/255.0,bg=backgroundLit?255:0;
            // Integer coefficients sum to10000, preserving exact grayscale and
            // white endpoints even when the threshold is255.
            double luminance=(2126*c.R+7152*c.G+722*c.B)/10000.0;
            return a*luminance+(1-a)*bg;
        }
        static double Sample(Bitmap image,double sx,double sy,bool backgroundLit) {
            sx=Math.Max(0,Math.Min(image.Width-1,sx));sy=Math.Max(0,Math.Min(image.Height-1,sy));
            int x0=(int)Math.Floor(sx),y0=(int)Math.Floor(sy),x1=Math.Min(x0+1,image.Width-1),y1=Math.Min(y0+1,image.Height-1);
            double fx=sx-x0,fy=sy-y0;
            double left=Luma(image.GetPixel(x0,y0),backgroundLit),right=Luma(image.GetPixel(x1,y0),backgroundLit);
            double top=left+(right-left)*fx;
            left=Luma(image.GetPixel(x0,y1),backgroundLit);right=Luma(image.GetPixel(x1,y1),backgroundLit);
            double bottom=left+(right-left)*fx;
            return top+(bottom-top)*fy;
        }
        public static MonoFrame Convert(Bitmap image,int width,int height,ImageOptions options) {
            if(image==null)throw new ArgumentNullException("image");if(options==null)throw new ArgumentNullException("options");options.Validate();
            var frame=new MonoFrame(width,height);double dw=width,dh=height;
            if(options.Resize!=ResizeMode.Stretch) {
                double sx=(double)width/image.Width,sy=(double)height/image.Height;
                double ratio=options.Resize==ResizeMode.Fit?Math.Min(sx,sy):Math.Max(sx,sy);
                dw=image.Width*ratio;dh=image.Height*ratio;
            }
            double dx=(width-dw)/2,dy=(height-dh)/2;
            double[] values=new double[width*height];bool[] active=new bool[values.Length];
            for(int y=0;y<height;y++)for(int x=0;x<width;x++) {
                int index=y*width+x;double px=x+0.5,py=y+0.5;
                active[index]=px>=dx&&px<dx+dw&&py>=dy&&py<dy+dh;
                values[index]=active[index]?Sample(image,(px-dx)*image.Width/dw-0.5,(py-dy)*image.Height/dh-0.5,options.BackgroundLit):(options.BackgroundLit?255:0);
            }
            for(int y=0;y<height;y++)for(int x=0;x<width;x++) {
                int index=y*width+x;double current=values[index];
                bool lit=active[index]?current>=options.Threshold:options.BackgroundLit;
                frame.SetPixel(x,y,lit);
                if(!options.Dither||!active[index])continue;
                double error=current-(lit?255:0);
                Add(values,active,width,height,x+1,y,error*7/16);
                Add(values,active,width,height,x-1,y+1,error*3/16);
                Add(values,active,width,height,x,y+1,error*5/16);
                Add(values,active,width,height,x+1,y+1,error/16);
            }
            if(options.Invert)frame.Invert();return frame;
        }
        static void Add(double[] values,bool[] active,int width,int height,int x,int y,double error) {
            if(x>=0&&x<width&&y>=0&&y<height&&active[y*width+x])values[y*width+x]+=error;
        }
    }

    public enum WidgetKind {Text,Clock,Battery,Dpi,CpuPercent,RamPercent,GpuPercent,CpuTemperatureC,GpuTemperatureC}
    public sealed class ScreenWidget {
        public string Id {get;set;}
        public WidgetKind Kind {get;set;}
        public int X {get;set;}
        public int Y {get;set;}
        public int Width {get;set;}
        public int Height {get;set;}
        public string Text {get;set;}
        public string Format {get;set;}
        public float FontSize {get;set;}
        public bool Bold {get;set;}
        public bool Lit {get;set;}
        public bool ClearBackground {get;set;}
        public bool ShowLabel {get;set;}
        public ScreenWidget() {Id=Guid.NewGuid().ToString("N");Width=60;Height=12;FontSize=9;Lit=true;ShowLabel=true;Text="Text";Format="HH:mm";}
        public void Validate() {
            if(!Enum.IsDefined(typeof(WidgetKind),Kind)||Width<1||Height<1||Width>4096||Height>4096||X < -4096||X>4096||Y < -4096||Y>4096||float.IsNaN(FontSize)||float.IsInfinity(FontSize)||FontSize<4||FontSize>256)throw new InvalidDataException("Invalid widget geometry, kind or font size");
            if(Text!=null&&Text.Length>4096)throw new InvalidDataException("Widget text is too long");
            if(Format!=null&&Format.Length>128)throw new InvalidDataException("Widget clock format is too long");
        }
    }
    public sealed class WidgetContext {
        public DateTime Now {get;set;}
        public double? BatteryPercent {get;set;}
        public int? Dpi {get;set;}
        // Supplied by a future sensor adapter or explicit preview value; this
        // renderer never reads CPU counters or starts a sensor background task.
        public double? CpuPercent {get;set;}
        public double? RamPercent {get;set;}
        public double? GpuPercent {get;set;}
        public double? CpuTemperatureC {get;set;}
        public double? GpuTemperatureC {get;set;}
        public string CultureName {get;set;}
        public WidgetContext() {Now=DateTime.Now;CultureName="ru-RU";}
    }
    public static class WidgetRenderer {
        static string Percent(double? value) {
            if(!value.HasValue||double.IsNaN(value.Value)||double.IsInfinity(value.Value))return "--%";
            return Math.Round(Math.Max(0,Math.Min(100,value.Value))).ToString("0",CultureInfo.InvariantCulture)+"%";
        }
        static string Temperature(double? value) {
            if(!value.HasValue||double.IsNaN(value.Value)||double.IsInfinity(value.Value))return "—";
            return Math.Round(value.Value).ToString("0",CultureInfo.InvariantCulture)+"°C";
        }
        public static string GetText(ScreenWidget widget,WidgetContext context) {
            if(widget==null||context==null)throw new ArgumentNullException("widget");
            switch(widget.Kind) {
                case WidgetKind.Clock:return context.Now.ToString(String.IsNullOrEmpty(widget.Format)?"HH:mm":widget.Format,CultureInfo.GetCultureInfo(String.IsNullOrEmpty(context.CultureName)?"ru-RU":context.CultureName));
                case WidgetKind.Battery:return (widget.ShowLabel?"BAT ":"")+Percent(context.BatteryPercent);
                case WidgetKind.Dpi:return (widget.ShowLabel?"DPI ":"")+(context.Dpi.HasValue&&context.Dpi.Value>0?context.Dpi.Value.ToString(CultureInfo.InvariantCulture):"--");
                case WidgetKind.CpuPercent:return (widget.ShowLabel?"CPU ":"")+Percent(context.CpuPercent);
                case WidgetKind.RamPercent:return (widget.ShowLabel?"RAM ":"")+Percent(context.RamPercent);
                case WidgetKind.GpuPercent:return (widget.ShowLabel?"GPU ":"")+Percent(context.GpuPercent);
                case WidgetKind.CpuTemperatureC:return (widget.ShowLabel?"CPU ":"")+Temperature(context.CpuTemperatureC);
                case WidgetKind.GpuTemperatureC:return (widget.ShowLabel?"GPU ":"")+Temperature(context.GpuTemperatureC);
                default:return widget.Text??"";
            }
        }
        public static MonoFrame Render(MonoFrame background,IEnumerable<ScreenWidget> widgets,WidgetContext context) {
            if(background==null||widgets==null||context==null)throw new ArgumentNullException("background");
            using(Bitmap canvas=background.ToBitmap()) {
                using(Graphics graphics=Graphics.FromImage(canvas)) {
                    graphics.TextRenderingHint=TextRenderingHint.SingleBitPerPixelGridFit;
                    graphics.SmoothingMode=System.Drawing.Drawing2D.SmoothingMode.None;
                    foreach(ScreenWidget widget in widgets) {
                        if(widget==null)throw new InvalidDataException("Null widget");widget.Validate();
                        Rectangle bounds=new Rectangle(widget.X,widget.Y,widget.Width,widget.Height);
                        var state=graphics.Save();
                        try {
                            graphics.SetClip(bounds,System.Drawing.Drawing2D.CombineMode.Intersect);
                            if(widget.ClearBackground)graphics.FillRectangle(widget.Lit?Brushes.Black:Brushes.White,bounds);
                            using(var font=new Font(FontFamily.GenericMonospace,widget.FontSize,widget.Bold?FontStyle.Bold:FontStyle.Regular,GraphicsUnit.Pixel))
                            using(var format=(StringFormat)StringFormat.GenericTypographic.Clone()) {
                                format.FormatFlags|=StringFormatFlags.NoWrap;
                                graphics.DrawString(GetText(widget,context),font,widget.Lit?Brushes.White:Brushes.Black,new PointF(widget.X,widget.Y),format);
                            }
                        } finally {graphics.Restore(state);}
                    }
                }
                return ImageConverter.Convert(canvas,background.Width,background.Height,new ImageOptions {Resize=ResizeMode.Stretch,Threshold=128});
            }
        }
        public static List<ScreenWidget> CreateDefault(int width,int height) {
            MonoFrame.ValidateGeometry(width,height);int top=Math.Max(1,height/2),half=Math.Max(1,width/2);
            float font=Math.Max(4,Math.Min(10,top-1));
            return new List<ScreenWidget> {
                new ScreenWidget {Kind=WidgetKind.Clock,X=0,Y=0,Width=half,Height=top,FontSize=font,ShowLabel=false},
                new ScreenWidget {Kind=WidgetKind.Battery,X=half,Y=0,Width=Math.Max(1,width-half),Height=top,FontSize=font},
                new ScreenWidget {Kind=WidgetKind.Dpi,X=0,Y=top,Width=width,Height=Math.Max(1,height-top),FontSize=font,Bold=true}
            };
        }
    }

    public enum CompositionMode {Image,Widgets,ImageAndWidgets}
    public sealed class ScreenProject {
        public int Version {get;set;}
        public int Width {get;set;}
        public int Height {get;set;}
        public string CultureName {get;set;}
        // Absolute paths may exist only while editing a new/imported project.
        // Save copies the source into portable assets and serializes a relative path.
        public string ImageFile {get;set;}
        public ImageOptions ImageOptions {get;set;}
        public CompositionMode Composition {get;set;}
        public List<ScreenWidget> Widgets {get;set;}
        [ScriptIgnore] public string DirectoryPath {get;private set;}
        public ScreenProject() {
            Version=1;Width=120;Height=28;CultureName="ru-RU";ImageOptions=new ImageOptions();Composition=CompositionMode.Widgets;
            Widgets=WidgetRenderer.CreateDefault(Width,Height);
        }
        public void Validate() {
            if(Version!=1)throw new InvalidDataException("Unsupported screen project version");MonoFrame.ValidateGeometry(Width,Height);
            if(ImageOptions==null||Widgets==null||Widgets.Count>128||!Enum.IsDefined(typeof(CompositionMode),Composition))throw new InvalidDataException("Invalid screen project settings");
            ImageOptions.Validate();foreach(ScreenWidget widget in Widgets){if(widget==null)throw new InvalidDataException("Null widget");widget.Validate();}
            CultureInfo.GetCultureInfo(String.IsNullOrEmpty(CultureName)?"ru-RU":CultureName);
        }
        static string RelativePath(string directory,string relative) {
            if(Path.IsPathRooted(relative)||relative.IndexOf(':')>=0)throw new InvalidDataException("Project image path must be relative");
            string[] parts=relative.Replace('\\','/').Split('/');foreach(string p in parts)if(p=="..")throw new InvalidDataException("Project image path cannot leave its directory");
            return Path.GetFullPath(Path.Combine(directory,relative));
        }
        public string ResolveImagePath() {
            if(String.IsNullOrEmpty(ImageFile))return null;
            return Path.IsPathRooted(ImageFile)?Path.GetFullPath(ImageFile):RelativePath(DirectoryPath??Environment.CurrentDirectory,ImageFile);
        }
        static string Digest(string file) {
            using(var sha=SHA256.Create())using(var stream=File.OpenRead(file))return BitConverter.ToString(sha.ComputeHash(stream)).Replace("-","").ToLowerInvariant();
        }
        public void Save(string file) {
            if(String.IsNullOrEmpty(file))throw new ArgumentException("Project filename required","file");Validate();
            string destination=Path.GetFullPath(file),directory=Path.GetDirectoryName(destination);Directory.CreateDirectory(directory);
            string previousImage=ImageFile;bool written=false;
            try {
                if(!String.IsNullOrEmpty(ImageFile)) {
                    string source=Path.IsPathRooted(ImageFile)?Path.GetFullPath(ImageFile):RelativePath(DirectoryPath??directory,ImageFile);
                    if(!File.Exists(source))throw new FileNotFoundException("Project image was not found",source);
                    string extension=Path.GetExtension(source).ToLowerInvariant();
                    if(extension!=".png"&&extension!=".jpg"&&extension!=".jpeg"&&extension!=".bmp"&&extension!=".gif"&&extension!=".tif"&&extension!=".tiff")throw new InvalidDataException("Unsupported project image extension");
                    string name=Digest(source)+extension;string assets=Path.Combine(directory,"assets");Directory.CreateDirectory(assets);
                    string copied=Path.Combine(assets,name);if(!File.Exists(copied))File.Copy(source,copied);
                    ImageFile="assets/"+name;
                }
                string text=new JavaScriptSerializer().Serialize(this);
                string temporary=destination+".tmp-"+Guid.NewGuid().ToString("N");
                try {File.WriteAllText(temporary,text,new UTF8Encoding(false));if(File.Exists(destination))File.Replace(temporary,destination,null);else File.Move(temporary,destination);}
                finally {if(File.Exists(temporary))File.Delete(temporary);}
                DirectoryPath=directory;written=true;
            } finally {if(!written)ImageFile=previousImage;}
        }
        public static ScreenProject Load(string file) {
            string full=Path.GetFullPath(file);var info=new FileInfo(full);if(info.Length>1048576)throw new InvalidDataException("Screen project is too large");
            var result=new JavaScriptSerializer().Deserialize<ScreenProject>(File.ReadAllText(full));if(result==null)throw new InvalidDataException("Empty screen project");
            result.DirectoryPath=Path.GetDirectoryName(full);result.Validate();
            if(!String.IsNullOrEmpty(result.ImageFile))RelativePath(result.DirectoryPath,result.ImageFile);
            return result;
        }
        public MonoFrame Render(WidgetContext context) {
            Validate();var frame=new MonoFrame(Width,Height);frame.Fill(ImageOptions.BackgroundLit);
            if(Composition!=CompositionMode.Widgets&&!String.IsNullOrEmpty(ImageFile)) {
                using(var image=new Bitmap(ResolveImagePath()))frame=ImageConverter.Convert(image,Width,Height,ImageOptions);
            }
            if(Composition!=CompositionMode.Image) {
                context=context??new WidgetContext();
                var configured=new WidgetContext {Now=context.Now,BatteryPercent=context.BatteryPercent,Dpi=context.Dpi,
                    CpuPercent=context.CpuPercent,RamPercent=context.RamPercent,GpuPercent=context.GpuPercent,
                    CpuTemperatureC=context.CpuTemperatureC,GpuTemperatureC=context.GpuTemperatureC,CultureName=CultureName};
                frame=WidgetRenderer.Render(frame,Widgets,configured);
            }
            return frame;
        }
    }
}
