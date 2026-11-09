using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace TrayToys
{
    /// <summary>一个有电量数据的设备。</summary>
    internal class BatteryDevice
    {
        public string Name;         // 设备友好名，如 "iQOO TWS 2"、"罗技 MX Master"
        public string Category;     // 中文类别：蓝牙 / 鼠标 / 键盘 / 手柄 / 其它
        public string InstanceId;
        public Guid ContainerId;    // 用于判断设备是否真的在线
        public int Level;           // 0 ~ 100
        public int DevNodeStatus;   // DEVPKEY_Device_DevNodeStatus（含 DN_DEVICE_DISCONNECTED 位）

        // 仅"查询式（Feature）2.4G 设备"会用到：接收器永远用缓存值应答，
        // 读数可能冻住（设备不在链路上也照答），所以界面要显示"值最后变化于 X 前"。
        public bool ValueMayBeStale;
        public DateTime ValueLastChanged;
    }

    /// <summary>
    /// 扫描"能向 Windows 上报电量"的无线设备，两条路径：
    ///
    /// ① 蓝牙等标准设备：PnP 属性 DEVPKEY_Device_BatteryLevel
    ///   （{104ea319-6ee2-4701-bd47-8ddbf425bbe5}, 2，类型 BYTE），
    ///   即 Windows「设置 → 蓝牙和其他设备」显示电量的同一数据源。
    ///
    /// ② 2.4G 私有协议设备：电量夹带在 HID 报告里，按协议表读取
    ///   （VID:PID + 接口 UsagePage + 读取方式 + 字节偏移）。
    ///   协议表存在 TrayToys.json 的 hidProtocols 字段（见 BatteryProtocols.cs），
    ///   用户可用「添加设备」向导自行配置；代码里只保留首次运行用的内置默认值。
    ///
    /// 重要事实（决定这个功能只能"有什么显示什么"）：
    ///   · 蓝牙 HID 鼠标/键盘、部分蓝牙耳机 会上报 → 能读到；
    ///   · 很多 TWS 耳机（如 iQOO TWS 2）不走标准 Battery Service → 读不到；
    ///   · 2.4G 设备协议五花八门（Input 主动上报 / Feature 查询式都有），需逐个实测收录；
    /// 蓝牙路径不写死设备、按实际能读到的来；2.4G 路径按协议表收录，未收录的读不到。
    /// </summary>
    internal static class DeviceBatteryManager
    {
        private static readonly Guid GUID_Device = new Guid("A45C254E-DF1C-4EFD-8020-67D146A850E0");
        private static readonly Guid GUID_DeviceStatus = new Guid("4340A6C5-93FA-4706-972C-7B648008A5A7"); // DEVPKEY_Device_DevNodeStatus
        private static readonly Guid GUID_BatteryLevel = new Guid("104EA319-6EE2-4701-BD47-8DDBF425BBE5");
        private static readonly Guid GUID_ContainerId = new Guid("8C7ED206-3F8A-4827-B3AB-AE9E1FAEFC6C");

        // 各类"功能接口"的设备类 GUID —— 这些节点会随设备连接/断开而出现/消失，
        // 而 DEVPKEY_Device_BatteryLevel 在断开后仍会残留（实测 iQOO TWS 2 断开后仍报 100%），
        // 所以必须用它们来判断设备是不是真的还在线。
        private static readonly Guid[] FunctionalClasses = new Guid[]
        {
            new Guid("C166523C-FE0C-4A94-A586-F1A80CFBBF3E"), // AudioEndpoint（耳机/扬声器）
            new Guid("745A17A0-74D3-11D0-B6FE-00A0C90F57DA"), // HIDClass
            new Guid("4D36E96F-E325-11CE-BFC1-08002BE10318"), // Mouse
            new Guid("4D36E96B-E325-11CE-BFC1-08002BE10318")  // Keyboard
        };

        private const uint DIGCF_PRESENT = 0x00000002;
        private const uint DIGCF_ALLCLASSES = 0x00000004;

        // DEVPKEY_Device_* 的 pid（都在 GUID_Device 下）
        private const uint PID_DeviceClass = 9;
        private const uint PID_FriendlyName = 14;
        private const uint PID_ContainerId = 2;   // 在 GUID_ContainerId 下
        private const uint PID_BatteryLevel = 2;  // 在 GUID_BatteryLevel 下
        private const uint PID_DevNodeStatus = 2; // 在 GUID_DeviceStatus 下（UINT32）

        // DEVPKEY_Device_DevNodeStatus 里的"设备已断开"位。
        // 实测（2026-10-27，键盘 CB082A4D6FD0）：
        //   蓝牙关闭时 = 0x0380200A（含 0x02000000），蓝牙连上后该位清除；耳机 D498B9E04807 同族属性 = 0x0180600A。
        // 这是判断"仅 BLE 配对的设备"是否在线的唯一可靠信号（经典蓝牙枚举 API 列不出它们）。
        // 注意 pid 必须是 2：写成 1 时 SetupDiGetDeviceProperty 直接返回"无此属性"，
        // 读出来恒为 0，过滤会静默失效（这个错踩过一次）。
        private const int DN_DEVICE_DISCONNECTED = 0x02000000;

        private static List<BatteryDevice> cached = new List<BatteryDevice>();
        private static DateTime lastScan = DateTime.MinValue;

        /// <summary>最近一次扫描到的设备（可能为空）。</summary>
        public static List<BatteryDevice> Devices { get { return cached; } }

        /// <summary>最近一次扫描时间。</summary>
        public static DateTime LastScanTime { get { return lastScan; } }

        /// <summary>重新扫描一次（同步）。调用方在 UI 线程时请改用 ScanNowAsync。</summary>
        public static void ScanNow()
        {
            try
            {
                // 扫描 300+ 设备约需 1 秒以上，所以先算到局部列表，最后整体替换引用（原子），
                // 避免 UI 线程读到"清了还没填"的中间状态。
                List<BatteryDevice> result = Scan();
                cached = result;
                lastScan = DateTime.Now;

                if (result.Count == 0)
                    Logger.Info("电量扫描完成：当前没有可读电量的设备");
                else
                {
                    Logger.Info("电量扫描完成，发现 " + result.Count + " 个可读电量的设备:");
                    for (int i = 0; i < result.Count; i++)
                        Logger.Info("  " + result[i].Name + "（" + result[i].Category + "）= " + result[i].Level + "%");
                }

                // 读数有变化时把"最后变化时间"落盘（电量变化很慢，不会频繁写文件）
                if (BatteryValueTracker.TakeDirty())
                {
                    try { Settings.Save(); } catch { }
                }
            }
            catch (Exception ex)
            {
                Logger.Error("电量扫描失败", ex);
            }
        }

        /// <summary>
        /// 后台线程扫描（不阻塞 UI），完成后回调（回调在后台线程，调用方需自行切回 UI 线程）。
        /// </summary>
        /// <summary>
        /// 后台线程扫描（不阻塞 UI），完成后回调（回调在后台线程，调用方需自行切回 UI 线程）。
        /// 若已有扫描在进行，则本次不再另起线程，只把回调挂到正在跑的那次之后
        /// （设备变动会连续冒消息，必须合并，否则会并行开多次枚举）。
        /// </summary>
        public static void ScanNowAsync(Action onDone)
        {
            lock (scanLock)
            {
                if (scanRunning)
                {
                    if (onDone != null) pendingCallbacks += onDone;
                    return;
                }
                scanRunning = true;
            }

            System.Threading.Thread t = new System.Threading.Thread(delegate()
            {
                try
                {
                    ScanNow();
                }
                finally
                {
                    Action mine = onDone;
                    Action extra;
                    lock (scanLock)
                    {
                        scanRunning = false;
                        extra = pendingCallbacks;
                        pendingCallbacks = null;
                    }
                    if (mine != null) { try { mine(); } catch { } }
                    if (extra != null) { try { extra(); } catch { } }
                }
            });
            t.IsBackground = true;
            t.Name = "DeviceBatteryScan";
            t.Start();
        }

        private static readonly object scanLock = new object();
        private static bool scanRunning = false;
        private static Action pendingCallbacks = null;

        private static List<BatteryDevice> Scan()
        {
            List<BatteryDevice> list = new List<BatteryDevice>();

            // ① 蓝牙等标准设备：走 DEVPKEY_Device_BatteryLevel。
            //    该属性在设备断开后不会清空（实测残留），所以还要确认设备是否真的在线。
            //    注意：CollectBatteryDevices 返回的是"每个设备节点一条"（不按容器去重），
            //    去重必须放在"在线过滤"之后 —— 否则同一容器里先枚举到的已断开节点，
            //    会把后面真正在线的节点顶掉（BLE 键盘同时有 BTHLE 与 HID 节点就是这种情形）。
            List<BatteryDevice> candidates = CollectBatteryDevices();
            if (candidates.Count > 0)
            {
                HashSet<Guid> online = CollectOnlineContainers();
                Dictionary<string, bool> btConnected = null; // 惰性：只有遇到蓝牙设备才去枚举
                HashSet<string> seenContainers = new HashSet<string>();
                for (int i = 0; i < candidates.Count; i++)
                {
                    BatteryDevice dev = candidates[i];

                    // 先看"设备节点自身是否被标记为已断开"。
                    // 蓝牙 LE 设备在主机侧被关掉（或带离）后，节点仍然 Present=True（GATT 子节点也都在），
                    // 而经典蓝牙枚举 API 又列不出"仅 BLE 配对"的设备 —— 这个状态位是唯一可靠的判据。
                    if (IsBluetoothInstanceId(dev.InstanceId) && (dev.DevNodeStatus & DN_DEVICE_DISCONNECTED) != 0)
                    {
                        Logger.Info("跳过未连接的蓝牙设备: " + dev.Name + " = " + dev.Level + "%（设备状态为已断开）");
                        continue;
                    }

                    // 蓝牙设备：优先问蓝牙层"现在是否真的连着"。
                    // 系统电量属性在断开后不会清空；而"同容器还有没有在线接口"这条判据
                    // 对蓝牙耳机有效（音频端点会消失），对蓝牙键盘/鼠标则不一定（HID 节点可能残留）。
                    // 所以对"已配对但未连接"的设备，必须靠蓝牙状态来拦。
                    string mac = ExtractBluetoothAddress(dev.InstanceId);
                    if (mac != null)
                    {
                        if (btConnected == null) btConnected = GetBluetoothConnectedMap();
                        bool connected;
                        if (btConnected.TryGetValue(mac, out connected))
                        {
                            if (!connected)
                            {
                                Logger.Info("跳过未连接的蓝牙设备: " + dev.Name + " = " + dev.Level + "%（已配对、当前未连接）");
                                continue;
                            }
                        }
                        // 该设备地址不在蓝牙列表里（例如仅 BLE 配对）→ 不在这里拦，继续往下判断
                    }
                    else if (dev.ContainerId != Guid.Empty && !online.Contains(dev.ContainerId))
                    {
                        Logger.Info("跳过已断开设备的残留电量: " + dev.Name + " = " + dev.Level + "%");
                        continue;
                    }

                    // 同一个物理设备可能有多个设备节点（蓝牙经典 + BLE + 各配置文件名），按容器去重，
                    // 保留第一个通过在线检查的节点。
                    if (dev.ContainerId != Guid.Empty && !seenContainers.Add(dev.ContainerId.ToString()))
                        continue;

                    list.Add(dev);
                }
            }

            // ② 2.4G 私有协议设备：电量夹带在周期性 HID Input 报告中，走协议表。
            //    HID 枚举本身用的是 DIGCF_PRESENT，只会列出当前在线的接口，天然无需在线过滤。
            List<BatteryDevice> hid = CollectHidBatteryDevices();
            if (hid.Count > 0) list.AddRange(hid);

            return list;
        }

        /// <summary>
        /// 诊断（--test-btdump）：把每个"带电量属性"的设备节点原样打印出来 ——
        /// 不去重、不做在线过滤，并同时读 DevNodeStatus 的两种 pid（1 / 2）与容器在线情况，
        /// 用来确定"设备已断开"到底该看哪个属性、哪一位。
        /// </summary>
        internal static void DumpBatteryCandidates()
        {
            IntPtr devInfoSet = SetupDiGetClassDevs(IntPtr.Zero, null, IntPtr.Zero, DIGCF_PRESENT | DIGCF_ALLCLASSES);
            if (devInfoSet == IntPtr.Zero || devInfoSet == new IntPtr(-1))
            {
                Logger.Info("枚举设备失败");
                return;
            }

            HashSet<Guid> online = CollectOnlineContainers();
            Dictionary<string, bool> bt = GetBluetoothConnectedMap();
            Logger.Info("在线容器 " + online.Count + " 个；蓝牙已配对设备 " + bt.Count + " 个（含未连接）");

            int count = 0;
            try
            {
                for (uint index = 0; ; index++)
                {
                    SP_DEVINFO_DATA info = new SP_DEVINFO_DATA();
                    info.cbSize = Marshal.SizeOf(typeof(SP_DEVINFO_DATA));
                    if (!SetupDiEnumDeviceInfo(devInfoSet, index, ref info)) break;

                    object levelObj = ReadProperty(devInfoSet, ref info, GUID_BatteryLevel, PID_BatteryLevel);
                    if (levelObj == null) continue;

                    int level;
                    try { level = Convert.ToInt32(levelObj); }
                    catch { continue; }

                    string instanceId = ReadInstanceId(devInfoSet, ref info);
                    string name = ReadProperty(devInfoSet, ref info, GUID_Device, PID_FriendlyName) as string;
                    string cls = ReadProperty(devInfoSet, ref info, GUID_Device, PID_DeviceClass) as string;

                    Guid containerId = Guid.Empty;
                    object cidObj = ReadProperty(devInfoSet, ref info, GUID_ContainerId, PID_ContainerId);
                    if (cidObj is Guid) containerId = (Guid)cidObj;

                    string s1 = PropText(devInfoSet, ref info, GUID_DeviceStatus, 1);
                    string s2 = PropText(devInfoSet, ref info, GUID_DeviceStatus, 2);

                    string mac = ExtractBluetoothAddress(instanceId);
                    string btText = "非蓝牙";
                    if (mac != null)
                    {
                        bool conn;
                        btText = bt.TryGetValue(mac, out conn) ? mac + " → " + (conn ? "已连接" : "未连接") : mac + " → 不在蓝牙列表";
                    }

                    count++;
                    Logger.Info("--- [" + count + "] " + CleanName(name) + "（" + cls + "）= " + level + "%");
                    Logger.Info("    InstanceId: " + instanceId);
                    Logger.Info("    Container : " + (containerId == Guid.Empty ? "(空)" : containerId.ToString())
                        + (containerId != Guid.Empty && online.Contains(containerId) ? "  [在线]" : "  [不在线]"));
                    Logger.Info("    Status512 : pid1=" + s1 + "  pid2=" + s2);
                    Logger.Info("    蓝牙      : " + btText);
                }
            }
            finally
            {
                SetupDiDestroyDeviceInfoList(devInfoSet);
            }
            Logger.Info("=== 共 " + count + " 个带电量属性的设备节点 ===");
        }

        private static string PropText(IntPtr devInfoSet, ref SP_DEVINFO_DATA info, Guid fmtid, uint pid)
        {
            object v = ReadProperty(devInfoSet, ref info, fmtid, pid);
            if (v == null) return "(无)";
            try
            {
                return Convert.ToInt32(v).ToString() + " (0x" + Convert.ToUInt32(v).ToString("X8") + ")";
            }
            catch { return v.ToString(); }
        }

        /// <summary>
        /// 遍历全部设备，收集"带电量属性"的候选（此时还不判断是否在线、也不按容器去重）。
        /// 去重放到调用方（Scan）的在线过滤之后做，见那边的注释。
        /// </summary>
        private static List<BatteryDevice> CollectBatteryDevices()
        {
            List<BatteryDevice> list = new List<BatteryDevice>();
            IntPtr devInfoSet = SetupDiGetClassDevs(IntPtr.Zero, null, IntPtr.Zero, DIGCF_PRESENT | DIGCF_ALLCLASSES);
            if (devInfoSet == IntPtr.Zero || devInfoSet == new IntPtr(-1)) return list;

            try
            {
                for (uint index = 0; ; index++)
                {
                    SP_DEVINFO_DATA info = new SP_DEVINFO_DATA();
                    info.cbSize = Marshal.SizeOf(typeof(SP_DEVINFO_DATA));
                    if (!SetupDiEnumDeviceInfo(devInfoSet, index, ref info)) break;

                    // 先只读电量（一次调用），没有就跳过，避免对上百个无关设备做多余查询
                    object levelObj = ReadProperty(devInfoSet, ref info, GUID_BatteryLevel, PID_BatteryLevel);
                    if (levelObj == null) continue;

                    int level;
                    try { level = Convert.ToInt32(levelObj); }
                    catch { continue; }
                    if (level < 0 || level > 100) continue;

                    Guid containerId = Guid.Empty;
                    object cidObj = ReadProperty(devInfoSet, ref info, GUID_ContainerId, PID_ContainerId);
                    if (cidObj is Guid) containerId = (Guid)cidObj;

                    string name = ReadProperty(devInfoSet, ref info, GUID_Device, PID_FriendlyName) as string;
                    string cls = ReadProperty(devInfoSet, ref info, GUID_Device, PID_DeviceClass) as string;

                    BatteryDevice dev = new BatteryDevice();
                    dev.Name = CleanName(name);
                    dev.Category = CategoryText(cls);
                    dev.Level = level;
                    dev.ContainerId = containerId;
                    dev.InstanceId = ReadInstanceId(devInfoSet, ref info);

                    object statusObj = ReadProperty(devInfoSet, ref info, GUID_DeviceStatus, PID_DevNodeStatus);
                    if (statusObj != null)
                    {
                        try { dev.DevNodeStatus = Convert.ToInt32(statusObj); }
                        catch { dev.DevNodeStatus = 0; }
                    }

                    list.Add(dev);
                }
            }
            finally
            {
                SetupDiDestroyDeviceInfoList(devInfoSet);
            }
            return list;
        }

        /// <summary>
        /// 一个"可配置"的 HID 接口（供「添加设备」界面选择用）。
        /// 只保留有 Input 或 Feature 报告的接口（纯按键接口会被过滤，避免列表太杂）。
        /// </summary>
        internal class HidInterfaceInfo
        {
            public string Path;
            public ushort Vid, Pid;
            public string Product;
            public ushort UsagePage, Usage;
            public int InputLen, FeatureLen;

            public string DisplayName
            {
                get
                {
                    string s = (Product == null || Product.Length == 0) ? "未知设备" : Product;
                    s += "　" + Vid.ToString("X4") + ":" + Pid.ToString("X4");
                    s += "　UsagePage 0x" + UsagePage.ToString("X2");
                    if (InputLen > 0) s += "　Input " + InputLen + " 字节";
                    if (FeatureLen > 0) s += "　Feature " + FeatureLen + " 字节";
                    return s;
                }
            }
        }

        /// <summary>枚举 HID 设备接口，匹配协议表，读取周期性上报的电量。</summary>
        private static List<BatteryDevice> CollectHidBatteryDevices()
        {
            List<BatteryDevice> list = new List<BatteryDevice>();
            Guid hidGuid;
            HidD_GetHidGuid(out hidGuid);

            IntPtr set = SetupDiGetClassDevs(ref hidGuid, null, IntPtr.Zero, DIGCF_PRESENT | DIGCF_DEVICEINTERFACE);
            if (set == IntPtr.Zero || set == new IntPtr(-1)) return list;

            try
            {
                for (uint idx = 0; ; idx++)
                {
                    SP_DEVICE_INTERFACE_DATA ifdata = new SP_DEVICE_INTERFACE_DATA();
                    ifdata.cbSize = Marshal.SizeOf(typeof(SP_DEVICE_INTERFACE_DATA));
                    if (!SetupDiEnumDeviceInterfaces(set, IntPtr.Zero, ref hidGuid, idx, ref ifdata)) break;

                    uint required = 0;
                    SetupDiGetDeviceInterfaceDetail(set, ref ifdata, IntPtr.Zero, 0, out required, IntPtr.Zero);
                    if (required == 0) continue;

                    IntPtr detail = Marshal.AllocHGlobal((int)required);
                    try
                    {
                        Marshal.WriteInt32(detail, 8); // SP_DEVICE_INTERFACE_DETAIL_DATA.cbSize
                        if (!SetupDiGetDeviceInterfaceDetail(set, ref ifdata, detail, required, out required, IntPtr.Zero))
                            continue;
                        string path = Marshal.PtrToStringUni(detail + 4); // DevicePath 在 cbSize 之后

                        SafeFileHandle h = CreateFile(path, GENERIC_READ | GENERIC_WRITE,
                            FILE_SHARE_READ | FILE_SHARE_WRITE, IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero);
                        if (h.IsInvalid) continue;

                        HIDD_ATTRIBUTES attr = new HIDD_ATTRIBUTES();
                        attr.Size = Marshal.SizeOf(typeof(HIDD_ATTRIBUTES));
                        if (!HidD_GetAttributes(h, ref attr)) { h.Dispose(); continue; }

                        // 按 VID:PID 匹配协议表（来自 battery_protocols.json）
                        BatteryProtocol proto = null;
                        List<BatteryProtocol> protos = BatteryProtocolStore.Protocols;
                        for (int p = 0; p < protos.Count; p++)
                        {
                            if (protos[p].Vid == (int)attr.VendorID && protos[p].Pid == (int)attr.ProductID)
                            { proto = protos[p]; break; }
                        }
                        if (proto == null) { h.Dispose(); continue; }

                        // 确认接口 UsagePage 匹配（一个接收器有多个接口，只认电量所在的那个）
                        ushort usagePage = 0;
                        int inputLen = 0;
                        int featureLen = 0;
                        IntPtr pp = IntPtr.Zero;
                        if (HidD_GetPreparsedData(h, out pp) && pp != IntPtr.Zero)
                        {
                            try
                            {
                                IntPtr capBuf = Marshal.AllocHGlobal(64);
                                try
                                {
                                    int st = HidP_GetCaps(pp, capBuf);
                                    if ((st & 0xFFFF0000) == 0x00110000) // HIDP_STATUS_SUCCESS
                                    {
                                        usagePage = (ushort)Marshal.ReadInt16(capBuf, 2);
                                        inputLen = Marshal.ReadInt16(capBuf, 4);
                                        featureLen = Marshal.ReadInt16(capBuf, 8);
                                    }
                                }
                                finally { Marshal.FreeHGlobal(capBuf); }
                            }
                            finally { HidD_FreePreparsedData(pp); }
                        }
                        h.Dispose();

                        if ((int)usagePage != proto.UsagePage) continue;

                        // 按协议表指定的方式读电量：Feature 报告（查询式）或 Input 报告（主动上报）
                        int level;
                        if (proto.UseFeature)
                        {
                            if (featureLen <= 0) continue;
                            level = ReadHidFeatureBattery(path, featureLen, (byte)proto.FeatureReportId, proto.BatteryByte);
                        }
                        else
                        {
                            if (inputLen <= 0) continue;
                            level = ReadHidBattery(path, inputLen, proto.BatteryByte);
                        }
                        if (level < 0) continue;

                        BatteryDevice dev = new BatteryDevice();
                        dev.Name = proto.Name;
                        dev.Category = "2.4G";
                        dev.Level = level;
                        dev.InstanceId = "";

                        // 查询式设备：接收器用缓存值应答，读到的可能不是"此刻"的数值。
                        // 记下这个值从何时起没变过，界面上如实标出来（不假装它是实时的）。
                        if (proto.UseFeature)
                        {
                            dev.ValueMayBeStale = true;
                            dev.ValueLastChanged = BatteryValueTracker.Note(ValueKey(proto), level);
                        }

                        list.Add(dev);
                    }
                    finally { Marshal.FreeHGlobal(detail); }
                }
            }
            finally { SetupDiDestroyDeviceInfoList(set); }

            return list;
        }

        /// <summary>
        /// 打开 HID 设备，在后台线程里用阻塞 ReadFile 读 Input 报告（最长等 6 秒），
        /// 循环读直到拿到 0~100 的电量字节；读不到 / 超时 / 越界返回 -1。
        /// 超时通过关闭句柄来强制中断阻塞中的 ReadFile。
        /// </summary>
        private static int ReadHidBattery(string path, int reportLen, int batteryByte)
        {
            SafeFileHandle h = CreateFile(path, GENERIC_READ | GENERIC_WRITE,
                FILE_SHARE_READ | FILE_SHARE_WRITE, IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero);
            if (h.IsInvalid) return -1;
            try
            {
                int result = -1;
                using (System.Threading.ManualResetEvent done = new System.Threading.ManualResetEvent(false))
                {
                    System.Threading.Thread t = new System.Threading.Thread(delegate()
                    {
                        try
                        {
                            byte[] buf = new byte[Math.Max(reportLen, 64)];
                            // 设备约每 2 秒报一次；循环读，最多等 6 秒，找第一个 0~100 的有效电量
                            DateTime deadline = DateTime.Now.AddSeconds(6);
                            while (DateTime.Now < deadline && result < 0)
                            {
                                uint read = 0;
                                if (!ReadFile(h, buf, (uint)buf.Length, out read, IntPtr.Zero)) break;
                                if ((int)read > batteryByte)
                                {
                                    int v = buf[batteryByte];
                                    if (v >= 0 && v <= 100) result = v;
                                }
                            }
                            if (result >= 0)
                                Logger.Info("读到 2.4G HID 电量（字节偏移 " + batteryByte + "）= " + result + "%");
                        }
                        catch { }
                        // 主线程超时后会 Dispose 这个事件对象；此后后台线程再 Set 会抛
                        // ObjectDisposedException —— 它发生在没人捕获的后台线程，会让整个
                        // 进程直接终止（表现为"程序突然报错然后自己消失"），必须包住。
                        try { done.Set(); } catch { }
                    });
                    t.IsBackground = true;
                    t.Start();

                    if (!done.WaitOne(7000))
                    {
                        try { h.Dispose(); } catch { } // 关闭句柄，强制中断阻塞中的 ReadFile
                        done.WaitOne(500);
                        return -1;
                    }
                    return result;
                }
            }
            finally { h.Dispose(); }
        }

        /// <summary>用 HidD_GetFeature 读 Feature 报告，取指定偏移的电量字节；失败/越界返回 -1。</summary>
        private static int ReadHidFeatureBattery(string path, int featureLen, byte reportId, int batteryByte)
        {
            SafeFileHandle h = CreateFile(path, GENERIC_READ | GENERIC_WRITE,
                FILE_SHARE_READ | FILE_SHARE_WRITE, IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero);
            if (h.IsInvalid) return -1;
            try
            {
                byte[] buf = new byte[Math.Max(featureLen, 16)];
                buf[0] = reportId;
                if (!HidD_GetFeature(h, buf, (uint)buf.Length)) return -1;
                if (batteryByte < 0 || batteryByte >= buf.Length) return -1;
                int v = buf[batteryByte];
                if (v < 0 || v > 100) return -1;
                return v;
            }
            finally { h.Dispose(); }
        }

        // ==================== 供「添加设备」界面使用的公开方法 ====================

        /// <summary>枚举所有有 Input 或 Feature 报告的 HID 接口（供配置界面选择）。</summary>
        public static List<HidInterfaceInfo> EnumerateInterfaces()
        {
            List<HidInterfaceInfo> list = new List<HidInterfaceInfo>();
            Guid hidGuid;
            HidD_GetHidGuid(out hidGuid);
            IntPtr set = SetupDiGetClassDevs(ref hidGuid, null, IntPtr.Zero, DIGCF_PRESENT | DIGCF_DEVICEINTERFACE);
            if (set == IntPtr.Zero || set == new IntPtr(-1)) return list;

            try
            {
                for (uint idx = 0; ; idx++)
                {
                    SP_DEVICE_INTERFACE_DATA ifdata = new SP_DEVICE_INTERFACE_DATA();
                    ifdata.cbSize = Marshal.SizeOf(typeof(SP_DEVICE_INTERFACE_DATA));
                    if (!SetupDiEnumDeviceInterfaces(set, IntPtr.Zero, ref hidGuid, idx, ref ifdata)) break;

                    uint required = 0;
                    SetupDiGetDeviceInterfaceDetail(set, ref ifdata, IntPtr.Zero, 0, out required, IntPtr.Zero);
                    if (required == 0) continue;

                    IntPtr detail = Marshal.AllocHGlobal((int)required);
                    try
                    {
                        Marshal.WriteInt32(detail, 8);
                        if (!SetupDiGetDeviceInterfaceDetail(set, ref ifdata, detail, required, out required, IntPtr.Zero)) continue;
                        string path = Marshal.PtrToStringUni(detail + 4);

                        SafeFileHandle h = CreateFile(path, GENERIC_READ | GENERIC_WRITE, FILE_SHARE_READ | FILE_SHARE_WRITE, IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero);
                        if (h.IsInvalid) continue;

                        HIDD_ATTRIBUTES attr = new HIDD_ATTRIBUTES();
                        attr.Size = Marshal.SizeOf(typeof(HIDD_ATTRIBUTES));
                        if (!HidD_GetAttributes(h, ref attr)) { h.Dispose(); continue; }

                        HidInterfaceInfo info = new HidInterfaceInfo();
                        info.Path = path;
                        info.Vid = attr.VendorID;
                        info.Pid = attr.ProductID;

                        System.Text.StringBuilder sb = new System.Text.StringBuilder(256);
                        if (HidD_GetProductString(h, sb, 256)) info.Product = sb.ToString();

                        IntPtr pp = IntPtr.Zero;
                        if (HidD_GetPreparsedData(h, out pp) && pp != IntPtr.Zero)
                        {
                            try
                            {
                                IntPtr capBuf = Marshal.AllocHGlobal(64);
                                try
                                {
                                    int st = HidP_GetCaps(pp, capBuf);
                                    if ((st & 0xFFFF0000) == 0x00110000)
                                    {
                                        info.Usage = (ushort)Marshal.ReadInt16(capBuf, 0);
                                        info.UsagePage = (ushort)Marshal.ReadInt16(capBuf, 2);
                                        info.InputLen = Marshal.ReadInt16(capBuf, 4);
                                        info.FeatureLen = Marshal.ReadInt16(capBuf, 8);
                                    }
                                }
                                finally { Marshal.FreeHGlobal(capBuf); }
                            }
                            finally { HidD_FreePreparsedData(pp); }
                        }
                        h.Dispose();

                        // 只保留有报告能力的接口（纯按键接口对配置没有意义，过滤掉让列表更清爽）
                        if (info.InputLen <= 0 && info.FeatureLen <= 0) continue;
                        list.Add(info);
                    }
                    finally { Marshal.FreeHGlobal(detail); }
                }
            }
            finally { SetupDiDestroyDeviceInfoList(set); }

            return list;
        }

        /// <summary>读一条 Input 报告（阻塞，最长 6 秒）；失败返回 null。</summary>
        public static byte[] ReadInputReport(string path, int reportLen)
        {
            SafeFileHandle h = CreateFile(path, GENERIC_READ | GENERIC_WRITE,
                FILE_SHARE_READ | FILE_SHARE_WRITE, IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero);
            if (h.IsInvalid) return null;
            try
            {
                byte[] result = null;
                using (System.Threading.ManualResetEvent done = new System.Threading.ManualResetEvent(false))
                {
                    System.Threading.Thread t = new System.Threading.Thread(delegate()
                    {
                        try
                        {
                            byte[] buf = new byte[Math.Max(reportLen, 16)];
                            uint read = 0;
                            if (ReadFile(h, buf, (uint)buf.Length, out read, IntPtr.Zero) && read > 0)
                            {
                                result = new byte[(int)read];
                                Array.Copy(buf, result, (int)read);
                            }
                        }
                        catch { }
                        // 主线程超时后会 Dispose 这个事件对象；此后后台线程再 Set 会抛
                        // ObjectDisposedException —— 它发生在没人捕获的后台线程，会让整个
                        // 进程直接终止（表现为"程序突然报错然后自己消失"），必须包住。
                        try { done.Set(); } catch { }
                    });
                    t.IsBackground = true;
                    t.Start();

                    if (!done.WaitOne(6000))
                    {
                        try { h.Dispose(); } catch { }
                        done.WaitOne(500);
                        return null;
                    }
                    return result;
                }
            }
            finally { h.Dispose(); }
        }

        /// <summary>读 Feature 报告（同步）；失败返回 null。</summary>
        public static byte[] ReadFeatureReport(string path, int featureLen, int reportId)
        {
            SafeFileHandle h = CreateFile(path, GENERIC_READ | GENERIC_WRITE,
                FILE_SHARE_READ | FILE_SHARE_WRITE, IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero);
            if (h.IsInvalid) return null;
            try
            {
                int len = Math.Max(featureLen, 16);
                byte[] buf = new byte[len];
                buf[0] = (byte)reportId;
                if (!HidD_GetFeature(h, buf, (uint)buf.Length)) return null;
                byte[] result = new byte[featureLen];
                Array.Copy(buf, result, featureLen);
                return result;
            }
            finally { h.Dispose(); }
        }

        /// <summary>遍历 ReportID 1~255，返回"能读出非全零内容"的 ReportID 列表。</summary>
        public static List<int> ProbeFeatureReportIds(string path, int featureLen)
        {
            List<int> ids = new List<int>();
            SafeFileHandle h = CreateFile(path, GENERIC_READ | GENERIC_WRITE,
                FILE_SHARE_READ | FILE_SHARE_WRITE, IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero);
            if (h.IsInvalid) return ids;
            try
            {
                for (int id = 1; id <= 255; id++)
                {
                    byte[] buf = new byte[Math.Max(featureLen, 16)];
                    buf[0] = (byte)id;
                    if (!HidD_GetFeature(h, buf, (uint)buf.Length)) continue;

                    // 过滤全零响应：不少设备对无效 ReportID 也返回成功，但内容全是 0
                    bool allZero = true;
                    for (int i = 1; i < featureLen; i++) if (buf[i] != 0) { allZero = false; break; }
                    if (!allZero) ids.Add(id);
                }
            }
            finally { h.Dispose(); }
            return ids;
        }

        /// <summary>自动搜索命中的一个候选位置。</summary>
        internal class BatteryMatch
        {
            public int ReportId;      // Feature 报告编号；Input 报告为 0
            public bool UseFeature;
            public int ByteIndex;     // 字节索引（0 = 第 1 个字节，通常是 ReportID）
            public int Value;
        }

        /// <summary>自动搜索结果：接口里可读的 ReportID 列表 + 所有命中位置。</summary>
        internal class BatterySearchResult
        {
            public List<int> ReportIds = new List<int>();
            public List<BatteryMatch> Matches = new List<BatteryMatch>();
        }

        /// <summary>
        /// 自动搜索电量位置：Feature 模式下遍历所有可读 ReportID × 所有字节，
        /// Input 模式下读一条报告扫一遍，找出值等于 targetLevel 的所有位置。
        /// 这样用户不用理解 ReportID，输入官方电量即可定位。
        /// </summary>
        public static BatterySearchResult AutoSearchBattery(string path, int inputLen, int featureLen, bool useFeature, int targetLevel)
        {
            BatterySearchResult result = new BatterySearchResult();

            if (useFeature)
            {
                result.ReportIds = ProbeFeatureReportIds(path, featureLen);
                for (int k = 0; k < result.ReportIds.Count; k++)
                {
                    byte[] report = ReadFeatureReport(path, featureLen, result.ReportIds[k]);
                    if (report == null) continue;
                    CollectMatches(report, result.ReportIds[k], true, targetLevel, result.Matches);
                }
            }
            else
            {
                byte[] report = ReadInputReport(path, inputLen);
                if (report != null) CollectMatches(report, 0, false, targetLevel, result.Matches);
            }

            return result;
        }

        // ==================== 智能识别（按「设备」而不是「接口」） ====================

        /// <summary>
        /// 一个物理设备：把它的多个 HID 接口合并在一起。
        /// 用户只需选「哪个设备」，具体接口由程序自己逐个尝试。
        /// </summary>
        internal class HidDeviceGroup
        {
            public string Product;
            public ushort Vid, Pid;
            public List<HidInterfaceInfo> Interfaces = new List<HidInterfaceInfo>();

            public string DisplayName
            {
                get
                {
                    string s = (Product == null || Product.Length == 0) ? "未知设备" : Product;
                    s += "　" + Vid.ToString("X4") + ":" + Pid.ToString("X4");
                    s += "　（" + Interfaces.Count + " 个接口）";
                    return s;
                }
            }
        }

        /// <summary>按 VID:PID + 产品名把接口合并成设备 —— 同名设备只显示一条。</summary>
        public static List<HidDeviceGroup> EnumerateDevices()
        {
            List<HidInterfaceInfo> all = EnumerateInterfaces();
            List<HidDeviceGroup> groups = new List<HidDeviceGroup>();

            for (int i = 0; i < all.Count; i++)
            {
                HidInterfaceInfo info = all[i];
                string name = (info.Product == null || info.Product.Length == 0) ? "未知设备" : info.Product;

                HidDeviceGroup hit = null;
                for (int k = 0; k < groups.Count; k++)
                {
                    if (groups[k].Vid == info.Vid && groups[k].Pid == info.Pid && groups[k].Product == name)
                    {
                        hit = groups[k];
                        break;
                    }
                }
                if (hit == null)
                {
                    hit = new HidDeviceGroup();
                    hit.Product = name;
                    hit.Vid = info.Vid;
                    hit.Pid = info.Pid;
                    groups.Add(hit);
                }
                hit.Interfaces.Add(info);
            }

            // 按名称排序，顺序稳定
            groups.Sort(delegate(HidDeviceGroup a, HidDeviceGroup b)
            {
                int c = string.Compare(a.Product, b.Product, StringComparison.CurrentCulture);
                if (c != 0) return c;
                c = a.Vid.CompareTo(b.Vid);
                if (c != 0) return c;
                return a.Pid.CompareTo(b.Pid);
            });
            return groups;
        }

        /// <summary>一个候选：某个接口、某种报告、某个字节，其值等于用户填写的官方电量。</summary>
        internal class BatteryCandidate
        {
            public string Path;
            public ushort UsagePage;
            public bool UseFeature;
            public int ReportId;
            public int ByteIndex;
            public int Value;
            public string InterfaceLabel;   // 显示用，如 "0xFF04　Feature #6"
        }

        /// <summary>自动识别结果。</summary>
        internal class IdentifyResult
        {
            public List<BatteryCandidate> Candidates = new List<BatteryCandidate>();
            public List<string> TimedOut = new List<string>();   // 没响应（超时）的接口
            public int InterfacesChecked;
            public int InterfacesTotal;
        }

        /// <summary>
        /// 全自动识别：把该设备的**所有接口**都试一遍，收集所有"值等于 targetLevel"的字节。
        ///
        /// · Input 接口**并行**读取（每条最多等 3.5 秒）—— 否则 4 个接口逐个等 6 秒会累加到 20 秒以上；
        /// · Feature 接口逐个探测可读 ReportID 后读取；
        /// · 返回所有候选（满电时可能有多处都是 100），由界面让用户选或自动选中唯一项。
        /// </summary>
        public static IdentifyResult IdentifyBattery(HidDeviceGroup dev, int targetLevel,
            Action<int, int, string> onProgress, Func<bool> isCancelled)
        {
            IdentifyResult result = new IdentifyResult();
            if (dev == null) return result;

            List<HidInterfaceInfo> inputs = new List<HidInterfaceInfo>();
            List<HidInterfaceInfo> features = new List<HidInterfaceInfo>();
            for (int i = 0; i < dev.Interfaces.Count; i++)
            {
                HidInterfaceInfo info = dev.Interfaces[i];
                if (info.InputLen > 0) inputs.Add(info);
                if (info.FeatureLen > 0) features.Add(info);
            }
            result.InterfacesTotal = inputs.Count + features.Count;
            int done = 0;

            // ① Input 接口：并行读
            if (inputs.Count > 0)
            {
                if (onProgress != null)
                    onProgress(0, result.InterfacesTotal, "正在读取 " + inputs.Count + " 个输入接口…");

                // Input 接口的等待预算：设备的周期上报通常 1~2 秒一次，
                // 2.5 秒足够捕获一条；再长就只是白等（全都不报的接口只能靠超时结束）。
                bool[] timedOut = new bool[inputs.Count];
                byte[][] reports = ReadInputsParallel(inputs, 2500, timedOut);
                for (int i = 0; i < inputs.Count; i++)
                {
                    if (isCancelled != null && isCancelled()) return result;
                    if (reports[i] == null)
                    {
                        if (timedOut[i]) result.TimedOut.Add(InterfaceLabel(inputs[i]));
                    }
                    else
                    {
                        CollectCandidates(inputs[i], reports[i], false, 0, targetLevel, result.Candidates);
                    }
                    done++;
                    result.InterfacesChecked = done;
                    if (onProgress != null)
                        onProgress(done, result.InterfacesTotal,
                            "已检查 " + done + "/" + result.InterfacesTotal + " 个接口");
                }
            }

            // ② Feature 接口：逐个探测后读取
            for (int i = 0; i < features.Count; i++)
            {
                if (isCancelled != null && isCancelled()) return result;
                HidInterfaceInfo info = features[i];
                if (onProgress != null)
                    onProgress(done, result.InterfacesTotal, "正在探测 " + InterfaceLabel(info) + " 的可读报告…");

                List<int> ids = ProbeFeatureReportIds(info.Path, info.FeatureLen);
                for (int k = 0; k < ids.Count; k++)
                {
                    if (isCancelled != null && isCancelled()) return result;
                    byte[] report = ReadFeatureReport(info.Path, info.FeatureLen, ids[k]);
                    if (report == null) continue;
                    CollectCandidates(info, report, true, ids[k], targetLevel, result.Candidates);
                }
                done++;
                result.InterfacesChecked = done;
                if (onProgress != null)
                    onProgress(done, result.InterfacesTotal,
                        "已检查 " + done + "/" + result.InterfacesTotal + " 个接口");
            }

            return result;
        }

        private static string InterfaceLabel(HidInterfaceInfo info)
        {
            string kind = (info.FeatureLen > 0 && info.InputLen > 0)
                ? "（Input+Feature）"
                : (info.FeatureLen > 0 ? "（Feature）" : "（Input）");
            return "0x" + info.UsagePage.ToString("X2") + kind;
        }

        /// <summary>把一条报告里等于目标值的字节记为候选（跳过第 0 字节，通常是 ReportID）。</summary>
        private static void CollectCandidates(HidInterfaceInfo info, byte[] report, bool useFeature,
            int reportId, int targetLevel, List<BatteryCandidate> outList)
        {
            for (int i = 1; i < report.Length; i++)
            {
                if (report[i] != targetLevel) continue;
                BatteryCandidate c = new BatteryCandidate();
                c.Path = info.Path;
                c.UsagePage = info.UsagePage;
                c.UseFeature = useFeature;
                c.ReportId = reportId;
                c.ByteIndex = i;
                c.Value = report[i];
                c.InterfaceLabel = "0x" + info.UsagePage.ToString("X2")
                    + (useFeature ? "　Feature #" + reportId : "　Input");
                outList.Add(c);
            }
        }

        /// <summary>
        /// 并行读取多个 Input 接口的报告，总等待不超过 budgetMs。
        ///
        /// 这里用 lock + Monitor.Wait 等待，**不用 ManualResetEvent**：
        /// 后者在超时后若被 Dispose，后台线程再 Set 会抛 ObjectDisposedException，
        /// 而它发生在无人捕获的后台线程 → 直接终止进程（本项目踩过的坑）。
        /// </summary>
        private static byte[][] ReadInputsParallel(List<HidInterfaceInfo> list, int budgetMs, bool[] timedOut)
        {
            int n = list.Count;
            byte[][] results = new byte[n][];
            int pending = n;
            object gate = new object();

            for (int i = 0; i < n; i++)
            {
                int idx = i;
                HidInterfaceInfo info = list[i];
                System.Threading.Thread t = new System.Threading.Thread(delegate()
                {
                    try { results[idx] = ReadInputReport(info.Path, info.InputLen); }
                    catch { }
                    finally
                    {
                        lock (gate)
                        {
                            pending--;
                            System.Threading.Monitor.PulseAll(gate);
                        }
                    }
                });
                t.IsBackground = true;
                t.Name = "IdentifyInput" + idx;
                t.Start();
            }

            DateTime deadline = DateTime.Now.AddMilliseconds(budgetMs);
            lock (gate)
            {
                while (pending > 0)
                {
                    int remain = (int)(deadline - DateTime.Now).TotalMilliseconds;
                    if (remain <= 0) break;
                    System.Threading.Monitor.Wait(gate, remain);
                }
            }

            if (timedOut != null)
                for (int i = 0; i < n; i++) timedOut[i] = (results[i] == null);
            return results;
        }

        /// <summary>扫描报告里的字节，收集所有等于目标值的索引（从 1 开始，跳过 ReportID）。</summary>
        private static void CollectMatches(byte[] report, int reportId, bool useFeature, int targetLevel, List<BatteryMatch> matches)
        {
            for (int i = 1; i < report.Length; i++)
            {
                if (report[i] != targetLevel) continue;
                BatteryMatch m = new BatteryMatch();
                m.ReportId = reportId;
                m.UseFeature = useFeature;
                m.ByteIndex = i;
                m.Value = report[i];
                matches.Add(m);
            }
        }

        /// <summary>读数字段的稳定标识：同一协议的同一字节共用一条"最后变化"记录。</summary>
        private static string ValueKey(BatteryProtocol proto)
        {
            return proto.Vid.ToString("X4") + ":" + proto.Pid.ToString("X4")
                + ":" + proto.UsagePage + ":" + proto.FeatureReportId + ":" + proto.BatteryByte;
        }

        // ==================== 蓝牙"当前是否连接"（Win32 经典蓝牙 API） ====================        // 比"同容器有没有在线接口"更可靠：Windows 明确记录了已配对设备此刻是否连着，
        // 不受"设备节点残留"影响（笔记本被带离键盘就是这种场景）。

        /// <summary>
        /// 枚举已配对蓝牙设备 → 地址(12 位大写十六进制) 到 "当前是否连接" 的映射。
        /// 本机没有蓝牙适配器、或调用失败时返回空表（调用方退回容器判断）。
        /// </summary>
        private static Dictionary<string, bool> GetBluetoothConnectedMap()
        {
            Dictionary<string, bool> map = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
            try
            {
                BLUETOOTH_FIND_RADIO_PARAMS rp = new BLUETOOTH_FIND_RADIO_PARAMS();
                rp.dwSize = (uint)Marshal.SizeOf(typeof(BLUETOOTH_FIND_RADIO_PARAMS));

                IntPtr radio;
                IntPtr hRadioFind = BluetoothFindFirstRadio(ref rp, out radio);
                if (hRadioFind == IntPtr.Zero) return map; // 没有蓝牙无线电

                try
                {
                    BLUETOOTH_DEVICE_SEARCH_PARAMS sp = new BLUETOOTH_DEVICE_SEARCH_PARAMS();
                    sp.dwSize = (uint)Marshal.SizeOf(typeof(BLUETOOTH_DEVICE_SEARCH_PARAMS));
                    sp.fReturnAuthenticated = 1;
                    sp.fReturnRemembered = 1;  // 已配对但未连接的也要列出来（这正是关键）
                    sp.fReturnConnected = 1;
                    sp.fIssueInquiry = 0;      // 不主动搜索，避免几秒等待
                    sp.cTimeoutMultiplier = 0;
                    sp.hRadio = radio;

                    BLUETOOTH_DEVICE_INFO info = new BLUETOOTH_DEVICE_INFO();
                    info.dwSize = (uint)Marshal.SizeOf(typeof(BLUETOOTH_DEVICE_INFO));

                    IntPtr hFind = BluetoothFindFirstDevice(ref sp, ref info);
                    if (hFind == IntPtr.Zero) return map;
                    try
                    {
                        do
                        {
                            map[info.Address.ToString("X12")] = info.fConnected != 0;
                            info = new BLUETOOTH_DEVICE_INFO();
                            info.dwSize = (uint)Marshal.SizeOf(typeof(BLUETOOTH_DEVICE_INFO));
                        }
                        while (BluetoothFindNextDevice(hFind, ref info));
                    }
                    finally { BluetoothFindDeviceClose(hFind); }
                }
                finally { BluetoothFindRadioClose(hRadioFind); }
            }
            catch (Exception ex)
            {
                Logger.Info("读取蓝牙连接状态失败（退回容器判断）: " + ex.Message);
            }
            return map;
        }

        /// <summary>是否蓝牙设备节点（经典 BTHENUM 或低功耗 BTHLE）。</summary>
        private static bool IsBluetoothInstanceId(string instanceId)
        {
            if (string.IsNullOrEmpty(instanceId)) return false;
            return instanceId.StartsWith("BTHENUM", StringComparison.OrdinalIgnoreCase)
                || instanceId.StartsWith("BTHLE", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// 从设备实例 ID 里取蓝牙设备地址。非蓝牙设备返回 null。
        /// 例：BTHENUM\{0000111E-...}_LOCALMFG&amp;0002\7&amp;354AEDDC&amp;0&amp;D498B9E04807_C00000000 → D498B9E04807
        /// </summary>
        private static string ExtractBluetoothAddress(string instanceId)
        {
            if (string.IsNullOrEmpty(instanceId)) return null;
            if (instanceId.IndexOf("BTHENUM", StringComparison.OrdinalIgnoreCase) < 0
                && instanceId.IndexOf("BTHLE", StringComparison.OrdinalIgnoreCase) < 0)
                return null;

            System.Text.RegularExpressions.Match m = System.Text.RegularExpressions.Regex.Match(
                instanceId, "(?:DEV_|0&)([0-9A-Fa-f]{12})");
            return m.Success ? m.Groups[1].Value.ToUpperInvariant() : null;
        }

        /// <summary>
        /// 收集"当前存在功能接口"的设备容器 ID。
        /// 这些接口只在设备真正连接时才存在，设备断开即消失，所以是判断在线与否的可靠依据。
        /// </summary>
        private static HashSet<Guid> CollectOnlineContainers()
        {
            HashSet<Guid> online = new HashSet<Guid>();
            for (int i = 0; i < FunctionalClasses.Length; i++)
            {
                Guid classGuid = FunctionalClasses[i];
                IntPtr set = SetupDiGetClassDevs(ref classGuid, null, IntPtr.Zero, DIGCF_PRESENT);
                if (set == IntPtr.Zero || set == new IntPtr(-1)) continue;

                try
                {
                    for (uint index = 0; ; index++)
                    {
                        SP_DEVINFO_DATA info = new SP_DEVINFO_DATA();
                        info.cbSize = Marshal.SizeOf(typeof(SP_DEVINFO_DATA));
                        if (!SetupDiEnumDeviceInfo(set, index, ref info)) break;

                        object cid = ReadProperty(set, ref info, GUID_ContainerId, PID_ContainerId);
                        if (cid is Guid) online.Add((Guid)cid);
                    }
                }
                finally
                {
                    SetupDiDestroyDeviceInfoList(set);
                }
            }
            return online;
        }

        /// <summary>去掉蓝牙配置文件名后缀，显示更干净（"iQOO TWS 2 Hands-Free AG" → "iQOO TWS 2"）。</summary>
        private static string CleanName(string raw)
        {
            if (string.IsNullOrEmpty(raw)) return "（未命名设备）";
            string s = System.Text.RegularExpressions.Regex.Replace(raw,
                @"\s+(Hands-Free AG|Hands-Free HF|Avrcp 传输|A2DP|Audio Sink|Audio Source)$",
                "", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            s = s.Trim();
            return s.Length == 0 ? raw : s;
        }

        private static string CategoryText(string cls)
        {
            if (string.IsNullOrEmpty(cls)) return "设备";
            switch (cls.ToLowerInvariant())
            {
                case "bluetooth": return "蓝牙";
                case "mouse": return "鼠标";
                case "keyboard": return "键盘";
                case "hidclass": return "HID";
                case "xboxcomposite":
                case "xnacomposite": return "手柄";
                case "media":
                case "audioendpoint": return "音频";
                default: return cls;
            }
        }

        private static string ReadInstanceId(IntPtr devInfoSet, ref SP_DEVINFO_DATA info)
        {
            System.Text.StringBuilder sb = new System.Text.StringBuilder(512);
            int required;
            if (SetupDiGetDeviceInstanceId(devInfoSet, ref info, sb, sb.Capacity, out required))
                return sb.ToString();
            return "";
        }

        /// <summary>读一个设备属性；不存在或不支持时返回 null。</summary>
        private static object ReadProperty(IntPtr devInfoSet, ref SP_DEVINFO_DATA info, Guid fmtid, uint pid)
        {
            DEVPROPKEY key = new DEVPROPKEY();
            key.fmtid = fmtid;
            key.pid = pid;

            uint type;
            int size;
            // 第一次只为拿所需缓冲区大小（会返回 false，属正常）
            SetupDiGetDeviceProperty(devInfoSet, ref info, ref key, out type, IntPtr.Zero, 0, out size, 0);
            if (size <= 0 || size > 8192) return null;

            IntPtr buffer = Marshal.AllocHGlobal(size);
            try
            {
                if (!SetupDiGetDeviceProperty(devInfoSet, ref info, ref key, out type, buffer, size, out size, 0))
                    return null;

                switch (type)
                {
                    case 0x00000003: // DEVPROP_TYPE_BYTE
                        return (int)Marshal.ReadByte(buffer);
                    case 0x00000007: // DEVPROP_TYPE_UINT32
                        return Marshal.ReadInt32(buffer);
                    case 0x00000012: // DEVPROP_TYPE_STRING
                        return Marshal.PtrToStringUni(buffer);
                    case 0x0000000D: // DEVPROP_TYPE_GUID
                        return (Guid)Marshal.PtrToStructure(buffer, typeof(Guid));
                    default:
                        return null;
                }
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }

        // ==================== SetupAPI ====================

        [StructLayout(LayoutKind.Sequential)]
        private struct SP_DEVINFO_DATA
        {
            public int cbSize;
            public Guid ClassGuid;
            public int DevInst;
            public IntPtr Reserved;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct DEVPROPKEY
        {
            public Guid fmtid;
            public uint pid;
        }

        [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr SetupDiGetClassDevs(IntPtr classGuid, string enumerator, IntPtr hwndParent, uint flags);

        [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr SetupDiGetClassDevs(ref Guid classGuid, string enumerator, IntPtr hwndParent, uint flags);

        [DllImport("setupapi.dll", SetLastError = true)]
        private static extern bool SetupDiEnumDeviceInfo(IntPtr deviceInfoSet, uint memberIndex, ref SP_DEVINFO_DATA deviceInfoData);

        [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool SetupDiGetDeviceInstanceId(IntPtr deviceInfoSet, ref SP_DEVINFO_DATA deviceInfoData,
            System.Text.StringBuilder deviceInstanceId, int deviceInstanceIdSize, out int requiredSize);

        [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool SetupDiGetDeviceProperty(IntPtr deviceInfoSet, ref SP_DEVINFO_DATA deviceInfoData,
            ref DEVPROPKEY propertyKey, out uint propertyType, IntPtr propertyBuffer, int propertyBufferSize,
            out int requiredSize, uint flags);

        [DllImport("setupapi.dll", SetLastError = true)]
        private static extern bool SetupDiDestroyDeviceInfoList(IntPtr deviceInfoSet);

        // ==================== HID（2.4G 电量读取用）====================

        private const uint GENERIC_READ = 0x80000000;
        private const uint GENERIC_WRITE = 0x40000000;
        private const uint FILE_SHARE_READ = 0x00000001;
        private const uint FILE_SHARE_WRITE = 0x00000002;
        private const uint OPEN_EXISTING = 3;
        private const uint DIGCF_DEVICEINTERFACE = 0x00000010;

        [StructLayout(LayoutKind.Sequential)]
        private struct SP_DEVICE_INTERFACE_DATA
        {
            public int cbSize;
            public Guid InterfaceClassGuid;
            public int Flags;
            public IntPtr Reserved;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct HIDD_ATTRIBUTES
        {
            public int Size;
            public ushort VendorID;
            public ushort ProductID;
            public ushort VersionNumber;
        }

        [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool SetupDiEnumDeviceInterfaces(IntPtr deviceInfoSet, IntPtr deviceInfoData,
            ref Guid interfaceClassGuid, uint memberIndex, ref SP_DEVICE_INTERFACE_DATA deviceInterfaceData);

        [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool SetupDiGetDeviceInterfaceDetail(IntPtr deviceInfoSet,
            ref SP_DEVICE_INTERFACE_DATA deviceInterfaceData, IntPtr deviceInterfaceDetailData,
            uint deviceInterfaceDetailDataSize, out uint requiredSize, IntPtr deviceInfoData);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern SafeFileHandle CreateFile(string lpFileName, uint dwDesiredAccess, uint dwShareMode,
            IntPtr lpSecurityAttributes, uint dwCreationDisposition, uint dwFlagsAndAttributes, IntPtr hTemplateFile);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool ReadFile(SafeFileHandle hFile, byte[] lpBuffer, uint nNumberOfBytesToRead,
            out uint lpNumberOfBytesRead, IntPtr lpOverlapped);

        [DllImport("hid.dll", SetLastError = true)]
        private static extern void HidD_GetHidGuid(out Guid hidGuid);

        [return: MarshalAs(UnmanagedType.U1)]
        [DllImport("hid.dll", SetLastError = true)]
        private static extern bool HidD_GetAttributes(SafeFileHandle hFile, ref HIDD_ATTRIBUTES attributes);

        [return: MarshalAs(UnmanagedType.U1)]
        [DllImport("hid.dll", SetLastError = true)]
        private static extern bool HidD_GetPreparsedData(SafeFileHandle hFile, out IntPtr preparsedData);

        [return: MarshalAs(UnmanagedType.U1)]
        [DllImport("hid.dll", SetLastError = true)]
        private static extern bool HidD_FreePreparsedData(IntPtr preparsedData);

        [DllImport("hid.dll", SetLastError = true)]
        private static extern int HidP_GetCaps(IntPtr preparsedData, IntPtr capabilities);

        [return: MarshalAs(UnmanagedType.U1)]
        [DllImport("hid.dll", SetLastError = true)]
        private static extern bool HidD_GetFeature(SafeFileHandle hFile, byte[] reportBuffer, uint reportBufferLength);

        [return: MarshalAs(UnmanagedType.U1)]
        [DllImport("hid.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool HidD_GetProductString(SafeFileHandle hFile, System.Text.StringBuilder buffer, int bufferLength);

        // ==================== Win32 经典蓝牙 API（bthprops.cpl） ====================
        // 注意：BLUETOOTH_DEVICE_INFO 里 szName 是 WCHAR[248]，
        // **结构体必须标 CharSet = CharSet.Unicode**，否则按 ANSI 只算 248 字节、
        // sizeof 会算成 312（正确是 560），API 直接返回 ERROR_NO_MORE_ITEMS(1306)。

        [StructLayout(LayoutKind.Sequential)]
        private struct BLUETOOTH_FIND_RADIO_PARAMS
        {
            public uint dwSize;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct BLUETOOTH_DEVICE_SEARCH_PARAMS
        {
            public uint dwSize;
            public int fReturnAuthenticated;
            public int fReturnRemembered;
            public int fReturnUnknown;
            public int fReturnConnected;
            public int fIssueInquiry;
            public byte cTimeoutMultiplier;
            public IntPtr hRadio;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct BT_SYSTEMTIME
        {
            public ushort wYear, wMonth, wDayOfWeek, wDay, wHour, wMinute, wSecond, wMilliseconds;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct BLUETOOTH_DEVICE_INFO
        {
            public uint dwSize;
            public ulong Address;
            public uint ulClassofDevice;
            public int fConnected;      // 当前是否连接（关键字段）
            public int fRemembered;     // 是否已配对
            public int fAuthenticated;
            public BT_SYSTEMTIME stLastSeen;
            public BT_SYSTEMTIME stLastUsed;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 248)]
            public string szName;
        }

        [DllImport("bthprops.cpl", SetLastError = true)]
        private static extern IntPtr BluetoothFindFirstRadio(ref BLUETOOTH_FIND_RADIO_PARAMS p, out IntPtr phRadio);

        [DllImport("bthprops.cpl", SetLastError = true)]
        private static extern bool BluetoothFindRadioClose(IntPtr hFind);

        [DllImport("bthprops.cpl", SetLastError = true)]
        private static extern IntPtr BluetoothFindFirstDevice(ref BLUETOOTH_DEVICE_SEARCH_PARAMS p, ref BLUETOOTH_DEVICE_INFO info);

        [DllImport("bthprops.cpl", SetLastError = true)]
        private static extern bool BluetoothFindNextDevice(IntPtr hFind, ref BLUETOOTH_DEVICE_INFO info);

        [DllImport("bthprops.cpl", SetLastError = true)]
        private static extern bool BluetoothFindDeviceClose(IntPtr hFind);
    }
}
