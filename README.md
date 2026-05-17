# SteamRipApp

A desktop client for downloading, extracting, and managing local PC games. 

## Features

- **Downloads:** Downloads files using multiple threads. Supports GoFile and Buzzheavier.
- **Extraction:** Automatically extracts downloaded archives using UnRarDLL, WinRAR, or 7-Zip.
- **Library:** Scans local directories and organizes games with cover images and metadata.
- **Repair:** Hashes local files to detect broken or missing components and downloads replacements.
- **Redistributables:** Scans game folders to check if required software (like DirectX or Visual C++) is missing.

## Instructions

1. Download and run SteamRipApp.
2. Complete the setup (occurs on every update as well).
3. Use the Home tab to search and download games.
4. The app will download and extract the files and then add it to your library.
5. Click Launch inside library, or add to steam if you prefer.

*Current Version: 1.5.2.9*
# Images

<img width="1919" height="1027" alt="image" src="https://github.com/user-attachments/assets/7f0385ee-51ef-47d4-9f45-ba8465ba3fb3" />
<img width="1919" height="1038" alt="image" src="https://github.com/user-attachments/assets/e181da2e-3919-4c80-9ba7-219541ea7cfc" />
<img width="1919" height="1029" alt="image" src="https://github.com/user-attachments/assets/48111f5f-f074-4fee-af07-d7b31392e868" />
<img width="1919" height="1032" alt="image" src="https://github.com/user-attachments/assets/01a922d0-636a-48d4-9bcd-8335f5888481" />

# How to use
### 📦 Option 1: MSIX Installer (Recommended)
Because the MSIX package is self-signed for development, Windows requires you to manually trust the certificate before it allows the installation.

#### 1. Trust the Certificate
Before opening the MSIX file, you **must** run the certificate batch script.
*   Locate `TrustCertificate.bat` in your download folder.
*   **Right-click** the file and select **"Run as Administrator"**.
*   This script adds the app's signature to your "Trusted Root Certification Authorities" and "Trusted People" stores. Without this, Windows will show a "Publisher Unknown" error and block the install.

#### 2. Run the MSIX
*   Double-click the `.msix` (or `.msixbundle`) file.
*   Click **Install**.
*   Windows will manage the installation, Start Menu shortcuts, and clean uninstalls.


### 📂 Option 2: Binary ZIP Files (Portable)
Use this version if you want to run the app from an external drive or avoid system-level installation.

1.  **Extract All:** Right-click the `.zip` and extract it to a permanent folder (e.g., `Desktop`). 
    *   *Warning:* Do not run the app from inside the compressed folder
2.  **Launch:** Run the primary `SteamRipApp.exe` within the extracted folder.

---
<br>
<sub><i>Tags:
steamrip, steamripapp, steamripclient, free game downloader, steamrip game managermsix</i></sub>
