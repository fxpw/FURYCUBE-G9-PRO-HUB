using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Web.Script.Serialization;
using Furycube.G9.Settings;

// Transaction policy is transport-independent and tested with synthetic reports.
// Native HID creation stays in G9Device.cs. No reset, firmware or RF-power writer.
internal interface IG9SettingsSession {
    ushort SettingsProductId { get; }
    string SettingsDevicePath { get; }
    byte[] QuerySettings(byte report, int profile);
    void SendSettings(byte[] packet);
}

internal sealed class G9SettingsSnapshot {
    internal readonly MouseSettingsProfile Model;
    internal readonly int ActiveProfile, MaxProfiles;
    internal readonly string DevicePath, ReadAtUtc;
    internal readonly ushort ProductId;
    internal readonly Dictionary<string, byte[]> Reports;
    internal string ArchivePath;
    internal G9SettingsSnapshot(MouseSettingsProfile model, int active, int maximum, IG9SettingsSession session, Dictionary<string,byte[]> reports) {
        Model=model; ActiveProfile=active; MaxProfiles=maximum; DevicePath=session.SettingsDevicePath;
        ProductId=session.SettingsProductId; ReadAtUtc=DateTime.UtcNow.ToString("o",CultureInfo.InvariantCulture); Reports=reports;
    }
    static object GlobalField(string id,string name,int value) {
        return new { id=id,name=name,type="integer",value=value.ToString(CultureInfo.InvariantCulture),unit="",
            min=(int?)1,max=(int?)5,step=(int?)1,options=new object[0],canWrite=true,unsupportedReason=(string)null };
    }
    internal Dictionary<string,object> ToDto() {
        var raw=new Dictionary<string,string>(); foreach(var item in Reports) raw.Add(item.Key,G9Wire.Hex(item.Value));
        var fields=new List<object>();
        fields.Add(GlobalField("active-profile","Активный профиль",ActiveProfile));
        fields.Add(GlobalField("max-profiles","Количество доступных профилей",MaxProfiles));
        foreach(var field in MouseSettingsCodec.GetFields(Model)) {
            var options=new List<object>(); foreach(var option in field.Options) options.Add(new {value=option.Value,name=option.Name});
            fields.Add(new {id=field.Id,name=field.Name,type=field.Type,value=field.Value,unit=field.Unit,
                min=field.Min==0 && field.Max==0?(int?)null:field.Min,
                max=field.Min==0 && field.Max==0?(int?)null:field.Max,
                step=field.Step==0?(int?)null:field.Step,options=options.ToArray(),
                canWrite=field.CanWrite,unsupportedReason=field.UnsupportedReason});
        }
        return new Dictionary<string,object> {
            {"schemaVersion",1},{"source","FirmwareLogicalProfile"},{"devicePath",DevicePath},
            {"productId",(int)ProductId},{"productIdHex",ProductId.ToString("x4",CultureInfo.InvariantCulture)},{"profile",Model.ProfileId},
            {"activeProfile",ActiveProfile},{"maxProfiles",MaxProfiles},{"readAtUtc",ReadAtUtc},
            {"fullBodyHex",G9Wire.Hex(Model.GetBodyCopy())},{"rawReports",raw},{"fields",fields.ToArray()},
            {"archivePath",ArchivePath},{"verified",true},{"isFirmwareBackup",false},{"macroBodiesIncluded",false}
        };
    }
}

