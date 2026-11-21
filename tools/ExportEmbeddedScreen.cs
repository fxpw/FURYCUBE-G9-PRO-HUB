using System;
using System.IO;
using Furycube.G9.Screen;

internal static class ExportEmbeddedScreen {
    static int Main(string[] args) {
        try {
            if(args.Length!=2)throw new ArgumentException("Usage: ExportEmbeddedScreen <saved layout> <new snapshot file>");
            ScreenProject layout=ScreenProject.Load(args[0]);
            if(layout.Width!=120||layout.Height!=28||layout.Composition==CompositionMode.Image)
                throw new InvalidDataException("The embedded screen needs a 120 x 28 layout with a battery indicator.");
            bool hasGauge=false;
            foreach(ScreenWidget widget in layout.Widgets) {
                if(widget.Kind==WidgetKind.BatteryIndicator)hasGauge=true;
                else if(widget.Kind!=WidgetKind.Text)
                    throw new InvalidDataException("The embedded snapshot supports static text, an image and battery gauges.");
            }
            if(!hasGauge)throw new InvalidDataException("The layout has no battery gauge.");
            var frames=new byte[11*480];
            for(int level=0;level<=10;level++) {
                MonoFrame frame=layout.Render(new WidgetContext {BatteryPercent=level*10});
                Buffer.BlockCopy(frame.Buffer,0,frames,level*480,480);
            }
            using(var file=new FileStream(args[1],FileMode.CreateNew,FileAccess.Write))
                file.Write(frames,0,frames.Length);
            return 0;
        } catch(Exception error) {
            Console.Error.WriteLine(error.Message);
            return 1;
        }
    }
}
