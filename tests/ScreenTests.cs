using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using Furycube.G9.Screen;
using ConvertImage=Furycube.G9.Screen.ImageConverter;

internal static class ScreenTests {
    static int assertions;
    static void Check(bool value,string message) {assertions++;if(!value)throw new Exception(message);}
    static int Lit(MonoFrame f) {int n=0;for(int y=0;y<f.Height;y++)for(int x=0;x<f.Width;x++)if(f.GetPixel(x,y))n++;return n;}
    static Bitmap Solid(int width,int height,Color color) {var bitmap=new Bitmap(width,height);using(var g=Graphics.FromImage(bitmap))g.Clear(color);return bitmap;}
    static void Packing() {
        var frame=new MonoFrame(3,9);frame.SetPixel(0,0,true);frame.SetPixel(1,7,true);frame.SetPixel(2,8,true);
        byte[] expected={1,128,0,0,0,1};Check(frame.Buffer.Length==6,"page allocation");
        for(int i=0;i<expected.Length;i++)Check(frame.Buffer[i]==expected[i],"known page byte "+i);
        frame.SetPixel(1,7,false);Check(frame.Buffer[1]==0,"clear one pixel");
        frame.Invert();Check(frame.Buffer[0]==254&&frame.Buffer[3]==1&&frame.Buffer[5]==0,"invert active pixels only");
        for(int i=3;i<6;i++)Check((frame.Buffer[i]&254)==0,"last page padding stays zero");
        var g9=new MonoFrame(120,28);g9.Fill(true);Check(g9.Buffer.Length==480,"G9 geometry memory size");
        for(int x=0;x<120;x++)Check(g9.Buffer[360+x]==15,"28 visible rows leave last4bits zero");
        var clone=g9.Clone();clone.SetPixel(0,0,false);Check(g9.GetPixel(0,0),"clones own their buffers");
    }
    static void Preview() {
        var frame=new MonoFrame(2,2);frame.SetPixel(1,0,true);frame.SetPixel(0,1,true);
        using(var image=frame.ToBitmap(3)) {
            Check(image.Width==6&&image.Height==6,"preview scale geometry");
            for(int y=0;y<6;y++)for(int x=0;x<6;x++)Check(image.GetPixel(x,y).ToArgb()==(frame.GetPixel(x/3,y/3)?Color.White.ToArgb():Color.Black.ToArgb()),"preview uses exact nearest pixels");
        }
    }
    static void Conversion() {
        using(var source=new Bitmap(2,1)) {
            source.SetPixel(0,0,Color.FromArgb(127,127,127));source.SetPixel(1,0,Color.FromArgb(129,129,129));
            MonoFrame f=ConvertImage.Convert(source,2,1,new ImageOptions());Check(!f.GetPixel(0,0)&&f.GetPixel(1,0),"threshold separates known neighboring luminance");
            MonoFrame inverted=ConvertImage.Convert(source,2,1,new ImageOptions {Invert=true});Check(inverted.GetPixel(0,0)&&!inverted.GetPixel(1,0),"image inversion");
        }
        foreach(Color color in new[]{Color.Black,Color.White})using(var source=Solid(16,16,color)) {
            MonoFrame f=ConvertImage.Convert(source,16,16,new ImageOptions {Dither=true});Check(Lit(f)==(color==Color.White?256:0),"dither preserves black and white endpoints");
        }
        using(var source=Solid(3,3,Color.White))Check(Lit(ConvertImage.Convert(source,3,3,new ImageOptions {Threshold=255}))==9,"inclusive maximum threshold preserves pure white");
        using(var source=Solid(32,32,Color.FromArgb(128,128,128))) {
            MonoFrame f=ConvertImage.Convert(source,32,32,new ImageOptions {Dither=true});int count=Lit(f);
            Check(count>460&&count<565,"Floyd-Steinberg preserves approximately half coverage for half-gray");
            Check(Lit(ConvertImage.Convert(source,32,32,new ImageOptions()))==1024,"plain threshold half-gray is uniformly lit");
        }
        using(var transparent=new Bitmap(1,1,PixelFormat.Format32bppArgb)) {
            transparent.SetPixel(0,0,Color.FromArgb(0,255,255,255));
            Check(!ConvertImage.Convert(transparent,1,1,new ImageOptions()).GetPixel(0,0),"transparent art composites against black");
            Check(ConvertImage.Convert(transparent,1,1,new ImageOptions {BackgroundLit=true}).GetPixel(0,0),"transparent art honors lit background");
        }
    }
    static void Resize() {
        using(var source=Solid(6,2,Color.White)) {
            for(int y=0;y<2;y++){source.SetPixel(0,y,Color.Black);source.SetPixel(5,y,Color.Black);}
            MonoFrame fit=ConvertImage.Convert(source,6,6,new ImageOptions {Resize=ResizeMode.Fit});
            Check(Lit(fit)==8,"fit preserves6:2aspect with centered2rows and black margins");
            for(int y=0;y<6;y++)if(y!=2&&y!=3)for(int x=0;x<6;x++)Check(!fit.GetPixel(x,y),"fit letterbox is black");
            MonoFrame crop=ConvertImage.Convert(source,4,4,new ImageOptions {Resize=ResizeMode.Crop});
            Check(Lit(crop)==16,"crop fills square and removes outside black columns");
            MonoFrame dither=ConvertImage.Convert(source,6,6,new ImageOptions {Resize=ResizeMode.Fit,Dither=true});
            for(int x=0;x<6;x++)Check(!dither.GetPixel(x,1)&&!dither.GetPixel(x,4),"dither error does not leak into letterbox");
        }
    }
    static void Widgets() {
        var context=new WidgetContext {Now=new DateTime(2026,11,9,13,7,0),BatteryPercent=72,Dpi=1600,DpiStage=3,CpuPercent=42.4,RamPercent=63.3,GpuPercent=25,CpuTemperatureC=56.2,GpuTemperatureC=60.7};
        Check(WidgetRenderer.GetText(new ScreenWidget {Kind=WidgetKind.Clock},context)=="13:07","clock uses supplied time");
        Check(WidgetRenderer.GetText(new ScreenWidget {Kind=WidgetKind.Battery},context)=="BAT 72%","battery uses supplied value");
        Check(WidgetRenderer.GetText(new ScreenWidget {Kind=WidgetKind.Dpi},context)=="DPI 1600","DPI value");
        Check((int)WidgetKind.Dpi==3&&(int)WidgetKind.GpuTemperatureC==8&&(int)WidgetKind.DpiStage==9,"new DPI stage preserves existing JSON enum values");
        Check(WidgetRenderer.GetText(new ScreenWidget {Kind=WidgetKind.DpiStage},context)=="DPI# 3","DPI stage is labeled as a stage, not numeric sensitivity");
        Check(WidgetRenderer.GetText(new ScreenWidget {Kind=WidgetKind.DpiStage,ShowLabel=false},context)=="3","DPI stage label can be hidden");
        Check(WidgetRenderer.GetText(new ScreenWidget {Kind=WidgetKind.Dpi},new WidgetContext {DpiStage=3})=="DPI --","stage is never substituted for unavailable numeric DPI");
        foreach(int invalidStage in new[]{0,9})Check(WidgetRenderer.GetText(new ScreenWidget {Kind=WidgetKind.DpiStage},new WidgetContext {DpiStage=invalidStage})=="DPI# --","out-of-range DPI stages remain unknown");
        Check(WidgetRenderer.GetText(new ScreenWidget {Kind=WidgetKind.DpiStage},new WidgetContext())=="DPI# --","missing DPI stage remains unknown");
        Check(WidgetRenderer.CreateDefault(120,28)[2].Kind==WidgetKind.DpiStage,"default layout uses the available passive stage value");
        Check(WidgetRenderer.GetText(new ScreenWidget {Kind=WidgetKind.CpuPercent},context)=="CPU 42%","CPU preview value without sensors");
        Check(WidgetRenderer.GetText(new ScreenWidget {Kind=WidgetKind.CpuPercent},new WidgetContext())=="CPU --%","unknown CPU is not fabricated");
        Check(WidgetRenderer.GetText(new ScreenWidget {Kind=WidgetKind.RamPercent},context)=="RAM 63%","RAM preview value");
        Check(WidgetRenderer.GetText(new ScreenWidget {Kind=WidgetKind.GpuPercent},context)=="GPU 25%","GPU preview value");
        Check(WidgetRenderer.GetText(new ScreenWidget {Kind=WidgetKind.CpuTemperatureC},context)=="CPU 56°C","CPU temperature value");
        Check(WidgetRenderer.GetText(new ScreenWidget {Kind=WidgetKind.GpuTemperatureC},context)=="GPU 61°C","GPU temperature value");
        Check(WidgetRenderer.GetText(new ScreenWidget {Kind=WidgetKind.CpuTemperatureC},new WidgetContext())=="CPU —","unknown temperature is an em dash, not a fabricated reading");
        context.GpuTemperatureC=double.NaN;Check(WidgetRenderer.GetText(new ScreenWidget {Kind=WidgetKind.GpuTemperatureC},context)=="GPU —","invalid sensor values remain unknown");
        Check(WidgetRenderer.GetText(new ScreenWidget {Kind=WidgetKind.Clock,Format="d MMMM"},context)=="9 ноября","clock uses configured Russian culture");
        var widget=new ScreenWidget {Kind=WidgetKind.Text,Text="HELLO",X=5,Y=4,Width=25,Height=10,FontSize=9};
        MonoFrame rendered=WidgetRenderer.Render(new MonoFrame(40,20),new[]{widget},context);Check(Lit(rendered)>0,"text rasterizes into pixels");
        for(int y=0;y<20;y++)for(int x=0;x<40;x++)if(x<5||x>=30||y<4||y>=14)Check(!rendered.GetPixel(x,y),"widget drawing is clipped to its rectangle");
        MonoFrame defaults=WidgetRenderer.Render(new MonoFrame(120,28),WidgetRenderer.CreateDefault(120,28),context);Check(Lit(defaults)>20,"default widget composition renders");
    }
    static void PortableProject(string directory) {
        string external=Path.Combine(directory,"input.png");using(var image=Solid(6,2,Color.White))image.Save(external,ImageFormat.Png);
        string first=Path.Combine(directory,"first","screen.json");
        var project=new ScreenProject {ImageFile=external,Composition=CompositionMode.Image,Width=6,Height=2};project.Save(first);
        string json=File.ReadAllText(first);Check(!json.Contains(directory)&&!json.Contains("DirectoryPath")&&!Path.IsPathRooted(project.ImageFile),"serialized project contains no absolute source paths");
        Check(project.ImageFile.StartsWith("assets/"),"image imported into portable asset directory");
        string originalDirectory=project.DirectoryPath,originalImage=project.ImageFile;
        var serializer=new System.Web.Script.Serialization.JavaScriptSerializer();
        var snapshot=serializer.Deserialize<ScreenProject>(serializer.Serialize(project));snapshot.ImageFile=project.ResolveImagePath();
        string autosave=Path.Combine(directory,"autosave","autosave.g9screen.json");snapshot.Save(autosave);
        Check(project.DirectoryPath==originalDirectory&&project.ImageFile==originalImage,"autosave snapshot preserves the active project folder and relative image path");
        var loaded=ScreenProject.Load(first);Check(loaded.Width==6&&loaded.Height==2&&loaded.ImageOptions.Threshold==128,"project settings round trip");
        string second=Path.Combine(directory,"second","screen.json");loaded.Save(second);
        File.Delete(external);Directory.Delete(Path.GetDirectoryName(first),true);
        var moved=ScreenProject.Load(second);using(var preview=moved.Render(new WidgetContext()).ToBitmap())Check(preview.GetPixel(0,0).ToArgb()==Color.White.ToArgb(),"save-as project renders after original sources are removed");
        Check(Lit(ScreenProject.Load(autosave).Render(new WidgetContext()))==12,"autosave snapshot carries its own image after original sources are removed");
        string hostile=Path.Combine(directory,"hostile.json");File.WriteAllText(hostile,"{\"Version\":1,\"Width\":120,\"Height\":28,\"ImageFile\":\"../private.png\",\"Composition\":0,\"ImageOptions\":{},\"Widgets\":[]}");
        bool rejected=false;try {ScreenProject.Load(hostile);}catch(InvalidDataException){rejected=true;}Check(rejected,"project cannot escape its asset directory");
    }
    static void PositionedImage(string directory) {
        string file=Path.Combine(directory,"layer.png");using(var bitmap=Solid(2,2,Color.White))bitmap.Save(file,ImageFormat.Png);
        var project=new ScreenProject {Width=4,Height=3,ImageFile=file,ImageX=2,ImageY=1,ImageWidth=2,ImageHeight=2,Composition=CompositionMode.Image};
        MonoFrame frame=project.Render(new WidgetContext());Check(Lit(frame)==4,"positioned image preserves its own rectangle size");
        for(int y=0;y<3;y++)for(int x=0;x<4;x++)Check(frame.GetPixel(x,y)==(x>=2&&y>=1),"image is blitted at its independent offset");
        project.ImageX=3;project.ImageY=2;frame=project.Render(new WidgetContext());Check(Lit(frame)==1&&frame.GetPixel(3,2),"overhanging image is clipped without shrinking");
        Check(project.ImageWidth==2&&project.ImageHeight==2,"moving image does not change image dimensions");
        project.ImageX=0;project.ImageY=0;project.ImageWidth=0;project.ImageHeight=0;project.ImageOptions.Resize=ResizeMode.Stretch;Check(Lit(project.Render(new WidgetContext()))==12,"legacy zero image dimensions use the canvas size");
        var canvas=new MonoFrame(3,3);var source=new MonoFrame(2,2);source.Fill(true);canvas.Blit(source,-1,-1);Check(Lit(canvas)==1&&canvas.GetPixel(0,0),"negative blit clips at the top-left boundary");
        canvas.Blit(source,8,8);Check(Lit(canvas)==1,"entirely outside blit has no effect");
    }
    static int Main() {
        string directory=Path.Combine(Path.GetTempPath(),"G9ScreenTests-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(directory);
        try {Packing();Preview();Conversion();Resize();Widgets();PortableProject(directory);PositionedImage(directory);Console.WriteLine("PASS "+assertions+" screen checks: pixel packing,28-row padding, preview, threshold/dither, fit/crop, widgets, portable projects and positioned image clipping");return 0;}
        catch(Exception e){Console.Error.WriteLine("FAIL "+e);return 1;}
        finally {if(Directory.Exists(directory))Directory.Delete(directory,true);}
    }
}
