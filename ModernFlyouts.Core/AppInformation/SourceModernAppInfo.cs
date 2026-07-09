using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Text;
using System.Threading.Tasks;
using ModernFlyouts.Core.Utilities;
using Windows.ApplicationModel.Core;
using Windows.Management.Deployment;
using static ModernFlyouts.Core.Interop.NativeMethods;

namespace ModernFlyouts.Core.AppInformation
{
    internal class SourceModernAppInfo : SourceAppInfo
    {
        private static readonly PlayerIconCache playerIconCache = new();

        public SourceModernAppInfo(SourceAppInfoData data)
        {
            Data = data;
        }

        public override event EventHandler InfoFetched;

        private AppListEntry sourceApp;
        private Process fallbackProcess;
        private int currentAppIndex;

        public override void Activate()
        {
            try
            {
                if (sourceApp != null && Data.DataType == SourceAppInfoDataType.FromAppUserModelId)
                {
                    _ = sourceApp?.LaunchAsync();
                }
                else if (fallbackProcess != null)
                {
                    SourceDesktopAppInfo.ActivateWindow(fallbackProcess.MainWindowHandle);
                }
                else if (Data.DataType == SourceAppInfoDataType.FromAppUserModelId &&
                    !string.IsNullOrWhiteSpace(Data.AppUserModelId))
                {
                    using var fallback = FindProcessByAppUserModelId(Data.AppUserModelId)
                        ?? FindProcessByFallbackDisplayName(Data.AppUserModelId);

                    if (fallback != null)
                    {
                        SourceDesktopAppInfo.ActivateWindow(fallback.MainWindowHandle);
                    }
                }
                else if (Data.DataType == SourceAppInfoDataType.FromProcessId)
                {
                    using var sourceProcess = Process.GetProcessById((int)Data.ProcessId);
                    IntPtr hWnd = IsWindow(Data.MainWindowHandle)
                        ? Data.MainWindowHandle : sourceProcess?.MainWindowHandle ?? IntPtr.Zero;
                    SourceDesktopAppInfo.ActivateWindow(hWnd);
                }
            }
            catch { }
        }

        public override async void FetchInfosAsync()
        {
            string appUserModelId = Data.AppUserModelId;
            string path = string.Empty;

            if (string.IsNullOrEmpty(Data.AppUserModelId)
                || string.IsNullOrWhiteSpace(Data.AppUserModelId))
            {
                await Task.Run(() =>
                {
                    appUserModelId = GetAppUserModelIdForProcess();
                });
            }

            if (TryApplyCachedPlayerIcon(appUserModelId))
            {
                InfoFetched?.Invoke(this, null);
                return;
            }

            try
            {
                var pm = new PackageManager();
                var packages = pm.FindPackagesForUser(string.Empty);

                async Task<AppListEntry> GetAppListEntry()
                {
                    foreach (var package in packages)
                    {
                        var result = await package.GetAppListEntriesAsync();
                        for (int i = 0; i < result.Count; i++)
                        {
                            var app = result[i];

                            if (app.AppUserModelId == appUserModelId)
                            {
                                path = package.InstalledLocation.Path;
                                currentAppIndex = i;
                                return app;
                            }
                        }
                    }

                    return null;
                }

                sourceApp = await GetAppListEntry();
            }
            catch { }

            if (sourceApp == null)
            {
                await FetchUnpackagedFallbackInfosAsync(appUserModelId);
                InfoFetched?.Invoke(this, null);
                return;
            }

            try
            {
                DisplayName = sourceApp.DisplayInfo.DisplayName;
            }
            catch { }

            await Task.Run(() =>
            {
                string logoPath = string.Empty;
                try
                {
                    logoPath = GetRefinedLogoPath(path);
                }
                catch { }

                if (File.Exists(logoPath))
                {
                    MemoryStream memoryStream = new();
                    byte[] fileBytes = File.ReadAllBytes(logoPath);
                    memoryStream.Write(fileBytes, 0, fileBytes.Length);
                    memoryStream.Seek(0, SeekOrigin.Begin);

                    LogoStream = memoryStream;
                }
            });

            InfoFetched?.Invoke(this, null);
        }

