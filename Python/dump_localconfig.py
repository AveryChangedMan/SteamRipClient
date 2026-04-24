import vdf
import json
def dump_localconfig():
    localconfig_path = r'c:\Program Files (x86)\Steam\userdata\1158533119\config\localconfig.vdf'
    try:
        with open(localconfig_path, 'r', encoding='utf-8') as f:
            data = vdf.load(f)
        with open('localconfig_dump.json', 'w', encoding='utf-8') as out:
            json.dump(data, out, indent=4)
        print("Successfully parsed localconfig.vdf")
        print("Dumped to localconfig_dump.json")
    except Exception as e:
        print(f"Error parsing localconfig.vdf: {e}")
if __name__ == '__main__':
    dump_localconfig()

