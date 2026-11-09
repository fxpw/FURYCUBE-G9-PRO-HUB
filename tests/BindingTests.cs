using System;
using System.Collections.Generic;
using System.IO;
using Furycube.G9.Bindings;

internal static class BindingTests {
    static int count;
    static byte[] Hex(string value) { byte[] data = new byte[value.Length / 2]; for (int i = 0; i < data.Length; i++) data[i] = Convert.ToByte(value.Substring(i * 2, 2), 16); return data; }
    static void Check(bool condition, string name) { if (!condition) throw new Exception("FAIL " + name); count++; }
    static void Reject(Action action, string name) { try { action(); } catch (InvalidDataException) { count++; return; } catch (ArgumentException) { count++; return; } throw new Exception("FAIL accepted " + name); }
    static void Triplet(string id, byte function, byte param1, byte param2) {
        BindingAction action = BindingCatalog.Find(id);
        Check(action != null && action.Function == function && action.DefaultParam1 == param1 && action.DefaultParam2 == param2, "captured native triplet " + id);
    }
    static int Main() { try {
        // Expectations below are independent transcriptions of the original x86 table / real keymap reads.
        Triplet("left", 2, 0, 0); Triplet("right", 3, 0, 0); Triplet("middle", 4, 0, 0);
        Triplet("forward", 6, 0, 0); Triplet("back", 5, 0, 0); Triplet("off", 1, 0, 0);
        Triplet("scroll-up", 9, 0, 0); Triplet("scroll-down", 10, 0, 0); Triplet("tilt-left", 11, 0, 0); Triplet("tilt-right", 12, 0, 0);
        Triplet("dpi-cycle", 13, 0, 0); Triplet("dpi-up", 14, 0, 0); Triplet("dpi-down", 15, 0, 0); Triplet("sniper", 16, 0, 3);
        Triplet("copy", 17, 1, 6); Triplet("paste", 17, 1, 0x19); Triplet("browser-favorites", 17, 3, 0x12);
        Triplet("close-window", 17, 4, 0x3D); Triplet("screenshot", 17, 10, 0x16); Triplet("macro", 18, 0, 13);
        Check(BindingCatalog.Buttons[3].PhysicalLabel == "6. Кнопка экрана OLED" && BindingCatalog.Buttons[3].OriginalControlName == "ms_1_btn_key_4", "screen button is table index three, not index five");
        Check(BindingCatalog.Buttons[7].PhysicalLabel == "4. Боковая «Вперёд»" && BindingCatalog.Buttons[6].PhysicalLabel == "5. Боковая «Назад»", "visible side-button order follows original XML");
        Check(BindingCatalog.GetMacroSlot(0) == 1 && BindingCatalog.GetMacroSlot(3) == 13 && BindingCatalog.GetMacroSlot(7) == 25, "native per-button macro slots");
        var ids = new HashSet<string>();
        foreach (var action in BindingCatalog.Actions) { Check(ids.Add(action.Id), "unique action ID " + action.Id); BindingCatalog.Validate(action.Function, action.DefaultParam1, action.DefaultParam2); }
        var usages = new HashSet<byte>();
        foreach (var key in BindingCatalog.KeyboardKeys) Check(usages.Add(key.Usage), "unique keyboard usage " + key.Name);
        bool zeroUsage = false, f24 = false;
        foreach (var key in BindingCatalog.KeyboardKeys) { if (key.Name == "0" && key.Usage == 0x27) zeroUsage = true; if (key.Name == "F24" && key.Usage == 0x73) f24 = true; }
        Check(zeroUsage && f24, "web keyboard usage constants");
        byte[] original = Hex("083b0102000003000004000012000d0d00003c00000500000600000100000100000100000100000100000100000100000100000a00000900000097");
        byte[] zero = Hex("083b010200000300000400001100270d00003c00000500000600000100000100000100000100000100000100000100000100000a000009000000b0");
        byte[] after = BindingCatalog.WithAssignment(original, 3, 17, 0, 0x27);
        Check(BitConverter.ToString(after) == BitConverter.ToString(zero), "assignment matches captured physical zero keymap");
        for (int i = 0; i < original.Length; i++) if (i < 12 || (i > 14 && i < 57)) Check(after[i] == original[i], "preserved byte " + i);
        Check(BindingCatalog.Resolve(17, 1, 6).Id == "copy" && BindingCatalog.Resolve(17, 0, 0x27).Id == "keyboard", "fixed shortcut resolution before generic keyboard");
        Reject(delegate { BindingCatalog.WithAssignment(original, 0, 17, 0, 0x27); }, "cannot remove the only physical Left click");
        byte[] alternate = BindingCatalog.WithAssignment(original, 6, 2, 0, 0);
        BindingCatalog.WithAssignment(alternate, 0, 17, 0, 0x27); count++;
        Reject(delegate { BindingCatalog.WithAssignment(original, -1, 2, 0, 0); }, "negative button index");
        Reject(delegate { BindingCatalog.WithAssignment(original, 18, 2, 0, 0); }, "button index overflow");
        Reject(delegate { BindingCatalog.Validate(0xFF, 0, 0); }, "unknown function");
        Reject(delegate { BindingCatalog.Validate(2, 1, 0); }, "unexpected parameters on a fixed action");
        Reject(delegate { BindingCatalog.Validate(17, 0, 0); }, "empty keyboard binding");
        Reject(delegate { BindingCatalog.Validate(17, 0, 1); }, "keyboard rollover error usage");
        BindingCatalog.Validate(17, 1, 0); count++;
        BindingCatalog.Validate(18, 0, 32); count++;
        Reject(delegate { BindingCatalog.Validate(18, 0, 33); }, "macro selector beyond firmware slot count");
        Reject(delegate { BindingCatalog.Validate(18, 1, 13); }, "unexpected macro parameter");
        Reject(delegate { BindingCatalog.Validate(16, 0, 0); }, "sniper stage must be one-based");
        byte[] damaged = (byte[])original.Clone(); damaged[58] ^= 1;
        Reject(delegate { BindingCatalog.WithAssignment(damaged, 3, 2, 0, 0); }, "corrupt source keymap");
        Console.WriteLine("PASS " + count + " native binding, keyboard catalog, preservation and Left-click guard fixtures; no HID APIs called."); return 0;
    } catch (Exception error) { Console.Error.WriteLine(error.Message); return 1; } }
}
