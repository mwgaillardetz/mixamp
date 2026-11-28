# Mixamp

I wanted my 50K+ track Plex library in one list, like old iTunes. Mixamp loads the whole library, remembers it, and checks for changes the next time I open it.

[Download Mixamp for Windows or Mac](https://github.com/mwgaillardetz/mixamp/releases/latest)

Unzip the download and open `Mixamp.exe` on Windows 11 or `Mixamp.app` on macOS 14 or newer. On Mac, pick `mac-silicon` for an M-series Mac or `mac-intel` for an Intel Mac. Click **Add Plex Library** in the sidebar (Windows: **Library → Connect**), sign in through your browser, then choose your server and music library.

## Using it

- Right-click a track to start **Sonically similar radio** using Plex sonic analysis. The station includes the seed and up to 29 unique matches. Right-click one or more tracks to **Edit…** metadata or **Add to playlist**. When you select several tracks, Mixamp confirms the track count before opening the editor. Save applies only the fields you changed to those tracks.
- Turn on **View → Glass appearance** for a translucent player and sidebar. The Now Playing spectrum uses the right half of the display.
- Songs and playlists scroll continuously on Mac and Windows. Search words can appear in any order, including across the title, artist, and album. Mixamp fetches nearby tracks from Plex as you browse, search, or sort. A single SQLite entry stores up to 128 tracks and the last known total so your saved sort can appear during reconnect. Searching, sorting, and scrolling through the full library use the connected server.
- On Mac, **Library**, **Edit**, **View**, and **EQ** are in the macOS menu bar. Double-click a track to play it.

- Right-click a column header to show genre, album artist, file path, or the other fields you care about. Click a header to sort.
- Open **Radio** for Plex stations or build a mix around a few artists or albums. **All stations** takes you back while the queue keeps playing.
- Choose **Cast → Google Cast** to play the current track and the rest of Up Next on a nearby Cast speaker. Keep Mixamp open while casting. On macOS, **Cast → AirPlay via Mac Sound output** opens Sound settings, where choosing an AirPlay output routes your Mac’s audio to that speaker.
- Press **Space** to play or pause. Use the arrow keys to browse and **Enter** to play a track.
- Use **Library → Share mapping** to rename files or organize a batch by artist. **Edit → Undo** and **Redo** work for file moves and renames. Deleting tracks from the server is permanent.
- **View → Themes** includes Bitstream 98, my take on classic Winamp, alongside Dracula, Nord, and Solarized Light. Choose **View → Background visualizer** for a pixel spectrum, mirrored peaks, or a dot matrix behind Now Playing. **Off** is the default.

## Build

With the .NET SDK from `global.json` and Python installed, run `pwsh -File tools/package.ps1` on Windows or `python3 tools/package-mac.py` on Mac with the Xcode command line tools. For macOS builds distributed as updates, set `MIXAMP_SIGN_IDENTITY` to the same Developer ID Application identity on each build so Keychain can recognize the updated app. For repeated local development builds, run `python3 tools/package-mac.py --local osx-arm64` (use `osx-x64` on Intel). This uses the local signing identity and writes the build under `artifacts/local/`. The standard packaging command defaults to ad-hoc signing. Mixamp reads the saved login at launch. If macOS asks to approve an updated copy, **Always Allow** lets Keychain remember that copy’s signing identity. Canceling leaves the saved login available for a later retry.
