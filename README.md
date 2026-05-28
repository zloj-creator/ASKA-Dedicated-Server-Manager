# ASKA Dedicated Server Manager (v1.3.4)

### Main Dashboard
<img width="656" height="758" alt="Screenshot 2026-05-04 113421" src="https://github.com/user-attachments/assets/b6040d45-98af-499c-ac97-95268a91a52d" />

<br><br>
### Configuration Editor
<img width="666" height="693" alt="Screenshot 2026-05-04 112804" src="https://github.com/user-attachments/assets/3357ccf2-5ace-4a64-a3e0-fd7f70110340" />
<br><br>
<img width="666" height="693" alt="Screenshot 2026-05-04 112835" src="https://github.com/user-attachments/assets/0267390e-eeb3-4211-b3fd-227d6831e472" />


**Powerful and user-friendly GUI tool for managing your ASKA dedicated servers.** 
Designed for stability, ease of use, and automated maintenance.

---

### 🚀 Key Features
*   **Easy Setup:** Automatic server installation and updates via integrated SteamCMD.
*   **Advanced Config Editor:** Intuitive management of `server_properties.txt` with logical grouping and world generation safety locks.
*   **Automated Backups:** Customizable backup intervals with a built-in cleanup system for old archives.
*   **Live Monitoring:** Real-time server logs, player activity tracking (Join/Leave), and game statistics.
*   **Player Notifications:** Audio alerts (Join/Leave) so you never miss a player.
*   **Plugin Support:** Fully compatible with *AskaMonitor* for advanced game-time and season tracking.

### 🧩 AskaMonitor Plugin (Optional)
To see game time, and seasons, Days Survived, you need to install the monitoring plugin:
1.  Install **BepInEx 5.4** into your server directory.
2.  Download `AskaMonitor.dll` from our [Releases](https://github.com/zloj-creator/ASKA-Dedicated-Server-Manager/releases)) page.
3.  Place `AskaMonitor.dll` into the `BepInEx/plugins/` folder on your server.

---

### 🛠 Quick Start
1. **Download:** Get the latest version from the [Releases](https://github.com/zloj-creator/ASKA-Dedicated-Server-Manager/releases)) section.
2. **Configure:** Run the Manager and open **Settings (⚙️)**.
3. **Paths:** Point to your server directory and configuration file.
4. **Launch:** Use the **Server** menu or type `start` in the console.
5. Check "list" in console for available commands

---

### 📖 FAQ Summary
*   **How to get a Token?** Generate a GSLT at [Steam Game Servers](https://steamcommunity.com/dev/managegameservers)) using **App ID: 1898300**.
*   **Why are world settings locked?** Generation settings (Seed, Terrain, Mode) can only be changed **BEFORE** the world is created for the first time.
*   **Troubleshooting:** If SteamCMD fails, run the Manager as **Administrator** and add the folder to your Antivirus exclusions.

---

### 📋 Requirements
*   **Operating System:** Windows 10 / 11 (64-bit).
*   **Runtime:** [.NET 10.0 Desktop Runtime](https://microsoft.com) (or newer) must be installed.
*   **SteamCMD:** Integrated (Manager will handle this for you).


---
*Developed by zloj-creator. MIT License — Feel free to use and improve!*
