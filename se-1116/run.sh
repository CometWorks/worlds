#!/bin/bash
# run.sh new|old [rig args...] -> rig-<name>.log (SE-1116). new: the branch's mod, old: main's.
# RUN=<name> names the run (default: new|old), e.g. FILAMENT_LINGER=2 RUN=new2 run.sh new --trace
cd ~/ws/se/tmp/se1116
which=$1; shift
name=${RUN:-$which}
export OCT_TASK=SE-1116-$name OCT_PULSAR_TEMPLATE=/home/factory/.se-test/SE-959-pulsar
export FILAMENT_OCT=$HOME/ws/se/tmp/se1116/worlds-$which/octahedron
~/.local/bin/uv run -q --no-project --with httpx --with numpy python rig1116.py "$@" > rig-$name.log 2>&1
echo "EXIT $?" >> rig-$name.log
