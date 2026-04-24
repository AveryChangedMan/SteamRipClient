import struct
import json
import os
def parse_appinfo(filepath):
    results = []
    with open(filepath, 'rb') as f:
        magic = struct.unpack('<I', f.read(4))[0]
        if magic != 0x07564429:
            print("Unsupported magic:", hex(magic))
            return []
        universe = struct.unpack('<I', f.read(4))[0]
        string_table_offset = struct.unpack('<Q', f.read(8))[0]
        f.seek(string_table_offset)
        count = struct.unpack('<I', f.read(4))[0]
        strings = []
        for _ in range(count):
            s = b""
            while True:
                c = f.read(1)
                if c == b'\x00' or not c:
                    break
                s += c
            strings.append(s.decode('utf-8', 'ignore'))
        f.seek(16)
        while True:
            pos_before_appid = f.tell()
            if pos_before_appid >= string_table_offset:
                break
            appid_bytes = f.read(4)
            if not appid_bytes or len(appid_bytes) < 4:
                break
            appid = struct.unpack('<I', appid_bytes)[0]
            if appid == 0:
                break
            size = struct.unpack('<I', f.read(4))[0]
            start_pos = f.tell()
            state = struct.unpack('<I', f.read(4))[0]
            last_updated = struct.unpack('<I', f.read(4))[0]
            access_token = struct.unpack('<Q', f.read(8))[0]
            checksum = f.read(20)
            change_number = struct.unpack('<I', f.read(4))[0]
            data_hash = f.read(20)
            def parse_node():
                obj = {}
                while True:
                    t_bytes = f.read(1)
                    if not t_bytes:
                        break
                    t = t_bytes[0]
                    if t == 8:
                        break
                    key_idx_bytes = f.read(4)
                    if len(key_idx_bytes) < 4:
                        break
                    key_idx = struct.unpack('<I', key_idx_bytes)[0]
                    key = strings[key_idx] if key_idx < len(strings) else f"unknown_{key_idx}"
                    if t == 0:
                        obj[key] = parse_node()
                    elif t == 1:
                        s = b""
                        while True:
                            c = f.read(1)
                            if c == b'\x00' or not c:
                                break
                            s += c
                        obj[key] = s.decode('utf-8', 'ignore')
                    elif t == 2:
                        obj[key] = struct.unpack('<i', f.read(4))[0]
                    elif t == 3:
                        obj[key] = struct.unpack('<f', f.read(4))[0]
                    elif t == 4:
                        obj[key] = struct.unpack('<i', f.read(4))[0]
                    elif t == 5:
                        s = b""
                        while True:
                            c = f.read(2)
                            if c == b'\x00\x00' or not c:
                                break
                            s += c
                        obj[key] = s.decode('utf-16', 'ignore')
                    elif t == 6:
                        obj[key] = struct.unpack('<I', f.read(4))[0]
                    elif t == 7:
                        obj[key] = struct.unpack('<q', f.read(8))[0]
                    else:
                        break
                return obj
            try:
                root_type_bytes = f.read(1)
                if root_type_bytes and root_type_bytes[0] == 0:
                    key_idx = struct.unpack('<I', f.read(4))[0]
                    key = strings[key_idx] if key_idx < len(strings) else f"unknown_{key_idx}"
                    data = parse_node()
                    app_data = {
                        'appid': appid,
                        'name': 'Unknown',
                        'hidden': False
                    }
                    if 'common' in data:
                        common = data['common']
                        app_data['name'] = common.get('name', 'Unknown')
                        hidden_val = common.get('hidden', 0)
                        app_data['hidden'] = str(hidden_val) == "1" or hidden_val == 1
                    results.append(app_data)
            except Exception as e:
                print(f"Error parsing appid {appid}: {e}")
            f.seek(start_pos + size)
    return results
if __name__ == '__main__':
    apps = parse_appinfo('appinfo.vdf')
    print(f"Extracted {len(apps)} apps.")
    with open('appinfo_dump.txt', 'w', encoding='utf-8') as f:
        f.write(f"{'AppID':<15} | {'Hidden':<8} | {'Name'}\n")
        f.write("-" * 80 + "\n")
        for app in apps:
            f.write(f"{app['appid']:<15} | {str(app['hidden']):<8} | {app['name']}\n")
    print("Dumped to appinfo_dump.txt")

