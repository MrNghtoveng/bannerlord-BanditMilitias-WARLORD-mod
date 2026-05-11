from pathlib import Path
import xml.etree.ElementTree as ET


PROJECT_ROOT = Path(__file__).resolve().parents[1]
MODULE_DATA_DIR = PROJECT_ROOT / "ModuleData"
SUBMODULE_PATH = PROJECT_ROOT / "SubModule.xml"


def validate_xml(path: Path) -> None:
    filename = path.name
    print(f"\nChecking: {filename}")

    try:
        tree = ET.parse(path)
        root = tree.getroot()
        print("  -> Well Formed!")

        if filename in {"bandits.xml", "lords.xml"} and root.tag != "NPCCharacters":
            print(f"  -> [ERROR] Root element must be NPCCharacters, got: {root.tag}")

        if filename in {"bandits.xml", "lords.xml"}:
            ids = [elem.attrib["id"] for elem in root.findall("NPCCharacter") if "id" in elem.attrib]
            duplicates = sorted({value for value in ids if ids.count(value) > 1})
            if duplicates:
                print("  -> [ERROR] Duplicate IDs found:")
                for duplicate in duplicates:
                    print(f"      - {duplicate}")
    except ET.ParseError as exc:
        print(f"  -> [ERROR] Malformed XML: {exc}")


for xml_file in sorted(MODULE_DATA_DIR.rglob("*.xml")):
    validate_xml(xml_file)


if SUBMODULE_PATH.exists():
    print(f"\nChecking: {SUBMODULE_PATH.name}")
    try:
        tree = ET.parse(SUBMODULE_PATH)
        root = tree.getroot()
        print("  -> Well Formed!")

        if root.tag != "Module":
            print(f"  -> [ERROR] Root element must be Module, got: {root.tag}")
        else:
            print("  -> [OK] Root element is Module (use C# validator for full Module.xsd validation)")
    except ET.ParseError as exc:
        print(f"  -> [ERROR] Malformed XML: {exc}")
