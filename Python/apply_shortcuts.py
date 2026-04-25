import os
import shutil
import time
import subprocess
from revdf_shortcuts import revdf_shortcuts
from revdf_localconfig import revdf_localconfig

def apply_and_clear_cache():
    
    print("Rebuilding vdf files from JSON...")
    revdf_shortcuts()
    revdf_localconfig()
    
    
    print("Stopping Steam to apply changes...")
    try:
        subprocess.run(['taskkill', '/F', '/IM', 'steam.exe'], stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
        subprocess.run(['taskkill', '/F', '/IM', 'steamwebhelper.exe'], stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
        time.sleep(3) 
    except Exception as e:
        print("Steam was not running or couldn't be killed.")

    
    steam_shortcuts_path = r'c:\Program Files (x86)\Steam\userdata\1158533119\config\shortcuts.vdf'
    modified_shortcuts_path = 'shortcuts_modified.vdf'
    
    steam_localconfig_path = r'c:\Program Files (x86)\Steam\userdata\1158533119\config\localconfig.vdf'
    modified_localconfig_path = 'localconfig_modified.vdf'
    
    if os.path.exists(modified_shortcuts_path):
        shutil.copy2(modified_shortcuts_path, steam_shortcuts_path)
        print("Copied modified shortcuts to Steam directory.")
        
    if os.path.exists(modified_localconfig_path):
        shutil.copy2(modified_localconfig_path, steam_localconfig_path)
        print("Copied modified localconfig to Steam directory.")

    
    
    htmlcache_path = r'C:\Users\sam23\AppData\Local\Steam\htmlcache'
    
    if os.path.exists(htmlcache_path):
        print(f"Clearing Steam Web Cache at: {htmlcache_path}")
        try:
            shutil.rmtree(htmlcache_path)
            print("Successfully cleared cache. Steam will rebuild it on next launch!")
        except Exception as e:
            print(f"Warning: Could not delete cache folder. Is a Steam process still running? Error: {e}")
    else:
        print("Cache already cleared or doesn't exist.")
        
    print("\nAll done! You can now launch Steam. Your game should be hidden!")

if __name__ == '__main__':
    apply_and_clear_cache()
