import xml.etree.ElementTree as ET
import glob
import os

# Relative paths for portability
base_dir = os.path.dirname(os.path.abspath(__file__))
files = glob.glob(os.path.join(base_dir, "**", "*.xml"), recursive=True)

# Also check SubModule.xml in parent dir
submodule_file = os.path.join(base_dir, "..", "SubModule.xml")
if os.path.exists(submodule_file):
    files.append(os.path.abspath(submodule_file))

for file in files:
    filename = os.path.basename(file)
    print(f"\nChecking: {filename}")
    try:
        tree = ET.parse(file)
        root = tree.getroot()
        print("  -> [OK] Well Formed!")
        
        # NPCCharacters validation
        if filename in ["bandits.xml", "lords.xml"]:
            if root.tag != "NPCCharacters":
                print(f"  -> [ERROR] Root element must be NPCCharacters, got: {root.tag}")
            
            # Check duplicate NPCCharacter IDs (top-level elements)
            character_ids = []
            for character in root.findall("NPCCharacter"):
                char_id = character.get("id")
                if char_id:
                    character_ids.append(char_id)
            
            duplicates = set([x for x in character_ids if character_ids.count(x) > 1])
            if duplicates:
                print("  -> [ERROR] Duplicate NPCCharacter IDs found:")
                for d in duplicates:
                    print(f"      - {d}")
            else:
                print(f"  -> [OK] All {len(character_ids)} NPCCharacter IDs are unique.")
        
        # SubModule.xml validation
        if filename == "SubModule.xml":
            if root.tag != "Module":
                print(f"  -> [ERROR] Root element must be Module, got: {root.tag}")
                
    except ET.ParseError as e:
        print(f"  -> [ERROR] Malformed XML: {e}")
