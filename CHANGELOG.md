# SteamRipApp v1.4.8.6 Changelog

- Fixed application crashes during shutdown by explicitly cancelling background tasks and timers.
- Improved extraction reliability by aligning with 64-bit UnRAR SDK and adding automatic CLI fallbacks.
- Added real-time byte-based progress tracking for smooth visual feedback during all operations.
- Optimized game verification using metadata fingerprinting to skip redundant file hashing.
- Implemented delta updates using the Leap remote RAR scanning system to save bandwidth.
- Added right-click context menus for launching, configuration, and Steam shortcut integration.
- Included a guided setup walkthrough for antivirus exclusions and performance optimization.
- Synchronized all internal metadata and manifests to version 1.4.8.6.
- Implemented SIMD Vectorization and Memory-Mapped Files for high-speed disk I/O and hashing.
- Developed a recursive self-healing search to automatically find and re-link shifted game files.
- Added a quarantine system and mods tracking to protect user-added game modifications.
- Integrated a scoring algorithm for intelligent automatic detection of game executables and launchers.
- Transitioned to the UnRAR Extended API to support archives with Unicode paths.
- Implemented a persistent repair state system to allow for data recovery after application restarts.
- Resolved memory access violations by enforcing proper calling conventions for native DLL imports.
- Added a sequential hashing queue to prevent disk contention when processing multiple games.
- Warning: The update function is currently considered unstable.
