using System.Drawing;
using System.IO;
using System.Windows;
using System.Windows.Forms;
using System.Windows.Interop;
using Application = System.Windows.Application;

namespace MovieTracker;

public partial class App : Application
{
    private NotifyIcon? _notifyIcon;
    private MediaWatcher? _mediaWatcher;
    private HotkeyManager? _hotkeyManager;
    private OverlayWindow? _currentOverlay;
    private static Mutex? _singleInstanceMutex;

    public static void Log(string message)
    {
        try
        {
            File.AppendAllText("app_log.txt", $"[{DateTime.Now:HH:mm:ss}] {message}\n");
        }
        catch { }
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        AppDomain.CurrentDomain.UnhandledException += (s, args) =>
        {
            try { File.AppendAllText("app_crash.log", $"Unhandled: {args.ExceptionObject}\n"); } catch { }
        };

        DispatcherUnhandledException += (s, args) =>
        {
            try { File.AppendAllText("app_crash.log", $"Dispatcher: {args.Exception}\n"); } catch { }
            args.Handled = true;
        };

        // Guard against multiple instances locking hotkeys
        _singleInstanceMutex = new Mutex(true, @"Local\MovieTracker_SingleInstance_Mutex_9A4B8C", out bool isNewInstance);
        if (!isNewInstance)
        {
            try { File.AppendAllText("app_log.txt", "Another instance of MovieTracker is already running. Exiting.\n"); } catch { }
            Shutdown();
            return;
        }

        File.WriteAllText("app_log.txt", "Starting MovieTracker...\n");

        try
        {
            LetterboxdDiaryService.EnsureFileExists();

            // 1. Setup Hotkeys
            SetupHotkeyListener();

            // 2. Setup System Tray Icon
            SetupSystemTray();

            // 3. Initialize Media Watcher
            _mediaWatcher = new MediaWatcher();
            _mediaWatcher.OnMovieDetected += info =>
            {
                Dispatcher.Invoke(() => ShowOverlay(info));
            };

            _ = Task.Run(async () =>
            {
                await _mediaWatcher.StartAsync();
                File.AppendAllText("app_log.txt", "MediaWatcher started.\n");
            });

            string hotkeyLabel = _hotkeyManager?.ActiveHotkeyNames.FirstOrDefault() ?? "Ctrl+Shift+M";
            _notifyIcon?.ShowBalloonTip(
                3000,
                "Movie Tracker Active",
                $"Press {hotkeyLabel} (or double-click tray) anytime while watching to log!",
                ToolTipIcon.Info
            );
        }
        catch (Exception ex)
        {
            File.AppendAllText("app_log.txt", $"Startup error: {ex}\n");
        }
    }

    private void SetupHotkeyListener()
    {
        try
        {
            _hotkeyManager = new HotkeyManager();
            _hotkeyManager.HotkeyPressed += OnHotkeyPressed;
            _hotkeyManager.RegisterHotkeys();

            string registeredList = string.Join(", ", _hotkeyManager.ActiveHotkeyNames);
            File.AppendAllText("app_log.txt", $"Hotkeys registered: {registeredList}\n");
        }
        catch (Exception ex)
        {
            File.AppendAllText("app_log.txt", $"[Hotkey] Setup error: {ex.Message}\n");
        }
    }

    private async void OnHotkeyPressed()
    {
        File.AppendAllText("app_log.txt", "[Hotkey] Pressed! Triggering manual log...\n");
        await TriggerManualLogAsync();
    }

    private async Task TriggerManualLogAsync()
    {
        if (_mediaWatcher == null) return;

        var playback = await _mediaWatcher.CaptureCurrentAsync(isManual: true);
        if (playback != null)
        {
            Dispatcher.Invoke(() => ShowOverlay(playback));
        }
        else
        {
            App.Log("[Trigger] No media returned by CaptureCurrentAsync");
            _notifyIcon?.ShowBalloonTip(
                2000,
                "No Media Playing",
                "Start a video in Stremio, Firefox, Chrome, or VLC, then try again.",
                ToolTipIcon.Warning
            );
        }
    }

    private void ShowOverlay(MediaPlaybackEvent info)
    {
        try
        {
            // If an overlay is already currently open and being viewed by the user,
            // never let a background auto-poll interrupt or switch away from it!
            if (_currentOverlay != null && _currentOverlay.IsLoaded)
            {
                if (!info.IsManualTrigger)
                {
                    // Ignore background auto-poll so we don't disrupt the user
                    return;
                }
                _currentOverlay.Close();
            }

            _mediaWatcher?.MarkAsSeen(info.RawTitle);

            File.AppendAllText("app_log.txt", $"[Overlay] Showing overlay for: {info.CleanTitle} ({info.Year}) from {info.AppName} (Manual: {info.IsManualTrigger})\n");

            _currentOverlay = new OverlayWindow();
            _currentOverlay.Populate(info);
            _currentOverlay.Show();
        }
        catch (Exception ex)
        {
            File.AppendAllText("app_log.txt", $"[Overlay] Show error: {ex}\n");
        }
    }

