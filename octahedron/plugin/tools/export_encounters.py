#!/usr/bin/env python3
"""Exports the encounters MES spawned in Octahedron for the plugin's own spawner.

    export_encounters.py [--workshop <folder>] [--game <Content folder>] [--out <folder>]

Reads the spawn groups of Modular Encounters Systems (MES), its encounter mods and the
base game's planetary encounters from the local Workshop and game folders, as the world
listed them in config/mes-mods.xml before MES was dropped (ticket 1447). Writes to
--out (default: ../Encounters next to this script):

- SpawnGroups.xml: every group the MES timers could spawn on their own, with its
  condition profiles (spawn types, chances, threat, planets, air, reputation, sandbox
  variables) and prefabs (blueprint, offset, speed). The plugin reads it at load.
- Blueprints/<prefab>.bp.sbc.gz: each prefab's grids as a gzipped local blueprint, as
  the mod has it, only without MES's three service terminals (research, suit upgrade,
  shipyard), if that leaves every grid in one piece. A prefab with any other block the
  base game doesn't define (MES's reactors, thrusters and remote controls, other mods'
  blocks) is left out, and with it a group whose main prefab it is.
- Factions.xml: the encounter mods' NPC factions, for the world build.

Groups only other NPCs spawn (RivalAiSpawn and the like), admin-only groups and groups
that need a named player online are left out: the plugin has no MES behaviors to spawn
them. See the plugin README, "Encounters".
"""

import argparse
import gzip
import re
import xml.etree.ElementTree as ET
from pathlib import Path
from xml.sax.saxutils import quoteattr

HERE = Path(__file__).resolve().parent
STEAM = Path.home() / ".steam/steam/steamapps"

# config/mes-mods.xml order: the first mod listed is loaded last and wins on conflicts
MODS = [
    (1521905890, "Modular Encounters Systems"),
    (2802625870, "MES Wreckage"),
    (2390106048, "MES Weyland-Yutani Corporation"),
    (2470950657, "MES The Xenvakian Empire"),
    (2615864222, "MES Terran Heavy Industries Engineering"),
    (2609155402, "MES Space Pirates Enhanced"),
    (2966429095, "Trade Operators Coalition"),
    (1135484957, "Parallax Concepts"),
    (445996030, "Independent Contractors"),
    (973526550, "Imber Corporation"),
    (2634554148, "MES Abandoned Mining Facility"),
    (2539299261, "Wasteland Encounters V2 Continued"),
    (2310821218, "Abandoned Settlements [v2]"),
]

# The spawn types the MES timers spawn; lunar cargo ships are off (MinLunarSpawnHeight 0)
TYPES = {
    "SpaceCargoShip": "SpaceCargoShipChance",
    "SpaceRandomEncounter": "RandomEncounterChance",
    "AtmosphericCargoShip": "AtmosphericCargoShipChance",
    "GravityCargoShip": "GravityCargoShipChance",
    "PlanetaryInstallation": "PlanetaryInstallationChance",
    "BossEncounterSpace": "BossEncounterChance",
    "BossEncounterAtmo": "BossEncounterChance",
    "BossEncounterAny": "BossEncounterChance",
}

# Base game <Factions> subtypes as MES maps them to tags (GetOldSpawnGroupDetails)
BASE_GAME_FACTIONS = {"Unknown": "UNKN", "SpacePirates": "SPRT", "Factorum": "FCTM"}

TAG = re.compile(r"\[([A-Za-z]+):([^\]\n]*)\]")
XSI = "http://www.w3.org/2001/XMLSchema-instance"


def local(tag):
    return tag.rsplit("}", 1)[-1]


def tags(text):
    out = {}
    for key, value in TAG.findall(text or ""):
        out.setdefault(key, []).append(value.strip())
    return out


def last(t, key, default=None):
    return t[key][-1] if key in t else default


def flag(t, key):
    return (last(t, key) or "").lower() == "true"


def items(t, key):
    return [x.strip() for v in t.get(key, []) for x in v.split(",") if x.strip()]


def number(t, key, default):
    try:
        return float(last(t, key, default))
    except ValueError:
        return default


