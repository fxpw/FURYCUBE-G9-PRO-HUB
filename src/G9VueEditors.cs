using System;
using System.IO;
using System.Windows.Forms;
using System.Threading.Tasks;
using Furycube.G9.Macros;
using Furycube.G9.Screen;

internal sealed partial class G9ScreenEditor {
    internal async Task VueUpload(bool enableLive=false) {
        if(uploading)throw new InvalidOperationException("Кадр уже передаётся.");
        if(!transportAvailable||sendFrame==null)throw new InvalidOperationException("Подключите и прочитайте мышь.");
        Render();if(current==null||project.Width!=120||project.Height!=28)throw new InvalidDataException("Нужен корректный макет 120 × 28.");
        int epoch=transportEpoch;
        if(enableLive)await ResumeLive();else await UploadCurrent();
        if(epoch!=transportEpoch)throw new OperationCanceledException("Подключение OLED изменилось.");
        if(lastUploadError!=null)throw new InvalidOperationException(lastUploadError.Message,lastUploadError);
        if(enableLive&&!IsLive)throw new InvalidOperationException("Автообновление OLED остановлено.");
    }
    internal object VueState() {return new {project=G9WebApi.Json().DeserializeObject(CaptureProjectJson()),pngBase64=PreviewPngDataUrl(),status=oledNote,live=IsLive,widgetNames=Names};}
    internal void VueDialog(string action,bool english=false) {
        Func<string,string,string> label=(ru,en)=>english?en:ru;
        switch(action) {
        case "screen.chooseImage":
            using(var dialog=new OpenFileDialog{Title=label("Выбрать картинку для OLED","Choose an OLED image"),Filter=label("Картинки","Image files")+"|*.png;*.jpg;*.jpeg;*.bmp;*.gif;*.tif;*.tiff"})if(dialog.ShowDialog(FindForm())==DialogResult.OK){project.ImageFile=dialog.FileName;if(project.Composition==CompositionMode.Widgets)project.Composition=project.Widgets.Count>0?CompositionMode.ImageAndWidgets:CompositionMode.Image;SyncControls(null,true);Changed();}break;
        case "screen.open":
            using(var dialog=new OpenFileDialog{Title=label("Открыть макет OLED","Open an OLED layout"),Filter=label("Проект экрана","OLED layout")+"|*.g9screen.json;*.json"})if(dialog.ShowDialog(FindForm())==DialogResult.OK){project=ScreenProject.Load(dialog.FileName);syncing=true;try{preset.SelectedIndex=-1;}finally{syncing=false;}SyncControls(null,false);Changed();}break;
        case "screen.save":
            using(var dialog=new SaveFileDialog{Title=label("Сохранить макет OLED","Save the OLED layout"),Filter=label("Проект экрана","OLED layout")+"|*.g9screen.json",FileName="screen.g9screen.json"})if(dialog.ShowDialog(FindForm())==DialogResult.OK){ScreenWidget selected=SelectedWidget();bool image=ImageSelected();project.Save(dialog.FileName);SyncControls(selected,image);dirty=true;SaveAutosave();}break;
        case "screen.exportPng":
            Render();if(current==null)return;
            using(var dialog=new SaveFileDialog{Title=label("Экспорт кадра OLED","Export the OLED frame"),Filter="PNG|*.png",FileName="oled.png"})if(dialog.ShowDialog(FindForm())==DialogResult.OK)using(var image=current.ToBitmap())image.Save(dialog.FileName,System.Drawing.Imaging.ImageFormat.Png);break;
        default:throw new InvalidOperationException(label("Неизвестная операция экрана.","Unknown screen operation."));
        }
    }
}
namespace Furycube.G9.Macros {
    public sealed partial class G9MacroEditor {
        internal object[] VueMacros() {return (object[])((System.Collections.Generic.Dictionary<string,object>)G9WebApi.Json().DeserializeObject(MacroProjectCodec.ToJson(Library)))["macros"];}
        internal void VueAccept(string json) {
            var value=MacroProjectCodec.FromJson(json);
            if(Library.Macros.Count>0) {
                string history=Path.Combine(Path.GetDirectoryName(autosavePath),"macros-history");Directory.CreateDirectory(history);
                MacroProjectCodec.Save(Library,Path.Combine(history,DateTime.UtcNow.ToString("yyyyMMddTHHmmssfffffffZ")+"-"+Guid.NewGuid().ToString("N")+".g9macros.json"));
            }
            Library=value;RefreshNames(Library.Macros.Count>0?0:-1);Changed();SaveAutosave();
        }
    }
}
