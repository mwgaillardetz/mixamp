"""Disposable Plex protocol fixture. All mutating routes only affect this process's temp dir."""
import argparse
import copy
import json
import math
from pathlib import Path
import re
import shutil
import struct
import tempfile
import threading
import wave
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from urllib.parse import parse_qs, unquote, urlsplit


def marker(path, seconds=260):
    with wave.open(str(path), "wb") as out:
        out.setparams((1, 2, 8000, 0, "NONE", ""))
        for second in range(seconds):
            hz = 220 if second < 42 else 880 if second < 248 else 330
            out.writeframes(struct.pack("<8000h", *(round(3200 * math.sin(2 * math.pi * hz * i / 8000)) for i in range(8000))))


class Library:
    def __init__(self, root, count, options):
        self.root, self.count, self.options = root, count, options
        self.lock = threading.RLock()
        self.deleted, self.changes, self.playlists = set(), {}, {}
        self.delete_requests = 0
        self.retry_attempts = 0
        self.redirect_hits = 0
        self.artist = {"a": "The Session Band", "b": "The Session Band"}
        self.timeline, self.entry = [], 0
        self.wav = root / "fixture.wav"
        marker(self.wav)
        self.versions = {}
        for i in range(1, min(count, 4) + 1):
            path = root / f"track-{i}.wav"
            shutil.copyfile(self.wav, path)
            self.versions[i] = path
        if getattr(options,"flac_fixture",False):
            source=Path(__file__).resolve().parents[1]/"tests"/"fixtures"/"flac.flac"
            dest=root/"track-1.flac"
            shutil.copyfile(source,dest)
            self.versions[1]=dest

    def track(self, i):
        album = "a" if i % 4 != 0 else "b"
        path = self.versions.get(i, self.wav)
        titles = ["Signal Room", "Night Ferry", "Open Rehearsal", "After the Rain", "Blue Lantern", "Paper Moon", "Second Take", "Pacific Avenue", "Back of the Hall", "North Window", "First Light", "Marigold"]
        title = titles[(i-1) % len(titles)] + (f" · Take {(i-1)//len(titles)+1}" if i>len(titles) else "")
        t = dict(ratingKey=str(i), updatedAt=1, type="track", librarySectionID="1", title=title, originalTitle=["Mira", "池田", "Léon"][i % 3],
                    parentRatingKey=album, parentTitle="Room Tapes" if album == "a" else "Studio B",
                    grandparentRatingKey="v" if self.artist[album] == "Various Artists" else "g",
                    grandparentTitle=self.artist[album], parentIndex=i % 2 + 1, index=i, year=2024, duration=260000,
                    guid=f"demo://recording/{i}", Genre=[dict(tag="Jazz")],
                    Media=[dict(audioCodec="pcm", container="wav", bitrate=128, Part=[dict(id=str(i), key=f"/parts/{i}", file="/demo/"+path.name, size=path.stat().st_size)])]) | self.changes.get(i, {})
        if getattr(self.options,"flac_fixture",False) and i==1:
            t["Media"][0].update(container="flac",audioCodec="flac")
            t["Media"][0]["Part"][0]["file"]="/demo/"+self.root.name+"/"+path.name
        if self.options.multiple and i == 1:
            other = copy.deepcopy(t["Media"][0])
            other["Part"][0].update(id="2",key="/parts/2",file="/demo/track-2.wav")
            t["Media"].append(other)
        return t