def condition(t):
    """A condition profile's spawn types and checks, as XML attributes; None if it spawns
    nothing on a timer or only an admin or a named player could spawn it."""
    if flag(t, "AdminSpawnOnly") or items(t, "RequiredPlayersOnline"):
        return None
    if items(t, "RequiredAnyPlayersOnline"):
        return None
    types = [k for k in TYPES if flag(t, k)]
    attrs = {"Types": " ".join(types)}
    for kind in types:
        chance = number(t, TYPES[kind], 100)
        if chance < 100:
            attrs[TYPES[kind]] = f"{chance:g}"
    if "ChanceCeiling" in t:
        attrs["ChanceCeiling"] = f"{number(t, 'ChanceCeiling', 100):g}"
    if "PlanetaryInstallation" in types:
        attrs["InstallationSize"] = last(
            t, "PlanetaryInstallationType", "Small"
        ).capitalize()
    if flag(t, "UseThreatLevelCheck"):
        attrs["ThreatMinimum"] = f"{number(t, 'ThreatScoreMinimum', -1):g}"
        attrs["ThreatMaximum"] = f"{number(t, 'ThreatScoreMaximum', -1):g}"
        attrs["ThreatRange"] = f"{number(t, 'ThreatLevelCheckRange', 5000):g}"
    for key in (
        "PlanetWhitelist",
        "PlanetBlacklist",
        "SandboxVariables",
        "FalseSandboxVariables",
    ):
        if items(t, key):
            attrs[key] = " ".join(items(t, key))
    # MES makes an atmospheric cargo ship need air unless it skips the air density check
    # or sets PlanetRequiresAtmo itself (SpawnConditionsProfile.InitTags)
    if "AtmosphericCargoShip" in types and "PlanetRequiresAtmo" not in t:
        if not flag(t, "SkipAirDensityCheck"):
            attrs["PlanetRequiresAtmo"] = "true"
    flags = (
        "PlanetRequiresAtmo",
        "SkipAirDensityCheck",
        "UniqueEncounter",
        "ForceStaticGrid",
        "DisableDampeners",
    )
    for key in flags:
        if flag(t, key):
            attrs[key] = "true"
    if flag(t, "UseTerrainTypeValidation") and items(t, "AllowedTerrainTypes"):
        attrs["TerrainTypes"] = " ".join(items(t, "AllowedTerrainTypes"))
    if "MaxGravity" in t:
        attrs["MaxGravity"] = f"{number(t, 'MaxGravity', -1):g}"
    if flag(t, "UsePlayerFactionReputation"):
        attrs["ReputationMinimum"] = f"{number(t, 'MinimumReputation', -1501):g}"
        attrs["ReputationMaximum"] = f"{number(t, 'MaximumReputation', 1501):g}"
        attrs["ReputationRange"] = (
            f"{number(t, 'PlayerReputationCheckRadius', 15000):g}"
        )
    if flag(t, "UseRemoteControlCodeRestrictions"):
        attrs["RemoteControlCode"] = last(t, "RemoteControlCode", "")
        for key in ("RemoteControlCodeMinDistance", "RemoteControlCodeMaxDistance"):
            if key in t:
                attrs[key] = f"{number(t, key, -1):g}"
    if "FactionOwner" in t:
        attrs["Faction"] = last(t, "FactionOwner")
    return attrs | prefab_mode(t)


def prefab_mode(t):
    """Which of the group's prefabs spawn (SpawnGroupCollection.SelectPrefabIndexes): all,
    one at random, the listed indexes, one of each index group, or a fixed count."""
    mode = last(t, "PrefabSpawningMode", "All")
    if mode == "All":
        return {}
    attrs = {"PrefabSpawningMode": mode}
    if items(t, "PrefabIndexes"):
        attrs["PrefabIndexes"] = " ".join(items(t, "PrefabIndexes"))
    groups = {}
    for name, value in zip(
        t.get("PrefabIndexGroupNames", []), t.get("PrefabIndexGroupValues", [])
    ):
        groups.setdefault(name, []).append(value)
    if groups:
        attrs["PrefabIndexGroups"] = " ".join(
            f"{k}:{','.join(v)}" for k, v in groups.items()
        )
    if "PrefabFixedCount" in t:
        attrs["PrefabFixedCount"] = last(t, "PrefabFixedCount")
    if flag(t, "AllowPrefabIndexReuse"):
        attrs["AllowPrefabIndexReuse"] = "true"
    return attrs


def sbc_files(root):
    return sorted(p for p in root.rglob("*.sbc") if p.is_file())


def read(path):
    return ET.fromstring(
        path.read_text(encoding="utf-8-sig", errors="replace").encode()
    )


