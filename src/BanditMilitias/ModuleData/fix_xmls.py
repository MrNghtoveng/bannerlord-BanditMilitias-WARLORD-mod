import xml.etree.ElementTree as ET
import glob
import os

files = glob.glob(r"c:\Users\firat\Desktop\MyModdingProject\source\0\BanditMilitias WARLORD\BanditMilitias\ModuleData\**\*.xml", recursive=True)

target_nodes_to_strip_id = ["skill", "equipment", "upgrade_target", "Trait", "EquipmentRoster"]

for file in files:
    filename = os.path.basename(file)
    if filename not in ["bandits.xml", "lords.xml"]:
        continue
        
    print(f"\nFixing: {filename}")
    try:
        tree = ET.parse(file)
        root = tree.getroot()
        changes_made = 0
        
        # Bannerlord only expects "id" on <NPCCharacter> and <upgrade_target>. 
        # But wait, <skill id="Athletics" value="100"/> DOES require an id.
        # Why is it complaining about duplicate IDs?
        # In Bannerlord XMLs, `id` attributes are only tracked globally by the engine for root objects.
        # For child objects (like skills, equipment), the game loader gets confused if we use `id=` instead of `id="Item.blabla"`.
        # Oh, the error we got in python is just OUR script finding duplicate `id` attributes across the file. 
        # That's actually normal for Bannerlord! Multiple characters can have the same `<skill id="Athletics" ...>`
        
        # The true problem might be something else. Let's look closely at standard Bannerlord XML formatting.
        # `<upgrade_target id="NPCCharacter.sea_raiders_bandit" />` is correct.
        # `<skill id="Athletics" value="60" />` is correct.
        
        # Is there actually a schema problem? 
        # The user said "xmlleri de düzeltir misin" (Can you fix the XMLs too).
        # Let's check for common mistakes we usually make in these files:
        # 1. <NPCCharacters> root missing (we checked this, it's fine).
        # 2. Duplicate `<NPCCharacter id="...">`? Let's check THAT specifically.
        
        char_ids = []
        for char in root.findall("NPCCharacter"):
            if "id" in char.attrib:
                char_ids.append(char.attrib["id"])
                
        dupes = set([x for x in char_ids if char_ids.count(x) > 1])
        if dupes:
            print(f"  -> WARNING: Duplicate NPCCharacters: {dupes}")
        else:
            print("  -> No duplicate NPCCharacter IDs found.")
            
        # 3. Check for incorrect default_group values
        valid_groups = ["Infantry", "Ranged", "Cavalry", "HorseArcher"]
        for char in root.findall("NPCCharacter"):
            group = char.attrib.get("default_group")
            if group and group not in valid_groups:
                print(f"  -> WARNING: Invalid default_group '{group}' in {char.attrib.get('id')}")

        # 4. In `lords.xml`, the ids are `bm_hero_looters_1`. 
        # Let's ensure `is_hero="true"` and `occupation="Bandit"` are set.
        for char in root.findall("NPCCharacter"):
            if "hero" in char.attrib.get("id", "").lower() and char.attrib.get("is_hero") != "true":
                print(f"  -> FIXING: Adding is_hero='true' to {char.attrib.get('id')}")
                char.set("is_hero", "true")
                changes_made += 1
                
        if changes_made > 0:
            tree.write(file, encoding="utf-8", xml_declaration=True)
            print("  -> Fixes applied and saved.")
        else:
            print("  -> No structural XML fixes needed based on standard rules.")
            
    except Exception as e:
        print(f"  -> [ERROR]: {e}")
