using System;
using System.IO;
using System.Text;

namespace TrayToys
{
    /// <summary>
    /// 极简文件日志：写入 exe 同目录下的 TrayToys.log（便于查找）。
    /// **默认关闭**（不生成任何日志），可在设置页勾选「记录日志」开启。
    /// 超过 2MB 自动清空重写（简单轮转）；日志失败绝不影响主程序。
    /// 文件名由 AppInfo.Name 拼出来，改程序名时不用再改这里。
    /// </summary>
    internal static class Logger
    {
        private static readonly object LockObj = new object();
        private const long MaxBytes = 2 * 1024 * 1024;
        private static string _path;

        /// <summary>是否启用日志（由设置里的「记录日志」控制，默认 false = 不产生日志）。</summary>
        public static bool Enabled = false;

        /// <summary>日志文件完整路径（exe 同目录）。</summary>
        public static string LogFilePath
        {
            get
            {
                if (_path == null)
                {
                    // exe 所在目录（BaseDirectory 自带结尾分隔符）
                    _path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, AppInfo.Name + ".log");
                }
                return _path;
            }
        }

        public static void Info(string msg)
        {
            Write("INFO", msg, null);
        }

        public static void Error(string msg, Exception ex)
        {
            Write("ERROR", msg, ex);
        }

        /// <summary>
        /// 崩溃日志：**忽略「记录日志」开关，强制写盘**。
        /// 崩溃信息是事后排查的唯一线索，不能因为用户没勾选"记录日志"就丢掉；
        /// 除 TrayToys.log 外另写一份 TrayToys_crash.log（单独文件，不会被日志轮转清掉）。
        /// </summary>
        public static void Crash(string msg, Exception ex)
        {
            WriteForce("FATAL", msg, ex);

            try
            {
                string path = CrashFilePath;
                lock (LockObj)
                {
                    if (File.Exists(path) && new FileInfo(path).Length > 256 * 1024)
                        File.WriteAllText(path, ""); // 超限清空，避免无限增长
                    StringBuilder sb = new StringBuilder();
                    sb.Append("===== ").Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff"))
                      .Append(" =====").AppendLine().Append(msg);
                    if (ex != null) sb.AppendLine().Append(ex.ToString());
                    File.AppendAllText(path, sb.AppendLine().AppendLine().ToString());
                }
            }
            catch { /* 崩溃日志也失败时不再向上抛 */ }
        }

        /// <summary>强制写入主日志文件（不看 Enabled 开关）。</summary>
        private static void WriteForce(string level, string msg, Exception ex)
        {
            try
            {
                lock (LockObj)
                {
                    if (File.Exists(LogFilePath))
                    {
                        FileInfo fi = new FileInfo(LogFilePath);
                        if (fi.Length > MaxBytes) File.WriteAllText(LogFilePath, "");
                    }
                    StringBuilder sb = new StringBuilder();
                    sb.Append('[').Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff"))
                      .Append("] [").Append(level).Append("] ").Append(msg);
                    if (ex != null) sb.AppendLine().Append(ex.ToString());
                    File.AppendAllText(LogFilePath, sb.AppendLine().ToString());
                }
            }
            catch { }
        }

        /// <summary>崩溃日志文件完整路径。</summary>
        public static string CrashFilePath
        {
            get { return Path.Combine(AppDomain.CurrentDomain.BaseDirectory, AppInfo.Name + "_crash.log"); }
        }

        private static void Write(string level, string msg, Exception ex)
        {
            if (!Enabled) return; // 关闭日志时直接跳过
            try
            {
                lock (LockObj)
                {
                    if (File.Exists(LogFilePath))
                    {
                        FileInfo fi = new FileInfo(LogFilePath);
                        if (fi.Length > MaxBytes) File.WriteAllText(LogFilePath, ""); // 超限轮转
                    }
                    StringBuilder sb = new StringBuilder();
                    sb.Append('[').Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff"))
                      .Append("] [").Append(level).Append("] ").Append(msg);
                    if (ex != null) sb.AppendLine().Append(ex.ToString());
                    File.AppendAllText(LogFilePath, sb.AppendLine().ToString());
                }
            }
            catch { /* 日志失败绝不影响主程序 */ }
        }
    }
}