def subtype(el):
    idel = el.find("Id")
    if idel is None:
        return None
    return idel.get("Subtype") or idel.findtext("SubtypeId")


def scan(workshop, game):
    """(groups by name, prefab elements by subtype, conditions profiles, condition groups,
    factions by tag, sandbox booleans by the behavior that sets them), later mods
    overriding earlier ones as the game loads them."""
    groups, prefabs, profiles, cgroups, factions, behaviors = {}, {}, {}, {}, {}, {}
    sources = [
        (
            0,
            [game / "Data/SpawnGroups_PlanetaryEncounters.sbc"]
            + sbc_files(game / "Data/Prefabs"),
        )
    ]
    sources += [(mod, sbc_files(workshop / str(mod))) for mod, _ in reversed(MODS)]
    for mod, files in sources:
        for path in files:
            text = path.read_text(encoding="utf-8-sig", errors="replace")
            if "[SetSandboxBooleansTrue:" in text:
                # A behavior that sets world variables, like the Xenvakian probes' Mod-Enable;
                # its triggers and actions sit in the same file
                names = re.findall(r"<SubtypeId>([^<]+)</SubtypeId>", text)
                values = sorted(set(items(tags(text), "SetSandboxBooleansTrue")))
                for name in names:
                    behaviors[name] = values
            if not any(
                k in text
                for k in ("Prefab", "SpawnGroup", "MES Spawn Conditions", "<Faction ")
            ):
                continue
            for el in read(path).iter():
                name = local(el.tag)
                if name == "Prefab" and el.find("CubeGrids") is not None:
                    prefabs[subtype(el)] = (el, mod)
                elif name == "Faction" and el.get("Tag"):
                    factions[el.get("Tag")] = (el, mod)
                elif (
                    name in ("EntityComponent", "Definition")
                    and el.find("Id") is not None
                ):
                    desc = el.findtext("Description") or ""
                    if "[MES Spawn Conditions Group]" in desc:
                        cgroups[subtype(el)] = tags(desc)
                    elif "[MES Spawn Conditions]" in desc:
                        profiles[subtype(el)] = tags(desc)
                elif name == "SpawnGroup" and el.find("Id") is not None:
                    groups[subtype(el)] = (el, mod)
    return groups, prefabs, profiles, cgroups, factions, behaviors


def group_conditions(el, mod, profiles, cgroups):
    """(persistent condition or None, conditions) of a spawn group, as MES builds them:
    the group's own tags are profile 0, then [SpawnConditionsProfiles:] and the profiles of
    [SpawnConditionGroups:]. None if MES can't load it or nothing spawns it on a timer.
    """
    desc = el.findtext("Description") or ""
    t = tags(desc)
    if (el.findtext("Enabled") or "true").lower() == "false":
        return None
    if (last(t, "SpawnGroupEnabled") or "true").lower() == "false":
        return None
    if "[Modular Encounters SpawnGroup]" not in desc:
        if mod != 0 or (el.findtext("IsPlanetaryEncounter") or "").lower() != "true":
            return None
        # A base-game planetary encounter: a small installation (GetOldSpawnGroupDetails)
        planets = [p.text for p in el.iter() if local(p.tag) == "PlanetName" and p.text]
        terrain = [
            m.text for m in el.iter() if local(m.tag) == "MaterialTypeName" and m.text
        ]
        legacy = {
            "PlanetaryInstallation": ["true"],
            "PlanetaryInstallationType": ["Small"],
        }
        if planets:
            legacy["PlanetWhitelist"] = [",".join(planets)]
        if terrain:
            legacy["UseTerrainTypeValidation"] = ["true"]
            legacy["AllowedTerrainTypes"] = [",".join(terrain)]
        if not {"EarthLike", "Moon"} & set(planets or ["EarthLike"]):
            return None
        return None, [legacy]
    chain = [t]
    for name in items(t, "SpawnConditionsProfiles"):
        if name not in profiles:
            return None
        chain.append(profiles[name])
    for name in items(t, "SpawnConditionGroups"):
        if name not in cgroups:
            return None
        group = cgroups[name]
        for p in items(group, "SpawnConditionProfiles") + items(
            group, "SpawnConditionsProfiles"
        ):
            if p not in profiles:
                return None
            if profiles[p] not in chain:
                chain.append(profiles[p])
    if flag(t, "UseFirstConditionsAsPersistent"):
        return chain[0], chain[1:]
    return None, chain


