# Cinema3D 1.0.2 — 3D cinema player for Windows

Portable Windows app for SBS/OU 3D video and a 120 Hz DLP-Link projector. The main player is a self-contained Windows executable; the source is C# and can be edited in Cursor.

## Download and install

1. Open the [latest release](../../releases/latest) and download **Cinema3D-Windows-x64.zip**. GitHub's automatic “Source code” ZIP does not contain the ready EXE.
2. Extract the ZIP and run `INSTALL-ON-LAPTOP.cmd`. The installer copies the app to `%USERPROFILE%\3D-Cinema`, downloads FFmpeg and yt-dlp from their official releases, and creates desktop shortcuts. Internet is required for this first installation.
3. Start **3D Кинотеатр** from the desktop. Put SBS/OU video files in `movies`, or choose **Открыть файл** in the app.

For DLP-Link glasses, select the projector in the app, set it to 120 Hz in Windows, enable DLP 3D in the projector, and use **Тест очков**. This hardware mode must be checked on the actual laptop and projector. A normal monitor can run the 2D and anaglyph modes.

For a browser video, choose **Браузер → проектор**, select the browser screen and the video's layout (SBS for side-by-side). Browser playback and audio stay in the browser. For a local movie, you can choose an embedded audio track or add a separate MP3/WAV file using **Выбрать MP3/WAV…**; it is copied into `movies/audio` for portability.

## Edit and rebuild

Open `%USERPROFILE%\3D-Cinema` in Cursor. The main app is in `native\Cinema3D\*.cs`; the browser fallback is in `app\`. Close the player, edit the code, then double-click `BUILD-APP.cmd`. The script uses an installed .NET 10 SDK or downloads a local copy on first use, builds a new `Cinema3D.exe`, and saves the previous one as `Cinema3D.exe.bak`. Restart the desktop shortcut.

To keep `git pull` available in Cursor, clone this repository into an empty `%USERPROFILE%\3D-Cinema` folder **before** running the release ZIP installer. The installer copies the EXE and tools into that folder and leaves `.git` intact. If an old installation already uses the folder, back up your movies and settings before replacing it.

## Limits

- Windows 10/11 x64 is required. The projector, cable, graphics driver, and 120 Hz mode determine whether active glasses work.
- Internet streaming depends on the source website. DRM-protected video cannot be captured.
- The release contains no movies, personal settings, or browser profile.

## Third-party tools

FFmpeg and yt-dlp are downloaded from their publishers during installation and remain under their own licenses. See [THIRD-PARTY.md](THIRD-PARTY.md).