def handler(lib):
    class Handler(BaseHTTPRequestHandler):
        def log_message(self, *_):
            pass

        def respond(self, code, data=None):
            payload = json.dumps({"MediaContainer": data or {}}).encode()
            self.send_response(code)
            self.send_header("Content-Type", "application/json")
            self.send_header("Content-Length", str(len(payload)))
            self.end_headers()
            self.wfile.write(payload)

        def route(self):
            url = urlsplit(self.path)
            path = url.path
            q = {k: v[0] for k, v in parse_qs(url.query, keep_blank_values=True).items()}
            if path == "/fixture/redirected":
                lib.redirect_hits+=1
                return self.respond(200)
            if self.headers.get("X-Plex-Token") != "fixture-only" or lib.options.revoked:
                return self.respond(401)
            if self.command != "GET" and lib.options.deny_writes:
                return self.respond(403)
            if path == "/":
                return self.respond(200, dict(machineIdentifier="mixamp-demo", allowMediaDeletion=not lib.options.deny_writes))
            if path == "/fixture/redirect":
                self.send_response(302)
                self.send_header("Location",f"http://localhost:{self.server.server_port}/fixture/redirected")
                self.end_headers()
                return
            if path == "/fixture/counters":
                return self.respond(200,dict(retries=lib.retry_attempts,redirects=lib.redirect_hits))
            if path == "/fixture/rate":
                lib.retry_attempts+=1
                if lib.retry_attempts<3:
                    self.send_response(429)
                    self.send_header("Retry-After","0")
                    self.end_headers()
                    return
                return self.respond(200,dict(ready=True))
            if path == "/fixture/deletes":
                return self.respond(200,dict(count=lib.delete_requests))
            if path == "/identity":
                return self.respond(200, dict(machineIdentifier="mixamp-demo"))
            if path == "/library/sections":
                return self.respond(200, dict(Directory=[dict(type="artist", key="1", title="Demo · Listening room")]))
            if path == "/:/prefs":
                return self.respond(200, dict(Setting=[dict(id="allowMediaDeletion", value=not lib.options.deny_writes)]))
            if path == "/:/timeline":
                lib.timeline.append({k: q.get(k) for k in ("ratingKey", "time", "state", "playQueueItemID")})
                return self.respond(200)
            if path == "/library/sections/1/all":
                kind = q.get("type")
                if q.get("includeMeta") == "1":
                    filters = [dict(filter="track.genre", key="/library/sections/1/genre?type=10")]
                    return self.respond(200, dict(Meta=dict(Type=[dict(type="track", Filter=filters)])))
                if self.command == "PUT":
                    if kind == "9":
                        lib.artist[q["id"]] = {"v": "Various Artists", "g": "The Session Band"}[q["artist.id.value"]]
                    else:
                        target = lib.changes.setdefault(int(q["id"]), {})
                        for key, value in q.items():
                            if key.endswith(".value"):
                                target[key[:-6]] = value
                    return self.respond(200)
                if kind in ("8", "9"):
                    groups = [("v", "Various Artists"), ("g", "The Session Band")] if kind == "8" else [("a", "Room Tapes"), ("b", "Studio B")]
                    return self.respond(200, dict(Metadata=[dict(ratingKey=k, title=t) for k, t in groups if not q.get("title") or q["title"] == t]))
                start, size = int(q.get("X-Plex-Container-Start", 0)), int(q.get("X-Plex-Container-Size", 256))
                if getattr(lib.options,"partial_scan",False):
                    if start:
                        return self.respond(503)
                    size=2
                ids = [i for i in range(1, lib.count + 1) if i not in lib.deleted] if lib.deleted else range(1,lib.count+1)
                rows = [lib.track(i) for i in ids[start:start+size]]
                if q.get("excludeElements") == "Media":
                    for row in rows:
                        row.pop("Media", None)
                return self.respond(200, dict(offset=start, totalSize=len(ids), Metadata=rows))
            if path == "/library/sections/1/genre":
                return self.respond(200, dict(Directory=[dict(title="Jazz", key="23")]))
            if path == "/hubs/sections/1" and getattr(lib.options,"deny_radio",False):
                return self.respond(403)
            if path == "/hubs/sections/1":
                return self.respond(200, {"Hub": [] if lib.options.no_station else [{"context": "hub.music.stations", "Metadata": [{"title": "Demo · Library Radio", "key": "/demo/station"}]}]})
            if path in ("/library/metadata/g", "/library/metadata/v"):
                return self.respond(200, dict(Metadata=[dict(Stations=dict(Metadata=[] if lib.options.no_station else [dict(title="Demo · Artist Radio", key="/demo/station")]))]))
            if path.startswith("/library/metadata/") and path.endswith(("/children", "/allLeaves")):
                group = path.split("/")[3]
                rows = [lib.track(i) for i in range(1, lib.count+1) if i not in lib.deleted]
                rows = [t for t in rows if group in (t["parentRatingKey"], t["grandparentRatingKey"])]
                start=int(q.get("X-Plex-Container-Start",0))
                return self.respond(200,dict(offset=start,totalSize=len(rows),Metadata=rows[start:start+int(q.get("X-Plex-Container-Size",len(rows)))]))
            if path.startswith("/library/metadata/"):
                i = int(path.rsplit("/", 1)[1])
                if self.command=="DELETE":
                    lib.delete_requests += 1
                if not 1 <= i <= lib.count or i in lib.deleted:
                    return self.respond(404)
                if self.command == "DELETE":
                    if i not in lib.versions:
                        return self.respond(403)
                    lib.versions[i].unlink()
                    lib.deleted.add(i)
                    if lib.options.lose_ack:
                        self.close_connection = True
                        return
                    return self.respond(200)
                return self.respond(200, dict(Metadata=[lib.track(i)]))
            if path.startswith("/parts/"):
                i = int(path.split("/")[-1])
                if i in lib.deleted:
                    return self.respond(404)
                file = lib.versions.get(i, lib.wav)
                size = file.stat().st_size
                m = re.fullmatch(r"bytes=(\d+)-(\d+)", self.headers.get("Range", ""))
                if not m or getattr(lib.options, "no_range", False):
                    self.send_response(200)
                    self.send_header("Content-Type", "audio/wav")
                    self.send_header("Content-Length", str(size))
                    self.end_headers()
                    with file.open("rb") as f:
                        shutil.copyfileobj(f, self.wfile)
                    return
                a, b = int(m[1]), min(size-1, int(m[2]))
                if a>b:
                    return self.respond(416)
                self.send_response(200 if getattr(lib.options,"no_range",False) else 206)
                self.send_header("Accept-Ranges", "bytes")
                self.send_header("Content-Range", f"bytes {a}-{b}/{size}")
                self.send_header("Content-Length", str(b-a+1))
                self.end_headers()
                with file.open("rb") as f:
                    f.seek(a)
                    self.wfile.write(f.read(b-a+1))
                return
            if path in ("/playQueues", "/playQueues/19"):
                if q.get("uri", "").startswith("library:///directory/"):
                    source = unquote(q["uri"][len("library:///directory/"):])
                    if source.startswith("/library/metadata/"):
                        ids = [int(i) for i in source.removeprefix("/library/metadata/").split(",")]
                    elif source == "/library/sections/1/all?type=10&track.genre=23&sort=random":
                        ids = list(range(1, min(lib.count, 12)+1))
                    else:
                        return self.respond(400)
                    return self.respond(200,dict(playQueueID=20,Metadata=[lib.track(i) | dict(playQueueItemID=str(j+1)) for j,i in enumerate(ids) if i not in lib.deleted]))
                if path == "/playQueues" and not q.get("uri", "").endswith("/demo/station"):
                    return self.respond(400)
                live = [i for i in range(1, min(lib.count, 4)+1) if i not in lib.deleted]
                start = max(1, int(q.get("center", 1)))
                return self.respond(200, dict(playQueueID=19, Metadata=[lib.track(live[j % len(live)]) | dict(playQueueItemID=str(j)) for j in range(start, start+5)] if live else []))
            if path == "/playQueues/20":
                return self.respond(200, dict(playQueueID=20,Metadata=[]))
            if path == "/playlists" and self.command == "GET":
                return self.respond(200, dict(Metadata=[dict(ratingKey=k, title=v[0]) for k, v in lib.playlists.items()]))
            if path.startswith("/playlists"):
                bits = path.split("/")
                pid = str(max(map(int,lib.playlists), default=0)+1) if len(bits)==2 else bits[2]
                if len(bits)==2:
                    lib.playlists[pid] = (q["title"], [])
                if len(bits)==3 and self.command=="DELETE":
                    del lib.playlists[pid]
                    return self.respond(200, {})
                rows = lib.playlists[pid][1]
                if "uri" in q:
                    lib.entry += 1
                    rows.append((str(lib.entry), int(q["uri"].rsplit("/", 1)[1])))
                elif len(bits)>4:
                    item = next(t for t in rows if t[0]==bits[4])
                    rows.remove(item)
                    if self.command=="PUT":
                        after = q["after"]
                        rows.insert(0 if after=="0" else next(i for i,t in enumerate(rows) if t[0]==after)+1, item)
                return self.respond(200, dict(Metadata=[dict(ratingKey=pid)] if len(bits)==2 else [lib.track(i)|dict(playlistItemID=k) for k,i in rows if i not in lib.deleted]))
            return self.respond(404)

        def handle_request(self):
            with lib.lock:
                try:
                    self.route()
                except (BrokenPipeError, ConnectionResetError):
                    pass
                except (ValueError, KeyError, IndexError, StopIteration):
                    self.respond(400)
        do_GET = do_PUT = do_POST = do_DELETE = handle_request
    return Handler


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--port", type=int, default=32491)
    parser.add_argument("--count", type=int, default=48)
    parser.add_argument("--marker", type=Path)
    parser.add_argument("--fixture-parent",type=Path)
    parser.add_argument("--flac-fixture",action="store_true")
    for flag in ["no-station", "deny-writes", "revoked", "lose-ack", "multiple", "partial-scan", "no-range", "deny-radio"]:
        parser.add_argument("--"+flag, action="store_true")
    args = parser.parse_args()
    if args.marker:
        marker(args.marker)
    else:
        with tempfile.TemporaryDirectory(prefix="mixamp-demo-",dir=args.fixture_parent) as root:
            lib = Library(Path(root), args.count, args)
            with ThreadingHTTPServer(("127.0.0.1", args.port), handler(lib)) as server:
                print(f"Disposable demo ready on loopback port {server.server_port}", flush=True)
                server.serve_forever()