def base_game_faction(el):
    """The owner MES gives a base-game group: a faction tag, or the faction types."""
    subtypes = [
        s.text for f in el.iter() if local(f.tag) == "Factions" for s in f if s.text
    ]
    if subtypes:
        return "Tags", " ".join(BASE_GAME_FACTIONS.get(s, s) for s in subtypes)
    types = [
        s.text for f in el.iter() if local(f.tag) == "FactionTypes" for s in f if s.text
    ]
    return "Types", " ".join(types or ["Pirate"])


# MES's terminals: services only MES runs (research points, suit upgrades, its NPC
# shipyard), 1 x 1 x 1 blocks. A blueprint whose only non-vanilla blocks are these loses
# them; one with any other non-vanilla block is left out.
MES_TERMINALS = {
    ("ButtonPanel", "MES-Blocks-ResearchTerminal"),
    ("ButtonPanel", "MES-Blocks-SuitUpgradeStation"),
    ("Projector", "MES-Blocks-ShipyardTerminal"),
}

DIRECTIONS = {
    "Forward": (0, 0, -1),
    "Backward": (0, 0, 1),
    "Up": (0, 1, 0),
    "Down": (0, -1, 0),
    "Left": (-1, 0, 0),
    "Right": (1, 0, 0),
}


def vanilla_blocks(game):
    """(type, subtype) -> (x, y, z) size of every block the base game defines"""
    found = {}
    for path in sorted((game / "Data").rglob("*.sbc")):
        text = path.read_text(encoding="utf-8-sig", errors="replace")
        if "<CubeBlocks>" not in text:
            continue
        for el in read(path).iter():
            if local(el.tag) != "Definition" or el.find("Id") is None:
                continue
            idel = el.find("Id")
            kind = (idel.get("Type") or idel.findtext("TypeId") or "").strip()
            sub = (idel.get("Subtype") or idel.findtext("SubtypeId") or "").strip()
            size = el.find("Size")
            found[(kind.removeprefix("MyObjectBuilder_"), sub)] = (
                tuple(int(size.get(a, 1)) for a in "xyz")
                if size is not None
                else (1, 1, 1)
            )
    return found


def block_key(block):
    kind = (block.get(f"{{{XSI}}}type") or "").removeprefix("MyObjectBuilder_")
    return kind, (block.findtext("SubtypeName") or "").strip()


def foreign_blocks(grids, vanilla):
    """The blocks of a prefab's grids the base game doesn't define"""
    return {
        block_key(b) for b in grids.iter("MyObjectBuilder_CubeBlock")
    } - vanilla.keys()


def cells(block, vanilla):
    """The grid cells a block takes, from its Min, orientation and size"""
    m = block.find("Min")
    lo = [int(m.get(a, 0)) for a in "xyz"] if m is not None else [0, 0, 0]
    o = block.find("BlockOrientation")
    forward = DIRECTIONS[o.get("Forward", "Forward")] if o is not None else (0, 0, -1)
    up = DIRECTIONS[o.get("Up", "Up")] if o is not None else (0, 1, 0)
    right = (
        forward[1] * up[2] - forward[2] * up[1],
        forward[2] * up[0] - forward[0] * up[2],
        forward[0] * up[1] - forward[1] * up[0],
    )
    sx, sy, sz = vanilla.get(block_key(block), (1, 1, 1))
    ext = [abs(sx * right[i] + sy * up[i] - sz * forward[i]) for i in range(3)]
    return [
        (lo[0] + x, lo[1] + y, lo[2] + z)
        for x in range(ext[0])
        for y in range(ext[1])
        for z in range(ext[2])
    ]


def pieces(blocks, vanilla):
    """How many face-connected pieces the blocks make"""
    owner = {}
    for i, b in enumerate(blocks):
        for c in cells(b, vanilla):
            owner[c] = i
    parent = list(range(len(blocks)))

    def find(i):
        while parent[i] != i:
            parent[i] = parent[parent[i]]
            i = parent[i]
        return i

    for (x, y, z), i in owner.items():
        for n in ((x + 1, y, z), (x, y + 1, z), (x, y, z + 1)):
            if n in owner:
                parent[find(i)] = find(owner[n])
    return len({find(i) for i in range(len(blocks))})


