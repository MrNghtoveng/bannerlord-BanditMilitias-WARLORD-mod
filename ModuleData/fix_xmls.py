from pathlib import Path
import xml.etree.ElementTree as ET


PROJECT_ROOT = Path(__file__).resolve().parents[1]
MODULE_DATA_DIR = PROJECT_ROOT / "ModuleData"
VALID_GROUPS = {"Infantry", "Ranged", "Cavalry", "HorseArcher"}


for xml_file in sorted(MODULE_DATA_DIR.glob("*.xml")):
    if xml_file.name not in {"bandits.xml", "lords.xml"}:
        continue

    print(f"\nFixing: {xml_file.name}")
    try:
        tree = ET.parse(xml_file)
        root = tree.getroot()
        changes_made = 0

        char_ids = [char.attrib["id"] for char in root.findall("NPCCharacter") if "id" in char.attrib]
        duplicates = sorted({value for value in char_ids if char_ids.count(value) > 1})
        if duplicates:
            print(f"  -> WARNING: Duplicate NPCCharacters: {', '.join(duplicates)}")
        else:
            print("  -> No duplicate NPCCharacter IDs found.")

        for char in root.findall("NPCCharacter"):
            group = char.attrib.get("default_group")
            if group and group not in VALID_GROUPS:
                print(f"  -> WARNING: Invalid default_group '{group}' in {char.attrib.get('id')}")

        for char in root.findall("NPCCharacter"):
            char_id = char.attrib.get("id", "")
            if "hero" in char_id.lower() and char.attrib.get("is_hero") != "true":
                print(f"  -> FIXING: Adding is_hero='true' to {char_id}")
                char.set("is_hero", "true")
                changes_made += 1

        if changes_made > 0:
            tree.write(xml_file, encoding="utf-8", xml_declaration=True)
            print("  -> Fixes applied and saved.")
        else:
            print("  -> No structural XML fixes needed based on standard rules.")
    except Exception as exc:
        print(f"  -> [ERROR]: {exc}")
