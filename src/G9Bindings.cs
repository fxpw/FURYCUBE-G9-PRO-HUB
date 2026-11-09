using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;

namespace Furycube.G9.Bindings {
    public enum BindingParameterKind { None, Keyboard, Macro, Fire, Sniper }
    public sealed class BindingAction {
        public string Id { get; private set; }
        public string Name { get; private set; }
        public string Category { get; private set; }
        public byte Function { get; private set; }
        public byte DefaultParam1 { get; private set; }
        public byte DefaultParam2 { get; private set; }
        public BindingParameterKind ParameterKind { get; private set; }
        internal BindingAction(string id, string name, string category, byte function, byte param1, byte param2, BindingParameterKind kind) {
            Id = id; Name = name; Category = category; Function = function; DefaultParam1 = param1; DefaultParam2 = param2; ParameterKind = kind;
        }
        public override string ToString() { return Name; }
    }
    public sealed class ButtonDefinition {
        public int Index { get; private set; }
        public string Name { get; private set; }
        public string PhysicalLabel { get; private set; }
        public string OriginalControlName { get { return "ms_1_btn_key_" + (Index + 1); } }
        public bool IsVisible { get; private set; }
        public bool IsPhysicalButton { get; private set; }
        internal ButtonDefinition(int index, string name, string physicalLabel, bool visible, bool physical) {
            Index = index; Name = name; PhysicalLabel = physicalLabel; IsVisible = visible; IsPhysicalButton = physical;
        }
        public override string ToString() { return PhysicalLabel; }
    }
    public sealed class KeyboardUsage {
        public string Name { get; private set; }
        public byte Usage { get; private set; }
        internal KeyboardUsage(string name, byte usage) { Name = name; Usage = usage; }
        public override string ToString() { return Name; }
    }

