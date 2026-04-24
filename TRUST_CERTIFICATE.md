# How to Trust the SteamRipApp Certificate 🛡️

To install the MSIX package on your machine, you must once-manually trust the developer certificate. This resolves the **0x800B010A** verification error.

### Step-by-Step Instructions:

1.  **Locate the File**: Find `SteamRipApp.pfx` in the application folder.
2.  **Right-Click**: Select **"Install PFX"** (or Install Certificate).
3.  **Store Location**: Select **"Local Machine"** and click Next.
4.  **Enter Password**: Enter exactly `steamrip` and click Next.
5.  **Placement**: 
    - Select **"Place all certificates in the following store"**.
    - Click **Browse**.
    - Select **"Trusted Root Certification Authorities"** (This is crucial).
6.  **Finish**: Click OK, Next, and Finish.
7.  **Confirm**: Click "Yes" on any Windows security prompt.

### ✅ Done!
You can now run the **`SteamRipApp.msix`** installer. Windows will recognize the application as signed and trusted.

> [!TIP]
> You only need to do this **once** on your machine. Future updates signed with the same certificate will install automatically.
