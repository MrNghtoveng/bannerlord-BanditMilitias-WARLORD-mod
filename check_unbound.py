import xml.etree.ElementTree as ET
import os

def check_unbound():
    root = os.path.dirname(os.path.abspath(__file__))
    module_data = os.path.join(root, 'ModuleData')
    bandits_path = os.path.join(module_data, 'bandits.xml')
    lords_path = os.path.join(module_data, 'lords.xml')
    
    local_units = set()
    hero_units = set()
    upgrade_targets = set()
    unit_upgrades_to = {} # unit id -> list of targets
    
    for path in [bandits_path, lords_path]:
        if not os.path.exists(path): continue
        tree = ET.parse(path)
        root = tree.getroot()
        for npc in root.findall('.//NPCCharacter'):
            uid = npc.get('id')
            if uid:
                local_units.add(uid)
                if npc.get('is_hero') == 'true':
                    hero_units.add(uid)
                unit_upgrades_to[uid] = []
                upgrades_node = npc.find('upgrade_targets')
                if upgrades_node is not None:
                    for target in upgrades_node.findall('upgrade_target'):
                        tid = target.get('id')
                        if tid:
                            tid = tid.replace('NPCCharacter.', '')
                            upgrade_targets.add(tid)
                            unit_upgrades_to[uid].append(tid)

    # 1. Targets outside the local mod graph.
    # Most of these are valid vanilla troops, so report them separately.
    external_targets = upgrade_targets - local_units

    # 2. Local units that no one upgrades to.
    unreachable = local_units.copy()
    for targets in unit_upgrades_to.values():
        for t in targets:
            if t in unreachable:
                unreachable.remove(t)

    intentional_roots = {
        'looter',
        *hero_units,
    }
    orphan_local_units = sorted(u for u in unreachable if u not in intentional_roots)

    print("=== EXTERNAL TARGETS (Usually valid vanilla troop links) ===")
    for t in sorted(external_targets):
        sources = [u for u, targets in unit_upgrades_to.items() if t in targets]
        print(f" - {t} (Targeted by: {', '.join(sources)})")

    print("\n=== INTENTIONAL ROOT UNITS (Entry/Hero nodes) ===")
    for u in sorted(intentional_roots):
        if u in local_units:
            print(f" - {u}")

    print("\n=== ORPHAN LOCAL UNITS (Defined locally, no inbound upgrade path) ===")
    if orphan_local_units:
        for u in orphan_local_units:
            print(f" - {u}")
    else:
        print(" - None")
        
    print("\n=== UNIT UPGRADE TREES ===")
    for u, targets in unit_upgrades_to.items():
        if targets:
            print(f" {u} -> {', '.join(targets)}")
        else:
            print(f" {u} -> (End of Line)")

if __name__ == '__main__':
    check_unbound()
