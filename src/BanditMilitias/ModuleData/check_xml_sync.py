import xml.etree.ElementTree as ET

files = {
    'EN': r'Languages/EN/std_BanditMilitias_xml_en.xml',
    'TR': r'Languages/TR/std_BanditMilitias_xml_tr.xml'
}

results = {}
for lang, path in files.items():
    tree = ET.parse(path)
    root = tree.getroot()
    ids = {s.get('id') for s in root.findall('.//string')}
    results[lang] = ids
    print(f'{lang}: {len(ids)} strings - XML VALID')

en = results['EN']
tr = results['TR']
missing_in_tr = en - tr
missing_in_en = tr - en

if missing_in_tr:
    print(f'MISSING in TR ({len(missing_in_tr)}): {sorted(missing_in_tr)}')
else:
    print('OK: TR has all EN strings')

if missing_in_en:
    print(f'MISSING in EN ({len(missing_in_en)}): {sorted(missing_in_en)}')
else:
    print('OK: EN has all TR strings')

if not missing_in_tr and not missing_in_en:
    print('\nPERFECT SYNC! Both files have identical string IDs.')
