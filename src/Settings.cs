using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;

namespace TrayToys
{
    /// <summary>持久化配置模型（序列化为 exe 同目录的 TrayToys.json）。</summary>
    [DataContract]
    internal class AppSettings
    {
        // —— 播放（输出）设备 ——
        [DataMember(Name = "modifiers")] public uint Modifiers { get; set; }
        [DataMember(Name = "key")] public uint Key { get; set; }
        /// <summary>参与循环的播放设备 Id 列表；null 或空 = 全部设备。</summary>
        [DataMember(Name = "selectedDevices")] public List<string> SelectedDevices { get; set; }

        // —— 输入（录音）设备 ——
        [DataMember(Name = "inputModifiers")] public uint InputModifiers { get; set; }
        [DataMember(Name = "inputKey")] public uint InputKey { get; set; }
        /// <summary>参与循环的输入设备 Id 列表；null 或空 = 全部设备。</summary>
        [DataMember(Name = "selectedInputDevices")] public List<string> SelectedInputDevices { get; set; }

        // —— 通用 ——
        [DataMember(Name = "toastMs")] public int ToastMs { get; set; }
        /// <summary>是否记录日志；null 表示未设置（按不记录处理）。</summary>
        [DataMember(Name = "logEnabled")] public bool? LogEnabled { get; set; }
        /// <summary>是否允许自动息屏；null 表示未设置（按允许处理，即系统默认）。</summary>
        [DataMember(Name = "allowScreenOff")] public bool? AllowScreenOff { get; set; }

        // —— 屏幕变暗（防息屏的补充）——
        /// <summary>是否启用屏幕变暗；null 表示未设置（按不启用处理）。</summary>
        [DataMember(Name = "dimEnabled")] public bool? DimEnabled { get; set; }
        /// <summary>无操作多少秒后变暗。</summary>
        [DataMember(Name = "dimSeconds")] public int DimSeconds { get; set; }
        /// <summary>变暗程度（百分比，越大越暗）。</summary>
        [DataMember(Name = "dimOpacity")] public int DimOpacity { get; set; }
        /// <summary>变暗/恢复的渐变时长（秒，0 = 立即不渐变）；null 表示未设置（按 0.4 秒处理）。</summary>
        [DataMember(Name = "dimFadeSec")] public double? DimFadeSec { get; set; }
        /// <summary>旧字段（毫秒，V1.5 用的单位）；仅为兼容读取保留，值为 null 时不写出。</summary>
        [DataMember(Name = "dimFadeMs", EmitDefaultValue = false)] public int? DimFadeMs { get; set; }

        // —— 2.4G 电量协议表 ——
        /// <summary>
        /// 自定义 2.4G 设备的电量读取规则。
        /// null 表示旧配置里没有该字段（此时使用内置默认两条）；
        /// 空列表表示用户把协议全删了（尊重为空）。
        /// </summary>
        [DataMember(Name = "hidProtocols", EmitDefaultValue = false)]
        public List<BatteryProtocol> HidProtocols { get; set; }

        // —— 电量读数的"最后变化时间" ——
        /// <summary>
        /// 每个 2.4G 读数字段对应的值与上次变化时间。
        /// 查询式（Feature）设备的接收器会用缓存值应答，读数可能冻住，
        /// 界面据此显示"值最后变化：X 前"，避免把旧值当成实时值。
        /// </summary>
        [DataMember(Name = "batteryValueStamps", EmitDefaultValue = false)]
        public List<BatteryValueStamp> BatteryValueStamps { get; set; }
    }

    /// <summary>
    /// 配置读写：存于 exe 同目录的 TrayToys.json（便于查找/手工修改）。
    /// 文件名与程序名无关，改程序名不会让用户丢配置。
    /// </summary>
    internal static class Settings
    {
        // 默认修饰键：Ctrl+Alt（主键默认「无」，由用户自行选择）
        private const uint DEF_MODS = NativeMethods.MOD_CONTROL | NativeMethods.MOD_ALT;

        public static uint Modifiers;
        public static uint Key;
        public static uint InputModifiers;
        public static uint InputKey;
        public static int ToastMs;
        public static bool LogEnabled;
        public static bool AllowScreenOff;
        public static bool DimEnabled;
        public static int DimSeconds;
        public static int DimOpacity;
        public static double DimFadeSec;
        public static List<string> SelectedDevices;
        public static List<string> SelectedInputDevices;

        /// <summary>是否本次为首次运行（从未有过配置文件，本次刚生成默认配置）。</summary>
        public static bool IsFirstRun;

