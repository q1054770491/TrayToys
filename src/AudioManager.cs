using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace TrayToys
{
    // ==================== Core Audio COM 接口定义 ====================

    internal enum EDataFlow : uint { eRender = 0, eCapture = 1, eAll = 2 }
    internal enum ERole : uint { eConsole = 0, eMultimedia = 1, eCommunications = 2 }

    [Flags]
    internal enum DeviceState : uint { Active = 0x1, Disabled = 0x2, NotPresent = 0x4, Unplugged = 0x8, All = 0xF }

    [Flags]
    internal enum CLSCTX : uint { CLSCTX_INPROC_SERVER = 0x1, CLSCTX_INPROC_HANDLER = 0x2, CLSCTX_LOCAL_SERVER = 0x4, CLSCTX_REMOTE_SERVER = 0x10, CLSCTX_ALL = 0x17 }

    [ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
    internal class MMDeviceEnumerator { }

    [Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IMMDeviceEnumerator
    {
        [PreserveSig] int EnumAudioEndpoints(EDataFlow dataFlow, uint dwStateMask, out IMMDeviceCollection ppDevices);
        [PreserveSig] int GetDefaultAudioEndpoint(EDataFlow dataFlow, ERole role, out IMMDevice ppDevice);
        [PreserveSig] int GetDevice([MarshalAs(UnmanagedType.LPWStr)] string pwstrId, out IMMDevice ppDevice);
        int RegisterEndpointNotificationCallback(IntPtr pClient);
        int UnregisterEndpointNotificationCallback(IntPtr pClient);
    }

    [Guid("0BD7A1BE-7A1A-44DB-8397-CC5392387B5E"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IMMDeviceCollection
    {
        [PreserveSig] int GetCount(out int pcDevices);
        [PreserveSig] int Item(int nDevice, out IMMDevice ppdevice);
    }

    [Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IMMDevice
    {
        [PreserveSig] int Activate(ref Guid iid, CLSCTX dwClsCtx, IntPtr pActivationParams, [MarshalAs(UnmanagedType.IUnknown)] out object ppInterface);
        [PreserveSig] int OpenPropertyStore(uint stgmAccess, out IPropertyStore ppProperties);
        [PreserveSig] int GetId([MarshalAs(UnmanagedType.LPWStr)] out string ppstrId);
        [PreserveSig] int GetState(out uint pdwState);
    }

    [Guid("886D8EEB-8AEC-4DA4-9834-0C8B7706B86C"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IPropertyStore
    {
        [PreserveSig] int GetCount(out int cProps);
        [PreserveSig] int GetAt(int iProp, out PROPERTYKEY pkey);
        [PreserveSig] int GetValue(ref PROPERTYKEY key, out PROPVARIANT pv);
        [PreserveSig] int SetValue(ref PROPERTYKEY key, ref PROPVARIANT pv);
        [PreserveSig] int Commit();
    }

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    internal struct PROPERTYKEY { public Guid fmtid; public uint pid; }

    [StructLayout(LayoutKind.Sequential)]
    internal struct PROPVARIANT
    {
        public ushort vt;
        public ushort wReserved1;
        public ushort wReserved2;
        public ushort wReserved3;
        public IntPtr pwszVal;
    }

    // 设置系统默认音频终结点的非公开 COM 接口（SoundSwitch 等工具同款做法）
    [ComImport, Guid("F8679F50-850A-41CF-9C72-430F290290C8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IPolicyConfig
    {
        [PreserveSig] int GetMixFormat(string pszDeviceName, IntPtr ppFormat);
        [PreserveSig] int GetDeviceFormat(string pszDeviceName, bool bDefault, IntPtr ppFormat);
        [PreserveSig] int ResetDeviceFormat(string pszDeviceName);
        [PreserveSig] int SetDeviceFormat(string pszDeviceName, IntPtr pEndpointFormat, IntPtr pMixFormat);
        [PreserveSig] int GetProcessingPeriod(string pszDeviceName, bool bDefault, IntPtr pmftDefaultPeriod, IntPtr pmftMinimumPeriod);
        [PreserveSig] int SetProcessingPeriod(string pszDeviceName, IntPtr pmftPeriod);
        [PreserveSig] int GetSharedMode(string pszDeviceName, IntPtr pMode);
        [PreserveSig] int SetSharedMode(string pszDeviceName, IntPtr mode);
        [PreserveSig] int GetPropertyValue(string pszDeviceName, bool bFxStore, IntPtr key, IntPtr pv);
        [PreserveSig] int SetPropertyValue(string pszDeviceName, bool bFxStore, IntPtr key, IntPtr pv);
        [PreserveSig] int SetDefaultEndpoint(string pszDeviceName, ERole eRole);
        [PreserveSig] int SetEndpointVisibility(string pszDeviceName, bool bVisible);
    }

    [ComImport, Guid("870AF99C-171D-4F9E-AF0D-E63DF40C2BC9")]
    internal class PolicyConfigClient { }

    // ==================== 设备模型与音频管理 ====================

    internal class AudioDevice
    {
        public string Id;
        public string Name;
        public override string ToString() { return Name; }
    }

    internal static class AudioManager
    {
        // 设备名称对应的注册表属性值（避免依赖 IPropertyStore 的 COM 封送）
        private const string PKEY_DeviceDesc_Value = "{b3f8fa53-0004-438e-9003-51a46e139bfc},6";   // 设备描述，如 "NVIDIA Broadcast"
        private const string PKEY_FriendlyName_Value = "{a45c254e-df1c-4efd-8020-67d146a850e0},14"; // 官方友好名
        private const string PKEY_EndpointName_Value = "{a45c254e-df1c-4efd-8020-67d146a850e0},2";  // 端点显示名，如 "Speakers"

        /// <summary>获取所有“已启用/已插入”的播放（输出/渲染）设备。</summary>
        public static List<AudioDevice> GetPlaybackDevices()
        {
            return GetDevices(EDataFlow.eRender, "Render");
        }

        /// <summary>获取所有“已启用/已插入”的输入（录音/捕获）设备，如麦克风。</summary>
        public static List<AudioDevice> GetRecordingDevices()
        {
            return GetDevices(EDataFlow.eCapture, "Capture");
        }

        private static List<AudioDevice> GetDevices(EDataFlow flow, string flowFolder)
        {
            List<AudioDevice> list = new List<AudioDevice>();
            IMMDeviceEnumerator enumerator = (IMMDeviceEnumerator)(new MMDeviceEnumerator());
            IMMDeviceCollection collection;
            enumerator.EnumAudioEndpoints(flow, (uint)DeviceState.Active, out collection);
            int count;
            collection.GetCount(out count);
            for (int i = 0; i < count; i++)
            {
                IMMDevice device;
                collection.Item(i, out device);
                string id;
                device.GetId(out id);
                AudioDevice ad = new AudioDevice();
                ad.Id = id;
                ad.Name = GetFriendlyName(id, flowFolder);
                list.Add(ad);
            }
            return list;
        }

        /// <summary>获取当前系统默认设备的 Id。input=true 取输入设备，false 取播放设备。</summary>
        public static string GetDefaultDeviceId(bool input)
        {
            EDataFlow flow = input ? EDataFlow.eCapture : EDataFlow.eRender;
            ERole role = input ? ERole.eConsole : ERole.eMultimedia;
            IMMDeviceEnumerator enumerator = (IMMDeviceEnumerator)(new MMDeviceEnumerator());
            IMMDevice device;
            enumerator.GetDefaultAudioEndpoint(flow, role, out device);
            string id;
            device.GetId(out id);
            return id;
        }

        /// <summary>切换默认设备（播放/输入均适用；同时作用于 控制台/多媒体/通信 三种角色）。</summary>
        public static void SetDefaultDevice(string deviceId)
        {
            IPolicyConfig policy = (IPolicyConfig)(new PolicyConfigClient());
            policy.SetDefaultEndpoint(deviceId, ERole.eConsole);
            policy.SetDefaultEndpoint(deviceId, ERole.eMultimedia);
            policy.SetDefaultEndpoint(deviceId, ERole.eCommunications);
        }

        /// <summary>
        /// 读取设备的友好名。
        /// 实现说明：不通过 IPropertyStore 的 COM 接口读取（部分 Win11 上 OpenPropertyStore 的接口封送会抛
        /// E_NOINTERFACE / InvalidCastException），改为直接读注册表
        /// HKLM\...\MMDevices\Audio\{Render|Capture}\{guid}\Properties 中的设备名称属性。
        /// </summary>
        private static string GetFriendlyName(string deviceId, string flowFolder)
        {
            try
            {
                // 设备 Id 形如 {0.0.0.00000000}.{xxxxxxxx-xxxx-xxxx-xxxx-xxxxxxxxxxxx}，
                // 注册表子键名带大括号（如 Render\{xxxxxxxx-...}），因此保留大括号
                int brace = deviceId.LastIndexOf('{');
                if (brace >= 0 && deviceId.EndsWith("}"))
                {
                    string guidPart = deviceId.Substring(brace);
                    string propsKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\MMDevices\Audio\"
                        + flowFolder + @"\" + guidPart + @"\Properties";
                    using (RegistryKey k = Registry.LocalMachine.OpenSubKey(propsKey))
                    {
                        if (k != null)
                        {
                            object v = k.GetValue(PKEY_DeviceDesc_Value);
                            if (v is string && ((string)v).Length > 0) return (string)v;
                            v = k.GetValue(PKEY_FriendlyName_Value);
                            if (v is string && ((string)v).Length > 0) return (string)v;
                            v = k.GetValue(PKEY_EndpointName_Value);
                            if (v is string && ((string)v).Length > 0) return (string)v;
                        }
                    }
                }
            }
            catch { /* 任何失败回落为未知设备 */ }
            return "(未知设备)";
        }
    }
}
