![Mixamp mixtape](docs/mixtape.png)

# Mixamp

Mixamp is a native Windows 11 x64 music library editor and player for Plex. This build is a preview. Windows audio, live Plex behavior and SMB replacement still require acceptance testing.

## Run

Extract the entire ZIP to a local directory and launch **Mixamp.exe**. Windows 11 22H2 or newer is the build target. The folder contains its .NET runtime. Standard user permissions suffice.

Choose **Library → Connect**, approve the strong PIN in Plex’s hosted sign-in page, then select your server and music library. Mixamp tries secure direct connections before relay. The device identifier persists across launches. Windows DPAPI protects the account token under your user profile. Plex supplies the connection token for each selected resource.

To use the loopback fixture, run the following in the extracted folder with Python 3 installed, then choose **Library → Demo**. The server generates temporary audio and accepts only the mock client credential. Each new process starts with a fresh disposable library.

```powershell
python mock_plex.py
```

Double-click a song or select it and press Enter. Ctrl+F focuses search. Ctrl+I opens Song Info while the current session continues playing. Playback and the row context menu include queue actions, full-track playback, repeat and radio. Under View, choose a theme, enable the column browser or toggle the player strip. Plex station availability depends on the server. Internet Radio accepts a direct HTTP(S) audio URL.

## Save changes

- **Plex library** edits the selected server fields and rereads them. Track artist uses the track override. Album artist reassignment requires one existing destination artist and a whole-album review. Previously inherited performers are materialized before changing the album relationship. Unsupported field controls are disabled. Test relationship edits on a disposable album with your PMS version first.
- **Audio files** requires **Library → Share mapping**. Enter the root exactly as Plex reports it, then the corresponding writable Windows UNC directory. Each mapping belongs to the selected account, server and section. Song Info stages a sibling copy, validates tags and encoded audio, then uses Windows file replacement. A successful write retains a sibling backup. Budget space for the staging copy and backup. Other tag readers can read the updated embedded fields. Plex’s embedded-tag preference and field locks can affect its display.
- **Both** verifies file and Plex work separately. Keep the editor open to retry unfinished track field changes. Album relationship edits have a separate preview and verification step. Read each result before retrying an interrupted save.
- **Playback** stores independent start and stop values locally. Enter `0:42` and `4:08` for a 3:26 interval. Preview, use the current position, or reset to the full track. Save to keep the change across launches. The settings belong to Mixamp.
- **File** lists Plex’s paths, sizes and reported format. Multi-version or multipart media is read-only for tagging and unsupported for direct playback.

**Remove from queue** and **Remove from playlist** edit their respective lists. **Delete file from server** reviews fresh server paths and requires a separate confirmation. The server’s effective deletion capability and media-deletion preference are checked again at commit. A lost acknowledgement triggers a metadata reread.

## Build, test and package

From the source root, install the SDK specified in global.json, Python 3.10+ and PowerShell 7. The command starts a disposable fixture, runs the portable integration suite, publishes the self-contained app, checks required native libraries, and on Windows launches the published executable to render five theme screenshots. Keep loopback port 32491 free.

```powershell
pwsh -File tools/package.ps1 -Benchmark
```

Outputs are `artifacts/mixamp-win-x64/Mixamp.exe`, `artifacts/Mixamp-win-x64.zip`, the raw store benchmark JSON and, on Windows, `artifacts/screenshots`. The GitHub Actions workflow uploads these artifacts on a Windows runner. An SDK-equipped runner exercises the published folder. The clean-machine test still needs a Windows installation without a preinstalled runtime.

To repeat the independent audio/tag checks after building:

```powershell
python -m pip install mutagen==1.47.0 imageio-ffmpeg==0.6.0
python tools/tag_fixtures.py
```

To regenerate a local 4:20 marker recording, run `python tools/mock_plex.py --marker marker.wav` from source and open **Library → Local clip**. The pitch changes at 0:42 and 4:08. The demo HTTP source serves the same tone sequence and requires authenticated byte ranges. For a large fixture, use `python tools/mock_plex.py --count 100000`.

Local catalog and preferences live under `%LOCALAPPDATA%\Mixamp`. Close the application before rebuilding its catalog. Retain preferences.db and its SQLite companion files when moving data. See docs/verification.md in the source repository for evidence and outstanding acceptance work.

## Windows acceptance measurements

With the 100k mock running, execute the following from the source root. The benchmark uses its own profile. It records thirty fresh processes, 120 visible searches, idle and minimized samples, and a 30-second scroll during sync in each classic layout.

```powershell
pwsh -File tools/benchmark.ps1
```

On a Windows desktop with a working sound endpoint, run the output test with the default mock running and other audio stopped. It takes about seven minutes for the two complete 3:26 intervals. It records the original clock samples and measures the marker tones through WASAPI loopback. The local and authenticated HTTP results are separate.

```powershell
python -m pip install numpy soundcard
python tools/measure_audio.py artifacts/mixamp-win-x64/Mixamp.exe artifacts/audio
```

An administrator on a disposable Windows fixture host can run `pwsh -File tools/test-smb.ps1` to create a temporary local SMB share, exercise the file writer, verify its backup, and remove the fixture. This privilege is specific to hosting the test share.

The published preview’s service checks, screenshots, measured headless budgets and remaining Windows/Plex/SMB acceptance work are recorded in the [verification report](docs/verification.md). The first four mock tracks have individual disposable source files. Other generated rows share the marker recording and reject deletion.

The root Mixamp.exe launches the self-contained player in app/. Keep that directory alongside the launcher when copying the release. Packaging installs Zig 0.13.0 in a build-only Python virtual environment.