        /// <summary>配置文件完整路径（exe 同目录）。</summary>
        public static string FilePath
        {
            get { return Path.Combine(AppDomain.CurrentDomain.BaseDirectory, AppInfo.Name + ".json"); }
        }

        /// <summary>
        /// 改名前的旧配置文件名，按"从新到旧"排列。只在"当前新文件还不存在"时依次找，
        /// 找到就迁移（写新文件、旧文件不删）。两次改名都算上：
        ///   config.json      —— 最初叫 AudioSwitcher 时的名字
        ///   a_few_tool.json  —— 改成 a_few_tool 之后的（本次改名前的名字）
        /// </summary>
        public static string[] LegacyFileNames
        {
            get { return new string[] { "a_few_tool.json", "config.json" }; }
        }

        /// <summary>按顺序找到的第一个存在的旧配置文件；都没有返回 null。</summary>
        public static string FindLegacyFile()
        {
            for (int i = 0; i < LegacyFileNames.Length; i++)
            {
                string p = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, LegacyFileNames[i]);
                try { if (File.Exists(p)) return p; }
                catch { }
            }
            return null;
        }

        private static bool loaded = false;

        static Settings()
        {
            Load();
        }

        /// <summary>幂等加载：确保配置已读入内存（诊断命令在设置 Logger 前应显式调用一次）。</summary>
        public static void EnsureLoaded()
        {
            if (!loaded) Load();
        }

