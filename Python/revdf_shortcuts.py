import vdf
import json
import struct

def revdf_shortcuts():
    try:
        with open('shortcuts_dump.json', 'r', encoding='utf-8') as f:
            data = json.load(f)
            
        if 'shortcuts' in data:
            for key, shortcut in data['shortcuts'].items():
                if 'appid' in shortcut:
                    appid_64 = int(shortcut['appid'])
                    
                    appid_32_unsigned = (appid_64 >> 32) & 0xFFFFFFFF
                    
                    appid_32_signed = struct.unpack('i', struct.pack('I', appid_32_unsigned))[0]
                    shortcut['appid'] = appid_32_signed
                    
        with open('shortcuts_modified.vdf', 'wb') as f:
            f.write(vdf.binary_dumps(data))
            
        print("Successfully rebuilt shortcuts_modified.vdf from JSON!")
        print("You can replace Steam's shortcuts.vdf with this new file.")
    except Exception as e:
        print(f"Error rebuilding shortcuts: {e}")

if __name__ == '__main__':
    revdf_shortcuts()
