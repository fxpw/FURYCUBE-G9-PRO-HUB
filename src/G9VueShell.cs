using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows.Forms;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;
using Furycube.G9.Bindings;
using Furycube.G9.Macros;

internal sealed partial class G9Hub {
    WebView2 vue;
    string vueFolder;
    System.Windows.Forms.Timer vueTimer;
    bool vueBusy;
    string vueFirmware;
    int vueFirmwareGeneration=-1;
    int vueFirmwareAttempt=-1;
    bool vueVersionReading;
    bool vueEnglish;
    string VueLabel(string ru,string en){return vueEnglish?en:ru;}
    void VueLanguage(Dictionary<string,object> payload){
        object value;if(!payload.TryGetValue("locale",out value))return;
        if(!(value is string)||((string)value!="ru"&&(string)value!="en"))throw new InvalidDataException("Unsupported UI language");
        vueEnglish=(string)value=="en";
        if(tray.ContextMenuStrip!=null&&tray.ContextMenuStrip.Items.Count>=2){tray.ContextMenuStrip.Items[0].Text=VueLabel("Открыть HUB","Open HUB");tray.ContextMenuStrip.Items[1].Text=VueLabel("Выход","Exit");}
    }
    G9SettingsPreset vueProfileDraft;
    readonly G9ProfileStore vueProfiles=new G9ProfileStore(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"FURYCUBE-G9-PRO","profiles"));
    internal void EnableVue() {
        string folder=Path.Combine(root,"frontend","dist");
        if(!File.Exists(Path.Combine(folder,"index.html")))throw new FileNotFoundException("Не собран Vue-интерфейс. Запустите tools\\build-vue-hub.cmd.");
        vueFolder=folder;
        Padding=new Padding(0);MinimumSize=new Size(1040,720);Width=1280;Height=860;
        Disposed+=delegate{if(vueTimer!=null){vueTimer.Stop();vueTimer.Dispose();}if(vue!=null)vue.Dispose();};
    }
    partial void ShowVue() {
        if(vueFolder!=null && vue==null && Visible) InitializeVue();
    }
    async void InitializeVue() {
        Panel cover=null;
        try {
            vue=new WebView2 {Dock=DockStyle.Fill,DefaultBackgroundColor=Color.FromArgb(18,20,25),CreationProperties=new CoreWebView2CreationProperties{UserDataFolder=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"FURYCUBE-G9-PRO","webview")}};
            // Background controllers run independently of WebView2, including
            // when the first show is deferred by a minimized startup.
            cover=new Panel{Dock=DockStyle.Fill,BackColor=Color.FromArgb(18,20,25)};
            cover.Controls.Add(vue);Controls.Add(cover);cover.BringToFront();
            await vue.EnsureCoreWebView2Async();
            if(closing || IsDisposed)return;
            var core=vue.CoreWebView2;
            core.Settings.AreDefaultContextMenusEnabled=false;core.Settings.AreDevToolsEnabled=Environment.GetEnvironmentVariable("G9_HUB_DEVTOOLS")=="1";
            core.Settings.IsStatusBarEnabled=false;
            core.SetVirtualHostNameToFolderMapping("g9.local",vueFolder,CoreWebView2HostResourceAccessKind.DenyCors);
            core.NavigationStarting+=delegate(object sender,CoreWebView2NavigationStartingEventArgs e){if(!G9WebApi.TrustedOrigin(e.Uri))e.Cancel=true;};
            core.NewWindowRequested+=delegate(object sender,CoreWebView2NewWindowRequestedEventArgs e){e.Handled=true;};
            core.PermissionRequested+=delegate(object sender,CoreWebView2PermissionRequestedEventArgs e){e.State=CoreWebView2PermissionState.Deny;};
            core.WebMessageReceived+=VueMessage;
            core.Navigate("https://g9.local/index.html");
            vueTimer=new System.Windows.Forms.Timer{Interval=1000};vueTimer.Tick+=async delegate{
                if(closing||vueBusy)return;
                if(startupScanComplete&&latestDeviceRead!=null&&!vueVersionReading&&vueFirmwareAttempt!=deviceGeneration){try{await VueReadFirmware();}catch(Exception error){Append("Версия прошивки: "+error.Message);}}
                if(!closing&&!vueBusy)VueEvent();
            };vueTimer.Start();
            foreach(Control legacy in Controls)if(legacy!=cover)legacy.Visible=false;
        }catch(Exception error){
            if(closing || IsDisposed)return;
            if(vueTimer!=null){vueTimer.Dispose();vueTimer=null;}
            if(cover!=null)cover.Dispose();else if(vue!=null)vue.Dispose();
            vue=null;
            Append("Vue-интерфейс не запущен: "+error.Message);
            if(Visible)MessageBox.Show(this,error.Message,"Интерфейс HUB",MessageBoxButtons.OK,MessageBoxIcon.Error);
        }
    }
    T FindController<T>(Control current) where T:Control {if(current is T)return (T)current;foreach(Control child in current.Controls){T result=FindController<T>(child);if(result!=null)return result;}return null;}
    G9MacroEditor VueMacros {get{var result=FindController<G9MacroEditor>(this);if(result==null)throw new InvalidOperationException("Редактор макросов недоступен.");return result;}}
    object VueState() {
        string identity=targetTracker.Current??"";bool wired=identity.StartsWith("caps vid=1d57 pid=2148 ",StringComparison.Ordinal);
        object settings=latestSettings;
        var buttons=new List<object>();
        if(latestMapping!=null)foreach(var row in latestMapping.buttons){var definition=BindingCatalog.Buttons.FirstOrDefault(x=>x.Index==row.index);var action=BindingCatalog.Resolve((byte)row.function,(byte)row.param1,(byte)row.param2);buttons.Add(new{index=row.index,function=row.function,param1=row.param1,param2=row.param2,name=definition==null?"":definition.Name,physicalLabel=definition==null?"":definition.PhysicalLabel,visible=definition!=null&&definition.IsVisible,bindingId=action==null?null:action.Id});}
        return new {
            app=new{autostart=autoStart.Checked,resumeOled=resumeOled.Checked,busy=vueBusy||!startupScanComplete,status=startupStatus.Text},
            device=new{connected=!String.IsNullOrEmpty(identity),connection=String.IsNullOrEmpty(identity)?"none":wired?"usb":"radio",name="FURYCUBE G9 PRO",firmware=vueFirmwareGeneration==deviceGeneration?vueFirmware:null,profile=latestMapping==null?(int?)null:latestMapping.profile,settingsAvailable=latestSettings!=null,error=latestScanFailure,lastReadAt=latestSettings==null?null:latestSettings.readAtUtc},
            mapping=latestMapping==null?null:new{profile=latestMapping.profile,keymapHex=latestMapping.keymapHex,buttons=buttons},settings=settings,
            bindingActions=BindingCatalog.Actions.Select(x=>new{id=x.Id,name=x.Name,category=x.Category,function=x.Function,defaultParam1=x.DefaultParam1,defaultParam2=x.DefaultParam2,parameterKind=x.ParameterKind.ToString()}).ToArray(),
            keyboardKeys=BindingCatalog.KeyboardKeys.Select(x=>new{name=x.Name,usage=x.Usage}).ToArray(),
            screen=screenEditor.VueState(),macros=VueMacros.VueMacros(),
            profiles=vueProfiles.ListEntries().Select(x=>new{id=x.Id,name=x.Name,readable=x.IsReadable}).ToArray(),
            profileDraft=vueProfileDraft,logs=log.Lines.Skip(Math.Max(0,log.Lines.Length-80)).ToArray()
        };
    }
    void VuePost(object value) {if(!closing&&vue!=null&&vue.CoreWebView2!=null)vue.CoreWebView2.PostWebMessageAsJson(G9WebApi.Json().Serialize(value));}
    void VueEvent() {try{VuePost(new{ @event="state",data=VueState()});}catch(Exception error){Append("Состояние Vue: "+error.Message);}}
    async void VueMessage(object sender,CoreWebView2WebMessageReceivedEventArgs e) {
        G9WebApi.Request request=null;bool entered=false;
        try {
            request=G9WebApi.Parse(e.Source,e.WebMessageAsJson);
            VueLanguage(request.Payload);
            if(request.Method=="app.state"){VuePost(new{id=request.Id,ok=true,data=VueState()});return;}
            if(vueBusy)throw new InvalidOperationException("Дождитесь завершения текущей операции.");
            vueBusy=true;entered=true;
            object result=await VueDispatch(request.Method,request.Payload);
            vueBusy=false;
            if(request.Method!="screen.update"&&request.Method!="app.exit")result=VueState();
            VuePost(new{id=request.Id,ok=true,data=result});
        }catch(Exception error){if(request!=null)VuePost(new{id=request.Id,ok=false,error=error.Message});Append("Vue: "+error.Message);}
        finally{if(entered)vueBusy=false;}
    }
    async Task<string> VueProtocol(string command,int generation) {
        return await Task.Run(delegate{lock(deviceGate){if(closing||generation!=deviceGeneration)throw new OperationCanceledException("Подключение изменилось; перечитайте мышь.");return RunDeviceProcess(command);}});
    }
    async Task<bool> VueScan() {
        if(monitorBusy||!startupScanComplete)throw new InvalidOperationException("Поиск мыши уже выполняется.");
        monitorBusy=true;
        try {
            screenEditor.StopLive();System.Threading.Interlocked.Increment(ref deviceGeneration);
            latestDeviceRead=null;vueProfileDraft=null;resumeReady=false;resumeAfterChange=false;
            screenEditor.SuspendDevice("Читаю подключённую мышь…");buttonEditor.SuspendDevice();settingsEditor.SuspendDevice();
            var previous=System.Threading.Interlocked.Exchange(ref oledWorker,null);if(previous!=null)await Task.Run(delegate{previous.Dispose();});
            return await ScanDevice(null,true);
        } finally {monitorBusy=false;}
    }
    async Task VueReadFirmware() {
        if(latestDeviceRead==null)return;int generation=deviceGeneration;vueFirmwareAttempt=generation;
        vueVersionReading=true;
        try{
            string suffix=G9Startup.DeviceSuffix(selectedIdentity);
            string output=await VueProtocol("firmware"+suffix,generation);
            if(generation!=deviceGeneration)throw new OperationCanceledException("Подключение изменилось.");
            string line=output.Split(new[]{'\r','\n'},StringSplitOptions.RemoveEmptyEntries).FirstOrDefault(x=>x.StartsWith("FIRMWARE_RESPONSE ",StringComparison.Ordinal));
            if(line==null)throw new InvalidDataException("Не получена версия прошивки.");
            byte[] data=G9SettingsSnapshot.Hex(line.Substring(18),8);string marker=System.Text.Encoding.ASCII.GetString(data,5,3);
            vueFirmware=data[3]+"."+data[4].ToString("D2")+(marker.StartsWith("OL",StringComparison.Ordinal)?" / "+marker:" / штатная");vueFirmwareGeneration=generation;
        }finally{vueVersionReading=false;}
    }
    async Task<object> VueDispatch(string method,Dictionary<string,object> payload) {
        if(method=="app.exit"){exitRequested=true;BeginInvoke(new Action(Close));return null;}
        if(method=="app.preferences"){
            if(payload.ContainsKey("autostart")){bool value=G9WebApi.Flag(payload,"autostart");autoStart.Checked=value;if(autoStart.Checked!=value)throw new InvalidOperationException("Не удалось изменить автозапуск.");}
            if(payload.ContainsKey("resumeOled")){bool value=G9WebApi.Flag(payload,"resumeOled");resumeOled.Checked=value;if(resumeOled.Checked!=value)throw new InvalidOperationException("Не удалось сохранить фоновый режим.");}
            return VueState();
        }
        if(method=="device.scan"){
            if(await VueScan()){
                await VueReadFirmware();
            }
            return VueState();
        }
        if(method=="device.assign"||method=="device.settings"){
            if(latestDeviceRead==null||latestMapping==null)throw new InvalidOperationException("Сначала прочитайте подключённую мышь.");
            int generation=deviceGeneration,profile=G9WebApi.Number(payload,"profile",1,5);if(profile!=latestMapping.profile)throw new InvalidOperationException("Активный профиль изменился; перечитайте мышь.");
            string suffix=G9WebApi.DeviceSuffix(latestDeviceRead.Wired,targetTracker.Current);screenEditor.StopLive();
            try{
                if(method=="device.assign")await VueProtocol(G9WebApi.Assign(payload)+suffix,generation);
                else{
                    if(latestSettings==null)throw new InvalidOperationException("Сначала прочитайте настройки подключённой мыши.");
                    object changesValue;object[] changes;bool transferKeys=payload.ContainsKey("includeButtons")&&G9WebApi.Flag(payload,"includeButtons");if(!payload.TryGetValue("changes",out changesValue)||(changes=changesValue as object[])==null||changes.Length>40||(changes.Length<1&&!transferKeys))throw new InvalidDataException("Выберите изменённые настройки.");
                    var draft=new Dictionary<string,string>(StringComparer.Ordinal);
                    foreach(object item in changes){var row=item as Dictionary<string,object>;if(row==null)throw new InvalidDataException("Неверный параметр.");string id=G9WebApi.Text(row,"id",80),value=G9WebApi.Text(row,"value",100);if(draft.ContainsKey(id))throw new InvalidDataException("Повторный параметр: "+id);draft.Add(id,value);}
                    var edits=G9MouseSettingsEditor.PrepareEdits(latestSettings,draft);
                    byte[] keys=null;
                    if(payload.ContainsKey("includeButtons")&&G9WebApi.Flag(payload,"includeButtons")){
                        if(vueProfileDraft==null||String.IsNullOrEmpty(vueProfileDraft.keymapHex))throw new InvalidDataException("В открытом профиле нет назначений кнопок.");
                        if(draft.ContainsKey("active-profile")||draft.ContainsKey("max-profiles"))throw new InvalidDataException("Перенос назначений выполняется отдельно от переключения профилей.");
                        keys=BindingCatalog.RetargetKeymap(G9SettingsSnapshot.Hex(vueProfileDraft.keymapHex,59),profile);BindingCatalog.EnsureLeftClick(keys,profile);
                    }
                    // Reuse the tested complete preflight and union-mask/stage
                    // ordering before the first native write.
                    var commands=edits.Select(x=>G9WebApi.Settings(profile,x.Key,x.Value)+suffix).ToArray();
                    foreach(string command in commands)await VueProtocol(command,generation);
                    if(keys!=null){string directory=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"FURYCUBE-G9-PRO","backups");Directory.CreateDirectory(directory);string file=Path.Combine(directory,"vue-profile-keymap-"+Guid.NewGuid().ToString("N")+".bin");File.WriteAllBytes(file,keys);await VueProtocol("restore "+G9WebApi.Quote(file)+" --profile "+profile+suffix,generation);}
                }
                vueProfileDraft=null;if(!await VueScan())throw new InvalidOperationException("Команда завершилась, но итоговое чтение не подтверждено. Подробности в журнале.");
            }catch{latestDeviceRead=null;latestSettings=null;latestMapping=null;vueProfileDraft=null;throw;}
            return VueState();
        }
        if(method=="screen.update") {object value;if(!payload.TryGetValue("project",out value))throw new InvalidDataException("Нет макета.");screenEditor.AcceptProjectJson(G9WebApi.Json().Serialize(value));return screenEditor.VueState();}
        if(method=="screen.upload"){await screenEditor.VueUpload();return VueState();}
        if(method=="screen.live"){if(G9WebApi.Flag(payload,"enabled"))await screenEditor.VueUpload(true);else screenEditor.StopLive();return VueState();}
        if(method=="screen.schedule"){
            screenEditor.SetLiveSchedule(G9WebApi.Number(payload,"intervalSeconds",1,86400),G9WebApi.Flag(payload,"waitForIdle"));
            return VueState();
        }
        if(method.StartsWith("screen.",StringComparison.Ordinal)){screenEditor.VueDialog(method,vueEnglish);return VueState();}
        if(method.StartsWith("macros.",StringComparison.Ordinal)){VueMacroOperation(method,payload);return VueState();}
        if(method.StartsWith("profiles.",StringComparison.Ordinal)){VueProfileOperation(method,payload);return VueState();}
        throw new InvalidDataException("Неизвестная операция.");
    }
    void VueMacroOperation(string method,Dictionary<string,object> payload) {
        var editor=VueMacros;var rows=new List<object>(editor.VueMacros());
        if(method=="macros.import") {using(var dialog=new OpenFileDialog{Title=VueLabel("Открыть макросы","Import macros"),Filter=VueLabel("Макросы G9","G9 macros")+"|*.g9macros.json;*.json"})if(dialog.ShowDialog(this)==DialogResult.OK)editor.VueAccept(MacroProjectCodec.ToJson(MacroProjectCodec.Load(dialog.FileName)));return;}
        if(method=="macros.export") {string id=G9WebApi.Text(payload,"id",20);var one=rows.Cast<Dictionary<string,object>>().FirstOrDefault(x=>(string)x["name"]==id);if(one==null)throw new InvalidDataException("Макрос не найден.");var library=MacroProjectCodec.FromJson(G9WebApi.Json().Serialize(new{format="FURYCUBE-G9-MACROS",version=1,macros=new[]{one}}));using(var dialog=new SaveFileDialog{Title=VueLabel("Сохранить макрос","Export a macro"),Filter=VueLabel("Макросы G9","G9 macros")+"|*.g9macros.json",FileName=id+".g9macros.json"})if(dialog.ShowDialog(this)==DialogResult.OK)MacroProjectCodec.Save(library,dialog.FileName);return;}
        string original;
        if(method=="macros.save") {object document;if(!payload.TryGetValue("document",out document)||!(document is Dictionary<string,object>))throw new InvalidDataException("Нет макроса.");original=payload.ContainsKey("originalName")?G9WebApi.Text(payload,"originalName",20):G9WebApi.Text((Dictionary<string,object>)document,"name",20);rows.RemoveAll(x=>String.Equals((string)((Dictionary<string,object>)x)["name"],original,StringComparison.OrdinalIgnoreCase));rows.Add(document);}
        else if(method=="macros.delete"){original=G9WebApi.Text(payload,"id",20);if(rows.RemoveAll(x=>String.Equals((string)((Dictionary<string,object>)x)["name"],original,StringComparison.OrdinalIgnoreCase))!=1)throw new InvalidDataException("Макрос не найден.");}
        else throw new InvalidDataException("Неизвестная операция макросов.");
        editor.VueAccept(G9WebApi.Json().Serialize(new{format="FURYCUBE-G9-MACROS",version=1,macros=rows}));
    }
    void VueProfileOperation(string method,Dictionary<string,object> payload) {
        string id=payload.ContainsKey("id")?G9WebApi.Text(payload,"id",32):null;
        if(method=="profiles.save"){
            if(latestSettings==null||latestMapping==null)throw new InvalidOperationException("Сначала прочитайте настройки подключённой мыши.");
            var values=new Dictionary<string,string>();foreach(var field in latestSettings.fields)if(field.canWrite&&field.id!="active-profile"&&field.id!="max-profiles")values.Add(field.id,field.value);
            if(vueProfileDraft!=null)foreach(var pair in vueProfileDraft.values)if(values.ContainsKey(pair.Key))values[pair.Key]=pair.Value;
            object supplied;if(payload.TryGetValue("values",out supplied)){var rows=supplied as Dictionary<string,object>;if(rows==null)throw new InvalidDataException("Неверные настройки профиля.");foreach(var pair in rows){if(!values.ContainsKey(pair.Key)||!(pair.Value is string))throw new InvalidDataException("Недоступный параметр профиля.");values[pair.Key]=(string)pair.Value;}}
            G9MouseSettingsEditor.PrepareEdits(latestSettings,values.Where(x=>latestSettings.fields.First(f=>f.id==x.Key).value!=x.Value).ToDictionary(x=>x.Key,x=>x.Value,StringComparer.Ordinal));
            vueProfiles.SaveNew(new G9SettingsPreset{schemaVersion=1,values=values,keymapHex=latestMapping.keymapHex},G9WebApi.Text(payload,"name",100));
        }else if(method=="profiles.open"){
            if(latestSettings==null)throw new InvalidOperationException("Сначала прочитайте настройки подключённой мыши.");var draft=vueProfiles.Read(id);
            foreach(var pair in draft.values)if(!latestSettings.fields.Any(f=>f.id==pair.Key&&f.canWrite))throw new InvalidDataException("Недоступный параметр профиля: "+pair.Key);
            G9MouseSettingsEditor.PrepareEdits(latestSettings,draft.values.Where(x=>latestSettings.fields.First(f=>f.id==x.Key).value!=x.Value).ToDictionary(x=>x.Key,x=>x.Value,StringComparer.Ordinal));vueProfileDraft=draft;
        }
        else if(method=="profiles.rename")vueProfiles.Rename(id,G9WebApi.Text(payload,"name",100));
        else if(method=="profiles.delete")vueProfiles.Archive(id);
        else if(method=="profiles.import"){using(var dialog=new OpenFileDialog{Title=VueLabel("Открыть профиль","Import a profile"),Filter=VueLabel("Профили G9","G9 profiles")+"|*.g9settings.json;*.json"})if(dialog.ShowDialog(this)==DialogResult.OK)vueProfiles.ImportFile(dialog.FileName,null);}
        else if(method=="profiles.export"){using(var dialog=new SaveFileDialog{Title=VueLabel("Сохранить профиль","Export a profile"),Filter=VueLabel("Профиль G9","G9 profile")+"|*.g9settings.json",FileName="profile.g9settings.json"})if(dialog.ShowDialog(this)==DialogResult.OK)vueProfiles.ExportFile(id,dialog.FileName);}
        else throw new InvalidDataException("Неизвестная операция профилей.");
    }
    internal static int LegacyArguments(string[] args) {return Main(args);}
}

internal static class G9VueHubProgram {
    [STAThread] static int Main(string[] args) {
        if(args.Length>0&&args[0].StartsWith("--prepare",StringComparison.Ordinal))return G9Hub.LegacyArguments(args);
        bool minimized;if(!G9Startup.TryArguments(args,out minimized))return 2;
        using(var instance=new G9HubInstance()) {
            if(!instance.IsOwner){if(!minimized)instance.RequestShow();return 0;}
            Application.EnableVisualStyles();Application.SetCompatibleTextRenderingDefault(false);
            string root=Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory,".."));
            try {using(var hub=new G9Hub(root,minimized,instance.ConsumeShowRequest)){hub.EnableVue();Application.Run(hub);}return 0;}
            catch(Exception error){MessageBox.Show(error.Message,"FURYCUBE G9 PRO HUB",MessageBoxButtons.OK,MessageBoxIcon.Error);return 1;}
        }
    }
}
