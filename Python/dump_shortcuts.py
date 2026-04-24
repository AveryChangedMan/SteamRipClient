import vdf
import json
def dump_shortcuts():
    shortcut_path = r'c:\Program Files (x86)\Steam\userdata\1158533119\config\shortcuts.vdf'
    try:
        with open(shortcut_path, 'rb') as f:
            data = vdf.binary_load(f)
        if 'shortcuts' in data:
            for key, shortcut in data['shortcuts'].items():
                if 'appid' in shortcut:
                    appid_32 = shortcut['appid']
                    appid_64 = ((appid_32 & 0xFFFFFFFF) << 32) | 0x02000000
                    shortcut['appid'] = appid_64
        with open('shortcuts_dump.json', 'w', encoding='utf-8') as out:
            json.dump(data, out, indent=4)
        with open('shortcuts_dump.txt', 'w', encoding='utf-8') as out:
            out.write(f"{'AppID':<22} | {'Hidden':<8} | {'Name'}\n")
            out.write("-" * 80 + "\n")
            if 'shortcuts' in data:
                shortcuts = data['shortcuts']
                for key, shortcut in shortcuts.items():
                    appid = shortcut.get('appid', '')
                    name = shortcut.get('AppName', shortcut.get('appname', 'Unknown'))
                    hidden = shortcut.get('IsHidden', shortcut.get('isHidden', shortcut.get('hidden', 0)))
                    is_hidden = str(hidden) == "1" or hidden == 1
                    out.write(f"{appid:<22} | {str(is_hidden):<8} | {name}\n")
        print("Successfully parsed shortcuts.vdf")
        print("Dumped to shortcuts_dump.txt and shortcuts_dump.json")
    except Exception as e:
        print(f"Error parsing shortcuts.vdf: {e}")
if __name__ == '__main__':
    dump_shortcuts()

