"""Run on a quiet Windows desktop with an active output endpoint and the loopback mock running.
pip install soundcard numpy
python tools/measure_audio.py artifacts/mixamp-win-x64/CrateDeck.exe artifacts
"""
import argparse
import json
from pathlib import Path
import subprocess
import time
import numpy as np
import soundcard as sc
from mock_plex import marker

parser=argparse.ArgumentParser(description=__doc__)
parser.add_argument("exe",type=Path)
parser.add_argument("out",type=Path)
args=parser.parse_args()
args.out.mkdir(exist_ok=True,parents=True)
source=args.out/"marker.wav"
marker(source)
speaker=sc.default_speaker()
mic=sc.get_microphone(speaker.id,include_loopback=True)
for route in ("local","http"):
    report=args.out/(route+"-clock.json")
    rate=48000
    spectra=[]
    with mic.recorder(samplerate=rate,blocksize=960) as rec:
        proc=subprocess.Popen([str(args.exe.resolve()),"--probe-media",route,str(source.resolve()),str(report.resolve())])
        try:
            deadline=time.monotonic()+250
            while proc.poll() is None:
                if time.monotonic()>deadline:
                    raise TimeoutError("Packaged probe timed out")
                block=rec.record(numframes=960)
                mono=block[:,np.argmax(np.max(np.abs(block),axis=0))]
                spectrum=np.abs(np.fft.rfft(mono*np.hanning(len(mono))))
                spectra.append([float(np.max(np.abs(mono))),float(np.argmax(spectrum)*rate/len(mono))])
        finally:
            if proc.poll() is None:
                proc.kill()
                proc.wait()
    peaks=np.array(spectra)
    if proc.returncode!=0 or not len(peaks):
        raise RuntimeError("Playback probe failed; inspect "+str(report))
    audible=peaks[:,0]>max(.0001,peaks[:,0].max()*.08)
    inside=audible & (np.abs(peaks[:,1]-880)<45)
    outside=audible & ((np.abs(peaks[:,1]-220)<45)|(np.abs(peaks[:,1]-330)<45))
    indexes=np.flatnonzero(inside)
    duration=0 if len(indexes)==0 else (int(indexes[-1])-int(indexes[0])+1)*20
    result=dict(route=route,device=speaker.name,rate=rate,blockMs=20,observedIntervalMs=duration,outsideToneMs=int(outside.sum()*20),samples=spectra)
    (args.out/(route+"-output.json")).write_text(json.dumps(result))
    if abs(duration-206000)>150 or result["outsideToneMs"]>150:
        raise RuntimeError(f"{route} output outside budget: duration {duration} ms, outside tone {result['outsideToneMs']} ms")
    print(route,duration,"ms")
