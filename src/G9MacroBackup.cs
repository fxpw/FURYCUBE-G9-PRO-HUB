// Opaque stock macro-slot readback and offline archives. No USB, input playback,
// SET_REPORT, vendor-binary execution, or conversion to the Windows-VK editor.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Web.Script.Serialization;

namespace Furycube.G9.MacroBackup {
    public sealed class MacroSlotRecord {
        readonly byte[] raw;
        public int Slot {get;private set;}
        public ushort Checksum {get;private set;}
        internal MacroSlotRecord(int slot,byte[] frame,ushort checksum){Slot=slot;raw=(byte[])frame.Clone();Checksum=checksum;}
        public byte[] GetRawFrameCopy(){return (byte[])raw.Clone();}
        public byte[] GetMacroDataCopy(){var data=new byte[MacroReadbackCodec.MacroDataBytes];Buffer.BlockCopy(raw,3,data,0,data.Length);return data;}
    }
    public static class MacroReadbackCodec {
        public const int SlotCount=32,MacroDataBytes=106,LogicalBodyBytes=108,LogicalFrameBytes=111,FlashApiSlotStrideBytes=128;
        public static void ValidateSlot(int slot){if(slot<1||slot>SlotCount)throw new ArgumentOutOfRangeException("slot","Macro slot must be 1..32.");}
        public static byte[] BuildReadRequest(int slot){ValidateSlot(slot);return new byte[]{0xA0,0x09,0x6F,0,(byte)slot,0,0,0};}
        public static MacroSlotRecord Decode(byte[] frame,int expectedSlot) {
            ValidateSlot(expectedSlot);
            if(frame==null||frame.Length!=LogicalFrameBytes)throw new InvalidDataException("A macro readback requires exactly 111 logical bytes; transport padding/fragments are not inferred.");
            if(frame[0]!=9||frame[1]!=LogicalFrameBytes||frame[2]!=expectedSlot)throw new InvalidDataException("Wrong macro report, length or requested slot.");
            int sum=0;for(int i=3;i<3+MacroDataBytes;i++)sum+=frame[i];
            int stored=(frame[109]<<8)|frame[110];
            if(sum!=stored)throw new InvalidDataException("Macro data checksum mismatch; retain a partial archive, never fabricate an empty slot.");
            return new MacroSlotRecord(expectedSlot,frame,(ushort)stored);
        }
    }
    public enum MacroFrameOrigin {OfflineFixture,ImportedCapture}

