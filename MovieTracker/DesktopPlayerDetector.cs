using System.Diagnostics;
using System.IO;
using System.Management;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;

namespace MovieTracker;

public static class DesktopPlayerDetector
{
    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);
    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    private static readonly (string ProcessName, string Suffix, string DisplayName)[] SupportedPlayers =
    [
        ("vlc", " - VLC media player", "VLC Media Player"),
        ("stremio-shell-ng", " - Stremio", "Stremio"),
        ("stremio", " - Stremio", "Stremio"),
        ("stremio-runtime", " - Stremio", "Stremio"),
        ("mpc-hc", " - MPC-HC", "MPC-HC"),
        ("mpc-hc64", " - MPC-HC", "MPC-HC"),
        ("mpc-be", " - MPC-BE", "MPC-BE"),
        ("mpc-be64", " - MPC-BE", "MPC-BE"),
        ("mpv", " - mpv", "MPV"),
        ("potplayermini64", " - PotPlayer", "PotPlayer"),
        ("potplayermini", " - PotPlayer", "PotPlayer"),
        ("potplayer", " - PotPlayer", "PotPlayer"),
        ("kmplayer", " - KMPlayer", "KMPlayer"),
        ("wmplayer", " - Windows Media Player", "Windows Media Player")
    ];

    public static MediaPlaybackEvent? DetectActivePlayer()
    {
        foreach (var (procName, suffix, displayName) in SupportedPlayers)
        {
            var procs = Process.GetProcessesByName(procName);
            if (procs.Length == 0) continue;

            foreach (var proc in procs)
            {
                try
                {
                    // 1. Try finding visible window and title
                    var (hWnd, rawFromWindow) = GetWindowInfoForProcess(proc.Id, suffix);

                    if (!string.IsNullOrWhiteSpace(rawFromWindow) && !IsGenericPlayerTitle(rawFromWindow))
                    {
                        var cleaned = MovieTitleCleaner.Clean(rawFromWindow);
                        return new MediaPlaybackEvent(
                            AppName: displayName,
                            AppId: procName,
                            RawTitle: rawFromWindow,
                            CleanTitle: cleaned.Title,
                            Year: cleaned.Year,
                            Duration: TimeSpan.Zero,
                            Season: cleaned.Season,
                            Episode: cleaned.Episode,
                            IsSeries: cleaned.IsSeries,
                            PlayerHwnd: hWnd,
                            IsManualTrigger: true
                        );
                    }

                    // 2. Fallback: Parse process command line via WMI (common for VLC / MPV / MPC-HC)
                    string? fileFromCmd = GetFileFromCommandLine(proc.Id);
                    if (!string.IsNullOrWhiteSpace(fileFromCmd))
                    {
                        string fileName = Path.GetFileNameWithoutExtension(fileFromCmd);
                        if (!string.IsNullOrWhiteSpace(fileName))
                        {
                            var cleaned = MovieTitleCleaner.Clean(fileName);
                            return new MediaPlaybackEvent(
                                AppName: displayName,
                                AppId: procName,
                                RawTitle: fileName,
                                CleanTitle: cleaned.Title,
                                Year: cleaned.Year,
                                Duration: TimeSpan.Zero,
                                Season: cleaned.Season,
                                Episode: cleaned.Episode,
                                IsSeries: cleaned.IsSeries,
                                PlayerHwnd: hWnd != IntPtr.Zero ? hWnd : proc.MainWindowHandle,
                                IsManualTrigger: true
                            );
                        }
                    }
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[DesktopPlayerDetector] Error inspecting {procName}: {ex.Message}");
                }
            }
        }

        return null;
    }

    private static bool IsGenericPlayerTitle(string title)
    {
        string t = title.Trim().ToLowerInvariant();
        if (t == "vlc" || t == "vlc media player") return true;
        if (t == "stremio" || t.StartsWith("stremio -") || t == "freedom to stream") return true;
        if (t == "mpv") return true;
        if (t == "mpc-hc" || t == "mpc-be") return true;
        if (t == "potplayer") return true;
        return false;
    }

    private static (IntPtr Hwnd, string? Title) GetWindowInfoForProcess(int targetPid, string suffixToStrip)
    {
        IntPtr bestHwnd = IntPtr.Zero;
        string? bestTitle = null;

        EnumWindows((hWnd, lParam) =>
        {
            GetWindowThreadProcessId(hWnd, out uint pid);
            if (pid == targetPid && IsWindowVisible(hWnd))
            {
                var sb = new StringBuilder(1024);
                int length = GetWindowText(hWnd, sb, 1024);
                if (length > 0)
                {
                    string title = sb.ToString().Trim();
                    if (!string.IsNullOrEmpty(title))
                    {
                        if (!string.IsNullOrEmpty(suffixToStrip) && title.EndsWith(suffixToStrip, StringComparison.OrdinalIgnoreCase))
                        {
                            title = title[..^suffixToStrip.Length].Trim();
                        }

                        if (!IsGenericPlayerTitle(title))
                        {
                            bestHwnd = hWnd;
                            bestTitle = title;
                            return false; // Found ideal window with actual media title
                        }
                        else if (bestHwnd == IntPtr.Zero)
                        {
                            bestHwnd = hWnd;
                            bestTitle = title;
                        }
                    }
                }
            }
            return true;
        }, IntPtr.Zero);

        return (bestHwnd, bestTitle);
    }

    private static string? GetFileFromCommandLine(int pid)
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(
                $"SELECT CommandLine FROM Win32_Process WHERE ProcessId = {pid}");
            foreach (ManagementObject obj in searcher.Get())
            {
                string? cmd = obj["CommandLine"]?.ToString();
                if (string.IsNullOrWhiteSpace(cmd)) continue;

                var match = Regex.Match(cmd, @"(?:""([^""]+\.(?:mkv|mp4|avi|mov|wmv|flv|webm|m4v|iso))""|([^\s""]+\.(?:mkv|mp4|avi|mov|wmv|flv|webm|m4v|iso)))", RegexOptions.IgnoreCase);
                if (match.Success)
                {
                    string path = !string.IsNullOrEmpty(match.Groups[1].Value) ? match.Groups[1].Value : match.Groups[2].Value;
                    return path;
                }

                var startedFromFileMatch = Regex.Match(cmd, @"--started-from-file\s+""([^""]+)""", RegexOptions.IgnoreCase);
                if (startedFromFileMatch.Success)
                {
                    return startedFromFileMatch.Groups[1].Value;
                }
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[DesktopPlayerDetector] WMI error for PID {pid}: {ex.Message}");
        }

        return null;
    }
}
