from pathlib import Path
import xml.etree.ElementTree as ET


def remove_duplicates(filepath):
    tree = ET.parse(filepath)
    root = tree.getroot()
    strings_node = root.find('strings')

    seen = set()
    to_remove = []

    for child in strings_node.findall('string'):
        sid = child.get('id')
        if sid in seen:
            to_remove.append(child)
        else:
            seen.add(sid)

    for child in to_remove:
        strings_node.remove(child)

    if to_remove:
        tree.write(filepath, encoding='utf-8', xml_declaration=True)
        content = filepath.read_text(encoding='utf-8')
        content = content.replace("<?xml version='1.0' encoding='utf-8'?>", '<?xml version="1.0" encoding="utf-8"?>')
        filepath.write_text(content, encoding='utf-8')
        print(f"Removed {len(to_remove)} duplicates from {filepath}")
    else:
        print(f"No duplicates in {filepath}")


PROJECT_ROOT = Path(__file__).resolve().parent
tr_file = PROJECT_ROOT / 'ModuleData' / 'Languages' / 'TR' / 'std_BanditMilitias_xml_tr.xml'
remove_duplicates(tr_file)

en_file = PROJECT_ROOT / 'ModuleData' / 'Languages' / 'EN' / 'std_BanditMilitias_xml_en.xml'
remove_duplicates(en_file)
