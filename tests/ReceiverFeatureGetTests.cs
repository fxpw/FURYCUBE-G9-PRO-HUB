using System;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

internal static class ReceiverFeatureGetTests {
 static int assertions;
 static void Check(bool value,string name){assertions++;if(!value)throw new Exception(name);}
 sealed class FakeIo:IG9ReceiverFeatureIo {
  internal readonly string Mode;
  internal G9ReceiverFeatureRequest Last;
  internal int Issues,Results,Waits,Cancels,Pauses;
  internal FakeIo(string mode){Mode=mode;}
  void Alive(G9ReceiverFeatureRequest op){
   Last=op;
   Check(!op.Released&&!op.Settled&&op.Buffer!=IntPtr.Zero&&op.NativeOverlapped!=IntPtr.Zero&&op.Event!=null,"premature release");
   lock(G9ReceiverFeatureRequest.Retained)Check(G9ReceiverFeatureRequest.Retained.Contains(op),"request root lost");
  }
  public bool Issue(G9ReceiverFeatureRequest op,out uint returned,out int error){
   Alive(op);Issues++;
   var ov=(G9ReceiverFeatureRequest.Overlapped)Marshal.PtrToStructure(op.NativeOverlapped,typeof(G9ReceiverFeatureRequest.Overlapped));
   Check(ov.Internal==IntPtr.Zero&&ov.InternalHigh==IntPtr.Zero&&ov.Offset==0&&ov.OffsetHigh==0,"OVERLAPPED not zeroed");
   Check(ov.Event==op.Event.SafeWaitHandle.DangerousGetHandle(),"wrong event");
   Check(Marshal.ReadByte(op.Buffer)==op.Data[0],"report ID not pinned");
   for(int i=1;i<op.Data.Length;i++)Check(op.Data[i]==0,"request contains old bytes");
   returned=0;error=Mode=="reject"?5:Mode=="immediate"?0:997;return Mode=="immediate";
  }
  public bool Result(G9ReceiverFeatureRequest op,out uint returned,out int error){
   Alive(op);Results++;returned=0;error=Mode=="unknown-error"?5:996;
   bool ok=Mode=="immediate"||Mode=="short"||Mode=="pending"&&Results>=2||Mode=="cancel-race"&&Cancels>0;
   if(ok){op.Data[1]=(byte)(op.Data[0]==0xA0?1:9);returned=(uint)op.Data.Length-(Mode=="short"?1u:0u);error=0;return true;}
   if((Mode=="cancel"||Mode=="cancel-delayed"&&Pauses>=3)&&Cancels>0){error=995;return false;}
   return false;
  }
  public int Wait(G9ReceiverFeatureRequest op,int ms){Alive(op);Check(ms==1500,"unexpected wait policy");Waits++;return Mode=="pending"?0:258;}
  public bool Cancel(G9ReceiverFeatureRequest op,out int error){Alive(op);Cancels++;error=Mode=="cancel-race"?1168:0;return error==0;}
  public void Pause(G9ReceiverFeatureRequest op,int ms){
   Alive(op);Check(ms==20,"unexpected cancellation grace");Pauses++;
   if(Pauses==1){GC.Collect();GC.WaitForPendingFinalizers();Alive(op);Check(Marshal.ReadByte(op.Buffer)==op.Data[0]&&!op.Event.SafeWaitHandle.IsClosed,"pending allocations did not survive GC");}
  }
 }
 static void Case(SafeFileHandle handle,string mode){
  var fake=new FakeIo(mode);bool failed=false;
  try{var data=G9ReceiverFeatureGet.Read(handle,0xA0,8,fake);Check(mode=="immediate"||mode=="pending","failure accepted");Check(data[1]==1,"completed data lost");}
  catch(InvalidDataException){failed=true;Check(mode=="short","unexpected data failure");}
  catch(TimeoutException){failed=true;Check(mode=="cancel"||mode=="cancel-delayed"||mode=="cancel-race"||mode=="unsettled"||mode=="unknown-error","unexpected timeout");}
  catch(Win32Exception){failed=true;Check(mode=="reject","unexpected native failure");}
  Check(fake.Issues==1,"native request retried");
  if(mode=="unsettled"||mode=="unknown-error"){
   Check(!fake.Last.Settled&&!fake.Last.Released&&fake.Pauses==25,"unsettled request released or grace unbounded");
   GC.Collect();GC.WaitForPendingFinalizers();Check(fake.Last.Buffer!=IntPtr.Zero&&!fake.Last.Event.SafeWaitHandle.IsClosed,"resources lost after exception");
   // Fake-only completion after the simulated kernel stops using the pointers.
   fake.Last.Settled=true;fake.Last.Release();
  }
  Check(fake.Last.Released&&G9ReceiverFeatureRequest.Retained.Count==0,"settled request leaked");
  if(mode!="immediate"&&mode!="pending")Check(failed,"expected failure missing");
  if(mode=="cancel"||mode=="cancel-delayed"||mode=="cancel-race"||mode=="unsettled"||mode=="unknown-error")Check(fake.Cancels==1,"wrong cancellation count");
  if(mode=="pending")Check(fake.Waits==1&&fake.Results==2,"pending completion skipped");
 }
 static int Main(){try{
  Check(Marshal.SizeOf(typeof(G9ReceiverFeatureRequest.Overlapped))==(IntPtr.Size==8?32:20),"native OVERLAPPED size");
  Check(Marshal.OffsetOf(typeof(G9ReceiverFeatureRequest.Overlapped),"Event").ToInt32()==(IntPtr.Size==8?24:16),"native event offset");
  using(var handle=new SafeFileHandle(new IntPtr(0x1234),false)){
   foreach(var mode in new[]{"immediate","pending","short","cancel","cancel-delayed","cancel-race","unsettled","unknown-error","reject"})Case(handle,mode);
   foreach(byte report in new byte[]{4,6,7,8,0x0B,0x0C}){var fake=new FakeIo("immediate");Check(G9ReceiverFeatureGet.Read(handle,report,64,fake).Length==64,"valid configuration shape");}
   foreach(var shape in new[]{new[]{0xA0,64},new[]{6,9},new[]{0x0A,120},new[]{0x10,8},new[]{0x22,64}}){
    var fake=new FakeIo("immediate");bool rejected=false;try{G9ReceiverFeatureGet.Read(handle,(byte)shape[0],shape[1],fake);}catch(InvalidDataException){rejected=true;}
    Check(rejected&&fake.Issues==0&&G9ReceiverFeatureRequest.Retained.Count==0,"invalid shape reached native IO");
   }
  }
  Console.WriteLine("ReceiverFeatureGetTests PASS "+assertions+" deviceAccess=0");return 0;
 }catch(Exception error){Console.Error.WriteLine(error);return 1;}}
}