using System;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace TrayToys
{
    /// <summary>
    /// 开机自启 = 在用户的「启动」文件夹里放一个指向**当前 exe** 的快捷方式。
    ///
    /// 为什么用后期绑定的 WScript.Shell，而不是引 COM 互操作程序集：
    /// 这个项目坚持"单个 exe、零依赖"，引 Interop/Microsoft.CSharp 会多出文件；
    /// 后期绑定只靠 System.Reflection，运行时按 ProgID 从注册表取类型。
    ///
    /// 与 set-startup-shortcut.ps1 写的是同一个 .lnk，两边结果一致：
    /// 脚本用于命令行/双击，这里用于设置界面里的勾选框。
    /// </summary>
    internal static class AutoStart
    {
        /// <summary>改名之前留下的快捷方式名，勾选时会一并清掉，避免启动文件夹里出现两条。</summary>
        private static readonly string[] LegacyNames = new string[] { "AudioSwitcher.lnk", "a_few_tool.lnk" };

        /// <summary>启动文件夹里本程序的快捷方式完整路径。</summary>
        public static string ShortcutPath
        {
            get
            {
                string dir = Environment.GetFolderPath(Environment.SpecialFolder.Startup);
                return Path.Combine(dir, AppInfo.Name + ".lnk");
            }
        }

        /// <summary>
        /// 是否"已开启"：快捷方式存在，**并且指向当前这个 exe**。
        /// 只判断存在是不够的 —— 换了新版本、或把 exe 挪了位置之后，
        /// 旧快捷方式还在但登录时跑的不是现在这个，那不算开启。
        /// </summary>
        public static bool IsEnabled
        {
            get
            {
                try
                {
                    if (!File.Exists(ShortcutPath)) return false;
                    string target = ReadTarget(ShortcutPath);
                    // 读不出目标（COM 异常等）时按"存在即开启"处理，避免把用户的设置误判成没开
                    if (target == null) return true;
                    return string.Equals(
                        Path.GetFullPath(target),
                        Path.GetFullPath(Application.ExecutablePath),
                        StringComparison.OrdinalIgnoreCase);
                }
                catch { return false; }
            }
        }

        /// <summary>创建/更新启动项，让它指向当前 exe。</summary>
        public static void Enable()
        {
            string exe = Application.ExecutablePath;
            string lnk = ShortcutPath;

            string dir = Path.GetDirectoryName(lnk);
            if (dir != null && dir.Length > 0 && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);

            RemoveLegacy();

            Type shellType = Type.GetTypeFromProgID("WScript.Shell");
            if (shellType == null)
                throw new InvalidOperationException("系统里没有 WScript.Shell，无法创建快捷方式。");

            object shell = null;
            object shortcut = null;
            try
            {
                shell = Activator.CreateInstance(shellType);
                shortcut = shellType.InvokeMember("CreateShortcut", BindingFlags.InvokeMethod,
                    null, shell, new object[] { lnk });

                Type st = shortcut.GetType();
                st.InvokeMember("TargetPath", BindingFlags.SetProperty, null, shortcut, new object[] { exe });
                st.InvokeMember("WorkingDirectory", BindingFlags.SetProperty, null, shortcut,
                    new object[] { Path.GetDirectoryName(exe) });
                st.InvokeMember("Description", BindingFlags.SetProperty, null, shortcut,
                    new object[] { AppInfo.Name + " - tray toolbox (auto start)" });
                st.InvokeMember("IconLocation", BindingFlags.SetProperty, null, shortcut,
                    new object[] { exe + ",0" });
                st.InvokeMember("Save", BindingFlags.InvokeMethod, null, shortcut, null);
            }
            finally
            {
                ReleaseCom(shortcut);
                ReleaseCom(shell);
            }
        }

        /// <summary>删除启动项（连同历史名字），不存在也算成功。</summary>
        public static void Disable()
        {
            try { if (File.Exists(ShortcutPath)) File.Delete(ShortcutPath); } catch { }
            RemoveLegacy();
        }

        /// <summary>诊断用：把快捷方式当前指向的目标读出来（读不到时返回带原因的占位串）。</summary>
        public static string DebugTarget()
        {
            try
            {
                if (!File.Exists(ShortcutPath)) return "<no shortcut>";
                string t = ReadTarget(ShortcutPath);
                return t == null ? "<null>" : t;
            }
            catch (Exception ex) { return "<" + ex.GetType().Name + ">"; }
        }

        /// <summary>读取快捷方式的目标路径；失败返回 null。</summary>
        private static string ReadTarget(string lnkPath)
        {
            Type shellType = Type.GetTypeFromProgID("WScript.Shell");
            if (shellType == null) return null;

            object shell = null;
            object shortcut = null;
            try
            {
                shell = Activator.CreateInstance(shellType);
                shortcut = shellType.InvokeMember("CreateShortcut", BindingFlags.InvokeMethod,
                    null, shell, new object[] { lnkPath });
                object v = shortcut.GetType().InvokeMember("TargetPath", BindingFlags.GetProperty,
                    null, shortcut, null);
                return v as string;
            }
            finally
            {
                ReleaseCom(shortcut);
                ReleaseCom(shell);
            }
        }

        private static void RemoveLegacy()
        {
            string dir = Path.GetDirectoryName(ShortcutPath);
            if (dir == null || dir.Length == 0) return;
            for (int i = 0; i < LegacyNames.Length; i++)
            {
                string p = Path.Combine(dir, LegacyNames[i]);
                try { if (File.Exists(p)) File.Delete(p); } catch { }
            }
        }

        private static void ReleaseCom(object o)
        {
            try
            {
                if (o != null && Marshal.IsComObject(o)) Marshal.ReleaseComObject(o);
            }
            catch { }
        }
    }
}
