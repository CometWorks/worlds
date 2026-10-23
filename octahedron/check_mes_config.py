"""Check the MES config files we ship against the MES sources.

    python3 check_mes_config.py <MES Data/Scripts/ModularEncountersSystems folder>

For every file in config/storage/1521905890.sbm_ModularEncountersSystems: the root element is the
MES config class the file name loads, every element is a public field of that class (or of
ConfigBase) and its value parses as the field's type. The copy in server/Storage must be identical,
since build_world.py copies config/storage there and server/ is what gets deployed. Exit 1 on any
problem.
"""

import re
import sys
import xml.etree.ElementTree as ET
from pathlib import Path

HERE = Path(__file__).parent
FOLDER = "1521905890.sbm_ModularEncountersSystems"
PARSERS = {
    "bool": lambda v: v in ("true", "false"),
    "int": lambda v: re.fullmatch(r"-?\d+", v) is not None,
    "long": lambda v: re.fullmatch(r"-?\d+", v) is not None,
    "double": lambda v: re.fullmatch(r"-?\d+(\.\d+)?([eE][-+]?\d+)?", v) is not None,
    "float": lambda v: re.fullmatch(r"-?\d+(\.\d+)?([eE][-+]?\d+)?", v) is not None,
}


def fields(source: Path) -> dict[str, str]:
    """Public field name -> type of a MES config class source file."""
    return dict(
        (name, kind)
        for kind, name in re.findall(
            r"^\s*public\s+([\w<>\[\]]+)\s+(\w+)\s*;", source.read_text(encoding="utf-8-sig"), re.M
        )
    )


def main(mes: Path) -> int:
    config = mes / "Configuration"
    base = fields(config / "ConfigBase.cs")
    problems = []
    shipped = sorted((HERE / "config/storage" / FOLDER).glob("*.xml"))
    for path in shipped:
        root = ET.parse(path).getroot()
        source = config / f"{root.tag}.cs"
        expected = "Config" + path.stem.removeprefix("Config-")
        if root.tag != expected.replace("ConfigConfig", "Config") or not source.exists():
            problems.append(f"{path.name}: root <{root.tag}> is not the class this file loads")
            continue
        known = base | fields(source)
        for element in root:
            kind = known.get(element.tag)
            if kind is None:
                problems.append(f"{path.name}: <{element.tag}> is not a field of {root.tag}")
            elif kind in PARSERS and not PARSERS[kind]((element.text or "").strip()):
                problems.append(f"{path.name}: <{element.tag}> = {element.text!r} is not a {kind}")
        deployed = HERE / "server/Storage" / FOLDER / path.name
        if not deployed.exists() or deployed.read_bytes() != path.read_bytes():
            problems.append(f"{path.name}: server/Storage copy differs, run build_world.py or copy it")
    for line in problems:
        print("FAIL", line)
    print(f"{'FAIL' if problems else 'PASS'}: {len(shipped)} MES config files, {len(problems)} problems")
    return 1 if problems else 0


if __name__ == "__main__":
    sys.exit(main(Path(sys.argv[1])))
