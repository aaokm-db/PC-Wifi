using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Shapes;
using System.Windows.Shell;
using System.Windows.Threading;
using Microsoft.Win32;
using Windows.Devices.WiFiDirect;
using Windows.Foundation;
using Windows.Networking;
using Windows.Networking.Connectivity;
using Windows.Networking.NetworkOperators;

namespace PCWifiApp
{
    #region Win32 & DWM Native Interop
    internal static class NativeMethods
    {
        [DllImport("dwmapi.dll", PreserveSig = true)]
        public static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);

        [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        public static extern uint SetThreadExecutionState(uint esFlags);

        [DllImport("user32.dll")]
        public static extern bool SetForegroundWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        public static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        public static extern IntPtr FindWindow(IntPtr lpClassName, string lpWindowName);

        public const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
        public const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
        public const int DWMWCP_ROUND = 2;

        public const uint ES_CONTINUOUS = 0x80000000;
        public const uint ES_SYSTEM_REQUIRED = 0x00000001;
        public const int SW_RESTORE = 9;

        public static void ApplyWin11WindowAttributes(IntPtr hwnd, bool dark)
        {
            if (hwnd == IntPtr.Zero) return;
            try
            {
                int cornerPref = DWMWCP_ROUND;
                DwmSetWindowAttribute(hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, ref cornerPref, sizeof(int));

                int darkVal = dark ? 1 : 0;
                DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref darkVal, sizeof(int));
            }
            catch
            {
            }
        }

        public static void UpdateSleepPrevention(bool preventSleep)
        {
            try
            {
                if (preventSleep)
                {
                    SetThreadExecutionState(ES_CONTINUOUS | ES_SYSTEM_REQUIRED);
                }
                else
                {
                    SetThreadExecutionState(ES_CONTINUOUS);
                }
            }
            catch
            {
            }
        }

