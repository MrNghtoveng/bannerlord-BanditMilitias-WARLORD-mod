from pathlib import Path
import xml.etree.ElementTree as ET


def read_strings(filepath):
    tree = ET.parse(filepath)
    root = tree.getroot()
    strings_node = root.find('strings')
    return {child.get('id'): child.get('text') for child in strings_node.findall('string')}


def add_missing(target_file, source_dict):
    tree = ET.parse(target_file)
    root = tree.getroot()
    strings_node = root.find('strings')

    target_ids = {child.get('id') for child in strings_node.findall('string')}
    added_count = 0
    for sid, text in source_dict.items():
        if sid not in target_ids:
            new_node = ET.Element('string')
            new_node.set('id', sid)
            new_node.set('text', text)
            new_node.tail = '
        '
            strings_node.append(new_node)
            added_count += 1

    if added_count > 0:
        tree.write(target_file, encoding='utf-8', xml_declaration=True)
        content = target_file.read_text(encoding='utf-8')
        content = content.replace("<?xml version='1.0' encoding='utf-8'?>", '<?xml version="1.0" encoding="utf-8"?>')
        target_file.write_text(content, encoding='utf-8')

    print(f"Added {added_count} strings to {target_file}")


PROJECT_ROOT = Path(__file__).resolve().parent
en_file = PROJECT_ROOT / 'ModuleData' / 'Languages' / 'EN' / 'std_BanditMilitias_xml_en.xml'
tr_file = PROJECT_ROOT / 'ModuleData' / 'Languages' / 'TR' / 'std_BanditMilitias_xml_tr.xml'

en_strings = read_strings(en_file)
tr_strings = read_strings(tr_file)

add_missing(en_file, tr_strings)
add_missing(tr_file, en_strings)
