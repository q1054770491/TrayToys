using System;
using System.Collections.Generic;
using System.Runtime.Serialization;

namespace TrayToys
{
    /// <summary>某个读数字段"上次变化"的记录（随 TrayToys.json 持久化）。</summary>
    [DataContract]
    internal class BatteryValueStamp
    {
        [DataMember(Name = "key")] public string Key { get; set; }
        [DataMember(Name = "value")] public int Value { get; set; }
        [DataMember(Name = "at")] public string At { get; set; } // ISO 8601
    }

    /// <summary>
    /// 记录每个 2.4G 读数字段的当前值与"上次变化时间"。
    ///
    /// 为什么需要：**查询式（Feature）2.4G 设备的接收器会用缓存值应答** ——
    /// 设备不在链路上时读数会冻住，且报文与"连着"时逐字节相同（实测 MCHOSE），
    /// 程序无法判断真假，只能把"多久没变化"如实显示出来。
    ///
    /// 状态存在 TrayToys.json 的 batteryValueStamps 字段里，跨重启有效。
    /// </summary>
    internal static class BatteryValueTracker
    {
        private class Entry
        {
            public int Value;
            public DateTime At;
        }

        private static readonly Dictionary<string, Entry> map = new Dictionary<string, Entry>(StringComparer.OrdinalIgnoreCase);
        private static bool dirty;

        /// <summary>由 Settings 在加载 TrayToys.json 后注入。</summary>
        public static void Load(List<BatteryValueStamp> list)
        {
            map.Clear();
            dirty = false;
            if (list == null) return;
            for (int i = 0; i < list.Count; i++)
            {
                BatteryValueStamp s = list[i];
                if (s == null || string.IsNullOrEmpty(s.Key)) continue;
                DateTime at;
                if (!DateTime.TryParse(s.At, out at)) at = DateTime.Now;
                Entry e = new Entry();
                e.Value = s.Value;
                e.At = at;
                map[s.Key] = e;
            }
        }

        /// <summary>供 Settings 保存时取出当前状态。</summary>
        public static List<BatteryValueStamp> Snapshot()
        {
            List<BatteryValueStamp> list = new List<BatteryValueStamp>();
            foreach (KeyValuePair<string, Entry> kv in map)
            {
                BatteryValueStamp s = new BatteryValueStamp();
                s.Key = kv.Key;
                s.Value = kv.Value.Value;
                s.At = kv.Value.At.ToString("o");
                list.Add(s);
            }
            return list;
        }

        /// <summary>
        /// 记录一次读数，返回"这个值从何时起没变过"。
        /// 值变了就刷新时间；没变则沿用旧时间（这正是要显示的东西）。
        /// </summary>
        public static DateTime Note(string key, int value)
        {
            if (string.IsNullOrEmpty(key)) return DateTime.Now;
            Entry e;
            if (!map.TryGetValue(key, out e))
            {
                e = new Entry();
                e.Value = value;
                e.At = DateTime.Now;
                map[key] = e;
                dirty = true;
                return e.At;
            }
            if (e.Value != value)
            {
                e.Value = value;
                e.At = DateTime.Now;
                dirty = true;
            }
            return e.At;
        }

        /// <summary>有新记录未落盘时返回 true（调用方负责触发一次设置保存）。</summary>
        public static bool TakeDirty()
        {
            bool d = dirty;
            dirty = false;
            return d;
        }

        /// <summary>把时间点转成"刚刚 / 12 分钟前 / 3 小时前 / 2 天前"。</summary>
        public static string Describe(DateTime at)
        {
            if (at == DateTime.MinValue) return "未知";
            TimeSpan span = DateTime.Now - at;
            if (span.TotalSeconds < 0) span = TimeSpan.Zero;
            if (span.TotalSeconds < 90) return "刚刚";
            if (span.TotalMinutes < 60) return ((int)span.TotalMinutes) + " 分钟前";
            if (span.TotalHours < 24) return ((int)span.TotalHours) + " 小时前";
            return ((int)span.TotalDays) + " 天前";
        }
    }
}