    private void SetupSystemTray()
    {
        var contextMenu = new ContextMenuStrip();

        string hotkeyLabel = _hotkeyManager?.ActiveHotkeyNames.FirstOrDefault() ?? "Ctrl+Shift+M / Ctrl+Alt+L";
        var logCurrentItem = new ToolStripMenuItem($"🎬 Log Current Media ({hotkeyLabel})", null, async (s, e) =>
        {
            await TriggerManualLogAsync();
        });

        var testSampleItem = new ToolStripMenuItem("✨ Test Overlay (Sample Movie)", null, (s, e) =>
        {
            ShowOverlay(new MediaPlaybackEvent(
                AppName: "VLC Media Player",
                AppId: "vlc",
                RawTitle: "Dune.Part.Two.2024.2160p.UHD.BluRay.x265.mkv",
                CleanTitle: "Dune Part Two",
                Year: "2024",
                Duration: TimeSpan.FromMinutes(166),
                IsManualTrigger: true
            ));
        });

        var testSeriesItem = new ToolStripMenuItem("📺 Test Overlay (Sample TV Series)", null, (s, e) =>
        {
            ShowOverlay(new MediaPlaybackEvent(
                AppName: "VLC Media Player",
                AppId: "vlc",
                RawTitle: "Severance.S01E03.1080p.WEB-DL.mkv",
                CleanTitle: "Severance",
                Year: "2022",
                Duration: TimeSpan.FromMinutes(54),
                Season: "1",
                Episode: "3",
                IsSeries: true,
                IsManualTrigger: true
            ));
        });

        var openCsvItem = new ToolStripMenuItem("📄 Open Diary CSV", null, (s, e) =>
        {
            LetterboxdDiaryService.OpenDiaryFile();
        });

        var openFolderItem = new ToolStripMenuItem("📁 Open Diary Folder", null, (s, e) =>
        {
            LetterboxdDiaryService.OpenDiaryFolder();
        });

        var exitItem = new ToolStripMenuItem("❌ Exit", null, (s, e) =>
        {
            ExitApplication();
        });

        contextMenu.Items.Add(logCurrentItem);
        contextMenu.Items.Add(testSampleItem);
        contextMenu.Items.Add(testSeriesItem);
        contextMenu.Items.Add(new ToolStripSeparator());
        contextMenu.Items.Add(openCsvItem);
        contextMenu.Items.Add(openFolderItem);
        contextMenu.Items.Add(new ToolStripSeparator());
        contextMenu.Items.Add(exitItem);

        _notifyIcon = new NotifyIcon
        {
            Icon = CreateAppIcon(),
            Text = $"Movie Tracker ({hotkeyLabel})",
            ContextMenuStrip = contextMenu,
            Visible = true
        };

        _notifyIcon.DoubleClick += async (s, e) =>
        {
            await TriggerManualLogAsync();
        };
    }

    private static Icon CreateAppIcon()
    {
        using var bmp = new Bitmap(32, 32);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;

            using var bgBrush = new SolidBrush(Color.FromArgb(20, 24, 28));
            g.FillEllipse(bgBrush, 1, 1, 30, 30);

            using var borderPen = new Pen(Color.FromArgb(0, 224, 84), 2);
            g.DrawEllipse(borderPen, 2, 2, 28, 28);

            using var playBrush = new SolidBrush(Color.FromArgb(0, 224, 84));
            PointF[] triangle =
            [
                new PointF(12, 10),
                new PointF(23, 16),
                new PointF(12, 22)
            ];
            g.FillPolygon(playBrush, triangle);
        }

        IntPtr hIcon = bmp.GetHicon();
        return (Icon)Icon.FromHandle(hIcon).Clone();
    }

    private void ExitApplication()
    {
        if (_notifyIcon != null)
        {
            _notifyIcon.Visible = false;
            _notifyIcon.Dispose();
        }

        _hotkeyManager?.Dispose();

        if (_singleInstanceMutex != null)
        {
            try { _singleInstanceMutex.ReleaseMutex(); } catch { }
            _singleInstanceMutex.Dispose();
            _singleInstanceMutex = null;
        }

        Shutdown();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        ExitApplication();
        base.OnExit(e);
    }
}