def usable(grids, vanilla):
    """Whether a prefab spawns with vanilla blocks only once its MES terminals are gone,
    every grid still in one piece without them"""
    foreign = foreign_blocks(grids, vanilla)
    if foreign - MES_TERMINALS:
        return False
    for grid in grids.findall("CubeGrid"):
        blocks = list(grid.iter("MyObjectBuilder_CubeBlock"))
        kept = [b for b in blocks if block_key(b) not in MES_TERMINALS]
        if len(kept) < len(blocks) and pieces(kept, vanilla) > pieces(blocks, vanilla):
            return False
    return True


def remap(c, index):
    """A condition with its prefab indexes moved to the prefabs left, old -> new in
    index; None if it would spawn nothing"""
    c = dict(c)
    if "PrefabIndexes" in c:
        kept = [
            str(index[int(i)]) for i in c["PrefabIndexes"].split() if int(i) in index
        ]
        if not kept:
            return None
        c["PrefabIndexes"] = " ".join(kept)
    if "PrefabIndexGroups" in c:
        groups = []
        for group in c["PrefabIndexGroups"].split():
            key, values = group.split(":")
            kept = [
                str(index[int(v)]) if int(v) >= 0 else v
                for v in values.split(",")
                if int(v) < 0 or int(v) in index
            ]
            if kept:
                groups.append(f"{key}:{','.join(kept)}")
        if not groups:
            return None
        c["PrefabIndexGroups"] = " ".join(groups)
    return c


def blueprint(name, grids):
    """A local blueprint of the prefab's grids, without MES's terminals"""
    for grid in grids.findall("CubeGrid"):
        for parent in grid.iter():
            for block in list(parent):
                if (
                    block.tag == "MyObjectBuilder_CubeBlock"
                    and block_key(block) in MES_TERMINALS
                ):
                    parent.remove(block)
    ET.indent(grids, "  ", level=3)
    xml = "      " + ET.tostring(grids, encoding="unicode").strip()
    xml = xml.replace(f' xmlns:xsi="{XSI}"', "")
    return (
        '<?xml version="1.0"?>\n'
        '<Definitions xmlns:xsd="http://www.w3.org/2001/XMLSchema" xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance">\n'
        "  <ShipBlueprints>\n"
        '    <ShipBlueprint xsi:type="MyObjectBuilder_ShipBlueprintDefinition">\n'
        f'      <Id Type="MyObjectBuilder_ShipBlueprintDefinition" Subtype={quoteattr(name)} />\n'
        f"      <DisplayName>{escape(name)}</DisplayName>\n"
        f"{xml}\n"
        "    </ShipBlueprint>\n"
        "  </ShipBlueprints>\n"
        "</Definitions>\n"
    )


def escape(s):
    return s.replace("&", "&amp;").replace("<", "&lt;").replace(">", "&gt;")


def file_name(prefab):
    return re.sub(r"[^A-Za-z0-9._()\[\] +-]", "_", prefab) + ".bp.sbc.gz"


def attributes(attrs):
    return "".join(f" {k}={quoteattr(str(v))}" for k, v in attrs.items())


