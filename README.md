# Movie Tracker for PC (Letterboxd Scrobbler)

A lightweight Windows background utility that monitors media playback from desktop players (like **Stremio**, **VLC**, **MPC-HC**, **MPV**) and web browsers (like **YouTube on Firefox, Chrome, Edge, Brave**), automatically popping up a sleek, frameless overlay **directly over the video player** to confirm details and save your watched history to a **Letterboxd-ready CSV diary**.

---

## ✨ Features

- **Context-Aware In-Player Overlay:**
  - Pops up **directly over the active media player** instead of a fixed monitor corner.
  - Automatically adapts to windowed or fullscreen playback on any monitor.
  - Does not steal focus from your fullscreen video upon popup, but clicking inside activates editable fields.
  - **Smoked Glass & Animated Neon Border:** High-contrast frosted dark obsidian card with an animated rotating gradient illumination.
- **Movies & TV Series Support:**
  - Auto-detects Season and Episode tags (e.g. `S04E02`, `1x05`, `Season 1 Episode 2`).
  - Includes dedicated **Season** and **Episode** fields in the popup (left blank for movies).
  - Fully editable fields: Title, Year, Season, Episode, Date.
- **Automatic Playback Detection:**
  - **Stremio Integration:** Real-time stream resolution down to the exact episode using local streaming API and disk cache heuristics.
  - **Classic Desktop Players:** Direct process and window inspection for **VLC 3.0**, **MPC-HC**, **MPV**, **PotPlayer**.
  - **Web Browsers:** Windows GSMTC (Global System Media Transport Controls) support for **Firefox, Chrome, Edge, Brave**.
  - **YouTube Filter:** Auto-popup only triggers for media $\ge 40$ minutes, filtering out short clips and tutorials.
- **Letterboxd-Ready CSV Logging:**
  - Directly appends to `Documents\MovieTracker\letterboxd_diary.csv`.
  - Schema: `Title,Type,Year,Season,Episode,Rating,WatchedDate,WatchedTime,Source,Tags`.
  - Tags automatically include `SxxExx` for TV episodes.
- **Global Hotkeys:**
  - **`Ctrl + Shift + M`** or **`Ctrl + Alt + L`**: Press anytime to immediately capture active media and pop up the card.
- **System Tray Icon:**
  - Runs silently with minimal RAM (~30MB).
  - Context menu options:
    - 🎬 **Log Current Media (Ctrl+Shift+M)**
    - ✨ **Test Overlay (Sample Movie)**
    - 📺 **Test Overlay (Sample TV Series)**
    - 📄 **Open Diary CSV**
    - 📁 **Open Diary Folder**
    - ❌ **Exit**

---

## 🚀 How to Run

### Published Executable
Run:
```
publish\MovieTracker.exe
```
Or start via .NET CLI:
```powershell
cd "MovieTracker"
dotnet run
```

---

## 📥 Importing to Letterboxd

1. Open your diary CSV at any time via the tray icon: **📄 Open Diary CSV** (or browse to `Documents\MovieTracker\letterboxd_diary.csv`).
2. Inspect or edit entries before importing.
3. In Letterboxd $\rightarrow$ **Settings** $\rightarrow$ **Import & Export** $\rightarrow$ **Import Your Data**, select `letterboxd_diary.csv` and import!
