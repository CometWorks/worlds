# SE-1095 evidence: filaments extend, glide and retract

Linux, factory@polaris and factory@nas, 2026-11-05. `rig1095.py` (run with `run.sh new|old`), `trace_check.py`.

- `weld-extend-new.jpg` / `weld-extend-old.jpg`: one welder switched on at +0 ms; its turret aims for about 2.3 s, then the branch's filaments grow from the welder (bottom) to the slab in about 0.8 s, main's appear at once.
- `weld-retract-new.jpg` / `weld-retract-old.jpg`: the welder switched off at +0 ms; the branch's filaments pull back toward the welder, main's vanish between two frames.
- Crops of 960x540 frames, about 4 frames per second; the labels are the time since the switch.

The per-frame trace and the timing comparison are in the PR description.
