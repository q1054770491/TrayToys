namespace TrayToys
{
    /// <summary>应用基本信息（版本号、程序名等集中管理）。</summary>
    internal static class AppInfo
    {
        public const string Version = "2.8";

        /// <summary>
        /// 程序名：托盘提示、窗口标题、配置文件名、日志文件名、互斥体名全部由它拼出来。
        /// 只改这一个常量就能整体改名（旧配置会被自动迁移，见 Settings.FindLegacyFile）。
        /// </summary>
        public const string Name = "TrayToys";
    }
}
