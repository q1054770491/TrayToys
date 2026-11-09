using System.Collections.Generic;

namespace TrayToys
{
    /// <summary>常用虚拟键码（Virtual-Key Code）与显示名的映射，供设置界面选择。</summary>
    internal static class HotkeyKeys
    {
        public static readonly Dictionary<uint, string> All = new Dictionary<uint, string>();

        static HotkeyKeys()
        {
            for (char c = 'A'; c <= 'Z'; c++) All[(uint)c] = c.ToString();
            for (char c = '0'; c <= '9'; c++) All[(uint)c] = c.ToString();
            All[0x70] = "F1"; All[0x71] = "F2"; All[0x72] = "F3"; All[0x73] = "F4";
            All[0x74] = "F5"; All[0x75] = "F6"; All[0x76] = "F7"; All[0x77] = "F8";
            All[0x78] = "F9"; All[0x79] = "F10"; All[0x7A] = "F11"; All[0x7B] = "F12";
            All[0x20] = "Space"; All[0x0D] = "Enter"; All[0x09] = "Tab";
            All[0x25] = "←"; All[0x26] = "↑"; All[0x27] = "→"; All[0x28] = "↓";
            All[0xBC] = ","; All[0xBE] = "."; All[0xBD] = "-"; All[0xBB] = "=";
            All[0xBA] = ";"; All[0xDE] = "'"; All[0xDB] = "["; All[0xDD] = "]"; All[0xDC] = "\\";
        }

        /// <summary>键码 0 表示「无」（不设置主键）。</summary>
        public const uint None = 0;

        public static string GetName(uint code)
        {
            if (code == None) return "无";
            string n;
            if (All.TryGetValue(code, out n)) return n;
            return "0x" + code.ToString("X2");
        }
    }
}
