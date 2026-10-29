using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Timers;
using Windows.Media.Control;
using Timer = System.Timers.Timer;

namespace MovieTracker;

public class MediaWatcher : IDisposable
{
    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern IntPtr GetAncestor(IntPtr hwnd, uint gaFlags);
    private const uint GA_ROOT = 2;

    private static IntPtr GetTopLevelWindow(IntPtr hWnd)
    {
        if (hWnd == IntPtr.Zero) return IntPtr.Zero;
        IntPtr root = GetAncestor(hWnd, GA_ROOT);
        return root != IntPtr.Zero ? root : hWnd;
    }

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);
    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    private GlobalSystemMediaTransportControlsSessionManager? _sessionManager;
    private readonly object _lock = new();
    private readonly Dictionary<string, DateTime> _seenMediaCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly Timer _pollingTimer;

    public event Action<MediaPlaybackEvent>? OnMovieDetected;

    public MediaWatcher()
    {
        _pollingTimer = new Timer(3000);
        _pollingTimer.Elapsed += PollingTimer_Elapsed;
        _pollingTimer.AutoReset = true;
    }

    public async Task StartAsync()
    {
        _pollingTimer.Start();

        try
        {
            _sessionManager = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
            if (_sessionManager != null)
            {
                _sessionManager.CurrentSessionChanged += SessionManager_CurrentSessionChanged;
                _sessionManager.SessionsChanged += SessionManager_SessionsChanged;

                var currentSession = _sessionManager.GetCurrentSession();
                if (currentSession != null)
                {
                    HookSessionEvents(currentSession);
                }
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[MediaWatcher] Error initializing SMTC: {ex.Message}");
        }
    }

    private async void PollingTimer_Elapsed(object? sender, ElapsedEventArgs e)
    {
        try
        {
            IntPtr fgHwnd = GetForegroundWindow();
            IntPtr rootHwnd = GetTopLevelWindow(fgHwnd);
            string fgProcessName = "";
            if (rootHwnd != IntPtr.Zero)
            {
                GetWindowThreadProcessId(rootHwnd, out uint fgPid);
                try { fgProcessName = Process.GetProcessById((int)fgPid).ProcessName.ToLowerInvariant(); } catch { }
            }

            // If the user is actively working in a web browser, do NOT background-poll Stremio.
            // Browser video detection is driven by Windows SMTC events.
            if (IsBrowserProcess(fgProcessName))
            {
                return;
            }

            // 1. Auto-check Stremio: only if user is in Stremio or focused on non-browser window
            if (IsStremioProcess(fgProcessName) || !IsBrowserProcess(fgProcessName))
            {
                var stremioMedia = await StremioDetector.DetectActiveStreamAsync(FindStremioWindow(), isManual: false);
                if (stremioMedia != null)
                {
                    if (CheckDebounce(stremioMedia.RawTitle))
                    {
                        OnMovieDetected?.Invoke(stremioMedia);
                        return;
                    }
                }
            }

            // 2. Auto-check classic desktop player (VLC, MPC, MPV)
            var desktopMedia = DesktopPlayerDetector.DetectActivePlayer();
            if (desktopMedia != null)
            {
                if (CheckDebounce(desktopMedia.RawTitle))
                {
                    OnMovieDetected?.Invoke(desktopMedia);
                }
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[MediaWatcher] Polling error: {ex.Message}");
        }
    }

    public void MarkAsSeen(string rawTitle)
    {
        if (string.IsNullOrWhiteSpace(rawTitle)) return;
        lock (_lock)
        {
            string key = GetCanonicalCacheKey(rawTitle);
            _seenMediaCache[key] = DateTime.Now;
        }
    }

    private static string GetCanonicalCacheKey(string rawTitle)
    {
        var cleaned = MovieTitleCleaner.Clean(rawTitle);
        if (cleaned.IsSeries && !string.IsNullOrWhiteSpace(cleaned.Season) && !string.IsNullOrWhiteSpace(cleaned.Episode))
        {
            return $"{cleaned.Title.ToLowerInvariant()}_s{cleaned.Season}e{cleaned.Episode}";
        }
        if (!string.IsNullOrWhiteSpace(cleaned.Title))
        {
            return cleaned.Title.ToLowerInvariant();
        }
        return rawTitle.Trim().ToLowerInvariant();
    }

    private bool CheckDebounce(string rawTitle)
    {
        if (string.IsNullOrWhiteSpace(rawTitle)) return false;

        lock (_lock)
        {
            var now = DateTime.Now;

            // Prune entries older than 2 hours
            var expiredKeys = _seenMediaCache
                .Where(kv => (now - kv.Value).TotalHours >= 2)
                .Select(kv => kv.Key)
                .ToList();

            foreach (var k in expiredKeys)
            {
                _seenMediaCache.Remove(k);
            }

            string key = GetCanonicalCacheKey(rawTitle);

            if (_seenMediaCache.TryGetValue(key, out var lastSeen))
            {
                // Already auto-detected or logged within this 2-hour session
                if ((now - lastSeen).TotalHours < 2)
                {
                    return false;
                }
            }

            _seenMediaCache[key] = now;
            return true;
        }
    }

    private void SessionManager_CurrentSessionChanged(
        GlobalSystemMediaTransportControlsSessionManager sender,
        CurrentSessionChangedEventArgs args)
    {
        var session = sender.GetCurrentSession();
        if (session != null)
        {
            HookSessionEvents(session);
            _ = CheckAndNotifySessionAsync(session, isManual: false);
        }
    }

    private void SessionManager_SessionsChanged(
        GlobalSystemMediaTransportControlsSessionManager sender,
        SessionsChangedEventArgs args)
    {
        var session = sender.GetCurrentSession();
        if (session != null)
        {
            HookSessionEvents(session);
        }
    }

    private void HookSessionEvents(GlobalSystemMediaTransportControlsSession session)
    {
        try
        {
            session.MediaPropertiesChanged -= Session_MediaPropertiesChanged;
            session.PlaybackInfoChanged -= Session_PlaybackInfoChanged;
            session.TimelinePropertiesChanged -= Session_TimelinePropertiesChanged;

            session.MediaPropertiesChanged += Session_MediaPropertiesChanged;
            session.PlaybackInfoChanged += Session_PlaybackInfoChanged;
            session.TimelinePropertiesChanged += Session_TimelinePropertiesChanged;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[MediaWatcher] HookSessionEvents error: {ex.Message}");
        }
    }

    private void Session_TimelinePropertiesChanged(
        GlobalSystemMediaTransportControlsSession sender,
        TimelinePropertiesChangedEventArgs args)
    {
        _ = CheckAndNotifySessionAsync(sender, isManual: false);
    }

    private void Session_PlaybackInfoChanged(
        GlobalSystemMediaTransportControlsSession sender,
        PlaybackInfoChangedEventArgs args)
    {
        _ = CheckAndNotifySessionAsync(sender, isManual: false);
    }

    private void Session_MediaPropertiesChanged(
        GlobalSystemMediaTransportControlsSession sender,
        MediaPropertiesChangedEventArgs args)
    {
        _ = CheckAndNotifySessionAsync(sender, isManual: false);
    }

    public async Task<MediaPlaybackEvent?> CaptureCurrentAsync(bool isManual = true)
    {
        IntPtr fgHwnd = GetForegroundWindow();
        IntPtr rootHwnd = GetTopLevelWindow(fgHwnd);
        string fgProcessName = "";
        if (rootHwnd != IntPtr.Zero)
        {
            GetWindowThreadProcessId(rootHwnd, out uint fgPid);
            try
            {
                var p = Process.GetProcessById((int)fgPid);
                fgProcessName = p.ProcessName.ToLowerInvariant();
            }
            catch { }
        }

        App.Log($"[CaptureCurrentAsync] isManual={isManual}, fgHwnd=0x{fgHwnd:X8}, rootHwnd=0x{rootHwnd:X8}, proc='{fgProcessName}'");

        // =========================================================================
        // PRIORITY 1: The user is actively focused on a specific player or browser
        // STRICT ISOLATION: When focused on a media player or browser with active video,
        // prioritize that source first!
        // =========================================================================
        if (IsStremioProcess(fgProcessName))
        {
            var stremioEvent = await StremioDetector.DetectActiveStreamAsync(rootHwnd, isManual: isManual);
            if (stremioEvent != null)
            {
                App.Log($"[CaptureCurrentAsync] Detected focused Stremio: '{stremioEvent.CleanTitle}'");
                return stremioEvent;
            }
            // Focused on Stremio window but no stream found: don't confuse with web/vlc
            return null;
        }
        else if (IsDesktopPlayerProcess(fgProcessName))
        {
            var desktopPlayer = DesktopPlayerDetector.DetectActivePlayer();
            if (desktopPlayer != null)
            {
                App.Log($"[CaptureCurrentAsync] Detected focused desktop player: '{desktopPlayer.CleanTitle}'");
                return desktopPlayer;
            }
            return null;
        }
        else if (IsBrowserProcess(fgProcessName))
        {
            // 1. Check SMTC session for this browser (playing OR paused)
            var browserSession = await FindBrowserSmtcSessionAsync(fgProcessName);
            if (browserSession != null)
            {
                App.Log($"[CaptureCurrentAsync] Detected focused browser SMTC: '{browserSession.CleanTitle}'");
                return browserSession;
            }

            // 2. Check tab title of this browser window
            var tabFallback = DetectActiveBrowserTabFallback(rootHwnd);
            if (tabFallback != null)
            {
                App.Log($"[CaptureCurrentAsync] Detected focused browser tab: '{tabFallback.CleanTitle}'");
                return tabFallback;
            }

            // Note: If the browser is NOT playing media (e.g. user is browsing or chatting in browser),
            // and the user manually pressed the hotkey (isManual == true), DO NOT return null!
            // Continue falling through to check background Stremio or Desktop player!
            if (!isManual)
            {
                // In background auto-polling, don't interrupt active web browsing
                return null;
            }
        }

        // =========================================================================
        // PRIORITY 2: Not focused on any active video player
        // (e.g. focused on desktop, explorer, code editor, or idle browser window)
        // Search across all sources for active/recent playback:
        // =========================================================================

        // 2a. Actively playing SMTC session (YouTube, Netflix, Prime in any browser)
        var playingSmtc = await FindPlayingSmtcSessionAsync();
        if (playingSmtc != null)
        {
            App.Log($"[CaptureCurrentAsync] Fallback matched playing SMTC: '{playingSmtc.CleanTitle}'");
            return playingSmtc;
        }

        // 2b. Actively playing Desktop player (VLC, MPC, MPV)
        var anyDesktop = DesktopPlayerDetector.DetectActivePlayer();
        if (anyDesktop != null)
        {
            App.Log($"[CaptureCurrentAsync] Fallback matched desktop player: '{anyDesktop.CleanTitle}'");
            return anyDesktop;
        }

        // 2c. Stremio stream (active or recent)
        var anyStremio = await StremioDetector.DetectActiveStreamAsync(FindStremioWindow(), isManual: isManual);
        if (anyStremio != null)
        {
            App.Log($"[CaptureCurrentAsync] Fallback matched Stremio: '{anyStremio.CleanTitle}'");
            return anyStremio;
        }

        // 2d. Any paused SMTC session
        var pausedSmtc = await FindAnyPausedSmtcSessionAsync();
        if (pausedSmtc != null)
        {
            App.Log($"[CaptureCurrentAsync] Fallback matched paused SMTC: '{pausedSmtc.CleanTitle}'");
            return pausedSmtc;
        }

        App.Log("[CaptureCurrentAsync] No media returned by CaptureCurrentAsync");
        return null;
    }

    private async Task<MediaPlaybackEvent?> FindBrowserSmtcSessionAsync(string browserProcessName)
    {
        await EnsureSessionManagerAsync();
        var sessions = _sessionManager?.GetSessions();
        if (sessions == null) return null;

        // 1. Try to find a Playing session for this browser
        foreach (var s in sessions)
        {
            if (IsSessionMatchBrowser(s, browserProcessName))
            {
                var pb = s.GetPlaybackInfo();
                if (pb?.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing)
                {
                    var ev = await EvaluateSessionAsync(s, isManual: true);
                    if (ev != null) return ev;
                }
            }
        }

        // 2. Try to find a Paused session for this browser
        foreach (var s in sessions)
        {
            if (IsSessionMatchBrowser(s, browserProcessName))
            {
                var ev = await EvaluateSessionAsync(s, isManual: true);
                if (ev != null) return ev;
            }
        }

        // 3. Fallback: if GetCurrentSession() matches this browser
        var current = _sessionManager?.GetCurrentSession();
        if (current != null && IsSessionMatchBrowser(current, browserProcessName))
        {
            var ev = await EvaluateSessionAsync(current, isManual: true);
            if (ev != null) return ev;
        }

        return null;
    }

    private static bool IsSessionMatchBrowser(GlobalSystemMediaTransportControlsSession session, string browserProcessName)
    {
        string appId = (session.SourceAppUserModelId ?? "").ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(appId)) return false;

        if (IsExcludedMusicApp(appId) || IsDesktopPlayerAppId(appId))
            return false;

        if (browserProcessName.Contains("firefox"))
        {
            if (appId.Contains("firefox")) return true;
            if (IsHexOnlyString(appId) && appId.Length >= 8) return true;
        }
        else if (browserProcessName.Contains("chrome"))
        {
            if (appId.Contains("chrome")) return true;
        }
        else if (browserProcessName.Contains("msedge") || browserProcessName.Contains("edge"))
        {
            if (appId.Contains("edge")) return true;
        }
        else if (browserProcessName.Contains("brave"))
        {
            if (appId.Contains("brave")) return true;
        }

        return false;
    }

    private static bool IsHexOnlyString(string s)
    {
        if (string.IsNullOrWhiteSpace(s)) return false;
        foreach (char c in s)
        {
            if (!((c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F')))
                return false;
        }
        return true;
    }

    private static bool IsDesktopPlayerAppId(string appId)
    {
        string lower = appId.ToLowerInvariant();
        return lower.Contains("vlc") || lower.Contains("mpc") || lower.Contains("mpv") || lower.Contains("potplayer");
    }

    private async Task<MediaPlaybackEvent?> FindPlayingSmtcSessionAsync()
    {
        await EnsureSessionManagerAsync();
        var sessions = _sessionManager?.GetSessions();
        if (sessions == null) return null;

        foreach (var s in sessions)
        {
            string appId = (s.SourceAppUserModelId ?? "").ToLowerInvariant();
            if (IsExcludedMusicApp(appId) || IsDesktopPlayerAppId(appId)) continue;

            var pb = s.GetPlaybackInfo();
            if (pb?.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing)
            {
                var ev = await EvaluateSessionAsync(s, isManual: true);
                if (ev != null) return ev;
            }
        }
        return null;
    }

    private async Task<MediaPlaybackEvent?> FindAnyPausedSmtcSessionAsync()
    {
        await EnsureSessionManagerAsync();
        var fallback = _sessionManager?.GetCurrentSession() ?? _sessionManager?.GetSessions()?.FirstOrDefault();
        if (fallback != null)
        {
            string appId = (fallback.SourceAppUserModelId ?? "").ToLowerInvariant();
            if (!IsExcludedMusicApp(appId) && !IsDesktopPlayerAppId(appId))
            {
                return await EvaluateSessionAsync(fallback, isManual: true);
            }
        }
        return null;
    }

    private async Task EnsureSessionManagerAsync()
    {
        if (_sessionManager == null)
        {
            try
            {
                _sessionManager = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
            }
            catch { }
        }
    }

    private static MediaPlaybackEvent? DetectActiveBrowserTabFallback(IntPtr fgHwnd)
    {
        if (fgHwnd == IntPtr.Zero) return null;

        try
        {
            IntPtr rootHwnd = GetTopLevelWindow(fgHwnd);
            GetWindowThreadProcessId(rootHwnd, out uint fgPid);
            var p = Process.GetProcessById((int)fgPid);
            string pName = p.ProcessName.ToLowerInvariant();

            if (IsBrowserProcess(pName))
            {
                var sb = new StringBuilder(1024);
                int len = GetWindowText(rootHwnd, sb, 1024);
                if (len > 0)
                {
                    string title = sb.ToString().Trim();
                    string[] suffixes = [" — Mozilla Firefox", " - Mozilla Firefox", " - Google Chrome", " - Microsoft​ Edge", " - Brave"];
                    foreach (var s in suffixes)
                    {
                        if (title.EndsWith(s, StringComparison.OrdinalIgnoreCase))
                        {
                            title = title[..^s.Length].Trim();
                        }
                    }

                    string[] ytSuffixes = [" - YouTube", " — YouTube", " | YouTube"];
                    foreach (var s in ytSuffixes)
                    {
                        if (title.EndsWith(s, StringComparison.OrdinalIgnoreCase))
                        {
                            title = title[..^s.Length].Trim();
                        }
                    }

                    if (!string.IsNullOrWhiteSpace(title) && !title.Equals("New Tab", StringComparison.OrdinalIgnoreCase) && !title.Equals("YouTube", StringComparison.OrdinalIgnoreCase))
                    {
                        var cleaned = MovieTitleCleaner.Clean(title);
                        return new MediaPlaybackEvent(
                            AppName: "Web Browser",
                            AppId: pName,
                            RawTitle: title,
                            CleanTitle: cleaned.Title,
                            Year: cleaned.Year,
                            Duration: TimeSpan.Zero,
                            Season: cleaned.Season,
                            Episode: cleaned.Episode,
                            IsSeries: cleaned.IsSeries,
                            PlayerHwnd: rootHwnd,
                            IsManualTrigger: true
                        );
                    }
                }
            }
        }
        catch { }

        return null;
    }

    private async Task CheckAndNotifySessionAsync(GlobalSystemMediaTransportControlsSession session, bool isManual)
    {
        try
        {
            var playbackEvent = await EvaluateSessionAsync(session, isManual);
            if (playbackEvent != null)
            {
                OnMovieDetected?.Invoke(playbackEvent);
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[MediaWatcher] CheckAndNotifySession error: {ex.Message}");
        }
    }

    private async Task<MediaPlaybackEvent?> EvaluateSessionAsync(
        GlobalSystemMediaTransportControlsSession session,
        bool isManual)
    {
        try
        {
            var playbackInfo = session.GetPlaybackInfo();
            if (!isManual && playbackInfo?.PlaybackStatus != GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing)
            {
                return null;
            }

            var mediaProps = await session.TryGetMediaPropertiesAsync();
            if (mediaProps == null || string.IsNullOrWhiteSpace(mediaProps.Title))
            {
                return null;
            }

            string appId = session.SourceAppUserModelId ?? "";
            string rawTitle = mediaProps.Title.Trim();

            if (!isManual && IsExcludedMusicApp(appId))
            {
                return null;
            }

            var timeline = session.GetTimelineProperties();
            TimeSpan duration = TimeSpan.Zero;
            if (timeline != null && timeline.EndTime > timeline.StartTime)
            {
                duration = timeline.EndTime - timeline.StartTime;
            }

            if (!isManual)
            {
                if (duration.TotalMinutes > 0 && duration.TotalMinutes < 40)
                {
                    return null;
                }

                if (!CheckDebounce(rawTitle))
                {
                    return null;
                }
            }

            string friendlyAppName = GetFriendlyAppName(appId);
            var cleaned = MovieTitleCleaner.Clean(rawTitle);
            IntPtr playerHwnd = FindBrowserOrActiveWindow();

            return new MediaPlaybackEvent(
                AppName: friendlyAppName,
                AppId: appId,
                RawTitle: rawTitle,
                CleanTitle: cleaned.Title,
                Year: cleaned.Year,
                Duration: duration,
                Season: cleaned.Season,
                Episode: cleaned.Episode,
                IsSeries: cleaned.IsSeries,
                PlayerHwnd: playerHwnd,
                IsManualTrigger: isManual
            );
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[MediaWatcher] EvaluateSessionAsync error: {ex.Message}");
            return null;
        }
    }

    private static IntPtr FindStremioWindow()
    {
        IntPtr foundHwnd = IntPtr.Zero;
        string[] stremioNames = ["stremio-shell-ng", "stremio", "msedgewebview2"];

        foreach (var name in stremioNames)
        {
            var procs = Process.GetProcessesByName(name);
            foreach (var proc in procs)
            {
                EnumWindows((hWnd, lParam) =>
                {
                    GetWindowThreadProcessId(hWnd, out uint pid);
                    if (pid == proc.Id && IsWindowVisible(hWnd))
                    {
                        foundHwnd = hWnd;
                        return false;
                    }
                    return true;
                }, IntPtr.Zero);

                if (foundHwnd != IntPtr.Zero) return foundHwnd;
            }
        }
        return IntPtr.Zero;
    }

    private static IntPtr FindBrowserOrActiveWindow()
    {
        IntPtr fgHwnd = GetForegroundWindow();
        if (fgHwnd != IntPtr.Zero)
        {
            GetWindowThreadProcessId(fgHwnd, out uint fgPid);
            try
            {
                var p = Process.GetProcessById((int)fgPid);
                string pName = p.ProcessName.ToLowerInvariant();
                if (IsBrowserProcess(pName) || IsDesktopPlayerProcess(pName) || IsStremioProcess(pName))
                {
                    return fgHwnd;
                }
            }
            catch { }
        }

        IntPtr foundHwnd = IntPtr.Zero;
        string[] browserProcessNames = ["firefox", "chrome", "msedge", "brave", "stremio-shell-ng", "stremio"];

        foreach (var name in browserProcessNames)
        {
            var procs = Process.GetProcessesByName(name);
            foreach (var proc in procs)
            {
                EnumWindows((hWnd, lParam) =>
                {
                    GetWindowThreadProcessId(hWnd, out uint pid);
                    if (pid == proc.Id && IsWindowVisible(hWnd))
                    {
                        foundHwnd = hWnd;
                        return false;
                    }
                    return true;
                }, IntPtr.Zero);

                if (foundHwnd != IntPtr.Zero) return foundHwnd;
            }
        }

        return fgHwnd;
    }

    private static bool IsStremioProcess(string pName) =>
        pName.Contains("stremio") || pName.Contains("stremio-shell-ng") || pName.Contains("stremio-runtime") || pName.Contains("webview2");

    private static bool IsDesktopPlayerProcess(string pName) =>
        pName.Contains("vlc") || pName.Contains("mpc") || pName.Contains("mpv") || pName.Contains("potplayer");

    private static bool IsBrowserProcess(string pName) =>
        (pName.Contains("firefox") || pName.Contains("chrome") || pName.Equals("msedge", StringComparison.OrdinalIgnoreCase) || pName.Contains("brave"))
        && !pName.Contains("webview");

    private static bool IsExcludedMusicApp(string appId)
    {
        string lower = appId.ToLowerInvariant();
        return lower.Contains("spotify") ||
               lower.Contains("applemusic") ||
               lower.Contains("tidal") ||
               lower.Contains("amazonmusic");
    }

    private static string GetFriendlyAppName(string appId)
    {
        string lower = appId.ToLowerInvariant();
        if (lower.Contains("vlc")) return "VLC Media Player";
        if (lower.Contains("stremio")) return "Stremio";
        if (lower.Contains("chrome")) return "Chrome / YouTube";
        if (lower.Contains("msedge")) return "Edge / YouTube";
        if (lower.Contains("firefox") || Process.GetProcessesByName("firefox").Length > 0) return "Firefox / YouTube";
        if (lower.Contains("brave")) return "Brave / YouTube";
        if (lower.Contains("mpc")) return "MPC-HC";
        if (lower.Contains("mpv")) return "MPV";
        return "Web Media";
    }

    public void Dispose()
    {
        _pollingTimer.Stop();
        _pollingTimer.Dispose();
    }
}
