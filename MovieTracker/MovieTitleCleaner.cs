using System.Text.RegularExpressions;

namespace MovieTracker;

public record CleanedMovieInfo(
    string Title,
    string Year,
    string RawTitle,
    string Season = "",
    string Episode = "",
    bool IsSeries = false
);

public static class MovieTitleCleaner
{
    private static readonly Regex YearRegex = new(@"\b(19\d\d|20\d\d)\b", RegexOptions.Compiled);
    
    // S01E02, S00E01, S1E2, S01.E02
    private static readonly Regex SeasonEpisodeRegex = new(@"\b[sS](\d{1,2})[\s._-]*[eE](\d{1,3})\b", RegexOptions.Compiled);
    
    // 1x02, 01x02
    private static readonly Regex AltSeasonEpisodeRegex = new(@"\b(\d{1,2})x(\d{1,3})\b", RegexOptions.Compiled);

    // Common video file extensions
    private static readonly Regex ExtensionRegex = new(@"\.(mkv|mp4|avi|mov|wmv|flv|webm|m4v)$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    
    // Scene release noise tags & YouTube channel/format noise
    private static readonly string[] NoiseKeywords =
    [
        "1080p", "720p", "480p", "2160p", "4k", "uhd", "hdr", "hdr10", "hdr10plus", "dv", "dovi",
        "bluray", "blu-ray", "brrip", "bdrip", "webrip", "web-dl", "web", "hdtv", "hdrip", "remux",
        "x264", "x265", "h264", "h265", "hevc", "avc", "10bit", "aac", "ac3", "dts", "dts-hd",
        "truehd", "atmos", "ddp5.1", "dd5.1", "dual", "audio", "sub", "subs", "multisubs", "proper",
        "repack", "yify", "yts", "rarbg", "eztv", "sparks", "amiable", "galaxyrg", "cinevood",
        "official trailer", "teaser", "full movie", "movie", "trailer", "subtitles", "eng sub", "esubs",
        "malayalam", "tamil", "telugu", "hindi", "kannada", "bengali", "punjabi"
    ];

    public static CleanedMovieInfo Clean(string rawInput)
    {
        if (string.IsNullOrWhiteSpace(rawInput))
            return new CleanedMovieInfo("Unknown Title", "", rawInput ?? "");

        string working = rawInput.Trim();

        // 1. Strip file extension if present
        working = ExtensionRegex.Replace(working, "");

        // 2. Remove text within square brackets [...] like [1080p] or [Official Video]
        working = Regex.Replace(working, @"\[.*?\]", " ");

        // 3. Check for Season and Episode (e.g. S01E02 or 1x05)
        string detectedSeason = "";
        string detectedEpisode = "";
        bool isSeries = false;

        var seMatch = SeasonEpisodeRegex.Match(working);
        if (!seMatch.Success)
        {
            seMatch = AltSeasonEpisodeRegex.Match(working);
        }

        if (seMatch.Success)
        {
            isSeries = true;
            if (int.TryParse(seMatch.Groups[1].Value, out int sNum))
                detectedSeason = sNum.ToString();
            else
                detectedSeason = seMatch.Groups[1].Value;

            if (int.TryParse(seMatch.Groups[2].Value, out int eNum))
                detectedEpisode = eNum.ToString();
            else
                detectedEpisode = seMatch.Groups[2].Value;

            string beforeSe = working[..seMatch.Index].Trim();
            string afterSe = working[(seMatch.Index + seMatch.Length)..].Trim();

            if (!string.IsNullOrWhiteSpace(beforeSe))
            {
                working = beforeSe;
            }
            else if (!string.IsNullOrWhiteSpace(afterSe))
            {
                working = afterSe;
            }
        }

        // 4. Look for a 4-digit year (1900-2099)
        string detectedYear = "";
        var yearMatches = YearRegex.Matches(working);
        if (yearMatches.Count > 0)
        {
            var lastYearMatch = yearMatches[^1];
            detectedYear = lastYearMatch.Value;
            
            int yearIndex = lastYearMatch.Index;
            string beforeYear = working[..yearIndex];
            if (!string.IsNullOrWhiteSpace(beforeYear))
            {
                working = beforeYear;
            }
        }

        // 5. Replace dots, underscores, dashes with spaces
        working = Regex.Replace(working, @"[._]+", " ");
        working = Regex.Replace(working, @"(?<=\s)-(?=\s)", " ");

        // 6. Remove noise keywords
        foreach (var keyword in NoiseKeywords)
        {
            working = Regex.Replace(working, $@"\b{Regex.Escape(keyword)}\b", "", RegexOptions.IgnoreCase);
        }

        // 7. Clean up trailing/leading special characters and excess whitespace
        working = Regex.Replace(working, @"[()\-–—|:]+", " ");
        working = Regex.Replace(working, @"\s+", " ").Trim();

        if (string.IsNullOrWhiteSpace(working))
            working = rawInput;

        // If no year was detected from title, leave it blank so user isn't misled
        return new CleanedMovieInfo(
            Title: working,
            Year: detectedYear,
            RawTitle: rawInput,
            Season: detectedSeason,
            Episode: detectedEpisode,
            IsSeries: isSeries
        );
    }
}
