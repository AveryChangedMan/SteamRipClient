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
                    Category = "REPAIR & UPDATES[unstable]",
                    Question = "What is 'Smart Update'?",
                    Answer = "Smart Update allows you to update your games without downloading the whole thing again. It uses our 'Leap' parser to scan the remote archive and only downloads the specific files that have changed, saving you gigabytes of bandwidth.",
                    Keywords = "smart update bandwidth speed leap parser"
                },
                new HelpItem {
                    Category = "REPAIR & UPDATES",
                    Question = "What is a '.rip_map.json' file?",
                    Answer = "This is a metadata map of your game's files. It stores the structure and checksums (CRC32) of every file. The app uses this map to instantly detect if a file is missing or corrupted and can fix it precisely.",
                    Keywords = "rip_map metadata crc32 fix repair"
                },
                new HelpItem {
                    Category = "REPAIR & UPDATES",
                    Question = "What are 'How To Run' notes?",
                    Answer = "Some games require special steps like enabling Developer Mode or running a specific batch file. The app automatically scrapes these instructions from the game page and shows a 'Message' icon on the game card if instructions are available.",
                    Keywords = "how to run instructions developer mode note"
                },
                new HelpItem {
                    Category = "GENERAL",
                    Question = "How do I open the Game Configuration menu?",
                    Answer = "Go to the 'Library' tab and click on any game in the list. A panel (or overlay) will appear with all the settings like AppID, Launch Arguments, and Executable selection.",
                    Keywords = "how open config menu settings"
                },
                new HelpItem {
                    Category = "GENERAL",
                    Question = "Why is the Steam tab unstable?",
                    Answer = "The Steam tab uses a web-scraping method to show the Steam Store. Because Steam frequently changes its website layout, this tab might occasionally show formatting errors. The 'Library' and 'Downloads' tabs are fully native and stable.",
                    Keywords = "steam unstable why broken"
                },
                new HelpItem {
                    Category = "DOWNLOADS",
                    Question = "Why is my download speed slow?",
                    Answer = "Speeds depend on the host (Buzzheavier or GoFile), or it can your wifi. The app uses multiple concurrent connections to maximize speed, but server congestion or your ISP can still limit the throughput. Buzzheavier is usually the fastest option.",
                    Keywords = "slow download speed gofile buzzheavier isp"
                },
                new HelpItem {
                    Category = "DOWNLOADS",
                    Question = "Can I pause and resume downloads?",
                    Answer = "The app will look for games in your Download Directory by default. The app saves session data. If you close the app or lose internet, you can resume exactly where you left off. The app also performs a quick integrity check after resume to ensure no data was corrupted during the interruption.",
                    Keywords = "pause resume progress session"
                },
                new HelpItem {
                    Category = "PERFORMANCE",
                    Question = "Which extraction engine should I use?",
                    Answer = "The 'Native Extraction Engine (Bundled)' is the default. However, if you have WinRAR or 7-Zip installed, using them can be slightly faster for massive archives. You can toggle this in Settings.",
                    Keywords = "extraction engine winrar 7zip native"
                },
                new HelpItem {
                    Category = "LIBRARY",
                    Question = "How do I add an existing game to the Library?",
                    Answer = "Go to the Settings tab and add the folder containing your games to 'Scan Directories'. The app will scan the folders, find the executables, and fetch the metadata and cover art automatically.",
                    Keywords = "add game existing folder scan"
                },
                new HelpItem {
                    Category = "LIBRARY",
                    Question = "What if the app identifies the wrong game?",
                    Answer = "You can manually change the EXE path in the game configuration. Use the 'Search' (Magnifying Glass) icon next to the game cover to search the SteamRip database and pick the correct match.",
                    Keywords = "wrong game identify appId search"
                },
                new HelpItem {
                    Category = "CLEANUP",
                    Question = "What does CleanUp do?",
                    Answer = "CleanUp scans for temporary download chunks, old installers, and abandoned session data. It helps you reclaim disk space by safely removing files that are no longer needed by the app.",
                    Keywords = "cleanup temporary space delete"
                },
                new HelpItem {
                    Category = "LIBRARY",
                    Question = "What should I do if a game cover is missing or incorrect?",
                    Answer = "Click on the game in your Library to open the configuration panel. Then, click the 'Magnifying Glass' icon next to the game cover. This will search the Steam database and allow you to pick the correct game and artwork.",
                    Keywords = "missing cover image wrong art fix search"
                },
                new HelpItem {
                    Category = "REPAIR & UPDATES",
                    Question = "How do I know if a game has an update available?",
                    Answer = "The app automatically checks for newer versions of your installed games. If an update is found, a gold 'UPDATE AVAILABLE' badge will appear on the game card. You can click it to start a Smart Update immediately.",
                    Keywords = "update available badge gold notify"
                },
                new HelpItem {
                    Category = "TROUBLESHOOTING",
                    Question = "How do I report a bug?",
                    Answer = "Detailed logs are stored in your AppData/Local/SteamRipApp folder. You can provide these logs to the development team on the official GitHub for faster resolution.",
                    Keywords = "bug report logs github"
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