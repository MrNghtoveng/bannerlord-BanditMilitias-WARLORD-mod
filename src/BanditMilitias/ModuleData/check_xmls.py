import xml.etree.ElementTree as ET
import glob
import os

files = glob.glob(r"c:\Users\firat\Desktop\MyModdingProject\source\0\BanditMilitias WARLORD\BanditMilitias\ModuleData\**\*.xml", recursive=True)

for file in files:
    filename = os.path.basename(file)
    print(f"\nChecking: {filename}")
    try:
        tree = ET.parse(file)
        root = tree.getroot()
        print("  -> Well Formed!")
        
        if filename in ["bandits.xml", "lords.xml"] and root.tag != "NPCCharacters":
            print(f"  -> [ERROR] Root element must be NPCCharacters, got: {root.tag}")
        
        # Check duplicate IDs inside bandits and lords
        if filename in ["bandits.xml", "lords.xml"]:
            ids = []
            for elem in tree.iter():
                if "id" in elem.attrib:
                    ids.append(elem.attrib["id"])
            
            duplicates = set([x for x in ids if ids.count(x) > 1])
            if duplicates:
                print("  -> [ERROR] Duplicate IDs found:")
                for d in duplicates:
                    print(f"      - {d}")
                    
    except ET.ParseError as e:
        print(f"  -> [ERROR] Malformed XML: {e}")

# Validate SubModule.xml basic structure
submodule_path = r"c:\Users\firat\Desktop\MyModdingProject\source\0\BanditMilitias WARLORD\BanditMilitias\SubModule.xml"
if os.path.exists(submodule_path):
    filename = os.path.basename(submodule_path)
    print(f"\nChecking: {filename}")
    try:
        tree = ET.parse(submodule_path)
        root = tree.getroot()
        print("  -> Well Formed!")
        
        if root.tag != "Module":
            print(f"  -> [ERROR] Root element must be Module, got: {root.tag}")
        else:
            print("  -> [OK] Root element is Module (Use C# validator for full Module.xsd validation)")
    except ET.ParseError as e:
        print(f"  -> [ERROR] Malformed XML: {e}")