    public sealed class MacroBackupArchive {
        readonly SortedDictionary<int,MacroSlotRecord> records=new SortedDictionary<int,MacroSlotRecord>();
        public MacroFrameOrigin Origin {get;private set;}
        public string SourceDescription {get;private set;}
        public string DevicePath {get;private set;}
        public ushort? ProductId {get;private set;}
        public int ValidatedSlotCount {get{return records.Count;}}
        public bool CompleteLogicalSlots {get{return records.Count==MacroReadbackCodec.SlotCount;}}
        public MacroBackupArchive(MacroFrameOrigin origin,string sourceDescription,string devicePath,ushort? productId) {
            if(!Enum.IsDefined(typeof(MacroFrameOrigin),origin))throw new ArgumentOutOfRangeException("origin");
            if(String.IsNullOrWhiteSpace(sourceDescription)||sourceDescription.Length>512)throw new ArgumentException("Describe the offline fixture or imported capture source.","sourceDescription");
            if(devicePath!=null&&(devicePath.Length==0||devicePath.Length>4096))throw new ArgumentException("Invalid recorded device path.","devicePath");
            if(productId.HasValue&&productId!=0x2148&&productId!=0xFA60)throw new ArgumentException("Unknown recorded G9 product ID.","productId");
            Origin=origin;SourceDescription=sourceDescription;DevicePath=devicePath;ProductId=productId;
        }
        public void Add(byte[] frame,int expectedSlot) {
            MacroSlotRecord record=MacroReadbackCodec.Decode(frame,expectedSlot);
            if(records.ContainsKey(expectedSlot))throw new InvalidDataException("This archive already contains macro slot "+expectedSlot+"; duplicate/stale observations are not silently replaced.");
            records.Add(expectedSlot,record);
        }
        public MacroSlotRecord GetSlot(int slot){MacroReadbackCodec.ValidateSlot(slot);MacroSlotRecord record;return records.TryGetValue(slot,out record)?record:null;}
        public int[] MissingSlots(){var missing=new List<int>();for(int slot=1;slot<=32;slot++)if(!records.ContainsKey(slot))missing.Add(slot);return missing.ToArray();}
        static string RawName(int slot){return "slot-"+slot.ToString("D2",CultureInfo.InvariantCulture)+"-report-111.bin";}
        static string DataName(int slot){return "slot-"+slot.ToString("D2",CultureInfo.InvariantCulture)+"-data-106.bin";}
        static string Hash(byte[] bytes){using(var sha=SHA256.Create())return BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-","").ToLowerInvariant();}
        static JavaScriptSerializer Serializer(){return new JavaScriptSerializer{MaxJsonLength=4194304,RecursionLimit=24};}
        Dictionary<string,object> Manifest(string createdAtUtc) {
            var rows=new List<object>();foreach(var item in records) {
                byte[] raw=item.Value.GetRawFrameCopy(),data=item.Value.GetMacroDataCopy();
                rows.Add(new Dictionary<string,object>{{"slot",item.Key},{"rawFile",RawName(item.Key)},{"dataFile",DataName(item.Key)},
                    {"rawSha256",Hash(raw)},{"dataSha256",Hash(data)},{"checksum16",(int)item.Value.Checksum}});
            }
            return new Dictionary<string,object>{{"schemaVersion",1},{"source","StockLogicalMacroSlots"},{"origin",Origin.ToString()},
                {"sourceDescription",SourceDescription},{"devicePath",DevicePath},{"productId",ProductId.HasValue?(object)(int)ProductId.Value:null},
                {"createdAtUtc",createdAtUtc},{"acquisitionPerformedByComponent",false},{"physicalCaptureVerified",false},
                {"transportQualified",false},{"restoreProven",false},{"isFirmwareBackup",false},{"isPhysicalMacroBankDump",false},
                {"expectedSlots",32},{"validatedSlotCount",records.Count},{"completeLogicalSlots",CompleteLogicalSlots},{"missingSlots",MissingSlots()},
                {"logicalFrameBytes",111},{"macroDataBytes",106},{"logicalBodyBytes",108},{"flashApiSlotStrideBytes",128},{"unexposedBytesPerSlot",20},
                {"records",rows.ToArray()}};
        }
        static void WriteNew(string path,byte[] bytes){using(var stream=new FileStream(path,FileMode.CreateNew,FileAccess.Write,FileShare.None)){stream.Write(bytes,0,bytes.Length);stream.Flush(true);}}
        public string Save(string backupRoot) {
            if(String.IsNullOrWhiteSpace(backupRoot))throw new ArgumentException("An offline backup directory is required.","backupRoot");
            string root=Path.GetFullPath(backupRoot);Directory.CreateDirectory(root);
            string directory=Path.Combine(root,"logical-macros-"+DateTime.UtcNow.ToString("yyyyMMdd'T'HHmmssfffffff'Z'",CultureInfo.InvariantCulture)+"-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(directory);
            foreach(var item in records){WriteNew(Path.Combine(directory,RawName(item.Key)),item.Value.GetRawFrameCopy());WriteNew(Path.Combine(directory,DataName(item.Key)),item.Value.GetMacroDataCopy());}
            string manifest=Path.Combine(directory,"macro-slots.json");
            WriteNew(manifest,new UTF8Encoding(false,true).GetBytes(Serializer().Serialize(Manifest(DateTime.UtcNow.ToString("o",CultureInfo.InvariantCulture)))));return manifest;
        }
        static Dictionary<string,object> Object(object value,params string[] names) {
            var row=value as Dictionary<string,object>;if(row==null||row.Count!=names.Length)throw new InvalidDataException("Unexpected macro archive object fields.");
            foreach(string name in names)if(!row.ContainsKey(name))throw new InvalidDataException("Missing archive field: "+name);return row;
        }
        static int Integer(object value){if(!(value is int))throw new InvalidDataException("Expected a JSON integer.");return (int)value;}
        static string Text(object value){var text=value as string;if(text==null)throw new InvalidDataException("Expected a JSON string.");return text;}
        static void Require(bool test,string message){if(!test)throw new InvalidDataException(message);}
        static byte[] ReadExact(string path,int expected) {
            using(var stream=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.Read)) {
                Require(stream.Length==expected,"Archive file has the wrong length.");var bytes=new byte[expected];int offset=0;
                while(offset<expected){int n=stream.Read(bytes,offset,expected-offset);Require(n>0,"Archive file ended early.");offset+=n;}return bytes;
            }
        }
        static bool Equal(byte[] first,byte[] second){if(first.Length!=second.Length)return false;for(int i=0;i<first.Length;i++)if(first[i]!=second[i])return false;return true;}
        static void RejectDuplicateMembers(string json,JavaScriptSerializer serializer) {
            var levels=new Stack<HashSet<string>>();int memberColon=-1;
            for(int i=0;i<json.Length;i++) {
                char c=json[i];
                if(c=='{'||c=='['){Require(levels.Count<24,"Archive JSON nesting limit exceeded.");levels.Push(c=='{'?new HashSet<string>(StringComparer.Ordinal):null);continue;}
                if(c=='}'||c==']'){Require(levels.Count>0&&((c==']')==(levels.Peek()==null)),"Mismatched archive JSON delimiter.");levels.Pop();continue;}
                if(c=='\'')throw new InvalidDataException("Archive JSON must use double-quoted strings.");
                if(c==':'){Require(memberColon==i,"Archive JSON member names must be quoted.");continue;}
                if(c!='"')continue;int start=i;bool escaped=false;
                for(i++;i<json.Length;i++){char next=json[i];if(escaped){escaped=false;continue;}if(next=='\\'){escaped=true;continue;}if(next=='"')break;}
                Require(i<json.Length,"Unterminated archive JSON string.");int after=i+1;while(after<json.Length&&Char.IsWhiteSpace(json[after]))after++;
                if(after<json.Length&&json[after]==':'){Require(levels.Count>0&&levels.Peek()!=null,"Archive JSON member outside object.");string name=serializer.Deserialize<string>(json.Substring(start,i-start+1));Require(levels.Peek().Add(name),"Duplicate archive JSON member: "+name);memberColon=after;}
            }
            Require(levels.Count==0,"Unclosed archive JSON container.");
        }
        public static MacroBackupArchive Load(string manifestPath) {
            string full=Path.GetFullPath(manifestPath);long size=new FileInfo(full).Length;Require(size>0&&size<=4194304,"Invalid archive manifest size.");
            string json=new UTF8Encoding(false,true).GetString(ReadExact(full,(int)size));if(json.Length>0&&json[0]=='\uFEFF')json=json.Substring(1);
            var serializer=Serializer();RejectDuplicateMembers(json,serializer);
            var root=Object(serializer.DeserializeObject(json),"schemaVersion","source","origin","sourceDescription","devicePath","productId","createdAtUtc",
                "acquisitionPerformedByComponent","physicalCaptureVerified","transportQualified","restoreProven","isFirmwareBackup","isPhysicalMacroBankDump",
                "expectedSlots","validatedSlotCount","completeLogicalSlots","missingSlots","logicalFrameBytes","macroDataBytes","logicalBodyBytes","flashApiSlotStrideBytes","unexposedBytesPerSlot","records");
            Require(Integer(root["schemaVersion"])==1&&Text(root["source"])=="StockLogicalMacroSlots","Unsupported macro archive schema/source.");
            foreach(string flag in new[]{"acquisitionPerformedByComponent","physicalCaptureVerified","transportQualified","restoreProven","isFirmwareBackup","isPhysicalMacroBankDump"})Require(root[flag] is bool&&!(bool)root[flag],"Unsupported physical/restore claim in archive.");
            Require(Integer(root["expectedSlots"])==32&&Integer(root["logicalFrameBytes"])==111&&Integer(root["macroDataBytes"])==106&&Integer(root["logicalBodyBytes"])==108&&Integer(root["flashApiSlotStrideBytes"])==128&&Integer(root["unexposedBytesPerSlot"])==20,"Macro archive geometry differs from stock logical format.");
            string originText=Text(root["origin"]);MacroFrameOrigin origin;
            Require(Enum.TryParse<MacroFrameOrigin>(originText,false,out origin)&&Enum.IsDefined(typeof(MacroFrameOrigin),origin)&&origin.ToString()==originText,"Unknown archive origin.");
            DateTimeOffset created;Require(DateTimeOffset.TryParse(Text(root["createdAtUtc"]),CultureInfo.InvariantCulture,DateTimeStyles.RoundtripKind,out created),"Invalid archive creation time.");
            ushort? pid=null;if(root["productId"]!=null){int value=Integer(root["productId"]);Require(value==0x2148||value==0xFA60,"Unknown recorded product ID.");pid=(ushort)value;}
            Require(root["devicePath"]==null||root["devicePath"] is string,"Invalid recorded device path.");
            var archive=new MacroBackupArchive(origin,Text(root["sourceDescription"]),(string)root["devicePath"],pid);
            var rows=root["records"] as object[];Require(rows!=null&&rows.Length<=32,"Invalid macro archive record list.");string directory=Path.GetDirectoryName(full);
            foreach(object entry in rows) {
                var row=Object(entry,"slot","rawFile","dataFile","rawSha256","dataSha256","checksum16");int slot=Integer(row["slot"]);MacroReadbackCodec.ValidateSlot(slot);
                Require(Text(row["rawFile"])==RawName(slot)&&Text(row["dataFile"])==DataName(slot),"Archive file names must use the fixed slot-relative format.");
                byte[] raw=ReadExact(Path.Combine(directory,RawName(slot)),111),data=ReadExact(Path.Combine(directory,DataName(slot)),106);
                Require(String.Equals(Text(row["rawSha256"]),Hash(raw),StringComparison.OrdinalIgnoreCase)&&String.Equals(Text(row["dataSha256"]),Hash(data),StringComparison.OrdinalIgnoreCase),"Archive file hash mismatch.");
                MacroSlotRecord decoded=MacroReadbackCodec.Decode(raw,slot);
                Require(Equal(data,decoded.GetMacroDataCopy())&&Integer(row["checksum16"])==decoded.Checksum,"Archive data/checksum disagrees with the raw frame.");archive.Add(raw,slot);
            }
            Require(Integer(root["validatedSlotCount"])==archive.ValidatedSlotCount&&root["completeLogicalSlots"] is bool&&(bool)root["completeLogicalSlots"]==archive.CompleteLogicalSlots,"Archive completeness/count metadata disagrees with records.");
            var missing=root["missingSlots"] as object[];int[] expectedMissing=archive.MissingSlots();Require(missing!=null&&missing.Length==expectedMissing.Length,"Archive missing-slot metadata disagrees with records.");
            for(int i=0;i<missing.Length;i++)Require(Integer(missing[i])==expectedMissing[i],"Archive missing-slot metadata is not exact.");return archive;
        }
    }
}
