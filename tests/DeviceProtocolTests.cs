using System;
using System.IO;

internal static class DeviceProtocolTests {
    static int count;
    static byte[] Hex(string value) { byte[] bytes = new byte[value.Length / 2]; for (int i = 0; i < bytes.Length; i++) bytes[i] = Convert.ToByte(value.Substring(i * 2, 2), 16); return bytes; }
    static byte[] Change(byte[] source, int at, byte value) { byte[] data = (byte[])source.Clone(); data[at] = value; return data; }
    static void Pass(bool value, string name) { if (!value) throw new Exception("FAIL " + name); count++; }
    static void Reject(Action action, string name) { try { action(); } catch (InvalidDataException) { count++; return; } throw new Exception("FAIL accepted " + name); }
    static int Main() { try {
        // Captured wired read after a real USB reconnect; independent of the C# implementation.
        byte[] zero = Hex("083b010200000300000400001100270d00003c00000500000600000100000100000100000100000100000100000100000100000a000009000000b0");
        byte[] original = Hex("083b0102000003000004000012000d0d00003c00000500000600000100000100000100000100000100000100000100000100000a00000900000097");
        Pass(G9Wire.Equal(G9Wire.DecodeKeys(zero), zero), "captured zero keymap");
        Pass(G9Wire.Equal(G9Wire.WithZero(original), zero), "original macro assignment becomes captured single zero");
        byte[] pollingPrepare=Hex("a006090001000000"),pollingSet=Hex("06090144bb00000000");
        foreach(byte[] packet in new[]{pollingPrepare,pollingSet,zero,G9Wire.BuildProfileSet(1,1)}) {
            byte[] before=(byte[])packet.Clone();
            byte[] radioSend=G9Wire.ConfigurationSendBuffer(0xFA60,134,packet);
            byte[] usbSend=G9Wire.ConfigurationSendBuffer(0x2148,262,packet);
            bool prefix=true,zeroTail=true;
            for(int i=0;i<packet.Length;i++)if(radioSend[i]!=packet[i])prefix=false;
            for(int i=packet.Length;i<radioSend.Length;i++)if(radioSend[i]!=0)zeroTail=false;
            Pass(radioSend.Length==134&&prefix&&zeroTail,"OEM134 envelope preserves validated read/SET bytes and zeroes every tail byte");
            Pass(G9Wire.Equal(usbSend,packet)&&usbSend.Length==packet.Length,"wired configuration keeps original EP0 packet length");
            radioSend[0]^=1;usbSend[0]^=1;
            Pass(G9Wire.Equal(packet,before),"configuration transport buffers do not alias caller packets");
        }
        byte[] preparedEnvelope=G9Wire.ConfigurationSendBuffer(0xFA60,134,pollingPrepare);
        Pass(G9Wire.Hex(preparedEnvelope).Substring(0,16)=="a006090001000000"&&preparedEnvelope[2]==9,"OEM polling prepare preserves logical reply length9 inside USB134");
        foreach(int capacity in new[]{-1,0,8,64,120,132,133,135,262})
            Reject(delegate{G9Wire.ConfigurationSendBuffer(0xFA60,capacity,pollingPrepare);},"unqualified receiver send capacity "+capacity);
        Reject(delegate{G9Wire.ConfigurationSendBuffer(0x2148,58,zero);},"wired send cannot exceed feature capacity");
        Reject(delegate{G9Wire.ConfigurationSendBuffer(0x0033,134,pollingPrepare);},"configuration envelope excludes bootloader");
        Reject(delegate{G9Wire.ConfigurationSendBuffer(0xFA60,134,null);},"missing logical send packet");
        Reject(delegate{G9Wire.ConfigurationSendBuffer(0xFA60,134,preparedEnvelope);},"already padded packet cannot bypass logical validation");
        Reject(delegate{G9Wire.ConfigurationSendBuffer(0xFA60,134,Hex("1008000000000000"));},"padding cannot admit firmware-update entry");
        Reject(delegate{G9Wire.ConfigurationSendBuffer(0xFA60,134,Hex("a010080001000000"));},"padding cannot admit update-entry GET prepare");
        Reject(delegate{G9Wire.ConfigurationSendBuffer(0xFA60,134,Change(pollingSet,4,0));},"padding cannot hide a bad polling complement");
        Reject(delegate{G9Wire.ConfigurationSendBuffer(0xFA60,134,Change(zero,58,0));},"padding cannot hide a bad captured key checksum");
        byte[] padded = new byte[64]; Buffer.BlockCopy(zero, 0, padded, 0, zero.Length);
        Pass(G9Wire.Equal(G9Wire.DecodeKeys(padded), zero), "receiver 64-byte transfer");
        Reject(delegate { G9Wire.DecodeKeys(new byte[58]); }, "truncated keymap");
        Reject(delegate { G9Wire.DecodeKeys(new byte[65]); }, "unexpected transfer length");
        Reject(delegate { G9Wire.DecodeKeys(Change(zero, 0, 9)); }, "stale macro header");
        Reject(delegate { G9Wire.DecodeKeys(Change(zero, 0, 11)); }, "stale version header");
        Reject(delegate { G9Wire.DecodeKeys(Change(zero, 1, 58)); }, "wrong logical length");
        Reject(delegate { G9Wire.DecodeKeys(Change(zero, 2, 2)); }, "wrong profile with otherwise valid checksum");
        Reject(delegate { G9Wire.DecodeKeys(Change(zero, 14, 0x26)); }, "corrupt payload");
        Reject(delegate { G9Wire.DecodeKeys(Change(zero, 58, 0xB1)); }, "corrupt checksum");
        byte[] idle = Hex("a000000000000000"), ready = Hex("a001000000000000");
        G9Wire.ValidateAck(idle, false); count++; G9Wire.ValidateAck(ready, true); count++;
        Reject(delegate { G9Wire.ValidateAck(ready, false); }, "pending read before a new transaction");
        Reject(delegate { G9Wire.ValidateAck(idle, true); }, "read not ready");
        Reject(delegate { G9Wire.ValidateAck(Change(ready, 7, 1), true); }, "unexpected ACK tail");
        byte[] active = Hex("0c080101fe01fe00");
        // Receiver captures have a 64-byte USB envelope even for 8-byte
        // records. Nonzero transport tail is not part of the logical record.
        foreach (byte report in new byte[] { 4, 6, 7, 8, 0x0B, 0x0C }) {
            int length = report == 0x0B ? 8 : G9Wire.SettingsReadLength(report);
            Pass(G9Wire.ConfigurationReadLength(0xFA60,report,length)==64,"receiver envelope " + report);
            Pass(G9Wire.ConfigurationReadLength(0x2148,report,length)==length,"wired length preserved " + report);
        }
        Pass(G9Wire.ConfigurationReadLength(0xFA60,0xA0,8)==8,"receiver ACK is still eight bytes");
        Pass(G9Wire.ConfigurationReadLength(0x2148,0x0A,120)==120,"wired full record unchanged");
        Reject(delegate { G9Wire.ConfigurationReadLength(0xFA60,0x0A,120); },"receiver long query blocked before prepare");
        Reject(delegate { G9Wire.ConfigurationReadLength(0x1234,0x0C,8); },"unknown product");
        Reject(delegate { G9Wire.ConfigurationReadLength(0xFA60,0x0C,64); },"transport size cannot replace logical request size");
        byte[] profileEnvelope = new byte[64];
        for(int i=8;i<64;i++)profileEnvelope[i]=(byte)(i*3);
        Buffer.BlockCopy(active,0,profileEnvelope,0,8);
        byte[] logicalProfile=G9Wire.ConfigurationReply(0xFA60,0x0C,8,profileEnvelope);
        Pass(G9Wire.Equal(logicalProfile,active),"receiver profile discards stale transport tail");
        Pass(G9Wire.Equal(G9Wire.DecodeProfile(logicalProfile),new byte[]{1,1}),"receiver profile reaches strict logical decoder");
        Reject(delegate { G9Wire.ConfigurationReply(0xFA60,0x0C,8,active); },"receiver short transfer rejected");
        Reject(delegate { G9Wire.ConfigurationReply(0x2148,0x0C,8,profileEnvelope); },"wired oversized transfer rejected");
        Reject(delegate { G9Wire.ConfigurationReply(0xFA60,0x0C,8,null); },"missing receiver transfer");
        Reject(delegate { G9Wire.DecodeProfile(G9Wire.ConfigurationReply(0xFA60,0x0C,8,Change(profileEnvelope,4,0))); },"receiver checksum/complements still checked");
        byte[] versionEnvelope=(byte[])profileEnvelope.Clone();
        Buffer.BlockCopy(Hex("0b080101084f4c34"),0,versionEnvelope,0,8);
        Pass(G9Wire.Equal(G9Wire.DecodeFirmware(G9Wire.ConfigurationReply(0xFA60,0x0B,8,versionEnvelope)),Hex("0b080101084f4c34")),"receiver firmware keeps complete marker");
        Buffer.BlockCopy(zero,0,padded,0,59);for(int i=59;i<64;i++)padded[i]=0xCD;
        Pass(G9Wire.Equal(G9Wire.DecodeKeys(G9Wire.ConfigurationReply(0xFA60,8,59,padded)),zero),"receiver key checksum excludes envelope tail");
        Pass(G9Wire.Equal(G9Wire.DecodeProfile(active), new byte[] { 1, 1 }), "captured active profile");
        Reject(delegate { G9Wire.DecodeProfile(Change(active, 2, 2)); }, "active-profile selector");
        Reject(delegate { G9Wire.DecodeProfile(Change(active, 4, 0xFF)); }, "profile complement");
        Reject(delegate { G9Wire.DecodeProfile(Change(active, 0, 8)); }, "profile report ID");
        Reject(delegate { G9Wire.DecodeProfile(Change(active, 7, 1)); }, "profile logical padding remains strict");
        Reject(delegate { G9Wire.DecodeProfile(Hex("0c080102fd01fe00")); }, "active profile cannot exceed count");
        Pass(G9Wire.Equal(G9Wire.ReadKeysRequest(), Hex("a0083b0001000000")), "captured read-keymap request");
        Pass(G9Wire.Equal(G9Wire.ReadProfileRequest(), Hex("a00c080001000000")), "captured read-profile request");
        Pass(G9Wire.Equal(G9Wire.ReadFirmwareRequest(), Hex("a00b080001000000")), "version query uses full declared eight-byte report");
        G9Wire.ValidateSend(G9Wire.ReadFirmwareRequest()); count++;
        Pass(G9Wire.Equal(G9Wire.DecodeFirmware(Hex("0b08010108000000")), Hex("0b08010108000000")), "stock version read remains available without OLED marker");
        G9Wire.DecodeFirmware(Hex("0b050101084f4c32")); count++;
        Reject(delegate { G9Wire.DecodeFirmware(Hex("0b08020108000000")); }, "wrong version selector");
        Reject(delegate { G9Wire.ValidateSend(Hex("2200000000000000")); }, "OLED feature report excluded from generic transport");
        Reject(delegate { G9Wire.ValidateSend(Furycube.G9.Oled.OledRuntimeStatusCodec.BuildReadRequest()); }, "OL3 status query excluded from generic configuration allowlist");
        G9Wire.ValidateSend(zero); count++; G9Wire.ValidateSend(G9Wire.SelectProfileOne()); count++;
        Reject(delegate { G9Wire.ValidateSend(Hex("1008000000000000")); }, "firmware-update entry command");
        Reject(delegate { G9Wire.ValidateSend(Change(zero, 58, 0)); }, "bad keymap write");
        Reject(delegate { G9Wire.ValidateSend(Hex("a0096f000d000000")); }, "macro query outside permitted operations");
        byte[] oledChunk=Furycube.G9.Oled.OledShortProtocol.BuildWriteChunk(0,0,new byte[40],true);
        Reject(delegate{G9Wire.ConfigurationSendBuffer(0xFA60,134,oledChunk);},"configuration padding does not admit OLED image commands");
        G9Wire.RequireQualifiedOledTransport(0x2148);count++;
        G9Wire.RequireQualifiedOledTransport(0xFA60);count++;
        Reject(delegate{G9Wire.RequireQualifiedOledTransport(0x0033);},"unqualified OLED bootloader blocked");
        byte[] usbOled=G9Wire.OledSetBuffer(0x2148,262,oledChunk);
        Pass(usbOled.Length==64&&G9Wire.Equal(usbOled,oledChunk),"wired OLED remains one64B EP0 packet despite max262 caps");
        byte[] rfOled=G9Wire.OledSetBuffer(0xFA60,134,oledChunk);
        Pass(rfOled.Length==56&&G9Wire.Equal(rfOled,G9Wire.Ol9RadioCarrier(oledChunk)),"RF selects the qualified 56-byte OL9 SET04 carrier");
        rfOled[12]=1;usbOled[12]=1;Pass(oledChunk[12]==0,"transport buffers cannot mutate the validated caller packet");
        Reject(delegate{G9Wire.OledSetBuffer(0xFA60,64,oledChunk);},"unqualified RF feature capacity");
        Reject(delegate{G9Wire.OledSetBuffer(0x2148,63,oledChunk);},"short wired feature capacity");
        Reject(delegate{G9Wire.OledSetBuffer(0x0033,134,oledChunk);},"no bootloader OLED envelope");
        Reject(delegate{G9Wire.OledSetBuffer(0xFA60,134,Change(oledChunk,62,0));},"bad logical OLED CRC rejected before carrier encoding");
        byte[] pixels40=new byte[40];for(int i=0;i<40;i++)pixels40[i]=(byte)i;
        byte[] carrier=G9Wire.Ol6RadioCarrier(Furycube.G9.Oled.OledShortProtocol.BuildWriteChunk(0,0,pixels40,true));
        Pass(G9Wire.Equal(carrier,Hex("04380100013fa55a00000128000102030405060708090a0b000c0d0e0f101112131415161718191a1b1c1d1e1f20212223242526273e8b06")),"OL6 carrier matches independent Python/ARM vector including displaced pixel and CRC");
        Pass(carrier.Length==56&&carrier[24]==0&&carrier[55]==6,"carrier has invalid native DPI stage and exact SET04 length");
        Reject(delegate{G9Wire.ValidateSend(carrier);},"reserved carrier never admitted by settings send allowlist");
        Reject(delegate{G9Wire.ConfigurationSendBuffer(0xFA60,134,carrier);},"configuration padding does not admit reserved OL6 carrier");
        byte[] ol6=Hex("0b080101084f4c36"),profileOne=Hex("0c080101fe01fe00");
        G9Wire.ValidateOl6RadioIdentity(0xFA60,134,ol6,profileOne);count++;
        G9Wire.ValidateOl6RadioIdentity(0xFA60,134,Hex("0b080101084f4c37"),profileOne);count++;
        G9Wire.ValidateOl6RadioIdentity(0xFA60,134,Hex("0b080101084f4c38"),profileOne);count++;
        G9Wire.ValidateOl6RadioIdentity(0xFA60,134,Hex("0b080101084f4c39"),profileOne);count++;
        byte[] liveOl9Profile=new byte[64];
        Buffer.BlockCopy(Hex("0c080101fe05fa00"),0,liveOl9Profile,0,8);
        liveOl9Profile[8]=0x04;
        G9Wire.ValidateOl6RadioIdentity(0xFA60,134,Hex("0b080101084f4c39"),G9Wire.ConfigurationReply(0xFA60,0x0C,8,liveOl9Profile));count++;
        byte[] ol9Chunk=Furycube.G9.Oled.OledShortProtocol.BuildWriteChunk(0,0,pixels40,true);
        byte[] carrier9=G9Wire.Ol9RadioCarrier(ol9Chunk);
        Pass(G9Wire.Equal(carrier9,Hex("04380100013f00000128000102030405060708090a0b0c0d000e0f101112131415161718191a1b1c1d1e1f20212223242526034ee98d2709")),"OL9 packet matches independent Python/ARM vector with OEM sum and isolated final pixel");
        int sensorSum=0;for(int i=3;i<50;i++)sensorSum+=carrier9[i];
        Pass(sensorSum==((carrier9[50]<<8)|carrier9[51]),"OL9 preserves the original sensor checksum envelope");
        byte[] shortCarrier9=G9Wire.RadioOledFeatureBuffer("OL9",134,ol9Chunk,true);
        byte[] paddedCarrier9=G9Wire.RadioOledFeatureBuffer("OL9",134,ol9Chunk,false);
        byte[] expectedPadded=new byte[134];Buffer.BlockCopy(carrier9,0,expectedPadded,0,56);
        Pass(G9Wire.Equal(shortCarrier9,carrier9)&&G9Wire.Equal(paddedCarrier9,expectedPadded),"OL9 length experiment changes only the feature envelope");
        Reject(delegate{G9Wire.RadioOledFeatureBuffer("OL8",134,ol9Chunk,true);},"56-byte feature experiment restricted to OL9");
        Reject(delegate{G9Wire.RadioOledFeatureBuffer("OL9",262,ol9Chunk,true);},"56-byte feature experiment cannot target a wired buffer");
        Reject(delegate{G9Wire.ValidateSend(carrier9);},"checksum-compatible carrier still cannot pass settings allowlist");
        Reject(delegate{G9Wire.ConfigurationSendBuffer(0xFA60,134,carrier9);},"configuration padding does not admit reserved OL9 carrier");
        Reject(delegate{G9Wire.Ol9RadioCarrier(Change(oledChunk,62,0));},"OL9 refuses malformed source GO4");
        Reject(delegate{G9Wire.ValidateOl6RadioIdentity(0xFA60,134,Hex("0b080101084f4c35"),profileOne);},"OL5 cannot receive reserved carrier");
        Reject(delegate{G9Wire.ValidateOl6RadioIdentity(0x2148,262,ol6,profileOne);},"carrier cannot use USB mouse path");
        Reject(delegate{G9Wire.ValidateOl6RadioIdentity(0xFA60,64,ol6,profileOne);},"carrier requires qualified receiver feature capacity");
        Reject(delegate{G9Wire.ValidateOl6RadioIdentity(0xFA60,134,ol6,Hex("0c080102fd02fd00"));},"inactive profile1 is never implicitly selected for carrier");
        Reject(delegate{G9Wire.Ol6RadioCarrier(Change(oledChunk,62,0));},"invalid GO4 cannot become a carrier");
        Console.WriteLine("PASS " + count + " captured-protocol and command-boundary fixtures; no HID APIs called."); return 0;
    } catch (Exception error) { Console.Error.WriteLine(error.Message); return 1; } }
}