internal static class G9SettingsTransactions {
    static readonly UTF8Encoding Utf8=new UTF8Encoding(false);
    static void Require(bool value,string message) { if(!value) throw new InvalidDataException(message); }
    static void Session(IG9SettingsSession session) {
        if(session==null) throw new ArgumentNullException("session");
        Require(session.SettingsProductId==0x2148,"Full settings currently require the wired G9 1D57:2148 interface; receiver long-read framing is not qualified.");
        Require(!String.IsNullOrEmpty(session.SettingsDevicePath),"Missing settings device identity");
    }
    static byte[] Profile(IG9SettingsSession session,out byte[] raw) {
        raw=session.QuerySettings(12,1); byte[] profile=G9Wire.DecodeProfile(raw);
        Require(raw[7]==0,"Unexpected active-profile padding"); return profile;
    }
    static void SameProfile(byte[] before,byte[] after) {
        Require(G9Wire.Equal(before,after),"Active profile or profile count changed during the operation; no further writes are permitted.");
    }
    static void ValidateRf(byte[] raw,int active,int selector) {
        Require(raw!=null && raw.Length==8 && raw[0]==7 && raw[1]==8 && raw[2]==selector && raw[7]==0,
            "Wrong partial RF report ID, length, profile or padding");
        Require((raw[3]^raw[4])==255 && (raw[5]^raw[6])==255 && raw[5]==active-1,
            "RF/profile partial-read complement or active-profile mismatch");
        // GET7 exposes40102A..2D, whereas SET7 writes401028..2B. Archive only.
    }
    static void ValidateKeys(MouseSettingsProfile model,byte[] raw) {
        byte[] keys=G9Wire.DecodeKeys(raw,model.ProfileId),body=model.GetBodyCopy();
        for(int i=0;i<54;i++) Require(keys[3+i]==body[62+i],"Key table differs from the logical profile snapshot");
        Require(keys[58]==body[116],"Logical profile's truncated key checksum differs from complete GET8");
    }
    static void WriteNew(string path,byte[] bytes) {
        using(var stream=new FileStream(path,FileMode.CreateNew,FileAccess.Write,FileShare.None)) {
            stream.Write(bytes,0,bytes.Length); stream.Flush(true);
        }
    }
    static string Archive(G9SettingsSnapshot snapshot,string backupRoot) {
        if(String.IsNullOrEmpty(backupRoot)) throw new ArgumentException("Settings backup directory is required.","backupRoot");
        string root=Path.GetFullPath(backupRoot); Directory.CreateDirectory(root);
        string name="settings-"+DateTime.UtcNow.ToString("yyyyMMdd'T'HHmmssfffffff'Z'",CultureInfo.InvariantCulture)+
            "-"+snapshot.ProductId.ToString("x4")+"-profile"+snapshot.Model.ProfileId+"-"+Guid.NewGuid().ToString("N");
        string directory=Path.Combine(root,name); Directory.CreateDirectory(directory);
        WriteNew(Path.Combine(directory,"logical-profile-117.bin"),snapshot.Model.GetBodyCopy());
        foreach(var report in snapshot.Reports) WriteNew(Path.Combine(directory,report.Key+".bin"),report.Value);
        snapshot.ArchivePath=Path.Combine(directory,"snapshot.json");
        WriteNew(snapshot.ArchivePath,Utf8.GetBytes(new JavaScriptSerializer().Serialize(snapshot.ToDto())));
        return snapshot.ArchivePath;
    }
    internal static G9SettingsSnapshot Read(IG9SettingsSession session,int? expectedProfile,string backupRoot) {
        Session(session); if(expectedProfile.HasValue) G9Wire.ValidateProfileNumber(expectedProfile.Value);
        byte[] profileBeforeRaw; byte[] profileBefore=Profile(session,out profileBeforeRaw);
        int profile=profileBefore[0];
        Require(!expectedProfile.HasValue || expectedProfile.Value==profile,"The requested profile is not currently active; select it explicitly before editing.");
        byte[] all=session.QuerySettings(0x0A,profile); MouseSettingsProfile model=MouseSettingsCodec.DecodeAllData(all,profile);
        byte[] sensor=session.QuerySettings(4,profile); MouseSettingsCodec.ValidateSensorMatches(model,sensor);
        byte[] polling=session.QuerySettings(6,profile); MouseSettingsCodec.ValidatePollingMatches(model,polling);
        byte[] rf=session.QuerySettings(7,profile); ValidateRf(rf,profile,profile);
        byte[] keys=session.QuerySettings(8,profile); ValidateKeys(model,keys);
        byte[] finalAll=session.QuerySettings(0x0A,profile);
        MouseSettingsProfile finalModel=MouseSettingsCodec.DecodeAllData(finalAll,profile);
        Require(G9Wire.Equal(model.GetBodyCopy(),finalModel.GetBodyCopy()),"Profile settings changed during the read; no defaults or writes were applied.");
        byte[] profileAfterRaw; byte[] profileAfter=Profile(session,out profileAfterRaw); SameProfile(profileBefore,profileAfter);
        var reports=new Dictionary<string,byte[]> {
            {"allData",finalAll},{"sensor",sensor},{"polling",polling},{"rf",rf},{"keys",G9Wire.DecodeKeys(keys,profile)},
            {"profile",profileAfterRaw},{"profileBefore",profileBeforeRaw}
        };
        var snapshot=new G9SettingsSnapshot(finalModel,profile,profileAfter[1],session,reports);
        Archive(snapshot,backupRoot); return snapshot;
    }
    static int ParseProfileValue(string value) {
        int parsed; if(!Int32.TryParse(value,NumberStyles.None,CultureInfo.InvariantCulture,out parsed)) throw new ArgumentException("Profile value must be an integer1..5");
        G9Wire.ValidateProfileNumber(parsed); return parsed;
    }
    sealed class SavedProfilePair {
        internal string SnapshotPath,SnapshotHash,DevicePath;
        internal byte[] Raw,Pair;
    }
    static object SavedField(Dictionary<string,object> fields,string name) {
        object value;Require(fields!=null&&fields.TryGetValue(name,out value),"Missing saved profile metadata: "+name);
        return fields[name];
    }
    static int SavedInteger(Dictionary<string,object> fields,string name) {
        object value=SavedField(fields,name);Require(value is int,"Saved profile metadata must be an integer: "+name);return (int)value;
    }
    static void SavedBoolean(Dictionary<string,object> fields,string name,bool expected) {
        object value=SavedField(fields,name);Require(value is bool&&(bool)value==expected,"Unexpected saved profile provenance: "+name);
    }
    static byte[] StrictProfilePair(byte[] raw) {
        byte[] pair=G9Wire.DecodeProfile(raw);
        Require(raw[7]==0&&pair[0]<=pair[1],"Invalid profile pair padding, order or complements");return pair;
    }
    static byte[] SavedRawProfile(object encoded) {
        string hex=encoded as string;Require(hex!=null&&hex.Length==16,"Saved profile report must contain exactly eight bytes");
        var raw=new byte[8];
        for(int i=0;i<raw.Length;i++) {
            byte value;Require(Byte.TryParse(hex.Substring(i*2,2),NumberStyles.AllowHexSpecifier,CultureInfo.InvariantCulture,out value),"Saved profile report is not strict hexadecimal");raw[i]=value;
        }
        StrictProfilePair(raw);return raw;
    }
    static void RejectDuplicateSavedMembers(string json,JavaScriptSerializer serializer) {
        // JavaScriptSerializer silently keeps the last duplicate member. Check
        // decoded names per object first, including equivalent \u escapes.
        var levels=new Stack<HashSet<string>>();int memberColon=-1;
        for(int i=0;i<json.Length;i++) {
            char c=json[i];
            if(c=='{'||c=='[') {
                Require(levels.Count<32,"Saved JSON exceeds the nesting limit");
                levels.Push(c=='{'?new HashSet<string>(StringComparer.Ordinal):null);continue;
            }
            if(c=='}'||c==']') {
                Require(levels.Count>0&&((c==']')==(levels.Peek()==null)),"Saved JSON has mismatched object/array delimiters");levels.Pop();continue;
            }
            if(c=='\'')throw new InvalidDataException("Saved JSON strings and member names must use double quotes");
            if(c==':'){Require(memberColon==i,"Saved JSON member names must be quoted strings");continue;}
            if(c!='"')continue;
            int start=i;bool escaped=false;
            for(i++;i<json.Length;i++){char next=json[i];if(escaped){escaped=false;continue;}if(next=='\\'){escaped=true;continue;}if(next=='"')break;}
            Require(i<json.Length,"Unterminated saved JSON string");
            int after=i+1;while(after<json.Length&&Char.IsWhiteSpace(json[after]))after++;
            if(after<json.Length&&json[after]==':') {
                Require(levels.Count>0&&levels.Peek()!=null,"Saved JSON member is outside an object");
                string name=serializer.Deserialize<string>(json.Substring(start,i-start+1));
                Require(levels.Peek().Add(name),"Duplicate saved JSON member: "+name);memberColon=after;
            }
        }
        Require(levels.Count==0,"Unclosed saved JSON object or array");
    }
    static SavedProfilePair ReadSavedProfilePair(string snapshotFile) {
        if(String.IsNullOrWhiteSpace(snapshotFile))throw new ArgumentException("A settings snapshot JSON file is required.","snapshotFile");
        string path=Path.GetFullPath(snapshotFile);byte[] bytes;
        using(var stream=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.Read)) {
            Require(stream.Length>0&&stream.Length<=4194304,"Saved settings snapshot has an invalid file size");
            bytes=new byte[(int)stream.Length];int offset=0;
            while(offset<bytes.Length){int read=stream.Read(bytes,offset,bytes.Length-offset);Require(read>0,"Saved settings snapshot changed during read");offset+=read;}
        }
        string text=new UTF8Encoding(false,true).GetString(bytes);if(text.Length>0&&text[0]=='\uFEFF')text=text.Substring(1);
        var serializer=new JavaScriptSerializer{MaxJsonLength=4194304,RecursionLimit=32};
        RejectDuplicateSavedMembers(text,serializer);
        var saved=serializer.DeserializeObject(text) as Dictionary<string,object>;
        Require(saved!=null,"Saved settings snapshot must be a JSON object");
        Require(SavedInteger(saved,"schemaVersion")==1,"Unsupported saved settings schema");
        Require((SavedField(saved,"source") as string)=="FirmwareLogicalProfile","Only a logical-settings snapshot can restore a profile pair");
        SavedBoolean(saved,"isFirmwareBackup",false);SavedBoolean(saved,"verified",true);SavedBoolean(saved,"macroBodiesIncluded",false);
        Require(SavedInteger(saved,"productId")==0x2148,"Saved settings snapshot is not for wired G9 PID2148");
        object pidHex;if(saved.TryGetValue("productIdHex",out pidHex))Require(pidHex is string&&String.Equals((string)pidHex,"2148",StringComparison.OrdinalIgnoreCase),"Saved product ID fields disagree");
        string devicePath=SavedField(saved,"devicePath") as string;Require(!String.IsNullOrEmpty(devicePath),"Saved settings snapshot lacks a device path");
        var reports=SavedField(saved,"rawReports") as Dictionary<string,object>;Require(reports!=null,"Saved settings snapshot lacks raw reports");
        byte[] raw=SavedRawProfile(SavedField(reports,"profile"));byte[] pair=StrictProfilePair(raw);
        Require(SavedInteger(saved,"activeProfile")==pair[0]&&SavedInteger(saved,"maxProfiles")==pair[1]&&SavedInteger(saved,"profile")==pair[0],"Saved profile metadata disagrees with its raw profile report");
        object earlier;if(reports.TryGetValue("profileBefore",out earlier))Require(G9Wire.Equal(raw,SavedRawProfile(earlier)),"Saved profile changed during the original snapshot");
        string hash;using(var sha=SHA256.Create())hash=G9Wire.Hex(sha.ComputeHash(bytes));
        return new SavedProfilePair{SnapshotPath=path,SnapshotHash=hash,DevicePath=devicePath,Raw=raw,Pair=pair};
    }
    static void SameRestoreTarget(IG9SettingsSession session,string expectedDevicePath) {
        Session(session);Require(String.Equals(session.SettingsDevicePath,expectedDevicePath,StringComparison.OrdinalIgnoreCase),"Saved profile pair belongs to a different device path; no write permitted");
    }
    static string ArchiveProfilePair(IG9SettingsSession session,byte[] currentRaw,SavedProfilePair saved,string backupRoot) {
        if(String.IsNullOrWhiteSpace(backupRoot))throw new ArgumentException("A durable backup directory is required.","backupRoot");
        string root=Path.GetFullPath(backupRoot);Directory.CreateDirectory(root);
        string directory=Path.Combine(root,"profile-pair-restore-"+DateTime.UtcNow.ToString("yyyyMMdd'T'HHmmssfffffff'Z'",CultureInfo.InvariantCulture)+"-2148-"+Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        WriteNew(Path.Combine(directory,"profile-before.bin"),currentRaw);WriteNew(Path.Combine(directory,"source-profile.bin"),saved.Raw);
        byte[] before=StrictProfilePair(currentRaw);
        var metadata=new Dictionary<string,object>{
            {"schemaVersion",1},{"source","HardwareProfilePairBeforeRestore"},{"isFirmwareBackup",false},{"macroBodiesIncluded",false},
            {"verificationScope","profile-pair-only"},{"productId",(int)session.SettingsProductId},{"devicePath",session.SettingsDevicePath},
            {"savedAtUtc",DateTime.UtcNow.ToString("o",CultureInfo.InvariantCulture)},
            {"before",new {activeProfile=(int)before[0],maxProfiles=(int)before[1],rawProfileHex=G9Wire.Hex(currentRaw)}},
            {"restoreSource",new {snapshotPath=saved.SnapshotPath,snapshotSha256=saved.SnapshotHash,schemaVersion=1,source="FirmwareLogicalProfile",
                productId=0x2148,devicePath=saved.DevicePath,activeProfile=(int)saved.Pair[0],maxProfiles=(int)saved.Pair[1],rawProfileHex=G9Wire.Hex(saved.Raw)}}
        };
        string archive=Path.Combine(directory,"profile-pair.json");
        WriteNew(archive,Utf8.GetBytes(new JavaScriptSerializer().Serialize(metadata)));
        Require(File.Exists(archive),"Profile-pair backup was not saved; no write permitted");return archive;
    }
    internal static Dictionary<string,object> RestoreProfilePair(IG9SettingsSession session,string snapshotFile,string backupRoot) {
        Session(session);SavedProfilePair saved=ReadSavedProfilePair(snapshotFile);
        SameRestoreTarget(session,saved.DevicePath);
        byte[] currentRaw;Profile(session,out currentRaw);byte[] current=StrictProfilePair(currentRaw);
        SameRestoreTarget(session,saved.DevicePath);
        // Only metadata/profile12 is required. An invalid current profile body
        // must not prevent an explicitly requested return to the saved pair.
        string archive=ArchiveProfilePair(session,currentRaw,saved,backupRoot);
        SameRestoreTarget(session,saved.DevicePath);
        byte[] guardRaw;byte[] guard=Profile(session,out guardRaw);StrictProfilePair(guardRaw);SameProfile(current,guard);
        Require(G9Wire.Equal(currentRaw,guardRaw),"Profile metadata changed immediately before restore; no write permitted");
        SameRestoreTarget(session,saved.DevicePath);
        byte[] packet=G9Wire.BuildProfileSet(saved.Pair[0],saved.Pair[1]);G9Wire.ValidateSend(packet);
        session.SendSettings(packet); // Exactly one SET0C; never retry or roll back.
        SameRestoreTarget(session,saved.DevicePath);
        byte[] afterRaw;byte[] after=Profile(session,out afterRaw);StrictProfilePair(afterRaw);SameRestoreTarget(session,saved.DevicePath);
        Require(G9Wire.Equal(after,saved.Pair)&&G9Wire.Equal(afterRaw,saved.Raw),"Profile-pair restore readback differs; operation stopped without retry");
        return new Dictionary<string,object>{
            {"status","restored-profile-pair"},{"verified",true},{"verificationScope","profile-pair-only"},{"isFirmwareBackup",false},
            {"activeProfile",(int)after[0]},{"maxProfiles",(int)after[1]},{"archivePath",archive},{"backupPath",archive},
            {"sourceSnapshot",saved.SnapshotPath},{"sourceSnapshotSha256",saved.SnapshotHash},{"devicePath",session.SettingsDevicePath},
            {"productId",(int)session.SettingsProductId},{"rawProfileHex",G9Wire.Hex(afterRaw)},{"writePacketHex",G9Wire.Hex(packet)}
        };
    }
    internal static Dictionary<string,object> Apply(IG9SettingsSession session,int? expectedProfile,string field,string value,string backupRoot) {
        if(String.IsNullOrEmpty(field) || value==null) throw new ArgumentException("A field and value are required.");
        G9SettingsSnapshot before=Read(session,expectedProfile,backupRoot);
        byte[] packet; MouseSettingsEdit edit=null; int afterProfile=before.ActiveProfile,afterMaximum=before.MaxProfiles;
        if(field=="active-profile") {
            afterProfile=ParseProfileValue(value);
            Require(afterProfile<=before.MaxProfiles,"Increase the available profile count explicitly before activating this profile.");
            packet=G9Wire.BuildProfileSet(afterProfile,before.MaxProfiles);
        } else if(field=="max-profiles") {
            afterMaximum=ParseProfileValue(value);
            Require(afterMaximum>=before.ActiveProfile,"Choose a lower active profile explicitly before reducing the profile count.");
            packet=G9Wire.BuildProfileSet(before.ActiveProfile,afterMaximum);
        } else {
            edit=MouseSettingsCodec.BuildEdit(before.Model,field,value); packet=edit.Packet;
            Require(edit.ExpectedProfile==before.ActiveProfile,"Settings edit targets another profile");
        }
        // The file is complete and durable before this last identity/profile guard.
        Require(File.Exists(before.ArchivePath),"Settings backup was not saved; write stopped.");
        byte[] freshProfileRaw; byte[] freshProfile=Profile(session,out freshProfileRaw);
        SameProfile(new byte[]{(byte)before.ActiveProfile,(byte)before.MaxProfiles},freshProfile);
        G9Wire.ValidateSend(packet); session.SendSettings(packet);
        G9SettingsSnapshot after=Read(session,afterProfile,backupRoot);
        Require(after.ActiveProfile==afterProfile && after.MaxProfiles==afterMaximum,"Profile setting readback differs from the requested state");
        if(edit!=null) Require(G9Wire.Equal(after.Model.GetBodyCopy(),edit.Updated.GetBodyCopy()),
            "Settings readback differs from the requested field-preserving edit; operation stopped without retry or reset.");
        else if(field=="max-profiles") Require(G9Wire.Equal(before.Model.GetBodyCopy(),after.Model.GetBodyCopy()),
            "Changing the profile count unexpectedly changed the profile body");
        var result=after.ToDto(); result["changedField"]=field; result["backupPath"]=before.ArchivePath;
        result["writePacketHex"]=G9Wire.Hex(packet); result["verified"]=true; return result;
    }
}
