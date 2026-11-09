using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

#if PROFILE_LIBRARY_TESTS
// The production DTO lives in G9MouseSettingsEditor. A test-only identical
// shape avoids instantiating the editor, a window or any device backend.
internal sealed class G9SettingsPreset
{
    public int schemaVersion { get; set; }
    public string name { get; set; }
    public Dictionary<string, string> values { get; set; } public string keymapHex { get; set; }
}
#endif

internal static class ProfileLibraryTests
{
    static int passed;
    static void Check(bool condition, string name) { if (!condition) throw new Exception("FAIL " + name); passed++; }
    static void Reject(Action action, string name)
    { bool rejected = false; try { action(); } catch (IOException) { rejected = true; } catch (InvalidDataException) { rejected = true; } catch (ArgumentException) { rejected = true; } Check(rejected, name); }
    static G9SettingsPreset Preset()
    { return new G9SettingsPreset { schemaVersion = 1, name = "Исходный", values = new Dictionary<string, string> { { "dpi.value1", "800" }, { "sleep.level1Seconds", "600" }, { "dpi.color1", "FF8000" } } }; }
    static int Main()
    {
        string temporaryBase = Path.GetFullPath(Path.GetTempPath());
        string root = Path.GetFullPath(Path.Combine(temporaryBase, "FURYCUBE-profile-tests-" + Guid.NewGuid().ToString("N")));
        try {
            var store = new G9ProfileStore(root);
            Check(store.ListEntries().Length == 0 && !Directory.Exists(root), "read-only listing does not create a directory");
            G9SettingsPreset preset = Preset(); string first = store.SaveNew(preset, "Рабочий профиль");
            Check(first.Length == 32 && store.ListEntries().Length == 1 && store.Read(first).name == "Рабочий профиль", "create and list Unicode profile");
            preset.values["dpi.value1"] = "999";
            Check(store.Read(first).values["dpi.value1"] == "800", "save snapshots caller dictionary");
            var loaded = store.Read(first); loaded.values["dpi.value1"] = "999";
            Check(store.Read(first).values["dpi.value1"] == "800", "read is a detached DTO");
            string second = store.SaveNew(Preset(), "Рабочий профиль");
            Check(first != second && store.ListEntries().Length == 2, "same display name never overwrites profile");
            store.Rename(first, "Новое имя"); Check(store.Read(first).name == "Новое имя" && store.Read(first).values.Count == 3, "atomic rename preserves settings");
            Check(Directory.GetFiles(root, ".pending-*", SearchOption.TopDirectoryOnly).Length == 0, "atomic save leaves no pending file");
            store.Rename(first, "../имя / не является путём");
            Check(File.Exists(Path.Combine(root, first + ".g9settings.json")) && store.Read(first).name.StartsWith("../", StringComparison.Ordinal), "display name never becomes a filename");
            string exported = Path.Combine(root, "export.json"); store.ExportFile(first, exported);
            string imported = store.ImportFile(exported, null);
            Check(imported != first && store.Read(imported).values["sleep.level1Seconds"] == "600", "export and import copy portable settings");
            const string buttons="083b010200000300000400001100270d00003c00000500000600000100000100000100000100000100000100000100000100000a000009000000b0";
            var complete=Preset();complete.keymapHex=buttons;
            string withButtons=store.SaveNew(complete,"С кнопками");
            Check(store.Read(withButtons).keymapHex==buttons.ToUpperInvariant(),"portable profile keeps a validated full button table");
            complete.keymapHex=buttons.Substring(0,116)+"b1";Reject(delegate{store.SaveNew(complete,"corrupt");},"bad button checksum cannot enter library");
            string importRenamed = store.ImportFile(exported, "Импортирован");
            Check(store.Read(importRenamed).name == "Импортирован", "import optional display name");
            byte[] source = File.ReadAllBytes(Path.Combine(root, first + ".g9settings.json")); string archived = store.Archive(first);
            Check(!File.Exists(Path.Combine(root, first + ".g9settings.json")) && File.Exists(archived) && Path.GetDirectoryName(archived) == Path.Combine(root, ".deleted"), "delete archives without data loss");
            Check(Convert.ToBase64String(source) == Convert.ToBase64String(File.ReadAllBytes(archived)), "archive preserves every file byte");
            Check(store.ListEntries().Length == 4, "archived file is not automatically reloaded");
            foreach (string invalid in new[] { "../escape", "", "..", new string('a', 33), "../../" + second }) {
                Reject(delegate { store.Read(invalid); }, "invalid read id " + invalid);
                Reject(delegate { store.Archive(invalid); }, "invalid archive id " + invalid);
                Reject(delegate { store.Rename(invalid, "test"); }, "invalid rename id " + invalid);
            }
            Reject(delegate { store.SaveNew(Preset(), " "); }, "empty display name");
            Reject(delegate { store.SaveNew(Preset(), new string('я', 101)); }, "oversized display name");
            Reject(delegate { store.SaveNew(Preset(), "line\nname"); }, "display control character");
            var bad = Preset(); bad.values["active-profile"] = "3"; Reject(delegate { store.SaveNew(bad, "test"); }, "active hardware profile not portable");
            bad = Preset(); bad.values["max-profiles"] = "5"; Reject(delegate { store.SaveNew(bad, "test"); }, "hardware profile cycle not portable");
            bad = Preset(); bad.values["bad/key"] = "5"; Reject(delegate { store.SaveNew(bad, "test"); }, "invalid field id");
            bad = Preset(); bad.values["dpi.value1"] = "12\n34"; Reject(delegate { store.SaveNew(bad, "test"); }, "value control character");
            bad = Preset(); bad.schemaVersion = 2; Reject(delegate { store.SaveNew(bad, "test"); }, "unsupported schema");
            string corruptId = Guid.NewGuid().ToString("N"), corruptFile = Path.Combine(root, corruptId + ".g9settings.json");
            File.WriteAllText(corruptFile, "{not-json", new UTF8Encoding(false));
            var entries = store.ListEntries(); bool corruptShown = false;
            foreach (var entry in entries) if (entry.Id == corruptId) corruptShown = !entry.IsReadable;
            Check(corruptShown, "corrupt entries shown as unavailable, not silently removed");
            Reject(delegate { store.Read(corruptId); }, "corrupt file cannot be opened as a draft");
            Check(File.Exists(store.Archive(corruptId)), "corrupt file can be safely archived");
            File.WriteAllBytes(corruptFile, new byte[65537]); Reject(delegate { store.ImportFile(corruptFile, "test"); }, "bounded import");
            Console.WriteLine("PASS " + passed + " local profile storage checks; no UI/USB opened."); return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error.ToString()); return 1; }
        finally {
            // Only our newly created GUID-named temporary subtree is removed.
            string checkedRoot = Path.GetFullPath(root), allowed = temporaryBase.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar + "FURYCUBE-profile-tests-";
            if (checkedRoot.StartsWith(allowed, StringComparison.OrdinalIgnoreCase) && checkedRoot.Length == allowed.Length + 32 && Directory.Exists(checkedRoot)) Directory.Delete(checkedRoot, true);
        }
    }
}
