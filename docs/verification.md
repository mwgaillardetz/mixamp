# Mixamp preview verification

## Build and evidence

Mixamp 0.1.0-preview.1 targets net10.0-windows10.0.22621.0 and win-x64. The build used SDK 10.0.404, runtime and Windows Desktop 10.0.15, Windows SDK projection 10.0.22621.57, Microsoft.Data.Sqlite 10.0.15, SQLitePCLRaw 2.1.13 and TagLibSharp 2.3.0. The published folder contains x64 coreclr, WPF graphics, hostfxr and e_sqlite3 native libraries. The package script checks these and the WinRT projection. No account or server credential was supplied.

The Linux tests use the production catalog, cue store, Plex HTTP adapter, album edit operation, deletion operation, tag checker and replacement journal. They exercise a Python loopback service that generates its own disposable files. A dropped DELETE response exposed an actual replay inside .NET’s HTTP transport. Counting requests on the fixture verified the fix. Permission checks, fresh part enumeration and a second scope check precede the single mutation. A missing metadata item after the request supplies server confirmation.

The published Windows executable also ran under Wine 10.0 with Xvfb. The fixture smoke path starts the normal main window, connects to the authenticated demo, syncs the catalog, selects a row, queues it and switches through all five themes. It opens Song Info through the application command, checks the start and stop boxes, enters 0:42 and 4:08, operates Save, edits a title and rereads the mock server. The screenshots are renders from this executable. Wine required disabling its incomplete Windows.Storage.ApplicationData activation and registering local substitute fonts in an isolated prefix. Some East Asian characters display as missing glyphs in this configuration. Native Windows rendering and audio still require verification.

## Performance on the available host

The packaged WPF benchmark ran on Ubuntu 26.04.1, Wine 10.0 and Xvfb, with an Intel Xeon E5-2650 v3, 40 logical CPUs and about 374 GiB of RAM. The disposable profile was on tmpfs. Wine reported an emulated Windows build 19043. The raw report identifies the measured Release client DLL by SHA-256. Subsequent changes added cloneable media readers, a range read deadline and stronger track/library identity checks. [Raw timings and hardware](wine-benchmark.json)

| Workload | Observed result | Target comparison on this host |
|---|---:|---|
| Initial sync, 100,000 tracks over loopback HTTP | 47.28 s | Separate cold sync workload |
| Warm launch to accepted focus and selection, 30 processes | 3.23 s p95 | Missed 2 s |
| Search to updated rows at dispatcher idle, 120 queries including debounce | 494.5 ms p95 | Missed 150 ms |
| Idle after warm-up, 60 samples | 0.23% total-machine CPU, 504.8 MiB total working set at end | CPU below 1%. Wine lacks a trustworthy private working-set result for the 200 MiB comparison |
| Scroll during sync, 30 s per classic layout | Silver 15.61 ms p95, Amp 12.81 ms p95 | Below 33 ms in Xvfb rendering callbacks. Maximum realized row counts were 19 and 22 |
| First audible sample and cue boundaries | No Windows output measurement | Direct LAN, transcode and ±150 ms cue acceptance pending |

The Linux C# store benchmark separately measured 25.72 s to generate, index and commit 100,000 rows and 44.80 ms p95 to search and deserialize a page. It excludes HTTP, WPF, debounce and rendering. [Raw store samples](linux-store-benchmark.json)

## User journeys

| Journey | Evidence obtained | Outstanding verification |
|---|---|---|
| Plex without SMB | Authenticated JSON paging and byte ranges, indexed searches, Song Info title save/reread, theme selection retention | Hosted browser approval, shared-server/relay access and uninterrupted Windows audio during edits |
| Compilation | Mock multi-disc album reassignment checks its complete membership, effective track performers and the unchanged second album. Independent tag tests edit only album artist. | Real PMS grouping and field-lock behavior on an owner-supplied disposable album, including the server’s response to a partial album operation |
| Cues | Integer parsing, validation, separate preference storage, cache rebuild, identity mismatch, packaged editor save and native MediaPlaybackItem construction | Native direct and transcoded clock origin, audible boundaries, SMTC seeks, repeat and exactly-once advancement at output |
| Radio and playlists | Mock station creation and continuation retain queue item IDs. Duplicate playlist entries can move and be removed separately. Denied edits, revoked credentials and an absent station are exercised. | Live station replenishment and queue/history semantics, transcode decisions and internet stream reconnection on Windows |
| File and Both | Tag edits on four generated files passed independent Mutagen rereads and decoded PCM comparison. Local fault injection forces failure before replacement and loss of acknowledgement after replacement. A reopened journal recognizes the completed work. | Windows ACLs, SMB disconnect, link races, network filesystem replacement, disk-full behavior, Plex refresh and end-to-end retry with both destinations |
| Delete and remove | Disposable physical deletion by the mock service, exact path review, multiple versions, denied deletion, lost acknowledgement/request count, playlist removal retaining media, traversal rejection | Physical deletion via real PMS and independent verification through an accessible Linux-hosted SMB share |
| Large library and accessibility | 100k local-store workload plus packaged Wine workload. Classic layouts recycle rows during active sync. All themes reuse one grid, queue and session. | Windows 11 hardware budgets, private working set, Windows screen reader, text scaling, small windows and 100/150/200% DPI |

