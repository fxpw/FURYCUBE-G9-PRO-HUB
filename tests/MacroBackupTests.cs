using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Web.Script.Serialization;
using Furycube.G9.MacroBackup;

internal static class MacroBackupTests {
    static int count;static readonly string Root=Path.Combine(Path.GetTempPath(),"g9-macro-backup-tests-"+Guid.NewGuid().ToString("N"));
    static void Check(bool value,string message){count++;if(!value)throw new Exception(message);}
    static void Reject(Action action,string message){count++;try{action();}catch(InvalidDataException){return;}catch(ArgumentException){return;}catch(IOException){return;}throw new Exception("Expected rejection: "+message);}
    static byte[] Known(int slot){var raw=new byte[111];raw[0]=9;raw[1]=111;raw[2]=(byte)slot;raw[3]=1;raw[53]=2;raw[108]=255;raw[109]=1;raw[110]=2;return raw;}
    static MacroBackupArchive NewArchive(){return new MacroBackupArchive(MacroFrameOrigin.OfflineFixture,"Synthetic offline test, not acquired from a mouse",null,null);}
    static string Hash(byte[] data){using(var sha=SHA256.Create())return BitConverter.ToString(sha.ComputeHash(data)).Replace("-","").ToLowerInvariant();}
    static Dictionary<string,object> ReadManifest(string path){return (Dictionary<string,object>)new JavaScriptSerializer().DeserializeObject(File.ReadAllText(path));}
    static void WriteManifest(string path,Dictionary<string,object> manifest){File.WriteAllText(path,new JavaScriptSerializer().Serialize(manifest));}
    static Dictionary<string,object> FirstRow(Dictionary<string,object> manifest){return (Dictionary<string,object>)((object[])manifest["records"])[0];}
    static string One(){var archive=NewArchive();archive.Add(Known(1),1);return archive.Save(Root);}
    static void Decode() {
        byte[] raw=Known(13);var record=MacroReadbackCodec.Decode(raw,13);Check(record.Slot==13&&record.Checksum==258,"independent sparse fixture checksum1+2+255=258");
        byte[] data=record.GetMacroDataCopy();Check(data.Length==106&&data[0]==1&&data[50]==2&&data[105]==255,"opaque macro payload offsets");
        raw[3]=99;data[0]=88;Check(record.GetRawFrameCopy()[3]==1&&record.GetMacroDataCopy()[0]==1,"snapshot owns input and returned buffers");
        var white=new byte[111];white[0]=9;white[1]=111;white[2]=1;for(int i=3;i<109;i++)white[i]=255;white[109]=0x69;white[110]=0x96;
        Check(MacroReadbackCodec.Decode(white,1).Checksum==27030,"maximum106-byte sum has known big-endian checksum6996");
        var zero=new byte[111];zero[0]=9;zero[1]=111;zero[2]=1;Check(MacroReadbackCodec.Decode(zero,1).Checksum==0,"valid all-zero logical record allowed without inventing one");
        white[109]=255;white[110]=255;Reject(delegate{MacroReadbackCodec.Decode(white,1);},"erased flash body withFFFF is invalid, not silently blank");
        foreach(int length in new[]{0,8,64,110,112,132,262})Reject(delegate{MacroReadbackCodec.Decode(new byte[length],1);},"unknown transport shape "+length);
        Reject(delegate{MacroReadbackCodec.Decode(null,1);},"null frame");
        foreach(int index in new[]{0,1,2,3,53,108,109,110}){var bad=Known(1);bad[index]^=1;Reject(delegate{MacroReadbackCodec.Decode(bad,1);},"corrupt header/payload/checksum byte "+index);}
        var swapped=Known(1);swapped[109]=2;swapped[110]=1;Reject(delegate{MacroReadbackCodec.Decode(swapped,1);},"little-endian checksum rejected");
        Reject(delegate{MacroReadbackCodec.Decode(Known(2),1);},"stale slot rejected even with a valid checksum");
        foreach(int slot in new[]{0,33,255,256,-1}){Reject(delegate{MacroReadbackCodec.BuildReadRequest(slot);},"request selector bound");Reject(delegate{MacroReadbackCodec.Decode(Known(1),slot);},"expected selector bound");}
        for(int slot=1;slot<=32;slot++){byte[] query=MacroReadbackCodec.BuildReadRequest(slot);Check(query.Length==8&&query[0]==0xA0&&query[1]==9&&query[2]==111&&query[3]==0&&query[4]==slot&&query[5]==0&&query[6]==0&&query[7]==0,"exact bounded A0 macro selector "+slot);}
    }
    static void PartialAndComplete() {
        var empty=NewArchive();Check(!empty.CompleteLogicalSlots&&empty.ValidatedSlotCount==0&&empty.MissingSlots().Length==32,"empty archive honestly incomplete");
        string emptyPath=empty.Save(Root);var emptyLoaded=MacroBackupArchive.Load(emptyPath);Check(emptyLoaded.ValidatedSlotCount==0&&!emptyLoaded.CompleteLogicalSlots,"empty archive roundtrip remains incomplete");
        var partial=NewArchive();partial.Add(Known(32),32);partial.Add(Known(1),1);Check(partial.MissingSlots().Length==30&&partial.MissingSlots()[0]==2,"partial archive tracks actual missing slots");
        Reject(delegate{partial.Add(Known(1),1);},"duplicate slot not overwritten");Check(partial.GetSlot(1).Checksum==258,"duplicate rejection retains original");
        var bad=Known(2);bad[109]=0;Reject(delegate{partial.Add(bad,2);},"invalid observation not added");Check(partial.ValidatedSlotCount==2&&partial.GetSlot(2)==null,"failed slot does not become fabricated data");
        string partialPath=partial.Save(Root);var restored=MacroBackupArchive.Load(partialPath);Check(restored.ValidatedSlotCount==2&&restored.GetSlot(32).GetMacroDataCopy()[105]==255,"partial archive preserves end slot bytes");
        var complete=NewArchive();for(int slot=1;slot<=32;slot++)complete.Add(Known(slot),slot);Check(complete.CompleteLogicalSlots&&complete.MissingSlots().Length==0,"all32 logical slots, not a physical-bank claim");
        string path=complete.Save(Root);var manifest=ReadManifest(path);
        foreach(string flag in new[]{"acquisitionPerformedByComponent","physicalCaptureVerified","transportQualified","restoreProven","isFirmwareBackup","isPhysicalMacroBankDump"})Check(!(bool)manifest[flag],"offline provenance never asserts "+flag);
        Check((int)manifest["logicalFrameBytes"]==111&&(int)manifest["macroDataBytes"]==106&&(int)manifest["unexposedBytesPerSlot"]==20,"logical and missing physical coverage explicit");
        Check((string)manifest["origin"]=="OfflineFixture"&&manifest["devicePath"]==null&&manifest["productId"]==null,"synthetic archive has no invented device identity");
        Check(!File.ReadAllText(path).Contains(Root),"manifest contains no private output path");
        var loaded=MacroBackupArchive.Load(path);Check(loaded.CompleteLogicalSlots&&loaded.GetSlot(32).Slot==32,"complete archive verifies exact selectors");
        string copy=Path.Combine(Root,"portable-copy");Directory.CreateDirectory(copy);foreach(string file in Directory.GetFiles(Path.GetDirectoryName(path)))File.Copy(file,Path.Combine(copy,Path.GetFileName(file)));
        Check(MacroBackupArchive.Load(Path.Combine(copy,"macro-slots.json")).ValidatedSlotCount==32,"relative files remain valid after portable copy");
    }
    static void ArchiveIntegrity() {
        string path=One();var manifest=ReadManifest(path);manifest["isFirmwareBackup"]=true;WriteManifest(path,manifest);Reject(delegate{MacroBackupArchive.Load(path);},"upgraded physical provenance rejected");
        path=One();manifest=ReadManifest(path);manifest["completeLogicalSlots"]=true;WriteManifest(path,manifest);Reject(delegate{MacroBackupArchive.Load(path);},"completeness cannot exceed actual observations");
        path=One();manifest=ReadManifest(path);manifest["missingSlots"]=new int[0];WriteManifest(path,manifest);Reject(delegate{MacroBackupArchive.Load(path);},"missing-slot list crosschecked");
        path=One();manifest=ReadManifest(path);FirstRow(manifest)["rawFile"]="../../other.bin";WriteManifest(path,manifest);Reject(delegate{MacroBackupArchive.Load(path);},"path traversal rejected before external file reads");
        path=One();manifest=ReadManifest(path);FirstRow(manifest)["checksum16"]=259;WriteManifest(path,manifest);Reject(delegate{MacroBackupArchive.Load(path);},"metadata checksum compared with frame");
        path=One();manifest=ReadManifest(path);FirstRow(manifest)["rawSha256"]=new string('0',64);WriteManifest(path,manifest);Reject(delegate{MacroBackupArchive.Load(path);},"raw file hash integrity");
        path=One();manifest=ReadManifest(path);var rows=(object[])manifest["records"];manifest["records"]=new[]{rows[0],rows[0]};WriteManifest(path,manifest);Reject(delegate{MacroBackupArchive.Load(path);},"duplicate record slot rejected");
        path=One();string json=File.ReadAllText(path).Replace("\"slot\":1","\"slot\":2,\"\\u0073lot\":1");File.WriteAllText(path,json);Reject(delegate{MacroBackupArchive.Load(path);},"escaped duplicate JSON names rejected");
        path=One();string rawPath=Path.Combine(Path.GetDirectoryName(path),"slot-01-report-111.bin");byte[] corrupted=File.ReadAllBytes(rawPath);corrupted[3]^=1;File.WriteAllBytes(rawPath,corrupted);
        manifest=ReadManifest(path);FirstRow(manifest)["rawSha256"]=Hash(corrupted);WriteManifest(path,manifest);Reject(delegate{MacroBackupArchive.Load(path);},"frame checksum still checked after matching file hash");
        path=One();string dataPath=Path.Combine(Path.GetDirectoryName(path),"slot-01-data-106.bin");var changed=File.ReadAllBytes(dataPath);changed[0]=9;File.WriteAllBytes(dataPath,changed);manifest=ReadManifest(path);FirstRow(manifest)["dataSha256"]=Hash(changed);WriteManifest(path,manifest);Reject(delegate{MacroBackupArchive.Load(path);},"opaque data must match raw frame even with updated hash");
        path=One();File.WriteAllBytes(Path.Combine(Path.GetDirectoryName(path),"slot-01-report-111.bin"),new byte[64]);Reject(delegate{MacroBackupArchive.Load(path);},"truncated physical transport report cannot become111 logical bytes");
        string blocked=Path.Combine(Root,"blocked-root");File.WriteAllText(blocked,"file");Reject(delegate{NewArchive().Save(blocked);},"filesystem error surfaced without successful archive");
    }
    static int Main(){try{Directory.CreateDirectory(Root);Decode();PartialAndComplete();ArchiveIntegrity();Console.WriteLine("MacroBackupTests PASS "+count+"; synthetic/offline only.");return 0;}catch(Exception ex){Console.Error.WriteLine(ex);return 1;}}
}
