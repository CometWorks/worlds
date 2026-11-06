"""SE-1116: how long the storm sound goes on after each stop, in a rig recording.

    python3 audio_check.py AUDIO.wav MARKS.json [PLOT.png]

MARKS.json is the rig's audio marks (byte offsets of the recording): "quiet" and
"quiet_end" with all tools off, "cycles" of [working from, stopped at, end]. The level
is the 250 Hz to 8 kHz band, where the storm is; the shipyard's own hum is below 60 Hz.
The storm's waves are sparse thunder over a quiet bed, so the level alone can't tell
whether a wave still plays. So for each stop this finds the wave that was playing: the
game's Lightning wave, pitch (+-1 semitone) and start whose envelope best matches the
recording before the stop. Then it measures the recording over the first 2 s after the
stop (from 0.2 s): a wave that plays on keeps it several dB over the quiet level, a wave
cut off at the stop drops it to the quiet level at once ("level_after_db" near 0).
"""

import json
import sys

import numpy as np

WAVES = [f"{__import__('os').path.expanduser('~')}/.steam/steam/steamapps/common/SpaceEngineers/Content/Audio/ARC/EVENT/Lightning_{i}.wav" for i in range(4)]
HOP = 480  # 10 ms envelope frames
RATE, HEADER = 48000, 68  # Remote's --audio-file: OpenAL Soft's 16-bit stereo wave writer
WINDOW = 4800  # 0.1 s


def band_levels(path):
    pcm = np.fromfile(path, dtype="<i2", offset=HEADER)
    x = pcm[: len(pcm) // 2 * 2].reshape(-1, 2).astype(float).mean(axis=1)
    x = x[: len(x) // WINDOW * WINDOW].reshape(-1, WINDOW)
    f = np.fft.rfftfreq(WINDOW, 1 / RATE)
    band = (f >= 250) & (f < 8000)
    level = np.sqrt((np.abs(np.fft.rfft(x * np.hanning(WINDOW), axis=1))[:, band] ** 2).sum(axis=1))
    return np.convolve(level, np.ones(5) / 5, mode="same")


def envelope(x, rate):
    """Band energy in dB per 10 ms of playback"""
    n = 2 * HOP
    frames = np.lib.stride_tricks.sliding_window_view(x, n)[::HOP] * np.hanning(n)
    f = np.fft.rfftfreq(n, 1 / rate)
    band = (f >= 250) & (f < 8000)
    return 10 * np.log10((np.abs(np.fft.rfft(frames, axis=1))[:, band] ** 2).sum(axis=1) + 1)


def templates():
    import wave

    out = []
    for i, path in enumerate(WAVES):
        w = wave.open(path)
        x = np.frombuffer(w.readframes(w.getnframes()), dtype="<i2").astype(float)
        rate = w.getframerate()
        for semis in np.linspace(-1, 1, 21):
            r = 2 ** (semis / 12)
            # Played r times faster: resample to RATE / r
            t = np.arange(0, len(x) / rate, r / RATE)
            out.append((i, round(semis, 2), envelope(np.interp(t, np.arange(len(x)) / rate, x), RATE)))
    return out


def ncc(a, b):
    a, b = a - a.mean(), b - b.mean()
    d = np.sqrt((a * a).sum() * (b * b).sum())
    return float((a * b).sum() / d) if d else 0.0


def playing_at_stops(path, marks):
    pcm = np.fromfile(path, dtype="<i2", offset=HEADER)
    x = pcm[: len(pcm) // 2 * 2].reshape(-1, 2).astype(float).mean(axis=1)
    rec = envelope(x, RATE)
    at = lambda b: (b - HEADER) // 4 // HOP
    floor = np.median(rec[at(marks["quiet"]) : at(marks["quiet_end"])])
    rec = np.maximum(rec - floor, 0)  # dB over the shipyard's own sound
    found = []
    tpls = [(i, s, np.maximum(e - np.median(e[-20:]) - 10, 0)) for i, s, e in templates()]
    for on, stop, end in marks["cycles"]:
        ts, t_on = at(stop), at(on) - 300  # the tool starts about 3 s before "on"
        best = None
        for i, semis, e in tpls:
            for k in range(100, min(len(e), ts - t_on)):  # k frames of the wave before the stop
                before = ncc(e[:k], rec[ts - k : ts])
                if best is None or before > best[0]:
                    best = (before, i, semis, k, e)
        before, i, semis, k, e = best
        rest = e[k : k + (at(end) - ts)]
        # Only a rest with thunder in its first 2 s (6 dB on average) can show whether it played on
        audible = len(rest) >= 200 and float(rest[20:200].mean()) > 6
        after = float(rec[ts + 20 : ts + 200].mean())
        found.append(
            {
                "wave": f"Lightning_{i}", "semitones": semis,
                "played_before_stop_s": round(k / 100, 2), "left_after_stop_s": round(len(rest) / 100, 2),
                "match_before": round(before, 2), "rest_audible": audible,
                "level_before_db": round(float(rec[ts - 200 : ts].mean()), 1), "level_after_db": round(after, 1),
            }
        )
    return found


def analyze(path, marks):
    level = band_levels(path)
    at = lambda b: (b - HEADER) // 4 // WINDOW
    quiet = float(np.median(level[at(marks["quiet"]) : at(marks["quiet_end"])]))
    cycles = []
    for on, stop, end in marks["cycles"]:
        working = float(np.median(level[at(on) : at(stop)]))
        after = level[at(stop) : at(end)]
        threshold = quiet + 0.2 * (working - quiet)
        over = np.where(after > threshold)[0]
        cycles.append(
            {
                "working_vs_quiet": round(working / quiet, 1),
                "tail_s": round((over[-1] + 1) * 0.1 if len(over) else 0.0, 1),
                "ended": bool(len(after) and after[-10:].max() < threshold),
            }
        )
    return {"quiet": round(quiet), "cycles": cycles}, level


def plot(path, marks, level, out, title):
    import matplotlib

    matplotlib.use("Agg")
    import matplotlib.pyplot as plt

    at = lambda b: (b - HEADER) / 4 / RATE
    t0 = at(marks["quiet"])
    t = np.arange(len(level)) * 0.1 - t0
    fig, ax = plt.subplots(figsize=(12, 3))
    ax.plot(t, level, lw=0.8)
    for on, stop, end in marks["cycles"]:
        ax.axvspan(at(on) - t0, at(stop) - t0, color="orange", alpha=0.3)
        ax.axvline(at(stop) - t0, color="red", lw=1)
    ax.set_xlim(0, at(marks["cycles"][-1][2]) - t0)
    ax.set_xlabel("s (orange: welder working, red: switched off)")
    ax.set_ylabel("250 Hz-8 kHz level")
    ax.set_title(title)
    fig.tight_layout()
    fig.savefig(out, dpi=90)


if __name__ == "__main__":
    marks = json.load(open(sys.argv[2]))
    result, level = analyze(sys.argv[1], marks)
    result["playing_at_stops"] = playing_at_stops(sys.argv[1], marks)
    print(json.dumps(result))
    if len(sys.argv) > 3:
        plot(sys.argv[1], marks, level, sys.argv[3], sys.argv[4] if len(sys.argv) > 4 else sys.argv[1])
