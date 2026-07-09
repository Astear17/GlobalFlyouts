using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading.Tasks;

namespace ModernFlyouts.Core.UI
{
    internal static class FlyoutBackdropExperiment
    {
        private const int DWM_BB_ENABLE = 0x00000001;
        private const int DWMWA_SYSTEMBACKDROP_TYPE = 38;
        private const int DWMWA_MICA_EFFECT_LEGACY = 1029;
        private const int DWMSBT_NONE = 1;
        private const int DWMSBT_MAINWINDOW = 2;

        private const string EnvironmentVariableName = "GLOBALFLYOUTS_FLYOUT_BACKDROP_EXPERIMENT";
        private const string SettingsFileName = "flyout-backdrop-experiment.txt";
        private const string LogFileName = "flyout-backdrop-experiment.log";

        internal static void Apply(IntPtr hWnd)
        {
            if (hWnd == IntPtr.Zero)
            {
                return;
            }

            string mode = GetMode();
            try
            {
                if (string.Equals(mode, "blur", StringComparison.OrdinalIgnoreCase))
                {
                    ApplyMica(hWnd, DWMSBT_NONE);
                    ApplyBlurBehind(hWnd, true);
                    LogResult("blur", "applied");
                    QueueCapture(hWnd, "blur");
                }
                else if (string.Equals(mode, "mica", StringComparison.OrdinalIgnoreCase))
                {
                    ApplyBlurBehind(hWnd, false);
                    ApplyMica(hWnd, DWMSBT_MAINWINDOW);
                    LogResult("mica", "applied");
                    QueueCapture(hWnd, "mica");
                }
                else
                {
                    ApplyBlurBehind(hWnd, false);
                    ApplyMica(hWnd, DWMSBT_NONE);
                    if (!string.IsNullOrWhiteSpace(mode))
                    {
                        LogResult(mode, "disabled");
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine(ex);
                LogResult(mode, ex.Message);
            }
        }

        private static async void QueueCapture(IntPtr hWnd, string mode)
        {
            await Task.Delay(500);

            try
            {
                if (!GetWindowRect(hWnd, out RECT rect))
                {
                    LogResult(mode, "GetWindowRect failed");
                    return;
                }

                int width = rect.Right - rect.Left;
                int height = rect.Bottom - rect.Top;
                if (width <= 0 || height <= 0)
                {
                    LogResult(mode, $"invalid capture size {width}x{height}");
                    return;
                }

                using Bitmap bitmap = new(width, height);
                using Graphics graphics = Graphics.FromImage(bitmap);
                graphics.CopyFromScreen(rect.Left, rect.Top, 0, 0, new Size(width, height));

                string capturePath = GetSettingsPath($"flyout-backdrop-{mode}-{DateTimeOffset.Now:yyyyMMdd-HHmmss-fff}.png");
                Directory.CreateDirectory(Path.GetDirectoryName(capturePath));
                bitmap.Save(capturePath, ImageFormat.Png);
                LogResult(mode, $"capture={capturePath}");
            }
            catch (Exception ex)
            {
                Debug.WriteLine(ex);
                LogResult(mode, $"capture failed: {ex.Message}");
            }
        }

        private static void ApplyBlurBehind(IntPtr hWnd, bool enabled)
        {
            DWM_BLURBEHIND blurBehind = new()
            {
                dwFlags = DWM_BB_ENABLE,
                fEnable = enabled,
                hRgnBlur = IntPtr.Zero,
                fTransitionOnMaximized = false
            };

            int result = DwmEnableBlurBehindWindow(hWnd, ref blurBehind);
            LogResult(enabled ? "blur" : "blur-off", $"DwmEnableBlurBehindWindow=0x{result:X8}");
        }

        private static void ApplyMica(IntPtr hWnd, int backdropType)
        {
            int result = DwmSetWindowAttribute(
                hWnd,
                DWMWA_SYSTEMBACKDROP_TYPE,
                ref backdropType,
                Marshal.SizeOf<int>());

            LogResult(backdropType == DWMSBT_MAINWINDOW ? "mica" : "mica-off",
                $"DwmSetWindowAttribute=0x{result:X8}");

            if (result != 0)
            {
                int legacyEnabled = backdropType == DWMSBT_MAINWINDOW ? 1 : 0;
                int legacyResult = DwmSetWindowAttribute(
                    hWnd,
                    DWMWA_MICA_EFFECT_LEGACY,
                    ref legacyEnabled,
                    Marshal.SizeOf<int>());

                LogResult(backdropType == DWMSBT_MAINWINDOW ? "mica-legacy" : "mica-legacy-off",
                    $"DwmSetWindowAttribute=0x{legacyResult:X8}");
            }
        }

        private static string GetMode()
        {
            string mode = Environment.GetEnvironmentVariable(EnvironmentVariableName);
            if (!string.IsNullOrWhiteSpace(mode))
            {
                return mode.Trim();
            }

            string settingsPath = GetSettingsPath(SettingsFileName);
            if (!File.Exists(settingsPath))
            {
                return string.Empty;
            }

            try
            {
                return File.ReadAllText(settingsPath).Trim();
            }
            catch
            {
                return string.Empty;
            }
        }

        private static void LogResult(string mode, string message)
        {
            try
            {
                string logPath = GetSettingsPath(LogFileName);
                Directory.CreateDirectory(Path.GetDirectoryName(logPath));
                File.AppendAllText(logPath, $"{DateTimeOffset.Now:O} [{mode}] {message}{Environment.NewLine}");
            }
            catch { }
        }

        private static string GetSettingsPath(string fileName)
        {
            string userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (!string.IsNullOrWhiteSpace(userProfile))
            {
                return Path.Combine(userProfile, "AppData", "Local", "GlobalFlyouts", fileName);
            }

            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "GlobalFlyouts",
                fileName);
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct DWM_BLURBEHIND
        {
            public int dwFlags;

            [MarshalAs(UnmanagedType.Bool)]
            public bool fEnable;

            public IntPtr hRgnBlur;

            [MarshalAs(UnmanagedType.Bool)]
            public bool fTransitionOnMaximized;
        }

        [DllImport("dwmapi.dll")]
        private static extern int DwmEnableBlurBehindWindow(IntPtr hWnd, ref DWM_BLURBEHIND pBlurBehind);

        [DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr hWnd, int dwAttribute, ref int pvAttribute, int cbAttribute);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

        [StructLayout(LayoutKind.Sequential)]
        private struct RECT
        {
            public int Left;
            public int Top;
            public int Right;
            public int Bottom;
        }
    }
}
