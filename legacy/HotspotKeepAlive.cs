using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
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
using Windows.Foundation;
using Windows.Networking;
using Windows.Networking.Connectivity;
using Windows.Networking.NetworkOperators;

namespace Win11HotspotKeepAlive
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
                // Ignore on older builds
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

    #region Persistent Settings
    internal sealed class AppSettings
    {
        public bool KeepAliveEnabled { get; set; }
        public bool ForceDisableOsTimeout { get; set; }
        public bool PreventSystemSleep { get; set; }
        public int WatchdogIntervalSeconds { get; set; }
        public bool AutoStartHotspotOnLaunch { get; set; }
        public bool MinimizeToTrayOnClose { get; set; }
        public bool IsDarkMode { get; set; }
        public string PreferredProfileName { get; set; }

        public AppSettings()
        {
            KeepAliveEnabled = true;
            ForceDisableOsTimeout = true;
            PreventSystemSleep = true;
            WatchdogIntervalSeconds = 5;
            AutoStartHotspotOnLaunch = false;
            MinimizeToTrayOnClose = true;
            IsDarkMode = true;
            PreferredProfileName = "Auto";
        }

        private static string GetSettingsFilePath()
        {
            string dir = System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Win11HotspotKeepAlive");
            if (!Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
            }
            return System.IO.Path.Combine(dir, "settings.cfg");
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
                    if (key == "KeepAliveEnabled" && bool.TryParse(val, out b)) s.KeepAliveEnabled = b;
                    else if (key == "ForceDisableOsTimeout" && bool.TryParse(val, out b)) s.ForceDisableOsTimeout = b;
                    else if (key == "PreventSystemSleep" && bool.TryParse(val, out b)) s.PreventSystemSleep = b;
                    else if (key == "WatchdogIntervalSeconds" && int.TryParse(val, out n) && n >= 2 && n <= 120) s.WatchdogIntervalSeconds = n;
                    else if (key == "AutoStartHotspotOnLaunch" && bool.TryParse(val, out b)) s.AutoStartHotspotOnLaunch = b;
                    else if (key == "MinimizeToTrayOnClose" && bool.TryParse(val, out b)) s.MinimizeToTrayOnClose = b;
                    else if (key == "IsDarkMode" && bool.TryParse(val, out b)) s.IsDarkMode = b;
                    else if (key == "PreferredProfileName" && !string.IsNullOrEmpty(val)) s.PreferredProfileName = val;
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
                sb.AppendLine("# Windows 11 Hotspot Keep-Alive Settings");
                sb.AppendLine("KeepAliveEnabled=" + KeepAliveEnabled);
                sb.AppendLine("ForceDisableOsTimeout=" + ForceDisableOsTimeout);
                sb.AppendLine("PreventSystemSleep=" + PreventSystemSleep);
                sb.AppendLine("WatchdogIntervalSeconds=" + WatchdogIntervalSeconds);
                sb.AppendLine("AutoStartHotspotOnLaunch=" + AutoStartHotspotOnLaunch);
                sb.AppendLine("MinimizeToTrayOnClose=" + MinimizeToTrayOnClose);
                sb.AppendLine("IsDarkMode=" + IsDarkMode);
                sb.AppendLine("PreferredProfileName=" + (PreferredProfileName ?? "Auto"));
                File.WriteAllText(path, sb.ToString(), Encoding.UTF8);
            }
            catch
            {
            }
        }

        private const string RunRegKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        private const string RunValueName = "Win11HotspotKeepAlive";

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

    #region Data Models & Hotspot Controller
    internal sealed class ConnectedClientInfo
    {
        public string HostName { get; set; }
        public string IpAddress { get; set; }
        public string MacAddress { get; set; }
        public DateTime FirstSeen { get; set; }
        public DateTime LastSeen { get; set; }
        public bool IsCurrentlyConnected { get; set; }
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
        public List<ConnectedClientInfo> Clients { get; set; }
        public List<string> AvailableProfiles { get; set; }
        public string ErrorMessage { get; set; }

        public HotspotSnapshot()
        {
            Clients = new List<ConnectedClientInfo>();
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

                try
                {
                    IReadOnlyList<NetworkOperatorTetheringClient> rawClients = tm.GetTetheringClients();
                    if (rawClients != null)
                    {
                        foreach (NetworkOperatorTetheringClient c in rawClients)
                        {
                            if (c == null) continue;
                            string host = "Connected Device";
                            string ip = "Assigned via DHCP";
                            try
                            {
                                if (c.HostNames != null && c.HostNames.Count > 0)
                                {
                                    List<string> names = new List<string>();
                                    foreach (HostName hn in c.HostNames)
                                    {
                                        if (hn == null || string.IsNullOrEmpty(hn.CanonicalName)) continue;
                                        if (hn.Type == HostNameType.Ipv4 || hn.Type == HostNameType.Ipv6)
                                        {
                                            ip = hn.CanonicalName;
                                        }
                                        else
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

                            snap.Clients.Add(new ConnectedClientInfo
                            {
                                HostName = host,
                                IpAddress = ip,
                                MacAddress = string.IsNullOrEmpty(c.MacAddress) ? "Unknown MAC" : c.MacAddress,
                                FirstSeen = DateTime.Now,
                                LastSeen = DateTime.Now,
                                IsCurrentlyConnected = true
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
                    return true; // Changed from True -> False
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
                    try
                    {
                        NetworkOperatorTetheringManager.DisableNoConnectionsTimeout();
                    }
                    catch
                    {
                    }
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
                    return Tuple.Create(true, "Mobile hotspot is already active.");
                }

                // Wait briefly if currently InTransition
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
                    return Tuple.Create(true, "Mobile hotspot started on profile '" + profile.ProfileName + "'.");
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
                    return Tuple.Create(true, "Mobile hotspot is already off.");
                }

                NetworkOperatorTetheringOperationResult result = await WinRtAsync.RunAsync(
                    () => tm.StopTetheringAsync());

                if (result != null && result.Status == TetheringOperationStatus.Success)
                {
                    return Tuple.Create(true, "Mobile hotspot turned off.");
                }

                string statusText = result != null ? result.Status.ToString() : "Unknown";
                return Tuple.Create(false, "Could not stop hotspot: " + statusText);
            }
            catch (Exception ex)
            {
                return Tuple.Create(false, "Error stopping hotspot: " + ex.Message);
            }
        }

        public async Task<Tuple<bool, string>> ConfigureAccessPointAsync(
            string preferredProfile,
            string newSsid,
            string newPassphrase,
            TetheringWiFiBand newBand)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(newSsid))
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
                cfg.Ssid = newSsid.Trim();
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
                    return Tuple.Create(false, "The selected Wi-Fi band (" + FormatBand(newBand) + ") is not supported by your Wi-Fi adapter. Choose 'Any available' or '2.4 GHz'.");
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

                return Tuple.Create(true, "Updated hotspot properties (SSID: " + cfg.Ssid + ", Band: " + FormatBand(newBand) + ").");
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

    #region Theme Palette (Windows 11 Dark & Light Mode)
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
                t.CardSecondaryBg = Hex("#252525");
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
                t.CardSecondaryBg = Hex("#F2F2F2");
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
                t.InputBg = Hex("#FFFFFF");
                t.InputBorder = Hex("#CCCCCC");
                t.ModalOverlayBrush = Hex("#77000000");
            }
            return t;
        }
    }
    #endregion

    #region Custom Windows 11 Controls (Pill ToggleSwitch, Win11Dropdown & Styled Buttons)
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

        public int ItemCount
        {
            get { return _items.Count; }
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

        public void ApplyTheme(Win11Theme theme)
        {
            _theme = theme;
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

    #region Main Windows 11 Settings Window
    internal sealed class MainWindow : Window
    {
        private AppSettings _settings;
        private readonly HotspotController _controller;
        private Win11Theme _theme;

        private readonly DispatcherTimer _watchdogTimer;
        private readonly DispatcherTimer _uptimeTimer;

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
        private readonly Dictionary<string, ConnectedClientInfo> _sessionClients = new Dictionary<string, ConnectedClientInfo>(StringComparer.OrdinalIgnoreCase);
        private readonly List<string> _activityLogs = new List<string>();

        // System Tray
        private System.Windows.Forms.NotifyIcon _trayIcon;
        private System.Windows.Forms.ToolStripMenuItem _trayToggleHotspotItem;
        private System.Windows.Forms.ToolStripMenuItem _trayToggleKeepAliveItem;

        // Font families
        private FontFamily _fontDisplay;
        private FontFamily _fontText;
        private FontFamily _fontIcons;

        // Root layout elements for live re-theming
        private Border _outerWindowBorder;
        private Grid _mainShellGrid;
        private Border _sidebarBorder;
        private Border _titleBarStatusPill;
        private Ellipse _titleBarStatusDot;
        private TextBlock _titleBarStatusText;
        private TextBlock _sidebarProfileSubText;
        private TextBox _searchBox;
        private TextBlock _breadcrumbSectionText;
        private TextBlock _breadcrumbPageText;
        private StackPanel _pageContainer;
        private TextBlock _sidebarHeartbeatText;
        private Ellipse _sidebarHeartbeatDot;

        // Nav items
        private readonly Dictionary<string, Border> _navItems = new Dictionary<string, Border>();
        private readonly Dictionary<string, Border> _navPills = new Dictionary<string, Border>();
        private readonly Dictionary<string, TextBlock> _navBadges = new Dictionary<string, TextBlock>();

        // Live controls on Page 1 (Mobile hotspot)
        private Win11ToggleSwitch _mainHotspotToggle;
        private TextBlock _mainHotspotSubtitle;
        private Border _heroStatusBadge;
        private TextBlock _heroStatusBadgeText;
        private TextBlock _heroPcNameText;
        private TextBlock _heroSsidText;
        private TextBlock _heroClientsCountText;
        private Win11ToggleSwitch _quickKeepAliveToggle;
        private Win11Dropdown _profileComboBox;
        private bool _suppressProfileComboEvent;
        private TextBlock _propSsidValue;
        private TextBlock _propPasswordValue;
        private TextBlock _propBandValue;
        private TextBlock _propEyeIcon;
        private TextBlock _connectedHeaderCountText;
        private StackPanel _inlineConnectedClientsPanel;
        private Win11ToggleSwitch _osPowerSavingToggle;

        // Live controls on Page 2 (Keep-Alive & Power)
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

        // Live controls on Page 3 (Connected devices)
        private StackPanel _devicesPageLivePanel;
        private StackPanel _devicesPageHistoryPanel;

        // Live controls on Page 4 (Activity & Logs)
        private TextBox _logTextBox;

        // Modal Dialog ("Edit network info")
        private Border _modalOverlay;
        private TextBox _editSsidBox;
        private TextBox _editPassVisibleBox;
        private PasswordBox _editPassHiddenBox;
        private bool _editDialogShowPass = true;
        private Win11Dropdown _editBandCombo;
        private TextBlock _editErrorText;
        private Button _editSaveBtn;

        // Toast notification banner inside window
        private Border _toastBanner;
        private TextBlock _toastText;
        private DispatcherTimer _toastTimer;

        public MainWindow(bool startMinimized, string screenshotDir = null)
        {
            _settings = AppSettings.Load();
            _controller = new HotspotController();
            _theme = Win11Theme.Create(_settings.IsDarkMode);
            _lastWatchdogCheckTime = DateTime.Now;

            InitializeFonts();
            InitializeWindowChrome();
            BuildShellUi();

            // Timers
            _watchdogTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromSeconds(Math.Max(2, _settings.WatchdogIntervalSeconds))
            };
            _watchdogTimer.Tick += async delegate { await RunWatchdogCycleAsync(false); };

            _uptimeTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromSeconds(1)
            };
            _uptimeTimer.Tick += delegate { UpdateUptimeDisplay(); };

            Loaded += async delegate
            {
                IntPtr hwnd = new WindowInteropHelper(this).Handle;
                NativeMethods.ApplyWin11WindowAttributes(hwnd, _theme.IsDark);
                if (_trayIcon == null)
                {
                    InitializeSystemTray();
                }

                AddLog("Application started. Initializing Windows 11 Mobile Hotspot controller...");
                if (_settings.ForceDisableOsTimeout)
                {
                    bool changed = _controller.EnsureNoConnectionsTimeoutDisabled();
                    if (changed)
                    {
                        AddLog("Disabled Windows idle hotspot timeout ('When no devices are connected, automatically turn off mobile hotspot').");
                    }
                }

                await RefreshHotspotStateAsync(true);

                if (_lastSnapshot.OperationalState == TetheringOperationalState.On)
                {
                    _userDesiredHotspotOn = true;
                    _hotspotActiveSince = DateTime.Now;
                    AddLog("Detected Mobile Hotspot is already ON (SSID: " + _lastSnapshot.Ssid + "). Keep-Alive Guard armed.");
                }
                else if (_settings.AutoStartHotspotOnLaunch)
                {
                    _userDesiredHotspotOn = true;
                    AddLog("Auto-start on launch is enabled. Starting Mobile Hotspot...");
                    await ToggleHotspotFromUserAsync(true);
                }

                UpdateSleepGuardState();
                _watchdogTimer.Start();
                _uptimeTimer.Start();

                if (!string.IsNullOrEmpty(screenshotDir))
                {
                    await Task.Delay(500);
                    CaptureUiPageToPng(System.IO.Path.Combine(screenshotDir, "page_hotspot_dark.png"));
                    NavigateToPage("KeepAlive");
                    await Task.Delay(250);
                    CaptureUiPageToPng(System.IO.Path.Combine(screenshotDir, "page_keepalive_dark.png"));
                    NavigateToPage("Hotspot");
                    OpenEditModal();
                    await Task.Delay(250);
                    CaptureUiPageToPng(System.IO.Path.Combine(screenshotDir, "modal_edit_dark.png"));
                    _modalOverlay.Visibility = Visibility.Collapsed;
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
                if (!_isExitingExplicitly && _settings.MinimizeToTrayOnClose)
                {
                    e.Cancel = true;
                    HideToTray(true);
                }
                else
                {
                    NativeMethods.UpdateSleepPrevention(false);
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
            Title = "Settings - Mobile hotspot";
            Width = 1060;
            Height = 740;
            MinWidth = 820;
            MinHeight = 560;
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

        #region Shell UI Construction (Windows 11 Settings Layout)
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
            _mainShellGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(42) }); // Custom Title Bar
            _mainShellGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) }); // Body

            // 1. Custom Windows 11 Settings Title Bar
            UIElement titleBar = BuildTitleBar();
            Grid.SetRow(titleBar, 0);
            _mainShellGrid.Children.Add(titleBar);

            // 2. Main Body (Left Navigation Sidebar + Right Content Area)
            Grid bodyGrid = new Grid();
            bodyGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(276) });
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

            // 3. Toast notification banner (top-right floating inside content)
            _toastBanner = BuildToastBanner();
            rootLayerGrid.Children.Add(_toastBanner);

            // 4. Modal Dialog Overlay ("Edit network info")
            _modalOverlay = BuildEditModalOverlay();
            rootLayerGrid.Children.Add(_modalOverlay);

            _outerWindowBorder.Child = rootLayerGrid;
            Content = _outerWindowBorder;

            RenderActivePage();
        }

        private UIElement BuildTitleBar()
        {
            Grid titleGrid = new Grid
            {
                Background = Brushes.Transparent
            };
            titleGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            titleGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
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

            // Left: Back button + Icon + "Settings"
            StackPanel leftPanel = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(10, 0, 0, 0)
            };

            Button backBtn = CreateChromeIconButton("\uE72B", "Back to Mobile hotspot overview", false);
            backBtn.Width = 34;
            backBtn.Height = 30;
            backBtn.Click += delegate { NavigateToPage("Hotspot"); };
            leftPanel.Children.Add(backBtn);

            Border appBadge = new Border
            {
                Width = 18,
                Height = 18,
                CornerRadius = new CornerRadius(4),
                Background = _theme.AccentBrush,
                Margin = new Thickness(8, 0, 8, 0),
                VerticalAlignment = VerticalAlignment.Center,
                Child = new TextBlock
                {
                    FontFamily = _fontIcons,
                    Text = "\uE88A",
                    FontSize = 11,
                    Foreground = _theme.AccentTextBrush,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center
                }
            };
            leftPanel.Children.Add(appBadge);

            TextBlock titleText = new TextBlock
            {
                Text = "Settings",
                FontSize = 12,
                Foreground = _theme.TextSecondary,
                VerticalAlignment = VerticalAlignment.Center
            };
            leftPanel.Children.Add(titleText);

            Grid.SetColumn(leftPanel, 0);
            titleGrid.Children.Add(leftPanel);

            // Right Status Pill (shows Keep-Alive Guard status at a glance)
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

            Grid.SetColumn(_titleBarStatusPill, 2);
            titleGrid.Children.Add(_titleBarStatusPill);

            // Window Caption Buttons (Minimize, Maximize, Close)
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

            Grid.SetColumn(captionBtns, 3);
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
            bd.Name = "ChromeBd";
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
                Padding = new Thickness(16, 6, 12, 16)
            };

            Grid sideGrid = new Grid();
            sideGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // User / PC Profile card
            sideGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // Search box
            sideGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) }); // Nav items
            sideGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // Footer (Theme + Heartbeat)

            // 1. User / Device Header (Windows 11 Settings top-left account/PC card)
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
                    Text = "\uE770",
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
                Text = Environment.MachineName,
                FontSize = 14,
                FontWeight = FontWeights.SemiBold,
                Foreground = _theme.TextPrimary,
                TextTrimming = TextTrimming.CharacterEllipsis
            });
            _sidebarProfileSubText = new TextBlock
            {
                Text = "Network & internet • Hotspot",
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

            // 2. Windows 11 Settings "Find a setting" Search Box
            Border searchBorder = new Border
            {
                Background = _theme.CardBg,
                BorderBrush = _theme.CardBorder,
                BorderThickness = new Thickness(1, 1, 1, 2),
                CornerRadius = new CornerRadius(4),
                Height = 34,
                Margin = new Thickness(0, 0, 0, 16),
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
                Text = "Find a setting",
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
                if (q.Contains("keep") || q.Contains("alive") || q.Contains("watchdog") || q.Contains("sleep") || q.Contains("timeout") || q.Contains("startup") || q.Contains("never"))
                {
                    NavigateToPage("KeepAlive");
                }
                else if (q.Contains("device") || q.Contains("client") || q.Contains("mac") || q.Contains("ip"))
                {
                    NavigateToPage("Devices");
                }
                else if (q.Contains("log") || q.Contains("history") || q.Contains("activity") || q.Contains("event"))
                {
                    NavigateToPage("Logs");
                }
                else if (q.Contains("ssid") || q.Contains("pass") || q.Contains("band") || q.Contains("hotspot") || q.Contains("share"))
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

            // 3. Navigation Items List
            StackPanel navStack = new StackPanel();
            navStack.Children.Add(CreateNavItem("Hotspot", "\uE88A", "Mobile hotspot", null));
            navStack.Children.Add(CreateNavItem("KeepAlive", "\uEA18", "Keep-Alive & Power", "ON"));
            navStack.Children.Add(CreateNavItem("Devices", "\uE839", "Connected devices", "0"));
            navStack.Children.Add(CreateNavItem("Logs", "\uE9D9", "Activity & logs", null));

            Grid.SetRow(navStack, 2);
            sideGrid.Children.Add(navStack);

            // 4. Sidebar Footer: Theme Switcher + Watchdog Heartbeat
            StackPanel footerStack = new StackPanel();

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
                Text = _theme.IsDark ? "Dark mode (Windows 11)" : "Light mode (Windows 11)",
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

            // Live Watchdog Heartbeat Bar
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
                Text = "Watchdog active • Never disconnect",
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
            Grid contentGrid = new Grid
            {
                Background = _theme.ContentBg
            };
            contentGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // Breadcrumb Header
            contentGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) }); // Scrollable Cards

            // 1. Breadcrumb Header ("Network & internet  ›  Mobile hotspot")
            Grid headerRow = new Grid
            {
                Margin = new Thickness(36, 14, 36, 18),
                MaxWidth = 900,
                HorizontalAlignment = HorizontalAlignment.Stretch
            };
            headerRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            headerRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            StackPanel breadcrumbStack = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                VerticalAlignment = VerticalAlignment.Center
            };

            _breadcrumbSectionText = new TextBlock
            {
                Text = "Network & internet",
                FontFamily = _fontDisplay,
                FontSize = 26,
                FontWeight = FontWeights.SemiBold,
                Foreground = _theme.TextMuted,
                Cursor = Cursors.Hand,
                VerticalAlignment = VerticalAlignment.Center
            };
            _breadcrumbSectionText.MouseLeftButtonUp += delegate { NavigateToPage("Hotspot"); };
            breadcrumbStack.Children.Add(_breadcrumbSectionText);

            breadcrumbStack.Children.Add(new TextBlock
            {
                FontFamily = _fontIcons,
                Text = "\uE76C",
                FontSize = 14,
                Foreground = _theme.TextMuted,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(12, 4, 12, 0)
            });

            _breadcrumbPageText = new TextBlock
            {
                Text = "Mobile hotspot",
                FontFamily = _fontDisplay,
                FontSize = 26,
                FontWeight = FontWeights.SemiBold,
                Foreground = _theme.TextPrimary,
                VerticalAlignment = VerticalAlignment.Center
            };
            breadcrumbStack.Children.Add(_breadcrumbPageText);

            Grid.SetColumn(breadcrumbStack, 0);
            headerRow.Children.Add(breadcrumbStack);

            // Right header buttons: Refresh & Open Windows Native Settings shortcut
            StackPanel headerActions = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                VerticalAlignment = VerticalAlignment.Center
            };

            Button refreshBtn = CreateWin11Button("\uE72C", "Refresh", false);
            refreshBtn.Click += async delegate
            {
                await RefreshHotspotStateAsync(false);
                ShowToast("Hotspot status refreshed.");
            };
            headerActions.Children.Add(refreshBtn);

            Grid.SetColumn(headerActions, 1);
            headerRow.Children.Add(headerActions);

            Grid.SetRow(headerRow, 0);
            contentGrid.Children.Add(headerRow);

            // 2. Scrollable Content Area
            ScrollViewer scroller = new ScrollViewer
            {
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                Padding = new Thickness(36, 0, 36, 36)
            };
            ApplyWin11ScrollBarStyle(scroller);

            _pageContainer = new StackPanel
            {
                MaxWidth = 900,
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
                _breadcrumbPageText.Text = "Mobile hotspot";
                BuildHotspotMainPage(_pageContainer);
            }
            else if (_activePage == "KeepAlive")
            {
                _breadcrumbPageText.Text = "Keep-Alive & Power";
                BuildKeepAlivePage(_pageContainer);
            }
            else if (_activePage == "Devices")
            {
                _breadcrumbPageText.Text = "Connected devices";
                BuildConnectedDevicesPage(_pageContainer);
            }
            else if (_activePage == "Logs")
            {
                _breadcrumbPageText.Text = "Activity & logs";
                BuildActivityLogsPage(_pageContainer);
            }
        }

        // PAGE 1: Windows 11 Mobile hotspot page
        private void BuildHotspotMainPage(StackPanel container)
        {
            // 1. Top Windows 11 Network Visual Summary Header Card
            Border heroBanner = CreateCardBorder(new Thickness(18, 14, 18, 14), new Thickness(0, 0, 0, 14));
            Grid heroGrid = new Grid();
            heroGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            heroGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            heroGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            heroGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            heroGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.15, GridUnitType.Star) });

            // Left Node: This PC & Internet Profile
            StackPanel node1 = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Left };
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
                Text = "Internet source: Auto",
                FontSize = 11.5,
                Foreground = _theme.TextSecondary,
                Margin = new Thickness(0, 2, 0, 0),
                TextTrimming = TextTrimming.CharacterEllipsis
            };
            node1Text.Children.Add(_heroPcNameText);
            node1.Children.Add(node1Text);
            Grid.SetColumn(node1, 0);
            heroGrid.Children.Add(node1);

            // Connector 1
            Border line1 = new Border
            {
                Width = 30,
                Height = 2,
                Background = _theme.AccentBrush,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(6, 0, 10, 0)
            };
            Grid.SetColumn(line1, 1);
            heroGrid.Children.Add(line1);

            // Center Node: Hotspot Broadcast
            StackPanel node2 = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Left };
            node2.Children.Add(CreateCircleIconBadge("\uE88A", _theme.AccentBrush));
            StackPanel node2Text = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 0, 0) };
            _heroSsidText = new TextBlock
            {
                Text = "Hotspot Network",
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

            // Connector 2
            Border line2 = new Border
            {
                Width = 30,
                Height = 2,
                Background = _theme.DividerBrush,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(6, 0, 10, 0)
            };
            Grid.SetColumn(line2, 3);
            heroGrid.Children.Add(line2);

            // Right Node: Connected Clients + Keep-Alive Guard
            StackPanel node3 = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Left };
            node3.Children.Add(CreateCircleIconBadge("\uEA18", _theme.SuccessBrush));
            StackPanel node3Text = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 0, 0) };
            node3Text.Children.Add(new TextBlock
            {
                Text = "Always-On Protection",
                FontSize = 13.5,
                FontWeight = FontWeights.SemiBold,
                Foreground = _theme.TextPrimary
            });
            _heroClientsCountText = new TextBlock
            {
                Text = "0 connected • Idle timeout disabled",
                FontSize = 11.5,
                Foreground = _theme.TextSecondary,
                Margin = new Thickness(0, 2, 0, 0),
                TextTrimming = TextTrimming.CharacterEllipsis
            };
            node3Text.Children.Add(_heroClientsCountText);
            node3.Children.Add(node3Text);
            Grid.SetColumn(node3, 4);
            heroGrid.Children.Add(node3);

            heroBanner.Child = heroGrid;
            container.Children.Add(heroBanner);

            // 2. Primary Mobile hotspot Toggle Card
            _mainHotspotToggle = new Win11ToggleSwitch(_theme, true);
            _mainHotspotToggle.Toggled += async delegate(object s, bool isOn)
            {
                await ToggleHotspotFromUserAsync(isOn);
            };

            Border mainToggleCard = CreateSettingRowCard(
                "\uE88A",
                "Mobile hotspot",
                "Share your internet connection with other devices over Wi-Fi",
                _mainHotspotToggle,
                out _mainHotspotSubtitle);
            container.Children.Add(mainToggleCard);

            // 3. Never Disconnect When Idle (Keep-Alive Guard) Card right on main page
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
                AddLog("Keep-Alive Guard (never disconnect when 0 devices connected) set to: " + (isOn ? "ON" : "OFF"));
                ShowToast(isOn
                    ? "Keep-Alive Guard enabled: Hotspot will never disconnect when idle."
                    : "Keep-Alive Guard paused.");
                UpdateUiFromSnapshot(_lastSnapshot);
            };

            TextBlock keepAliveSub;
            Border keepAliveCard = CreateSettingRowCard(
                "\uEA18",
                "Never disconnect when no one is connected",
                "Disables Windows idle timeout and automatically restarts hotspot if it ever drops with 0 devices",
                _quickKeepAliveToggle,
                out keepAliveSub);
            container.Children.Add(keepAliveCard);

            // 4. Share my internet connection from
            _profileComboBox = CreateStyledComboBox(230);
            _profileComboBox.SelectionChanged += async delegate
            {
                if (_suppressProfileComboEvent || _profileComboBox.SelectedItem == null) return;
                string sel = _profileComboBox.SelectedItem;
                _settings.PreferredProfileName = sel.StartsWith("Automatic") ? "Auto" : sel;
                _settings.Save();
                AddLog("Preferred internet connection profile set to: " + _settings.PreferredProfileName);
                await RefreshHotspotStateAsync(false);
            };

            TextBlock shareFromSub;
            Border shareFromCard = CreateSettingRowCard(
                "\uE774",
                "Share my internet connection from",
                "Choose which network adapter provides connectivity for your Wi-Fi hotspot",
                _profileComboBox,
                out shareFromSub);
            container.Children.Add(shareFromCard);

            // 5. Share over (Wi-Fi)
            Win11Dropdown shareOverCombo = CreateStyledComboBox(140);
            shareOverCombo.AddItem("Wi-Fi");
            shareOverCombo.SelectedIndex = 0;
            TextBlock shareOverSub;
            Border shareOverCard = CreateSettingRowCard(
                "\uE701",
                "Share over",
                "Broadcast hotspot network over your wireless Wi-Fi adapter",
                shareOverCombo,
                out shareOverSub);
            container.Children.Add(shareOverCard);

            // 6. Properties Card (Windows 11 Settings Expander-style detailed card)
            Border propsCard = CreateCardBorder(new Thickness(0), new Thickness(0, 0, 0, 6));
            StackPanel propsStack = new StackPanel();

            // Header of Properties card
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
                Text = "Properties",
                FontSize = 14,
                FontWeight = FontWeights.SemiBold,
                Foreground = _theme.TextPrimary
            });
            propsTitleStack.Children.Add(new TextBlock
            {
                Text = "Network name (SSID), password, and frequency band",
                FontSize = 12,
                Foreground = _theme.TextSecondary,
                Margin = new Thickness(0, 2, 0, 0)
            });
            Grid.SetColumn(propsTitleStack, 1);
            propsHeader.Children.Add(propsTitleStack);

            TextBlock chevronIcon = new TextBlock
            {
                FontFamily = _fontIcons,
                Text = "\uE70E",
                FontSize = 12,
                Foreground = _theme.TextSecondary,
                VerticalAlignment = VerticalAlignment.Center
            };
            Grid.SetColumn(chevronIcon, 2);
            propsHeader.Children.Add(chevronIcon);
            propsStack.Children.Add(propsHeader);

            propsStack.Children.Add(CreateHorizontalDivider());

            // Properties Rows Body
            StackPanel propsBody = new StackPanel { Margin = new Thickness(54, 14, 20, 16) };

            // Row 1: Network name
            Grid ssidRow = CreatePropertyDetailRow("Network name:", out _propSsidValue);
            Button copySsidBtn = CreateWin11Button("\uE8C8", "Copy", false);
            copySsidBtn.Margin = new Thickness(8, 0, 0, 0);
            copySsidBtn.Click += delegate
            {
                CopyToClipboard(_lastSnapshot.Ssid, "Network name (SSID) copied to clipboard.");
            };
            Grid.SetColumn(copySsidBtn, 2);
            ssidRow.Children.Add(copySsidBtn);
            propsBody.Children.Add(ssidRow);

            // Row 2: Network password
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

            // Row 3: Network band
            Grid bandRow = CreatePropertyDetailRow("Network band:", out _propBandValue);
            propsBody.Children.Add(bandRow);

            // Action Buttons row (Edit + Copy Credentials)
            StackPanel editBtnRow = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Margin = new Thickness(0, 12, 0, 0)
            };

            Button editPropsBtn = CreateWin11Button("\uE70F", "Edit", true);
            editPropsBtn.MinWidth = 96;
            editPropsBtn.Click += delegate { OpenEditModal(); };
            editBtnRow.Children.Add(editPropsBtn);

            Button copyAllBtn = CreateWin11Button("\uE8C8", "Copy credentials", false);
            copyAllBtn.Margin = new Thickness(10, 0, 0, 0);
            copyAllBtn.Click += delegate
            {
                string summary = "Wi-Fi Network (SSID): " + _lastSnapshot.Ssid + Environment.NewLine +
                                 "Password: " + _lastSnapshot.Passphrase + Environment.NewLine +
                                 "Band: " + HotspotController.FormatBand(_lastSnapshot.Band);
                CopyToClipboard(summary, "Hotspot SSID & password copied to clipboard.");
            };
            editBtnRow.Children.Add(copyAllBtn);

            propsBody.Children.Add(editBtnRow);
            propsStack.Children.Add(propsBody);
            propsCard.Child = propsStack;
            container.Children.Add(propsCard);

            // 7. Connected Devices Card (on main page)
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
                Text = "Devices currently using your Wi-Fi hotspot",
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
                Foreground = _theme.TextSecondary,
                VerticalAlignment = VerticalAlignment.Center
            };
            Grid.SetColumn(_connectedHeaderCountText, 2);
            connHeader.Children.Add(_connectedHeaderCountText);
            connStack.Children.Add(connHeader);

            connStack.Children.Add(CreateHorizontalDivider());

            _inlineConnectedClientsPanel = new StackPanel
            {
                Margin = new Thickness(54, 12, 20, 14)
            };
            connStack.Children.Add(_inlineConnectedClientsPanel);
            connectedCard.Child = connStack;
            container.Children.Add(connectedCard);

            // 8. Power saving (Windows 11 OS Native Setting)
            container.Children.Add(CreateSectionHeader("Power saving"));

            _osPowerSavingToggle = new Win11ToggleSwitch(_theme, true);
            _osPowerSavingToggle.Toggled += delegate(object s, bool turnOffWhenIdle)
            {
                if (turnOffWhenIdle && _settings.KeepAliveEnabled)
                {
                    _settings.KeepAliveEnabled = false;
                    _settings.ForceDisableOsTimeout = false;
                    _settings.Save();
                    AddLog("User enabled Windows idle power-saving timeout; Keep-Alive Guard paused.");
                }
                else if (!turnOffWhenIdle)
                {
                    _settings.ForceDisableOsTimeout = true;
                    _settings.Save();
                }

                _controller.SetNoConnectionsTimeout(turnOffWhenIdle);
                UpdateUiFromSnapshot(_controller.GetSnapshot(_settings.PreferredProfileName));
            };

            TextBlock powerSub;
            Border powerSavingCard = CreateSettingRowCard(
                "\uE7E8",
                "When no devices are connected, automatically turn off mobile hotspot",
                "Keep this Off so Windows never shuts down your hotspot when 0 devices are connected",
                _osPowerSavingToggle,
                out powerSub);
            container.Children.Add(powerSavingCard);
        }

        // PAGE 2: Keep-Alive & Power (Dedicated Never-Disconnect Engine)
        private void BuildKeepAlivePage(StackPanel container)
        {
            // Top 3 Stat Cards
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

            // 1. Active Keep-Alive Watchdog
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

            // 2. Watchdog Check Interval
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

            // 3. Force Disable Windows Idle Timeout
            _forceDisableTimeoutToggle = new Win11ToggleSwitch(_theme, true);
            _forceDisableTimeoutToggle.SetCheckedSilent(_settings.ForceDisableOsTimeout);
            _forceDisableTimeoutToggle.Toggled += delegate(object s, bool isOn)
            {
                _settings.ForceDisableOsTimeout = isOn;
                _settings.Save();
                if (isOn) _controller.EnsureNoConnectionsTimeoutDisabled();
                AddLog("Force disable Windows OS idle timeout set to: " + (isOn ? "ON" : "OFF"));
                UpdateUiFromSnapshot(_controller.GetSnapshot(_settings.PreferredProfileName));
            };
            TextBlock sub3;
            container.Children.Add(CreateSettingRowCard(
                "\uE72E",
                "Lock Windows 'No Connections Timeout' to OFF",
                "Enforces NetworkOperatorTetheringManager.DisableNoConnectionsTimeout() on every check so Windows updates or reboots cannot re-enable idle shutoff",
                _forceDisableTimeoutToggle,
                out sub3));

            // 4. Prevent System Sleep While Hotspot is Active
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

            // 5. Auto-Start Hotspot on App Launch
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
                "Automatically turn on hotspot when app opens",
                "Starts broadcasting your Wi-Fi hotspot immediately when you launch this app",
                _autoStartOnLaunchToggle,
                out sub5));

            // 6. Launch with Windows Startup
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
                "Launches quietly in the system tray at Windows startup",
                _runAtStartupToggle,
                out sub6));

            // 7. Minimize to System Tray on Close
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
                "Clicking the X button hides the window to the system tray so the Keep-Alive Watchdog stays active 24/7",
                _minimizeToTrayToggle,
                out sub7));
        }

        // PAGE 3: Connected devices
        private void BuildConnectedDevicesPage(StackPanel container)
        {
            container.Children.Add(CreateSectionHeader("Currently connected devices"));
            _devicesPageLivePanel = new StackPanel { Margin = new Thickness(0, 0, 0, 18) };
            container.Children.Add(_devicesPageLivePanel);

            container.Children.Add(CreateSectionHeader("Session device history"));
            _devicesPageHistoryPanel = new StackPanel();
            container.Children.Add(_devicesPageHistoryPanel);
        }

        // PAGE 4: Activity & logs
        private void BuildActivityLogsPage(StackPanel container)
        {
            Border actionsCard = CreateCardBorder(new Thickness(18, 12, 18, 12), new Thickness(0, 0, 0, 10));
            Grid actGrid = new Grid();
            actGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            actGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            StackPanel descStack = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            descStack.Children.Add(new TextBlock
            {
                Text = "Watchdog & Hotspot Event Log",
                FontSize = 14,
                FontWeight = FontWeights.SemiBold,
                Foreground = _theme.TextPrimary
            });
            descStack.Children.Add(new TextBlock
            {
                Text = "Live record of hotspot state checks, idle timeout locks, and automatic reconnections",
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

        #region Reusable Windows 11 Card Builders
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
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(165) });
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

        private Button CreateWin11Button(string iconGlyph, string text, bool isAccent)
        {
            Brush normalBg = isAccent ? _theme.AccentBrush : _theme.SubtleButtonBg;
            Brush hoverBg = isAccent ? _theme.AccentHoverBrush : _theme.SubtleButtonHoverBg;
            Brush borderBr = isAccent ? _theme.AccentBrush : _theme.SubtleButtonBorder;
            Brush fg = isAccent ? _theme.AccentTextBrush : _theme.TextPrimary;

            Button btn = new Button
            {
                Background = normalBg,
                BorderBrush = borderBr,
                BorderThickness = new Thickness(1),
                Foreground = fg,
                Padding = new Thickness(14, 6, 14, 6),
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
                    FontSize = 12.5,
                    Foreground = fg,
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(0, 0, string.IsNullOrEmpty(text) ? 0 : 7, 0)
                });
            }
            if (!string.IsNullOrEmpty(text))
            {
                sp.Children.Add(new TextBlock
                {
                    Text = text,
                    FontSize = 13,
                    FontWeight = isAccent ? FontWeights.SemiBold : FontWeights.Normal,
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
        #endregion

        #region Modal Dialog ("Edit network info") & Toast Banner
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
                Width = 420,
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
                Text = "Edit network info",
                FontFamily = _fontDisplay,
                FontSize = 20,
                FontWeight = FontWeights.SemiBold,
                Foreground = _theme.TextPrimary,
                Margin = new Thickness(0, 0, 0, 16)
            });

            // Network name
            stack.Children.Add(new TextBlock
            {
                Text = "Network name",
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

            // Network password
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
            eyeBtn.ToolTip = "Show / hide password";
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

            // Network band
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
                Margin = new Thickness(0, 0, 0, 12)
            };
            _editBandCombo.AddItem("Any available");
            _editBandCombo.AddItem("5 GHz");
            _editBandCombo.AddItem("2.4 GHz");
            _editBandCombo.SelectedIndex = 0;
            stack.Children.Add(_editBandCombo);

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

            // Buttons row
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
            cancelBtn.Click += delegate { _modalOverlay.Visibility = Visibility.Collapsed; };
            Grid.SetColumn(cancelBtn, 2);
            btnGrid.Children.Add(cancelBtn);

            stack.Children.Add(btnGrid);
            dialog.Child = stack;
            overlay.Child = dialog;
            return overlay;
        }

        private void OpenEditModal()
        {
            _editSsidBox.Text = _lastSnapshot.Ssid;
            _editDialogShowPass = true;
            _editPassVisibleBox.Text = _lastSnapshot.Passphrase;
            _editPassHiddenBox.Password = _lastSnapshot.Passphrase;
            _editPassVisibleBox.Visibility = Visibility.Visible;
            _editPassHiddenBox.Visibility = Visibility.Collapsed;

            if (_lastSnapshot.Band == TetheringWiFiBand.FiveGigahertz) _editBandCombo.SelectedIndex = 1;
            else if (_lastSnapshot.Band == TetheringWiFiBand.TwoPointFourGigahertz) _editBandCombo.SelectedIndex = 2;
            else _editBandCombo.SelectedIndex = 0;

            _editErrorText.Visibility = Visibility.Collapsed;
            _modalOverlay.Visibility = Visibility.Visible;
            _editSsidBox.Focus();
        }

        private async Task SaveEditedNetworkInfoAsync()
        {
            string newSsid = (_editSsidBox.Text ?? "").Trim();
            string newPass = _editDialogShowPass ? (_editPassVisibleBox.Text ?? "") : (_editPassHiddenBox.Password ?? "");
            TetheringWiFiBand newBand = TetheringWiFiBand.Auto;
            if (_editBandCombo.SelectedIndex == 1) newBand = TetheringWiFiBand.FiveGigahertz;
            else if (_editBandCombo.SelectedIndex == 2) newBand = TetheringWiFiBand.TwoPointFourGigahertz;

            if (string.IsNullOrEmpty(newSsid))
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

            _editSaveBtn.IsEnabled = false;
            _editErrorText.Visibility = Visibility.Collapsed;

            Tuple<bool, string> res = await _controller.ConfigureAccessPointAsync(
                _settings.PreferredProfileName,
                newSsid,
                newPass,
                newBand);

            _editSaveBtn.IsEnabled = true;

            if (!res.Item1)
            {
                _editErrorText.Text = res.Item2;
                _editErrorText.Visibility = Visibility.Visible;
                AddLog(res.Item2);
                return;
            }

            _modalOverlay.Visibility = Visibility.Collapsed;
            AddLog(res.Item2);
            ShowToast("Saved network info (" + newSsid + ").");
            await RefreshHotspotStateAsync(false);
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
                    _mainHotspotSubtitle.Text = turnOn ? "Starting mobile hotspot..." : "Stopping mobile hotspot...";
                }

                if (turnOn)
                {
                    Tuple<bool, string> res = await _controller.StartHotspotAsync(
                        _settings.PreferredProfileName,
                        _settings.ForceDisableOsTimeout || _settings.KeepAliveEnabled);
                    AddLog(res.Item2);
                    if (res.Item1)
                    {
                        _hotspotActiveSince = DateTime.Now;
                        ShowToast("Mobile hotspot is now ON • Keep-Alive Guard active.");
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
                    ShowToast("Mobile hotspot turned OFF.");
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

            // 1. Always enforce Windows NoConnectionsTimeout == Disabled if configured
            if (_settings.ForceDisableOsTimeout || _settings.KeepAliveEnabled)
            {
                bool fixedTimeout = await Task.Run(() => _controller.EnsureNoConnectionsTimeoutDisabled());
                if (fixedTimeout)
                {
                    AddLog("Watchdog locked Windows idle hotspot timeout back to OFF.");
                }
            }

            // 2. Fetch live snapshot
            HotspotSnapshot snap = await Task.Run(() => _controller.GetSnapshot(_settings.PreferredProfileName));
            _lastSnapshot = snap;

            // 3. Track if user turned hotspot on from external Windows Settings
            if (snap.OperationalState == TetheringOperationalState.On && !_userDesiredHotspotOn)
            {
                _userDesiredHotspotOn = true;
                if (_hotspotActiveSince == null) _hotspotActiveSince = DateTime.Now;
                AddLog("Hotspot detected ON externally. Keep-Alive Guard is now protecting this session.");
            }

            // 4. Core Never-Disconnect Watchdog Recovery:
            // If Keep-Alive is enabled AND the hotspot should be ON, but Windows / Wi-Fi driver turned it OFF (e.g. 0 clients connected):
            if (_settings.KeepAliveEnabled &&
                _userDesiredHotspotOn &&
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

            if (manualTrigger)
            {
                AddLog("Manual watchdog check completed: State=" + snap.OperationalState + ", Clients=" + snap.ClientCount + ", IdleTimeout=" + (snap.IsOsTimeoutEnabled ? "Enabled" : "Disabled"));
            }

            UpdateSessionClients(snap.Clients);
            UpdateSleepGuardState();
            UpdateUiFromSnapshot(snap);
        }

        private async Task RefreshHotspotStateAsync(bool initial)
        {
            HotspotSnapshot snap = await Task.Run(() => _controller.GetSnapshot(_settings.PreferredProfileName));
            _lastSnapshot = snap;
            UpdateSessionClients(snap.Clients);
            UpdateUiFromSnapshot(snap);
        }

        private void UpdateSessionClients(List<ConnectedClientInfo> currentClients)
        {
            foreach (ConnectedClientInfo existing in _sessionClients.Values)
            {
                existing.IsCurrentlyConnected = false;
            }

            if (currentClients == null) return;
            foreach (ConnectedClientInfo c in currentClients)
            {
                ConnectedClientInfo tracked;
                if (_sessionClients.TryGetValue(c.MacAddress, out tracked))
                {
                    tracked.HostName = c.HostName;
                    tracked.IpAddress = c.IpAddress;
                    tracked.LastSeen = DateTime.Now;
                    tracked.IsCurrentlyConnected = true;
                }
                else
                {
                    _sessionClients[c.MacAddress] = c;
                    AddLog("Device connected: " + c.HostName + " (" + c.IpAddress + " / " + c.MacAddress + ")");
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

            // Title bar status pill & sidebar heartbeat
            if (_titleBarStatusDot != null && _titleBarStatusText != null)
            {
                if (_settings.KeepAliveEnabled)
                {
                    _titleBarStatusDot.Fill = _theme.SuccessBrush;
                    _titleBarStatusText.Text = isOn
                        ? ("Hotspot ON • Never-Disconnect Guard Active (" + snap.ClientCount + " connected)")
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
                    ? (snap.ProfileType + " (" + snap.ProfileName + ")")
                    : "No upstream connection";
            }

            if (_sidebarHeartbeatDot != null && _sidebarHeartbeatText != null)
            {
                _sidebarHeartbeatDot.Fill = _settings.KeepAliveEnabled ? _theme.SuccessBrush : _theme.WarningBrush;
                _sidebarHeartbeatText.Text = _settings.KeepAliveEnabled
                    ? ("Guard active • Check #" + _watchdogCheckCount + " (" + _lastWatchdogCheckTime.ToString("HH:mm:ss") + ")")
                    : "Guard paused • Click Keep-Alive to enable";
            }

            // Nav badges
            if (_navBadges.ContainsKey("KeepAlive"))
            {
                _navBadges["KeepAlive"].Text = _settings.KeepAliveEnabled ? "ON" : "OFF";
            }
            if (_navBadges.ContainsKey("Devices"))
            {
                _navBadges["Devices"].Text = snap.ClientCount.ToString();
            }

            // Tray icon update
            UpdateTrayStatus(snap);

            // Page 1: Mobile hotspot controls
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
                        _mainHotspotSubtitle.Text = "Broadcasting '" + snap.Ssid + "' over " + HotspotController.FormatBand(snap.Band) +
                            " • Keep-Alive Guard is keeping this network open even with 0 devices";
                    }
                    else if (inTransition)
                    {
                        _mainHotspotSubtitle.Text = "Mobile hotspot is transitioning state...";
                    }
                    else
                    {
                        _mainHotspotSubtitle.Text = "Share your internet connection with other devices over Wi-Fi";
                    }
                }

                if (_heroPcNameText != null)
                {
                    _heroPcNameText.Text = snap.IsAvailable ? ("Sharing: " + snap.ProfileName) : "No internet source";
                }
                if (_heroSsidText != null)
                {
                    _heroSsidText.Text = string.IsNullOrEmpty(snap.Ssid) ? "Mobile Hotspot" : snap.Ssid;
                }
                if (_heroStatusBadge != null && _heroStatusBadgeText != null)
                {
                    _heroStatusBadge.Background = isOn ? _theme.SuccessBgBrush : _theme.CardSecondaryBg;
                    _heroStatusBadgeText.Foreground = isOn ? _theme.SuccessBrush : _theme.TextSecondary;
                    _heroStatusBadgeText.Text = isOn
                        ? ("Active • " + HotspotController.FormatBand(snap.Band))
                        : (inTransition ? "Starting..." : "Off");
                }
                if (_heroClientsCountText != null)
                {
                    _heroClientsCountText.Text = snap.ClientCount + "/" + snap.MaxClientCount + " devices • " +
                        (_settings.KeepAliveEnabled ? "Guard ON" : "Standard");
                }

                if (_quickKeepAliveToggle != null)
                {
                    _quickKeepAliveToggle.SetCheckedSilent(_settings.KeepAliveEnabled);
                }

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

                if (_propSsidValue != null) _propSsidValue.Text = string.IsNullOrEmpty(snap.Ssid) ? "-" : snap.Ssid;
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
                if (_propEyeIcon != null)
                {
                    _propEyeIcon.Text = _isPasswordRevealed ? "\uED1A" : "\uE7B3";
                }
                if (_propBandValue != null) _propBandValue.Text = HotspotController.FormatBand(snap.Band);

                if (_connectedHeaderCountText != null)
                {
                    _connectedHeaderCountText.Text = snap.ClientCount + " of " + snap.MaxClientCount;
                }

                if (_inlineConnectedClientsPanel != null)
                {
                    PopulateClientsStack(_inlineConnectedClientsPanel, snap.Clients, isOn);
                }

                if (_osPowerSavingToggle != null)
                {
                    _osPowerSavingToggle.SetCheckedSilent(snap.IsOsTimeoutEnabled);
                }
            }
            else if (_activePage == "KeepAlive")
            {
                UpdateUptimeDisplay();
                if (_statChecksValue != null)
                {
                    _statChecksValue.Text = _watchdogCheckCount + " checks";
                }
                if (_statRecoveriesValue != null)
                {
                    _statRecoveriesValue.Text = _autoRecoveryCount + " recoveries";
                }
                if (_keepAliveMasterToggle != null)
                {
                    _keepAliveMasterToggle.SetCheckedSilent(_settings.KeepAliveEnabled);
                }
                if (_forceDisableTimeoutToggle != null)
                {
                    _forceDisableTimeoutToggle.SetCheckedSilent(_settings.ForceDisableOsTimeout);
                }
            }
            else if (_activePage == "Devices")
            {
                if (_devicesPageLivePanel != null)
                {
                    PopulateClientsStack(_devicesPageLivePanel, snap.Clients, isOn);
                }
                if (_devicesPageHistoryPanel != null)
                {
                    PopulateHistoryClientsStack(_devicesPageHistoryPanel);
                }
            }
        }

        private void PopulateClientsStack(StackPanel panel, List<ConnectedClientInfo> clients, bool hotspotIsOn)
        {
            panel.Children.Clear();
            if (clients == null || clients.Count == 0)
            {
                TextBlock emptyTb = new TextBlock
                {
                    Text = hotspotIsOn
                        ? "No devices are currently connected. Keep-Alive Guard is actively keeping your Wi-Fi hotspot online so devices can join at any time."
                        : "No devices connected.",
                    FontSize = 12.5,
                    Foreground = _theme.TextSecondary,
                    TextWrapping = TextWrapping.Wrap
                };
                panel.Children.Add(emptyTb);
                return;
            }

            foreach (ConnectedClientInfo client in clients)
            {
                ConnectedClientInfo captured = client;
                Border rowCard = new Border
                {
                    Background = _theme.CardSecondaryBg,
                    BorderBrush = _theme.CardBorder,
                    BorderThickness = new Thickness(1),
                    CornerRadius = new CornerRadius(6),
                    Padding = new Thickness(12, 9, 12, 9),
                    Margin = new Thickness(0, 0, 0, 6)
                };

                Grid g = new Grid();
                g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(32) });
                g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

                g.Children.Add(new TextBlock
                {
                    FontFamily = _fontIcons,
                    Text = "\uE8EA",
                    FontSize = 16,
                    Foreground = _theme.AccentBrush,
                    VerticalAlignment = VerticalAlignment.Center
                });

                StackPanel infoStack = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
                infoStack.Children.Add(new TextBlock
                {
                    Text = captured.HostName,
                    FontSize = 13,
                    FontWeight = FontWeights.SemiBold,
                    Foreground = _theme.TextPrimary
                });
                infoStack.Children.Add(new TextBlock
                {
                    Text = "IP: " + captured.IpAddress + "   •   MAC: " + captured.MacAddress,
                    FontSize = 11.5,
                    Foreground = _theme.TextSecondary,
                    Margin = new Thickness(0, 2, 0, 0)
                });
                Grid.SetColumn(infoStack, 1);
                g.Children.Add(infoStack);

                Button copyClientBtn = CreateWin11Button("\uE8C8", "Copy", false);
                copyClientBtn.Click += delegate
                {
                    CopyToClipboard(captured.HostName + " | " + captured.IpAddress + " | " + captured.MacAddress,
                        "Device info copied to clipboard.");
                };
                Grid.SetColumn(copyClientBtn, 2);
                g.Children.Add(copyClientBtn);

                rowCard.Child = g;
                panel.Children.Add(rowCard);
            }
        }

        private void PopulateHistoryClientsStack(StackPanel panel)
        {
            panel.Children.Clear();
            if (_sessionClients.Count == 0)
            {
                Border emptyCard = CreateCardBorder(new Thickness(18, 14, 18, 14), new Thickness(0));
                emptyCard.Child = new TextBlock
                {
                    Text = "No devices have connected during this session yet.",
                    FontSize = 12.5,
                    Foreground = _theme.TextSecondary
                };
                panel.Children.Add(emptyCard);
                return;
            }

            foreach (ConnectedClientInfo client in _sessionClients.Values.OrderByDescending(c => c.LastSeen))
            {
                Border card = CreateCardBorder(new Thickness(16, 12, 16, 12), new Thickness(0, 0, 0, 6));
                Grid g = new Grid();
                g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(36) });
                g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

                g.Children.Add(new TextBlock
                {
                    FontFamily = _fontIcons,
                    Text = "\uE8EA",
                    FontSize = 17,
                    Foreground = client.IsCurrentlyConnected ? _theme.SuccessBrush : _theme.TextMuted,
                    VerticalAlignment = VerticalAlignment.Center
                });

                StackPanel st = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
                st.Children.Add(new TextBlock
                {
                    Text = client.HostName + (client.IsCurrentlyConnected ? " (Online)" : " (Disconnected)"),
                    FontSize = 13.5,
                    FontWeight = FontWeights.SemiBold,
                    Foreground = _theme.TextPrimary
                });
                st.Children.Add(new TextBlock
                {
                    Text = "IP: " + client.IpAddress + "  •  MAC: " + client.MacAddress + "  •  Last seen: " + client.LastSeen.ToString("HH:mm:ss"),
                    FontSize = 12,
                    Foreground = _theme.TextSecondary,
                    Margin = new Thickness(0, 2, 0, 0)
                });
                Grid.SetColumn(st, 1);
                g.Children.Add(st);

                card.Child = g;
                panel.Children.Add(card);
            }
        }
        #endregion

        #region System Tray Integration & Helpers
        private void InitializeSystemTray()
        {
            _trayIcon = new System.Windows.Forms.NotifyIcon
            {
                Text = "Windows 11 Hotspot Keep-Alive",
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

            System.Windows.Forms.ToolStripMenuItem openItem = new System.Windows.Forms.ToolStripMenuItem("Open Hotspot Settings");
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

            System.Windows.Forms.ToolStripMenuItem exitItem = new System.Windows.Forms.ToolStripMenuItem("Exit Completely");
            exitItem.Click += delegate
            {
                _isExitingExplicitly = true;
                Close();
            };
            menu.Items.Add(exitItem);

            _trayIcon.ContextMenuStrip = menu;
            _trayIcon.DoubleClick += delegate { RestoreFromTray(); };
        }

        private void UpdateTrayStatus(HotspotSnapshot snap)
        {
            if (_trayIcon == null) return;
            bool isOn = (snap.OperationalState == TetheringOperationalState.On);
            string tip = isOn
                ? string.Format("Hotspot ON ({0}) • {1} clients • Keep-Alive {2}",
                    snap.Ssid, snap.ClientCount, _settings.KeepAliveEnabled ? "ON" : "OFF")
                : string.Format("Hotspot OFF • Keep-Alive {0}", _settings.KeepAliveEnabled ? "Armed" : "OFF");

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
                    "Hotspot Keep-Alive Running",
                    "Keep-Alive Guard continues protecting your Wi-Fi hotspot in the system tray.",
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
            bool createdNew;
            _mutex = new Mutex(true, "Local\\Win11HotspotKeepAlive_SingleInstanceMutex", out createdNew);
            if (!createdNew)
            {
                IntPtr existingHwnd = NativeMethods.FindWindow(IntPtr.Zero, "Settings - Mobile hotspot");
                if (existingHwnd != IntPtr.Zero)
                {
                    NativeMethods.ShowWindow(existingHwnd, NativeMethods.SW_RESTORE);
                    NativeMethods.SetForegroundWindow(existingHwnd);
                }
                return;
            }

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
