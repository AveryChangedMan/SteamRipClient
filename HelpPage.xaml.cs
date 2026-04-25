using Microsoft.UI.Xaml.Controls;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;

namespace SteamRipApp
{
    public class HelpItem
    {
        public string Question { get; set; } = "";
        public string Answer { get; set; } = "";
        public string Category { get; set; } = "";
        public string Keywords { get; set; } = "";
    }

    public sealed partial class HelpPage : Page
    {
        private List<HelpItem> _allHelpItems = new List<HelpItem>();
        private ObservableCollection<HelpItem> _filteredItems = new ObservableCollection<HelpItem>();

        public HelpPage()
        {
            this.InitializeComponent();
            LoadHelpItems();
            HelpList.ItemsSource = _filteredItems;
            FilterItems("");
        }

        private void LoadHelpItems()
        {
            _allHelpItems = new List<HelpItem>
            {
                
                new HelpItem {
                    Category = "TROUBLESHOOTING",
                    Question = "What if the game doesn't open, or opens and immediately closes?",
                    Answer = "This is often caused by an incompatible emulator patch. Try clicking 'Reverse Goldberg Patch' in the game configuration menu. If you don't care about the game page showing up in Steam, it is MUCH safer to leave Goldberg OFF.",
                    Keywords = "closes immediately doesn't open fix undo goldberg"
                },
                new HelpItem {
                    Category = "TROUBLESHOOTING",
                    Question = "What if the game says 'No License' or opens a Steam Store page?",
                    Answer = "First, try applying the Goldberg Patch. If it's already applied and still says 'No License', try reversing it. Some games require specific cracks that Goldberg might interfere with.",
                    Keywords = "no license steam store page fix undo goldberg"
                },
                new HelpItem {
                    Category = "GENERAL",
                    Question = "How do I open the Game Configuration menu?",
                    Answer = "Go to the 'Library' tab and click on any game in the list. A right-hand panel (or overlay) will appear with all the settings like AppID, Goldberg Patch, and Launch Arguments.",
                    Keywords = "how open config menu settings"
                },
                new HelpItem {
                    Category = "GENERAL",
                    Question = "How does the Configuration menu work?",
                    Answer = "The menu allows you to customize how each game behaves. You can change the Steam AppID for integration, apply compatibility patches, set CPU priority, or add custom launch arguments like '-windowed'. Changes are saved automatically when you click 'Save'.",
                    Keywords = "config menu how works explanation"
                },
                new HelpItem {
                    Category = "TROUBLESHOOTING",
                    Question = "What if my game controller isn't working?",
                    Answer = "Ensure you have 'Integrated' the game into Steam and are launching it THROUGH Steam. Steam's 'Input' system is usually required to translate controller signals for non-Steam games.",
                    Keywords = "controller game pad not working fix"
                },
                new HelpItem {
                    Category = "GENERAL",
                    Question = "Why is the Steam tab marked as 'Unstable'?",
                    Answer = "The Steam tab uses a web-scraping method to show the Steam Store. Because Steam frequently changes its website layout, this tab might occasionally break or show formatting errors. The 'Library' and 'Downloads' tabs are much more stable.",
                    Keywords = "steam unstable why broken"
                },

                
                new HelpItem {
                    Category = "GOLDBERG PATCH",
                    Question = "What is the Goldberg Patch?",
                    Answer = "The Goldberg Patch is a Steam API emulator that allows games to run without the Steam client. It replaces the original steam_api.dll or steam_api64.dll with a version that tells the game 'Yes, you are logged in and authorized' locally.",
                    Keywords = "goldberg patch emulator api dll"
                },
                new HelpItem {
                    Category = "GOLDBERG PATCH",
                    Question = "Why would I want to apply the Goldberg Patch?",
                    Answer = "The primary reason is visual: it makes Steam show the official 'Steam Game Page' (with achievements, community, and backgrounds) instead of a generic 'Non-Steam Game' page. If you want your library to look professional, this is how you do it.",
                    Keywords = "why apply goldberg benefit launch crack visual style"
                },
                new HelpItem {
                    Category = "GOLDBERG PATCH",
                    Question = "Why might I NOT want to apply the Goldberg Patch?",
                    Answer = "If you don't care about the Steam UI style, leave it OFF. It modifies game files (DLLs) which can trigger antivirus flags or cause crashes in games that already have their own cracks.",
                    Keywords = "why not goldberg multiplayer online fix conflict stability"
                },
                new HelpItem {
                    Category = "GOLDBERG PATCH",
                    Question = "Can I reverse the Goldberg Patch?",
                    Answer = "Yes. The app creates a .bak backup of your original DLLs. Clicking 'Reverse Goldberg Patch' in the config menu will restore your original files and delete the emulator configuration.",
                    Keywords = "reverse goldberg undo backup restore"
                },
                new HelpItem {
                    Category = "GOLDBERG PATCH",
                    Question = "Does the Goldberg Patch support multiplayer?",
                    Answer = "Standard Goldberg is for offline use. While it supports LAN play in some games, it generally does not provide access to official Steam matchmaking or servers.",
                    Keywords = "multiplayer goldberg lan matchmaking servers"
                },

                
                new HelpItem {
                    Category = "STEAM INTEGRATION",
                    Question = "What is 'Steam Integration'?",
                    Answer = "It's an unstable bridge that allows you to launch games from the official Steam Library page. It is mainly for 'Style' and 'Big Picture Mode' enthusiasts. If you prefer stability, avoid using this feature.",
                    Keywords = "steam integration bridge overlay big picture unstable"
                },
                new HelpItem {
                    Category = "STEAM INTEGRATION",
                    Question = "Why use Steam Integration instead of just adding a shortcut manually?",
                    Answer = "Integration automatically manages AppID mapping, downloads official Steam assets (banners, icons), and provides a 'Play' button inside the actual Steam Store/Library pages for that game.",
                    Keywords = "manual shortcut benefit assets banners icon"
                },
                new HelpItem {
                    Category = "STEAM INTEGRATION",
                    Question = "What are '--worker' shortcuts?",
                    Answer = "These are hidden shortcuts the app adds to Steam. They act as background listeners so that when you click 'Play' in Steam, our app intercepts the request and launches the actual game with your custom settings.",
                    Keywords = "worker shortcut hidden background listener"
                },
                new HelpItem {
                    Category = "STEAM INTEGRATION",
                    Question = "Will Steam Integration get my account banned?",
                    Answer = "No. The integration uses official Steam features (Non-Steam Shortcuts) and standard debugging ports. It does not modify Steam's core files or memory.",
                    Keywords = "ban safety account risk"
                },
                new HelpItem {
                    Category = "STEAM INTEGRATION",
                    Question = "Why does Steam restart when I integrate a game?",
                    Answer = "Steam only reads its shortcut configuration file during startup. To ensure your new game appears in your library immediately, the app must restart Steam to reload the config.",
                    Keywords = "restart steam reload library config"
                },

                
                new HelpItem {
                    Category = "DOWNLOADS",
                    Question = "Why is my download speed slow?",
                    Answer = "Speeds are primarily limited by the host (GoFile). The app uses up to 4 concurrent connections to maximize speed, but server congestion or your ISP can still limit the throughput.",
                    Keywords = "slow download speed gofile isp congestion"
                },
                new HelpItem {
                    Category = "DOWNLOADS",
                    Question = "What is 'GoFile Authentication'?",
                    Answer = "GoFile sometimes requires an account token for high-speed downloads. You can add your token in Settings to bypass guest limits and ensure more stable connections.",
                    Keywords = "gofile auth token settings account"
                },
                new HelpItem {
                    Category = "DOWNLOADS",
                    Question = "Can I pause and resume downloads?",
                    Answer = "Yes. The app saves '.progress' files. If you close the app or lose internet, you can resume exactly where you left off without losing data.",
                    Keywords = "pause resume progress save"
                },
                new HelpItem {
                    Category = "DOWNLOADS",
                    Question = "What does 'Automatic Extraction' do?",
                    Answer = "When enabled, the app will automatically unzip the game files to your library folder once the download finishes, saving you the manual step of using WinRAR or 7-Zip.",
                    Keywords = "automatic extraction unzip zip 7z rar"
                },
                new HelpItem {
                    Category = "DOWNLOADS",
                    Question = "Where are my downloads stored?",
                    Answer = "Temporary chunks are stored in your AppData/Local/SteamRipApp folder. Once complete, they are moved to your specified 'Library' folder.",
                    Keywords = "storage folder path where"
                },

                
                new HelpItem {
                    Category = "PERFORMANCE",
                    Question = "What is 'CPU Priority'?",
                    Answer = "Setting a game to 'High' priority tells Windows to give that game more processor time than other apps. This can help reduce stuttering in CPU-heavy games.",
                    Keywords = "cpu priority stutter processor"
                },
                new HelpItem {
                    Category = "PERFORMANCE",
                    Question = "Why would I want a 'Launch Delay'?",
                    Answer = "Some games crash if launched too quickly after Steam starts. A delay ensures all background services (like the Bridge) are fully ready before the game executable runs.",
                    Keywords = "launch delay crash background service"
                },
                new HelpItem {
                    Category = "PERFORMANCE",
                    Question = "What are 'Fullscreen Optimizations'?",
                    Answer = "Windows 10/11 tries to improve game performance with 'optimizations', but they can sometimes cause screen tearing or alt-tab issues. Disabling them can improve stability for older games.",
                    Keywords = "fullscreen optimization screen tearing alt tab stability"
                },
                new HelpItem {
                    Category = "PERFORMANCE",
                    Question = "What is 'High DPI Scaling'?",
                    Answer = "If a game looks blurry or the mouse cursor is tiny on a 4K monitor, enabling High DPI override forces the game to render at your screen's native crispness.",
                    Keywords = "dpi scaling blurry mouse 4k cursor"
                },
                new HelpItem {
                    Category = "PERFORMANCE",
                    Question = "Does 'Run as Administrator' help?",
                    Answer = "Yes, especially for games that save data in their own folder. Without Admin rights, Windows might block the game from saving your progress.",
                    Keywords = "admin administrator permissions save data"
                },

                
                new HelpItem {
                    Category = "REDISTS",
                    Question = "What are 'Redistributables'?",
                    Answer = "These are shared libraries (DirectX, VC++, .NET) that games need to run. If you get a 'Missing DLL' error, you are likely missing a Redistributable.",
                    Keywords = "redist dll missing directx vc++ .net"
                },
                new HelpItem {
                    Category = "REDISTS",
                    Question = "How does the app detect missing Redists?",
                    Answer = "The app scans your Windows Registry and System32 folder to see which versions of Visual C++ and DirectX are installed, then compares them to the game's requirements.",
                    Keywords = "detect missing registry system32 scan"
                },
                new HelpItem {
                    Category = "REDISTS",
                    Question = "Why should I NOT install all Redists?",
                    Answer = "There is no harm in having them, but installing unneeded versions just takes up disk space. The app only recommends what is strictly necessary for your specific games.",
                    Keywords = "why not install space"
                },
                new HelpItem {
                    Category = "REDISTS",
                    Question = "What is 'VC++ 2015-2022'?",
                    Answer = "This is the most common runtime. Most modern games require the x64 version. If a game won't start at all, this is the first thing to check.",
                    Keywords = "vc++ 2015 2022 runtime modern"
                },
                new HelpItem {
                    Category = "REDISTS",
                    Question = "Can the app install Redists automatically?",
                    Answer = "The app identifies what you need and can launch the installers for you, but Windows requires you to click 'Yes' on the UAC prompt for each one.",
                    Keywords = "automatic install uac"
                },

                
                new HelpItem {
                    Category = "LIBRARY",
                    Question = "How do I add an existing game to the Library?",
                    Answer = "Go to the Library tab and click 'Add Folder'. Once selected, the app will scan the folder for an executable and try to identify the game automatically.",
                    Keywords = "add game existing folder scan"
                },
                new HelpItem {
                    Category = "LIBRARY",
                    Question = "What if the app identifies the wrong game?",
                    Answer = "You can manually change the AppID in the game configuration. Search for the game on SteamDB or use our built-in search tool to find the correct ID.",
                    Keywords = "wrong game identify appId steamdb"
                },
                new HelpItem {
                    Category = "LIBRARY",
                    Question = "Can I hide games from the Library?",
                    Answer = "Yes. In the config menu, you can toggle visibility. This is useful for hiding DLCs or 'Worker' shortcuts that you don't want to see in the list.",
                    Keywords = "hide game dlc visibility"
                },
                new HelpItem {
                    Category = "LIBRARY",
                    Question = "What are the 'Status Badges' in the Library?",
                    Answer = "Green means Integrated (Steam), Blue means Patched (Goldberg), and Red means missing Redistributables. Hover over them for more info.",
                    Keywords = "badge status colors green blue red"
                },
                new HelpItem {
                    Category = "LIBRARY",
                    Question = "How do I update a game?",
                    Answer = "The app does not support 'delta patches' yet. You should download the new version from SteamRIP and the app will detect the updated files in your library folder.",
                    Keywords = "update version new"
                },
                new HelpItem {
                    Category = "LIBRARY",
                    Question = "Why is my game cover missing or incorrect?",
                    Answer = "Covers are fetched automatically using the Steam AppID. If a cover is missing, click the game in your library and use the 'Magnifying Glass' (Search) icon in the config panel to manually search for the correct game cover.",
                    Keywords = "missing cover image wrong art fix"
                },
                new HelpItem {
                    Category = "LIBRARY",
                    Question = "What should I do after downloading a new game?",
                    Answer = "We highly recommend pressing the 'Magnifying Glass' (Search) icon in the game configuration panel after every new download. This ensures the app correctly identifies the game and applies the highest quality cover art and metadata available.",
                    Keywords = "new download what do magnifying glass search cover"
                },
                new HelpItem {
                    Category = "LIBRARY",
                    Question = "Can I change the cover art to a custom image?",
                    Answer = "Currently, the app supports searching the Steam database for covers. Custom local image support is planned for a future update. For now, use the Magnifying Glass to find the best official match.",
                    Keywords = "custom cover image art change"
                },

                
                new HelpItem {
                    Category = "CLEANUP",
                    Question = "What is 'CleanUp'?",
                    Answer = "CleanUp scans for temporary download chunks, old installers, and empty folders that are no longer needed, helping you reclaim disk space.",
                    Keywords = "cleanup temporary space delete"
                },
                new HelpItem {
                    Category = "CLEANUP",
                    Question = "Is it safe to delete everything in CleanUp?",
                    Answer = "Yes. The app only targets files that are redundant or temporary. It will never delete your actual game files or save data.",
                    Keywords = "safe delete data"
                },
                new HelpItem {
                    Category = "CLEANUP",
                    Question = "Why are there so many 'Shader Caches'?",
                    Answer = "Some games generate large caches to improve performance. You can delete them to save space, but the game might stutter slightly the next time you play as it regenerates them.",
                    Keywords = "shader cache stutter space"
                },
                new HelpItem {
                    Category = "SAFETY",
                    Question = "Does the app send data to SteamRIP?",
                    Answer = "The app only talks to SteamRIP to fetch game info and download links. It does not send any personal information or your library contents to any servers.",
                    Keywords = "privacy data server safety"
                },
                new HelpItem {
                    Category = "SAFETY",
                    Question = "Why does my Antivirus flag the Goldberg Patch?",
                    Answer = "Antivirus software often flags DLL emulators as 'GameHack' or 'PUP' (Potentially Unwanted Program) because they modify how a program behaves. It is a false positive.",
                    Keywords = "antivirus virus false positive gamehack pup"
                },

                
                new HelpItem {
                    Category = "TROUBLESHOOTING",
                    Question = "The app says 'Steam Debugging Disabled'?",
                    Answer = "The app needs Steam to be in 'Developer Mode' to talk to its UI. The app will automatically try to relaunch Steam with the correct flags if this happens.",
                    Keywords = "debugging disabled developer mode relaunch"
                },
                new HelpItem {
                    Category = "TROUBLESHOOTING",
                    Question = "My game doesn't show up in Steam Big Picture?",
                    Answer = "Ensure you have 'Integrated' the game in the Library tab. If it still doesn't show, try 'Removing' and 'Re-adding' it to force a shortcut refresh.",
                    Keywords = "big picture missing refresh"
                },
                new HelpItem {
                    Category = "TROUBLESHOOTING",
                    Question = "I get a 'Disk Write Error' during download?",
                    Answer = "Check if your drive is full or if you have write permissions to the folder. Try running the app as Administrator or changing the library folder to a different drive.",
                    Keywords = "disk write error full permissions"
                },
                new HelpItem {
                    Category = "TROUBLESHOOTING",
                    Question = "The app keeps crashing on the Downloads page?",
                    Answer = "This was a known bug in older versions related to UI thread safety. Ensure you are on the latest version (2.0+) where this has been stabilized.",
                    Keywords = "crash downloads stabilized bug"
                },
                new HelpItem {
                    Category = "TROUBLESHOOTING",
                    Question = "How do I report a bug?",
                    Answer = "Check the 'Native Bridge' tab for detailed logs. You can copy these logs and provide them to the development team on the official Discord or GitHub.",
                    Keywords = "bug report logs discord github"
                }
            };
        }

        private void HelpSearchBox_TextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args)
        {
            FilterItems(sender.Text);
        }

        private void FilterItems(string query)
        {
            _filteredItems.Clear();
            var results = _allHelpItems.Where(i => 
                i.Question.Contains(query, StringComparison.OrdinalIgnoreCase) || 
                i.Answer.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                i.Keywords.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                i.Category.Contains(query, StringComparison.OrdinalIgnoreCase)
            ).ToList();

            foreach (var item in results) _filteredItems.Add(item);
            NoResultsText.Visibility = _filteredItems.Count == 0 ? Microsoft.UI.Xaml.Visibility.Visible : Microsoft.UI.Xaml.Visibility.Collapsed;
        }
    }
}
