"""Generate and independently verify disposable tag and audio fixtures.
Requires imageio-ffmpeg 0.6.0 and mutagen 1.47.0. Run from the repository root.
"""
import argparse
import base64
import hashlib
import shutil
import subprocess
import tempfile
from pathlib import Path

import imageio_ffmpeg
from mutagen.flac import FLAC, Picture
from mutagen.id3 import APIC, TALB, TPE1, TPE2, TRCK, TPOS, TXXX
from mutagen.mp3 import MP3
from mutagen.mp4 import MP4, MP4Cover
from mock_plex import marker

ffmpeg = imageio_ffmpeg.get_ffmpeg_exe()
picture = base64.b64decode("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+/p9sAAAAASUVORK5CYII=")


def generate(folder):
    folder.mkdir(parents=True, exist_ok=True)
    with tempfile.TemporaryDirectory() as d:
        wav = Path(d) / "source.wav"
        marker(wav, 2)
        for codec, ext in [("libmp3lame","mp3"),("aac","m4a"),("alac","m4a"),("flac","flac")]:
            dst = folder / f"{codec}.{ext}"
            subprocess.run([ffmpeg,"-v","error","-y","-i",str(wav),"-c:a",codec,str(dst)],check=True)
            if ext=="mp3":
                m = MP3(dst)
                for frame in [TPE1(encoding=3,text=["Mira","池田"]),TPE2(encoding=3,text=["Session Band"]), TALB(encoding=3,text=["Room Tapes"]),TRCK(encoding=3,text="2/12"),TPOS(encoding=3,text="1/2"),TXXX(encoding=3,desc="MusicBrainz Track Id",text="fixture-recording"),TXXX(encoding=3,desc="REPLAYGAIN_TRACK_GAIN",text="-2 dB"),APIC(encoding=3,mime="image/png",type=3,desc="Cover",data=picture)]:
                    m.tags.add(frame)
            elif ext=="flac":
                m = FLAC(dst)
                for k,v in {"artist":["Mira","池田"],"albumartist":"Session Band","album":"Room Tapes","tracknumber":"2","tracktotal":"12","discnumber":"1","disctotal":"2","musicbrainz_trackid":"fixture-recording","replaygain_track_gain":"-2 dB"}.items():
                    m[k]=v
                p=Picture();p.type=3;p.mime="image/png";p.data=picture;m.add_picture(p)
            else:
                m=MP4(dst)
                for k,v in {"©ART":["Mira","池田"],"aART":["Session Band"],"©alb":["Room Tapes"],"trkn":[(2,12)],"disk":[(1,2)],"----:com.apple.iTunes:MusicBrainz Track Id":[b"fixture-recording"],"----:com.apple.iTunes:replaygain_track_gain":[b"-2 dB"],"covr":[MP4Cover(picture,MP4Cover.FORMAT_PNG)]}.items():
                    m[k]=v
            m.save()


def check(folder, dotnet):
    def pcm(path):
        return hashlib.sha256(subprocess.check_output([ffmpeg,"-v","error","-i",str(path),"-f","s16le","-"])).digest()
    for src in sorted(folder.glob("*.*")):
        with tempfile.TemporaryDirectory() as d:
            dst=Path(d)/src.name
            shutil.copyfile(src,dst)
            original=pcm(dst)
            subprocess.run([dotnet,"tests/bin/Release/net10.0/Checks.dll","--tag",str(dst)],check=True,stdout=subprocess.DEVNULL)
            read={"flac":FLAC,"mp3":MP3,"m4a":MP4}[dst.suffix[1:]]
            before,after=read(src),read(dst)
            artist={"flac":"albumartist","mp3":"TPE2","m4a":"aART"}[dst.suffix[1:]]
            assert list(after[artist])==["Various Artists"]
            assert {k:v for k,v in before.items() if k!=artist}=={k:v for k,v in after.items() if k!=artist}
            if dst.suffix==".flac":
                assert [p.write() for p in before.pictures]==[p.write() for p in after.pictures]
            assert original==pcm(dst)
            print(src.name,"verified independent tags and PCM",original.hex(),flush=True)


if __name__=="__main__":
    p=argparse.ArgumentParser()
    p.add_argument("--folder",type=Path,default=Path("tests/fixtures"))
    p.add_argument("--generate",action="store_true")
    p.add_argument("--dotnet",default="dotnet")
    args=p.parse_args()
    if args.generate:
        generate(args.folder)
    check(args.folder,args.dotnet)