## Playback and ownership decisions

Authenticated direct play probes one byte for a consistent HTTP 206 and length. The seekable stream caches at most 256 KiB per reader. The Plex HTTP client refuses redirects and origin changes. HLS fallback for unrestricted tracks uses a separate Windows HTTP filter that checks every destination before adding Plex headers. Cue-restricted playback requires the seekable route. Internet radio uses the native URI source without Plex credentials. The player submits timeline events and leaves history accounting to PMS. The original-versus-clip clock assumption needs the native output probe. [Plex API](https://developer.plex.tv/pms/) · [MediaPlaybackItem](https://learn.microsoft.com/en-us/uwp/api/windows.media.playback.mediaplaybackitem) · [MediaSource.CreateFromStream](https://learn.microsoft.com/en-us/uwp/api/windows.media.core.mediasource.createfromstream)

Plex track title, originalTitle and track number use the track scope. Advertised lock states accompany changes and are checked on reread. The album relationship operation requires one existing, uniquely named destination artist. A whole-album confirmation precedes saving inherited performer overrides and changing the album’s artist ID. These are multiple server operations. A failure can leave already verified overrides in place. [Plex music editing](https://support.plex.tv/articles/205661857-changing-artist-album-or-track-information/) · [Community edit implementation](https://python-plexapi.readthedocs.io/en/latest/_modules/plexapi/mixins/edit.html)

File saves read and hash the selected Plex part before replacing its mapped counterpart. This adds a full media read to a write operation. The staging copy is reopened and checked against format-specific preservation rules. Fixtures include multiple performers, track/disc totals, cover art, a MusicBrainz identifier and ReplayGain. A changed unrelated frame, atom, comment value or encoded payload blocks replacement. MP4 layouts that move chunk offsets can fail this conservative check. Verified backups remain beside the original. UNC directory and file handles must support the required identity, link and sharing checks. [Tag mappings](https://picard-docs.musicbrainz.org/en/latest/appendices/tag_mapping.html) · [Windows ReplaceFile](https://learn.microsoft.com/en-us/windows/win32/api/winbase/nf-winbase-replacefilew)

## Limits of this preview

- Plex discovery selects HTTPS connections, including secure relay. Playback requires one unambiguous media part. HLS negotiation, native codecs and output timing have only been compiled against the Windows SDK here.
- Cues identify a recording using its Plex GUID, duration, part paths and byte sizes. An ID change can reuse a range on one matching catalog identity. Ambiguous matches need manual cue entry. A replacement that preserves every identity attribute can evade detection.
- A custom cue is local to Mixamp. Queue state is saved on a clean close or server switch. Startup opens the last cached library. Reconnect before streaming.
- For metadata, unsupported server field scopes are disabled. Fields displayed by Plex may differ from the embedded file, particularly with locked fields or a different library metadata preference. The refresh operation is asynchronous on the server.
- The column selectors show up to 256 choices per level. Songs and album/artist drill-downs page from SQLite. Large Plex playlists currently use the returned server window. Thumbnails are fetched only for the current playback item. There is no persistent artwork cache.
- The Studio inspector opens the shared editor. Real audio level meters are omitted. The Wine captures exercise selection and layout changes. Full keyboard, high-contrast and focus/scroll continuity at different native scale factors remain open.
- The Windows CI workflow and optional Windows SMB fixture are supplied in source. The workspace started without a Git remote. No hosted CI run, clean Windows installation, live Plex mutation or real SMB save was available to certify.

## Reproduction

Run `pwsh -File tools/package.ps1 -Benchmark` for the service and packaging checks. Use `python tools/tag_fixtures.py` for the independent tag and audio check. [Fixture PCM hashes](fixture-audio-hashes.txt)

Run `python tools/mock_plex.py --count 100000` and `pwsh -File tools/benchmark.ps1` for the packaged application workload on Windows. The script uses a separate benchmark profile, records hardware and executable information, and samples Windows process counters. For audible direct-play boundaries, run the local and authenticated HTTP marker probes with `tools/measure_audio.py` as described in the README. Run `tools/test-smb.ps1` on a disposable Windows fixture host to exercise UNC replacement. A private Plex server needs an expressly designated disposable library for mutation tests.
