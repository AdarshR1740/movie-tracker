using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Threading;
using Button = System.Windows.Controls.Button;
using Color = System.Windows.Media.Color;
using ColorConverter = System.Windows.Media.ColorConverter;

namespace MovieTracker;

public partial class OverlayWindow : Window
{
    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_NOACTIVATE = 0x08000000;
    private const int WS_EX_TOOLWINDOW = 0x00000080;
    private const int WS_EX_TOPMOST = 0x00000008;

    [StructLayout(LayoutKind.Sequential)]
    public struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
        public int Width => Right - Left;
        public int Height => Bottom - Top;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

    [DllImport("user32.dll")]
    private static extern bool IsIconic(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll")]
    private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

    private readonly DispatcherTimer _dismissTimer;
    private int _remainingSeconds = 25;
    private const int TotalSeconds = 25;
    private bool _isPaused = false;
    private int _selectedRating = 0;
    private string _sourceApp = "";
    private IntPtr _playerHwnd = IntPtr.Zero;

    public OverlayWindow()
    {
        InitializeComponent();

        _dismissTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(1)
        };
        _dismissTimer.Tick += DismissTimer_Tick;
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);

        PositionOverPlayerOrScreen();

        // WS_EX_TOOLWINDOW prevents taskbar/alt-tab button; WS_EX_TOPMOST keeps it on top
        var helper = new WindowInteropHelper(this);
        int exStyle = GetWindowLong(helper.Handle, GWL_EXSTYLE);
        SetWindowLong(helper.Handle, GWL_EXSTYLE, exStyle | WS_EX_TOOLWINDOW | WS_EX_TOPMOST);

