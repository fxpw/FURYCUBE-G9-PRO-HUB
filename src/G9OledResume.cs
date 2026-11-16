using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace Furycube.G9.Oled {
    // PC-side last successful host submission; never a claim of visible OLED
    // output or device-side persistence. No transport or device I/O here.
    internal sealed class OledResumeCache {
        static readonly byte[] Magic=Encoding.ASCII.GetBytes("G9FRAME1");
        byte[] frame;
        internal bool HasFrame {get{return frame!=null;}}
        internal bool Live {get;private set;}
        internal byte[] GetFrame(){return frame==null?null:(byte[])frame.Clone();}
        internal void Remember(byte[] pixels,bool live){OledProtocol.ValidateFrame(pixels);frame=(byte[])pixels.Clone();Live=live;}
        internal void Freeze(){Live=false;}
        internal static OledResumeCache Load(string path){
            var result=new OledResumeCache();if(!File.Exists(path))return result;
            byte[] data=File.ReadAllBytes(path);
            if(data.Length!=528)throw new InvalidDataException("Invalid saved OLED frame size");
            for(int i=0;i<8;i++)if(data[i]!=Magic[i])throw new InvalidDataException("Unknown saved OLED frame format");
            if(data[8]>1)throw new InvalidDataException("Invalid saved OLED mode");
            for(int i=9;i<16;i++)if(data[i]!=0)throw new InvalidDataException("Invalid saved OLED padding");
            using(var sha=SHA256.Create()){
                byte[] hash=sha.ComputeHash(data,0,496);
                for(int i=0;i<32;i++)if(data[496+i]!=hash[i])throw new InvalidDataException("Saved OLED frame checksum mismatch");
            }
            byte[] pixels=new byte[480];Buffer.BlockCopy(data,16,pixels,0,480);
            result.Remember(pixels,data[8]==1);return result;
        }
        internal void Save(string path){
            if(frame==null)return;
            string target=Path.GetFullPath(path),folder=Path.GetDirectoryName(target);
            Directory.CreateDirectory(folder);
            string temporary=target+"."+Guid.NewGuid().ToString("N")+".tmp";
            byte[] data=new byte[528];Buffer.BlockCopy(Magic,0,data,0,8);data[8]=Live?(byte)1:(byte)0;
            Buffer.BlockCopy(frame,0,data,16,480);
            using(var sha=SHA256.Create())Buffer.BlockCopy(sha.ComputeHash(data,0,496),0,data,496,32);
            try{
                using(var file=new FileStream(temporary,FileMode.CreateNew,FileAccess.Write,FileShare.None)) {file.Write(data,0,data.Length);file.Flush(true);}
                if(File.Exists(target))File.Replace(temporary,target,null);else File.Move(temporary,target);
            }finally{if(File.Exists(temporary))File.Delete(temporary);}
        }
    }
}