        private async Task FetchUnpackagedFallbackInfosAsync(string appUserModelId)
        {
            if (string.IsNullOrWhiteSpace(appUserModelId))
            {
                return;
            }

            await Task.Run(() =>
            {
                fallbackProcess?.Dispose();
                fallbackProcess = FindProcessByAppUserModelId(appUserModelId)
                    ?? FindProcessByFallbackDisplayName(appUserModelId);
                if (fallbackProcess == null)
                {
                    DisplayName = GetFallbackDisplayName(appUserModelId);
                    return;
                }

                string executablePath = string.Empty;

                try
                {
                    executablePath = fallbackProcess.MainModule.FileName;
                    DisplayName = fallbackProcess.MainModule.FileVersionInfo.FileDescription;
                }
                catch { }

                if (string.IsNullOrWhiteSpace(DisplayName))
                {
                    DisplayName = GetFallbackDisplayName(appUserModelId);
                }

                if (string.IsNullOrWhiteSpace(executablePath) || !File.Exists(executablePath))
                {
                    return;
                }

                LogoStream = CreateIconStreamFromExecutable(executablePath);
                playerIconCache.Store(appUserModelId, DisplayName, LogoStream);
            });
        }

        private bool TryApplyCachedPlayerIcon(string appUserModelId)
        {
            if (!playerIconCache.TryGet(appUserModelId, out var result))
            {
                return false;
            }

            if (!string.IsNullOrWhiteSpace(result.DisplayName))
            {
                DisplayName = result.DisplayName;
            }

            LogoStream?.Dispose();
            LogoStream = result.CreateStream();
            return true;
        }

        private static Process FindProcessByAppUserModelId(string appUserModelId)
        {
            Process fallback = null;

            foreach (var process in Process.GetProcesses())
            {
                try
                {
                    string processAppUserModelId = GetAppUserModelIdForProcess(process);
                    if (!string.Equals(processAppUserModelId, appUserModelId, StringComparison.OrdinalIgnoreCase))
                    {
                        process.Dispose();
                        continue;
                    }

                    if (process.MainWindowHandle != IntPtr.Zero)
                    {
                        fallback?.Dispose();
                        return process;
                    }

                    if (fallback == null)
                    {
                        fallback = process;
                    }
                    else
                    {
                        process.Dispose();
                    }
                }
                catch
                {
                    process.Dispose();
                }
            }

            return fallback;
        }

        private static MemoryStream CreateIconStreamFromExecutable(string executablePath)
        {
            return TryCreateIconStream(() => Icon.ExtractAssociatedIcon(executablePath), out var associatedIconStream)
                ? associatedIconStream
                : TryCreateIconStream(() => new IconExtractor(executablePath).GetIcon(0), out var extractedIconStream)
                    ? extractedIconStream
                    : null;
        }

        private static bool TryCreateIconStream(Func<Icon> iconFactory, out MemoryStream iconStream)
        {
            iconStream = null;

            try
            {
                using Icon icon = iconFactory();
                if (icon == null)
                {
                    return false;
                }

                using Bitmap bitmap = icon.ToBitmap();
                iconStream = new MemoryStream();
                bitmap.Save(iconStream, ImageFormat.Png);
                iconStream.Seek(0, SeekOrigin.Begin);
                return true;
            }
            catch
            {
                iconStream?.Dispose();
                iconStream = null;
                return false;
            }
        }

