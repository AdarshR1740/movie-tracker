namespace MovieTracker;

public record MediaPlaybackEvent(
    string AppName,
    string AppId,
    string RawTitle,
    string CleanTitle,
    string Year,
    TimeSpan Duration,
    string Season = "",
    string Episode = "",
    bool IsSeries = false,
    IntPtr PlayerHwnd = default,
    bool IsManualTrigger = false
);
