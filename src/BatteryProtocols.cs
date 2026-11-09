using System;
using System.Collections.Generic;
using System.Runtime.Serialization;

namespace TrayToys
{
    /// <summary>一个 2.4G 设备的电量读取规则（对应协议表里的一行）。</summary>
    [DataContract]
    internal class BatteryProtocol
    {
        [DataMember(Name = "vid")] public int Vid { get; set; }
        [DataMember(Name = "pid")] public int Pid { get; set; }
        [DataMember(Name = "usagePage")] public int UsagePage { get; set; }
        [DataMember(Name = "useFeature")] public bool UseFeature { get; set; }
        [DataMember(Name = "featureReportId")] public int FeatureReportId { get; set; }
        [DataMember(Name = "batteryByte")] public int BatteryByte { get; set; }
        [DataMember(Name = "name")] public string Name { get; set; }

        /// <summary>界面显示用的可读描述。</summary>
        public string Describe()
        {
            string s = (Name == null || Name.Length == 0) ? "（未命名）" : Name;
            s += "　" + Vid.ToString("X4") + ":" + Pid.ToString("X4");
            s += "　UsagePage 0x" + UsagePage.ToString("X2");
            s += UseFeature ? ("　Feature #" + FeatureReportId) : "　Input";
            s += "　第 " + (BatteryByte + 1) + " 字节";
            return s;
        }
    }

    /// <summary>
    /// 2.4G 电量协议表（内存缓存）。
    /// 持久化**统一走 TrayToys.json 的 hidProtocols 字段**，不再单独维护 JSON 文件。
    ///
    /// **出厂默认为空**：代码里不预设任何具体设备（避免把软件发给别人时，
    /// 配置里出现对方没有的设备名）。需要识别某个设备时，用「添加设备」向导自己配。
    /// </summary>
    internal static class BatteryProtocolStore
    {
        private static List<BatteryProtocol> protocols = new List<BatteryProtocol>();
        private static bool settingsLoaded = false;

        /// <summary>当前生效的协议表（顺序即界面显示顺序）。</summary>
        public static List<BatteryProtocol> Protocols
        {
            get
            {
                EnsureSettingsLoaded();
                return protocols;
            }
        }

        /// <summary>
        /// 确保 TrayToys.json 已加载。
        /// 必要性：正常流程下 Settings 会先被访问并自动 Load，但诊断命令（如 --test-battery）
        /// 可能直接调用扫描而绕过 Settings，此时就会拿到内置默认值、忽略用户在 TrayToys.json 里的修改。
        /// </summary>
        private static void EnsureSettingsLoaded()
        {
            if (settingsLoaded) return;
            settingsLoaded = true; // 先置位，防止 Load → Apply → SetProtocols 期间递归
            try { Settings.Load(); }
            catch (Exception ex) { Logger.Error("加载配置失败（电量协议表将用内置默认）", ex); }
        }

        /// <summary>
        /// 由 Settings 在加载 TrayToys.json 后调用。
        /// 传 null 表示配置里没有该字段（保持空表）；传列表则覆盖。
        /// </summary>
        public static void SetProtocols(List<BatteryProtocol> list)
        {
            settingsLoaded = true; // 已由 Settings 注入，无需再自行加载
            if (list != null) protocols = list;
        }

        /// <summary>持久化：写入统一的 TrayToys.json。</summary>
        public static void Save()
        {
            Settings.Save();
        }

        /// <summary>按 VID+PID+UsagePage 匹配：有则替换，无则新增。</summary>
        public static void AddOrUpdate(BatteryProtocol p)
        {
            for (int i = 0; i < protocols.Count; i++)
            {
                if (protocols[i].Vid == p.Vid && protocols[i].Pid == p.Pid && protocols[i].UsagePage == p.UsagePage)
                {
                    protocols[i] = p;
                    Save();
                    return;
                }
            }
            protocols.Add(p);
            Save();
        }

        public static void Remove(BatteryProtocol p)
        {
            protocols.Remove(p);
            Save();
        }
    }
}
