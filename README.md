# Mixamp

I wanted my 50K+ track Plex library in one list, like old iTunes. Mixamp loads the whole library, remembers it, and checks for changes the next time I open it.

[Download Mixamp for Windows or Mac](https://github.com/mwgaillardetz/mixamp/releases/latest)

Unzip the download and open `Mixamp.exe` on Windows 11 or `Mixamp.app` on macOS 14 or newer. On Mac, pick the download for your chip (Apple Silicon or Intel). Choose **Library → Connect** and sign in to Plex.

## Using it

- Right-click a column header to show genre, album artist, file path, or the other fields you care about. Click a header to sort.
- Open **Radio** for Plex stations or build a mix around a few artists or albums. **All stations** takes you back while the queue keeps playing.
- Press **Space** to play or pause. Use the arrow keys to browse and **Enter** to play a track.
- Use **Library → Share mapping** to rename files or organize a batch by artist. **Edit → Undo** and **Redo** work for file moves and renames. Deleting tracks from the server is permanent.
- **View → Themes** includes Bitstream 98, my take on classic Winamp. There’s also an optional visualizer and an automatic EQ that falls back to Flat.

## Build

With the .NET SDK from `global.json` and Python installed, run `pwsh -File tools/package.ps1` on Windows or `python3 tools/package-mac.py` on Mac with the Xcode command line tools.
