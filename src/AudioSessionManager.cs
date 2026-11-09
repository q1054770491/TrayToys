using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace TrayToys
{
    /// <summary>音频会话状态（对应 AUDIO_SESSION_STATE）。</summary>
    internal enum SessionState
    {
        Inactive = 0,   // 会话存在但没有流在跑（例如视频暂停）
        Active = 1,     // 有音频流正在运行
        Expired = 2     // 会话已结束（应用关闭 / 音频流被销毁）
    }

    // ==================== Core Audio 会话相关 COM 接口 ====================
    // 声明顺序必须与原生 vtable 一致，且继承关系要展开重写，否则 QueryInterface 会失败。

    [ComImport, Guid("77AA99A0-1BD6-484F-8BC7-2C654C9A9B6F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IAudioSessionManager2
    {
        // --- IAudioSessionManager ---
        [PreserveSig] int GetAudioSessionControl(IntPtr audioSessionGuid, uint streamFlags, out IAudioSessionControl sessionControl);
        [PreserveSig] int GetSimpleAudioVolume(IntPtr audioSessionGuid, uint streamFlags, out ISimpleAudioVolume audioVolume);
        // --- IAudioSessionManager2 ---
        [PreserveSig] int GetSessionEnumerator(out IAudioSessionEnumerator sessionEnum);
        [PreserveSig] int RegisterSessionNotification(IntPtr sessionNotification);
        [PreserveSig] int UnregisterSessionNotification(IntPtr sessionNotification);
        [PreserveSig] int RegisterDuckNotification(IntPtr sessionId, IntPtr duckNotification);
        [PreserveSig] int UnregisterDuckNotification(IntPtr duckNotification);
    }

    [ComImport, Guid("E2F5BB11-0570-40CA-ACDD-3AA01277DEE8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IAudioSessionEnumerator
    {
        [PreserveSig] int GetCount(out int sessionCount);
        [PreserveSig] int GetSession(int sessionIndex, out IAudioSessionControl session);
    }

    [ComImport, Guid("F4B1A599-7266-4319-A8CA-E70ACB11E8CD"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IAudioSessionControl
    {
        [PreserveSig] int GetState(out int state);
        [PreserveSig] int GetDisplayName(out IntPtr name);
        [PreserveSig] int SetDisplayName([MarshalAs(UnmanagedType.LPWStr)] string name, IntPtr eventContext);
        [PreserveSig] int GetIconPath(out IntPtr path);
        [PreserveSig] int SetIconPath([MarshalAs(UnmanagedType.LPWStr)] string path, IntPtr eventContext);
        [PreserveSig] int GetGroupingParam(out Guid groupingParam);
        [PreserveSig] int SetGroupingParam(ref Guid groupingParam, IntPtr eventContext);
        [PreserveSig] int RegisterAudioSessionNotification(IntPtr newNotifications);
        [PreserveSig] int UnregisterAudioSessionNotification(IntPtr newNotifications);
    }

    [ComImport, Guid("BFB7FF88-7239-4FC9-8FA2-07C950BE9C6D"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IAudioSessionControl2
    {
        // 前 9 个是 IAudioSessionControl 的方法，必须在同一接口里按原顺序重写
        [PreserveSig] int GetState(out int state);
        [PreserveSig] int GetDisplayName(out IntPtr name);
        [PreserveSig] int SetDisplayName([MarshalAs(UnmanagedType.LPWStr)] string name, IntPtr eventContext);
        [PreserveSig] int GetIconPath(out IntPtr path);
        [PreserveSig] int SetIconPath([MarshalAs(UnmanagedType.LPWStr)] string path, IntPtr eventContext);
        [PreserveSig] int GetGroupingParam(out Guid groupingParam);
        [PreserveSig] int SetGroupingParam(ref Guid groupingParam, IntPtr eventContext);
        [PreserveSig] int RegisterAudioSessionNotification(IntPtr newNotifications);
        [PreserveSig] int UnregisterAudioSessionNotification(IntPtr newNotifications);
        // --- IAudioSessionControl2 ---
        [PreserveSig] int GetSessionIdentifier(out IntPtr retVal);
        [PreserveSig] int GetSessionInstanceIdentifier(out IntPtr retVal);
        [PreserveSig] int GetProcessId(out uint retVal);
        [PreserveSig] int IsSystemSoundsSession();
        [PreserveSig] int SetDuckingPreference([MarshalAs(UnmanagedType.Bool)] bool optOut);
    }

    [ComImport, Guid("87CE5498-68D6-44E5-9215-6DA47EF883D8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface ISimpleAudioVolume
    {
        [PreserveSig] int SetMasterVolume(float level, IntPtr eventContext);
        [PreserveSig] int GetMasterVolume(out float level);
        [PreserveSig] int SetMute([MarshalAs(UnmanagedType.Bool)] bool mute, IntPtr eventContext);
        [PreserveSig] int GetMute([MarshalAs(UnmanagedType.Bool)] out bool mute);
    }

    [ComImport, Guid("5CDF2C82-841E-4546-9722-0CF74078229A"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IAudioEndpointVolume
    {
        [PreserveSig] int RegisterControlChangeNotify(IntPtr notify);
        [PreserveSig] int UnregisterControlChangeNotify(IntPtr notify);
        [PreserveSig] int GetChannelCount(out uint channelCount);
        [PreserveSig] int SetMasterVolumeLevel(float levelDb, IntPtr eventContext);
        [PreserveSig] int SetMasterVolumeLevelScalar(float level, IntPtr eventContext);
        [PreserveSig] int GetMasterVolumeLevel(out float levelDb);
        [PreserveSig] int GetMasterVolumeLevelScalar(out float level);
    }

    /// <summary>一条音频会话（= 音量合成器里的一行）。</summary>
    internal class AudioSessionInfo
    {
        public uint ProcessId;
        public string ExePath;          // 取不到时为 null
        public string ProcessName;      // 显示用名称
        public string SessionIdentifier;
        public float Volume;            // 0.0 ~ 1.0
        public bool Muted;
        public int State;               // SessionState
        public bool IsSystemSounds;
        public bool VolumeAccessible;   // 是否能拿到 ISimpleAudioVolume
        public ISimpleAudioVolume VolumeControl;

        public bool IsActive { get { return State == (int)SessionState.Active; } }

        /// <summary>
        /// 识别"同一个应用"的键：优先用 exe 完整路径（应用重启后路径不变）。
        /// 不能用 SessionIdentifier —— 它里面带 PID/随机串，每次启动都会变。
        /// </summary>
        public string AppKey
        {
            get
            {
                if (!string.IsNullOrEmpty(ExePath)) return ExePath.ToLowerInvariant();
                if (!string.IsNullOrEmpty(ProcessName)) return "name:" + ProcessName.ToLowerInvariant();
                return "pid:" + ProcessId;
            }
        }

        public string StateText
        {
            get
            {
                if (State == (int)SessionState.Active) return "播放中";
                if (State == (int)SessionState.Inactive) return "已暂停";
                return "已结束";
            }
        }
    }

    /// <summary>
    /// 音频会话（每个应用一路音量）的读写。
    /// 对应 Windows「音量合成器」那套 Core Audio Session API。
    /// </summary>
    internal static class AudioSessionManager
    {
        private static readonly Guid IID_IAudioSessionManager2 = new Guid("77AA99A0-1BD6-484F-8BC7-2C654C9A9B6F");
        private static readonly Guid IID_IAudioEndpointVolume = new Guid("5CDF2C82-841E-4546-9722-0CF74078229A");

        /// <summary>拿当前默认播放设备（多播/多媒体角色）。失败返回 null。</summary>
        private static IMMDevice GetDefaultRenderDevice()
        {
            object enObj = null;
            try
            {
                enObj = new MMDeviceEnumerator();
                IMMDeviceEnumerator en = (IMMDeviceEnumerator)enObj;
                IMMDevice dev;
                if (en.GetDefaultAudioEndpoint(EDataFlow.eRender, ERole.eMultimedia, out dev) != 0) return null;
                return dev;
            }
            catch
            {
                return null;
            }
            finally
            {
                // 本方法每秒会被调用多次（音量守卫 + 设置页刷新），枚举器用完必须立即释放，
                // 否则 RCW 会持续累积、COM 对象迟迟不释放。
                ReleaseComQuietly(enObj);
            }
        }

        /// <summary>
        /// 安静释放 COM 对象（RCW）。
        /// 只用于"自己刚创建、且之后不再使用"的局部对象；绝不要释放还持有引用的对象。
        /// </summary>
        private static void ReleaseComQuietly(object obj)
        {
            if (obj == null) return;
            try
            {
                if (Marshal.IsComObject(obj)) Marshal.ReleaseComObject(obj);
            }
            catch { /* 释放失败不影响功能，交给 GC 兜底 */ }
        }

        /// <summary>
        /// 枚举结果的短时缓存（毫秒）。
        /// 音量守卫每秒枚举一次、设置页打开时也每秒枚举一次 —— 二者本来会各跑一遍完整 COM 枚举；
        /// 400ms 内复用同一份结果即可让它们合并成一次，界面最多"旧" 0.4 秒，肉眼无感。
        /// </summary>
        private const int CacheMs = 400;
        private static List<AudioSessionInfo> cachedSessions;
        private static DateTime cachedAt = DateTime.MinValue;

        /// <summary>是否启用短时缓存。**诊断模式必须关闭**，否则"改完立即读回"会读到旧数据。</summary>
        public static bool CacheEnabled = true;

        /// <summary>枚举默认播放设备上的所有音频会话（含已暂停的）。</summary>
        public static List<AudioSessionInfo> GetSessions()
        {
            if (CacheEnabled && cachedSessions != null
                && (DateTime.Now - cachedAt).TotalMilliseconds < CacheMs)
            {
                return new List<AudioSessionInfo>(cachedSessions); // 返回副本，避免调用方改动污染缓存
            }

            List<AudioSessionInfo> fresh = GetSessionsCore();
            if (CacheEnabled)
            {
                cachedSessions = fresh;
                cachedAt = DateTime.Now;
            }
            return new List<AudioSessionInfo>(fresh);
        }

        private static List<AudioSessionInfo> GetSessionsCore()
        {
            List<AudioSessionInfo> list = new List<AudioSessionInfo>();
            IMMDevice device = GetDefaultRenderDevice();
            if (device == null) return list;

            object managerObj = null;
            IAudioSessionEnumerator enumerator = null;
            try
            {
                object obj;
                Guid iid = IID_IAudioSessionManager2;
                if (device.Activate(ref iid, CLSCTX.CLSCTX_ALL, IntPtr.Zero, out obj) != 0 || obj == null) return list;
                managerObj = obj;
                IAudioSessionManager2 manager = obj as IAudioSessionManager2;
                if (manager == null) return list;

                if (manager.GetSessionEnumerator(out enumerator) != 0 || enumerator == null) return list;

                int count;
                if (enumerator.GetCount(out count) != 0) return list;

                List<uint> pids = new List<uint>();
                for (int i = 0; i < count; i++)
                {
                    IAudioSessionControl control;
                    if (enumerator.GetSession(i, out control) != 0 || control == null) continue;
                    AudioSessionInfo info = Describe(control);
                    if (info != null)
                    {
                        list.Add(info);
                        pids.Add(info.ProcessId);
                    }
                }
                TrimProcessCache(pids); // 清掉已退出进程的缓存条目
                return list;
            }
            finally
            {
                // 会话对象（control / VolumeControl）会被界面行持有，这里不能释放；
                // 但枚举器、管理器、设备都是本方法的临时对象，用完即释放。
                if (enumerator != null) ReleaseComQuietly(enumerator);
                ReleaseComQuietly(managerObj);
                ReleaseComQuietly(device);
            }
        }

        private static AudioSessionInfo Describe(IAudioSessionControl control)
        {
            try
            {
                IAudioSessionControl2 control2 = control as IAudioSessionControl2;
                if (control2 == null) return null;

                AudioSessionInfo info = new AudioSessionInfo();

                int state;
                if (control.GetState(out state) == 0) info.State = state;

                uint pid;
                if (control2.GetProcessId(out pid) == 0) info.ProcessId = pid;
                info.IsSystemSounds = control2.IsSystemSoundsSession() == 0;

                IntPtr sid;
                if (control2.GetSessionIdentifier(out sid) == 0 && sid != IntPtr.Zero)
                {
                    info.SessionIdentifier = Marshal.PtrToStringUni(sid);
                    Marshal.FreeCoTaskMem(sid);
                }
                if (string.IsNullOrEmpty(info.SessionIdentifier))
                    info.SessionIdentifier = "pid:" + info.ProcessId + "@" + info.State;

                // 名称与 exe 路径合并成**一次**进程查询。
                // 原来是分两次调用（各开一次进程），这是 GetSessions 单次 15ms 的最大来源。
                string procName, exePath;
                ResolveProcessInfo(info.ProcessId, info.IsSystemSounds, out procName, out exePath);
                info.ProcessName = procName;
                info.ExePath = exePath;

                // 会话对象同时实现了 ISimpleAudioVolume，可直接 QI 过去
                ISimpleAudioVolume vol = control as ISimpleAudioVolume;
                if (vol != null)
                {
                    float v;
                    if (vol.GetMasterVolume(out v) == 0) info.Volume = v;
                    bool m;
                    if (vol.GetMute(out m) == 0) info.Muted = m;
                    info.VolumeControl = vol;
                    info.VolumeAccessible = true;
                }
                return info;
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// 进程信息缓存：PID → 名称 / exe 路径。
        ///
        /// 为什么需要：`GetProcessById` 与 `MainModule.FileName` 都是跨进程操作（单次约 1~2ms），
        /// 每秒枚举一次、每次 2×N 次查询，实测占了 GetSessions 单次 15ms 的绝大部分。
        /// PID 可能被复用（进程退出后新进程可能拿到同一个号），所以：
        ///   · 条目只保留 60 秒；
        ///   · 每次枚举结束后清理"本次没出现过"的 PID（见 TrimProcessCache）。
        /// </summary>
        private class ProcInfo
        {
            public string Name;
            public string ExePath;
            public DateTime CachedAt;
        }

        private static readonly Dictionary<uint, ProcInfo> procCache = new Dictionary<uint, ProcInfo>();
        private static readonly object procCacheLock = new object();
        private const double ProcCacheSeconds = 60;

        /// <summary>一次查询同时取到进程名与 exe 路径（带缓存）。</summary>
        private static void ResolveProcessInfo(uint pid, bool isSystemSounds, out string name, out string exePath)
        {
            if (isSystemSounds) { name = "系统提示音"; exePath = null; return; }
            if (pid == 0) { name = "（未知）"; exePath = null; return; }

            lock (procCacheLock)
            {
                ProcInfo hit;
                if (procCache.TryGetValue(pid, out hit) && (DateTime.Now - hit.CachedAt).TotalSeconds < ProcCacheSeconds)
                {
                    name = hit.Name;
                    exePath = hit.ExePath;
                    return;
                }
            }

            name = "PID " + pid;
            exePath = null;
            try
            {
                // using：Process 持有原生句柄，不释放会随枚举累积
                using (Process p = Process.GetProcessById((int)pid))
                {
                    name = p.ProcessName;
                    try { exePath = p.MainModule.FileName; }
                    catch { exePath = null; } // 受保护进程取不到，属正常情况
                }
            }
            catch
            {
                return; // 进程已退出（刚枚举到会话就关掉），保留兜底名且不缓存
            }

            lock (procCacheLock)
            {
                ProcInfo e = new ProcInfo();
                e.Name = name;
                e.ExePath = exePath;
                e.CachedAt = DateTime.Now;
                procCache[pid] = e;
            }
        }

        /// <summary>清理已消失的 PID 缓存，避免长期运行时字典无限增长。</summary>
        private static void TrimProcessCache(List<uint> alivePids)
        {
            lock (procCacheLock)
            {
                List<uint> dead = null;
                foreach (KeyValuePair<uint, ProcInfo> kv in procCache)
                {
                    if (!alivePids.Contains(kv.Key))
                    {
                        if (dead == null) dead = new List<uint>();
                        dead.Add(kv.Key);
                    }
                }
                if (dead == null) return;
                for (int i = 0; i < dead.Count; i++) procCache.Remove(dead[i]);
            }
        }

        private static float cachedDeviceVolume = 1f;
        private static DateTime deviceVolumeAt = DateTime.MinValue;

        /// <summary>设备总音量的缓存时间。比会话缓存长：它变化很慢，而每次 COM 激活约 3.8ms。</summary>
        private const int DeviceVolumeCacheMs = 2000;

        /// <summary>读取默认播放设备的总音量（0.0~1.0），失败按 1.0 处理。带缓存。</summary>
        public static float GetDeviceVolume()
        {
            // 设置页每秒读一次，单次 COM 激活约 3.8ms；2 秒内复用同一结果（音量不会突变）
            if (CacheEnabled && (DateTime.Now - deviceVolumeAt).TotalMilliseconds < DeviceVolumeCacheMs)
                return cachedDeviceVolume;

            float v = GetDeviceVolumeCore();
            if (CacheEnabled)
            {
                cachedDeviceVolume = v;
                deviceVolumeAt = DateTime.Now;
            }
            return v;
        }

        private static float GetDeviceVolumeCore()
        {
            IMMDevice device = GetDefaultRenderDevice();
            if (device == null) return 1f;

            object obj = null;
            try
            {
                Guid iid = IID_IAudioEndpointVolume;
                if (device.Activate(ref iid, CLSCTX.CLSCTX_ALL, IntPtr.Zero, out obj) != 0 || obj == null) return 1f;
                IAudioEndpointVolume ev = obj as IAudioEndpointVolume;
                if (ev == null) return 1f;
                float v;
                if (ev.GetMasterVolumeLevelScalar(out v) != 0) return 1f;
                return v;
            }
            catch
            {
                return 1f;
            }
            finally
            {
                ReleaseComQuietly(obj);
                ReleaseComQuietly(device);
            }
        }

        /// <summary>设置单个应用的音量（0.0~1.0）。</summary>
        public static bool SetVolume(AudioSessionInfo session, float volume)
        {
            if (session == null || session.VolumeControl == null) return false;
            try
            {
                if (volume < 0f) volume = 0f;
                if (volume > 1f) volume = 1f;
                return session.VolumeControl.SetMasterVolume(volume, IntPtr.Zero) == 0;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>设置单个应用是否静音。</summary>
        public static bool SetMute(AudioSessionInfo session, bool mute)
        {
            if (session == null || session.VolumeControl == null) return false;
            try
            {
                return session.VolumeControl.SetMute(mute, IntPtr.Zero) == 0;
            }
            catch
            {
                return false;
            }
        }
    }
}
