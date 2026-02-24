# 🌙 Midnight C2 v0.6.54: *The Stealth Update*

## ⚠️ Project Status: MAINTENANCE MODE
**Notice:** This project is no longer under active development. Updates will be infrequent and may cease entirely.

**Why?**
I'm honestly exhausted from the endless cycle of coding, debugging, and testing on both real machines and VMs. Balancing this project with my studies has meant many sleepless nights after school, and despite all the effort, I haven't quite reached all the goals I set for myself. 

The tool **does work**, but if you're planning to use it with **WinPE**, I can't guarantee it will work as expected. If you don't see any more updates, consider the project discontinued. Use at your own risk.

## 📝 Description
**Midnight C2** is a hybrid Command & Control agent designed for stealth and persistence. It combines a **Rust Loader** (for low-level system interaction and assembly hosting) with a **C# Agent** (for feature-rich payload execution).

> **⚠️ DISCLAIMER:** This project is for educational purposes and authorized security testing only. The author is not responsible for any misuse or damage caused by this tool.

---

## 🛠 Features (v0.6.54)
*   **Hybrid Execution:** Rust loader hosts the .NET CLR and executes the C# Agent in-process.
*   **Alternative Data Stream (ADS) Loading:** The Agent DLL is hidden within the `:metadata` stream of the executable, bypassing basic file-based detection.
*   **In-Memory Auto-Updater:** Updates are downloaded as ZIP files from Dropbox directly into RAM and executed without writing the update to disk.
*   **AMSI & ETW Bypasses:** Dynamic patching of `AmsiScanBuffer` and `EtwEventWrite` to evade detection.
*   **Telegram Command & Control:** Control your agents through a simple Telegram Bot interface.
*   **Robust Persistence:** Enforced UTF-16 Task Scheduler XML for reliable persistence in `C:\ProgramData\Microsoft\Windows\Security`.

---

## 🚀 Build Instructions

### Prerequisites
1.  **Visual Studio 2022:** Install "Desktop development with C++" and ".NET desktop development".
2.  **Rust:** Install via [rustup.rs](https://rustup.rs/). Make sure the `x86_64-pc-windows-msvc` toolchain is present.
3.  **.NET SDK:** Required for building the C# project.

### Step-by-Step Build
1.  **Configuration:** 
    *   Open `MidnightAgent/Core/Config.cs`.
    *   Set your `BotToken` and `ChatId`.
    *   (Optional) Adjust `Version` and `BuildNumber`.
2.  **Build All:**
    *   Open a CMD/PowerShell terminal in the root directory.
    *   Run the provided build script:
    ```cmd
    build_all.bat
    ```
    *   *Note: The script first builds the C# Agent because the Rust Loader needs to embed the resulting DLL.*
3.  **Output:**
    *   Your final stealth executable will be located at:
    `MidnightLoader\target\release\midnight_loader.exe`

---

## 🕹 Deployment & Usage

### Manual Execution
*   Copy `midnight_loader.exe` to the target.
*   Run as **Administrator** for full persistence and features.
*   The agent will install itself to `C:\ProgramData\Microsoft\Windows\Security\SecurityHost.exe`.

### Offline Injection (WinPE)
*   **Option A (Automatic):** Use `build_winpe.ps1` to create a bootable `.iso`. Booting from this ISO will automatically inject the payload into the target Windows drive.
*   **Option B (Manual):** Use `winpe_injector.ps1` from a WinPE environment (like HBCD) to manually inject the payload into a target system's offline Registry and Tasks.

---

## 📄 Support & Updates
As stated above, this project is in maintenance mode. 
*   **Bug Reports:** Feel free to open issues, but they may not be addressed.
*   **Contributions:** Pull requests are welcome if you wish to improve the tool yourself.

**Happy Hacking!** 🌙🚀
