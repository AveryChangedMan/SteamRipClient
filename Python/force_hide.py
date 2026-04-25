import json

def force_hide_localconfig():
    with open('localconfig_dump.json', 'r', encoding='utf-8') as f:
        data = json.load(f)
        
    apps = data.setdefault('UserLocalConfigStore', {}).setdefault('Software', {}).setdefault('Valve', {}).setdefault('Steam', {}).setdefault('apps', {})
    
    
    appid = "12514375952760832000"
    
    if appid not in apps:
        apps[appid] = {}
        
    
    apps[appid]['tags'] = {"0": "hidden"}
    
    apps[appid]['Hidden'] = "1"
    apps[appid]['visibility'] = "0"
    
    with open('localconfig_dump.json', 'w', encoding='utf-8') as out:
        json.dump(data, out, indent=4)
        
    print("Injected hidden flags into localconfig_dump.json")

if __name__ == '__main__':
    force_hide_localconfig()
