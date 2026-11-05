#!/bin/bash
# run.sh new|old [rig args...] -> rig-<which>.log (SE-1095). new: the branch's mod, old: main's
cd ~/ws/se/tmp/se1095
which=$1; shift
export OCT_TASK=SE-1095-$which OCT_PULSAR_TEMPLATE=/home/factory/.se-test/SE-959-pulsar
export FILAMENT_OCT=$HOME/ws/se/tmp/se1095/worlds-$which/octahedron
~/.local/bin/uv run -q --no-project --with httpx --with numpy python rig1095.py "$@" > rig-$which.log 2>&1
echo "EXIT $?" >> rig-$which.log