        public static bool IsRunningAsAdministrator()
        {
            try
            {
                WindowsIdentity identity = WindowsIdentity.GetCurrent();
                WindowsPrincipal principal = new WindowsPrincipal(identity);
                return principal.IsInRole(WindowsBuiltInRole.Administrator);
            }
            catch
            {
                return false;
            }
        }
    }
    #endregion

    #region WinRT Async Helper
    internal static class WinRtAsync
    {
        public static Task<T> RunAsync<T>(Func<IAsyncOperation<T>> starter)
        {
            return Task.Run<T>(delegate
            {
                IAsyncOperation<T> op = starter();
                int waited = 0;
                while (op.Status == AsyncStatus.Started && waited < 35000)
                {
                    Thread.Sleep(40);
                    waited += 40;
                }
                if (op.Status == AsyncStatus.Error && op.ErrorCode != null)
                {
                    throw op.ErrorCode;
                }
                return op.GetResults();
            });
        }

        public static Task RunAsync(Func<IAsyncAction> starter)
        {
            return Task.Run(delegate
            {
                IAsyncAction op = starter();
                int waited = 0;
                while (op.Status == AsyncStatus.Started && waited < 25000)
                {
                    Thread.Sleep(40);
                    waited += 40;
                }
                if (op.Status == AsyncStatus.Error && op.ErrorCode != null)
                {
                    throw op.ErrorCode;
                }
            });
        }
    }
    #endregion

    #region Persistent Settings & Per-Device Profiles
    internal sealed class DevicePolicyRecord
    {
        public string MacAddress { get; set; }
        public string LastIpAddress { get; set; }
        public string HostName { get; set; }
        public string CustomName { get; set; }
        public bool IsBlocked { get; set; }
        public long DownloadedBytes { get; set; }
        public long UploadedBytes { get; set; }
        public double MaxDownloadLimitMB { get; set; } // 0 = Unlimited
        public double MaxSpeedKbps { get; set; }       // 0 = Unlimited
        public DateTime FirstSeen { get; set; }
        public DateTime LastSeen { get; set; }
        public bool IsCurrentlyConnected { get; set; }
        public double CurrentDownloadSpeedKbps { get; set; }
        public double CurrentUploadSpeedKbps { get; set; }
        public bool QuotaTriggeredBlock { get; set; }

        public string DisplayName
        {
            get
            {
                if (!string.IsNullOrWhiteSpace(CustomName)) return CustomName;
                if (!string.IsNullOrWhiteSpace(HostName)) return HostName;
                return "Wi-Fi Client (" + MacAddress + ")";
            }
        }

        public DevicePolicyRecord()
        {
            MacAddress = "";
            LastIpAddress = "";
            HostName = "Connected Device";
            CustomName = "";
            FirstSeen = DateTime.Now;
            LastSeen = DateTime.Now;
        }
    }

    internal sealed class AppSettings
    {
        public bool KeepAliveEnabled { get; set; }
        public bool ForceDisableOsTimeout { get; set; }
        public bool PreventSystemSleep { get; set; }
        public int WatchdogIntervalSeconds { get; set; }
        public bool AutoStartHotspotOnLaunch { get; set; }
        public bool MinimizeToTrayOnClose { get; set; }
        public bool IsDarkMode { get; set; }
        public string Language { get; set; }
        public string PreferredProfileName { get; set; }

        // Hidden Wi-Fi & Security
        public bool IsHiddenWifiEnabled { get; set; }
        public string SavedVisibleSsid { get; set; }
        public bool BlockNewUnknownDevices { get; set; }

        // Global Data & Bandwidth Limits
        public bool GlobalDownloadLimitEnabled { get; set; }
        public double GlobalDownloadLimitMB { get; set; }
        public bool GlobalSpeedLimitEnabled { get; set; }
        public double GlobalMaxSpeedKbps { get; set; }

        public AppSettings()
        {
            KeepAliveEnabled = true;
            ForceDisableOsTimeout = true;
            PreventSystemSleep = true;
            WatchdogIntervalSeconds = 5;
            AutoStartHotspotOnLaunch = false;
            MinimizeToTrayOnClose = true;
            IsDarkMode = true;
            Language = "en";
            PreferredProfileName = "Auto";
            IsHiddenWifiEnabled = false;
            SavedVisibleSsid = "";
            BlockNewUnknownDevices = false;
            GlobalDownloadLimitEnabled = false;
            GlobalDownloadLimitMB = 2048; // 2 GB default when enabled
            GlobalSpeedLimitEnabled = false;
            GlobalMaxSpeedKbps = 2048;    // 2 MB/s (16 Mbps) default when enabled
        }

        public static string GetAppDataDir()
        {
            string dir = System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "PCWifi");
            if (!Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
            }
            return dir;
        }

        private static string GetSettingsFilePath()
        {
            return System.IO.Path.Combine(GetAppDataDir(), "settings.cfg");
        }

        private static string GetDevicesFilePath()
        {
            return System.IO.Path.Combine(GetAppDataDir(), "devices.cfg");
        }

        public static AppSettings Load()
        {
            AppSettings s = new AppSettings();
            try
            {
                string path = GetSettingsFilePath();
                if (!File.Exists(path)) return s;
                string[] lines = File.ReadAllLines(path, Encoding.UTF8);
                foreach (string raw in lines)
                {
                    if (string.IsNullOrWhiteSpace(raw) || raw.StartsWith("#")) continue;
                    int idx = raw.IndexOf('=');
                    if (idx <= 0) continue;
                    string key = raw.Substring(0, idx).Trim();
                    string val = raw.Substring(idx + 1).Trim();
                    bool b;
                    int n;
                    double d;
                    if (key == "KeepAliveEnabled" && bool.TryParse(val, out b)) s.KeepAliveEnabled = b;
                    else if (key == "ForceDisableOsTimeout" && bool.TryParse(val, out b)) s.ForceDisableOsTimeout = b;
                    else if (key == "PreventSystemSleep" && bool.TryParse(val, out b)) s.PreventSystemSleep = b;
                    else if (key == "WatchdogIntervalSeconds" && int.TryParse(val, out n) && n >= 2 && n <= 120) s.WatchdogIntervalSeconds = n;
                    else if (key == "AutoStartHotspotOnLaunch" && bool.TryParse(val, out b)) s.AutoStartHotspotOnLaunch = b;
                    else if (key == "MinimizeToTrayOnClose" && bool.TryParse(val, out b)) s.MinimizeToTrayOnClose = b;
                    else if (key == "IsDarkMode" && bool.TryParse(val, out b)) s.IsDarkMode = b;
                    else if (key == "Language" && (val == "en" || val == "ar")) s.Language = val;
                    else if (key == "PreferredProfileName" && !string.IsNullOrEmpty(val)) s.PreferredProfileName = val;
                    else if (key == "IsHiddenWifiEnabled" && bool.TryParse(val, out b)) s.IsHiddenWifiEnabled = b;
                    else if (key == "SavedVisibleSsid") s.SavedVisibleSsid = val;
                    else if (key == "BlockNewUnknownDevices" && bool.TryParse(val, out b)) s.BlockNewUnknownDevices = b;
                    else if (key == "GlobalDownloadLimitEnabled" && bool.TryParse(val, out b)) s.GlobalDownloadLimitEnabled = b;
                    else if (key == "GlobalDownloadLimitMB" && double.TryParse(val, NumberStyles.Any, CultureInfo.InvariantCulture, out d) && d > 0) s.GlobalDownloadLimitMB = d;
                    else if (key == "GlobalSpeedLimitEnabled" && bool.TryParse(val, out b)) s.GlobalSpeedLimitEnabled = b;
                    else if (key == "GlobalMaxSpeedKbps" && double.TryParse(val, NumberStyles.Any, CultureInfo.InvariantCulture, out d) && d > 0) s.GlobalMaxSpeedKbps = d;
                }
            }
            catch
            {
            }
            return s;
        }

        public void Save()
        {
            try
            {
                string path = GetSettingsFilePath();
                StringBuilder sb = new StringBuilder();
                sb.AppendLine("# PC Wifi Settings");
                sb.AppendLine("KeepAliveEnabled=" + KeepAliveEnabled);
                sb.AppendLine("ForceDisableOsTimeout=" + ForceDisableOsTimeout);
                sb.AppendLine("PreventSystemSleep=" + PreventSystemSleep);
                sb.AppendLine("WatchdogIntervalSeconds=" + WatchdogIntervalSeconds);
                sb.AppendLine("AutoStartHotspotOnLaunch=" + AutoStartHotspotOnLaunch);
                sb.AppendLine("MinimizeToTrayOnClose=" + MinimizeToTrayOnClose);
                sb.AppendLine("IsDarkMode=" + IsDarkMode);
                sb.AppendLine("Language=" + (Language == "ar" ? "ar" : "en"));
                sb.AppendLine("PreferredProfileName=" + (PreferredProfileName ?? "Auto"));
                sb.AppendLine("IsHiddenWifiEnabled=" + IsHiddenWifiEnabled);
                sb.AppendLine("SavedVisibleSsid=" + (SavedVisibleSsid ?? ""));
                sb.AppendLine("BlockNewUnknownDevices=" + BlockNewUnknownDevices);
                sb.AppendLine("GlobalDownloadLimitEnabled=" + GlobalDownloadLimitEnabled);
                sb.AppendLine("GlobalDownloadLimitMB=" + GlobalDownloadLimitMB.ToString("0.##", CultureInfo.InvariantCulture));
                sb.AppendLine("GlobalSpeedLimitEnabled=" + GlobalSpeedLimitEnabled);
                sb.AppendLine("GlobalMaxSpeedKbps=" + GlobalMaxSpeedKbps.ToString("0.##", CultureInfo.InvariantCulture));
                File.WriteAllText(path, sb.ToString(), Encoding.UTF8);
            }
            catch
            {
            }
        }

        public static Dictionary<string, DevicePolicyRecord> LoadDevices()
        {
            Dictionary<string, DevicePolicyRecord> map = new Dictionary<string, DevicePolicyRecord>(StringComparer.OrdinalIgnoreCase);
            try
            {
                string path = GetDevicesFilePath();
                if (!File.Exists(path)) return map;
                string[] lines = File.ReadAllLines(path, Encoding.UTF8);
                foreach (string line in lines)
                {
                    if (string.IsNullOrWhiteSpace(line) || line.StartsWith("#")) continue;
                    string[] parts = line.Split('|');
                    if (parts.Length < 8) continue;
                    string mac = parts[0].Trim();
                    if (string.IsNullOrEmpty(mac)) continue;

                    bool blocked;
                    long downBytes, upBytes;
                    double maxMb, maxKbps;
                    bool.TryParse(parts[4], out blocked);
                    long.TryParse(parts[5], out downBytes);
                    long.TryParse(parts[6], out upBytes);
                    double.TryParse(parts[7], NumberStyles.Any, CultureInfo.InvariantCulture, out maxMb);
                    maxKbps = 0;
                    if (parts.Length >= 9) double.TryParse(parts[8], NumberStyles.Any, CultureInfo.InvariantCulture, out maxKbps);

                    DevicePolicyRecord rec = new DevicePolicyRecord
                    {
                        MacAddress = mac,
                        LastIpAddress = parts[1],
                        HostName = parts[2],
                        CustomName = parts[3],
                        IsBlocked = blocked,
                        DownloadedBytes = Math.Max(0L, downBytes),
                        UploadedBytes = Math.Max(0L, upBytes),
                        MaxDownloadLimitMB = Math.Max(0.0, maxMb),
                        MaxSpeedKbps = Math.Max(0.0, maxKbps),
                        FirstSeen = DateTime.Now,
                        LastSeen = DateTime.Now
                    };
                    map[mac] = rec;
                }
            }
            catch
            {
            }
            return map;
        }

        public static void SaveDevices(IEnumerable<DevicePolicyRecord> devices)
        {
            try
            {
                string path = GetDevicesFilePath();
                StringBuilder sb = new StringBuilder();
                sb.AppendLine("# MAC|LastIP|HostName|CustomName|IsBlocked|DownBytes|UpBytes|MaxDownloadMB|MaxSpeedKbps");
                foreach (DevicePolicyRecord d in devices)
                {
                    if (d == null || string.IsNullOrEmpty(d.MacAddress)) continue;
                    string cleanHost = (d.HostName ?? "").Replace("|", " ");
                    string cleanCustom = (d.CustomName ?? "").Replace("|", " ");
                    sb.AppendLine(string.Format(CultureInfo.InvariantCulture, "{0}|{1}|{2}|{3}|{4}|{5}|{6}|{7:0.##}|{8:0.##}",
                        d.MacAddress,
                        d.LastIpAddress ?? "",
                        cleanHost,
                        cleanCustom,
                        d.IsBlocked,
                        d.DownloadedBytes,
                        d.UploadedBytes,
                        d.MaxDownloadLimitMB,
                        d.MaxSpeedKbps));
                }
                File.WriteAllText(path, sb.ToString(), Encoding.UTF8);
            }
            catch
            {
            }
        }

        private const string RunRegKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        private const string RunValueName = "PCWifi";

        public static bool IsRunAtStartupEnabled()
        {
            try
            {
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(RunRegKey, false))
                {
                    if (key == null) return false;
                    object val = key.GetValue(RunValueName);
                    return val != null && !string.IsNullOrEmpty(val.ToString());
                }
            }
            catch
            {
                return false;
            }
        }

        public static bool SetRunAtStartup(bool enable)
        {
            try
            {
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(RunRegKey, true))
                {
                    if (key == null) return false;
                    if (enable)
                    {
                        string exePath = Process.GetCurrentProcess().MainModule.FileName;
                        key.SetValue(RunValueName, "\"" + exePath + "\" --minimized");
                    }
                    else
                    {
                        key.DeleteValue(RunValueName, false);
                    }
                    return true;
                }
            }
            catch
            {
                return false;
            }
        }
    }
    #endregion

    #region Data Formatting & Network Enforcement Helpers
    internal static class DataFormatter
    {
        public static string FormatBytes(long bytes)
        {
            if (bytes < 0) bytes = 0;
            if (bytes < 1024) return bytes.ToString(CultureInfo.InvariantCulture) + " B";
            double kb = bytes / 1024.0;
            if (kb < 1024.0) return kb.ToString("0.0", CultureInfo.InvariantCulture) + " KB";
            double mb = kb / 1024.0;
            if (mb < 1024.0) return mb.ToString("0.0", CultureInfo.InvariantCulture) + " MB";
            double gb = mb / 1024.0;
            return gb.ToString("0.00", CultureInfo.InvariantCulture) + " GB";
        }

        public static string FormatSpeedKbps(double kbps)
        {
            if (kbps < 0.05) return "0.0 KB/s";
            if (kbps < 1024.0) return kbps.ToString("0.0", CultureInfo.InvariantCulture) + " KB/s";
            double mbs = kbps / 1024.0;
            return mbs.ToString("0.00", CultureInfo.InvariantCulture) + " MB/s";
        }
    }

    internal sealed class NetworkEnforcementEngine
    {
        private WiFiDirectAdvertisementPublisher _stealthPublisher;

        // Stealth / Hidden SSID characters (accepted by Windows Mobile Hotspot while appearing blank in Wi-Fi scanners)
        public const string StealthSsidToken = "\u00A0\u200B\u00A0\u200B\u00A0\u200B\u00A0\u200B";

        public static bool IsStealthSsid(string ssid)
        {
            if (string.IsNullOrEmpty(ssid)) return false;
            return ssid.Contains("\u200B") || string.IsNullOrWhiteSpace(ssid.Replace("\u00A0", " "));
        }

        public void SetWifiDirectStealthDiscoverability(bool stealth)
        {
            try
            {
                if (stealth)
                {
                    if (_stealthPublisher == null)
                    {
                        _stealthPublisher = new WiFiDirectAdvertisementPublisher();
                    }
                    _stealthPublisher.Advertisement.ListenStateDiscoverability =
                        WiFiDirectAdvertisementListenStateDiscoverability.None;
                    if (_stealthPublisher.Status != WiFiDirectAdvertisementPublisherStatus.Started)
                    {
                        _stealthPublisher.Start();
                    }
                }
                else
                {
                    if (_stealthPublisher != null &&
                        _stealthPublisher.Status == WiFiDirectAdvertisementPublisherStatus.Started)
                    {
                        _stealthPublisher.Stop();
                    }
                }
            }
            catch
            {
            }
        }

        public static NetworkInterface FindHotspotVirtualInterface(out string gatewayIp)
        {
            gatewayIp = "192.168.137.1";
            try
            {
                NetworkInterface[] interfaces = NetworkInterface.GetAllNetworkInterfaces();
                NetworkInterface fallbackVirtual = null;

                foreach (NetworkInterface ni in interfaces)
                {
                    if (ni.OperationalStatus != OperationalStatus.Up) continue;
                    string desc = ni.Description ?? "";
                    string name = ni.Name ?? "";

                    try
                    {
                        IPInterfaceProperties ipProps = ni.GetIPProperties();
                        foreach (UnicastIPAddressInformation uni in ipProps.UnicastAddresses)
                        {
                            if (uni.Address != null)
                            {
                                string ipStr = uni.Address.ToString();
                                if (ipStr.StartsWith("192.168.137."))
                                {
                                    gatewayIp = ipStr;
                                    return ni;
                                }
                            }
                        }
                    }
                    catch
                    {
                    }

                    if (desc.IndexOf("Wi-Fi Direct Virtual Adapter", StringComparison.OrdinalIgnoreCase) >= 0 ||
                        desc.IndexOf("Hosted Network Virtual Adapter", StringComparison.OrdinalIgnoreCase) >= 0 ||
                        name.IndexOf("Local Area Connection*", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        fallbackVirtual = ni;
                    }
                }

                return fallbackVirtual;
            }
            catch
            {
                return null;
            }
        }

        public static Dictionary<string, string> GetHotspotArpMap(string gatewayIp)
        {
            Dictionary<string, string> macToIp = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                ProcessStartInfo psi = new ProcessStartInfo("arp", "-a")
                {
                    CreateNoWindow = true,
                    UseShellExecute = false,
                    RedirectStandardOutput = true
                };
                using (Process p = Process.Start(psi))
                {
                    string output = p.StandardOutput.ReadToEnd();
                    p.WaitForExit(2000);
                    string[] lines = output.Split(new char[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
                    foreach (string line in lines)
                    {
                        Match m = Regex.Match(line, @"^\s*(192\.168\.137\.\d+)\s+([0-9a-fA-F\-]{17})\s+");
                        if (m.Success)
                        {
                            string ip = m.Groups[1].Value;
                            if (ip == "192.168.137.1" || ip == "192.168.137.255") continue;
                            string mac = m.Groups[2].Value.Replace('-', ':').ToLowerInvariant();
                            if (mac != "ff:ff:ff:ff:ff:ff" && mac != "00:00:00:00:00:00")
                            {
                                macToIp[mac] = ip;
                            }
                        }
                    }
                }
            }
            catch
            {
            }
            return macToIp;
        }

        private static bool RunShellCommands(string commands, bool requestElevationIfNotAdmin)
        {
            try
            {
                bool isAdmin = NativeMethods.IsRunningAsAdministrator();
                if (isAdmin || !requestElevationIfNotAdmin)
                {
                    ProcessStartInfo psi = new ProcessStartInfo("cmd.exe", "/c " + commands)
                    {
                        CreateNoWindow = true,
                        UseShellExecute = false,
                        WindowStyle = ProcessWindowStyle.Hidden
                    };
                    using (Process p = Process.Start(psi))
                    {
                        p.WaitForExit(4000);
                        return p.ExitCode == 0;
                    }
                }
                else
                {
                    ProcessStartInfo psi = new ProcessStartInfo("cmd.exe", "/c " + commands)
                    {
                        Verb = "runas",
                        UseShellExecute = true,
                        WindowStyle = ProcessWindowStyle.Hidden
                    };
                    using (Process p = Process.Start(psi))
                    {
                        p.WaitForExit(6000);
                        return p.ExitCode == 0;
                    }
                }
            }
            catch
            {
                return false;
            }
        }

        public static bool ApplyDeviceBlockRule(string macAddress, string ipAddress, bool requestElevation)
        {
            string safeMac = (macAddress ?? "unknown").Replace(":", "").Replace("-", "");
            string ruleIn = "PCWifi_Block_In_" + safeMac;
            string ruleOut = "PCWifi_Block_Out_" + safeMac;

            string gwIp;
            NetworkInterface hotspotNi = FindHotspotVirtualInterface(out gwIp);
            string ifaceName = hotspotNi != null ? hotspotNi.Name : "Local Area Connection* 12";

            StringBuilder cmd = new StringBuilder();
            cmd.Append("netsh advfirewall firewall delete rule name=\"" + ruleIn + "\" >nul 2>&1 & ");
            cmd.Append("netsh advfirewall firewall delete rule name=\"" + ruleOut + "\" >nul 2>&1");

            if (!string.IsNullOrEmpty(ipAddress) && ipAddress.Contains("."))
            {
                cmd.Append(" & netsh advfirewall firewall add rule name=\"" + ruleIn + "\" dir=in action=block remoteip=" + ipAddress + " profile=any >nul 2>&1");
                cmd.Append(" & netsh advfirewall firewall add rule name=\"" + ruleOut + "\" dir=out action=block remoteip=" + ipAddress + " profile=any >nul 2>&1");
                cmd.Append(" & netsh interface ipv4 delete neighbors \"" + ifaceName + "\" " + ipAddress + " >nul 2>&1");
                cmd.Append(" & netsh interface ipv4 set neighbors \"" + ifaceName + "\" " + ipAddress + " 00-00-00-00-00-00 store=active >nul 2>&1");
            }

            return RunShellCommands(cmd.ToString(), requestElevation);
        }

        public static bool RemoveDeviceBlockRule(string macAddress, string ipAddress, bool requestElevation)
        {
            string safeMac = (macAddress ?? "unknown").Replace(":", "").Replace("-", "");
            string ruleIn = "PCWifi_Block_In_" + safeMac;
            string ruleOut = "PCWifi_Block_Out_" + safeMac;

            string gwIp;
            NetworkInterface hotspotNi = FindHotspotVirtualInterface(out gwIp);
            string ifaceName = hotspotNi != null ? hotspotNi.Name : "Local Area Connection* 12";

            StringBuilder cmd = new StringBuilder();
            cmd.Append("netsh advfirewall firewall delete rule name=\"" + ruleIn + "\" >nul 2>&1 & ");
            cmd.Append("netsh advfirewall firewall delete rule name=\"" + ruleOut + "\" >nul 2>&1");

            if (!string.IsNullOrEmpty(ipAddress) && ipAddress.Contains("."))
            {
                cmd.Append(" & netsh interface ipv4 delete neighbors \"" + ifaceName + "\" " + ipAddress + " >nul 2>&1");
            }

            return RunShellCommands(cmd.ToString(), requestElevation);
        }

        public static void ApplyQosSpeedLimit(string policySuffix, string targetIpOrSubnet, double maxKbps, bool requestElevation)
        {
            try
            {
                string policyName = "PCWifi_Speed_" + policySuffix;
                long bitsPerSec = (long)(maxKbps * 1024.0 * 8.0);
                string psCmd;
                if (maxKbps <= 0)
                {
                    psCmd = "Remove-NetQosPolicy -Name '" + policyName + "' -Confirm:$false -ErrorAction SilentlyContinue";
                }
                else
                {
                    psCmd = "Remove-NetQosPolicy -Name '" + policyName + "' -Confirm:$false -ErrorAction SilentlyContinue; " +
                            "New-NetQosPolicy -Name '" + policyName + "' -IPDstPrefixMatchCondition '" + targetIpOrSubnet + "' -ThrottleRateActionBitsPerSecond " + bitsPerSec + " -ErrorAction SilentlyContinue";
                }
                RunShellCommands("powershell -NoProfile -NonInteractive -Command \"" + psCmd + "\"", requestElevation);
            }
            catch
            {
            }
        }

        // Brief ARP gate pulse when a device exceeds its configured MaxSpeedKbps
        public static void PulseThrottleDeviceArp(string ipAddress, string realMacAddress, int pauseMs)
        {
            if (string.IsNullOrEmpty(ipAddress) || !ipAddress.StartsWith("192.168.137.")) return;
            if (!NativeMethods.IsRunningAsAdministrator()) return;
            Task.Run(async delegate
            {
                try
                {
                    string gwIp;
                    NetworkInterface hotspotNi = FindHotspotVirtualInterface(out gwIp);
                    if (hotspotNi == null) return;
                    string ifaceName = hotspotNi.Name;
                    string dashMac = (realMacAddress ?? "").Replace(':', '-');

                    RunShellCommands("netsh interface ipv4 set neighbors \"" + ifaceName + "\" " + ipAddress + " 00-00-00-00-00-00 store=active >nul 2>&1", false);
                    await Task.Delay(Math.Min(700, Math.Max(150, pauseMs)));
                    if (!string.IsNullOrEmpty(dashMac) && dashMac.Length == 17)
                    {
                        RunShellCommands("netsh interface ipv4 set neighbors \"" + ifaceName + "\" " + ipAddress + " " + dashMac + " store=active >nul 2>&1", false);
                    }
                    else
                    {
                        RunShellCommands("netsh interface ipv4 delete neighbors \"" + ifaceName + "\" " + ipAddress + " >nul 2>&1", false);
                    }
                }
                catch
                {
                }
            });
        }
    }
    #endregion

    #region Hotspot Snapshot & WinRT Controller
    internal sealed class ConnectedClientSnapshot
    {
        public string HostName { get; set; }
        public string IpAddress { get; set; }
        public string MacAddress { get; set; }
    }

    internal sealed class HotspotSnapshot
    {
        public bool IsAvailable { get; set; }
        public string ProfileName { get; set; }
        public string ProfileType { get; set; }
        public TetheringOperationalState OperationalState { get; set; }
        public string Ssid { get; set; }
        public string Passphrase { get; set; }
        public TetheringWiFiBand Band { get; set; }
        public uint ClientCount { get; set; }
        public uint MaxClientCount { get; set; }
        public bool IsOsTimeoutEnabled { get; set; }
        public List<ConnectedClientSnapshot> Clients { get; set; }
        public List<string> AvailableProfiles { get; set; }
        public string ErrorMessage { get; set; }

        public HotspotSnapshot()
        {
            Clients = new List<ConnectedClientSnapshot>();
            AvailableProfiles = new List<string>();
            ProfileName = "None";
            ProfileType = "Wi-Fi";
            Ssid = "";
            Passphrase = "";
            Band = TetheringWiFiBand.Auto;
            MaxClientCount = 8;
        }
    }

    internal sealed class HotspotController
    {
        public ConnectionProfile ResolveConnectionProfile(string preferredName, out List<string> availableProfiles)
        {
            availableProfiles = new List<string>();
            ConnectionProfile internetProfile = null;
            try
            {
                internetProfile = NetworkInformation.GetInternetConnectionProfile();
            }
            catch
            {
            }

            IReadOnlyList<ConnectionProfile> allProfiles = null;
            try
            {
                allProfiles = NetworkInformation.GetConnectionProfiles();
            }
            catch
            {
            }

            ConnectionProfile matchedPreferred = null;
            ConnectionProfile firstConnected = null;
            ConnectionProfile fallbackAny = null;

            if (allProfiles != null)
            {
                foreach (ConnectionProfile p in allProfiles)
                {
                    if (p == null || string.IsNullOrWhiteSpace(p.ProfileName)) continue;
                    NetworkConnectivityLevel level = NetworkConnectivityLevel.None;
                    try
                    {
                        level = p.GetNetworkConnectivityLevel();
                    }
                    catch
                    {
                    }

                    if (level != NetworkConnectivityLevel.None ||
                        (internetProfile != null && p.ProfileName == internetProfile.ProfileName))
                    {
                        if (!availableProfiles.Contains(p.ProfileName))
                        {
                            availableProfiles.Add(p.ProfileName);
                        }
                        if (firstConnected == null) firstConnected = p;
                    }

                    if (fallbackAny == null) fallbackAny = p;

                    if (!string.IsNullOrEmpty(preferredName) &&
                        preferredName != "Auto" &&
                        string.Equals(p.ProfileName, preferredName, StringComparison.OrdinalIgnoreCase))
                    {
                        matchedPreferred = p;
                    }
                }
            }

            if (internetProfile != null && !string.IsNullOrWhiteSpace(internetProfile.ProfileName))
            {
                if (!availableProfiles.Contains(internetProfile.ProfileName))
                {
                    availableProfiles.Insert(0, internetProfile.ProfileName);
                }
            }

            if (matchedPreferred != null) return matchedPreferred;
            if (internetProfile != null) return internetProfile;
            if (firstConnected != null) return firstConnected;
            return fallbackAny;
        }

        public HotspotSnapshot GetSnapshot(string preferredProfile)
        {
            HotspotSnapshot snap = new HotspotSnapshot();
            try
            {
                snap.IsOsTimeoutEnabled = NetworkOperatorTetheringManager.IsNoConnectionsTimeoutEnabled();
            }
            catch
            {
            }

            try
            {
                List<string> profiles;
                ConnectionProfile profile = ResolveConnectionProfile(preferredProfile, out profiles);
                snap.AvailableProfiles = profiles;

                if (profile == null)
                {
                    snap.IsAvailable = false;
                    snap.ErrorMessage = "No network connection profile found on this PC.";
                    return snap;
                }

                snap.ProfileName = profile.ProfileName;
                snap.ProfileType = profile.IsWlanConnectionProfile ? "Wi-Fi" : (profile.IsWwanConnectionProfile ? "Cellular" : "Ethernet");

                NetworkOperatorTetheringManager tm = NetworkOperatorTetheringManager.CreateFromConnectionProfile(profile);
                snap.IsAvailable = true;
                snap.OperationalState = tm.TetheringOperationalState;
                snap.ClientCount = tm.ClientCount;
                snap.MaxClientCount = tm.MaxClientCount > 0 ? tm.MaxClientCount : 8u;

                NetworkOperatorTetheringAccessPointConfiguration cfg = tm.GetCurrentAccessPointConfiguration();
                if (cfg != null)
                {
                    snap.Ssid = cfg.Ssid ?? "";
                    snap.Passphrase = cfg.Passphrase ?? "";
                    snap.Band = cfg.Band;
                }

                Dictionary<string, string> arpMap = null;
                try
                {
                    IReadOnlyList<NetworkOperatorTetheringClient> rawClients = tm.GetTetheringClients();
                    if (rawClients != null && rawClients.Count > 0)
                    {
                        foreach (NetworkOperatorTetheringClient c in rawClients)
                        {
                            if (c == null) continue;
                            string mac = string.IsNullOrEmpty(c.MacAddress) ? "Unknown MAC" : c.MacAddress.ToLowerInvariant();
                            string host = "Connected Device";
                            string ip = "";

                            try
                            {
                                if (c.HostNames != null && c.HostNames.Count > 0)
                                {
                                    List<string> names = new List<string>();
                                    foreach (HostName hn in c.HostNames)
                                    {
                                        if (hn == null || string.IsNullOrEmpty(hn.CanonicalName)) continue;
                                        if (hn.Type == HostNameType.Ipv4)
                                        {
                                            ip = hn.CanonicalName;
                                        }
                                        else if (hn.Type != HostNameType.Ipv6)
                                        {
                                            names.Add(hn.CanonicalName);
                                        }
                                    }
                                    if (names.Count > 0) host = names[0];
                                }
                            }
                            catch
                            {
                            }

                            if (string.IsNullOrEmpty(ip))
                            {
                                if (arpMap == null) arpMap = NetworkEnforcementEngine.GetHotspotArpMap("192.168.137.1");
                                string resolvedIp;
                                if (arpMap.TryGetValue(mac, out resolvedIp))
                                {
                                    ip = resolvedIp;
                                }
                                else
                                {
                                    ip = "192.168.137.x";
                                }
                            }

                            snap.Clients.Add(new ConnectedClientSnapshot
                            {
                                HostName = host,
                                IpAddress = ip,
                                MacAddress = mac
                            });
                        }
                    }
                }
                catch
                {
                }
            }
            catch (Exception ex)
            {
                snap.IsAvailable = false;
                snap.ErrorMessage = ex.Message;
            }
            return snap;
        }

        public bool EnsureNoConnectionsTimeoutDisabled()
        {
            try
            {
                if (NetworkOperatorTetheringManager.IsNoConnectionsTimeoutEnabled())
                {
                    NetworkOperatorTetheringManager.DisableNoConnectionsTimeout();
                    return true;
                }
            }
            catch
            {
            }
            return false;
        }

        public bool SetNoConnectionsTimeout(bool enableTimeout)
        {
            try
            {
                if (enableTimeout)
                {
                    NetworkOperatorTetheringManager.EnableNoConnectionsTimeout();
                }
                else
                {
                    NetworkOperatorTetheringManager.DisableNoConnectionsTimeout();
                }
                return true;
            }
            catch
            {
                return false;
            }
        }

        public async Task<Tuple<bool, string>> StartHotspotAsync(string preferredProfile, bool disableTimeoutFirst)
        {
            try
            {
                if (disableTimeoutFirst)
                {
                    try { NetworkOperatorTetheringManager.DisableNoConnectionsTimeout(); } catch { }
                }

                List<string> profiles;
                ConnectionProfile profile = ResolveConnectionProfile(preferredProfile, out profiles);
                if (profile == null)
                {
                    return Tuple.Create(false, "No active network profile found to share.");
                }

                NetworkOperatorTetheringManager tm = NetworkOperatorTetheringManager.CreateFromConnectionProfile(profile);
                if (tm.TetheringOperationalState == TetheringOperationalState.On)
                {
                    return Tuple.Create(true, "Wi-Fi hotspot is already active.");
                }

                int waitLoops = 0;
                while (tm.TetheringOperationalState == TetheringOperationalState.InTransition && waitLoops < 25)
                {
                    await Task.Delay(200);
                    waitLoops++;
                }

                NetworkOperatorTetheringOperationResult result = await WinRtAsync.RunAsync(
                    () => tm.StartTetheringAsync());

                if (result != null && result.Status == TetheringOperationStatus.Success)
                {
                    return Tuple.Create(true, "Wi-Fi hotspot started sharing '" + profile.ProfileName + "'.");
                }

                string statusText = result != null ? result.Status.ToString() : "Unknown";
                string extra = (result != null && !string.IsNullOrEmpty(result.AdditionalErrorMessage))
                    ? (" (" + result.AdditionalErrorMessage + ")")
                    : "";
                return Tuple.Create(false, "Could not start hotspot: " + statusText + extra);
            }
            catch (Exception ex)
            {
                return Tuple.Create(false, "Error starting hotspot: " + ex.Message);
            }
        }

        public async Task<Tuple<bool, string>> StopHotspotAsync(string preferredProfile)
        {
            try
            {
                List<string> profiles;
                ConnectionProfile profile = ResolveConnectionProfile(preferredProfile, out profiles);
                if (profile == null)
                {
                    return Tuple.Create(false, "No network profile found.");
                }

                NetworkOperatorTetheringManager tm = NetworkOperatorTetheringManager.CreateFromConnectionProfile(profile);
                if (tm.TetheringOperationalState == TetheringOperationalState.Off)
                {
                    return Tuple.Create(true, "Wi-Fi hotspot is already off.");
                }

                NetworkOperatorTetheringOperationResult result = await WinRtAsync.RunAsync(
                    () => tm.StopTetheringAsync());

                if (result != null && result.Status == TetheringOperationStatus.Success)
                {
                    return Tuple.Create(true, "Wi-Fi hotspot turned off.");
                }

                string statusText = result != null ? result.Status.ToString() : "Unknown";
                return Tuple.Create(false, "Could not stop hotspot: " + statusText);
            }
            catch (Exception ex)
            {
                return Tuple.Create(false, "Error stopping hotspot: " + ex.Message);
            }
        }

        public async Task<Tuple<bool, string>> RestartHotspotToKickClientAsync(string preferredProfile)
        {
            try
            {
                List<string> profiles;
                ConnectionProfile profile = ResolveConnectionProfile(preferredProfile, out profiles);
                if (profile == null) return Tuple.Create(false, "No network profile found.");

                NetworkOperatorTetheringManager tm = NetworkOperatorTetheringManager.CreateFromConnectionProfile(profile);
                if (tm.TetheringOperationalState == TetheringOperationalState.On)
                {
                    await WinRtAsync.RunAsync(() => tm.StopTetheringAsync());
                    await Task.Delay(500);
                    await WinRtAsync.RunAsync(() => tm.StartTetheringAsync());
                    return Tuple.Create(true, "Disconnected client and refreshed Wi-Fi hotspot session.");
                }
                return Tuple.Create(true, "Hotspot is currently off.");
            }
            catch (Exception ex)
            {
                return Tuple.Create(false, "Failed to disconnect client: " + ex.Message);
            }
        }

        public async Task<Tuple<bool, string>> ConfigureAccessPointAsync(
            string preferredProfile,
            string newSsid,
            string newPassphrase,
            TetheringWiFiBand newBand,
            bool allowStealthWhitespace = false)
        {
            try
            {
                if (!allowStealthWhitespace && string.IsNullOrWhiteSpace(newSsid))
                {
                    return Tuple.Create(false, "Network name (SSID) cannot be empty.");
                }
                if (string.IsNullOrEmpty(newPassphrase) || newPassphrase.Length < 8 || newPassphrase.Length > 63)
                {
                    return Tuple.Create(false, "Network password must be between 8 and 63 characters.");
                }

                List<string> profiles;
                ConnectionProfile profile = ResolveConnectionProfile(preferredProfile, out profiles);
                if (profile == null)
                {
                    return Tuple.Create(false, "No network profile available to configure.");
                }

                NetworkOperatorTetheringManager tm = NetworkOperatorTetheringManager.CreateFromConnectionProfile(profile);
                bool wasOn = (tm.TetheringOperationalState == TetheringOperationalState.On);

                NetworkOperatorTetheringAccessPointConfiguration cfg = tm.GetCurrentAccessPointConfiguration();
                cfg.Ssid = allowStealthWhitespace ? newSsid : newSsid.Trim();
                cfg.Passphrase = newPassphrase;

                bool bandSupported = true;
                try
                {
                    bandSupported = await WinRtAsync.RunAsync(() => cfg.IsBandSupportedAsync(newBand));
                }
                catch
                {
                }

                if (!bandSupported && newBand != TetheringWiFiBand.Auto)
                {
                    return Tuple.Create(false, "The selected Wi-Fi band (" + FormatBand(newBand) + ") is not supported by your Wi-Fi adapter.");
                }

                cfg.Band = newBand;

                if (wasOn)
                {
                    await WinRtAsync.RunAsync(() => tm.StopTetheringAsync());
                    await Task.Delay(400);
                }

                await WinRtAsync.RunAsync(() => tm.ConfigureAccessPointAsync(cfg));

                if (wasOn)
                {
                    await Task.Delay(300);
                    await WinRtAsync.RunAsync(() => tm.StartTetheringAsync());
                }

                string displaySsid = NetworkEnforcementEngine.IsStealthSsid(cfg.Ssid) ? "[Hidden Stealth SSID]" : cfg.Ssid;
                return Tuple.Create(true, "Updated Wi-Fi configuration (SSID: " + displaySsid + ", Band: " + FormatBand(newBand) + ").");
            }
            catch (Exception ex)
            {
                return Tuple.Create(false, "Failed to update configuration: " + ex.Message);
            }
        }

        public static string FormatBand(TetheringWiFiBand band)
        {
            switch (band)
            {
                case TetheringWiFiBand.FiveGigahertz:
                    return "5 GHz";
                case TetheringWiFiBand.TwoPointFourGigahertz:
                    return "2.4 GHz";
                default:
                    return "Any available";
            }
        }
    }
    #endregion

    #region Theme Palette (Windows 11 Modern Dark & Light Mode)
    internal sealed class Win11Theme
    {
        public bool IsDark { get; private set; }
        public SolidColorBrush WindowBg { get; private set; }
        public SolidColorBrush SidebarBg { get; private set; }
        public SolidColorBrush ContentBg { get; private set; }
        public SolidColorBrush CardBg { get; private set; }
        public SolidColorBrush CardHoverBg { get; private set; }
        public SolidColorBrush CardSecondaryBg { get; private set; }
        public SolidColorBrush CardBorder { get; private set; }
        public SolidColorBrush DividerBrush { get; private set; }
        public SolidColorBrush TextPrimary { get; private set; }
        public SolidColorBrush TextSecondary { get; private set; }
        public SolidColorBrush TextMuted { get; private set; }
        public SolidColorBrush AccentBrush { get; private set; }
        public SolidColorBrush AccentHoverBrush { get; private set; }
        public SolidColorBrush AccentTextBrush { get; private set; }
        public SolidColorBrush SubtleButtonBg { get; private set; }
        public SolidColorBrush SubtleButtonHoverBg { get; private set; }
        public SolidColorBrush SubtleButtonBorder { get; private set; }
        public SolidColorBrush NavSelectedBg { get; private set; }
        public SolidColorBrush NavHoverBg { get; private set; }
        public SolidColorBrush SuccessBrush { get; private set; }
        public SolidColorBrush SuccessBgBrush { get; private set; }
        public SolidColorBrush WarningBrush { get; private set; }
        public SolidColorBrush DangerBrush { get; private set; }
        public SolidColorBrush DangerBgBrush { get; private set; }
        public SolidColorBrush InputBg { get; private set; }
        public SolidColorBrush InputBorder { get; private set; }
        public SolidColorBrush ModalOverlayBrush { get; private set; }

        private static SolidColorBrush Hex(string hex)
        {
            Color c = (Color)ColorConverter.ConvertFromString(hex);
            SolidColorBrush b = new SolidColorBrush(c);
            b.Freeze();
            return b;
        }

        public static Win11Theme Create(bool dark)
        {
            Win11Theme t = new Win11Theme();
            t.IsDark = dark;
            if (dark)
            {
                t.WindowBg = Hex("#202020");
                t.SidebarBg = Hex("#202020");
                t.ContentBg = Hex("#202020");
                t.CardBg = Hex("#2B2B2B");
                t.CardHoverBg = Hex("#323232");
                t.CardSecondaryBg = Hex("#242424");
                t.CardBorder = Hex("#373737");
                t.DividerBrush = Hex("#363636");
                t.TextPrimary = Hex("#FFFFFF");
                t.TextSecondary = Hex("#C6C6C6");
                t.TextMuted = Hex("#9A9A9A");
                t.AccentBrush = Hex("#60CDFF");
                t.AccentHoverBrush = Hex("#7AD5FF");
                t.AccentTextBrush = Hex("#000000");
                t.SubtleButtonBg = Hex("#353535");
                t.SubtleButtonHoverBg = Hex("#3F3F3F");
                t.SubtleButtonBorder = Hex("#454545");
                t.NavSelectedBg = Hex("#2D2D2D");
                t.NavHoverBg = Hex("#292929");
                t.SuccessBrush = Hex("#6CCB5F");
                t.SuccessBgBrush = Hex("#1E3520");
                t.WarningBrush = Hex("#FCE100");
                t.DangerBrush = Hex("#FF99A4");
                t.DangerBgBrush = Hex("#3B1D20");
                t.InputBg = Hex("#232323");
                t.InputBorder = Hex("#4A4A4A");
                t.ModalOverlayBrush = Hex("#AA000000");
            }
            else
            {
                t.WindowBg = Hex("#F3F3F3");
                t.SidebarBg = Hex("#F3F3F3");
                t.ContentBg = Hex("#F3F3F3");
                t.CardBg = Hex("#FBFBFB");
                t.CardHoverBg = Hex("#F5F5F5");
                t.CardSecondaryBg = Hex("#F1F1F1");
                t.CardBorder = Hex("#E5E5E5");
                t.DividerBrush = Hex("#E6E6E6");
                t.TextPrimary = Hex("#1A1A1A");
                t.TextSecondary = Hex("#5D5D5D");
                t.TextMuted = Hex("#787878");
                t.AccentBrush = Hex("#0067C0");
                t.AccentHoverBrush = Hex("#1874D0");
                t.AccentTextBrush = Hex("#FFFFFF");
                t.SubtleButtonBg = Hex("#FFFFFF");
                t.SubtleButtonHoverBg = Hex("#F4F4F4");
                t.SubtleButtonBorder = Hex("#D6D6D6");
                t.NavSelectedBg = Hex("#EAEAEA");
                t.NavHoverBg = Hex("#ECECEC");
                t.SuccessBrush = Hex("#107C10");
                t.SuccessBgBrush = Hex("#DFF6DD");
                t.WarningBrush = Hex("#9D5D00");
                t.DangerBrush = Hex("#C42B1C");
                t.DangerBgBrush = Hex("#FDE7E9");
                t.InputBg = Hex("#FFFFFF");
                t.InputBorder = Hex("#CCCCCC");
                t.ModalOverlayBrush = Hex("#77000000");
            }
            return t;
        }
    }
    #endregion

    #region Custom Windows 11 Controls (Win11Dropdown & Win11ToggleSwitch)
    internal sealed class Win11Dropdown : Border
    {
        private readonly Win11Theme _theme;
        private readonly FontFamily _iconFont;
        private readonly TextBlock _selectedText;
        private readonly Popup _popup;
        private readonly StackPanel _itemsPanel;
        private readonly List<string> _items = new List<string>();
        private int _selectedIndex = -1;

        public event EventHandler SelectionChanged;

        public int SelectedIndex
        {
            get { return _selectedIndex; }
            set
            {
                if (value < -1 || value >= _items.Count) return;
                _selectedIndex = value;
                _selectedText.Text = (_selectedIndex >= 0 && _selectedIndex < _items.Count) ? _items[_selectedIndex] : "";
                RebuildPopupItems();
                if (SelectionChanged != null)
                {
                    SelectionChanged(this, EventArgs.Empty);
                }
            }
        }

        public string SelectedItem
        {
            get
            {
                return (_selectedIndex >= 0 && _selectedIndex < _items.Count) ? _items[_selectedIndex] : null;
            }
        }

        public void ClearItems()
        {
            _items.Clear();
            _selectedIndex = -1;
            _selectedText.Text = "";
            RebuildPopupItems();
        }

        public void AddItem(string item)
        {
            _items.Add(item ?? "");
            if (_selectedIndex == -1)
            {
                _selectedIndex = 0;
                _selectedText.Text = _items[0];
            }
            RebuildPopupItems();
        }

        public Win11Dropdown(Win11Theme theme, FontFamily iconFont, double width)
        {
            _theme = theme;
            _iconFont = iconFont;
            if (!double.IsNaN(width) && width > 0)
            {
                Width = width;
            }
            Height = 32;
            Background = _theme.SubtleButtonBg;
            BorderBrush = _theme.SubtleButtonBorder;
            BorderThickness = new Thickness(1);
            CornerRadius = new CornerRadius(4);
            Padding = new Thickness(11, 0, 10, 0);
            Cursor = Cursors.Hand;

            Grid contentGrid = new Grid();
            contentGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            contentGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            _selectedText = new TextBlock
            {
                Text = "",
                FontSize = 13,
                Foreground = _theme.TextPrimary,
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis
            };
            Grid.SetColumn(_selectedText, 0);
            contentGrid.Children.Add(_selectedText);

            TextBlock chevron = new TextBlock
            {
                FontFamily = _iconFont,
                Text = "\uE70D",
                FontSize = 10,
                Foreground = _theme.TextSecondary,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(8, 1, 0, 0)
            };
            Grid.SetColumn(chevron, 1);
            contentGrid.Children.Add(chevron);

            _itemsPanel = new StackPanel();
            Border popupBorder = new Border
            {
                Background = _theme.CardBg,
                BorderBrush = _theme.CardBorder,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(4),
                Margin = new Thickness(0, 2, 0, 8),
                MinWidth = (!double.IsNaN(width) && width > 0) ? width : 220,
                Effect = new DropShadowEffect
                {
                    BlurRadius = 16,
                    ShadowDepth = 4,
                    Opacity = 0.38
                },
                Child = _itemsPanel
            };

            _popup = new Popup
            {
                PlacementTarget = this,
                Placement = PlacementMode.Bottom,
                AllowsTransparency = true,
                StaysOpen = false,
                PopupAnimation = PopupAnimation.Fade,
                Child = popupBorder
            };

            Grid wrapper = new Grid();
            wrapper.Children.Add(contentGrid);
            wrapper.Children.Add(_popup);
            Child = wrapper;

            MouseEnter += delegate { Background = _theme.SubtleButtonHoverBg; };
            MouseLeave += delegate { Background = _theme.SubtleButtonBg; };
            MouseLeftButtonUp += delegate(object s, MouseButtonEventArgs e)
            {
                e.Handled = true;
                if (_items.Count == 0) return;
                popupBorder.MinWidth = ActualWidth > 0 ? ActualWidth : 180;
                RebuildPopupItems();
                _popup.IsOpen = !_popup.IsOpen;
            };
        }

        private void RebuildPopupItems()
        {
            _itemsPanel.Children.Clear();
            for (int i = 0; i < _items.Count; i++)
            {
                int capturedIdx = i;
                bool isSel = (i == _selectedIndex);

                Border itemBd = new Border
                {
                    CornerRadius = new CornerRadius(4),
                    Padding = new Thickness(8, 7, 10, 7),
                    Margin = new Thickness(0, 1, 0, 1),
                    Background = isSel ? _theme.NavSelectedBg : Brushes.Transparent,
                    Cursor = Cursors.Hand
                };

                Grid row = new Grid();
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(8) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

                Border pill = new Border
                {
                    Width = 3,
                    Height = 14,
                    CornerRadius = new CornerRadius(1.5),
                    Background = _theme.AccentBrush,
                    HorizontalAlignment = HorizontalAlignment.Left,
                    VerticalAlignment = VerticalAlignment.Center,
                    Visibility = isSel ? Visibility.Visible : Visibility.Hidden
                };
                Grid.SetColumn(pill, 0);
                row.Children.Add(pill);

                TextBlock tb = new TextBlock
                {
                    Text = _items[i],
                    FontSize = 13,
                    Foreground = _theme.TextPrimary,
                    VerticalAlignment = VerticalAlignment.Center
                };
                Grid.SetColumn(tb, 1);
                row.Children.Add(tb);

                itemBd.Child = row;
                itemBd.MouseEnter += delegate
                {
                    if (capturedIdx != _selectedIndex) itemBd.Background = _theme.CardHoverBg;
                };
                itemBd.MouseLeave += delegate
                {
                    if (capturedIdx != _selectedIndex) itemBd.Background = Brushes.Transparent;
                };
                itemBd.MouseLeftButtonUp += delegate(object s, MouseButtonEventArgs e)
                {
                    e.Handled = true;
                    _popup.IsOpen = false;
                    SelectedIndex = capturedIdx;
                };

                _itemsPanel.Children.Add(itemBd);
            }
        }
    }

    internal sealed class Win11ToggleSwitch : Border
    {
        private readonly Border _track;
        private readonly Ellipse _thumb;
        private readonly TextBlock _stateLabel;
        private bool _isChecked;
        private bool _isUpdatingProgrammatically;
        private Win11Theme _theme;

        public event EventHandler<bool> Toggled;

        public bool ShowOnOffText { get; set; }

        public bool IsChecked
        {
            get { return _isChecked; }
            set
            {
                _isChecked = value;
                UpdateVisuals();
            }
        }

        public void SetCheckedSilent(bool value)
        {
            _isUpdatingProgrammatically = true;
            _isChecked = value;
            UpdateVisuals();
            _isUpdatingProgrammatically = false;
        }

        public Win11ToggleSwitch(Win11Theme theme, bool showOnOffText = true)
        {
            _theme = theme;
            ShowOnOffText = showOnOffText;
            Cursor = Cursors.Hand;
            Background = Brushes.Transparent;

            StackPanel root = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                VerticalAlignment = VerticalAlignment.Center
            };

            _stateLabel = new TextBlock
            {
                FontSize = 13,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 10, 0),
                MinWidth = 24,
                TextAlignment = TextAlignment.Right
            };

            _track = new Border
            {
                Width = 40,
                Height = 20,
                CornerRadius = new CornerRadius(10),
                BorderThickness = new Thickness(1),
                Padding = new Thickness(3, 0, 3, 0),
                VerticalAlignment = VerticalAlignment.Center
            };

            _thumb = new Ellipse
            {
                Width = 12,
                Height = 12,
                VerticalAlignment = VerticalAlignment.Center
            };

            _track.Child = _thumb;
            if (ShowOnOffText)
            {
                root.Children.Add(_stateLabel);
            }
            root.Children.Add(_track);
            Child = root;

            MouseLeftButtonUp += delegate(object s, MouseButtonEventArgs e)
            {
                if (!IsEnabled) return;
                e.Handled = true;
                _isChecked = !_isChecked;
                UpdateVisuals();
                if (!_isUpdatingProgrammatically && Toggled != null)
                {
                    Toggled(this, _isChecked);
                }
            };

            UpdateVisuals();
        }

        private void UpdateVisuals()
        {
            if (_theme == null) return;
            _stateLabel.Text = _isChecked ? "On" : "Off";
            _stateLabel.Foreground = _theme.TextPrimary;
            _stateLabel.Visibility = ShowOnOffText ? Visibility.Visible : Visibility.Collapsed;

            if (_isChecked)
            {
                _track.Background = _theme.AccentBrush;
                _track.BorderBrush = _theme.AccentBrush;
                _thumb.Fill = _theme.AccentTextBrush;
                _thumb.HorizontalAlignment = HorizontalAlignment.Right;
            }
            else
            {
                _track.Background = Brushes.Transparent;
                _track.BorderBrush = _theme.TextMuted;
                _thumb.Fill = _theme.TextSecondary;
                _thumb.HorizontalAlignment = HorizontalAlignment.Left;
            }

            Opacity = IsEnabled ? 1.0 : 0.55;
        }
    }
    #endregion

    #region Main PC Wifi Window
    internal sealed class MainWindow : Window
    {
        private AppSettings _settings;
        private readonly HotspotController _controller;
        private readonly NetworkEnforcementEngine _enforcer;
        private Win11Theme _theme;

        private readonly DispatcherTimer _watchdogTimer;
        private readonly DispatcherTimer _trafficTimer;

        private bool _userDesiredHotspotOn;
        private bool _isBusyToggling;
        private bool _isExitingExplicitly;
        private bool _isPasswordRevealed;
        private DateTime? _hotspotActiveSince;
        private int _watchdogCheckCount;
        private int _autoRecoveryCount;
        private DateTime _lastWatchdogCheckTime;
        private string _activePage = "Hotspot";

        private HotspotSnapshot _lastSnapshot = new HotspotSnapshot();
        private Dictionary<string, DevicePolicyRecord> _deviceRecords;
        private readonly List<string> _activityLogs = new List<string>();

        // Traffic & Data Consumption Tracking State
        private bool _trafficBaselineInitialized;
        private long _lastAdapterTxBytes; // Sent from PC to hotspot clients = Client Download
        private long _lastAdapterRxBytes; // Received by PC from hotspot clients = Client Upload
        private double _currentTotalDownloadKbps;
        private double _currentTotalUploadKbps;
        private double _peakDownloadKbps;
        private DateTime _lastTrafficSampleTime = DateTime.Now;
        private bool _globalQuotaTriggeredStop;

        // System Tray
        private System.Windows.Forms.NotifyIcon _trayIcon;
        private System.Windows.Forms.ToolStripMenuItem _trayToggleHotspotItem;
        private System.Windows.Forms.ToolStripMenuItem _trayToggleKeepAliveItem;

        // Font families
        private FontFamily _fontDisplay;
        private FontFamily _fontText;
        private FontFamily _fontIcons;

        // Root layout elements
        private Border _outerWindowBorder;
        private Grid _mainShellGrid;
        private Border _sidebarBorder;
        private TextBlock _titleBarSpeedText;
        private Border _titleBarStatusPill;
        private Ellipse _titleBarStatusDot;
        private TextBlock _titleBarStatusText;
        private TextBlock _sidebarProfileSubText;
        private TextBox _searchBox;
        private TextBlock _pageHeaderTitleText;
        private TextBlock _pageHeaderSubText;
        private StackPanel _pageContainer;
        private TextBlock _sidebarHeartbeatText;
        private Ellipse _sidebarHeartbeatDot;

        // Nav items
        private readonly Dictionary<string, Border> _navItems = new Dictionary<string, Border>();
        private readonly Dictionary<string, Border> _navPills = new Dictionary<string, Border>();
        private readonly Dictionary<string, TextBlock> _navBadges = new Dictionary<string, TextBlock>();

        // Page 1 Controls (Wi-Fi Hotspot)
        private Win11ToggleSwitch _mainHotspotToggle;
        private TextBlock _mainHotspotSubtitle;
        private Border _heroStatusBadge;
        private TextBlock _heroStatusBadgeText;
        private TextBlock _heroPcNameText;
        private TextBlock _heroSsidText;
        private TextBlock _heroDataSummaryText;
        private Win11ToggleSwitch _quickKeepAliveToggle;
        private Win11ToggleSwitch _hiddenWifiToggle;
        private TextBlock _hiddenWifiSubtitle;
        private Win11ToggleSwitch _lockUnknownDevicesToggle;
        private Win11Dropdown _profileComboBox;
        private bool _suppressProfileComboEvent;
        private TextBlock _propSsidValue;
        private TextBlock _propPasswordValue;
        private TextBlock _propBandValue;
        private TextBlock _propVisibilityValue;
        private TextBlock _propEyeIcon;
        private TextBlock _connectedHeaderCountText;
        private StackPanel _inlineConnectedClientsPanel;

        // Page 2 Controls (Connected Devices & Controls)
        private TextBlock _devicesLiveHeaderCount;
        private StackPanel _devicesPageLivePanel;
        private StackPanel _devicesPageBlockedPanel;
        private StackPanel _devicesPageHistoryPanel;

        // Page 3 Controls (Data Usage & Speed Limits)
        private TextBlock _dataStatDownloadedValue;
        private TextBlock _dataStatDownloadedSub;
        private TextBlock _dataStatUploadedValue;
        private TextBlock _dataStatUploadedSub;
        private TextBlock _dataStatSpeedValue;
        private TextBlock _dataStatSpeedSub;
        private Win11ToggleSwitch _globalQuotaToggle;
        private Win11Dropdown _globalQuotaDropdown;
        private bool _suppressGlobalQuotaEvent;
        private ProgressBar _globalQuotaProgressBar;
        private TextBlock _globalQuotaStatusText;
        private Win11ToggleSwitch _globalSpeedToggle;
        private Win11Dropdown _globalSpeedDropdown;
        private bool _suppressGlobalSpeedEvent;
        private StackPanel _perDeviceDataBreakdownPanel;

        // Page 4 Controls (Keep-Alive & Power)
        private TextBlock _statUptimeValue;
        private TextBlock _statChecksValue;
        private TextBlock _statRecoveriesValue;
        private Win11ToggleSwitch _keepAliveMasterToggle;
        private Win11Dropdown _intervalComboBox;
        private Win11ToggleSwitch _forceDisableTimeoutToggle;
        private Win11ToggleSwitch _preventSleepToggle;
        private Win11ToggleSwitch _autoStartOnLaunchToggle;
        private Win11ToggleSwitch _runAtStartupToggle;
        private Win11ToggleSwitch _minimizeToTrayToggle;

        // Page 5 Controls (Activity & Logs)
        private TextBox _logTextBox;

        // Modal 1: "Edit network info"
        private Border _editModalOverlay;
        private TextBox _editSsidBox;
        private TextBox _editPassVisibleBox;
        private PasswordBox _editPassHiddenBox;
        private bool _editDialogShowPass = true;
        private Win11Dropdown _editBandCombo;
        private Win11ToggleSwitch _editHiddenToggle;
        private TextBlock _editErrorText;
        private Button _editSaveBtn;

        // Modal 2: "Device Data & Speed Limits"
        private Border _deviceLimitModalOverlay;
        private DevicePolicyRecord _editingDeviceRecord;
        private TextBlock _devModalTitleText;
        private TextBlock _devModalSubText;
        private TextBox _devCustomNameBox;
        private TextBox _devMaxDownloadMbBox;
        private TextBox _devMaxSpeedKbpsBox;
        private Border _toastBanner;
        private TextBlock _toastText;
        private DispatcherTimer _toastTimer;

        public MainWindow(bool startMinimized, string screenshotDir = null)
        {
            _settings = AppSettings.Load();
            _deviceRecords = AppSettings.LoadDevices();
            _controller = new HotspotController();
            _enforcer = new NetworkEnforcementEngine();
            _theme = Win11Theme.Create(_settings.IsDarkMode);
            _lastWatchdogCheckTime = DateTime.Now;

            InitializeFonts();
            InitializeWindowChrome();
            BuildShellUi();

            _watchdogTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromSeconds(Math.Max(2, _settings.WatchdogIntervalSeconds))
            };
            _watchdogTimer.Tick += async delegate { await RunWatchdogCycleAsync(false); };

            _trafficTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromSeconds(1)
            };
            _trafficTimer.Tick += async delegate
            {
                UpdateUptimeDisplay();
                await SampleTrafficAndEnforceQuotasAsync();
            };

            Loaded += async delegate
            {
                IntPtr hwnd = new WindowInteropHelper(this).Handle;
                NativeMethods.ApplyWin11WindowAttributes(hwnd, _theme.IsDark);
                if (_trayIcon == null)
                {
                    InitializeSystemTray();
                }

                AddLog("PC Wifi initialized. Starting hotspot & traffic controller...");
                if (_settings.ForceDisableOsTimeout)
                {
                    bool changed = _controller.EnsureNoConnectionsTimeoutDisabled();
                    if (changed)
                    {
                        AddLog("Disabled Windows idle hotspot timeout ('Never disconnect when 0 devices connected').");
                    }
                }

                if (_settings.IsHiddenWifiEnabled)
                {
                    _enforcer.SetWifiDirectStealthDiscoverability(true);
                }

                await RefreshHotspotStateAsync(true);

                // Seed initial adapter bytes if hotspot is already running
                InitializeTrafficBaselineFromActiveAdapter();

                if (_lastSnapshot.OperationalState == TetheringOperationalState.On)
                {
                    _userDesiredHotspotOn = true;
                    _hotspotActiveSince = DateTime.Now;
                    AddLog("Hotspot is currently ON. Keep-Alive Guard & Traffic Monitor armed.");
                }
                else if (_settings.AutoStartHotspotOnLaunch)
                {
                    _userDesiredHotspotOn = true;
                    AddLog("Auto-start on launch is enabled. Starting Wi-Fi Hotspot...");
                    await ToggleHotspotFromUserAsync(true);
                }

                // Re-apply any saved blocked devices
                foreach (DevicePolicyRecord rec in _deviceRecords.Values)
                {
                    if (rec.IsBlocked)
                    {
                        NetworkEnforcementEngine.ApplyDeviceBlockRule(rec.MacAddress, rec.LastIpAddress, false);
                    }
                }

                UpdateSleepGuardState();
                _watchdogTimer.Start();
                _trafficTimer.Start();

                if (!string.IsNullOrEmpty(screenshotDir))
                {
                    await Task.Delay(600);
                    CaptureUiPageToPng(System.IO.Path.Combine(screenshotDir, "pcwifi_hotspot.png"));
                    NavigateToPage("Devices");
                    await Task.Delay(250);
                    CaptureUiPageToPng(System.IO.Path.Combine(screenshotDir, "pcwifi_devices.png"));
                    NavigateToPage("DataUsage");
                    await Task.Delay(250);
                    CaptureUiPageToPng(System.IO.Path.Combine(screenshotDir, "pcwifi_data.png"));
                    _isExitingExplicitly = true;
                    Close();
                    return;
                }

                if (startMinimized)
                {
                    HideToTray(false);
                }
            };

            Closing += delegate(object sender, CancelEventArgs e)
            {
                AppSettings.SaveDevices(_deviceRecords.Values);
                if (!_isExitingExplicitly && _settings.MinimizeToTrayOnClose)
                {
                    e.Cancel = true;
                    HideToTray(true);
                }
                else
                {
                    NativeMethods.UpdateSleepPrevention(false);
                    _enforcer.SetWifiDirectStealthDiscoverability(false);
                    if (_trayIcon != null)
                    {
                        _trayIcon.Visible = false;
                        _trayIcon.Dispose();
                    }
                }
            };
        }

        private void InitializeFonts()
        {
            _fontDisplay = new FontFamily("Segoe UI Variable Display, Segoe UI");
            _fontText = new FontFamily("Segoe UI Variable Text, Segoe UI");
            _fontIcons = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets");
        }

        private void InitializeWindowChrome()
        {
            Title = "PC Wifi";
            Width = 1100;
            Height = 770;
            MinWidth = 860;
            MinHeight = 580;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            Background = _theme.WindowBg;
            FontFamily = _fontText;
            UseLayoutRounding = true;
            SnapsToDevicePixels = true;

            try
            {
                string icoPath = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "app.ico");
                if (File.Exists(icoPath))
                {
                    Icon = new System.Windows.Media.Imaging.BitmapImage(new Uri(icoPath, UriKind.Absolute));
                }
            }
            catch
            {
            }

            WindowChrome chrome = new WindowChrome
            {
                CaptionHeight = 0,
                ResizeBorderThickness = new Thickness(6),
                GlassFrameThickness = new Thickness(1),
                CornerRadius = new CornerRadius(8),
                UseAeroCaptionButtons = false
            };
            WindowChrome.SetWindowChrome(this, chrome);
        }

        #region Shell UI Construction (Modern Windows 11 Theme for PC Wifi)
        private void BuildShellUi()
        {
            _outerWindowBorder = new Border
            {
                Background = _theme.WindowBg,
                BorderBrush = _theme.CardBorder,
                BorderThickness = new Thickness(1)
            };

            Grid rootLayerGrid = new Grid();
            _mainShellGrid = new Grid();
            _mainShellGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(42) });
            _mainShellGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

            UIElement titleBar = BuildTitleBar();
            Grid.SetRow(titleBar, 0);
            _mainShellGrid.Children.Add(titleBar);

            Grid bodyGrid = new Grid();
            bodyGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(272) });
            bodyGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            UIElement sidebar = BuildSidebar();
            Grid.SetColumn(sidebar, 0);
            bodyGrid.Children.Add(sidebar);

            UIElement contentArea = BuildContentArea();
            Grid.SetColumn(contentArea, 1);
            bodyGrid.Children.Add(contentArea);

            Grid.SetRow(bodyGrid, 1);
            _mainShellGrid.Children.Add(bodyGrid);

            rootLayerGrid.Children.Add(_mainShellGrid);

            _toastBanner = BuildToastBanner();
            rootLayerGrid.Children.Add(_toastBanner);

            _editModalOverlay = BuildEditModalOverlay();
            rootLayerGrid.Children.Add(_editModalOverlay);

            _deviceLimitModalOverlay = BuildDeviceLimitModalOverlay();
            rootLayerGrid.Children.Add(_deviceLimitModalOverlay);

            _outerWindowBorder.Child = rootLayerGrid;
            Content = _outerWindowBorder;

            RenderActivePage();
        }

        private UIElement BuildTitleBar()
        {
            Grid titleGrid = new Grid { Background = Brushes.Transparent };
            titleGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            titleGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            titleGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            titleGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            titleGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            titleGrid.MouseLeftButtonDown += delegate(object s, MouseButtonEventArgs e)
            {
                if (e.ClickCount == 2)
                {
                    WindowState = (WindowState == WindowState.Maximized) ? WindowState.Normal : WindowState.Maximized;
                }
                else if (e.LeftButton == MouseButtonState.Pressed)
                {
                    try { DragMove(); } catch { }
                }
            };

            // Left: App Icon + "PC Wifi" branding
            StackPanel leftPanel = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(16, 0, 0, 0)
            };

            Border appBadge = new Border
            {
                Width = 20,
                Height = 20,
                CornerRadius = new CornerRadius(5),
                Background = _theme.AccentBrush,
                Margin = new Thickness(0, 0, 9, 0),
                VerticalAlignment = VerticalAlignment.Center,
                Child = new TextBlock
                {
                    FontFamily = _fontIcons,
                    Text = "\uE88A",
                    FontSize = 11.5,
                    Foreground = _theme.AccentTextBrush,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center
                }
            };
            leftPanel.Children.Add(appBadge);

            leftPanel.Children.Add(new TextBlock
            {
                Text = "PC Wifi",
                FontSize = 12.5,
                FontWeight = FontWeights.SemiBold,
                Foreground = _theme.TextPrimary,
                VerticalAlignment = VerticalAlignment.Center
            });

            Grid.SetColumn(leftPanel, 0);
            titleGrid.Children.Add(leftPanel);

            // Live Speed Pill in Title Bar
            Border speedPill = new Border
            {
                CornerRadius = new CornerRadius(12),
                Padding = new Thickness(10, 4, 10, 4),
                Margin = new Thickness(0, 0, 8, 0),
                VerticalAlignment = VerticalAlignment.Center,
                Background = _theme.CardBg,
                BorderBrush = _theme.CardBorder,
                BorderThickness = new Thickness(1),
                Cursor = Cursors.Hand
            };
            speedPill.MouseLeftButtonUp += delegate { NavigateToPage("DataUsage"); };
            _titleBarSpeedText = new TextBlock
            {
                Text = "↓ 0.0 KB/s   ↑ 0.0 KB/s",
                FontSize = 11.5,
                Foreground = _theme.AccentBrush,
                FontWeight = FontWeights.SemiBold,
                VerticalAlignment = VerticalAlignment.Center
            };
            speedPill.Child = _titleBarSpeedText;
            Grid.SetColumn(speedPill, 2);
            titleGrid.Children.Add(speedPill);

            // Keep-Alive Status Pill
            _titleBarStatusPill = new Border
            {
                CornerRadius = new CornerRadius(12),
                Padding = new Thickness(10, 4, 10, 4),
                Margin = new Thickness(0, 0, 12, 0),
                VerticalAlignment = VerticalAlignment.Center,
                Background = _theme.CardBg,
                BorderBrush = _theme.CardBorder,
                BorderThickness = new Thickness(1),
                Cursor = Cursors.Hand
            };
            _titleBarStatusPill.MouseLeftButtonUp += delegate { NavigateToPage("KeepAlive"); };

            StackPanel pillStack = new StackPanel { Orientation = Orientation.Horizontal };
            _titleBarStatusDot = new Ellipse
            {
                Width = 8,
                Height = 8,
                Fill = _theme.SuccessBrush,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 7, 0)
            };
            _titleBarStatusText = new TextBlock
            {
                Text = "Keep-Alive Guard: Armed",
                FontSize = 11.5,
                Foreground = _theme.TextSecondary,
                VerticalAlignment = VerticalAlignment.Center
            };
            pillStack.Children.Add(_titleBarStatusDot);
            pillStack.Children.Add(_titleBarStatusText);
            _titleBarStatusPill.Child = pillStack;

            Grid.SetColumn(_titleBarStatusPill, 3);
            titleGrid.Children.Add(_titleBarStatusPill);

            // Caption Buttons
            StackPanel captionBtns = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                VerticalAlignment = VerticalAlignment.Stretch
            };

            Button minBtn = CreateChromeIconButton("\uE921", "Minimize", false);
            minBtn.Click += delegate { WindowState = WindowState.Minimized; };

            Button maxBtn = CreateChromeIconButton("\uE922", "Maximize / Restore", false);
            maxBtn.Click += delegate
            {
                WindowState = (WindowState == WindowState.Maximized) ? WindowState.Normal : WindowState.Maximized;
            };

            Button closeBtn = CreateChromeIconButton("\uE8BB", "Close (minimizes to tray if enabled)", true);
            closeBtn.Click += delegate { Close(); };

            captionBtns.Children.Add(minBtn);
            captionBtns.Children.Add(maxBtn);
            captionBtns.Children.Add(closeBtn);

            Grid.SetColumn(captionBtns, 4);
            titleGrid.Children.Add(captionBtns);

            return titleGrid;
        }

        private Button CreateChromeIconButton(string glyph, string tooltip, bool isCloseButton)
        {
            Button btn = new Button
            {
                Width = 46,
                Height = 42,
                Background = Brushes.Transparent,
                BorderThickness = new Thickness(0),
                ToolTip = tooltip,
                Cursor = Cursors.Arrow,
                FocusVisualStyle = null
            };

            TextBlock icon = new TextBlock
            {
                FontFamily = _fontIcons,
                Text = glyph,
                FontSize = 10,
                Foreground = _theme.TextPrimary,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };

            ControlTemplate tpl = new ControlTemplate(typeof(Button));
            FrameworkElementFactory bd = new FrameworkElementFactory(typeof(Border));
            bd.SetValue(Border.BackgroundProperty, new TemplateBindingExtension(Button.BackgroundProperty));
            FrameworkElementFactory cp = new FrameworkElementFactory(typeof(ContentPresenter));
            cp.SetValue(ContentPresenter.HorizontalAlignmentProperty, HorizontalAlignment.Center);
            cp.SetValue(ContentPresenter.VerticalAlignmentProperty, VerticalAlignment.Center);
            bd.AppendChild(cp);
            tpl.VisualTree = bd;
            btn.Template = tpl;
            btn.Content = icon;

            btn.MouseEnter += delegate
            {
                if (isCloseButton)
                {
                    btn.Background = new SolidColorBrush(Color.FromRgb(196, 43, 28));
                    icon.Foreground = Brushes.White;
                }
                else
                {
                    btn.Background = _theme.CardHoverBg;
                }
            };
            btn.MouseLeave += delegate
            {
                btn.Background = Brushes.Transparent;
                icon.Foreground = _theme.TextPrimary;
            };

            return btn;
        }

        private UIElement BuildSidebar()
        {
            _sidebarBorder = new Border
            {
                Background = _theme.SidebarBg,
                Padding = new Thickness(16, 8, 12, 16)
            };

            Grid sideGrid = new Grid();
            sideGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // PC Profile header
            sideGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // Search box
            sideGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) }); // Nav list
            sideGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // Footer

            // 1. PC Wifi Device Card
            Border profileCard = new Border
            {
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(8, 8, 8, 8),
                Margin = new Thickness(0, 0, 0, 12),
                Background = Brushes.Transparent
            };

            Grid profileGrid = new Grid();
            profileGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            profileGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            Border avatarCircle = new Border
            {
                Width = 44,
                Height = 44,
                CornerRadius = new CornerRadius(22),
                Background = new LinearGradientBrush(
                    Color.FromRgb(56, 189, 248),
                    Color.FromRgb(0, 103, 192),
                    45.0),
                Margin = new Thickness(0, 0, 12, 0),
                Child = new TextBlock
                {
                    FontFamily = _fontIcons,
                    Text = "\uE701",
                    FontSize = 20,
                    Foreground = Brushes.White,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center
                }
            };
            Grid.SetColumn(avatarCircle, 0);
            profileGrid.Children.Add(avatarCircle);

            StackPanel profileTextStack = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            profileTextStack.Children.Add(new TextBlock
            {
                Text = "PC Wifi • " + Environment.MachineName,
                FontSize = 14,
                FontWeight = FontWeights.SemiBold,
                Foreground = _theme.TextPrimary,
                TextTrimming = TextTrimming.CharacterEllipsis
            });
            _sidebarProfileSubText = new TextBlock
            {
                Text = "Always-On Hotspot Manager",
                FontSize = 12,
                Foreground = _theme.TextMuted,
                Margin = new Thickness(0, 2, 0, 0),
                TextTrimming = TextTrimming.CharacterEllipsis
            };
            profileTextStack.Children.Add(_sidebarProfileSubText);
            Grid.SetColumn(profileTextStack, 1);
            profileGrid.Children.Add(profileTextStack);

            profileCard.Child = profileGrid;
            Grid.SetRow(profileCard, 0);
            sideGrid.Children.Add(profileCard);

            // 2. Search Box
            Border searchBorder = new Border
            {
                Background = _theme.CardBg,
                BorderBrush = _theme.CardBorder,
                BorderThickness = new Thickness(1, 1, 1, 2),
                CornerRadius = new CornerRadius(4),
                Height = 34,
                Margin = new Thickness(0, 0, 0, 14),
                Padding = new Thickness(10, 0, 10, 0)
            };

            Grid searchGrid = new Grid();
            searchGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            searchGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            _searchBox = new TextBox
            {
                Background = Brushes.Transparent,
                BorderThickness = new Thickness(0),
                Foreground = _theme.TextPrimary,
                CaretBrush = _theme.AccentBrush,
                FontSize = 13,
                VerticalContentAlignment = VerticalAlignment.Center,
                Text = ""
            };

            TextBlock placeholder = new TextBlock
            {
                Text = "Search PC Wifi controls...",
                Foreground = _theme.TextMuted,
                FontSize = 13,
                VerticalAlignment = VerticalAlignment.Center,
                IsHitTestVisible = false
            };

            _searchBox.GotFocus += delegate
            {
                searchBorder.BorderBrush = _theme.AccentBrush;
                placeholder.Visibility = Visibility.Collapsed;
            };
            _searchBox.LostFocus += delegate
            {
                searchBorder.BorderBrush = _theme.CardBorder;
                placeholder.Visibility = string.IsNullOrEmpty(_searchBox.Text) ? Visibility.Visible : Visibility.Collapsed;
            };
            _searchBox.TextChanged += delegate
            {
                placeholder.Visibility = string.IsNullOrEmpty(_searchBox.Text) ? Visibility.Visible : Visibility.Collapsed;
                string q = (_searchBox.Text ?? "").Trim().ToLowerInvariant();
                if (string.IsNullOrEmpty(q)) return;
                if (q.Contains("block") || q.Contains("disconnect") || q.Contains("device") || q.Contains("client") || q.Contains("mac"))
                {
                    NavigateToPage("Devices");
                }
                else if (q.Contains("data") || q.Contains("speed") || q.Contains("limit") || q.Contains("quota") || q.Contains("download") || q.Contains("mb"))
                {
                    NavigateToPage("DataUsage");
                }
                else if (q.Contains("keep") || q.Contains("alive") || q.Contains("watchdog") || q.Contains("sleep") || q.Contains("startup"))
                {
                    NavigateToPage("KeepAlive");
                }
                else if (q.Contains("log") || q.Contains("event"))
                {
                    NavigateToPage("Logs");
                }
                else if (q.Contains("hidden") || q.Contains("hide") || q.Contains("ssid") || q.Contains("pass") || q.Contains("wifi"))
                {
                    NavigateToPage("Hotspot");
                }
            };

            searchGrid.Children.Add(_searchBox);
            searchGrid.Children.Add(placeholder);

            TextBlock searchIcon = new TextBlock
            {
                FontFamily = _fontIcons,
                Text = "\uE721",
                FontSize = 13,
                Foreground = _theme.TextSecondary,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(6, 0, 0, 0)
            };
            Grid.SetColumn(searchIcon, 1);
            searchGrid.Children.Add(searchIcon);

            searchBorder.Child = searchGrid;
            Grid.SetRow(searchBorder, 1);
            sideGrid.Children.Add(searchBorder);

            // 3. Navigation Items (5 pages)
            StackPanel navStack = new StackPanel();
            navStack.Children.Add(CreateNavItem("Hotspot", "\uE88A", "Wi-Fi Hotspot", null));
            navStack.Children.Add(CreateNavItem("Devices", "\uE839", "Connected Devices", "0"));
            navStack.Children.Add(CreateNavItem("DataUsage", "\uE9D9", "Data Usage & Limits", null));
            navStack.Children.Add(CreateNavItem("KeepAlive", "\uEA18", "Keep-Alive & Power", "ON"));
            navStack.Children.Add(CreateNavItem("Logs", "\uE81C", "Activity & Logs", null));

            Grid.SetRow(navStack, 2);
            sideGrid.Children.Add(navStack);

            // 4. Sidebar Footer: Admin Mode Button (if not elevated), Theme Switcher, Watchdog Heartbeat
            StackPanel footerStack = new StackPanel();

            bool isAdmin = NativeMethods.IsRunningAsAdministrator();
            if (!isAdmin)
            {
                Border adminCard = new Border
                {
                    Background = _theme.CardSecondaryBg,
                    BorderBrush = _theme.CardBorder,
                    BorderThickness = new Thickness(1),
                    CornerRadius = new CornerRadius(6),
                    Padding = new Thickness(11, 8, 11, 8),
                    Margin = new Thickness(0, 0, 0, 8),
                    Cursor = Cursors.Hand,
                    ToolTip = "Restart PC Wifi as Administrator for direct Windows Firewall & QoS bandwidth control without prompts"
                };
                Grid adminGrid = new Grid();
                adminGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                adminGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                adminGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

                adminGrid.Children.Add(new TextBlock
                {
                    FontFamily = _fontIcons,
                    Text = "\uE7EF",
                    FontSize = 14,
                    Foreground = _theme.WarningBrush,
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(0, 0, 9, 0)
                });
                TextBlock adminTb = new TextBlock
                {
                    Text = "Standard Mode",
                    FontSize = 12,
                    Foreground = _theme.TextSecondary,
                    VerticalAlignment = VerticalAlignment.Center
                };
                Grid.SetColumn(adminTb, 1);
                adminGrid.Children.Add(adminTb);

                TextBlock elevateHint = new TextBlock
                {
                    Text = "Elevate",
                    FontSize = 11.5,
                    FontWeight = FontWeights.SemiBold,
                    Foreground = _theme.AccentBrush,
                    VerticalAlignment = VerticalAlignment.Center
                };
                Grid.SetColumn(elevateHint, 2);
                adminGrid.Children.Add(elevateHint);

                adminCard.Child = adminGrid;
                adminCard.MouseLeftButtonUp += delegate { RestartElevated(); };
                footerStack.Children.Add(adminCard);
            }

            Border languageCard = new Border
            {
                Background = _theme.CardBg,
                BorderBrush = _theme.CardBorder,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(12, 9, 12, 9),
                Margin = new Thickness(0, 0, 0, 8),
                Cursor = Cursors.Hand,
                ToolTip = "Switch application language / تغيير لغة التطبيق"
            };
            Grid languageGrid = new Grid();
            languageGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            languageGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            TextBlock languageLabel = new TextBlock
            {
                Text = "Language / اللغة",
                FontSize = 12.5,
                Foreground = _theme.TextPrimary,
                VerticalAlignment = VerticalAlignment.Center
            };
            Grid.SetColumn(languageLabel, 0);
            languageGrid.Children.Add(languageLabel);
            TextBlock languageValue = new TextBlock
            {
                Text = _settings.Language == "ar" ? "English" : "العربية",
                FontSize = 11.5,
                FontWeight = FontWeights.SemiBold,
                Foreground = _theme.AccentBrush,
                VerticalAlignment = VerticalAlignment.Center
            };
            Grid.SetColumn(languageValue, 1);
            languageGrid.Children.Add(languageValue);
            languageCard.Child = languageGrid;
            languageCard.MouseLeftButtonUp += delegate
            {
                _settings.Language = _settings.Language == "ar" ? "en" : "ar";
                _settings.Save();
                BuildShellUi();
                UpdateUiFromSnapshot(_lastSnapshot);
            };
            footerStack.Children.Add(languageCard);

            Border themeCard = new Border
            {
                Background = _theme.CardBg,
                BorderBrush = _theme.CardBorder,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(12, 9, 12, 9),
                Margin = new Thickness(0, 0, 0, 8),
                Cursor = Cursors.Hand
            };

            Grid themeGrid = new Grid();
            themeGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            themeGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            themeGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            themeGrid.Children.Add(new TextBlock
            {
                FontFamily = _fontIcons,
                Text = "\uE790",
                FontSize = 15,
                Foreground = _theme.AccentBrush,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 10, 0)
            });

            TextBlock themeLabel = new TextBlock
            {
                Text = _theme.IsDark ? "Dark theme (Windows 11)" : "Light theme (Windows 11)",
                FontSize = 12.5,
                Foreground = _theme.TextPrimary,
                VerticalAlignment = VerticalAlignment.Center
            };
            Grid.SetColumn(themeLabel, 1);
            themeGrid.Children.Add(themeLabel);

            TextBlock switchThemeHint = new TextBlock
            {
                Text = "Switch",
                FontSize = 11.5,
                Foreground = _theme.AccentBrush,
                VerticalAlignment = VerticalAlignment.Center
            };
            Grid.SetColumn(switchThemeHint, 2);
            themeGrid.Children.Add(switchThemeHint);

            themeCard.Child = themeGrid;
            themeCard.MouseLeftButtonUp += delegate
            {
                _settings.IsDarkMode = !_settings.IsDarkMode;
                _settings.Save();
                _theme = Win11Theme.Create(_settings.IsDarkMode);
                IntPtr hwnd = new WindowInteropHelper(this).Handle;
                NativeMethods.ApplyWin11WindowAttributes(hwnd, _theme.IsDark);
                BuildShellUi();
                UpdateUiFromSnapshot(_lastSnapshot);
            };
            footerStack.Children.Add(themeCard);

            Border heartbeatCard = new Border
            {
                Background = _theme.CardSecondaryBg,
                BorderBrush = _theme.CardBorder,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(10, 7, 10, 7)
            };
            StackPanel hbRow = new StackPanel { Orientation = Orientation.Horizontal };
            _sidebarHeartbeatDot = new Ellipse
            {
                Width = 7,
                Height = 7,
                Fill = _theme.SuccessBrush,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 8, 0)
            };
            _sidebarHeartbeatText = new TextBlock
            {
                Text = "Guard active • Never disconnect",
                FontSize = 11.5,
                Foreground = _theme.TextSecondary,
                VerticalAlignment = VerticalAlignment.Center
            };
            hbRow.Children.Add(_sidebarHeartbeatDot);
            hbRow.Children.Add(_sidebarHeartbeatText);
            heartbeatCard.Child = hbRow;
            footerStack.Children.Add(heartbeatCard);

            Grid.SetRow(footerStack, 3);
            sideGrid.Children.Add(footerStack);

            _sidebarBorder.Child = sideGrid;
            return _sidebarBorder;
        }

        private UIElement CreateNavItem(string pageKey, string iconGlyph, string title, string badgeText)
        {
            Border itemBorder = new Border
            {
                Height = 40,
                CornerRadius = new CornerRadius(6),
                Margin = new Thickness(0, 2, 0, 2),
                Background = Brushes.Transparent,
                Cursor = Cursors.Hand
            };

            Grid row = new Grid();
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(6) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(34) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            Border leftPill = new Border
            {
                Width = 3,
                Height = 16,
                CornerRadius = new CornerRadius(1.5),
                Background = _theme.AccentBrush,
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Center,
                Visibility = Visibility.Hidden
            };
            Grid.SetColumn(leftPill, 0);
            row.Children.Add(leftPill);

            TextBlock icon = new TextBlock
            {
                FontFamily = _fontIcons,
                Text = iconGlyph,
                FontSize = 16,
                Foreground = _theme.TextPrimary,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
            Grid.SetColumn(icon, 1);
            row.Children.Add(icon);

            TextBlock label = new TextBlock
            {
                Text = title,
                FontSize = 13.5,
                Foreground = _theme.TextPrimary,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(4, 0, 0, 0)
            };
            Grid.SetColumn(label, 2);
            row.Children.Add(label);

            if (badgeText != null)
            {
                Border badgeBd = new Border
                {
                    CornerRadius = new CornerRadius(9),
                    Background = _theme.CardBg,
                    BorderBrush = _theme.CardBorder,
                    BorderThickness = new Thickness(1),
                    Padding = new Thickness(7, 1, 7, 1),
                    Margin = new Thickness(0, 0, 10, 0),
                    VerticalAlignment = VerticalAlignment.Center
                };
                TextBlock badgeTb = new TextBlock
                {
                    Text = badgeText,
                    FontSize = 11,
                    FontWeight = FontWeights.SemiBold,
                    Foreground = _theme.AccentBrush
                };
                badgeBd.Child = badgeTb;
                Grid.SetColumn(badgeBd, 3);
                row.Children.Add(badgeBd);
                _navBadges[pageKey] = badgeTb;
            }

            itemBorder.Child = row;

            itemBorder.MouseEnter += delegate
            {
                if (_activePage != pageKey) itemBorder.Background = _theme.NavHoverBg;
            };
            itemBorder.MouseLeave += delegate
            {
                if (_activePage != pageKey) itemBorder.Background = Brushes.Transparent;
            };
            itemBorder.MouseLeftButtonUp += delegate
            {
                NavigateToPage(pageKey);
            };

            _navItems[pageKey] = itemBorder;
            _navPills[pageKey] = leftPill;
            return itemBorder;
        }

        private UIElement BuildContentArea()
        {
            Grid contentGrid = new Grid { Background = _theme.ContentBg };
            contentGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            contentGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

            // Clean PC Wifi Page Header (Removed "Network & internet >" copy text)
            Grid headerRow = new Grid
            {
                Margin = new Thickness(36, 12, 36, 16),
                MaxWidth = 920,
                HorizontalAlignment = HorizontalAlignment.Stretch
            };
            headerRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            headerRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            StackPanel titleStack = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            _pageHeaderTitleText = new TextBlock
            {
                Text = "Wi-Fi Hotspot",
                FontFamily = _fontDisplay,
                FontSize = 26,
                FontWeight = FontWeights.SemiBold,
                Foreground = _theme.TextPrimary
            };
            titleStack.Children.Add(_pageHeaderTitleText);

            _pageHeaderSubText = new TextBlock
            {
                Text = "Broadcast an always-on Wi-Fi network with device control, hidden mode, and bandwidth limits",
                FontSize = 12.5,
                Foreground = _theme.TextSecondary,
                Margin = new Thickness(0, 3, 0, 0)
            };
            titleStack.Children.Add(_pageHeaderSubText);

            Grid.SetColumn(titleStack, 0);
            headerRow.Children.Add(titleStack);

            StackPanel headerActions = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                VerticalAlignment = VerticalAlignment.Center
            };

            Button refreshBtn = CreateWin11Button("\uE72C", "Refresh", false);
            refreshBtn.Click += async delegate
            {
                await RefreshHotspotStateAsync(false);
                ShowToast("Hotspot & device status refreshed.");
            };
            headerActions.Children.Add(refreshBtn);

            Grid.SetColumn(headerActions, 1);
            headerRow.Children.Add(headerActions);

            Grid.SetRow(headerRow, 0);
            contentGrid.Children.Add(headerRow);

            ScrollViewer scroller = new ScrollViewer
            {
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                Padding = new Thickness(36, 0, 36, 36)
            };
            ApplyWin11ScrollBarStyle(scroller);

            _pageContainer = new StackPanel
            {
                MaxWidth = 920,
                HorizontalAlignment = HorizontalAlignment.Stretch
            };

            scroller.Content = _pageContainer;
            Grid.SetRow(scroller, 1);
            contentGrid.Children.Add(scroller);

            return contentGrid;
        }
        #endregion

        #region Navigation & Page Rendering
        private void NavigateToPage(string pageKey)
        {
            _activePage = pageKey;
            RenderActivePage();
            UpdateUiFromSnapshot(_lastSnapshot);
        }

        private void RenderActivePage()
        {
            foreach (KeyValuePair<string, Border> kv in _navItems)
            {
                bool active = (kv.Key == _activePage);
                kv.Value.Background = active ? _theme.NavSelectedBg : Brushes.Transparent;
                if (_navPills.ContainsKey(kv.Key))
                {
                    _navPills[kv.Key].Visibility = active ? Visibility.Visible : Visibility.Hidden;
                }
            }

            _pageContainer.Children.Clear();

            if (_activePage == "Hotspot")
            {
                _pageHeaderTitleText.Text = "Wi-Fi Hotspot";
                _pageHeaderSubText.Text = "Broadcast an always-on Wi-Fi network with hidden SSID, device controls, and live data tracking";
                BuildHotspotMainPage(_pageContainer);
            }
            else if (_activePage == "Devices")
            {
                _pageHeaderTitleText.Text = "Connected Devices";
                _pageHeaderSubText.Text = "Monitor per-device data usage, set individual speed/download limits, or disconnect and block devices";
                BuildConnectedDevicesPage(_pageContainer);
            }
            else if (_activePage == "DataUsage")
            {
                _pageHeaderTitleText.Text = "Data Usage & Speed Limits";
                _pageHeaderSubText.Text = "Track real-time bandwidth consumption, set an upper download cap, and throttle max Wi-Fi speed";
                BuildDataUsagePage(_pageContainer);
            }
            else if (_activePage == "KeepAlive")
            {
                _pageHeaderTitleText.Text = "Keep-Alive & Power";
                _pageHeaderSubText.Text = "Ensure your PC Wifi network never disconnects even when 0 devices are connected";
                BuildKeepAlivePage(_pageContainer);
            }
            else if (_activePage == "Logs")
            {
                _pageHeaderTitleText.Text = "Activity & Logs";
                _pageHeaderSubText.Text = "Live audit trail of connections, blocks, data quota triggers, and watchdog checks";
                BuildActivityLogsPage(_pageContainer);
            }

            ApplyLocalization();
        }

        private bool IsArabic
        {
            get { return _settings != null && _settings.Language == "ar"; }
        }

        private string Localize(string value)
        {
            if (!IsArabic || string.IsNullOrEmpty(value)) return value;
            string translated;
            return ArabicText.TryGetValue(value, out translated) ? translated : value;
        }

        private static readonly Dictionary<string, string> ArabicText = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            { "Wi-Fi Hotspot", "نقطة اتصال Wi‑Fi" },
            { "Connected Devices", "الأجهزة المتصلة" },
            { "Data Usage & Speed Limits", "استهلاك البيانات وحدود السرعة" },
            { "Data Usage & Limits", "استهلاك البيانات والحدود" },
            { "Keep-Alive & Power", "استمرارية الاتصال والطاقة" },
            { "Activity & Logs", "النشاط والسجلات" },
            { "Broadcast an always-on Wi-Fi network with hidden SSID, device controls, and live data tracking", "بث شبكة Wi‑Fi دائمة مع إخفاء اسم الشبكة وإدارة الأجهزة ومراقبة البيانات مباشرة" },
            { "Monitor per-device data usage, set individual speed/download limits, or disconnect and block devices", "راقب استهلاك كل جهاز، وحدد السرعة أو التنزيل، أو افصل الأجهزة واحظرها" },
            { "Track real-time bandwidth consumption, set an upper download cap, and throttle max Wi-Fi speed", "تابع استهلاك النطاق الترددي لحظياً، وحدد سقف التنزيل والسرعة القصوى" },
            { "Ensure your PC Wifi network never disconnects even when 0 devices are connected", "حافظ على اتصال شبكة PC Wifi حتى عند عدم وجود أجهزة متصلة" },
            { "Live audit trail of connections, blocks, data quota triggers, and watchdog checks", "سجل مباشر للاتصالات والحظر وتجاوز حصص البيانات وفحوصات المراقبة" },
            { "Always-On Hotspot Manager", "مدير نقطة الاتصال الدائمة" },
            { "Search PC Wifi controls...", "ابحث في إعدادات PC Wifi..." },
            { "Standard Mode", "الوضع العادي" },
            { "Elevate", "تشغيل كمسؤول" },
            { "Dark theme (Windows 11)", "المظهر الداكن (Windows 11)" },
            { "Light theme (Windows 11)", "المظهر الفاتح (Windows 11)" },
            { "Switch", "تبديل" },
            { "Language / اللغة", "اللغة / Language" },
            { "Broadcast Wi-Fi hotspot from this PC", "بث نقطة اتصال Wi‑Fi من هذا الكمبيوتر" },
            { "PC Wifi Hotspot", "نقطة اتصال PC Wifi" },
            { "PC Wifi Network", "شبكة PC Wifi" },
            { "Wi-Fi Network Properties", "خصائص شبكة Wi‑Fi" },
            { "Network name (SSID):", "اسم الشبكة (SSID):" },
            { "Network password:", "كلمة مرور الشبكة:" },
            { "Network band:", "نطاق الشبكة:" },
            { "Internet source adapter", "محول مصدر الإنترنت" },
            { "Source: Auto", "المصدر: تلقائي" },
            { "Auto", "تلقائي" },
            { "Automatic", "آلي" },
            { "ON", "تشغيل" },
            { "OFF", "إيقاف" },
            { "Connected", "متصل" },
            { "Offline", "غير متصل" },
            { "Connected devices", "الأجهزة المتصلة" },
            { "Active Connected Devices (0)", "الأجهزة المتصلة حالياً (0)" },
            { "All Known Devices & History", "جميع الأجهزة المعروفة والسجل" },
            { "Blocked Devices (Banned from Hotspot)", "الأجهزة المحظورة من نقطة الاتصال" },
            { "Blocked", "محظور" },
            { "Unblock", "إلغاء الحظر" },
            { "Disconnect", "فصل الاتصال" },
            { "Disconnect this device from the Wi-Fi hotspot right now", "افصل هذا الجهاز عن نقطة اتصال Wi‑Fi الآن" },
            { "Data & Speed Limits", "حدود البيانات والسرعة" },
            { "Data Consumption Meter", "مقياس استهلاك البيانات" },
            { "Live Hotspot Speed", "سرعة نقطة الاتصال مباشرة" },
            { "Total Downloaded (Clients)", "إجمالي التنزيل (الأجهزة المتصلة)" },
            { "Total Uploaded (Clients)", "إجمالي الرفع (الأجهزة المتصلة)" },
            { "Per-Device Data Consumption & Individual Limits", "استهلاك البيانات والحدود لكل جهاز" },
            { "Upper Download Limit (Data Quota)", "الحد الأعلى للتنزيل (حصة البيانات)" },
            { "Global upper download limit", "حد التنزيل الأعلى العام" },
            { "Global max speed limit", "حد السرعة الأقصى العام" },
            { "Global max speed limiter ", "محدد السرعة الأقصى العام " },
            { "Limit maximum hotspot download speed", "تحديد أقصى سرعة تنزيل لنقطة الاتصال" },
            { "Maximum Bandwidth Speed Limiter", "محدد أقصى سرعة للنطاق الترددي" },
            { "Limits", "الحدود" },
            { "Global", "عام" },
            { "Keep-Alive Guard ON: Hotspot will never disconnect when idle.", "الحماية من الانقطاع مفعّلة: لن تنقطع نقطة الاتصال عند الخمول." },
            { "Keep-Alive Guard paused.", "تم إيقاف الحماية من الانقطاع مؤقتاً." },
            { "Always-On Hotspot Protection", "حماية نقطة الاتصال الدائمة" },
            { "Watchdog health checks", "فحوصات سلامة المراقب" },
            { "Hotspot active uptime", "مدة تشغيل نقطة الاتصال" },
            { "Auto-recoveries triggered", "عمليات الاستعادة التلقائية" },
            { "Watchdog check frequency", "معدل فحص المراقب" },
            { "How often the background guard verifies your hotspot is broadcasting", "عدد مرات تحقق الحماية في الخلفية من بث نقطة الاتصال" },
            { "Every 2 seconds (Instant)", "كل ثانيتين (فوري)" },
            { "Every 5 seconds (Recommended)", "كل 5 ثوانٍ (موصى به)" },
            { "Every 10 seconds", "كل 10 ثوانٍ" },
            { "Every 30 seconds", "كل 30 ثانية" },
            { "Run check now", "إجراء الفحص الآن" },
            { "Startup & Background Behavior", "التشغيل وسلوك الخلفية" },
            { "Prevent PC from sleeping while hotspot is active", "منع الكمبيوتر من الدخول في وضع السكون أثناء تشغيل نقطة الاتصال" },
            { "Start automatically when I sign in to Windows", "التشغيل تلقائياً عند تسجيل الدخول إلى Windows" },
            { "Automatically turn on hotspot when PC Wifi opens", "تشغيل نقطة الاتصال تلقائياً عند فتح PC Wifi" },
            { "Keep running in system tray when window is closed", "الاستمرار في العمل بمنطقة الإعلام عند إغلاق النافذة" },
            { "Lock Windows 'No Connections Timeout' to OFF", "تعطيل مهلة إيقاف نقطة الاتصال عند عدم وجود اتصالات" },
            { "Never disconnect when no one is connected", "عدم قطع الاتصال عند عدم وجود أجهزة متصلة" },
            { "Make Wi-Fi hidden (Stealth Mode)", "إخفاء شبكة Wi‑Fi (الوضع الخفي)" },
            { "Lock network to known devices only", "السماح للأجهزة المعروفة فقط بالاتصال" },
            { "Traffic & Protection", "حركة البيانات والحماية" },
            { "Activity log cleared.", "تم مسح سجل النشاط." },
            { "Activity log copied to clipboard.", "تم نسخ سجل النشاط." },
            { "Copy log", "نسخ السجل" },
            { "Copy", "نسخ" },
            { "Clear", "مسح" },
            { "Edit network info", "تعديل معلومات الشبكة" },
            { "Show or hide password", "إظهار أو إخفاء كلمة المرور" },
            { "Network SSID copied to clipboard.", "تم نسخ اسم الشبكة إلى الحافظة." },
            { "Network password copied to clipboard.", "تم نسخ كلمة مرور الشبكة إلى الحافظة." },
            { "Checking...", "جارٍ الفحص..." },
            { "Active Keep-Alive Watchdog", "مراقب استمرارية الاتصال نشط" },
            { "Keep-Alive Guard: Armed", "حماية استمرارية الاتصال: جاهزة" },
            { "Broadcast mode:", "وضع البث:" },
            { "Hidden", "مخفي" },
            { "Visible", "ظاهر" },
            { "Reset data usage to 0 MB", "تصفير استهلاك البيانات إلى 0 MB" },
            { "Reset all device data counters", "إعادة تعيين عدادات بيانات جميع الأجهزة" },
            { "Network Locked: New unknown devices will be automatically blocked.", "الشبكة مقفلة: سيتم حظر الأجهزة الجديدة غير المعروفة تلقائياً." },
            { "Network Unlocked: New devices with the password can join.", "الشبكة مفتوحة: يمكن للأجهزة الجديدة الاتصال باستخدام كلمة المرور." },
            { "Auto-block new unknown devices set to: ", "تم ضبط الحظر التلقائي للأجهزة غير المعروفة على: " },
            { "Enabled launch at Windows startup.", "تم تفعيل التشغيل عند بدء Windows." },
            { "Removed from Windows startup.", "تمت إزالة التطبيق من بدء تشغيل Windows." },
            { "Select which network connection provides internet to your PC Wifi hotspot", "اختر اتصال الشبكة الذي يوفر الإنترنت لنقطة اتصال PC Wifi" },
            { "View data consumed per device, set speed/download limits, or disconnect and block", "عرض استهلاك كل جهاز وتحديد السرعة والتنزيل أو الفصل والحظر" },
            { "View or customize your Wi-Fi name (SSID), password, band, and visibility", "عرض أو تخصيص اسم شبكة Wi‑Fi وكلمة المرور والنطاق وإمكانية الظهور" },
            { "Uses Windows execution state APIs so idle PC sleep never cuts off your Wi-Fi hotspot", "يستخدم واجهات Windows لمنع وضع السكون من قطع نقطة اتصال Wi‑Fi" },
            { "Continuously monitors hotspot state and immediately restarts it if Windows or the Wi-Fi driver drops it when 0 devices are connected", "يراقب حالة نقطة الاتصال باستمرار ويعيد تشغيلها فور توقفها بسبب Windows أو تعريف Wi‑Fi عند عدم وجود أجهزة متصلة" },
            { "Automatically stops the hotspot when total client downloads reach the upper data cap", "يوقف نقطة الاتصال تلقائياً عند بلوغ إجمالي تنزيل الأجهزة حد البيانات الأعلى" },
            { "Caps the maximum bandwidth speed shared with connected Wi-Fi devices (you can also set per-device speed limits)", "يحدد أقصى سرعة مشتركة مع أجهزة Wi‑Fi المتصلة (يمكنك أيضاً تحديد سرعة لكل جهاز)" },
            { "Cloaks the broadcast network name so it appears invisible in nearby Wi-Fi scan lists and disables peer discovery", "يخفي اسم الشبكة من قوائم شبكات Wi‑Fi القريبة ويعطّل اكتشاف الأجهزة النظيرة" },
            { "Automatically block any new unfamiliar device that tries to connect without your approval", "يحظر تلقائياً أي جهاز جديد غير معروف يحاول الاتصال دون موافقتك" },
            { "Clicking the X button hides PC Wifi to the system tray so the Keep-Alive Watchdog stays active 24/7", "زر الإغلاق يخفي PC Wifi في منطقة الإعلام ليبقى مراقب الاتصال نشطاً طوال الوقت" },
            { "Launches PC Wifi quietly in the system tray at Windows startup", "يشغّل PC Wifi بهدوء في منطقة الإعلام عند بدء Windows" },
            { "Starts broadcasting your Wi-Fi hotspot immediately when you launch this app", "يبدأ بث نقطة اتصال Wi‑Fi فور تشغيل التطبيق" },
            { "Set upper download limit for hotspot", "تحديد الحد الأعلى لتنزيل نقطة الاتصال" },
            { "Set upper download limit (MB), max speed (KB/s), or custom name for this device", "حدد حد التنزيل الأعلى (MB) أو السرعة القصوى (KB/s) أو اسماً مخصصاً لهذا الجهاز" },
            { "Hotspot speed limit removed (Unlimited).", "تمت إزالة حد سرعة نقطة الاتصال (غير محدود)." },
            { "Hotspot is currently ON. Keep-Alive Guard & Traffic Monitor armed.", "نقطة الاتصال تعمل حالياً. تمت تهيئة الحماية ومراقبة حركة البيانات." },
            { "PC Wifi initialized. Starting hotspot & traffic controller...", "تم تشغيل PC Wifi. جارٍ بدء التحكم بنقطة الاتصال وحركة البيانات..." },
            { "Active Keep-Alive Watchdog set to: ", "تم ضبط مراقب استمرارية الاتصال على: " },
            { "Keep-Alive Guard set to: ", "تم ضبط حماية استمرارية الاتصال على: " },
            { "Lock Windows idle timeout OFF set to: ", "تم ضبط تعطيل مهلة خمول Windows على: " },
            { "Prevent PC sleep while hotspot is active set to: ", "تم ضبط منع سكون الكمبيوتر أثناء تشغيل نقطة الاتصال على: " },
            { "Auto-start hotspot on app launch set to: ", "تم ضبط التشغيل التلقائي لنقطة الاتصال عند فتح التطبيق على: " },
            { "Minimize to system tray on close set to: ", "تم ضبط التصغير إلى منطقة الإعلام عند الإغلاق على: " },
            { "Launch automatically at Windows sign-in set to: ", "تم ضبط التشغيل عند تسجيل الدخول إلى Windows على: " },
            { "Global upper download limit set to: ", "تم ضبط حد التنزيل الأعلى العام على: " },
            { "Global max speed limit set to: ", "تم ضبط حد السرعة الأقصى العام على: " },
            { "Watchdog check interval updated to every ", "تم تحديث فترة فحص المراقب إلى كل " },
            { "Watchdog verification check completed.", "اكتمل فحص التحقق من المراقب." },
            { "Internet source profile set to: ", "تم ضبط ملف مصدر الإنترنت على: " },
            { "IP: ", "عنوان IP: " },
            { "MAC: ", "عنوان MAC: " },
            { "Peak: ", "الذروة: " },
            { "Speed: ↓ ", "السرعة: ↓ " },
            { "1 GB (1024 MB)", "1 GB (1024 MB)" },
            { "2 GB (2048 MB)", "2 GB (2048 MB)" },
            { "5 GB (5120 MB)", "5 GB (5120 MB)" },
            { "10 GB", "10 GB" },
            { "20 GB", "20 GB" },
            { "250 MB", "250 MB" },
            { "500 MB", "500 MB" },
            { "256 KB/s (2 Mbps)", "256 KB/s (2 Mbps)" },
            { "512 KB/s (4 Mbps)", "512 KB/s (4 Mbps)" },
            { "1 MB/s (8 Mbps)", "1 MB/s (8 Mbps)" },
            { "2 MB/s (16 Mbps)", "2 MB/s (16 Mbps)" },
            { "5 MB/s (40 Mbps)", "5 MB/s (40 Mbps)" },
            { "10 MB/s (80 Mbps)", "10 MB/s (80 Mbps)" },
            { "20 MB/s (160 Mbps)", "20 MB/s (160 Mbps)" },
            { "Per-Device", "لكل جهاز" },
            { "Data consumed: ↓ ", "البيانات المستهلكة: ↓ " },
            { "No devices connected", "لا توجد أجهزة متصلة" },
            { "Keep-Alive Guard Armed • Ready", "حماية استمرارية الاتصال جاهزة" },
            { "Keep-Alive Guard Paused", "حماية استمرارية الاتصال متوقفة" },
            { "No upstream connection", "لا يوجد اتصال إنترنت مصدر" },
            { "No internet source", "لا يوجد مصدر إنترنت" },
            { "Guard paused • Click Keep-Alive to enable", "الحماية متوقفة • اضغط على استمرارية الاتصال للتفعيل" },
            { "Hotspot is transitioning state...", "جارٍ تغيير حالة نقطة الاتصال..." },
            { "Starting...", "جارٍ التشغيل..." },
            { "Off", "متوقف" },
            { "Hidden (Stealth Mode)", "مخفي (الوضع الخفي)" },
            { "Visible (Standard Broadcast)", "ظاهر (البث العادي)" },
            { "No devices are currently connected. Keep-Alive Guard is actively keeping your PC Wifi network online so devices can join anytime.", "لا توجد أجهزة متصلة حالياً. تحافظ حماية استمرارية الاتصال على بقاء شبكة PC Wifi متاحة للاتصال في أي وقت." },
            { "Hotspot is currently off. Turn on PC Wifi Hotspot to allow devices to connect.", "نقطة الاتصال متوقفة حالياً. شغّل نقطة اتصال PC Wifi للسماح للأجهزة بالاتصال." },
            { "Wi-Fi hotspot is already active.", "نقطة اتصال Wi‑Fi تعمل بالفعل." },
            { "Wi-Fi hotspot is already off.", "نقطة اتصال Wi‑Fi متوقفة بالفعل." },
            { "Wi-Fi hotspot turned off.", "تم إيقاف نقطة اتصال Wi‑Fi." },
            { "Could not start hotspot: ", "تعذر تشغيل نقطة الاتصال: " },
            { "Error starting hotspot: ", "حدث خطأ أثناء تشغيل نقطة الاتصال: " },
            { "Could not stop hotspot: ", "تعذر إيقاف نقطة الاتصال: " },
            { "Error stopping hotspot: ", "حدث خطأ أثناء إيقاف نقطة الاتصال: " },
            { "Hotspot ON • Never-Disconnect Active", "نقطة الاتصال تعمل • الحماية من الانقطاع نشطة" },
        };

        private void ApplyLocalization()
        {
            if (_outerWindowBorder == null) return;
            _outerWindowBorder.FlowDirection = IsArabic ? FlowDirection.RightToLeft : FlowDirection.LeftToRight;
            ApplyLocalizationToElement(_outerWindowBorder);
        }

        private void ApplyLocalizationToElement(DependencyObject element)
        {
            TextBlock text = element as TextBlock;
            if (text != null) text.Text = Localize(text.Text);

            TextBox box = element as TextBox;
            if (box != null && !string.IsNullOrEmpty(box.ToolTip as string))
                box.ToolTip = Localize(box.ToolTip as string);

            Control control = element as Control;
            if (control != null && control.ToolTip is string)
                control.ToolTip = Localize(control.ToolTip as string);

            ContentControl content = element as ContentControl;
            if (content != null && content.Content is string)
                content.Content = Localize(content.Content as string);

            try
            {
                int count = VisualTreeHelper.GetChildrenCount(element);
                for (int i = 0; i < count; i++)
                    ApplyLocalizationToElement(VisualTreeHelper.GetChild(element, i));
            }
            catch
            {
                // Some WPF content elements are not part of the visual tree.
            }
        }

        // PAGE 1: Wi-Fi Hotspot Main Page
        private void BuildHotspotMainPage(StackPanel container)
        {
            // 1. Top Visual Overview Card
            Border heroBanner = CreateCardBorder(new Thickness(18, 14, 18, 14), new Thickness(0, 0, 0, 12));
            Grid heroGrid = new Grid();
            heroGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            heroGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            heroGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            heroGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            heroGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.15, GridUnitType.Star) });

            StackPanel node1 = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            node1.Children.Add(CreateCircleIconBadge("\uE770", _theme.AccentBrush));
            StackPanel node1Text = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 0, 0) };
            node1Text.Children.Add(new TextBlock
            {
                Text = Environment.MachineName,
                FontSize = 13.5,
                FontWeight = FontWeights.SemiBold,
                Foreground = _theme.TextPrimary
            });
            _heroPcNameText = new TextBlock
            {
                Text = "Source: Auto",
                FontSize = 11.5,
                Foreground = _theme.TextSecondary,
                Margin = new Thickness(0, 2, 0, 0),
                TextTrimming = TextTrimming.CharacterEllipsis
            };
            node1Text.Children.Add(_heroPcNameText);
            node1.Children.Add(node1Text);
            Grid.SetColumn(node1, 0);
            heroGrid.Children.Add(node1);

            Border line1 = new Border
            {
                Width = 28,
                Height = 2,
                Background = _theme.AccentBrush,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(6, 0, 10, 0)
            };
            Grid.SetColumn(line1, 1);
            heroGrid.Children.Add(line1);

            StackPanel node2 = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            node2.Children.Add(CreateCircleIconBadge("\uE88A", _theme.AccentBrush));
            StackPanel node2Text = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 0, 0) };
            _heroSsidText = new TextBlock
            {
                Text = "PC Wifi Network",
                FontSize = 13.5,
                FontWeight = FontWeights.SemiBold,
                Foreground = _theme.TextPrimary,
                TextTrimming = TextTrimming.CharacterEllipsis
            };
            node2Text.Children.Add(_heroSsidText);

            _heroStatusBadge = new Border
            {
                CornerRadius = new CornerRadius(4),
                Padding = new Thickness(6, 1, 6, 1),
                Margin = new Thickness(0, 3, 0, 0),
                HorizontalAlignment = HorizontalAlignment.Left,
                Background = _theme.CardSecondaryBg
            };
            _heroStatusBadgeText = new TextBlock
            {
                Text = "Checking...",
                FontSize = 11,
                FontWeight = FontWeights.SemiBold,
                Foreground = _theme.TextSecondary
            };
            _heroStatusBadge.Child = _heroStatusBadgeText;
            node2Text.Children.Add(_heroStatusBadge);
            node2.Children.Add(node2Text);
            Grid.SetColumn(node2, 2);
            heroGrid.Children.Add(node2);

            Border line2 = new Border
            {
                Width = 28,
                Height = 2,
                Background = _theme.DividerBrush,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(6, 0, 10, 0)
            };
            Grid.SetColumn(line2, 3);
            heroGrid.Children.Add(line2);

            StackPanel node3 = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            node3.Children.Add(CreateCircleIconBadge("\uE9D9", _theme.SuccessBrush));
            StackPanel node3Text = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 0, 0) };
            node3Text.Children.Add(new TextBlock
            {
                Text = "Traffic & Protection",
                FontSize = 13.5,
                FontWeight = FontWeights.SemiBold,
                Foreground = _theme.TextPrimary
            });
            _heroDataSummaryText = new TextBlock
            {
                Text = "0.0 MB used • Guard ON",
                FontSize = 11.5,
                Foreground = _theme.TextSecondary,
                Margin = new Thickness(0, 2, 0, 0),
                TextTrimming = TextTrimming.CharacterEllipsis
            };
            node3Text.Children.Add(_heroDataSummaryText);
            node3.Children.Add(node3Text);
            Grid.SetColumn(node3, 4);
            heroGrid.Children.Add(node3);

            heroBanner.Child = heroGrid;
            container.Children.Add(heroBanner);

            // 2. Primary Wi-Fi Hotspot Toggle Card
            _mainHotspotToggle = new Win11ToggleSwitch(_theme, true);
            _mainHotspotToggle.Toggled += async delegate(object s, bool isOn)
            {
                await ToggleHotspotFromUserAsync(isOn);
            };
            Border mainToggleCard = CreateSettingRowCard(
                "\uE88A",
                "PC Wifi Hotspot",
                "Broadcast Wi-Fi hotspot from this PC",
                _mainHotspotToggle,
                out _mainHotspotSubtitle);
            container.Children.Add(mainToggleCard);

            // 3. Never Disconnect When Idle (Keep-Alive Guard)
            _quickKeepAliveToggle = new Win11ToggleSwitch(_theme, true);
            _quickKeepAliveToggle.SetCheckedSilent(_settings.KeepAliveEnabled);
            _quickKeepAliveToggle.Toggled += delegate(object s, bool isOn)
            {
                _settings.KeepAliveEnabled = isOn;
                if (isOn)
                {
                    _settings.ForceDisableOsTimeout = true;
                    _controller.EnsureNoConnectionsTimeoutDisabled();
                }
                _settings.Save();
                AddLog("Keep-Alive Guard set to: " + (isOn ? "ON" : "OFF"));
                ShowToast(isOn ? "Keep-Alive Guard ON: Hotspot will never disconnect when idle." : "Keep-Alive Guard paused.");
                UpdateUiFromSnapshot(_lastSnapshot);
            };
            TextBlock keepAliveSub;
            container.Children.Add(CreateSettingRowCard(
                "\uEA18",
                "Never disconnect when no one is connected",
                "Locks Windows idle timeout OFF and automatically restarts the hotspot if it ever drops",
                _quickKeepAliveToggle,
                out keepAliveSub));

            // 4. Make Wi-Fi Hidden (Stealth SSID & Discovery Suppression)
            _hiddenWifiToggle = new Win11ToggleSwitch(_theme, true);
            _hiddenWifiToggle.SetCheckedSilent(_settings.IsHiddenWifiEnabled);
            _hiddenWifiToggle.Toggled += async delegate(object s, bool hideWifi)
            {
                await ToggleHiddenWifiModeAsync(hideWifi);
            };
            container.Children.Add(CreateSettingRowCard(
                "\uED1A",
                "Make Wi-Fi hidden (Stealth Mode)",
                "Cloaks the broadcast network name so it appears invisible in nearby Wi-Fi scan lists and disables peer discovery",
                _hiddenWifiToggle,
                out _hiddenWifiSubtitle));

            // 5. Lock Hotspot (Auto-block new unknown devices)
            _lockUnknownDevicesToggle = new Win11ToggleSwitch(_theme, true);
            _lockUnknownDevicesToggle.SetCheckedSilent(_settings.BlockNewUnknownDevices);
            _lockUnknownDevicesToggle.Toggled += delegate(object s, bool lockNet)
            {
                _settings.BlockNewUnknownDevices = lockNet;
                _settings.Save();
                AddLog("Auto-block new unknown devices set to: " + (lockNet ? "ON" : "OFF"));
                ShowToast(lockNet
                    ? "Network Locked: New unknown devices will be automatically blocked."
                    : "Network Unlocked: New devices with the password can join.");
            };
            TextBlock lockSub;
            container.Children.Add(CreateSettingRowCard(
                "\uE72E",
                "Lock network to known devices only",
                "Automatically block any new unfamiliar device that tries to connect without your approval",
                _lockUnknownDevicesToggle,
                out lockSub));

            // 6. Share Internet Connection From
            _profileComboBox = CreateStyledComboBox(230);
            _profileComboBox.SelectionChanged += async delegate
            {
                if (_suppressProfileComboEvent || _profileComboBox.SelectedItem == null) return;
                string sel = _profileComboBox.SelectedItem;
                _settings.PreferredProfileName = sel.StartsWith("Automatic") ? "Auto" : sel;
                _settings.Save();
                AddLog("Internet source profile set to: " + _settings.PreferredProfileName);
                await RefreshHotspotStateAsync(false);
            };
            TextBlock shareFromSub;
            container.Children.Add(CreateSettingRowCard(
                "\uE774",
                "Internet source adapter",
                "Select which network connection provides internet to your PC Wifi hotspot",
                _profileComboBox,
                out shareFromSub));

            // 7. Network Properties Card
            Border propsCard = CreateCardBorder(new Thickness(0), new Thickness(0, 0, 0, 6));
            StackPanel propsStack = new StackPanel();

            Grid propsHeader = new Grid { Margin = new Thickness(18, 14, 18, 14) };
            propsHeader.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(36) });
            propsHeader.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            propsHeader.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            propsHeader.Children.Add(new TextBlock
            {
                FontFamily = _fontIcons,
                Text = "\uE946",
                FontSize = 18,
                Foreground = _theme.TextPrimary,
                VerticalAlignment = VerticalAlignment.Center
            });

            StackPanel propsTitleStack = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            propsTitleStack.Children.Add(new TextBlock
            {
                Text = "Wi-Fi Network Properties",
                FontSize = 14,
                FontWeight = FontWeights.SemiBold,
                Foreground = _theme.TextPrimary
            });
            propsTitleStack.Children.Add(new TextBlock
            {
                Text = "View or customize your Wi-Fi name (SSID), password, band, and visibility",
                FontSize = 12,
                Foreground = _theme.TextSecondary,
                Margin = new Thickness(0, 2, 0, 0)
            });
            Grid.SetColumn(propsTitleStack, 1);
            propsHeader.Children.Add(propsTitleStack);
            propsStack.Children.Add(propsHeader);
            propsStack.Children.Add(CreateHorizontalDivider());

            StackPanel propsBody = new StackPanel { Margin = new Thickness(54, 14, 20, 16) };

            Grid ssidRow = CreatePropertyDetailRow("Network name (SSID):", out _propSsidValue);
            Button copySsidBtn = CreateWin11Button("\uE8C8", "Copy", false);
            copySsidBtn.Margin = new Thickness(8, 0, 0, 0);
            copySsidBtn.Click += delegate
            {
                CopyToClipboard(_lastSnapshot.Ssid, "Network SSID copied to clipboard.");
            };
            Grid.SetColumn(copySsidBtn, 2);
            ssidRow.Children.Add(copySsidBtn);
            propsBody.Children.Add(ssidRow);

            Grid passRow = CreatePropertyDetailRow("Network password:", out _propPasswordValue);
            StackPanel passActions = new StackPanel { Orientation = Orientation.Horizontal };

            Button togglePassBtn = CreateWin11Button(_isPasswordRevealed ? "\uED1A" : "\uE7B3", "", false);
            togglePassBtn.Padding = new Thickness(10, 6, 10, 6);
            togglePassBtn.ToolTip = "Show or hide password";
            _propEyeIcon = ((StackPanel)togglePassBtn.Content).Children[0] as TextBlock;
            togglePassBtn.Click += delegate
            {
                _isPasswordRevealed = !_isPasswordRevealed;
                UpdateUiFromSnapshot(_lastSnapshot);
            };
            passActions.Children.Add(togglePassBtn);

            Button copyPassBtn = CreateWin11Button("\uE8C8", "Copy", false);
            copyPassBtn.Margin = new Thickness(8, 0, 0, 0);
            copyPassBtn.Click += delegate
            {
                CopyToClipboard(_lastSnapshot.Passphrase, "Network password copied to clipboard.");
            };
            passActions.Children.Add(copyPassBtn);
            Grid.SetColumn(passActions, 2);
            passRow.Children.Add(passActions);
            propsBody.Children.Add(passRow);

            Grid bandRow = CreatePropertyDetailRow("Network band:", out _propBandValue);
            propsBody.Children.Add(bandRow);

            Grid visRow = CreatePropertyDetailRow("Broadcast mode:", out _propVisibilityValue);
            propsBody.Children.Add(visRow);

            StackPanel editBtnRow = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Margin = new Thickness(0, 12, 0, 0)
            };

            Button editPropsBtn = CreateWin11Button("\uE70F", "Edit network info", true);
            editPropsBtn.Click += delegate { OpenEditModal(); };
            editBtnRow.Children.Add(editPropsBtn);

            Button manageLimitsBtn = CreateWin11Button("\uE9D9", "Data & Speed Limits", false);
            manageLimitsBtn.Margin = new Thickness(10, 0, 0, 0);
            manageLimitsBtn.Click += delegate { NavigateToPage("DataUsage"); };
            editBtnRow.Children.Add(manageLimitsBtn);

            propsBody.Children.Add(editBtnRow);
            propsStack.Children.Add(propsBody);
            propsCard.Child = propsStack;
            container.Children.Add(propsCard);

            // 8. Connected Devices Card (with inline Disconnect, Block, and Data Usage)
            Border connectedCard = CreateCardBorder(new Thickness(0), new Thickness(0, 0, 0, 6));
            StackPanel connStack = new StackPanel();

            Grid connHeader = new Grid { Margin = new Thickness(18, 14, 18, 14) };
            connHeader.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(36) });
            connHeader.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            connHeader.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            connHeader.Children.Add(new TextBlock
            {
                FontFamily = _fontIcons,
                Text = "\uE839",
                FontSize = 18,
                Foreground = _theme.TextPrimary,
                VerticalAlignment = VerticalAlignment.Center
            });

            StackPanel connTitleStack = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            connTitleStack.Children.Add(new TextBlock
            {
                Text = "Connected devices",
                FontSize = 14,
                FontWeight = FontWeights.SemiBold,
                Foreground = _theme.TextPrimary
            });
            connTitleStack.Children.Add(new TextBlock
            {
                Text = "View data consumed per device, set speed/download limits, or disconnect and block",
                FontSize = 12,
                Foreground = _theme.TextSecondary,
                Margin = new Thickness(0, 2, 0, 0)
            });
            Grid.SetColumn(connTitleStack, 1);
            connHeader.Children.Add(connTitleStack);

            _connectedHeaderCountText = new TextBlock
            {
                Text = "0 of 8",
                FontSize = 13,
                FontWeight = FontWeights.SemiBold,
                Foreground = _theme.AccentBrush,
                VerticalAlignment = VerticalAlignment.Center
            };
            Grid.SetColumn(_connectedHeaderCountText, 2);
            connHeader.Children.Add(_connectedHeaderCountText);
            connStack.Children.Add(connHeader);
            connStack.Children.Add(CreateHorizontalDivider());

            _inlineConnectedClientsPanel = new StackPanel
            {
                Margin = new Thickness(18, 12, 18, 14)
            };
            connStack.Children.Add(_inlineConnectedClientsPanel);
            connectedCard.Child = connStack;
            container.Children.Add(connectedCard);
        }

        // PAGE 2: Connected Devices & Controls Page
        private void BuildConnectedDevicesPage(StackPanel container)
        {
            Grid topBar = new Grid { Margin = new Thickness(2, 4, 2, 8) };
            topBar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            topBar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            _devicesLiveHeaderCount = new TextBlock
            {
                Text = "Active Connected Devices (0)",
                FontSize = 14,
                FontWeight = FontWeights.SemiBold,
                Foreground = _theme.TextPrimary,
                VerticalAlignment = VerticalAlignment.Center
            };
            Grid.SetColumn(_devicesLiveHeaderCount, 0);
            topBar.Children.Add(_devicesLiveHeaderCount);

            Button resetAllDevDataBtn = CreateWin11Button("\uE777", "Reset all device data counters", false);
            resetAllDevDataBtn.Click += delegate
            {
                ResetAllDataUsageCounters();
            };
            Grid.SetColumn(resetAllDevDataBtn, 1);
            topBar.Children.Add(resetAllDevDataBtn);
            container.Children.Add(topBar);

            _devicesPageLivePanel = new StackPanel { Margin = new Thickness(0, 0, 0, 16) };
            container.Children.Add(_devicesPageLivePanel);

            container.Children.Add(CreateSectionHeader("Blocked Devices (Banned from Hotspot)"));
            _devicesPageBlockedPanel = new StackPanel { Margin = new Thickness(0, 0, 0, 16) };
            container.Children.Add(_devicesPageBlockedPanel);

            container.Children.Add(CreateSectionHeader("All Known Devices & History"));
            _devicesPageHistoryPanel = new StackPanel();
            container.Children.Add(_devicesPageHistoryPanel);
        }

        // PAGE 3: Data Usage & Speed Limits Page
        private void BuildDataUsagePage(StackPanel container)
        {
            // Top 3 Live Traffic Metric Cards
            Grid statsGrid = new Grid { Margin = new Thickness(0, 0, 0, 16) };
            statsGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            statsGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(12) });
            statsGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            statsGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(12) });
            statsGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            Border stat1 = CreateDetailedMetricCard(
                "\uE896",
                "Total Downloaded (Clients)",
                DataFormatter.FormatBytes(GetTotalDownloadedBytes()),
                "↓ 0.0 KB/s current",
                out _dataStatDownloadedValue,
                out _dataStatDownloadedSub);
            Grid.SetColumn(stat1, 0);
            statsGrid.Children.Add(stat1);

            Border stat2 = CreateDetailedMetricCard(
                "\uE898",
                "Total Uploaded (Clients)",
                DataFormatter.FormatBytes(GetTotalUploadedBytes()),
                "↑ 0.0 KB/s current",
                out _dataStatUploadedValue,
                out _dataStatUploadedSub);
            Grid.SetColumn(stat2, 2);
            statsGrid.Children.Add(stat2);

            Border stat3 = CreateDetailedMetricCard(
                "\uE945",
                "Live Hotspot Speed",
                DataFormatter.FormatSpeedKbps(_currentTotalDownloadKbps),
                "Peak: " + DataFormatter.FormatSpeedKbps(_peakDownloadKbps),
                out _dataStatSpeedValue,
                out _dataStatSpeedSub);
            Grid.SetColumn(stat3, 4);
            statsGrid.Children.Add(stat3);

            container.Children.Add(statsGrid);

            // Reset counters bar
            Border resetCard = CreateCardBorder(new Thickness(18, 12, 18, 12), new Thickness(0, 0, 0, 12));
            Grid resetGrid = new Grid();
            resetGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            resetGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            StackPanel resetTextStack = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 14, 0) };
            resetTextStack.Children.Add(new TextBlock
            {
                Text = "Data Consumption Meter",
                FontSize = 14,
                FontWeight = FontWeights.SemiBold,
                Foreground = _theme.TextPrimary
            });
            resetTextStack.Children.Add(new TextBlock
            {
                Text = "Measures exact bytes transmitted and received across the PC Wifi virtual adapter and connected clients",
                FontSize = 12,
                Foreground = _theme.TextSecondary,
                Margin = new Thickness(0, 2, 0, 0),
                TextWrapping = TextWrapping.Wrap
            });
            Grid.SetColumn(resetTextStack, 0);
            resetGrid.Children.Add(resetTextStack);

            Button resetBtn = CreateWin11Button("\uE777", "Reset data usage to 0 MB", false);
            resetBtn.Click += delegate { ResetAllDataUsageCounters(); };
            Grid.SetColumn(resetBtn, 1);
            resetGrid.Children.Add(resetBtn);
            resetCard.Child = resetGrid;
            container.Children.Add(resetCard);

            container.Children.Add(CreateSectionHeader("Upper Download Limit (Data Quota)"));

            // Global Download Limit Card with ProgressBar
            Border quotaCard = CreateCardBorder(new Thickness(18, 14, 18, 14), new Thickness(0, 0, 0, 8));
            StackPanel quotaStack = new StackPanel();

            Grid quotaTopRow = new Grid();
            quotaTopRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(36) });
            quotaTopRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            quotaTopRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            quotaTopRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            quotaTopRow.Children.Add(new TextBlock
            {
                FontFamily = _fontIcons,
                Text = "\uE7BA",
                FontSize = 18,
                Foreground = _theme.AccentBrush,
                VerticalAlignment = VerticalAlignment.Center
            });

            StackPanel quotaTitleStack = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 14, 0) };
            quotaTitleStack.Children.Add(new TextBlock
            {
                Text = "Set upper download limit for hotspot",
                FontSize = 14,
                FontWeight = FontWeights.SemiBold,
                Foreground = _theme.TextPrimary
            });
            quotaTitleStack.Children.Add(new TextBlock
            {
                Text = "Automatically stops the hotspot when total client downloads reach the upper data cap",
                FontSize = 12,
                Foreground = _theme.TextSecondary,
                Margin = new Thickness(0, 2, 0, 0),
                TextWrapping = TextWrapping.Wrap
            });
            Grid.SetColumn(quotaTitleStack, 1);
            quotaTopRow.Children.Add(quotaTitleStack);

            double[] quotaPresetsMb = new double[] { 250, 500, 1024, 2048, 5120, 10240, 20480 };
            string[] quotaLabels = new string[]
            {
                "250 MB",
                "500 MB",
                "1 GB (1024 MB)",
                "2 GB (2048 MB)",
                "5 GB (5120 MB)",
                "10 GB",
                "20 GB"
            };
            _globalQuotaDropdown = CreateStyledComboBox(160);
            _suppressGlobalQuotaEvent = true;
            int selQuotaIdx = 3;
            for (int i = 0; i < quotaPresetsMb.Length; i++)
            {
                _globalQuotaDropdown.AddItem(quotaLabels[i]);
                if (Math.Abs(_settings.GlobalDownloadLimitMB - quotaPresetsMb[i]) < 1.0) selQuotaIdx = i;
            }
            _globalQuotaDropdown.SelectedIndex = selQuotaIdx;
            _suppressGlobalQuotaEvent = false;
            _globalQuotaDropdown.Margin = new Thickness(0, 0, 14, 0);
            _globalQuotaDropdown.SelectionChanged += delegate
            {
                if (_suppressGlobalQuotaEvent) return;
                int idx = _globalQuotaDropdown.SelectedIndex;
                if (idx >= 0 && idx < quotaPresetsMb.Length)
                {
                    _settings.GlobalDownloadLimitMB = quotaPresetsMb[idx];
                    _globalQuotaTriggeredStop = false;
                    _settings.Save();
                    AddLog("Global upper download limit set to: " + quotaLabels[idx]);
                    UpdateDataUsageUi();
                }
            };
            Grid.SetColumn(_globalQuotaDropdown, 2);
            quotaTopRow.Children.Add(_globalQuotaDropdown);

            _globalQuotaToggle = new Win11ToggleSwitch(_theme, true);
            _globalQuotaToggle.SetCheckedSilent(_settings.GlobalDownloadLimitEnabled);
            _globalQuotaToggle.Toggled += delegate(object s, bool enabled)
            {
                _settings.GlobalDownloadLimitEnabled = enabled;
                _globalQuotaTriggeredStop = false;
                _settings.Save();
                AddLog("Global upper download limit " + (enabled ? ("enabled (" + _settings.GlobalDownloadLimitMB + " MB).") : "disabled."));
                UpdateDataUsageUi();
            };
            Grid.SetColumn(_globalQuotaToggle, 3);
            quotaTopRow.Children.Add(_globalQuotaToggle);
            quotaStack.Children.Add(quotaTopRow);

            // Quota Progress Bar row
            StackPanel quotaBarRow = new StackPanel { Margin = new Thickness(36, 12, 0, 2) };
            _globalQuotaProgressBar = new ProgressBar
            {
                Height = 8,
                Minimum = 0,
                Maximum = 100,
                Value = 0,
                Background = _theme.CardSecondaryBg,
                Foreground = _theme.AccentBrush,
                BorderThickness = new Thickness(0)
            };
            quotaBarRow.Children.Add(_globalQuotaProgressBar);

            _globalQuotaStatusText = new TextBlock
            {
                Text = "0.0 MB used • No upper limit active",
                FontSize = 12,
                Foreground = _theme.TextSecondary,
                Margin = new Thickness(0, 6, 0, 0)
            };
            quotaBarRow.Children.Add(_globalQuotaStatusText);
            quotaStack.Children.Add(quotaBarRow);

            quotaCard.Child = quotaStack;
            container.Children.Add(quotaCard);

            container.Children.Add(CreateSectionHeader("Maximum Bandwidth Speed Limiter"));

            // Global Max Speed Limiter Card
            double[] speedPresetsKbps = new double[] { 256, 512, 1024, 2048, 5120, 10240, 20480 };
            string[] speedLabels = new string[]
            {
                "256 KB/s (2 Mbps)",
                "512 KB/s (4 Mbps)",
                "1 MB/s (8 Mbps)",
                "2 MB/s (16 Mbps)",
                "5 MB/s (40 Mbps)",
                "10 MB/s (80 Mbps)",
                "20 MB/s (160 Mbps)"
            };
            _globalSpeedDropdown = CreateStyledComboBox(180);
            _suppressGlobalSpeedEvent = true;
            int selSpeedIdx = 3;
            for (int i = 0; i < speedPresetsKbps.Length; i++)
            {
                _globalSpeedDropdown.AddItem(speedLabels[i]);
                if (Math.Abs(_settings.GlobalMaxSpeedKbps - speedPresetsKbps[i]) < 1.0) selSpeedIdx = i;
            }
            _globalSpeedDropdown.SelectedIndex = selSpeedIdx;
            _suppressGlobalSpeedEvent = false;
            _globalSpeedDropdown.Margin = new Thickness(0, 0, 14, 0);
            _globalSpeedDropdown.SelectionChanged += delegate
            {
                if (_suppressGlobalSpeedEvent) return;
                int idx = _globalSpeedDropdown.SelectedIndex;
                if (idx >= 0 && idx < speedPresetsKbps.Length)
                {
                    _settings.GlobalMaxSpeedKbps = speedPresetsKbps[idx];
                    _settings.Save();
                    if (_settings.GlobalSpeedLimitEnabled)
                    {
                        NetworkEnforcementEngine.ApplyQosSpeedLimit("Global", "192.168.137.0/24", _settings.GlobalMaxSpeedKbps, false);
                    }
                    AddLog("Global max speed limit set to: " + speedLabels[idx]);
                }
            };

            _globalSpeedToggle = new Win11ToggleSwitch(_theme, true);
            _globalSpeedToggle.SetCheckedSilent(_settings.GlobalSpeedLimitEnabled);
            _globalSpeedToggle.Toggled += delegate(object s, bool enabled)
            {
                _settings.GlobalSpeedLimitEnabled = enabled;
                _settings.Save();
                NetworkEnforcementEngine.ApplyQosSpeedLimit("Global", "192.168.137.0/24", enabled ? _settings.GlobalMaxSpeedKbps : 0, false);
                AddLog("Global max speed limiter " + (enabled ? ("enabled (" + DataFormatter.FormatSpeedKbps(_settings.GlobalMaxSpeedKbps) + ").") : "disabled."));
                ShowToast(enabled
                    ? ("Max hotspot speed limited to " + DataFormatter.FormatSpeedKbps(_settings.GlobalMaxSpeedKbps))
                    : "Hotspot speed limit removed (Unlimited).");
            };

            StackPanel speedControls = new StackPanel { Orientation = Orientation.Horizontal };
            speedControls.Children.Add(_globalSpeedDropdown);
            speedControls.Children.Add(_globalSpeedToggle);

            TextBlock speedSub;
            container.Children.Add(CreateSettingRowCard(
                "\uE945",
                "Limit maximum hotspot download speed",
                "Caps the maximum bandwidth speed shared with connected Wi-Fi devices (you can also set per-device speed limits)",
                speedControls,
                out speedSub));

            container.Children.Add(CreateSectionHeader("Per-Device Data Consumption & Individual Limits"));
            _perDeviceDataBreakdownPanel = new StackPanel();
            container.Children.Add(_perDeviceDataBreakdownPanel);
        }

        // PAGE 4: Keep-Alive & Power
        private void BuildKeepAlivePage(StackPanel container)
        {
            Grid statsGrid = new Grid { Margin = new Thickness(0, 0, 0, 16) };
            statsGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            statsGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(12) });
            statsGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            statsGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(12) });
            statsGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            Border stat1 = CreateMetricCard("\uE916", "Hotspot active uptime", "00:00:00", out _statUptimeValue);
            Grid.SetColumn(stat1, 0);
            statsGrid.Children.Add(stat1);

            Border stat2 = CreateMetricCard("\uEA18", "Watchdog health checks", _watchdogCheckCount + " checks", out _statChecksValue);
            Grid.SetColumn(stat2, 2);
            statsGrid.Children.Add(stat2);

            Border stat3 = CreateMetricCard("\uE777", "Auto-recoveries triggered", _autoRecoveryCount + " recoveries", out _statRecoveriesValue);
            Grid.SetColumn(stat3, 4);
            statsGrid.Children.Add(stat3);

            container.Children.Add(statsGrid);

            container.Children.Add(CreateSectionHeader("Always-On Hotspot Protection"));

            _keepAliveMasterToggle = new Win11ToggleSwitch(_theme, true);
            _keepAliveMasterToggle.SetCheckedSilent(_settings.KeepAliveEnabled);
            _keepAliveMasterToggle.Toggled += delegate(object s, bool isOn)
            {
                _settings.KeepAliveEnabled = isOn;
                if (isOn)
                {
                    _settings.ForceDisableOsTimeout = true;
                    _controller.EnsureNoConnectionsTimeoutDisabled();
                }
                _settings.Save();
                AddLog("Active Keep-Alive Watchdog set to: " + (isOn ? "ON" : "OFF"));
                UpdateUiFromSnapshot(_lastSnapshot);
            };
            TextBlock sub1;
            container.Children.Add(CreateSettingRowCard(
                "\uEA18",
                "Active Keep-Alive Watchdog",
                "Continuously monitors hotspot state and immediately restarts it if Windows or the Wi-Fi driver drops it when 0 devices are connected",
                _keepAliveMasterToggle,
                out sub1));

            _intervalComboBox = CreateStyledComboBox(215);
            int[] intervals = new int[] { 2, 5, 10, 30 };
            string[] labels = new string[]
            {
                "Every 2 seconds (Instant)",
                "Every 5 seconds (Recommended)",
                "Every 10 seconds",
                "Every 30 seconds"
            };
            int selectedIdx = 1;
            for (int i = 0; i < intervals.Length; i++)
            {
                _intervalComboBox.AddItem(labels[i]);
                if (_settings.WatchdogIntervalSeconds == intervals[i]) selectedIdx = i;
            }
            _intervalComboBox.SelectedIndex = selectedIdx;
            _intervalComboBox.SelectionChanged += delegate
            {
                int idx = _intervalComboBox.SelectedIndex;
                if (idx >= 0 && idx < intervals.Length)
                {
                    _settings.WatchdogIntervalSeconds = intervals[idx];
                    _settings.Save();
                    _watchdogTimer.Interval = TimeSpan.FromSeconds(_settings.WatchdogIntervalSeconds);
                    AddLog("Watchdog check interval updated to every " + _settings.WatchdogIntervalSeconds + " seconds.");
                }
            };
            TextBlock sub2;
            container.Children.Add(CreateSettingRowCard(
                "\uE916",
                "Watchdog check frequency",
                "How often the background guard verifies your hotspot is broadcasting",
                _intervalComboBox,
                out sub2));

            _forceDisableTimeoutToggle = new Win11ToggleSwitch(_theme, true);
            _forceDisableTimeoutToggle.SetCheckedSilent(_settings.ForceDisableOsTimeout);
            _forceDisableTimeoutToggle.Toggled += delegate(object s, bool isOn)
            {
                _settings.ForceDisableOsTimeout = isOn;
                _settings.Save();
                if (isOn) _controller.EnsureNoConnectionsTimeoutDisabled();
                AddLog("Lock Windows idle timeout OFF set to: " + (isOn ? "ON" : "OFF"));
            };
            TextBlock sub3;
            container.Children.Add(CreateSettingRowCard(
                "\uE72E",
                "Lock Windows 'No Connections Timeout' to OFF",
                "Enforces NetworkOperatorTetheringManager.DisableNoConnectionsTimeout() on every check so Windows never shuts off an empty hotspot",
                _forceDisableTimeoutToggle,
                out sub3));

            _preventSleepToggle = new Win11ToggleSwitch(_theme, true);
            _preventSleepToggle.SetCheckedSilent(_settings.PreventSystemSleep);
            _preventSleepToggle.Toggled += delegate(object s, bool isOn)
            {
                _settings.PreventSystemSleep = isOn;
                _settings.Save();
                UpdateSleepGuardState();
                AddLog("Prevent PC sleep while hotspot is active set to: " + (isOn ? "ON" : "OFF"));
            };
            TextBlock sub4;
            container.Children.Add(CreateSettingRowCard(
                "\uE708",
                "Prevent PC from sleeping while hotspot is active",
                "Uses Windows execution state APIs so idle PC sleep never cuts off your Wi-Fi hotspot",
                _preventSleepToggle,
                out sub4));

            container.Children.Add(CreateSectionHeader("Startup & Background Behavior"));

            _autoStartOnLaunchToggle = new Win11ToggleSwitch(_theme, true);
            _autoStartOnLaunchToggle.SetCheckedSilent(_settings.AutoStartHotspotOnLaunch);
            _autoStartOnLaunchToggle.Toggled += delegate(object s, bool isOn)
            {
                _settings.AutoStartHotspotOnLaunch = isOn;
                _settings.Save();
                AddLog("Auto-start hotspot on app launch set to: " + (isOn ? "ON" : "OFF"));
            };
            TextBlock sub5;
            container.Children.Add(CreateSettingRowCard(
                "\uE768",
                "Automatically turn on hotspot when PC Wifi opens",
                "Starts broadcasting your Wi-Fi hotspot immediately when you launch this app",
                _autoStartOnLaunchToggle,
                out sub5));

            _runAtStartupToggle = new Win11ToggleSwitch(_theme, true);
            _runAtStartupToggle.SetCheckedSilent(AppSettings.IsRunAtStartupEnabled());
            _runAtStartupToggle.Toggled += delegate(object s, bool isOn)
            {
                bool ok = AppSettings.SetRunAtStartup(isOn);
                if (ok)
                {
                    AddLog("Launch automatically at Windows sign-in set to: " + (isOn ? "ON" : "OFF"));
                    ShowToast(isOn ? "Enabled launch at Windows startup." : "Removed from Windows startup.");
                }
            };
            TextBlock sub6;
            container.Children.Add(CreateSettingRowCard(
                "\uE7F8",
                "Start automatically when I sign in to Windows",
                "Launches PC Wifi quietly in the system tray at Windows startup",
                _runAtStartupToggle,
                out sub6));

            _minimizeToTrayToggle = new Win11ToggleSwitch(_theme, true);
            _minimizeToTrayToggle.SetCheckedSilent(_settings.MinimizeToTrayOnClose);
            _minimizeToTrayToggle.Toggled += delegate(object s, bool isOn)
            {
                _settings.MinimizeToTrayOnClose = isOn;
                _settings.Save();
                AddLog("Minimize to system tray on close set to: " + (isOn ? "ON" : "OFF"));
            };
            TextBlock sub7;
            container.Children.Add(CreateSettingRowCard(
                "\uE912",
                "Keep running in system tray when window is closed",
                "Clicking the X button hides PC Wifi to the system tray so the Keep-Alive Watchdog stays active 24/7",
                _minimizeToTrayToggle,
                out sub7));
        }

        // PAGE 5: Activity & Logs
        private void BuildActivityLogsPage(StackPanel container)
        {
            Border actionsCard = CreateCardBorder(new Thickness(18, 12, 18, 12), new Thickness(0, 0, 0, 10));
            Grid actGrid = new Grid();
            actGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            actGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            StackPanel descStack = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            descStack.Children.Add(new TextBlock
            {
                Text = "PC Wifi Event & Watchdog Log",
                FontSize = 14,
                FontWeight = FontWeights.SemiBold,
                Foreground = _theme.TextPrimary
            });
            descStack.Children.Add(new TextBlock
            {
                Text = "Live record of hotspot state checks, device blocks, quota triggers, and automatic reconnections",
                FontSize = 12,
                Foreground = _theme.TextSecondary,
                Margin = new Thickness(0, 2, 0, 0)
            });
            Grid.SetColumn(descStack, 0);
            actGrid.Children.Add(descStack);

            StackPanel btnRow = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            Button runCheckBtn = CreateWin11Button("\uEA18", "Run check now", true);
            runCheckBtn.Click += async delegate
            {
                await RunWatchdogCycleAsync(true);
                ShowToast("Watchdog verification check completed.");
            };
            btnRow.Children.Add(runCheckBtn);

            Button copyLogBtn = CreateWin11Button("\uE8C8", "Copy log", false);
            copyLogBtn.Margin = new Thickness(8, 0, 0, 0);
            copyLogBtn.Click += delegate
            {
                CopyToClipboard(string.Join(Environment.NewLine, _activityLogs), "Activity log copied to clipboard.");
            };
            btnRow.Children.Add(copyLogBtn);

            Button clearLogBtn = CreateWin11Button("\uE74D", "Clear", false);
            clearLogBtn.Margin = new Thickness(8, 0, 0, 0);
            clearLogBtn.Click += delegate
            {
                _activityLogs.Clear();
                AddLog("Activity log cleared.");
            };
            btnRow.Children.Add(clearLogBtn);

            Grid.SetColumn(btnRow, 1);
            actGrid.Children.Add(btnRow);
            actionsCard.Child = actGrid;
            container.Children.Add(actionsCard);

            Border logCard = CreateCardBorder(new Thickness(14), new Thickness(0));
            _logTextBox = new TextBox
            {
                IsReadOnly = true,
                TextWrapping = TextWrapping.Wrap,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                Background = Brushes.Transparent,
                BorderThickness = new Thickness(0),
                Foreground = _theme.TextSecondary,
                FontFamily = new FontFamily("Consolas, Segoe UI"),
                FontSize = 12.5,
                MinHeight = 380,
                Text = string.Join(Environment.NewLine, _activityLogs)
            };
            logCard.Child = _logTextBox;
            container.Children.Add(logCard);
        }
        #endregion

        #region Reusable Windows 11 Card Builders & Device Card Renderer
        private Border CreateCardBorder(Thickness padding, Thickness margin)
        {
            return new Border
            {
                Background = _theme.CardBg,
                BorderBrush = _theme.CardBorder,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(8),
                Padding = padding,
                Margin = margin
            };
        }

        private Border CreateCircleIconBadge(string glyph, Brush accent)
        {
            return new Border
            {
                Width = 38,
                Height = 38,
                CornerRadius = new CornerRadius(19),
                Background = _theme.CardSecondaryBg,
                BorderBrush = _theme.CardBorder,
                BorderThickness = new Thickness(1),
                Child = new TextBlock
                {
                    FontFamily = _fontIcons,
                    Text = glyph,
                    FontSize = 17,
                    Foreground = accent,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center
                }
            };
        }

        private Border CreateSettingRowCard(
            string iconGlyph,
            string title,
            string subtitle,
            UIElement rightControl,
            out TextBlock subtitleBlock)
        {
            Border card = CreateCardBorder(new Thickness(18, 14, 18, 14), new Thickness(0, 0, 0, 6));
            Grid row = new Grid();
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(36) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            TextBlock icon = new TextBlock
            {
                FontFamily = _fontIcons,
                Text = iconGlyph,
                FontSize = 18,
                Foreground = _theme.TextPrimary,
                VerticalAlignment = VerticalAlignment.Center
            };
            Grid.SetColumn(icon, 0);
            row.Children.Add(icon);

            StackPanel textStack = new StackPanel
            {
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 16, 0)
            };
            textStack.Children.Add(new TextBlock
            {
                Text = title,
                FontSize = 14,
                FontWeight = FontWeights.SemiBold,
                Foreground = _theme.TextPrimary,
                TextWrapping = TextWrapping.Wrap
            });
            subtitleBlock = new TextBlock
            {
                Text = subtitle,
                FontSize = 12,
                Foreground = _theme.TextSecondary,
                Margin = new Thickness(0, 2, 0, 0),
                TextWrapping = TextWrapping.Wrap
            };
            textStack.Children.Add(subtitleBlock);
            Grid.SetColumn(textStack, 1);
            row.Children.Add(textStack);

            if (rightControl != null)
            {
                Grid.SetColumn(rightControl, 2);
                row.Children.Add(rightControl);
            }

            card.Child = row;
            return card;
        }

        private Grid CreatePropertyDetailRow(string labelText, out TextBlock valueBlock)
        {
            Grid row = new Grid { Margin = new Thickness(0, 5, 0, 5) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(175) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            row.Children.Add(new TextBlock
            {
                Text = labelText,
                FontSize = 13,
                Foreground = _theme.TextSecondary,
                VerticalAlignment = VerticalAlignment.Center
            });

            valueBlock = new TextBlock
            {
                Text = "-",
                FontSize = 13,
                FontWeight = FontWeights.SemiBold,
                Foreground = _theme.TextPrimary,
                VerticalAlignment = VerticalAlignment.Center
            };
            Grid.SetColumn(valueBlock, 1);
            row.Children.Add(valueBlock);
            return row;
        }

        private Border CreateMetricCard(string glyph, string label, string initialValue, out TextBlock valueBlock)
        {
            Border card = CreateCardBorder(new Thickness(16, 14, 16, 14), new Thickness(0));
            StackPanel stack = new StackPanel();
            StackPanel topRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 8) };
            topRow.Children.Add(new TextBlock
            {
                FontFamily = _fontIcons,
                Text = glyph,
                FontSize = 15,
                Foreground = _theme.AccentBrush,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 8, 0)
            });
            topRow.Children.Add(new TextBlock
            {
                Text = label,
                FontSize = 12,
                Foreground = _theme.TextSecondary,
                VerticalAlignment = VerticalAlignment.Center
            });
            stack.Children.Add(topRow);

            valueBlock = new TextBlock
            {
                Text = initialValue,
                FontFamily = _fontDisplay,
                FontSize = 20,
                FontWeight = FontWeights.SemiBold,
                Foreground = _theme.TextPrimary
            };
            stack.Children.Add(valueBlock);
            card.Child = stack;
            return card;
        }

        private Border CreateDetailedMetricCard(
            string glyph,
            string label,
            string initialValue,
            string initialSub,
            out TextBlock valueBlock,
            out TextBlock subBlock)
        {
            Border card = CreateCardBorder(new Thickness(16, 14, 16, 14), new Thickness(0));
            StackPanel stack = new StackPanel();
            StackPanel topRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 6) };
            topRow.Children.Add(new TextBlock
            {
                FontFamily = _fontIcons,
                Text = glyph,
                FontSize = 15,
                Foreground = _theme.AccentBrush,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 8, 0)
            });
            topRow.Children.Add(new TextBlock
            {
                Text = label,
                FontSize = 12,
                Foreground = _theme.TextSecondary,
                VerticalAlignment = VerticalAlignment.Center
            });
            stack.Children.Add(topRow);

            valueBlock = new TextBlock
            {
                Text = initialValue,
                FontFamily = _fontDisplay,
                FontSize = 22,
                FontWeight = FontWeights.SemiBold,
                Foreground = _theme.TextPrimary
            };
            stack.Children.Add(valueBlock);

            subBlock = new TextBlock
            {
                Text = initialSub,
                FontSize = 11.5,
                Foreground = _theme.AccentBrush,
                Margin = new Thickness(0, 3, 0, 0)
            };
            stack.Children.Add(subBlock);

            card.Child = stack;
            return card;
        }

        private TextBlock CreateSectionHeader(string title)
        {
            return new TextBlock
            {
                Text = title,
                FontSize = 14,
                FontWeight = FontWeights.SemiBold,
                Foreground = _theme.TextPrimary,
                Margin = new Thickness(2, 16, 0, 8)
            };
        }

        private Border CreateHorizontalDivider()
        {
            return new Border
            {
                Height = 1,
                Background = _theme.DividerBrush,
                HorizontalAlignment = HorizontalAlignment.Stretch
            };
        }

        private Button CreateWin11Button(string iconGlyph, string text, bool isAccent, bool isDanger = false)
        {
            Brush normalBg = isAccent ? _theme.AccentBrush : (isDanger ? _theme.DangerBgBrush : _theme.SubtleButtonBg);
            Brush hoverBg = isAccent ? _theme.AccentHoverBrush : _theme.SubtleButtonHoverBg;
            Brush borderBr = isAccent ? _theme.AccentBrush : (isDanger ? _theme.DangerBrush : _theme.SubtleButtonBorder);
            Brush fg = isAccent ? _theme.AccentTextBrush : (isDanger ? _theme.DangerBrush : _theme.TextPrimary);

            Button btn = new Button
            {
                Background = normalBg,
                BorderBrush = borderBr,
                BorderThickness = new Thickness(1),
                Foreground = fg,
                Padding = new Thickness(12, 6, 12, 6),
                Cursor = Cursors.Hand,
                FocusVisualStyle = null
            };

            ControlTemplate tpl = new ControlTemplate(typeof(Button));
            FrameworkElementFactory bd = new FrameworkElementFactory(typeof(Border));
            bd.SetValue(Border.BackgroundProperty, new TemplateBindingExtension(Button.BackgroundProperty));
            bd.SetValue(Border.BorderBrushProperty, new TemplateBindingExtension(Button.BorderBrushProperty));
            bd.SetValue(Border.BorderThicknessProperty, new TemplateBindingExtension(Button.BorderThicknessProperty));
            bd.SetValue(Border.CornerRadiusProperty, new CornerRadius(4));
            bd.SetValue(Border.PaddingProperty, new TemplateBindingExtension(Button.PaddingProperty));

            FrameworkElementFactory cp = new FrameworkElementFactory(typeof(ContentPresenter));
            cp.SetValue(ContentPresenter.HorizontalAlignmentProperty, HorizontalAlignment.Center);
            cp.SetValue(ContentPresenter.VerticalAlignmentProperty, VerticalAlignment.Center);
            bd.AppendChild(cp);
            tpl.VisualTree = bd;
            btn.Template = tpl;

            StackPanel sp = new StackPanel { Orientation = Orientation.Horizontal };
            if (!string.IsNullOrEmpty(iconGlyph))
            {
                sp.Children.Add(new TextBlock
                {
                    FontFamily = _fontIcons,
                    Text = iconGlyph,
                    FontSize = 12,
                    Foreground = fg,
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(0, 0, string.IsNullOrEmpty(text) ? 0 : 6, 0)
                });
            }
            if (!string.IsNullOrEmpty(text))
            {
                sp.Children.Add(new TextBlock
                {
                    Text = text,
                    FontSize = 12.5,
                    FontWeight = (isAccent || isDanger) ? FontWeights.SemiBold : FontWeights.Normal,
                    Foreground = fg,
                    VerticalAlignment = VerticalAlignment.Center
                });
            }

            btn.Content = sp;
            btn.MouseEnter += delegate { btn.Background = hoverBg; };
            btn.MouseLeave += delegate { btn.Background = normalBg; };
            return btn;
        }

        private Win11Dropdown CreateStyledComboBox(double width)
        {
            return new Win11Dropdown(_theme, _fontIcons, width);
        }

        private void ApplyWin11ScrollBarStyle(ScrollViewer scroller)
        {
            try
            {
                string thumbHex = _theme.IsDark ? "#555555" : "#BBBBBB";
                string xaml =
                    "<Style xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' " +
                    "       xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml' TargetType='ScrollBar'>" +
                    "  <Setter Property='Width' Value='6'/>" +
                    "  <Setter Property='MinWidth' Value='6'/>" +
                    "  <Setter Property='Background' Value='Transparent'/>" +
                    "  <Setter Property='Margin' Value='0,4,3,4'/>" +
                    "  <Setter Property='Template'>" +
                    "    <Setter.Value>" +
                    "      <ControlTemplate TargetType='ScrollBar'>" +
                    "        <Grid Background='Transparent'>" +
                    "          <Track Name='PART_Track' IsDirectionReversed='True'>" +
                    "            <Track.Thumb>" +
                    "              <Thumb>" +
                    "                <Thumb.Template>" +
                    "                  <ControlTemplate TargetType='Thumb'>" +
                    "                    <Border Background='" + thumbHex + "' CornerRadius='3'/>" +
                    "                  </ControlTemplate>" +
                    "                </Thumb.Template>" +
                    "              </Thumb>" +
                    "            </Track.Thumb>" +
                    "          </Track>" +
                    "        </Grid>" +
                    "      </ControlTemplate>" +
                    "    </Setter.Value>" +
                    "  </Setter>" +
                    "</Style>";
                Style sbStyle = (Style)System.Windows.Markup.XamlReader.Parse(xaml);
                scroller.Resources.Add(typeof(ScrollBar), sbStyle);
            }
            catch
            {
            }
        }

        private UIElement CreateRichDeviceControlCard(DevicePolicyRecord rec)
        {
            DevicePolicyRecord captured = rec;
            Border card = new Border
            {
                Background = _theme.CardSecondaryBg,
                BorderBrush = captured.IsBlocked ? _theme.DangerBrush : _theme.CardBorder,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(14, 11, 14, 11),
                Margin = new Thickness(0, 0, 0, 8)
            };

            StackPanel mainStack = new StackPanel();
            Grid topRow = new Grid();
            topRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(36) });
            topRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            topRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            topRow.Children.Add(new TextBlock
            {
                FontFamily = _fontIcons,
                Text = captured.IsBlocked ? "\uE8F8" : "\uE8EA",
                FontSize = 18,
                Foreground = captured.IsBlocked
                    ? _theme.DangerBrush
                    : (captured.IsCurrentlyConnected ? _theme.AccentBrush : _theme.TextMuted),
                VerticalAlignment = VerticalAlignment.Center
            });

            StackPanel infoStack = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 12, 0) };

            // Name + Status Badge row
            StackPanel nameRow = new StackPanel { Orientation = Orientation.Horizontal };
            nameRow.Children.Add(new TextBlock
            {
                Text = captured.DisplayName,
                FontSize = 13.5,
                FontWeight = FontWeights.SemiBold,
                Foreground = _theme.TextPrimary,
                VerticalAlignment = VerticalAlignment.Center
            });

            Border statusPill = new Border
            {
                CornerRadius = new CornerRadius(4),
                Padding = new Thickness(6, 1, 6, 1),
                Margin = new Thickness(8, 0, 0, 0),
                Background = captured.IsBlocked
                    ? _theme.DangerBgBrush
                    : (captured.IsCurrentlyConnected ? _theme.SuccessBgBrush : _theme.CardBg)
            };
            statusPill.Child = new TextBlock
            {
                Text = captured.IsBlocked
                    ? (captured.QuotaTriggeredBlock ? "Blocked (Quota Reached)" : "Blocked")
                    : (captured.IsCurrentlyConnected ? "Connected" : "Offline"),
                FontSize = 10.5,
                FontWeight = FontWeights.SemiBold,
                Foreground = captured.IsBlocked
                    ? _theme.DangerBrush
                    : (captured.IsCurrentlyConnected ? _theme.SuccessBrush : _theme.TextMuted)
            };
            nameRow.Children.Add(statusPill);
            infoStack.Children.Add(nameRow);

            // IP + MAC + Live Speed row
            string speedPart = captured.IsCurrentlyConnected
                ? ("   •   Speed: ↓ " + DataFormatter.FormatSpeedKbps(captured.CurrentDownloadSpeedKbps) +
                   "  ↑ " + DataFormatter.FormatSpeedKbps(captured.CurrentUploadSpeedKbps))
                : "";
            infoStack.Children.Add(new TextBlock
            {
                Text = "IP: " + (string.IsNullOrEmpty(captured.LastIpAddress) ? "-" : captured.LastIpAddress) +
                       "   •   MAC: " + captured.MacAddress + speedPart,
                FontSize = 11.5,
                Foreground = _theme.TextSecondary,
                Margin = new Thickness(0, 3, 0, 0)
            });

            // Data Consumed + Limits Summary row
            string limitSummary = "";
            if (captured.MaxDownloadLimitMB > 0)
            {
                limitSummary += "   •   Download Cap: " + captured.MaxDownloadLimitMB.ToString("0") + " MB";
            }
            if (captured.MaxSpeedKbps > 0)
            {
                limitSummary += "   •   Max Speed: " + DataFormatter.FormatSpeedKbps(captured.MaxSpeedKbps);
            }

            infoStack.Children.Add(new TextBlock
            {
                Text = "Data consumed: ↓ " + DataFormatter.FormatBytes(captured.DownloadedBytes) +
                       "   ↑ " + DataFormatter.FormatBytes(captured.UploadedBytes) +
                       "   (Total: " + DataFormatter.FormatBytes(captured.DownloadedBytes + captured.UploadedBytes) + ")" +
                       limitSummary,
                FontSize = 11.5,
                FontWeight = FontWeights.SemiBold,
                Foreground = _theme.AccentBrush,
                Margin = new Thickness(0, 3, 0, 0)
            });

            Grid.SetColumn(infoStack, 1);
            topRow.Children.Add(infoStack);

            // Action Buttons: Limits | Disconnect | Block / Unblock
            StackPanel actionsRow = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                VerticalAlignment = VerticalAlignment.Center
            };

            Button limitsBtn = CreateWin11Button("\uE945", "Limits", false);
            limitsBtn.ToolTip = "Set upper download limit (MB), max speed (KB/s), or custom name for this device";
            limitsBtn.Click += delegate
            {
                OpenDeviceLimitModal(captured);
            };
            actionsRow.Children.Add(limitsBtn);

            if (captured.IsCurrentlyConnected && !captured.IsBlocked)
            {
                Button disconnectBtn = CreateWin11Button("\uE8CD", "Disconnect", false);
                disconnectBtn.Margin = new Thickness(6, 0, 0, 0);
                disconnectBtn.ToolTip = "Disconnect this device from the Wi-Fi hotspot right now";
                disconnectBtn.Click += async delegate
                {
                    await DisconnectDeviceNowAsync(captured);
                };
                actionsRow.Children.Add(disconnectBtn);
            }

            if (captured.IsBlocked)
            {
                Button unblockBtn = CreateWin11Button("\uE785", "Unblock", true);
                unblockBtn.Margin = new Thickness(6, 0, 0, 0);
                unblockBtn.ToolTip = "Unblock this device and allow it to use the hotspot again";
                unblockBtn.Click += delegate
                {
                    UnblockDevice(captured);
                };
                actionsRow.Children.Add(unblockBtn);
            }
            else
            {
                Button blockBtn = CreateWin11Button("\uE8F8", "Block", false, true);
                blockBtn.Margin = new Thickness(6, 0, 0, 0);
                blockBtn.ToolTip = "Block this device from using your Wi-Fi hotspot";
                blockBtn.Click += async delegate
                {
                    await BlockDeviceAsync(captured, false);
                };
                actionsRow.Children.Add(blockBtn);
            }

            Grid.SetColumn(actionsRow, 2);
            topRow.Children.Add(actionsRow);
            mainStack.Children.Add(topRow);

            // Per-device Quota Progress Bar if MaxDownloadLimitMB > 0
            if (captured.MaxDownloadLimitMB > 0)
            {
                double usedMb = captured.DownloadedBytes / (1024.0 * 1024.0);
                double pct = Math.Min(100.0, (usedMb / captured.MaxDownloadLimitMB) * 100.0);
                ProgressBar devBar = new ProgressBar
                {
                    Height = 5,
                    Minimum = 0,
                    Maximum = 100,
                    Value = pct,
                    Margin = new Thickness(36, 8, 0, 0),
                    Background = _theme.CardBg,
                    Foreground = pct >= 100.0 ? _theme.DangerBrush : _theme.AccentBrush,
                    BorderThickness = new Thickness(0)
                };
                mainStack.Children.Add(devBar);
            }

            card.Child = mainStack;
            return card;
        }
        #endregion

        #region Modals ("Edit network info" & "Device Data & Speed Limits") & Toast Banner
        private Border BuildToastBanner()
        {
            Border banner = new Border
            {
                Background = _theme.CardBg,
                BorderBrush = _theme.AccentBrush,
                BorderThickness = new Thickness(1.5),
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(14, 9, 14, 9),
                Margin = new Thickness(0, 52, 36, 0),
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Top,
                Visibility = Visibility.Collapsed,
                Effect = new DropShadowEffect
                {
                    BlurRadius = 14,
                    ShadowDepth = 3,
                    Opacity = 0.35
                }
            };

            StackPanel row = new StackPanel { Orientation = Orientation.Horizontal };
            row.Children.Add(new TextBlock
            {
                FontFamily = _fontIcons,
                Text = "\uE73E",
                FontSize = 14,
                Foreground = _theme.AccentBrush,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 8, 0)
            });
            _toastText = new TextBlock
            {
                Text = "",
                FontSize = 12.5,
                Foreground = _theme.TextPrimary,
                VerticalAlignment = VerticalAlignment.Center
            };
            row.Children.Add(_toastText);
            banner.Child = row;
            return banner;
        }

        private void ShowToast(string message)
        {
            if (_toastBanner == null || _toastText == null) return;
            _toastText.Text = message;
            _toastBanner.Visibility = Visibility.Visible;

            if (_toastTimer == null)
            {
                _toastTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3.5) };
                _toastTimer.Tick += delegate
                {
                    _toastBanner.Visibility = Visibility.Collapsed;
                    _toastTimer.Stop();
                };
            }
            _toastTimer.Stop();
            _toastTimer.Start();
        }

        private Border BuildEditModalOverlay()
        {
            Border overlay = new Border
            {
                Background = _theme.ModalOverlayBrush,
                Visibility = Visibility.Collapsed
            };

            Border dialog = new Border
            {
                Width = 430,
                Background = _theme.CardBg,
                BorderBrush = _theme.CardBorder,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(24, 20, 24, 20),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Effect = new DropShadowEffect
                {
                    BlurRadius = 28,
                    ShadowDepth = 8,
                    Opacity = 0.45
                }
            };

            StackPanel stack = new StackPanel();
            stack.Children.Add(new TextBlock
            {
                Text = "Edit Wi-Fi network info",
                FontFamily = _fontDisplay,
                FontSize = 20,
                FontWeight = FontWeights.SemiBold,
                Foreground = _theme.TextPrimary,
                Margin = new Thickness(0, 0, 0, 16)
            });

            stack.Children.Add(new TextBlock
            {
                Text = "Network name (SSID)",
                FontSize = 13,
                Foreground = _theme.TextSecondary,
                Margin = new Thickness(0, 0, 0, 6)
            });
            _editSsidBox = new TextBox
            {
                Height = 34,
                FontSize = 13.5,
                Padding = new Thickness(10, 5, 10, 5),
                Background = _theme.InputBg,
                Foreground = _theme.TextPrimary,
                CaretBrush = _theme.AccentBrush,
                BorderBrush = _theme.InputBorder,
                BorderThickness = new Thickness(1, 1, 1, 2),
                Margin = new Thickness(0, 0, 0, 14)
            };
            stack.Children.Add(_editSsidBox);

            stack.Children.Add(new TextBlock
            {
                Text = "Network password (at least 8 characters)",
                FontSize = 13,
                Foreground = _theme.TextSecondary,
                Margin = new Thickness(0, 0, 0, 6)
            });

            Grid passInputGrid = new Grid { Margin = new Thickness(0, 0, 0, 14) };
            passInputGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            passInputGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            _editPassVisibleBox = new TextBox
            {
                Height = 34,
                FontSize = 13.5,
                Padding = new Thickness(10, 5, 10, 5),
                Background = _theme.InputBg,
                Foreground = _theme.TextPrimary,
                CaretBrush = _theme.AccentBrush,
                BorderBrush = _theme.InputBorder,
                BorderThickness = new Thickness(1, 1, 1, 2)
            };
            _editPassHiddenBox = new PasswordBox
            {
                Height = 34,
                FontSize = 13.5,
                Padding = new Thickness(10, 5, 10, 5),
                Background = _theme.InputBg,
                Foreground = _theme.TextPrimary,
                CaretBrush = _theme.AccentBrush,
                BorderBrush = _theme.InputBorder,
                BorderThickness = new Thickness(1, 1, 1, 2),
                Visibility = Visibility.Collapsed
            };

            passInputGrid.Children.Add(_editPassVisibleBox);
            passInputGrid.Children.Add(_editPassHiddenBox);

            Button eyeBtn = CreateWin11Button("\uE7B3", "", false);
            eyeBtn.Margin = new Thickness(6, 0, 0, 0);
            eyeBtn.Height = 34;
            eyeBtn.Click += delegate
            {
                _editDialogShowPass = !_editDialogShowPass;
                if (_editDialogShowPass)
                {
                    _editPassVisibleBox.Text = _editPassHiddenBox.Password;
                    _editPassVisibleBox.Visibility = Visibility.Visible;
                    _editPassHiddenBox.Visibility = Visibility.Collapsed;
                }
                else
                {
                    _editPassHiddenBox.Password = _editPassVisibleBox.Text;
                    _editPassHiddenBox.Visibility = Visibility.Visible;
                    _editPassVisibleBox.Visibility = Visibility.Collapsed;
                }
            };
            Grid.SetColumn(eyeBtn, 1);
            passInputGrid.Children.Add(eyeBtn);
            stack.Children.Add(passInputGrid);

            stack.Children.Add(new TextBlock
            {
                Text = "Network band",
                FontSize = 13,
                Foreground = _theme.TextSecondary,
                Margin = new Thickness(0, 0, 0, 6)
            });
            _editBandCombo = new Win11Dropdown(_theme, _fontIcons, double.NaN)
            {
                Height = 34,
                Margin = new Thickness(0, 0, 0, 14)
            };
            _editBandCombo.AddItem("Any available");
            _editBandCombo.AddItem("5 GHz");
            _editBandCombo.AddItem("2.4 GHz");
            _editBandCombo.SelectedIndex = 0;
            stack.Children.Add(_editBandCombo);

            // Hidden Wi-Fi toggle inside Edit Modal too
            Grid hideRow = new Grid { Margin = new Thickness(0, 2, 0, 12) };
            hideRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            hideRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            hideRow.Children.Add(new TextBlock
            {
                Text = "Make Wi-Fi hidden (Stealth SSID)",
                FontSize = 13,
                Foreground = _theme.TextPrimary,
                VerticalAlignment = VerticalAlignment.Center
            });
            _editHiddenToggle = new Win11ToggleSwitch(_theme, true);
            Grid.SetColumn(_editHiddenToggle, 1);
            hideRow.Children.Add(_editHiddenToggle);
            stack.Children.Add(hideRow);

            _editErrorText = new TextBlock
            {
                Text = "",
                FontSize = 12,
                Foreground = _theme.DangerBrush,
                TextWrapping = TextWrapping.Wrap,
                Visibility = Visibility.Collapsed,
                Margin = new Thickness(0, 0, 0, 12)
            };
            stack.Children.Add(_editErrorText);

            Grid btnGrid = new Grid { Margin = new Thickness(0, 8, 0, 0) };
            btnGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            btnGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(10) });
            btnGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            _editSaveBtn = CreateWin11Button(null, "Save", true);
            _editSaveBtn.Height = 34;
            _editSaveBtn.Click += async delegate { await SaveEditedNetworkInfoAsync(); };
            Grid.SetColumn(_editSaveBtn, 0);
            btnGrid.Children.Add(_editSaveBtn);

            Button cancelBtn = CreateWin11Button(null, "Cancel", false);
            cancelBtn.Height = 34;
            cancelBtn.Click += delegate { _editModalOverlay.Visibility = Visibility.Collapsed; };
            Grid.SetColumn(cancelBtn, 2);
            btnGrid.Children.Add(cancelBtn);

            stack.Children.Add(btnGrid);
            dialog.Child = stack;
            overlay.Child = dialog;
            return overlay;
        }

        private void OpenEditModal()
        {
            string visibleSsid = _lastSnapshot.Ssid;
            if (NetworkEnforcementEngine.IsStealthSsid(visibleSsid) && !string.IsNullOrEmpty(_settings.SavedVisibleSsid))
            {
                visibleSsid = _settings.SavedVisibleSsid;
            }
            _editSsidBox.Text = visibleSsid;
            _editDialogShowPass = true;
            _editPassVisibleBox.Text = _lastSnapshot.Passphrase;
            _editPassHiddenBox.Password = _lastSnapshot.Passphrase;
            _editPassVisibleBox.Visibility = Visibility.Visible;
            _editPassHiddenBox.Visibility = Visibility.Collapsed;

            if (_lastSnapshot.Band == TetheringWiFiBand.FiveGigahertz) _editBandCombo.SelectedIndex = 1;
            else if (_lastSnapshot.Band == TetheringWiFiBand.TwoPointFourGigahertz) _editBandCombo.SelectedIndex = 2;
            else _editBandCombo.SelectedIndex = 0;

            _editHiddenToggle.SetCheckedSilent(_settings.IsHiddenWifiEnabled);
            _editErrorText.Visibility = Visibility.Collapsed;
            _editModalOverlay.Visibility = Visibility.Visible;
            _editSsidBox.Focus();
        }

        private async Task SaveEditedNetworkInfoAsync()
        {
            string enteredSsid = (_editSsidBox.Text ?? "").Trim();
            string newPass = _editDialogShowPass ? (_editPassVisibleBox.Text ?? "") : (_editPassHiddenBox.Password ?? "");
            TetheringWiFiBand newBand = TetheringWiFiBand.Auto;
            if (_editBandCombo.SelectedIndex == 1) newBand = TetheringWiFiBand.FiveGigahertz;
            else if (_editBandCombo.SelectedIndex == 2) newBand = TetheringWiFiBand.TwoPointFourGigahertz;
            bool makeHidden = _editHiddenToggle.IsChecked;

            if (string.IsNullOrEmpty(enteredSsid))
            {
                _editErrorText.Text = "Please enter a network name (SSID).";
                _editErrorText.Visibility = Visibility.Visible;
                return;
            }
            if (newPass.Length < 8 || newPass.Length > 63)
            {
                _editErrorText.Text = "Password must be at least 8 characters long.";
                _editErrorText.Visibility = Visibility.Visible;
                return;
            }

            _settings.SavedVisibleSsid = enteredSsid;
            _settings.IsHiddenWifiEnabled = makeHidden;
            _settings.Save();
            _enforcer.SetWifiDirectStealthDiscoverability(makeHidden);

            string targetSsid = makeHidden ? NetworkEnforcementEngine.StealthSsidToken : enteredSsid;

            _editSaveBtn.IsEnabled = false;
            _editErrorText.Visibility = Visibility.Collapsed;

            Tuple<bool, string> res = await _controller.ConfigureAccessPointAsync(
                _settings.PreferredProfileName,
                targetSsid,
                newPass,
                newBand,
                makeHidden);

            _editSaveBtn.IsEnabled = true;

            if (!res.Item1)
            {
                _editErrorText.Text = res.Item2;
                _editErrorText.Visibility = Visibility.Visible;
                AddLog(res.Item2);
                return;
            }

            _editModalOverlay.Visibility = Visibility.Collapsed;
            AddLog(res.Item2);
            ShowToast(makeHidden ? "Saved hidden Wi-Fi settings." : ("Saved Wi-Fi settings (" + enteredSsid + ")."));
            await RefreshHotspotStateAsync(false);
        }

        // Modal 2: Per-Device Data & Speed Limits Dialog
        private Border BuildDeviceLimitModalOverlay()
        {
            Border overlay = new Border
            {
                Background = _theme.ModalOverlayBrush,
                Visibility = Visibility.Collapsed
            };

            Border dialog = new Border
            {
                Width = 430,
                Background = _theme.CardBg,
                BorderBrush = _theme.CardBorder,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(24, 20, 24, 20),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Effect = new DropShadowEffect
                {
                    BlurRadius = 28,
                    ShadowDepth = 8,
                    Opacity = 0.45
                }
            };

            StackPanel stack = new StackPanel();
            _devModalTitleText = new TextBlock
            {
                Text = "Device Data & Speed Limits",
                FontFamily = _fontDisplay,
                FontSize = 19,
                FontWeight = FontWeights.SemiBold,
                Foreground = _theme.TextPrimary
            };
            stack.Children.Add(_devModalTitleText);

            _devModalSubText = new TextBlock
            {
                Text = "",
                FontSize = 12,
                Foreground = _theme.TextSecondary,
                Margin = new Thickness(0, 3, 0, 14)
            };
            stack.Children.Add(_devModalSubText);

            stack.Children.Add(new TextBlock
            {
                Text = "Device friendly name (optional)",
                FontSize = 13,
                Foreground = _theme.TextSecondary,
                Margin = new Thickness(0, 0, 0, 5)
            });
            _devCustomNameBox = new TextBox
            {
                Height = 32,
                FontSize = 13,
                Padding = new Thickness(10, 4, 10, 4),
                Background = _theme.InputBg,
                Foreground = _theme.TextPrimary,
                CaretBrush = _theme.AccentBrush,
                BorderBrush = _theme.InputBorder,
                BorderThickness = new Thickness(1, 1, 1, 2),
                Margin = new Thickness(0, 0, 0, 12)
            };
            stack.Children.Add(_devCustomNameBox);

            stack.Children.Add(new TextBlock
            {
                Text = "Upper download limit in MB (0 = Unlimited)",
                FontSize = 13,
                Foreground = _theme.TextSecondary,
                Margin = new Thickness(0, 0, 0, 5)
            });
            _devMaxDownloadMbBox = new TextBox
            {
                Height = 32,
                FontSize = 13,
                Padding = new Thickness(10, 4, 10, 4),
                Background = _theme.InputBg,
                Foreground = _theme.TextPrimary,
                CaretBrush = _theme.AccentBrush,
                BorderBrush = _theme.InputBorder,
                BorderThickness = new Thickness(1, 1, 1, 2),
                Margin = new Thickness(0, 0, 0, 6)
            };
            stack.Children.Add(_devMaxDownloadMbBox);

            // Quick preset buttons for Download Limit (MB)
            StackPanel mbPresets = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 12) };
            int[] presetMbs = new int[] { 0, 100, 500, 1024, 2048, 5120 };
            string[] presetMbLabels = new string[] { "Unlimited", "100 MB", "500 MB", "1 GB", "2 GB", "5 GB" };
            for (int i = 0; i < presetMbs.Length; i++)
            {
                int val = presetMbs[i];
                Button b = CreateWin11Button(null, presetMbLabels[i], false);
                b.Padding = new Thickness(8, 3, 8, 3);
                b.Margin = new Thickness(0, 0, 5, 0);
                b.Click += delegate { _devMaxDownloadMbBox.Text = val.ToString(); };
                mbPresets.Children.Add(b);
            }
            stack.Children.Add(mbPresets);

            stack.Children.Add(new TextBlock
            {
                Text = "Max download speed in KB/s (0 = Unlimited)",
                FontSize = 13,
                Foreground = _theme.TextSecondary,
                Margin = new Thickness(0, 0, 0, 5)
            });
            _devMaxSpeedKbpsBox = new TextBox
            {
                Height = 32,
                FontSize = 13,
                Padding = new Thickness(10, 4, 10, 4),
                Background = _theme.InputBg,
                Foreground = _theme.TextPrimary,
                CaretBrush = _theme.AccentBrush,
                BorderBrush = _theme.InputBorder,
                BorderThickness = new Thickness(1, 1, 1, 2),
                Margin = new Thickness(0, 0, 0, 6)
            };
            stack.Children.Add(_devMaxSpeedKbpsBox);

            // Quick preset buttons for Max Speed (KB/s)
            StackPanel speedPresets = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 14) };
            int[] presetKbps = new int[] { 0, 128, 256, 512, 1024, 2048 };
            string[] presetSpeedLabels = new string[] { "Unlimited", "128 KB/s", "256 KB/s", "512 KB/s", "1 MB/s", "2 MB/s" };
            for (int i = 0; i < presetKbps.Length; i++)
            {
                int val = presetKbps[i];
                Button b = CreateWin11Button(null, presetSpeedLabels[i], false);
                b.Padding = new Thickness(8, 3, 8, 3);
                b.Margin = new Thickness(0, 0, 5, 0);
                b.Click += delegate { _devMaxSpeedKbpsBox.Text = val.ToString(); };
                speedPresets.Children.Add(b);
            }
            stack.Children.Add(speedPresets);

            Button resetDevUsageBtn = CreateWin11Button("\uE777", "Reset this device's consumed data to 0 MB", false);
            resetDevUsageBtn.Margin = new Thickness(0, 0, 0, 14);
            resetDevUsageBtn.HorizontalAlignment = HorizontalAlignment.Stretch;
            resetDevUsageBtn.Click += delegate
            {
                if (_editingDeviceRecord != null)
                {
                    _editingDeviceRecord.DownloadedBytes = 0;
                    _editingDeviceRecord.UploadedBytes = 0;
                    _editingDeviceRecord.QuotaTriggeredBlock = false;
                    AppSettings.SaveDevices(_deviceRecords.Values);
                    AddLog("Reset consumed data counter for device " + _editingDeviceRecord.DisplayName + ".");
                    ShowToast("Reset data usage for " + _editingDeviceRecord.DisplayName + ".");
                    UpdateUiFromSnapshot(_lastSnapshot);
                }
            };
            stack.Children.Add(resetDevUsageBtn);

            Grid btnGrid = new Grid { Margin = new Thickness(0, 4, 0, 0) };
            btnGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            btnGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(10) });
            btnGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            Button saveBtn = CreateWin11Button(null, "Save Limits", true);
            saveBtn.Height = 34;
            saveBtn.Click += delegate { SaveDeviceLimitsModal(); };
            Grid.SetColumn(saveBtn, 0);
            btnGrid.Children.Add(saveBtn);

            Button cancelBtn = CreateWin11Button(null, "Cancel", false);
            cancelBtn.Height = 34;
            cancelBtn.Click += delegate { _deviceLimitModalOverlay.Visibility = Visibility.Collapsed; };
            Grid.SetColumn(cancelBtn, 2);
            btnGrid.Children.Add(cancelBtn);

            stack.Children.Add(btnGrid);
            dialog.Child = stack;
            overlay.Child = dialog;
            return overlay;
        }

        private void OpenDeviceLimitModal(DevicePolicyRecord rec)
        {
            if (rec == null) return;
            _editingDeviceRecord = rec;
            _devModalTitleText.Text = "Limits for " + rec.DisplayName;
            _devModalSubText.Text = "MAC: " + rec.MacAddress + "   •   IP: " + rec.LastIpAddress +
                                    "   •   Used: " + DataFormatter.FormatBytes(rec.DownloadedBytes);
            _devCustomNameBox.Text = rec.CustomName ?? "";
            _devMaxDownloadMbBox.Text = rec.MaxDownloadLimitMB.ToString("0", CultureInfo.InvariantCulture);
            _devMaxSpeedKbpsBox.Text = rec.MaxSpeedKbps.ToString("0", CultureInfo.InvariantCulture);
            _deviceLimitModalOverlay.Visibility = Visibility.Visible;
        }

        private void SaveDeviceLimitsModal()
        {
            if (_editingDeviceRecord == null) return;
            _editingDeviceRecord.CustomName = (_devCustomNameBox.Text ?? "").Trim();

            double maxMb = 0;
            double.TryParse((_devMaxDownloadMbBox.Text ?? "0").Trim(), NumberStyles.Any, CultureInfo.InvariantCulture, out maxMb);
            _editingDeviceRecord.MaxDownloadLimitMB = Math.Max(0.0, maxMb);

            double maxKbps = 0;
            double.TryParse((_devMaxSpeedKbpsBox.Text ?? "0").Trim(), NumberStyles.Any, CultureInfo.InvariantCulture, out maxKbps);
            _editingDeviceRecord.MaxSpeedKbps = Math.Max(0.0, maxKbps);

            if (!string.IsNullOrEmpty(_editingDeviceRecord.LastIpAddress) &&
                _editingDeviceRecord.LastIpAddress.StartsWith("192.168.137."))
            {
                string safeMac = _editingDeviceRecord.MacAddress.Replace(":", "");
                NetworkEnforcementEngine.ApplyQosSpeedLimit(
                    safeMac,
                    _editingDeviceRecord.LastIpAddress + "/32",
                    _editingDeviceRecord.MaxSpeedKbps,
                    false);
            }

            AppSettings.SaveDevices(_deviceRecords.Values);
            _deviceLimitModalOverlay.Visibility = Visibility.Collapsed;

            AddLog(string.Format("Updated limits for {0}: MaxDownload={1} MB, MaxSpeed={2} KB/s",
                _editingDeviceRecord.DisplayName,
                _editingDeviceRecord.MaxDownloadLimitMB,
                _editingDeviceRecord.MaxSpeedKbps));
            ShowToast("Saved limits for " + _editingDeviceRecord.DisplayName + ".");
            UpdateUiFromSnapshot(_lastSnapshot);
        }
        #endregion

        #region Hidden Wi-Fi, Disconnect, Block & Traffic Tracking Actions
        private async Task ToggleHiddenWifiModeAsync(bool enableHidden)
        {
            _settings.IsHiddenWifiEnabled = enableHidden;
            _enforcer.SetWifiDirectStealthDiscoverability(enableHidden);

            string currentSsid = _lastSnapshot.Ssid ?? "";
            if (enableHidden)
            {
                if (!NetworkEnforcementEngine.IsStealthSsid(currentSsid) && !string.IsNullOrWhiteSpace(currentSsid))
                {
                    _settings.SavedVisibleSsid = currentSsid;
                }
                _settings.Save();

                Tuple<bool, string> res = await _controller.ConfigureAccessPointAsync(
                    _settings.PreferredProfileName,
                    NetworkEnforcementEngine.StealthSsidToken,
                    _lastSnapshot.Passphrase,
                    _lastSnapshot.Band,
                    true);

                AddLog(enableHidden ? ("Enabled Hidden Wi-Fi (Stealth Mode): " + res.Item2) : res.Item2);
                ShowToast("Hidden Wi-Fi enabled: Network name is now cloaked.");
            }
            else
            {
                string restoreSsid = !string.IsNullOrWhiteSpace(_settings.SavedVisibleSsid)
                    ? _settings.SavedVisibleSsid
                    : (Environment.MachineName + " Wifi");
                _settings.Save();

                Tuple<bool, string> res = await _controller.ConfigureAccessPointAsync(
                    _settings.PreferredProfileName,
                    restoreSsid,
                    _lastSnapshot.Passphrase,
                    _lastSnapshot.Band,
                    false);

                AddLog("Restored visible Wi-Fi SSID '" + restoreSsid + "': " + res.Item2);
                ShowToast("Wi-Fi is now visible as '" + restoreSsid + "'.");
            }

            await RefreshHotspotStateAsync(false);
        }

        private async Task DisconnectDeviceNowAsync(DevicePolicyRecord rec)
        {
            if (rec == null) return;
            AddLog("Disconnecting device: " + rec.DisplayName + " (" + rec.LastIpAddress + " / " + rec.MacAddress + ")...");
            ShowToast("Disconnecting " + rec.DisplayName + "...");

            // 1. Temporarily drop ARP & packets to cut active connections immediately
            if (!string.IsNullOrEmpty(rec.LastIpAddress) && rec.LastIpAddress.StartsWith("192.168.137."))
            {
                NetworkEnforcementEngine.ApplyDeviceBlockRule(rec.MacAddress, rec.LastIpAddress, false);
            }

            // 2. Cycle the hotspot AP session so the client's 802.11 association is dropped immediately
            await _controller.RestartHotspotToKickClientAsync(_settings.PreferredProfileName);

            // 3. Remove temporary block rule if device is not permanently blocked
            if (!rec.IsBlocked)
            {
                NetworkEnforcementEngine.RemoveDeviceBlockRule(rec.MacAddress, rec.LastIpAddress, false);
            }

            AddLog("Disconnected " + rec.DisplayName + " from hotspot.");
            await RefreshHotspotStateAsync(false);
        }

        private async Task BlockDeviceAsync(DevicePolicyRecord rec, bool triggeredByQuota)
        {
            if (rec == null) return;
            rec.IsBlocked = true;
            rec.QuotaTriggeredBlock = triggeredByQuota;
            AppSettings.SaveDevices(_deviceRecords.Values);

            bool appliedKernelRule = NetworkEnforcementEngine.ApplyDeviceBlockRule(
                rec.MacAddress,
                rec.LastIpAddress,
                !triggeredByQuota); // Request UAC if user clicked Block directly and not elevated

            AddLog(string.Format("Blocked device {0} ({1} / {2}){3}.",
                rec.DisplayName,
                rec.LastIpAddress,
                rec.MacAddress,
                triggeredByQuota ? " [Download Quota Reached]" : ""));

            ShowToast(triggeredByQuota
                ? ("Quota reached: Automatically blocked " + rec.DisplayName + ".")
                : ("Blocked " + rec.DisplayName + " from using your hotspot."));

            if (!appliedKernelRule && rec.IsCurrentlyConnected)
            {
                await _controller.RestartHotspotToKickClientAsync(_settings.PreferredProfileName);
            }

            UpdateUiFromSnapshot(_lastSnapshot);
        }

        private void UnblockDevice(DevicePolicyRecord rec)
        {
            if (rec == null) return;
            rec.IsBlocked = false;
            rec.QuotaTriggeredBlock = false;
            AppSettings.SaveDevices(_deviceRecords.Values);

            NetworkEnforcementEngine.RemoveDeviceBlockRule(rec.MacAddress, rec.LastIpAddress, true);
            AddLog("Unblocked device: " + rec.DisplayName + " (" + rec.MacAddress + ").");
            ShowToast("Unblocked " + rec.DisplayName + ".");
            UpdateUiFromSnapshot(_lastSnapshot);
        }

        private void InitializeTrafficBaselineFromActiveAdapter()
        {
            try
            {
                string gwIp;
                NetworkInterface ni = NetworkEnforcementEngine.FindHotspotVirtualInterface(out gwIp);
                if (ni != null)
                {
                    IPv4InterfaceStatistics stats = ni.GetIPv4Statistics();
                    long tx = stats.BytesSent;     // PC -> Clients = Client Download
                    long rx = stats.BytesReceived; // Clients -> PC = Client Upload

                    _lastAdapterTxBytes = tx;
                    _lastAdapterRxBytes = rx;
                    _trafficBaselineInitialized = true;
                    _lastTrafficSampleTime = DateTime.Now;

                    // If we have 1 active client and its stored usage is 0, seed it with the current adapter session bytes
                    List<DevicePolicyRecord> activeUnblocked = _deviceRecords.Values
                        .Where(d => d.IsCurrentlyConnected && !d.IsBlocked)
                        .ToList();
                    if (activeUnblocked.Count == 1 && activeUnblocked[0].DownloadedBytes == 0 && tx > 0)
                    {
                        activeUnblocked[0].DownloadedBytes = tx;
                        activeUnblocked[0].UploadedBytes = rx;
                        AppSettings.SaveDevices(_deviceRecords.Values);
                    }
                }
            }
            catch
            {
            }
        }

        private async Task SampleTrafficAndEnforceQuotasAsync()
        {
            try
            {
                string gwIp;
                NetworkInterface ni = NetworkEnforcementEngine.FindHotspotVirtualInterface(out gwIp);
                DateTime now = DateTime.Now;
                double elapsedSec = Math.Max(0.2, (now - _lastTrafficSampleTime).TotalSeconds);
                _lastTrafficSampleTime = now;

                if (ni == null || _lastSnapshot.OperationalState != TetheringOperationalState.On)
                {
                    _currentTotalDownloadKbps = 0;
                    _currentTotalUploadKbps = 0;
                    foreach (DevicePolicyRecord d in _deviceRecords.Values)
                    {
                        d.CurrentDownloadSpeedKbps = 0;
                        d.CurrentUploadSpeedKbps = 0;
                    }
                    UpdateLiveTrafficHeaderAndPage();
                    return;
                }

                IPv4InterfaceStatistics stats = ni.GetIPv4Statistics();
                long tx = stats.BytesSent;     // Client Download
                long rx = stats.BytesReceived; // Client Upload

                if (!_trafficBaselineInitialized || tx < _lastAdapterTxBytes || rx < _lastAdapterRxBytes)
                {
                    _lastAdapterTxBytes = tx;
                    _lastAdapterRxBytes = rx;
                    _trafficBaselineInitialized = true;
                    return;
                }

                long deltaDown = Math.Max(0L, tx - _lastAdapterTxBytes);
                long deltaUp = Math.Max(0L, rx - _lastAdapterRxBytes);
                _lastAdapterTxBytes = tx;
                _lastAdapterRxBytes = rx;

                _currentTotalDownloadKbps = (deltaDown / 1024.0) / elapsedSec;
                _currentTotalUploadKbps = (deltaUp / 1024.0) / elapsedSec;
                if (_currentTotalDownloadKbps > _peakDownloadKbps)
                {
                    _peakDownloadKbps = _currentTotalDownloadKbps;
                }

                // Distribute hardware adapter delta across active unblocked clients
                List<DevicePolicyRecord> activeClients = _deviceRecords.Values
                    .Where(d => d.IsCurrentlyConnected && !d.IsBlocked)
                    .ToList();

                foreach (DevicePolicyRecord d in _deviceRecords.Values)
                {
                    if (!d.IsCurrentlyConnected || d.IsBlocked)
                    {
                        d.CurrentDownloadSpeedKbps = 0;
                        d.CurrentUploadSpeedKbps = 0;
                    }
                }

                if (activeClients.Count > 0)
                {
                    long shareDown = deltaDown / activeClients.Count;
                    long shareUp = deltaUp / activeClients.Count;
                    long remDown = deltaDown % activeClients.Count;
                    long remUp = deltaUp % activeClients.Count;

                    for (int i = 0; i < activeClients.Count; i++)
                    {
                        DevicePolicyRecord c = activeClients[i];
                        long addDown = shareDown + (i == 0 ? remDown : 0);
                        long addUp = shareUp + (i == 0 ? remUp : 0);
                        c.DownloadedBytes += addDown;
                        c.UploadedBytes += addUp;
                        c.CurrentDownloadSpeedKbps = (addDown / 1024.0) / elapsedSec;
                        c.CurrentUploadSpeedKbps = (addUp / 1024.0) / elapsedSec;

                        // Check Per-Device Speed Limit
                        if (c.MaxSpeedKbps > 0 && c.CurrentDownloadSpeedKbps > c.MaxSpeedKbps * 1.05)
                        {
                            NetworkEnforcementEngine.PulseThrottleDeviceArp(c.LastIpAddress, c.MacAddress, 350);
                        }

                        // Check Per-Device Upper Download Limit (Quota)
                        if (c.MaxDownloadLimitMB > 0)
                        {
                            double usedMb = c.DownloadedBytes / (1024.0 * 1024.0);
                            if (usedMb >= c.MaxDownloadLimitMB && !c.IsBlocked)
                            {
                                await BlockDeviceAsync(c, true);
                            }
                        }
                    }
                }

                // Check Global Speed Limit
                if (_settings.GlobalSpeedLimitEnabled &&
                    _settings.GlobalMaxSpeedKbps > 0 &&
                    _currentTotalDownloadKbps > _settings.GlobalMaxSpeedKbps * 1.05)
                {
                    foreach (DevicePolicyRecord c in activeClients)
                    {
                        NetworkEnforcementEngine.PulseThrottleDeviceArp(c.LastIpAddress, c.MacAddress, 300);
                    }
                }

                // Check Global Upper Download Limit (Quota)
                if (_settings.GlobalDownloadLimitEnabled && _settings.GlobalDownloadLimitMB > 0 && !_globalQuotaTriggeredStop)
                {
                    double totalUsedMb = GetTotalDownloadedBytes() / (1024.0 * 1024.0);
                    if (totalUsedMb >= _settings.GlobalDownloadLimitMB)
                    {
                        _globalQuotaTriggeredStop = true;
                        AddLog("Global upper download limit reached (" + totalUsedMb.ToString("0.0", CultureInfo.InvariantCulture) + " MB / " + _settings.GlobalDownloadLimitMB.ToString("0", CultureInfo.InvariantCulture) + " MB). Stopping hotspot...");
                        ShowToast("Upper download limit reached! Stopping Wi-Fi hotspot.");
                        await ToggleHotspotFromUserAsync(false);
                    }
                }

                UpdateLiveTrafficHeaderAndPage();
            }
            catch
            {
            }
        }

        private long GetTotalDownloadedBytes()
        {
            long sum = 0;
            foreach (DevicePolicyRecord d in _deviceRecords.Values)
            {
                sum += Math.Max(0L, d.DownloadedBytes);
            }
            return sum;
        }

        private long GetTotalUploadedBytes()
        {
            long sum = 0;
            foreach (DevicePolicyRecord d in _deviceRecords.Values)
            {
                sum += Math.Max(0L, d.UploadedBytes);
            }
            return sum;
        }

        private void ResetAllDataUsageCounters()
        {
            foreach (DevicePolicyRecord d in _deviceRecords.Values)
            {
                d.DownloadedBytes = 0;
                d.UploadedBytes = 0;
                d.QuotaTriggeredBlock = false;
            }
            _peakDownloadKbps = 0;
            _globalQuotaTriggeredStop = false;
            AppSettings.SaveDevices(_deviceRecords.Values);

            try
            {
                string gwIp;
                NetworkInterface ni = NetworkEnforcementEngine.FindHotspotVirtualInterface(out gwIp);
                if (ni != null)
                {
                    IPv4InterfaceStatistics stats = ni.GetIPv4Statistics();
                    _lastAdapterTxBytes = stats.BytesSent;
                    _lastAdapterRxBytes = stats.BytesReceived;
                }
            }
            catch
            {
            }

            AddLog("Reset all data consumption counters to 0 MB.");
            ShowToast("All data usage counters reset to 0 MB.");
            UpdateUiFromSnapshot(_lastSnapshot);
        }

        private void UpdateLiveTrafficHeaderAndPage()
        {
            if (_titleBarSpeedText != null)
            {
                _titleBarSpeedText.Text = string.Format("↓ {0}   ↑ {1}",
                    DataFormatter.FormatSpeedKbps(_currentTotalDownloadKbps),
                    DataFormatter.FormatSpeedKbps(_currentTotalUploadKbps));
            }

            if (_heroDataSummaryText != null)
            {
                _heroDataSummaryText.Text = DataFormatter.FormatBytes(GetTotalDownloadedBytes()) + " used • " +
                    (_settings.KeepAliveEnabled ? "Guard ON" : "Standard");
            }

            if (_activePage == "DataUsage")
            {
                UpdateDataUsageUi();
            }
            else if (_activePage == "Hotspot" && _inlineConnectedClientsPanel != null && _currentTotalDownloadKbps > 0.1)
            {
                PopulateLiveDevicesStack(_inlineConnectedClientsPanel, _lastSnapshot.OperationalState == TetheringOperationalState.On);
            }
            else if (_activePage == "Devices" && _devicesPageLivePanel != null && _currentTotalDownloadKbps > 0.1)
            {
                PopulateLiveDevicesStack(_devicesPageLivePanel, _lastSnapshot.OperationalState == TetheringOperationalState.On);
            }
        }

        private void UpdateDataUsageUi()
        {
            long totalDown = GetTotalDownloadedBytes();
            long totalUp = GetTotalUploadedBytes();

            if (_dataStatDownloadedValue != null)
                _dataStatDownloadedValue.Text = DataFormatter.FormatBytes(totalDown);
            if (_dataStatDownloadedSub != null)
                _dataStatDownloadedSub.Text = "↓ " + DataFormatter.FormatSpeedKbps(_currentTotalDownloadKbps) + " current";

            if (_dataStatUploadedValue != null)
                _dataStatUploadedValue.Text = DataFormatter.FormatBytes(totalUp);
            if (_dataStatUploadedSub != null)
                _dataStatUploadedSub.Text = "↑ " + DataFormatter.FormatSpeedKbps(_currentTotalUploadKbps) + " current";

            if (_dataStatSpeedValue != null)
                _dataStatSpeedValue.Text = DataFormatter.FormatSpeedKbps(_currentTotalDownloadKbps);
            if (_dataStatSpeedSub != null)
                _dataStatSpeedSub.Text = "Peak: " + DataFormatter.FormatSpeedKbps(_peakDownloadKbps);

            if (_globalQuotaProgressBar != null && _globalQuotaStatusText != null)
            {
                double usedMb = totalDown / (1024.0 * 1024.0);
                if (_settings.GlobalDownloadLimitEnabled && _settings.GlobalDownloadLimitMB > 0)
                {
                    double pct = Math.Min(100.0, (usedMb / _settings.GlobalDownloadLimitMB) * 100.0);
                    _globalQuotaProgressBar.Value = pct;
                    _globalQuotaProgressBar.Foreground = pct >= 95.0 ? _theme.DangerBrush : _theme.AccentBrush;
                    _globalQuotaStatusText.Text = string.Format(CultureInfo.InvariantCulture,
                        "Consumed {0:0.0} MB of {1:0} MB upper limit ({2:0.0}%)",
                        usedMb, _settings.GlobalDownloadLimitMB, pct);
                }
                else
                {
                    _globalQuotaProgressBar.Value = 0;
                    _globalQuotaStatusText.Text = string.Format(CultureInfo.InvariantCulture,
                        "Consumed {0:0.0} MB • Upper download limit is currently Off (Unlimited)", usedMb);
                }
            }

            if (_perDeviceDataBreakdownPanel != null)
            {
                _perDeviceDataBreakdownPanel.Children.Clear();
                if (_deviceRecords.Count == 0)
                {
                    Border empty = CreateCardBorder(new Thickness(16, 12, 16, 12), new Thickness(0));
                    empty.Child = new TextBlock
                    {
                        Text = "No devices recorded yet.",
                        FontSize = 12.5,
                        Foreground = _theme.TextSecondary
                    };
                    _perDeviceDataBreakdownPanel.Children.Add(empty);
                }
                else
                {
                    foreach (DevicePolicyRecord rec in _deviceRecords.Values.OrderByDescending(d => d.DownloadedBytes))
                    {
                        _perDeviceDataBreakdownPanel.Children.Add(CreateRichDeviceControlCard(rec));
                    }
                }
            }
        }
        #endregion

        #region Hotspot Control & Watchdog Keep-Alive Engine
        private async Task ToggleHotspotFromUserAsync(bool turnOn)
        {
            if (_isBusyToggling) return;
            _isBusyToggling = true;

            try
            {
                _userDesiredHotspotOn = turnOn;
                if (_mainHotspotToggle != null) _mainHotspotToggle.IsEnabled = false;
                if (_mainHotspotSubtitle != null)
                {
                    _mainHotspotSubtitle.Text = turnOn ? "Starting PC Wifi hotspot..." : "Stopping PC Wifi hotspot...";
                }

                if (turnOn)
                {
                    _globalQuotaTriggeredStop = false;
                    Tuple<bool, string> res = await _controller.StartHotspotAsync(
                        _settings.PreferredProfileName,
                        _settings.ForceDisableOsTimeout || _settings.KeepAliveEnabled);
                    AddLog(res.Item2);
                    if (res.Item1)
                    {
                        _hotspotActiveSince = DateTime.Now;
                        InitializeTrafficBaselineFromActiveAdapter();
                        ShowToast("PC Wifi hotspot is ON • Keep-Alive Guard active.");
                    }
                    else
                    {
                        ShowToast(res.Item2);
                    }
                }
                else
                {
                    Tuple<bool, string> res = await _controller.StopHotspotAsync(_settings.PreferredProfileName);
                    AddLog(res.Item2);
                    _hotspotActiveSince = null;
                    ShowToast("PC Wifi hotspot turned OFF.");
                }
            }
            finally
            {
                if (_mainHotspotToggle != null) _mainHotspotToggle.IsEnabled = true;
                _isBusyToggling = false;
                UpdateSleepGuardState();
            }
            await RefreshHotspotStateAsync(false);
        }

        private async Task RunWatchdogCycleAsync(bool manualTrigger)
        {
            if (_isBusyToggling) return;

            _watchdogCheckCount++;
            _lastWatchdogCheckTime = DateTime.Now;

            if (_settings.ForceDisableOsTimeout || _settings.KeepAliveEnabled)
            {
                bool fixedTimeout = await Task.Run(() => _controller.EnsureNoConnectionsTimeoutDisabled());
                if (fixedTimeout)
                {
                    AddLog("Watchdog locked Windows idle hotspot timeout back to OFF.");
                }
            }

            HotspotSnapshot snap = await Task.Run(() => _controller.GetSnapshot(_settings.PreferredProfileName));
            _lastSnapshot = snap;

            if (snap.OperationalState == TetheringOperationalState.On && !_userDesiredHotspotOn)
            {
                _userDesiredHotspotOn = true;
                if (_hotspotActiveSince == null) _hotspotActiveSince = DateTime.Now;
                AddLog("Hotspot detected ON. Keep-Alive Guard is now protecting this session.");
            }

            if (_settings.KeepAliveEnabled &&
                _userDesiredHotspotOn &&
                !_globalQuotaTriggeredStop &&
                snap.IsAvailable &&
                snap.OperationalState == TetheringOperationalState.Off)
            {
                _isBusyToggling = true;
                try
                {
                    _autoRecoveryCount++;
                    AddLog("Keep-Alive Guard detected unexpected hotspot drop (0 devices / idle timeout). Automatically restarting hotspot (#" + _autoRecoveryCount + ")...");
                    Tuple<bool, string> recovery = await _controller.StartHotspotAsync(_settings.PreferredProfileName, true);
                    AddLog("Keep-Alive Auto-Recovery result: " + recovery.Item2);
                    if (recovery.Item1 && _hotspotActiveSince == null)
                    {
                        _hotspotActiveSince = DateTime.Now;
                    }
                    snap = await Task.Run(() => _controller.GetSnapshot(_settings.PreferredProfileName));
                    _lastSnapshot = snap;
                }
                finally
                {
                    _isBusyToggling = false;
                }
            }

            await SyncConnectedClientsWithPoliciesAsync(snap.Clients);
            AppSettings.SaveDevices(_deviceRecords.Values);

            if (manualTrigger)
            {
                AddLog("Watchdog check completed: State=" + snap.OperationalState + ", Clients=" + snap.ClientCount + ", IdleTimeout=" + (snap.IsOsTimeoutEnabled ? "Enabled" : "Disabled"));
            }

            UpdateSleepGuardState();
            UpdateUiFromSnapshot(snap);
        }

        private async Task RefreshHotspotStateAsync(bool initial)
        {
            HotspotSnapshot snap = await Task.Run(() => _controller.GetSnapshot(_settings.PreferredProfileName));
            _lastSnapshot = snap;
            await SyncConnectedClientsWithPoliciesAsync(snap.Clients);
            UpdateUiFromSnapshot(snap);
        }

        private async Task SyncConnectedClientsWithPoliciesAsync(List<ConnectedClientSnapshot> currentClients)
        {
            foreach (DevicePolicyRecord existing in _deviceRecords.Values)
            {
                existing.IsCurrentlyConnected = false;
            }

            if (currentClients == null) return;
            foreach (ConnectedClientSnapshot c in currentClients)
            {
                if (c == null || string.IsNullOrEmpty(c.MacAddress)) continue;
                DevicePolicyRecord tracked;
                if (_deviceRecords.TryGetValue(c.MacAddress, out tracked))
                {
                    bool wasOffline = !tracked.IsCurrentlyConnected;
                    tracked.HostName = c.HostName;
                    if (!string.IsNullOrEmpty(c.IpAddress) && !c.IpAddress.EndsWith(".x"))
                    {
                        tracked.LastIpAddress = c.IpAddress;
                    }
                    tracked.LastSeen = DateTime.Now;
                    tracked.IsCurrentlyConnected = true;

                    if (tracked.IsBlocked)
                    {
                        NetworkEnforcementEngine.ApplyDeviceBlockRule(tracked.MacAddress, tracked.LastIpAddress, false);
                    }
                }
                else
                {
                    tracked = new DevicePolicyRecord
                    {
                        MacAddress = c.MacAddress,
                        HostName = c.HostName,
                        LastIpAddress = c.IpAddress,
                        FirstSeen = DateTime.Now,
                        LastSeen = DateTime.Now,
                        IsCurrentlyConnected = true,
                        IsBlocked = _settings.BlockNewUnknownDevices
                    };
                    _deviceRecords[c.MacAddress] = tracked;
                    AddLog("Device connected: " + tracked.DisplayName + " (" + tracked.LastIpAddress + " / " + tracked.MacAddress + ")");

                    if (_settings.BlockNewUnknownDevices)
                    {
                        await BlockDeviceAsync(tracked, false);
                    }
                }
            }
        }

        private void UpdateSleepGuardState()
        {
            bool shouldPreventSleep = _settings.PreventSystemSleep &&
                                      (_userDesiredHotspotOn || _lastSnapshot.OperationalState == TetheringOperationalState.On);
            NativeMethods.UpdateSleepPrevention(shouldPreventSleep);
        }

        private void UpdateUptimeDisplay()
        {
            if (_lastSnapshot.OperationalState == TetheringOperationalState.On)
            {
                if (_hotspotActiveSince == null) _hotspotActiveSince = DateTime.Now;
                TimeSpan elapsed = DateTime.Now - _hotspotActiveSince.Value;
                string formatted = string.Format("{0:D2}:{1:D2}:{2:D2}",
                    (int)elapsed.TotalHours, elapsed.Minutes, elapsed.Seconds);
                if (_statUptimeValue != null) _statUptimeValue.Text = formatted;
            }
            else
            {
                if (_statUptimeValue != null) _statUptimeValue.Text = "00:00:00";
            }
        }
        #endregion

        #region UI State Synchronization
        private void UpdateUiFromSnapshot(HotspotSnapshot snap)
        {
            if (snap == null) return;

            bool isOn = (snap.OperationalState == TetheringOperationalState.On);
            bool inTransition = (snap.OperationalState == TetheringOperationalState.InTransition);
            bool isHiddenNow = _settings.IsHiddenWifiEnabled || NetworkEnforcementEngine.IsStealthSsid(snap.Ssid);
            string displaySsid = isHiddenNow
                ? ("[Hidden] " + (!string.IsNullOrEmpty(_settings.SavedVisibleSsid) ? _settings.SavedVisibleSsid : "Stealth Network"))
                : (string.IsNullOrEmpty(snap.Ssid) ? "PC Wifi" : snap.Ssid);

            if (_titleBarStatusDot != null && _titleBarStatusText != null)
            {
                if (_settings.KeepAliveEnabled)
                {
                    _titleBarStatusDot.Fill = _theme.SuccessBrush;
                    _titleBarStatusText.Text = isOn
                        ? ("Hotspot ON • Never-Disconnect Active (" + snap.ClientCount + " connected)")
                        : "Keep-Alive Guard Armed • Ready";
                }
                else
                {
                    _titleBarStatusDot.Fill = _theme.WarningBrush;
                    _titleBarStatusText.Text = "Keep-Alive Guard Paused";
                }
            }

            if (_sidebarProfileSubText != null)
            {
                _sidebarProfileSubText.Text = snap.IsAvailable
                    ? ("Sharing: " + snap.ProfileName)
                    : "No upstream connection";
            }

            if (_sidebarHeartbeatDot != null && _sidebarHeartbeatText != null)
            {
                _sidebarHeartbeatDot.Fill = _settings.KeepAliveEnabled ? _theme.SuccessBrush : _theme.WarningBrush;
                _sidebarHeartbeatText.Text = _settings.KeepAliveEnabled
                    ? ("Guard active • Check #" + _watchdogCheckCount + " (" + _lastWatchdogCheckTime.ToString("HH:mm:ss") + ")")
                    : "Guard paused • Click Keep-Alive to enable";
            }

            if (_navBadges.ContainsKey("KeepAlive"))
            {
                _navBadges["KeepAlive"].Text = _settings.KeepAliveEnabled ? "ON" : "OFF";
            }
            if (_navBadges.ContainsKey("Devices"))
            {
                _navBadges["Devices"].Text = snap.ClientCount.ToString();
            }

            UpdateTrayStatus(snap, displaySsid);

            if (_activePage == "Hotspot")
            {
                if (_mainHotspotToggle != null && !_isBusyToggling)
                {
                    _mainHotspotToggle.SetCheckedSilent(isOn);
                }
                if (_mainHotspotSubtitle != null && !_isBusyToggling)
                {
                    if (!snap.IsAvailable)
                    {
                        _mainHotspotSubtitle.Text = snap.ErrorMessage ?? "Mobile hotspot is unavailable.";
                    }
                    else if (isOn)
                    {
                        _mainHotspotSubtitle.Text = "Broadcasting " + displaySsid + " over " + HotspotController.FormatBand(snap.Band) +
                            " • Never disconnects even with 0 devices";
                    }
                    else if (inTransition)
                    {
                        _mainHotspotSubtitle.Text = "Hotspot is transitioning state...";
                    }
                    else
                    {
                        _mainHotspotSubtitle.Text = "Broadcast Wi-Fi hotspot from this PC";
                    }
                }

                if (_heroPcNameText != null)
                {
                    _heroPcNameText.Text = snap.IsAvailable ? ("Sharing: " + snap.ProfileName) : "No internet source";
                }
                if (_heroSsidText != null)
                {
                    _heroSsidText.Text = displaySsid;
                }
                if (_heroStatusBadge != null && _heroStatusBadgeText != null)
                {
                    _heroStatusBadge.Background = isOn ? _theme.SuccessBgBrush : _theme.CardSecondaryBg;
                    _heroStatusBadgeText.Foreground = isOn ? _theme.SuccessBrush : _theme.TextSecondary;
                    _heroStatusBadgeText.Text = isOn
                        ? ("Active • " + HotspotController.FormatBand(snap.Band) + (isHiddenNow ? " • Hidden" : ""))
                        : (inTransition ? "Starting..." : "Off");
                }
                if (_heroDataSummaryText != null)
                {
                    _heroDataSummaryText.Text = DataFormatter.FormatBytes(GetTotalDownloadedBytes()) + " used • " +
                        (_settings.KeepAliveEnabled ? "Guard ON" : "Standard");
                }

                if (_quickKeepAliveToggle != null) _quickKeepAliveToggle.SetCheckedSilent(_settings.KeepAliveEnabled);
                if (_hiddenWifiToggle != null) _hiddenWifiToggle.SetCheckedSilent(isHiddenNow);
                if (_lockUnknownDevicesToggle != null) _lockUnknownDevicesToggle.SetCheckedSilent(_settings.BlockNewUnknownDevices);

                if (_profileComboBox != null)
                {
                    _suppressProfileComboEvent = true;
                    _profileComboBox.ClearItems();
                    string autoLabel = "Automatic (" + (snap.ProfileName ?? "Active Internet") + ")";
                    _profileComboBox.AddItem(autoLabel);
                    int selIdx = 0;
                    for (int i = 0; i < snap.AvailableProfiles.Count; i++)
                    {
                        string pName = snap.AvailableProfiles[i];
                        _profileComboBox.AddItem(pName);
                        if (!string.Equals(_settings.PreferredProfileName, "Auto", StringComparison.OrdinalIgnoreCase) &&
                            string.Equals(_settings.PreferredProfileName, pName, StringComparison.OrdinalIgnoreCase))
                        {
                            selIdx = i + 1;
                        }
                    }
                    _profileComboBox.SelectedIndex = selIdx;
                    _suppressProfileComboEvent = false;
                }

                if (_propSsidValue != null) _propSsidValue.Text = displaySsid;
                if (_propPasswordValue != null)
                {
                    if (string.IsNullOrEmpty(snap.Passphrase))
                    {
                        _propPasswordValue.Text = "-";
                    }
                    else
                    {
                        _propPasswordValue.Text = _isPasswordRevealed
                            ? snap.Passphrase
                            : new string('•', Math.Max(8, snap.Passphrase.Length));
                    }
                }
                if (_propEyeIcon != null) _propEyeIcon.Text = _isPasswordRevealed ? "\uED1A" : "\uE7B3";
                if (_propBandValue != null) _propBandValue.Text = HotspotController.FormatBand(snap.Band);
                if (_propVisibilityValue != null)
                {
                    _propVisibilityValue.Text = isHiddenNow ? "Hidden (Stealth Mode)" : "Visible (Standard Broadcast)";
                    _propVisibilityValue.Foreground = isHiddenNow ? _theme.AccentBrush : _theme.TextPrimary;
                }

                if (_connectedHeaderCountText != null)
                {
                    _connectedHeaderCountText.Text = snap.ClientCount + " of " + snap.MaxClientCount;
                }

                if (_inlineConnectedClientsPanel != null)
                {
                    PopulateLiveDevicesStack(_inlineConnectedClientsPanel, isOn);
                }
            }
            else if (_activePage == "Devices")
            {
                if (_devicesLiveHeaderCount != null)
                {
                    _devicesLiveHeaderCount.Text = "Active Connected Devices (" + snap.ClientCount + " of " + snap.MaxClientCount + ")";
                }
                if (_devicesPageLivePanel != null)
                {
                    PopulateLiveDevicesStack(_devicesPageLivePanel, isOn);
                }
                if (_devicesPageBlockedPanel != null)
                {
                    PopulateBlockedDevicesStack(_devicesPageBlockedPanel);
                }
                if (_devicesPageHistoryPanel != null)
                {
                    PopulateAllDevicesHistoryStack(_devicesPageHistoryPanel);
                }
            }
            else if (_activePage == "DataUsage")
            {
                UpdateDataUsageUi();
            }
            else if (_activePage == "KeepAlive")
            {
                UpdateUptimeDisplay();
                if (_statChecksValue != null) _statChecksValue.Text = _watchdogCheckCount + " checks";
                if (_statRecoveriesValue != null) _statRecoveriesValue.Text = _autoRecoveryCount + " recoveries";
                if (_keepAliveMasterToggle != null) _keepAliveMasterToggle.SetCheckedSilent(_settings.KeepAliveEnabled);
                if (_forceDisableTimeoutToggle != null) _forceDisableTimeoutToggle.SetCheckedSilent(_settings.ForceDisableOsTimeout);
            }

            // Re-apply translations after live state updates and dynamic device-panel refreshes.
            ApplyLocalization();
        }

        private void PopulateLiveDevicesStack(StackPanel panel, bool hotspotIsOn)
        {
            panel.Children.Clear();
            List<DevicePolicyRecord> live = _deviceRecords.Values
                .Where(d => d.IsCurrentlyConnected)
                .OrderByDescending(d => d.DownloadedBytes)
                .ToList();

            if (live.Count == 0)
            {
                Border emptyBd = new Border
                {
                    Background = _theme.CardSecondaryBg,
                    BorderBrush = _theme.CardBorder,
                    BorderThickness = new Thickness(1),
                    CornerRadius = new CornerRadius(6),
                    Padding = new Thickness(14, 12, 14, 12)
                };
                emptyBd.Child = new TextBlock
                {
                    Text = hotspotIsOn
                        ? "No devices are currently connected. Keep-Alive Guard is actively keeping your PC Wifi network online so devices can join anytime."
                        : "Hotspot is currently off. Turn on PC Wifi Hotspot to allow devices to connect.",
                    FontSize = 12.5,
                    Foreground = _theme.TextSecondary,
                    TextWrapping = TextWrapping.Wrap
                };
                panel.Children.Add(emptyBd);
                return;
            }

            foreach (DevicePolicyRecord rec in live)
            {
                panel.Children.Add(CreateRichDeviceControlCard(rec));
            }
        }

        private void PopulateBlockedDevicesStack(StackPanel panel)
        {
            panel.Children.Clear();
            List<DevicePolicyRecord> blocked = _deviceRecords.Values
                .Where(d => d.IsBlocked)
                .OrderByDescending(d => d.LastSeen)
                .ToList();

            if (blocked.Count == 0)
            {
                Border emptyCard = CreateCardBorder(new Thickness(16, 12, 16, 12), new Thickness(0));
                emptyCard.Child = new TextBlock
                {
                    Text = "No blocked devices. Click 'Block' on any connected device to prevent it from using your hotspot.",
                    FontSize = 12.5,
                    Foreground = _theme.TextSecondary
                };
                panel.Children.Add(emptyCard);
                return;
            }

            foreach (DevicePolicyRecord rec in blocked)
            {
                panel.Children.Add(CreateRichDeviceControlCard(rec));
            }
        }

        private void PopulateAllDevicesHistoryStack(StackPanel panel)
        {
            panel.Children.Clear();
            if (_deviceRecords.Count == 0)
            {
                Border emptyCard = CreateCardBorder(new Thickness(16, 12, 16, 12), new Thickness(0));
                emptyCard.Child = new TextBlock
                {
                    Text = "No devices have connected yet.",
                    FontSize = 12.5,
                    Foreground = _theme.TextSecondary
                };
                panel.Children.Add(emptyCard);
                return;
            }

            foreach (DevicePolicyRecord rec in _deviceRecords.Values.OrderByDescending(c => c.LastSeen))
            {
                panel.Children.Add(CreateRichDeviceControlCard(rec));
            }
        }
        #endregion

        #region System Tray, Elevation & Utilities
        private void InitializeSystemTray()
        {
            _trayIcon = new System.Windows.Forms.NotifyIcon
            {
                Text = "PC Wifi",
                Visible = true
            };

            try
            {
                string icoPath = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "app.ico");
                if (File.Exists(icoPath))
                {
                    _trayIcon.Icon = new System.Drawing.Icon(icoPath);
                }
                else
                {
                    _trayIcon.Icon = System.Drawing.SystemIcons.Application;
                }
            }
            catch
            {
                _trayIcon.Icon = System.Drawing.SystemIcons.Application;
            }

            System.Windows.Forms.ContextMenuStrip menu = new System.Windows.Forms.ContextMenuStrip();

            System.Windows.Forms.ToolStripMenuItem openItem = new System.Windows.Forms.ToolStripMenuItem("Open PC Wifi");
            openItem.Font = new System.Drawing.Font(openItem.Font, System.Drawing.FontStyle.Bold);
            openItem.Click += delegate { RestoreFromTray(); };
            menu.Items.Add(openItem);

            menu.Items.Add(new System.Windows.Forms.ToolStripSeparator());

            _trayToggleHotspotItem = new System.Windows.Forms.ToolStripMenuItem("Turn Hotspot On");
            _trayToggleHotspotItem.Click += async delegate
            {
                bool currentlyOn = (_lastSnapshot.OperationalState == TetheringOperationalState.On);
                await ToggleHotspotFromUserAsync(!currentlyOn);
            };
            menu.Items.Add(_trayToggleHotspotItem);

            _trayToggleKeepAliveItem = new System.Windows.Forms.ToolStripMenuItem("Keep-Alive Guard (Never Disconnect)")
            {
                Checked = _settings.KeepAliveEnabled
            };
            _trayToggleKeepAliveItem.Click += delegate
            {
                _settings.KeepAliveEnabled = !_settings.KeepAliveEnabled;
                _settings.Save();
                UpdateUiFromSnapshot(_lastSnapshot);
            };
            menu.Items.Add(_trayToggleKeepAliveItem);

            menu.Items.Add(new System.Windows.Forms.ToolStripSeparator());

            System.Windows.Forms.ToolStripMenuItem exitItem = new System.Windows.Forms.ToolStripMenuItem("Exit PC Wifi");
            exitItem.Click += delegate
            {
                _isExitingExplicitly = true;
                Close();
            };
            menu.Items.Add(exitItem);

            _trayIcon.ContextMenuStrip = menu;
            _trayIcon.DoubleClick += delegate { RestoreFromTray(); };
        }

        private void UpdateTrayStatus(HotspotSnapshot snap, string displaySsid)
        {
            if (_trayIcon == null) return;
            bool isOn = (snap.OperationalState == TetheringOperationalState.On);
            string tip = isOn
                ? string.Format("PC Wifi ON ({0}) • {1} clients • {2}",
                    displaySsid, snap.ClientCount, DataFormatter.FormatBytes(GetTotalDownloadedBytes()))
                : string.Format("PC Wifi OFF • Keep-Alive {0}", _settings.KeepAliveEnabled ? "Armed" : "OFF");

            if (tip.Length > 63) tip = tip.Substring(0, 63);
            _trayIcon.Text = tip;

            if (_trayToggleHotspotItem != null)
            {
                _trayToggleHotspotItem.Text = isOn ? "Turn Hotspot Off" : "Turn Hotspot On";
            }
            if (_trayToggleKeepAliveItem != null)
            {
                _trayToggleKeepAliveItem.Checked = _settings.KeepAliveEnabled;
            }
        }

        private void HideToTray(bool showBalloon)
        {
            Hide();
            if (showBalloon && _trayIcon != null)
            {
                _trayIcon.ShowBalloonTip(
                    2500,
                    "PC Wifi Running in Background",
                    "Keep-Alive Guard and Traffic Monitor continue running in the system tray.",
                    System.Windows.Forms.ToolTipIcon.Info);
            }
        }

        private void RestoreFromTray()
        {
            Show();
            if (WindowState == WindowState.Minimized)
            {
                WindowState = WindowState.Normal;
            }
            Activate();
        }

        private void RestartElevated()
        {
            try
            {
                string exePath = Process.GetCurrentProcess().MainModule.FileName;
                ProcessStartInfo psi = new ProcessStartInfo(exePath)
                {
                    Verb = "runas",
                    UseShellExecute = true
                };
                Process.Start(psi);
                _isExitingExplicitly = true;
                Close();
            }
            catch
            {
                ShowToast("Administrator elevation was cancelled.");
            }
        }

        private void CopyToClipboard(string text, string toastMsg)
        {
            if (string.IsNullOrEmpty(text)) return;
            try
            {
                Clipboard.SetText(text);
                ShowToast(toastMsg);
            }
            catch
            {
            }
        }

        private void AddLog(string message)
        {
            string entry = "[" + DateTime.Now.ToString("HH:mm:ss") + "] " + message;
            _activityLogs.Add(entry);
            if (_activityLogs.Count > 500)
            {
                _activityLogs.RemoveAt(0);
            }
            if (_logTextBox != null && _activePage == "Logs")
            {
                _logTextBox.Text = string.Join(Environment.NewLine, _activityLogs);
                _logTextBox.ScrollToEnd();
            }
        }

        private void CaptureUiPageToPng(string filePath)
        {
            try
            {
                UpdateLayout();
                int w = (int)Math.Max(800, ActualWidth);
                int h = (int)Math.Max(500, ActualHeight);
                System.Windows.Media.Imaging.RenderTargetBitmap rtb =
                    new System.Windows.Media.Imaging.RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
                rtb.Render(_outerWindowBorder);
                System.Windows.Media.Imaging.PngBitmapEncoder encoder =
                    new System.Windows.Media.Imaging.PngBitmapEncoder();
                encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(rtb));
                using (FileStream fs = File.Create(filePath))
                {
                    encoder.Save(fs);
                }
            }
            catch
            {
            }
        }
        #endregion
    }
    #endregion

    #region Application Entry Point
    internal static class Program
    {
        private static Mutex _mutex;

        [STAThread]
        public static void Main(string[] args)
        {
            bool startMinimized = false;
            string screenshotDir = null;
            if (args != null)
            {
                for (int i = 0; i < args.Length; i++)
                {
                    string a = args[i];
                    if (string.Equals(a, "--minimized", StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(a, "-minimized", StringComparison.OrdinalIgnoreCase))
                    {
                        startMinimized = true;
                    }
                    else if (string.Equals(a, "--screenshots", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
                    {
                        screenshotDir = args[i + 1];
                    }
                }
            }

            if (string.IsNullOrEmpty(screenshotDir))
            {
                bool createdNew;
                _mutex = new Mutex(true, "Local\\PCWifi_SingleInstanceMutex", out createdNew);
                if (!createdNew)
                {
                    IntPtr existingHwnd = NativeMethods.FindWindow(IntPtr.Zero, "PC Wifi");
                    if (existingHwnd != IntPtr.Zero)
                    {
                        NativeMethods.ShowWindow(existingHwnd, NativeMethods.SW_RESTORE);
                        NativeMethods.SetForegroundWindow(existingHwnd);
                    }
                    return;
                }
            }

            try
            {
                Application app = new Application();
                app.DispatcherUnhandledException += delegate(object s, DispatcherUnhandledExceptionEventArgs e)
                {
                    try
                    {
                        File.WriteAllText(
                            System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "error.log"),
                            e.Exception.ToString());
                    }
                    catch { }
                    e.Handled = true;
                };
                MainWindow win = new MainWindow(startMinimized, screenshotDir);
                app.Run(win);
            }
            catch (Exception ex)
            {
                try
                {
                    File.WriteAllText(
                        System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "error.log"),
                        ex.ToString());
                }
                catch { }
            }
        }
    }
    #endregion
}
