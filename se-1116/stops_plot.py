import json, sys
import numpy as np
sys.path.insert(0, "/home/viktor/ws/se/tmp/se1116")
import audio_check as ac
import matplotlib; matplotlib.use("Agg"); import matplotlib.pyplot as plt
SP = sys.argv[1]
tpl = {(i, s): e for i, s, e in ac.templates()}
fig, axes = plt.subplots(8, 2, figsize=(16, 18))
for col, w in enumerate(("new", "old")):
    marks = json.load(open(f"{SP}/{w}-marks.json"))
    pcm = np.fromfile(f"{SP}/{w}.wav", dtype="<i2", offset=ac.HEADER)
    x = pcm[: len(pcm) // 2 * 2].reshape(-1, 2).astype(float).mean(axis=1)
    rec = ac.envelope(x, ac.RATE)
    at = lambda b: (b - ac.HEADER) // 4 // ac.HOP
    rec = np.maximum(rec - np.median(rec[at(marks["quiet"]) : at(marks["quiet_end"])]), 0)
    stops = ac.playing_at_stops(f"{SP}/{w}.wav", marks)
    for k, ((on, stop, end), st) in enumerate(zip(marks["cycles"], stops)):
        ax = axes[k, col]
        ts = at(stop); i = int(st["wave"][-1]); e = tpl[(i, st["semitones"])]
        e = np.maximum(e - np.median(e[-20:]) - 10, 0)
        kk = int(round(st["played_before_stop_s"] * 100)); t0 = ts - kk
        lo, hi = t0 - 100, min(t0 + len(e) + 100, at(end))
        t = (np.arange(lo, hi) - ts) / 100
        ax.plot(t, rec[lo:hi], lw=0.7, label="recording")
        te = (np.arange(len(e)) + t0 - ts) / 100
        ax.plot(te, -e, lw=0.7, color="green", label="wave (mirrored)")
        ax.axvline(0, color="red")
        ax.set_title(f"{w} stop {k}: {st['wave']} {st['semitones']:+} st, before {st["level_before_db"]} dB, after {st["level_after_db"]} dB", fontsize=9)
fig.tight_layout(); fig.savefig(f"{SP}/stops.png", dpi=60)