    public static class BindingCatalog {
        // Triplets are transcribed from the original x86 serializer table at VA46D618
        // (20-byte records), with labels resolved through the original menu handler.
        // The web G9 PRO page independently confirms keyboard11, macro12 and media/DPI codes.
        static readonly ReadOnlyCollection<BindingAction> actions = CreateActions().AsReadOnly();
        static readonly ReadOnlyCollection<ButtonDefinition> buttons = CreateButtons().AsReadOnly();
        static readonly ReadOnlyCollection<KeyboardUsage> keys = CreateKeys().AsReadOnly();
        public static ReadOnlyCollection<BindingAction> Actions { get { return actions; } }
        public static ReadOnlyCollection<ButtonDefinition> Buttons { get { return buttons; } }
        public static ReadOnlyCollection<KeyboardUsage> KeyboardKeys { get { return keys; } }
        static BindingAction Action(string id, string name, string category, byte function, byte p1 = 0, byte p2 = 0, BindingParameterKind kind = BindingParameterKind.None) {
            return new BindingAction(id, name, category, function, p1, p2, kind);
        }
        static List<BindingAction> CreateActions() {
            return new List<BindingAction> {
                Action("left", "Левый клик", "Мышь", 2), Action("right", "Правый клик", "Мышь", 3),
                Action("middle", "Средняя кнопка", "Мышь", 4), Action("forward", "Вперёд", "Мышь", 6), Action("back", "Назад", "Мышь", 5),
                Action("double-click", "Двойной левый клик", "Мышь", 7), Action("fire", "Скоростная стрельба", "Мышь", 8, 0, 0, BindingParameterKind.Fire),
                Action("scroll-up", "Прокрутка вверх", "Мышь", 9), Action("scroll-down", "Прокрутка вниз", "Мышь", 10),
                Action("tilt-left", "Прокрутка влево", "Мышь", 11), Action("tilt-right", "Прокрутка вправо", "Мышь", 12),
                Action("off", "Отключить кнопку", "Мышь", 1),
                Action("dpi-cycle", "Циклическое переключение DPI", "DPI", 13), Action("dpi-up", "DPI +", "DPI", 14), Action("dpi-down", "DPI −", "DPI", 15),
                Action("sniper", "Снайперская кнопка", "DPI", 16, 0, 3, BindingParameterKind.Sniper),
                Action("keyboard", "Одиночная клавиша или сочетание", "Клавиатура", 17, 0, 0x27, BindingParameterKind.Keyboard),
                Action("macro", "Макрос из памяти мыши", "Макрос", 18, 0, 13, BindingParameterKind.Macro),
                Action("media-player", "Открыть медиаплеер", "Мультимедиа", 0x15), Action("previous-track", "Предыдущая дорожка", "Мультимедиа", 0x16),
                Action("next-track", "Следующая дорожка", "Мультимедиа", 0x17), Action("play-pause", "Воспроизведение / пауза", "Мультимедиа", 0x18),
                Action("media-stop", "Остановить воспроизведение", "Мультимедиа", 0x19), Action("mute", "Выключить звук", "Мультимедиа", 0x1A),
                Action("volume-up", "Громкость +", "Мультимедиа", 0x1B), Action("volume-down", "Громкость −", "Мультимедиа", 0x1C),
                Action("browser-home", "Домашняя страница браузера", "Браузер", 0x25),
                Action("browser-favorites", "Избранное браузера", "Браузер", 0x11, 3, 0x12),
                Action("browser-favorites-consumer", "Избранное (мультимедийная команда)", "Браузер", 0x1F),
                Action("browser-forward", "Вперёд в браузере", "Браузер", 0x20), Action("browser-back", "Назад в браузере", "Браузер", 0x21),
                Action("browser-stop", "Остановить загрузку страницы", "Браузер", 0x22), Action("browser-refresh", "Обновить страницу", "Браузер", 0x24),
                Action("browser-search", "Поиск в браузере", "Браузер", 0x26), Action("email", "Электронная почта", "Приложения", 0x1E),
                Action("calculator", "Калькулятор", "Приложения", 0x1D), Action("my-computer", "Этот компьютер", "Приложения", 0x23),
                Action("cut", "Вырезать", "Сочетания", 0x11, 1, 0x1B), Action("copy", "Копировать", "Сочетания", 0x11, 1, 0x06),
                Action("paste", "Вставить", "Сочетания", 0x11, 1, 0x19), Action("open", "Открыть", "Сочетания", 0x11, 1, 0x12),
                Action("save", "Сохранить", "Сочетания", 0x11, 1, 0x16), Action("find", "Найти", "Сочетания", 0x11, 1, 0x09),
                Action("redo", "Повторить действие", "Сочетания", 0x11, 1, 0x1C), Action("select-all", "Выделить всё", "Сочетания", 0x11, 1, 0x04),
                Action("print", "Печать", "Сочетания", 0x11, 1, 0x13), Action("close-window", "Закрыть окно", "Сочетания", 0x11, 4, 0x3D),
                Action("switch-windows", "Переключить окно", "Сочетания", 0x11, 4, 0x2B), Action("desktop", "Показать рабочий стол", "Сочетания", 0x11, 8, 0x07),
                Action("run", "Выполнить…", "Сочетания", 0x11, 8, 0x15), Action("lock", "Заблокировать компьютер", "Сочетания", 0x11, 8, 0x0F),
                Action("screenshot", "Снимок экрана", "Сочетания", 0x11, 0x0A, 0x16),
                Action("led-cycle", "Переключить подсветку", "Дополнительно", 0x29, 0, 3),
                Action("profile-cycle", "Циклическое переключение профиля", "Дополнительно", 0x34),
                Action("profile-up", "Следующий профиль", "Дополнительно", 0x35), Action("profile-down", "Предыдущий профиль", "Дополнительно", 0x36),
                Action("mode-key", "Переключение режима", "Дополнительно", 0x3C)
            };
        }
        static List<ButtonDefinition> CreateButtons() {
            var result = new List<ButtonDefinition>();
            string[] names = { "Левая кнопка", "Правая кнопка", "Нажатие колеса", "Кнопка экрана OLED", "Скрытый слот DPI", "Скрытый слот режима",
                "Боковая «Назад»", "Боковая «Вперёд»" };
            int[] physicalNumbers = { 1, 2, 3, 6, 0, 0, 5, 4 };
            for (int i = 0; i < 18; i++) {
                string name = i < names.Length ? names[i] : i == 16 ? "Колесо вниз" : i == 17 ? "Колесо вверх" : "Резервный слот " + (i + 1);
                bool physical = i == 0 || i == 1 || i == 2 || i == 3 || i == 6 || i == 7;
                string label = physical ? physicalNumbers[i] + ". " + name : name;
                result.Add(new ButtonDefinition(i, name, label, physical || i == 16 || i == 17, physical));
            }
            return result;
        }
        static List<KeyboardUsage> CreateKeys() {
            var result = new List<KeyboardUsage>();
            for (int i = 0; i < 26; i++) result.Add(new KeyboardUsage(((char)('A' + i)).ToString(), (byte)(4 + i)));
            for (int i = 1; i <= 9; i++) result.Add(new KeyboardUsage(i.ToString(), (byte)(0x1D + i))); result.Add(new KeyboardUsage("0", 0x27));
            string[] basic = { "Enter", "Esc", "Backspace", "Tab", "Пробел", "−", "=", "[", "]", "\\" };
            for (int i = 0; i < basic.Length; i++) result.Add(new KeyboardUsage(basic[i], (byte)(0x28 + i)));
            string[] punctuation = { ";", "'", "`", ",", ".", "/", "Caps Lock" };
            for (int i = 0; i < punctuation.Length; i++) result.Add(new KeyboardUsage(punctuation[i], (byte)(0x33 + i)));
            for (int i = 1; i <= 12; i++) result.Add(new KeyboardUsage("F" + i, (byte)(0x39 + i)));
            string[] navigation = { "Print Screen", "Scroll Lock", "Pause", "Insert", "Home", "Page Up", "Delete", "End", "Page Down", "→", "←", "↓", "↑", "Num Lock", "Num /", "Num *", "Num −", "Num +", "Num Enter" };
            for (int i = 0; i < navigation.Length; i++) result.Add(new KeyboardUsage(navigation[i], (byte)(0x46 + i)));
            for (int i = 1; i <= 9; i++) result.Add(new KeyboardUsage("Num " + i, (byte)(0x58 + i)));
            result.Add(new KeyboardUsage("Num 0", 0x62)); result.Add(new KeyboardUsage("Num .", 0x63)); result.Add(new KeyboardUsage("ISO 102", 0x64));
            result.Add(new KeyboardUsage("Контекстное меню", 0x65)); result.Add(new KeyboardUsage("Power", 0x66)); result.Add(new KeyboardUsage("Num =", 0x67));
            for (int i = 13; i <= 24; i++) result.Add(new KeyboardUsage("F" + i, (byte)(0x68 + i - 13)));
            string[] modifiers = { "Левый Ctrl", "Левый Shift", "Левый Alt", "Левый Win", "Правый Ctrl", "Правый Shift", "Правый Alt", "Правый Win" };
            for (int i = 0; i < modifiers.Length; i++) result.Add(new KeyboardUsage(modifiers[i], (byte)(0xE0 + i)));
            return result;
        }
        public static BindingAction Find(string id) { foreach (var action in actions) if (action.Id == id) return action; return null; }
        public static BindingAction Resolve(byte function, byte param1, byte param2) {
            foreach (var action in actions) if (action.ParameterKind == BindingParameterKind.None && action.Function == function && action.DefaultParam1 == param1 && action.DefaultParam2 == param2) return action;
            foreach (var action in actions) if (action.ParameterKind != BindingParameterKind.None && action.Function == function) return action;
            return null;
        }
        public static byte GetMacroSlot(int index) {
            if (index < 0 || index >= 18) throw new ArgumentOutOfRangeException("index");
            byte[] originalSlots = { 1, 5, 9, 13, 17, 0, 21, 25, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 }; return originalSlots[index];
        }
        public static void Validate(byte function, byte param1, byte param2) {
            BindingAction action = Resolve(function, param1, param2);
            if (action == null) throw new InvalidDataException("Unsupported binding triplet");
            switch (action.ParameterKind) {
                case BindingParameterKind.None: return;
                case BindingParameterKind.Keyboard:
                    if ((param2 >= 4 && param2 <= 0xE7) || (param2 == 0 && param1 != 0)) return;
                    break;
                case BindingParameterKind.Macro:
                    // Firmware FLASH_RD_Macro clamps zero-based indices >=32; wire selectors are one-based.
                    if (param1 == 0 && param2 >= 1 && param2 <= 32) return; break;
                case BindingParameterKind.Fire: if (param1 == 0) return; break;
                case BindingParameterKind.Sniper: if (param1 == 0 && param2 >= 1 && param2 <= 8) return; break;
            }
            throw new InvalidDataException("Parameters are outside the supported binding range");
        }
        static byte[] DecodeKeys(byte[] data, int expectedProfile) {
            if (expectedProfile < 1 || expectedProfile > 5) throw new ArgumentOutOfRangeException("expectedProfile");
            if (data == null || (data.Length != 59 && data.Length != 64) || data[0] != 8 || data[1] != 59 || data[2] != expectedProfile) throw new InvalidDataException("Keymap does not match the requested onboard profile");
            int sum = 0; for (int i = 3; i < 57; i++) sum += data[i];
            if (sum != ((data[57] << 8) | data[58])) throw new InvalidDataException("Keymap checksum mismatch");
            byte[] result = new byte[59]; Buffer.BlockCopy(data, 0, result, 0, 59); return result;
        }
        public static void EnsureLeftClick(byte[] keymap) {
            EnsureLeftClick(keymap, 1);
        }
        public static void EnsureLeftClick(byte[] keymap, int expectedProfile) {
            byte[] data = DecodeKeys(keymap, expectedProfile);
            foreach (var button in buttons) if (button.IsPhysicalButton && data[3 + button.Index * 3] == 2) return;
            throw new InvalidDataException("At least one main physical mouse button must retain Left click");
        }
        public static byte[] WithAssignment(byte[] keymap, int index, byte function, byte param1, byte param2) {
            return WithAssignment(keymap, index, function, param1, param2, 1);
        }
        public static byte[] WithAssignment(byte[] keymap, int index, byte function, byte param1, byte param2, int expectedProfile) {
            if (index < 0 || index >= 18) throw new ArgumentOutOfRangeException("index"); Validate(function, param1, param2);
            byte[] result = DecodeKeys(keymap, expectedProfile); int offset = 3 + index * 3;
            result[offset] = function; result[offset + 1] = param1; result[offset + 2] = param2;
            int sum = 0; for (int i = 3; i < 57; i++) sum += result[i]; result[57] = (byte)(sum >> 8); result[58] = (byte)sum;
            EnsureLeftClick(result, expectedProfile); return result;
        }
        public static byte[] RetargetKeymap(byte[] keymap, int targetProfile) {
            if(targetProfile<1 || targetProfile>5)throw new ArgumentOutOfRangeException("targetProfile");
            if(keymap==null || keymap.Length<3)throw new InvalidDataException("Missing portable button table");
            byte[] result=DecodeKeys(keymap,keymap[2]);EnsureLeftClick(result,keymap[2]);
            result[2]=(byte)targetProfile;return result;
        }
    }
}
