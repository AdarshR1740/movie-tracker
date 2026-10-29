using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace MovieTracker;

public record DiaryEntry(
    string Title,
    string Year,
    string Rating,
    string WatchedDate,
    string Season = "",
    string Episode = "",
    string Source = "",
    string WatchedTime = ""
);

public class LetterboxdDiaryService
{
    private static readonly string DiaryDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
        "MovieTracker"
    );

    private static readonly string DiaryFilePath = Path.Combine(DiaryDirectory, "letterboxd_diary.csv");
    private const string CurrentHeader = "Title,Type,Year,Season,Episode,Rating,WatchedDate,WatchedTime,Source,Tags\n";
    private static readonly object FileLock = new();

    public static string GetFilePath() => DiaryFilePath;

    public static void EnsureFileExists()
    {
        lock (FileLock)
        {
            if (!Directory.Exists(DiaryDirectory))
            {
                Directory.CreateDirectory(DiaryDirectory);
            }

            if (!File.Exists(DiaryFilePath))
            {
                File.WriteAllText(DiaryFilePath, CurrentHeader, Encoding.UTF8);
                return;
            }

            // Check if file has old header and auto-migrate
            MigrateOldFileIfNeeded();
        }
    }

    private static void MigrateOldFileIfNeeded()
    {
        try
        {
            var lines = File.ReadAllLines(DiaryFilePath, Encoding.UTF8);
            if (lines.Length == 0)
            {
                File.WriteAllText(DiaryFilePath, CurrentHeader, Encoding.UTF8);
                return;
            }

            string firstLine = lines[0].Trim();
            if (firstLine.Equals(CurrentHeader.TrimEnd('\n'), StringComparison.OrdinalIgnoreCase))
            {
                // Already up to date
                return;
            }

            // Backup old file
            string backupPath = Path.Combine(DiaryDirectory, "letterboxd_diary.csv.bak");
            File.WriteAllLines(backupPath, lines, Encoding.UTF8);

            var newLines = new List<string> { CurrentHeader.TrimEnd('\n') };

            for (int i = 1; i < lines.Length; i++)
            {
                string line = lines[i];
                if (string.IsNullOrWhiteSpace(line)) continue;

                var cols = ParseCsvLine(line);
                if (cols.Count == 0) continue;

                string title = cols.Count > 0 ? cols[0] : "";
                string type = "Movie";
                string year = "";
                string season = "";
                string episode = "";
                string rating = "";
                string watchedDate = "";
                string watchedTime = "";
                string source = "";
                string tags = "";

                if (cols.Count == 4)
                {
                    // Old 4-col format: Title,Year,Rating,WatchedDate
                    year = cols.Count > 1 ? cols[1] : "";
                    rating = cols.Count > 2 ? cols[2] : "";
                    watchedDate = cols.Count > 3 ? cols[3] : "";
                    type = "Movie";
                }
                else if (cols.Count == 7)
                {
                    // 7-col format: Title,Year,Season,Episode,Rating,WatchedDate,Tags
                    year = cols[1];
                    season = cols[2];
                    episode = cols[3];
                    rating = cols[4];
                    watchedDate = cols[5];
                    tags = cols[6];
                    bool hasSe = !string.IsNullOrWhiteSpace(season) || !string.IsNullOrWhiteSpace(episode);
                    type = hasSe ? "Series" : "Movie";
                }
                else if (cols.Count == 8)
                {
                    // 8-col format: Title,Type,Year,Season,Episode,Rating,WatchedDate,Tags
                    type = cols[1];
                    year = cols[2];
                    season = cols[3];
                    episode = cols[4];
                    rating = cols[5];
                    watchedDate = cols[6];
                    tags = cols[7];
                }
                else if (cols.Count >= 10)
                {
                    type = cols[1];
                    year = cols[2];
                    season = cols[3];
                    episode = cols[4];
                    rating = cols[5];
                    watchedDate = cols[6];
                    watchedTime = cols[7];
                    source = cols[8];
                    tags = cols[9];
                }
                else
                {
                    year = cols.Count > 1 ? cols[1] : "";
                    watchedDate = cols.Count > 2 ? cols[2] : "";
                }

                // Check title for S00E01 if season is empty
                if (string.IsNullOrEmpty(season) && string.IsNullOrEmpty(episode))
                {
                    var m = Regex.Match(title, @"\b[sS](\d{1,2})[eE](\d{1,3})\b");
                    if (m.Success)
                    {
                        season = int.Parse(m.Groups[1].Value).ToString();
                        episode = int.Parse(m.Groups[2].Value).ToString();
                        type = "Series";
                        if (string.IsNullOrEmpty(tags))
                            tags = $"S{m.Groups[1].Value.PadLeft(2, '0')}E{m.Groups[2].Value.PadLeft(2, '0')}";
                    }
                }

                string newLine = $"{EscapeCsvField(title)},{type},{EscapeCsvField(year)},{EscapeCsvField(season)},{EscapeCsvField(episode)},{EscapeCsvField(rating)},{EscapeCsvField(watchedDate)},{EscapeCsvField(watchedTime)},{EscapeCsvField(source)},{EscapeCsvField(tags)}";
                newLines.Add(newLine);
            }

            File.WriteAllLines(DiaryFilePath, newLines, Encoding.UTF8);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[DiaryMigration] Error: {ex.Message}");
        }
    }

    public static void AppendEntry(DiaryEntry entry)
    {
        EnsureFileExists();

        lock (FileLock)
        {
            bool isSeries = !string.IsNullOrWhiteSpace(entry.Season) || !string.IsNullOrWhiteSpace(entry.Episode);
            string mediaType = isSeries ? "Series" : "Movie";

            string escapedTitle = EscapeCsvField(entry.Title);
            string escapedType = mediaType;
            string escapedYear = EscapeCsvField(entry.Year);
            string escapedSeason = EscapeCsvField(entry.Season);
            string escapedEpisode = EscapeCsvField(entry.Episode);
            string escapedRating = EscapeCsvField(entry.Rating);
            string escapedDate = EscapeCsvField(entry.WatchedDate);
            string timeStr = !string.IsNullOrWhiteSpace(entry.WatchedTime) 
                ? entry.WatchedTime 
                : DateTime.Now.ToString("HH:mm:ss");
            string escapedTime = EscapeCsvField(timeStr);
            string escapedSource = EscapeCsvField(entry.Source);

            string tags = "";
            if (isSeries)
            {
                string s = entry.Season.Trim();
                string e = entry.Episode.Trim();
                if (int.TryParse(s, out int sNum) && int.TryParse(e, out int eNum))
                {
                    tags = $"S{sNum:D2}E{eNum:D2}";
                }
                else if (!string.IsNullOrEmpty(s) && !string.IsNullOrEmpty(e))
                {
                    tags = $"S{s}E{e}";
                }
                else if (!string.IsNullOrEmpty(s))
                {
                    tags = $"S{s}";
                }
                else if (!string.IsNullOrEmpty(e))
                {
                    tags = $"E{e}";
                }
            }
            string escapedTags = EscapeCsvField(tags);

            string line = $"{escapedTitle},{escapedType},{escapedYear},{escapedSeason},{escapedEpisode},{escapedRating},{escapedDate},{escapedTime},{escapedSource},{escapedTags}\n";
            File.AppendAllText(DiaryFilePath, line, Encoding.UTF8);
        }
    }

    public static void OpenDiaryFolder()
    {
        EnsureFileExists();
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = $"/select,\"{DiaryFilePath}\"",
                UseShellExecute = true
            });
        }
        catch
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = DiaryDirectory,
                UseShellExecute = true
            });
        }
    }

    public static void OpenDiaryFile()
    {
        EnsureFileExists();
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = DiaryFilePath,
                UseShellExecute = true
            });
        }
        catch
        {
            OpenDiaryFolder();
        }
    }

    private static string EscapeCsvField(string? field)
    {
        if (string.IsNullOrEmpty(field)) return "";
        bool needsQuotes = field.Contains(',') || field.Contains('"') || field.Contains('\n') || field.Contains('\r');
        if (needsQuotes)
        {
            return $"\"{field.Replace("\"", "\"\"")}\"";
        }
        return field;
    }

    private static List<string> ParseCsvLine(string line)
    {
        var result = new List<string>();
        var sb = new StringBuilder();
        bool inQuotes = false;

        for (int i = 0; i < line.Length; i++)
        {
            char c = line[i];
            if (c == '"')
            {
                if (inQuotes && i + 1 < line.Length && line[i + 1] == '"')
                {
                    sb.Append('"');
                    i++; // Skip escaped quote
                }
                else
                {
                    inQuotes = !inQuotes;
                }
            }
            else if (c == ',' && !inQuotes)
            {
                result.Add(sb.ToString().Trim());
                sb.Clear();
            }
            else
            {
                sb.Append(c);
            }
        }
        result.Add(sb.ToString().Trim());
        return result;
    }
}
