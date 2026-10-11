#!/usr/bin/env bash
# Builds the plugin as a Magnetar local plugin bundle, <output>/Octahedron/Octahedron.dll with
# Octahedron.xml pinned to the checkout's commit, for loaders that take compiled plugins only:
# the cluster CLI (CLUSTER_EXTRA_PLUGINS=node:<output>/Octahedron) and Quasar's managed
# clusters, whose preparation refuses dev folders.
set -euo pipefail
here=$(cd "$(dirname "$0")" && pwd)
output=${1:?usage: package.sh <output folder>}
if [ -n "$(git -C "$here" status --porcelain -- .)" ]; then
    echo "Commit the changes in $here first: the bundle names the commit it was built from" >&2
    exit 1
fi
commit=$(git -C "$here" rev-parse HEAD)
bundle="$output/Octahedron"
rm -rf "$bundle"
dotnet build "$here/ServerPlugin/ServerPlugin.csproj" -c Release -f net10.0 -nologo -v q -o "$bundle.build"
mkdir -p "$bundle"
cp "$bundle.build/Octahedron.dll" "$bundle/"
rm -rf "$bundle.build"
cat > "$bundle/Octahedron.xml" <<XML
<?xml version="1.0"?>
<PluginData xmlns:xsd="http://www.w3.org/2001/XMLSchema" xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance" xsi:type="GitHubPlugin">
    <Id>F7FB710B-7B41-45D6-B05C-A01DDE0FEE3A</Id>
    <FriendlyName>Octahedron</FriendlyName>
    <RepoId>CometWorks/worlds</RepoId>
    <Commit>$commit</Commit>
    <Runtimes>CoreCLR</Runtimes>
</PluginData>
XML
echo "$bundle"