        public static void Load()
        {
            loaded = true;

            // 0) 老文件名迁移：当前配置文件不存在时，按 LegacyFileNames 的顺序找旧的（a_few_tool.json → config.json）。
            //    读成功后由 Save() 落盘成新名字，用户不必手工改文件名，旧文件保留不删。
            string readPath = FilePath;
            string legacyPath = null;
            bool migrated = false;
            try
            {
                if (!File.Exists(readPath))
                {
                    legacyPath = FindLegacyFile();
                    if (legacyPath != null)
                    {
                        readPath = legacyPath;
                        migrated = true;
                    }
                }
            }
            catch { }

            // 1) 优先读 exe 同目录的 TrayToys.json（或迁移前的 config.json）
            try
            {
                if (File.Exists(readPath))
                {
                    using (FileStream fs = File.OpenRead(readPath))
                    {
                        DataContractJsonSerializer ser = new DataContractJsonSerializer(typeof(AppSettings));
                        AppSettings s = (AppSettings)ser.ReadObject(fs);
                        if (s != null)
                        {
                            Apply(s);
                            if (migrated)
                            {
                                Logger.Info("检测到旧配置 " + Path.GetFileName(legacyPath)
                                    + "，已迁移为 " + AppInfo.Name + ".json");
                                Save();
                            }
                            return;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Error("读取 " + Path.GetFileName(readPath) + " 失败，将使用默认配置", ex);
            }

            // 2) 首次运行：生成默认配置
            //    - 主键一律为「无」，由用户在设置里自行指定；
            //    - 默认开启防息屏 + 屏幕自动变暗（无操作 300 秒后渐变变暗，暗度 75%，渐变 1 秒）。
            //      注意：变暗只在「不允许自动息屏」时才生效，所以这里必须同时把 AllowScreenOff 设为 false，
            //      否则默认开启的变暗会处于置灰不生效的状态。
            Apply(new AppSettings
            {
                Modifiers = DEF_MODS,
                Key = HotkeyKeys.None,
                InputModifiers = DEF_MODS,
                InputKey = HotkeyKeys.None,
                ToastMs = 2000,
                LogEnabled = false,
                AllowScreenOff = false,
                DimEnabled = true,
                DimSeconds = 300,
                DimOpacity = 75,
                DimFadeSec = 1.0,
                SelectedDevices = null,
                SelectedInputDevices = null
            });
            IsFirstRun = true;
            Save(); // 首次启动即生成 TrayToys.json
        }

        private static double ClampFade(double sec)
        {
            if (sec < 0) return 0;
            if (sec > 5) return 5;
            return sec;
        }

        private static void Apply(AppSettings s)
        {
            Modifiers = s.Modifiers;
            Key = s.Key;
            ToastMs = s.ToastMs > 0 ? s.ToastMs : 2000;
            LogEnabled = s.LogEnabled ?? false; // 缺省 = 不记录日志
            AllowScreenOff = s.AllowScreenOff ?? true; // 缺省 = 允许自动息屏（系统默认）

            // 屏幕变暗：缺省不启用
            DimEnabled = s.DimEnabled ?? false;
            DimSeconds = (s.DimSeconds >= 5 && s.DimSeconds <= 3600) ? s.DimSeconds : 60;
            DimOpacity = (s.DimOpacity >= 20 && s.DimOpacity <= 95) ? s.DimOpacity : 70;
            // 渐变时长（秒）：注意必须用可空类型——旧配置里没有该字段时不能被当成「0 = 不渐变」；
            // 同时兼容 V1.5 的毫秒字段 dimFadeMs（如 2000 → 2 秒）。
            if (s.DimFadeSec.HasValue)
                DimFadeSec = ClampFade(s.DimFadeSec.Value);
            else if (s.DimFadeMs.HasValue)
                DimFadeSec = ClampFade(s.DimFadeMs.Value / 1000.0);
            else
                DimFadeSec = 0.4;

            // 输入设备快捷键：旧配置里没有该字段时（全 0）按「无」处理，与默认一致
            if (s.InputModifiers == 0 && s.InputKey == 0)
            {
                InputModifiers = DEF_MODS;
                InputKey = HotkeyKeys.None;
            }
            else
            {
                InputModifiers = s.InputModifiers;
                InputKey = s.InputKey;
            }

            SelectedDevices = s.SelectedDevices;               // null/空 = 全部设备
            SelectedInputDevices = s.SelectedInputDevices;

            // 2.4G 电量协议表：null（旧配置没有该字段）→ 保持内置默认；有值则覆盖
            BatteryProtocolStore.SetProtocols(s.HidProtocols);

            // 电量读数的"最后变化时间"（跨重启保留，否则重启后会把旧值误标成"刚刚"）
            BatteryValueTracker.Load(s.BatteryValueStamps);

            Logger.Enabled = LogEnabled;
        }

        /// <summary>写文件加锁：后台扫描（记录电量"最后变化时间"）与界面保存可能同时发生，
        /// 不加锁会写出半截 JSON 把配置搞坏。</summary>
        private static readonly object saveLock = new object();

        public static void Save()
        {
            lock (saveLock)
            {
                try
                {
                    AppSettings s = new AppSettings();
                    s.Modifiers = Modifiers;
                    s.Key = Key;
                    s.SelectedDevices = SelectedDevices;
                    s.InputModifiers = InputModifiers;
                    s.InputKey = InputKey;
                    s.SelectedInputDevices = SelectedInputDevices;
                    s.ToastMs = ToastMs;
                    s.LogEnabled = LogEnabled;
                    s.AllowScreenOff = AllowScreenOff;
                    s.DimEnabled = DimEnabled;
                    s.DimSeconds = DimSeconds;
                    s.DimOpacity = DimOpacity;
                    s.DimFadeSec = DimFadeSec;
                    s.HidProtocols = BatteryProtocolStore.Protocols; // 2.4G 协议表随配置一起存
                    s.BatteryValueStamps = BatteryValueTracker.Snapshot(); // 电量读数的"最后变化时间"
                    using (FileStream fs = File.Create(FilePath))
                    {
                        DataContractJsonSerializer ser = new DataContractJsonSerializer(typeof(AppSettings));
                        ser.WriteObject(fs, s);
                    }
                    Logger.Info("配置已保存: " + FilePath);
                }
                catch (Exception ex)
                {
                    Logger.Error("保存 TrayToys.json 失败", ex);
                }
            }
        }

        /// <summary>播放设备快捷键的可读名称，如 Ctrl+Alt+S。</summary>
        public static string DisplayName
        {
            get { return FormatHotkey(Modifiers, Key); }
        }

        /// <summary>输入设备快捷键的可读名称，如 Ctrl+Alt+M。</summary>
        public static string InputDisplayName
        {
            get { return FormatHotkey(InputModifiers, InputKey); }
        }

        private static string FormatHotkey(uint mods, uint key)
        {
            if (key == HotkeyKeys.None) return "（未设置）";
            List<string> parts = new List<string>();
            if ((mods & NativeMethods.MOD_CONTROL) != 0) parts.Add("Ctrl");
            if ((mods & NativeMethods.MOD_ALT) != 0) parts.Add("Alt");
            if ((mods & NativeMethods.MOD_SHIFT) != 0) parts.Add("Shift");
            if ((mods & NativeMethods.MOD_WIN) != 0) parts.Add("Win");
            parts.Add(HotkeyKeys.GetName(key));
            return string.Join("+", parts.ToArray());
        }
    }
}
