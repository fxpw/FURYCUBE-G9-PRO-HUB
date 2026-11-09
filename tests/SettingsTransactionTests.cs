using System;
using System.Collections.Generic;
using System.IO;
using System.Web.Script.Serialization;
using Furycube.G9.Settings;

// Synthetic transport only. No G9Connection, HID enumeration or native API call.
internal static class SettingsTransactionTests {
    static int count;
    static readonly string Root=Path.Combine(Path.GetTempPath(),"G9-settings-transactions-"+Guid.NewGuid().ToString("N"));
    static void Check(bool value,string name) { if(!value) throw new Exception("FAIL "+name); count++; }
    static void Reject(Action action,string name) {
        try {action();} catch(ArgumentException){count++;return;} catch(FormatException){count++;return;}
        catch(InvalidOperationException){count++;return;} catch(NotSupportedException){count++;return;} catch(InvalidDataException){count++;return;}
        catch(IOException){count++;return;}
        throw new Exception("FAIL accepted "+name);
    }
    static byte[] Hex(string text) { var bytes=new byte[text.Length/2]; for(int i=0;i<bytes.Length;i++) bytes[i]=Convert.ToByte(text.Substring(i*2,2),16); return bytes; }
    static int Sum(byte[] value,int offset,int length) {int sum=0;for(int i=offset;i<offset+length;i++)sum+=value[i];return sum&65535;}
    static byte[] Fixture(int profile) {
        byte[] sensor=Hex("04380101013f0000204080c040e0000003060c121f2e000002ff000000ff000000ffffff0000ffffff00ffff4000ffffff0110ab00000000");
        byte[] light=Hex("050f010003a800ff00140401c20000");
        var all=new byte[120];all[0]=10;all[1]=120;all[2]=(byte)profile;all[3]=0x44;all[4]=0xBB;
        Buffer.BlockCopy(sensor,3,all,5,47);all[8]=0xA5;all[9]=0x5A;
        all[52]=0xA7;int sum=Sum(all,5,47);all[53]=(byte)sum;all[54]=(byte)(sum>>8);
        Buffer.BlockCopy(light,3,all,55,8);all[63]=light[12];all[64]=light[11];
        for(int i=65;i<119;i++)all[i]=(byte)(i*3+profile);all[119]=(byte)Sum(all,65,54);return all;
    }
    sealed class Fake : IG9SettingsSession {
        internal int Active=2,Maximum=5,Queries,ProfileQueries,DriftOnProfileQuery;
        internal bool DropWrite,ShortAllData,CorruptSensor,VerifyBackup;
        internal ushort Pid=0x2148;
        internal string PathId="fake-g9-wired-device";
        internal Func<int,byte[]> ProfileReply;
        internal Action BeforeSet;
        internal readonly List<byte[]> Writes=new List<byte[]>();
        internal readonly Dictionary<int,byte[]> Profiles=new Dictionary<int,byte[]>();
        internal readonly List<string> Calls=new List<string>();
        public ushort SettingsProductId {get{return Pid;}}
        public string SettingsDevicePath {get{return PathId;}}
        internal Fake(){for(int p=1;p<=5;p++)Profiles.Add(p,Fixture(p));}
        public byte[] QuerySettings(byte report,int profile) {
            Queries++;Calls.Add(report.ToString("x2")+":"+profile);
            if(report==12){ProfileQueries++;if(ProfileQueries==DriftOnProfileQuery)Active=3;
                if(ProfileReply!=null)return ProfileReply(ProfileQueries);
                return new byte[]{12,8,1,(byte)Active,(byte)(Active^255),(byte)Maximum,(byte)(Maximum^255),0};}
            byte[] all=Profiles[profile];var model=MouseSettingsCodec.DecodeAllData(all,profile);
            if(report==10)return ShortAllData?new byte[8]:(byte[])all.Clone();
            if(report==4){byte[] full=MouseSettingsCodec.BuildSetPacket(model,4);var raw=new byte[52];Buffer.BlockCopy(full,0,raw,0,52);raw[1]=52;if(CorruptSensor)raw[51]^=1;return raw;}
            if(report==6)return MouseSettingsCodec.BuildSetPacket(model,6);
            if(report==7)return new byte[]{7,8,(byte)profile,2,253,(byte)(Active-1),(byte)((Active-1)^255),0};
            if(report==8){var raw=new byte[59];raw[0]=8;raw[1]=59;raw[2]=(byte)profile;Buffer.BlockCopy(all,65,raw,3,54);int sum=Sum(raw,3,54);raw[57]=(byte)(sum>>8);raw[58]=(byte)sum;return raw;}
            throw new Exception("Unexpected query "+report.ToString("x2"));
        }
        public void SendSettings(byte[] packet) {
            G9Wire.ValidateSend(packet);
            if(BeforeSet!=null)BeforeSet();
            if(VerifyBackup){string[] saved=Directory.GetFiles(Root,"snapshot.json",SearchOption.AllDirectories);Check(saved.Length>0,"durable snapshot exists before send");
                string body=Path.Combine(Path.GetDirectoryName(saved[saved.Length-1]),"logical-profile-117.bin");Check(File.Exists(body)&&new FileInfo(body).Length==117,"full117 backup exists before send");}
            Writes.Add((byte[])packet.Clone());if(DropWrite)return;
            if(packet[0]==12){byte[] state=G9Wire.DecodeProfileSet(packet);Active=state[0];Maximum=state[1];return;}
            byte[] all=Profiles[packet[2]];
            if(packet[0]==4){Buffer.BlockCopy(packet,3,all,5,47);all[53]=packet[51];all[54]=packet[50];}
            else if(packet[0]==5){Buffer.BlockCopy(packet,3,all,55,8);all[63]=packet[12];all[64]=packet[11];}
            else if(packet[0]==6)Buffer.BlockCopy(packet,3,all,3,2);
            else throw new Exception("Unexpected settings writer");
        }
    }
    static void Run() {
        Directory.CreateDirectory(Root);
        var fake=new Fake();var snapshot=G9SettingsTransactions.Read(fake,null,Root);
        Check(snapshot.ActiveProfile==2 && snapshot.MaxProfiles==5 && snapshot.Model.ProfileId==2,"actual active profile selected");
        Check(snapshot.Model.GetBodyCopy()[49]==0xA7,"alignment/unknown byte preserved");
        Check(fake.Writes.Count==0 && fake.Queries==8,"full read performs no settings SET");
        Check(snapshot.Reports["allData"].Length==120 && snapshot.Reports["keys"].Length==59,"complete logical/raw archive lengths");
        Check(File.Exists(snapshot.ArchivePath),"snapshot archive saved");
        Check(new FileInfo(Path.Combine(Path.GetDirectoryName(snapshot.ArchivePath),"logical-profile-117.bin")).Length==117,"117-byte binary archived");
        Check((string)snapshot.ToDto()["source"]=="FirmwareLogicalProfile" && !(bool)snapshot.ToDto()["isFirmwareBackup"],"provenance is not firmware backup");
        Check((int)snapshot.ToDto()["productId"]==0x2148 && (string)snapshot.ToDto()["productIdHex"]=="2148","numeric product ID matches UI contract");
        foreach(string query in fake.Calls) Check(!query.StartsWith("05:") && !query.StartsWith("10:") && !query.StartsWith("09:"),"no unsupported lighting/macro/loader query");

        string[][] edits={new[]{"dpi.value2","1800"},new[]{"dpi.stage","3"},new[]{"dpi.enabledMask","3"},
            new[]{"dpi.color8","1234AB"},new[]{"sensor.ripple","0"},new[]{"sensor.angleSnap","0"},
            new[]{"polling.hz","500"},new[]{"debounce.ms","16"},new[]{"sleep.level1Seconds","900"},new[]{"sleep.idleMinutes","20"}};
        foreach(string[] edit in edits){fake=new Fake();fake.VerifyBackup=true;byte[] other=(byte[])fake.Profiles[1].Clone();
            MouseSettingsProfile before=MouseSettingsCodec.DecodeAllData(fake.Profiles[2],2);
            byte[] expected=MouseSettingsCodec.BuildEdit(before,edit[0],edit[1]).Updated.GetBodyCopy();
            var result=G9SettingsTransactions.Apply(fake,2,edit[0],edit[1],Root);
            Check(fake.Writes.Count==1 && fake.Writes[0][0]!=12,"one dedicated record write for "+edit[0]);
            Check(fake.Active==2 && fake.Maximum==5,"profile/count preserved for "+edit[0]);
            Check(G9Wire.Equal(other,fake.Profiles[1]),"other profile untouched for "+edit[0]);
            Check(G9Wire.Equal(expected,MouseSettingsCodec.DecodeAllData(fake.Profiles[2],2).GetBodyCopy()),"exact merge/readback "+edit[0]);
            Check((bool)result["verified"] && File.Exists((string)result["backupPath"]),"apply result and backup "+edit[0]);
        }

        fake=new Fake();var switched=G9SettingsTransactions.Apply(fake,2,"active-profile","4",Root);
        Check(fake.Writes.Count==1 && fake.Writes[0][0]==12 && fake.Active==4 && fake.Maximum==5,"explicit profile switch preserves count");
        Check((int)switched["profile"]==4,"profile switch returns new-profile snapshot");
        fake=new Fake();G9SettingsTransactions.Apply(fake,2,"max-profiles","3",Root);
        Check(fake.Active==2 && fake.Maximum==3 && fake.Writes.Count==1,"explicit count edit preserves active");

        fake=new Fake();Reject(delegate{G9SettingsTransactions.Apply(fake,2,"max-profiles","1",Root);},"count cannot implicitly move active profile");Check(fake.Writes.Count==0,"invalid count writes none");
        fake=new Fake();fake.Maximum=2;Reject(delegate{G9SettingsTransactions.Apply(fake,2,"active-profile","4",Root);},"activation cannot implicitly increase count");Check(fake.Writes.Count==0,"invalid active writes none");
        fake=new Fake();Reject(delegate{G9SettingsTransactions.Apply(fake,2,"rf.power","3",Root);},"missing RF baseline fails closed");Check(fake.Writes.Count==0,"RF SET never sent");
        fake=new Fake();Reject(delegate{G9SettingsTransactions.Read(fake,3,Root);},"inactive requested profile refused");Check(fake.Queries==1 && fake.Writes.Count==0,"active mismatch stops after profile read");
        fake=new Fake();fake.Pid=0xFA60;Reject(delegate{G9SettingsTransactions.Read(fake,null,Root);},"receiver long reads unqualified");Check(fake.Queries==0,"receiver refusal before reports");
        fake=new Fake();fake.ShortAllData=true;Reject(delegate{G9SettingsTransactions.Apply(fake,2,"debounce.ms","16",Root);},"short logical body cannot authorize write");Check(fake.Writes.Count==0,"short body sends no SET");
        fake=new Fake();fake.CorruptSensor=true;Reject(delegate{G9SettingsTransactions.Apply(fake,2,"debounce.ms","16",Root);},"independent sensor validation");Check(fake.Writes.Count==0,"bad source sends no SET");
        fake=new Fake();fake.DriftOnProfileQuery=2;Reject(delegate{G9SettingsTransactions.Read(fake,2,Root);},"profile drift during read");Check(fake.Writes.Count==0,"read drift writes none");
        fake=new Fake();fake.DriftOnProfileQuery=3;Reject(delegate{G9SettingsTransactions.Apply(fake,2,"debounce.ms","16",Root);},"profile drift immediately before write");Check(fake.Writes.Count==0,"prewrite drift sends no SET");
        fake=new Fake();fake.DropWrite=true;Reject(delegate{G9SettingsTransactions.Apply(fake,2,"debounce.ms","16",Root);},"readback mismatch reported");Check(fake.Writes.Count==1,"no retry/default/reset after failed verification");

        foreach(byte id in new byte[]{4,6,7,8,10,12})for(int p=1;p<=5;p++){byte[] query=G9Wire.ReadSettingsRequest(id,p);G9Wire.ValidateSend(query);Check(query[4]==p,"bounded profile query");}
        foreach(byte[] denied in new[]{new byte[]{0xA0,0x10,8,0,1,0,0,0},new byte[]{0x10,8,0,0,0,0,0,0},new byte[]{0x0A,8,1,0,0,0,0,0},new byte[]{0xA0,0x0A,120,0,0,0,0,0}})
            Reject(delegate{G9Wire.ValidateSend(denied);},"loader/reset/unscoped query denied");
        Check(G9Wire.BuildProfileSet(2,5)[4]==5,"profile packet preserves explicit maximum");
        ProfilePairRestore();
    }
    static byte[] PairReport(int active,int maximum){return new byte[]{12,8,1,(byte)active,(byte)(active^255),(byte)maximum,(byte)(maximum^255),0};}
    static string SourceSnapshot(Action<Dictionary<string,object>> change) {
        var raw=new Dictionary<string,object>{{"profile","0c080102fd05fa00"},{"profileBefore","0c080102fd05fa00"}};
        var source=new Dictionary<string,object>{{"schemaVersion",1},{"source","FirmwareLogicalProfile"},{"devicePath","fake-g9-wired-device"},
            {"productId",8520},{"productIdHex","2148"},{"profile",2},{"activeProfile",2},{"maxProfiles",5},
            {"verified",true},{"isFirmwareBackup",false},{"macroBodiesIncluded",false},{"rawReports",raw}};
        if(change!=null)change(source);string path=Path.Combine(Root,"restore-source-"+Guid.NewGuid().ToString("N")+".json");
        File.WriteAllText(path,new JavaScriptSerializer().Serialize(source));return path;
    }
    static Fake RestoreFake(){return new Fake{Active=4,Maximum=5,ShortAllData=true};}
    static void ProfileOnly(Fake fake,string name){foreach(string call in fake.Calls)Check(call=="0c:1",name+" queries only GET0C");foreach(byte[] packet in fake.Writes)Check(packet[0]==12&&packet.Length==10,name+" writes only SET0C");}
    static void ProfilePairRestore() {
        string source=SourceSnapshot(null),pairRoot=Path.Combine(Root,"pair-success");var fake=RestoreFake();
        byte[] untouched=(byte[])fake.Profiles[4].Clone();
        fake.BeforeSet=delegate {
            string[] metadata=Directory.GetFiles(pairRoot,"profile-pair.json",SearchOption.AllDirectories);Check(metadata.Length==1,"pair archive exists before SET0C");
            string directory=Path.GetDirectoryName(metadata[0]);Check(G9Wire.Equal(File.ReadAllBytes(Path.Combine(directory,"profile-before.bin")),PairReport(4,5)),"current pair durably saved before SET");
            Check(G9Wire.Equal(File.ReadAllBytes(Path.Combine(directory,"source-profile.bin")),PairReport(2,5)),"saved source pair durably saved before SET");
            var dto=new JavaScriptSerializer().DeserializeObject(File.ReadAllText(metadata[0])) as Dictionary<string,object>;
            Check((string)dto["source"]=="HardwareProfilePairBeforeRestore"&&!(bool)dto["isFirmwareBackup"],"pair archive is not mislabeled firmware/full settings");
            var provenance=(Dictionary<string,object>)dto["restoreSource"];Check((string)provenance["snapshotPath"]==source&&((string)provenance["snapshotSha256"]).Length==64,"source path/hash provenance retained");
        };
        Dictionary<string,object> result=G9SettingsTransactions.RestoreProfilePair(fake,source,pairRoot);
        Check(fake.Writes.Count==1&&fake.Queries==3&&fake.Active==2&&fake.Maximum==5,"pair restore uses three GET0C and one SET0C despite invalid current full body");
        Check(G9Wire.Equal(fake.Writes[0],Hex("0c0a02fd05fa00000000")),"known exact profile pair SET packet");
        Check((bool)result["verified"]&&(string)result["status"]=="restored-profile-pair"&&(string)result["verificationScope"]=="profile-pair-only","success requires final pair readback");
        Check(!(bool)result["isFirmwareBackup"]&&File.Exists((string)result["archivePath"]),"verified result references pair-only durable archive");
        Check(G9Wire.Equal(untouched,fake.Profiles[4]),"pair restore never touches profile body");ProfileOnly(fake,"successful restore");
        fake=RestoreFake();string caseSource=SourceSnapshot(delegate(Dictionary<string,object> s){s["devicePath"]="FAKE-G9-WIRED-DEVICE";});
        G9SettingsTransactions.RestoreProfilePair(fake,caseSource,Path.Combine(Root,"pair-case"));Check(fake.Writes.Count==1,"device paths matched case-insensitively");

        var invalid=new List<Action<Dictionary<string,object>>>{
            delegate(Dictionary<string,object> s){s["schemaVersion"]=2;},delegate(Dictionary<string,object> s){s["schemaVersion"]="1";},
            delegate(Dictionary<string,object> s){s["source"]="HardwareProfilePairBeforeRestore";},delegate(Dictionary<string,object> s){s["isFirmwareBackup"]=true;},
            delegate(Dictionary<string,object> s){s["verified"]=false;},delegate(Dictionary<string,object> s){s["macroBodiesIncluded"]=true;},
            delegate(Dictionary<string,object> s){s["productId"]="2148";},delegate(Dictionary<string,object> s){s["productId"]=0xFA60;},
            delegate(Dictionary<string,object> s){s["productIdHex"]="fa60";},delegate(Dictionary<string,object> s){s["devicePath"]="another-g9-device";},
            delegate(Dictionary<string,object> s){s["activeProfile"]=3;},delegate(Dictionary<string,object> s){s["maxProfiles"]=4;},
            delegate(Dictionary<string,object> s){s["profile"]=1;},delegate(Dictionary<string,object> s){s.Remove("rawReports");},
            delegate(Dictionary<string,object> s){((Dictionary<string,object>)s["rawReports"])["profile"]="0c080102fc05fa00";},
            delegate(Dictionary<string,object> s){((Dictionary<string,object>)s["rawReports"])["profile"]="0c080102fd05fa01";},
            delegate(Dictionary<string,object> s){((Dictionary<string,object>)s["rawReports"])["profile"]="0c080202fd05fa00";},
            delegate(Dictionary<string,object> s){((Dictionary<string,object>)s["rawReports"])["profile"]="0c080102fd05fa0000";},
            delegate(Dictionary<string,object> s){((Dictionary<string,object>)s["rawReports"])["profile"]="0c080106f905fa00";},
            delegate(Dictionary<string,object> s){((Dictionary<string,object>)s["rawReports"])["profile"]="0c080104fb02fd00";},
            delegate(Dictionary<string,object> s){((Dictionary<string,object>)s["rawReports"])["profileBefore"]="0c080103fc05fa00";}
        };
        int test=0;foreach(var change in invalid){fake=RestoreFake();string bad=SourceSnapshot(change);Reject(delegate{G9SettingsTransactions.RestoreProfilePair(fake,bad,Root);},"invalid saved pair/provenance "+test);Check(fake.Writes.Count==0&&fake.Queries==0,"invalid archive authorizes no query/SET "+test);test++;}
        fake=RestoreFake();fake.Pid=0xFA60;Reject(delegate{G9SettingsTransactions.RestoreProfilePair(fake,source,Root);},"restore receiver rejected");Check(fake.Queries==0&&fake.Writes.Count==0,"wrong live product has zero effects");
        fake=RestoreFake();Reject(delegate{G9SettingsTransactions.RestoreProfilePair(fake,Path.Combine(Root,"absent-source.json"),Root);},"missing source archive");Check(fake.Queries==0&&fake.Writes.Count==0,"missing file writes none");
        string malformed=Path.Combine(Root,"malformed.json");File.WriteAllText(malformed,"{");fake=RestoreFake();Reject(delegate{G9SettingsTransactions.RestoreProfilePair(fake,malformed,Root);},"malformed archive");Check(fake.Queries==0&&fake.Writes.Count==0,"malformed JSON writes none");
        string sourceText=File.ReadAllText(source);var duplicateJson=new[]{
            sourceText.Replace("\"productId\":8520","\"productId\":64096,\"productId\":8520"),
            sourceText.Replace("\"profile\":\"0c080102fd05fa00\"","\"profile\":\"0c080102fd05fa00\",\"\\u0070rofile\":\"0c080102fd05fa00\""),
            sourceText.Replace("\"activeProfile\":2","\"activeProfile\":4,\"activeProfile\":2"),
            sourceText.Replace("\"productId\":8520","\"\\u0070roductId\":8520,\"productId\":8520"),
            sourceText.Replace("\"productId\":8520","productId:8520"),
            sourceText.Replace("\"productId\":8520","'productId':8520")
        };
        for(int duplicate=0;duplicate<duplicateJson.Length;duplicate++) {
            string path=Path.Combine(Root,"duplicate-source-"+duplicate+".json");File.WriteAllText(path,duplicateJson[duplicate]);fake=RestoreFake();
            Reject(delegate{G9SettingsTransactions.RestoreProfilePair(fake,path,Root);},"ambiguous/non-JSON source "+duplicate);
            Check(fake.Writes.Count==0&&fake.Queries==0,"duplicate/escaped member rejected before device effects "+duplicate);
        }
        string escapedPath=Path.Combine(Root,"escaped-single-profile.json");File.WriteAllText(escapedPath,sourceText.Replace("\"profile\":\"0c080102fd05fa00\"","\"\\u0070rofile\":\"0c080102fd05fa00\""));
        fake=RestoreFake();G9SettingsTransactions.RestoreProfilePair(fake,escapedPath,Root);Check(fake.Writes.Count==1,"single escaped JSON name decoded normally");
        string structured=SourceSnapshot(delegate(Dictionary<string,object> s){s["fields"]=new[]{new {id="a",name="braces {x} [y] and apostrophe '"},new {id="b",name="same member names in separate objects"}};});
        fake=RestoreFake();G9SettingsTransactions.RestoreProfilePair(fake,structured,Root);Check(fake.Writes.Count==1,"duplicate scanner respects strings and separate object scopes");
        fake=RestoreFake();fake.ProfileReply=delegate(int n){var raw=PairReport(4,5);raw[4]^=1;return raw;};Reject(delegate{G9SettingsTransactions.RestoreProfilePair(fake,source,Root);},"bad current pair complement");Check(fake.Writes.Count==0&&fake.Queries==1,"current checksum failure has zero SET");
        fake=RestoreFake();fake.ProfileReply=delegate(int n){return PairReport(5,4);};Reject(delegate{G9SettingsTransactions.RestoreProfilePair(fake,source,Root);},"current active exceeds count");Check(fake.Writes.Count==0,"current pair order rejects SET");
        fake=RestoreFake();fake.DriftOnProfileQuery=2;Reject(delegate{G9SettingsTransactions.RestoreProfilePair(fake,source,Root);},"pair changes before SET");Check(fake.Writes.Count==0&&fake.Queries==2,"second guard prevents write");
        fake=RestoreFake();var identityFake=fake;fake.ProfileReply=delegate(int n){if(n==2)identityFake.PathId="changed-device";return PairReport(4,5);};Reject(delegate{G9SettingsTransactions.RestoreProfilePair(fake,source,Root);},"identity changes at last guard");Check(fake.Writes.Count==0,"path drift before SET rejected");
        fake=RestoreFake();var productFake=fake;fake.ProfileReply=delegate(int n){if(n==2)productFake.Pid=0xFA60;return PairReport(4,5);};Reject(delegate{G9SettingsTransactions.RestoreProfilePair(fake,source,Root);},"product changes at last guard");Check(fake.Writes.Count==0,"product drift before SET rejected");
        string blocked=Path.Combine(Root,"pair-backup-path-is-file");File.WriteAllText(blocked,"not a directory");fake=RestoreFake();Reject(delegate{G9SettingsTransactions.RestoreProfilePair(fake,source,blocked);},"durable archive cannot be saved");Check(fake.Writes.Count==0&&fake.Queries==1,"filesystem failure prevents SET and second query");
        fake=RestoreFake();fake.ProfileReply=delegate(int n){if(n==2)throw new IOException("synthetic prewrite read failure");return PairReport(4,5);};Reject(delegate{G9SettingsTransactions.RestoreProfilePair(fake,source,Root);},"guard transport failure");Check(fake.Writes.Count==0,"prewrite IO failure never writes");
        fake=RestoreFake();fake.DropWrite=true;Dictionary<string,object> failedResult=null;Reject(delegate{failedResult=G9SettingsTransactions.RestoreProfilePair(fake,source,Root);},"postwrite mismatched pair");Check(fake.Writes.Count==1&&failedResult==null,"failed restore has one SET, no success DTO/no retry");ProfileOnly(fake,"failed postwrite restore");
        fake=RestoreFake();fake.ProfileReply=delegate(int n){var raw=PairReport(n<3?4:2,5);if(n==3)raw[6]^=1;return raw;};failedResult=null;Reject(delegate{failedResult=G9SettingsTransactions.RestoreProfilePair(fake,source,Root);},"postwrite complement failure");Check(fake.Writes.Count==1&&failedResult==null,"bad final ACK has no success DTO and no rollback");
        fake=RestoreFake();fake.ProfileReply=delegate(int n){if(n==3)throw new IOException("synthetic postwrite transport failure");return PairReport(4,5);};failedResult=null;Reject(delegate{failedResult=G9SettingsTransactions.RestoreProfilePair(fake,source,Root);},"postwrite transport failure");Check(fake.Writes.Count==1&&failedResult==null,"final transport error has no success DTO/retry");
    }
    static int Main(){try{Run();Console.WriteLine("PASS "+count+" settings transaction checks; synthetic transport, no HID. Archives: "+Root);return 0;}
        catch(Exception error){Console.Error.WriteLine(error.ToString());return 1;}}
}
