#!/usr/bin/env bash
# Builds the Octahedron plugin and the test probe as Magnetar local plugins (DLL + manifest per folder), the
# layout the cluster CLI installs from CLUSTER_EXTRA_PLUGINS:  tests/mes/cluster/build-local-plugins.sh OUT
# writes OUT/Octahedron/{Octahedron.dll,.xml} and OUT/MesClusterProbe/{MesClusterProbe.dll,.xml}.
set -euo pipefail
PLUGIN="$(cd "$(dirname "${BASH_SOURCE[0]}")/../../.." && pwd)"
OUT="$(realpath -m "${1:?usage: build-local-plugins.sh OUT}")"
COMMIT="$(git -C "$PLUGIN" rev-parse HEAD)"
build() {  # project manifest name
    nice -n 19 dotnet build "$PLUGIN/$1" -c Release --nologo -v q -p:RunPostBuildEvent=Never -o "$OUT/.build/$3" >&2
    mkdir -p "$OUT/$3"
    cp -a "$OUT/.build/$3/$3.dll" "$OUT/$3/"
    sed "s|<Commit>[^<]*</Commit>|<Commit>$COMMIT</Commit>|" "$PLUGIN/$2" > "$OUT/$3/$3.xml"
}
build ServerPlugin/ServerPlugin.csproj Octahedron.xml Octahedron
build tests/mes/probe/MesClusterProbe.csproj tests/mes/probe/MesClusterProbe.xml MesClusterProbe
rm -rf "$OUT/.build"
echo "$OUT"
