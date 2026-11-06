# SE-1116 evidence: filaments linger 3 s, tool sounds play to their end

Linux, factory@nas and factory@polaris, 2026-11-05/06. `rig1116.py` (run with `run.sh new|old`, `FILAMENT_LINGER=2 RUN=new2` for a 2 s linger) is SE-1095's rig with the tool's working state in the trace, a linger check in `trace_check.py`, and an audio phase that `audio_check.py` scores.

- `weld-retract-new.jpg` / `weld-retract-old.jpg`: one welder switched off at +0 s. The branch's filaments stay on the slab until +3 s, then pull back by about +4.2 s. Main's start pulling back at once. Crops of 960x540 frames, about 4 per second.
- `audio-stops-run1.png` / `audio-stops-run2.png`: two recordings each of the branch (left) and main (right), 10 m from one welder that works 4-10 s and is then switched off (red line). Blue is the 250 Hz-8 kHz band of the recording in dB over the quiet level. Green, mirrored, is the Lightning wave `audio_check.py` found playing at the stop, with its pitch and start fitted to the part before the stop. On the branch the recording follows the wave past the stop until the wave ends. On main it drops to the quiet level at the stop.
- `stop-new.ogg` / `stop-old.ogg`: 10 s of those recordings, the stop at 4 s (run 1, branch stop 4 and main stop 3).
- `logs/`: the rig output of every run that the PR description quotes.

The shipyard hums below 60 Hz, louder than the storm, and the storm is sparse thunder over a quiet bed. That is why the audio check identifies the playing wave and measures the band level after the stop instead of the overall loudness.

The trace and timing logs come from runs made before the audio check was final. Their `audio` lines and audio FAILs are from a first check that compared overall loudness, which the shipyard's hum and the jetpack drowned. Only the trace and timing lines of those logs count; the audio results are in `audio-run*.log`.