        private static Process FindProcessByFallbackDisplayName(string appUserModelId)
        {
            string fallbackDisplayName = GetFallbackDisplayName(appUserModelId);
            string normalizedDisplayName = NormalizeProcessName(fallbackDisplayName);
            if (string.IsNullOrWhiteSpace(normalizedDisplayName))
            {
                return null;
            }

            Process fallback = null;
            int fallbackScore = 0;

            foreach (var process in Process.GetProcesses())
            {
                try
                {
                    int score = GetFallbackProcessMatchScore(process, normalizedDisplayName);
                    if (score == 0)
                    {
                        process.Dispose();
                        continue;
                    }

                    if (process.MainWindowHandle != IntPtr.Zero)
                    {
                        score += 100;
                    }

                    if (score > fallbackScore)
                    {
                        fallback?.Dispose();
                        fallback = process;
                        fallbackScore = score;
                    }
                    else
                    {
                        process.Dispose();
                    }
                }
                catch
                {
                    process.Dispose();
                }
            }

            return fallback;
        }

        private static int GetFallbackProcessMatchScore(Process process, string normalizedDisplayName)
        {
            int score = 0;

            score = Math.Max(score, GetExactNameMatchScore(process.ProcessName, normalizedDisplayName, 90));
            score = Math.Max(score, GetExactNameMatchScore(process.MainWindowTitle, normalizedDisplayName, 80));

            try
            {
                string executablePath = process.MainModule?.FileName;
                score = Math.Max(score, GetExactNameMatchScore(Path.GetFileNameWithoutExtension(executablePath), normalizedDisplayName, 85));
                score = Math.Max(score, GetExactNameMatchScore(process.MainModule?.FileVersionInfo.FileDescription, normalizedDisplayName, 75));
                score = Math.Max(score, GetExactNameMatchScore(process.MainModule?.FileVersionInfo.ProductName, normalizedDisplayName, 70));
                score = Math.Max(score, GetExactNameMatchScore(process.MainModule?.FileVersionInfo.OriginalFilename, normalizedDisplayName, 65));
            }
            catch { }

            return score;
        }

        private static int GetExactNameMatchScore(string value, string normalizedDisplayName, int score)
        {
            return string.Equals(NormalizeProcessName(value), normalizedDisplayName, StringComparison.Ordinal)
                ? score
                : 0;
        }

        private static string NormalizeProcessName(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return string.Empty;
            }

            if (value.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            {
                value = value[..^4];
            }

            return string.Concat(value.Where(char.IsLetterOrDigit)).ToLowerInvariant();
        }

        private static string GetAppUserModelIdForProcess(Process process)
        {
            if (process == null)
            {
                return string.Empty;
            }

            int amuidBufferLength = 512;
            StringBuilder amuidBuffer = new(amuidBufferLength);
            int result = GetApplicationUserModelId(process.Handle, ref amuidBufferLength, amuidBuffer);
            return result == 0 ? amuidBuffer.ToString() : string.Empty;
        }

        private string GetLogoPathFromAppPath(string appPath)
        {
            var factory = (IAppxFactory)new AppxFactory();
            string logo = string.Empty;

            string manifestPath = Path.Combine(appPath, "AppXManifest.xml");
            const int STGM_SHARE_DENY_NONE = 0x40;

            SHCreateStreamOnFileEx(manifestPath, STGM_SHARE_DENY_NONE, 0, false, IntPtr.Zero, out IStream strm);
            if (strm != null)
            {
                var reader = factory.CreateManifestReader(strm);
                var apps = reader.GetApplications();
                int i = 0;

                while (apps.GetHasCurrent())
                {
                    var app = apps.GetCurrent();
                    if (currentAppIndex == i)
                    {
                        app.GetStringValue("Square44x44Logo", out logo);
                        break;
                    }
                    else
                    {
                        i++;
                        apps.MoveNext();
                    }
                }
                Marshal.ReleaseComObject(strm);
            }

            Marshal.ReleaseComObject(factory);
            return logo;
        }