def main():
    parser = argparse.ArgumentParser(description=__doc__.split("\n\n")[0])
    parser.add_argument(
        "--workshop", type=Path, default=STEAM / "workshop/content/244850"
    )
    parser.add_argument(
        "--game", type=Path, default=STEAM / "common/SpaceEngineers/Content"
    )
    parser.add_argument("--out", type=Path, default=HERE.parent / "Encounters")
    args = parser.parse_args()

    groups, prefabs, profiles, cgroups, factions, behaviors = scan(
        args.workshop, args.game
    )
    mod_names = dict(MODS) | {0: "Space Engineers"}
    vanilla = vanilla_blocks(args.game)
    lines, used, skipped, dropped_prefabs = [], {}, [], 0
    for name, (el, mod) in sorted(groups.items(), key=lambda g: (g[1][1], g[0])):
        found = group_conditions(el, mod, profiles, cgroups)
        if found is None:
            continue
        persistent, chain = found
        conditions = []
        for profile in chain:
            c = condition(profile)
            if c and c["Types"]:
                if flag(profile, "UseSpawnGroupPrefabSpawningMode"):
                    c = {k: v for k, v in c.items() if not k.startswith("Prefab")}
                    c |= prefab_mode(tags(el.findtext("Description")))
                conditions.append(c)
        if not conditions or (persistent is not None and condition(persistent) is None):
            continue
        pf, names = [], []
        for p in (
            x for x in el.iter() if local(x.tag) == "Prefab" and x.get("SubtypeId")
        ):
            if p.get("SubtypeId") not in prefabs:
                break
            pos = p.find("Position")
            pf.append(
                {
                    "Blueprint": file_name(p.get("SubtypeId")),
                    "X": pos.findtext("X") if pos is not None else "0",
                    "Y": pos.findtext("Y") if pos is not None else "0",
                    "Z": pos.findtext("Z") if pos is not None else "0",
                    "Speed": p.findtext("Speed") or "0",
                }
            )
            if behaviors.get(p.findtext("Behaviour")):
                pf[-1]["SetsVariables"] = " ".join(behaviors[p.findtext("Behaviour")])
            names.append(p.get("SubtypeId"))
        else:
            # Prefabs with blocks the base game doesn't define, other than MES's
            # terminals, are left out; a group whose main (first) prefab is one of them
            # goes, as does a condition left with nothing to spawn
            ok = [usable(prefabs[n][0].find("CubeGrids"), vanilla) for n in names]
            dropped_prefabs += ok.count(False)
            if not ok or not ok[0]:
                skipped.append(name)
                continue
            index = {}
            for i, keep in enumerate(ok):
                if keep:
                    index[i] = len(index)
            conditions = [c for c in (remap(c, index) for c in conditions) if c]
            if not conditions:
                skipped.append(name)
                continue
            pf = [p for p, keep in zip(pf, ok) if keep]
            for n, keep in zip(names, ok):
                if keep:
                    used[n] = file_name(n)
            t = tags(el.findtext("Description"))
            attrs = {"Name": name, "Mod": mod, "ModName": mod_names[mod]}
            attrs["Frequency"] = f"{float(el.findtext('Frequency') or 1):g}"
            if mod == 0:
                kind, value = base_game_faction(el)
                attrs["BaseGameFaction" + kind] = value
            elif "FactionOverride" in t:
                attrs["FactionOverride"] = last(t, "FactionOverride")
            if flag(t, "EraseIngameScripts"):
                attrs["EraseScripts"] = "true"
            if flag(t, "IgnoreCleanupRules"):
                attrs["IgnoreCleanupRules"] = "true"
            lines.append(f"  <Group{attributes(attrs)}>")
            if persistent is not None:
                p = condition(persistent)
                p.pop("Types")
                lines.append(f"    <Persistent{attributes(p)} />")
            for c in conditions:
                lines.append(f"    <Condition{attributes(c)} />")
            for p in pf:
                lines.append(f"    <Prefab{attributes(p)} />")
            lines.append("  </Group>")

    out = args.out
    (out / "Blueprints").mkdir(parents=True, exist_ok=True)
    for old in (out / "Blueprints").glob("*.bp.sbc.gz"):
        old.unlink()
    for prefab, file in sorted(used.items()):
        xml = blueprint(prefab, prefabs[prefab][0].find("CubeGrids"))
        (out / "Blueprints" / file).write_bytes(gzip.compress(xml.encode(), 9, mtime=0))
    (out / "SpawnGroups.xml").write_text(
        '<?xml version="1.0" encoding="utf-8"?>\n'
        "<!-- Written by tools/export_encounters.py from the MES mods; do not edit -->\n"
        "<SpawnGroups>\n" + "\n".join(lines) + "\n</SpawnGroups>\n"
    )

    faction_lines = []
    for tag, (el, mod) in sorted(factions.items()):
        attrs = {
            "Tag": tag,
            "Name": el.findtext("DisplayName") or tag,
            "Founder": el.get("Founder") or f"{tag} Founder",
            "DefaultRelation": el.findtext("DefaultRelation") or "Enemies",
            "Mod": mod,
        }
        desc = escape((el.findtext("Description") or "").strip())
        faction_lines.append(f"  <Faction{attributes(attrs)}>{desc}</Faction>")
    (out / "Factions.xml").write_text(
        '<?xml version="1.0" encoding="utf-8"?>\n'
        "<!-- Written by tools/export_encounters.py from the MES mods; do not edit -->\n"
        "<Factions>\n" + "\n".join(faction_lines) + "\n</Factions>\n"
    )
    print(
        f"{sum(l.startswith('  <Group') for l in lines)} groups, {len(used)} blueprints, "
        f"{len(factions)} factions; non-vanilla blocks left out {dropped_prefabs} prefabs "
        f"and {len(skipped)} groups"
    )


if __name__ == "__main__":
    main()