        StartIlluminatedBorderAnimation();
    }

    private void StartIlluminatedBorderAnimation()
    {
        try
        {
            // Rapid, vivid rotating neon gradient along perimeter
            var rotateAnim = new DoubleAnimation
            {
                From = 0,
                To = 360,
                Duration = TimeSpan.FromSeconds(3.5),
                RepeatBehavior = RepeatBehavior.Forever
            };
            BorderGlowAngle.BeginAnimation(RotateTransform.AngleProperty, rotateAnim);

            // Pulsing luminous outer halo that fades smoothly into transparency
            var opacityAnim = new DoubleAnimation
            {
                From = 0.55,
                To = 0.95,
                Duration = TimeSpan.FromSeconds(2.0),
                AutoReverse = true,
                RepeatBehavior = RepeatBehavior.Forever
            };
            OuterGlow.BeginAnimation(DropShadowEffect.OpacityProperty, opacityAnim);

            var blurAnim = new DoubleAnimation
            {
                From = 16,
                To = 28,
                Duration = TimeSpan.FromSeconds(2.0),
                AutoReverse = true,
                RepeatBehavior = RepeatBehavior.Forever
            };
            OuterGlow.BeginAnimation(DropShadowEffect.BlurRadiusProperty, blurAnim);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[Overlay] Animation error: {ex.Message}");
        }
    }

    private void PositionOverPlayerOrScreen()
    {
        bool positionedOverPlayer = false;

        if (_playerHwnd != IntPtr.Zero && !IsIconic(_playerHwnd))
        {
            if (GetWindowRect(_playerHwnd, out RECT rect) && rect.Width > 200 && rect.Height > 200)
            {
                // Position at the bottom-right inside the player's bounding box
                double targetLeft = rect.Right - Width - 20;
                double targetTop = rect.Bottom - Height - 20;

                // Ensure it does not go beyond the left/top edges of the player
                if (targetLeft < rect.Left) targetLeft = rect.Left + 16;
                if (targetTop < rect.Top) targetTop = rect.Top + 16;

                Left = targetLeft;
                Top = targetTop;
                positionedOverPlayer = true;
            }
        }

        if (!positionedOverPlayer)
        {
            // Fallback: bottom-right of primary screen
            var workArea = SystemParameters.WorkArea;
            Left = workArea.Right - Width - 16;
            Top = workArea.Bottom - Height - 16;
        }
    }

    public void Populate(MediaPlaybackEvent info)
    {
        _sourceApp = info.AppName;
        _playerHwnd = info.PlayerHwnd;

        TxtSourceBadge.Text = string.IsNullOrEmpty(info.AppName) ? "🎬 Media Detected" : $"🎬 {info.AppName}";
        
        if (info.Duration > TimeSpan.Zero)
        {
            TxtDuration.Text = $"({(int)info.Duration.TotalMinutes} min)";
        }
        else
        {
            TxtDuration.Text = "";
        }

        TxtTitle.Text = info.CleanTitle;
        TxtYear.Text = info.Year;
        TxtSeason.Text = info.Season;
        TxtEpisode.Text = info.Episode;
        TxtDate.Text = DateTime.Today.ToString("yyyy-MM-dd");

        _selectedRating = 0;
        UpdateStarDisplay();

        // Re-evaluate positioning now that we have player HWND
        if (IsLoaded)
        {
            PositionOverPlayerOrScreen();
        }

        // Start countdown
        _remainingSeconds = TotalSeconds;
        ProgressDismiss.Value = 100;
        _isPaused = false;
        _dismissTimer.Stop();
        _dismissTimer.Start();
    }

    private void DismissTimer_Tick(object? sender, EventArgs e)
    {
        if (_isPaused) return;

        _remainingSeconds--;
        ProgressDismiss.Value = (_remainingSeconds / (double)TotalSeconds) * 100;

        if (_remainingSeconds <= 0)
        {
            _dismissTimer.Stop();
            Close();
        }
    }

    private void Window_MouseEnter(object sender, System.Windows.Input.MouseEventArgs e)
    {
        _isPaused = true;
    }

    private void Window_MouseLeave(object sender, System.Windows.Input.MouseEventArgs e)
    {
        _isPaused = false;
    }

    private void Window_MouseDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        _isPaused = true;
    }

    private void Star_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && int.TryParse(btn.Tag?.ToString(), out int rating))
        {
            if (_selectedRating == rating)
                _selectedRating = 0;
            else
                _selectedRating = rating;

            UpdateStarDisplay();
        }
    }

    private void UpdateStarDisplay()
    {
        Button[] stars = [Star1, Star2, Star3, Star4, Star5];
        var activeColor = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#00E054"));
        var inactiveColor = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#3E4B59"));

        for (int i = 0; i < stars.Length; i++)
        {
            stars[i].Foreground = (i < _selectedRating) ? activeColor : inactiveColor;
        }

        TxtRatingVal.Text = _selectedRating > 0 ? $"{_selectedRating} ★" : "";
    }

    private void BtnSave_Click(object sender, RoutedEventArgs e)
    {
        _dismissTimer.Stop();

        string title = TxtTitle.Text.Trim();
        if (string.IsNullOrWhiteSpace(title))
        {
            TxtTitle.Focus();
            return;
        }

        string year = TxtYear.Text.Trim();
        string season = TxtSeason.Text.Trim();
        string episode = TxtEpisode.Text.Trim();
        string date = TxtDate.Text.Trim();
        string time = DateTime.Now.ToString("HH:mm:ss");
        string ratingStr = _selectedRating > 0 ? _selectedRating.ToString() : "";

        var entry = new DiaryEntry(title, year, ratingStr, date, season, episode, _sourceApp, time);
        LetterboxdDiaryService.AppendEntry(entry);

        // Feedback animation
        BtnSave.Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#00C030"));
        BtnSave.Content = "✓ Logged to Diary!";
        BtnSave.IsEnabled = false;

        var closeDelay = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(750) };
        closeDelay.Tick += (s, args) =>
        {
            closeDelay.Stop();
            Close();
        };
        closeDelay.Start();
    }

    private void BtnDismiss_Click(object sender, RoutedEventArgs e)
    {
        _dismissTimer.Stop();
        Close();
    }
}
