using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text.Json;

namespace MovieTracker;

public static class StremioDetector
{
    private static readonly HttpClient HttpClient = new()
    {
        Timeout = TimeSpan.FromSeconds(3)
    };

    private class StremioCandidate
    {
        public string StreamName { get; set; } = "";
        public string ActiveFileName { get; set; } = "";
        public int SelectionsCount { get; set; } = 0;
        public double DownloadSpeed { get; set; } = 0;
        public DateTime LatestAccessTime { get; set; } = DateTime.MinValue;
        public int OrderIndex { get; set; } = 0;
    }

    private class FileInfo
    {
        public string Name { get; set; } = "";
        public long Length { get; set; } = 0;
        public long Offset { get; set; } = 0;
        public bool HasCacheEvents { get; set; } = false;
    }

    public static async Task<MediaPlaybackEvent?> DetectActiveStreamAsync(IntPtr playerHwnd = default, bool isManual = true)
    {
        try
        {
            string response;
            try
            {
                response = await HttpClient.GetStringAsync("http://127.0.0.1:11470/stats.json");
            }
            catch (Exception ex)
            {
                // Stremio server not running or connection refused
                Debug.WriteLine($"[StremioDetector] HTTP GET stats.json failed: {ex.Message}");
                return null;
            }

            if (string.IsNullOrWhiteSpace(response) || response.Trim() == "{}")
            {
                return null;
            }

            using var doc = JsonDocument.Parse(response);
            var root = doc.RootElement;

            var candidates = new List<StremioCandidate>();
            int index = 0;

            foreach (var streamProp in root.EnumerateObject())
            {
                index++;
                string infoHash = streamProp.Name;
                var item = streamProp.Value;

                string streamName = "";
                if (item.TryGetProperty("name", out var nameProp))
                {
                    streamName = nameProp.GetString() ?? "";
                }

                // Locate cache folder on disk for exact file write timestamps
                string cacheFolderPath = "";
                if (item.TryGetProperty("opts", out var optsProp) && optsProp.TryGetProperty("path", out var pathProp))
                {
                    cacheFolderPath = pathProp.GetString() ?? "";
                }

                if (string.IsNullOrEmpty(cacheFolderPath) && !string.IsNullOrEmpty(infoHash))
                {
                    string localApp = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                    cacheFolderPath = Path.Combine(localApp, "Stremio", "stremio-cache", infoHash);
                }

                if (cacheFolderPath.StartsWith(@"\\?\"))
                {
                    cacheFolderPath = cacheFolderPath.Substring(4);
                }

                int selectionsCount = 0;
                long p1From = -1;
                long p1To = -1;

                if (item.TryGetProperty("selections", out var selProp) && selProp.ValueKind == JsonValueKind.Array)
                {
                    selectionsCount = selProp.GetArrayLength();

                    long anyFrom = -1;
                    long anyTo = -1;

                    foreach (var s in selProp.EnumerateArray())
                    {
                        int priority = 0;
                        if (s.TryGetProperty("priority", out var prioProp) && prioProp.TryGetInt32(out int pVal))
                        {
                            priority = pVal;
                        }

                        if (s.TryGetProperty("from", out var fromProp) && fromProp.TryGetInt64(out long fVal) &&
                            s.TryGetProperty("to", out var toProp) && toProp.TryGetInt64(out long tVal))
                        {
                            if (priority == 1 && p1From == -1)
                            {
                                p1From = fVal;
                                p1To = tVal;
                            }
                            if (anyFrom == -1)
                            {
                                anyFrom = fVal;
                                anyTo = tVal;
                            }
                        }
                    }

                    if (p1From == -1 && anyFrom != -1)
                    {
                        p1From = anyFrom;
                        p1To = anyTo;
                    }
                }

                double downloadSpeed = 0;
                if (item.TryGetProperty("downloadSpeed", out var speedProp) && speedProp.TryGetDouble(out double speed))
                {
                    downloadSpeed = speed;
                }

                var filesList = new List<FileInfo>();
                if (item.TryGetProperty("files", out var filesProp) && filesProp.ValueKind == JsonValueKind.Array)
                {
                    foreach (var fileItem in filesProp.EnumerateArray())
                    {
                        string fName = "";
                        if (fileItem.TryGetProperty("name", out var fnProp))
                        {
                            fName = fnProp.GetString() ?? "";
                        }

                        long fLen = 0;
                        if (fileItem.TryGetProperty("length", out var lenProp) && lenProp.TryGetInt64(out long lVal))
                        {
                            fLen = lVal;
                        }

                        long fOffset = 0;
                        if (fileItem.TryGetProperty("offset", out var offProp) && offProp.TryGetInt64(out long oVal))
                        {
                            fOffset = oVal;
                        }

                        bool isCacheActive = false;
                        if (fileItem.TryGetProperty("__cacheEvents", out var cacheProp) &&
                            (cacheProp.ValueKind == JsonValueKind.True || (cacheProp.ValueKind == JsonValueKind.String && cacheProp.GetString() == "true")))
                        {
                            isCacheActive = true;
                        }

                        filesList.Add(new FileInfo
                        {
                            Name = fName,
                            Length = fLen,
                            Offset = fOffset,
                            HasCacheEvents = isCacheActive
                        });
                    }
                }

                string activeFileName = "";
                DateTime latestWriteUtc = DateTime.MinValue;

                // Strategy 1: Check cache folder on disk for the most recently modified file index (exact down to millisecond)
                if (!string.IsNullOrEmpty(cacheFolderPath) && Directory.Exists(cacheFolderPath) && filesList.Count > 0)
                {
                    try
                    {
                        var di = new DirectoryInfo(cacheFolderPath);
                        var latestFile = di.GetFiles()
                            .Where(f => int.TryParse(f.Name, out _))
                            .OrderByDescending(f => f.LastWriteTimeUtc)
                            .FirstOrDefault();

                        if (latestFile != null && int.TryParse(latestFile.Name, out int fileIdx) && fileIdx >= 0 && fileIdx < filesList.Count)
                        {
                            activeFileName = filesList[fileIdx].Name;
                            latestWriteUtc = latestFile.LastWriteTimeUtc;
                        }
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"[StremioDetector] Cache folder check failed: {ex.Message}");
                    }
                }

                // Strategy 2: Match file using selection piece byte offset (during active piece buffer)
                if (string.IsNullOrEmpty(activeFileName) && p1From != -1 && p1To >= p1From && filesList.Count > 0)
                {
                    long totalTorrentLength = 0;
                    foreach (var f in filesList) totalTorrentLength += f.Length;

                    long[] standardPieceLengths = [262144, 524288, 1048576, 2097152, 4194304, 8388608, 16777216];
                    long bestPieceLength = 524288;
                    double bestScore = double.MaxValue;

                    foreach (var pl in standardPieceLengths)
                    {
                        double pieceCount = (double)totalTorrentLength / pl;
                        if (pieceCount >= 500 && pieceCount <= 35000)
                        {
                            double score = Math.Abs(pieceCount - Math.Max(p1To, 2000));
                            if (score < bestScore)
                            {
                                bestScore = score;
                                bestPieceLength = pl;
                            }
                        }
                    }

                    long midPiece = (p1From + p1To) / 2;
                    long targetByte = midPiece * bestPieceLength;

                    var matchedFile = filesList.FirstOrDefault(f => targetByte >= f.Offset && targetByte < (f.Offset + f.Length));
                    if (matchedFile != null)
                    {
                        activeFileName = matchedFile.Name;
                    }
                }

                // Strategy 3: Fallback to __cacheEvents
                if (string.IsNullOrEmpty(activeFileName))
                {
                    var lastCached = filesList.LastOrDefault(f => f.HasCacheEvents);
                    if (lastCached != null)
                    {
                        activeFileName = lastCached.Name;
                    }
                }

                // Strategy 4: Fallback to first video file in torrent
                if (string.IsNullOrEmpty(activeFileName))
                {
                    var firstVideo = filesList.FirstOrDefault(f =>
                        f.Name.EndsWith(".mkv", StringComparison.OrdinalIgnoreCase) ||
                        f.Name.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase) ||
                        f.Name.EndsWith(".avi", StringComparison.OrdinalIgnoreCase));

                    if (firstVideo != null)
                    {
                        activeFileName = firstVideo.Name;
                    }
                }

                candidates.Add(new StremioCandidate
                {
                    StreamName = streamName,
                    ActiveFileName = activeFileName,
                    SelectionsCount = selectionsCount,
                    DownloadSpeed = downloadSpeed,
                    LatestAccessTime = latestWriteUtc,
                    OrderIndex = index
                });
            }

            if (candidates.Count == 0) return null;

            // Pick the best stream:
            // 1. Actively streaming/downloading (DownloadSpeed > 0)
            // 2. Actively requested pieces (SelectionsCount > 0)
            // 3. Most recently written/accessed on disk (LatestAccessTime)
            // 4. Most recently added stream (OrderIndex)
            var best = candidates
                .OrderByDescending(c => c.DownloadSpeed > 0)
                .ThenByDescending(c => c.SelectionsCount > 0)
                .ThenByDescending(c => c.LatestAccessTime)
                .ThenByDescending(c => c.OrderIndex)
                .FirstOrDefault();

            if (best == null) return null;

            string chosenTitle = !string.IsNullOrEmpty(best.ActiveFileName) ? best.ActiveFileName : best.StreamName;
            if (string.IsNullOrWhiteSpace(chosenTitle)) return null;

            var cleaned = MovieTitleCleaner.Clean(chosenTitle);

            string year = cleaned.Year;
            if (string.IsNullOrEmpty(year) && !string.IsNullOrEmpty(best.StreamName))
            {
                var streamCleaned = MovieTitleCleaner.Clean(best.StreamName);
                if (!string.IsNullOrEmpty(streamCleaned.Year))
                    year = streamCleaned.Year;
            }

            App.Log($"[StremioDetector] Resolved active stream: '{cleaned.Title}' (S{cleaned.Season}E{cleaned.Episode}, {year}) from '{chosenTitle}'");

            return new MediaPlaybackEvent(
                AppName: "Stremio",
                AppId: "stremio",
                RawTitle: chosenTitle,
                CleanTitle: cleaned.Title,
                Year: year,
                Duration: TimeSpan.Zero,
                Season: cleaned.Season,
                Episode: cleaned.Episode,
                IsSeries: cleaned.IsSeries,
                PlayerHwnd: playerHwnd,
                IsManualTrigger: isManual
            );
        }
        catch (Exception ex)
        {
            App.Log($"[StremioDetector] Check failed: {ex.Message}");
        }

        return null;
    }
}
