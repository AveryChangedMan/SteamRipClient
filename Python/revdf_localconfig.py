import vdf
import json
def revdf_localconfig():
    try:
        with open('localconfig_dump.json', 'r', encoding='utf-8') as f:
            data = json.load(f)
        with open('localconfig_modified.vdf', 'w', encoding='utf-8') as f:
            vdf.dump(data, f, pretty=True)
        print("Successfully rebuilt localconfig_modified.vdf from JSON!")
        print("You can replace Steam's localconfig.vdf with this new file.")
    except Exception as e:
        print(f"Error rebuilding localconfig: {e}")
if __name__ == '__main__':
    revdf_localconfig()

