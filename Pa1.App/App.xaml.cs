using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Threading;

namespace Pa1.App
{
    public partial class App : Application
    {
        static App()
        {
            // 单文件发布也显式启用 WPF 的每显示器 DPI 布局。
            AppContext.SetSwitch("Switch.System.Windows.DoNotScaleForDpiChanges", false);
        }

        /* 系统定时器精度 15.6ms → 1ms：音频轮询线程的睡眠粒度、DispatcherTimer 量化都靠它 */
        [DllImport("winmm.dll")] internal static extern uint timeBeginPeriod(uint period);
        [DllImport("winmm.dll")] internal static extern uint timeEndPeriod(uint period);

        protected override void OnStartup(StartupEventArgs e)
        {
            /* 旧 PHOSPHOR-PA1 数据目录 → GSharpMinor（先于任何 Settings/Loudness/NativeSkia 路径读取） */
            DataDirs.MigrateLegacy();
            /* 必须先于任何 Skia 类型加载：单文件模式下从内嵌资源抽出 libSkiaSharp */
            NativeSkia.EnsureLoaded();
            base.OnStartup(e);
            try { timeBeginPeriod(1); } catch { }
            DispatcherUnhandledException += OnDispatcherUnhandled;
            AppDomain.CurrentDomain.UnhandledException += OnDomainUnhandled;
            try
            {
                File.AppendAllText(Path.Combine(Path.GetTempPath(), "gsharpminor-crash.log"),
                    DateTime.Now + " [start] pid=" + System.Diagnostics.Process.GetCurrentProcess().Id + Environment.NewLine);
            }
            catch { }
        }

        protected override void OnExit(ExitEventArgs e)
        {
            try { timeEndPeriod(1); } catch { }
            try
            {
                File.AppendAllText(Path.Combine(Path.GetTempPath(), "gsharpminor-crash.log"),
                    DateTime.Now + " [exit] code=" + e.ApplicationExitCode + Environment.NewLine);
            }
            catch { }
            base.OnExit(e);
        }

        static void Log(string kind, Exception ex)
        {
            try
            {
                File.AppendAllText(Path.Combine(Path.GetTempPath(), "gsharpminor-crash.log"),
                    DateTime.Now + " [" + kind + "] " + ex + Environment.NewLine + Environment.NewLine);
            }
            catch { }
        }

        static void OnDispatcherUnhandled(object s, DispatcherUnhandledExceptionEventArgs e)
        {
            Log("dispatcher", e.Exception);
            MessageBox.Show("G# minor 发生错误：\n" + e.Exception.Message + "\n\n详情已写入 %TEMP%\\gsharpminor-crash.log",
                "G# minor", MessageBoxButton.OK, MessageBoxImage.Warning);
            e.Handled = true;
        }

        static void OnDomainUnhandled(object s, UnhandledExceptionEventArgs e)
        {
            Log("domain", e.ExceptionObject as Exception ?? new Exception(e.ExceptionObject.ToString()));
        }
    }
}