        private string GetRefinedLogoPath(string appPath)
        {
            var resourceName = GetLogoPathFromAppPath(appPath);
            const string targetSizeToken = ".targetsize-";
            const string scaleToken = ".scale-";
            SortedDictionary<int, string> files = new();
            string name = Path.GetFileNameWithoutExtension(resourceName);
            string ext = Path.GetExtension(resourceName);

            string finalSizeToken;
            if (Directory.EnumerateFiles(Path.Combine(appPath, Path.GetDirectoryName(resourceName)), name + targetSizeToken + "*" + ext).Any())
            {
                finalSizeToken = targetSizeToken;
            }
            else
            {
                finalSizeToken = scaleToken;
            }

            foreach (var file in Directory.EnumerateFiles(Path.Combine(appPath, Path.GetDirectoryName(resourceName)), name + finalSizeToken + "*" + ext))
            {
                string fileName = Path.GetFileNameWithoutExtension(file);
                int pos = fileName.IndexOf(finalSizeToken) + finalSizeToken.Length;
                string sizeText = string.Empty;
                if (fileName.Contains('_'))
                {
                    int endpos = fileName.IndexOf('_', pos);
                    sizeText = fileName[pos..endpos];
                }
                else
                {
                    sizeText = fileName[pos..];
                }

                if (int.TryParse(sizeText, out int size))
                {
                    if (!files.ContainsKey(size))
                    {
                        files.Add(size, file);
                    }
                }
            }
            if (files.Count == 0)
                return null;

            return files.First().Value;
        }

        private string GetAppUserModelIdForProcess()
        {
            using var process = Process.GetProcessById((int)Data.ProcessId);
            if (process == null)
                return string.Empty;

            int amuidBufferLength = 512;
            StringBuilder amuidBuffer = new(amuidBufferLength);

            GetApplicationUserModelId(process.Handle, ref amuidBufferLength, amuidBuffer);
            return amuidBuffer.ToString();
        }

        protected override void Disconnect()
        {
            base.Disconnect();
            sourceApp = null;
            fallbackProcess?.Dispose();
            fallbackProcess = null;
        }

        #region Appx Things

        [Guid("5842a140-ff9f-4166-8f5c-62f5b7b0c781"), ComImport]
        private class AppxFactory
        {
        }

        [Guid("BEB94909-E451-438B-B5A7-D79E767B75D8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IAppxFactory
        {
            void _VtblGap0_2(); // skip 2 methods

            IAppxManifestReader CreateManifestReader(IStream inputStream);
        }

        [Guid("4E1BD148-55A0-4480-A3D1-15544710637C"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IAppxManifestReader
        {
            void _VtblGap0_1(); // skip 1 method

            IAppxManifestProperties GetProperties();

            void _VtblGap1_5(); // skip 5 methods

            IAppxManifestApplicationsEnumerator GetApplications();
        }

        [Guid("9EB8A55A-F04B-4D0D-808D-686185D4847A"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IAppxManifestApplicationsEnumerator
        {
            IAppxManifestApplication GetCurrent();

            bool GetHasCurrent();

            bool MoveNext();
        }

        [Guid("5DA89BF4-3773-46BE-B650-7E744863B7E8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IAppxManifestApplication
        {
            [PreserveSig]
            int GetStringValue([MarshalAs(UnmanagedType.LPWStr)] string name, [MarshalAs(UnmanagedType.LPWStr)] out string vaue);
        }

        [Guid("03FAF64D-F26F-4B2C-AAF7-8FE7789B8BCA"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IAppxManifestProperties
        {
            [PreserveSig]
            int GetBoolValue([MarshalAs(UnmanagedType.LPWStr)] string name, out bool value);

            [PreserveSig]
            int GetStringValue([MarshalAs(UnmanagedType.LPWStr)] string name, [MarshalAs(UnmanagedType.LPWStr)] out string vaue);
        }

        [DllImport("shlwapi.dll", CharSet = CharSet.Unicode)]
        private static extern int SHCreateStreamOnFileEx(string fileName, int grfMode, int attributes, bool create, IntPtr reserved, out IStream stream);

        #endregion
    }
}
