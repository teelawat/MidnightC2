# 🌙 Midnight C2 v0.6.8: *The "It Works on My Machine" Edition*

Welcome to **Midnight C2**, a hybrid C2 agent that tries its best to be stealthy, but mostly just tries its best to exist. It's a Frankenstein's monster of **Rust** (the brain) and **C#** (the brawn), living together in the same process like roommates who don't really get along but have to share the rent.

> **⚠️ DISCLAIMER:** This project is for educational purposes and ethical security testing only. If you use this to do anything illegal, you're on your own. Also, if it breaks your computer, don't say I didn't warn you.

---

## 🛠 What's inside (The Cool Stuff)

We've upgraded the architecture to be more "tactical" (which is a fancy word for "harder to see"):

*   **Hybrid In-Process Execution:** The Rust loader (the `.exe`) literally hosts the .NET runtime inside itself. The C# Agent doesn't even have its own process. It's like a ghost in the machine.
*   **Bypasses? We've heard of them:**
    *   **AMSI Bypass:** We patch `AmsiScanBuffer` because we don't like being judged by Antivirus.
    *   **ETW Bypass:** We patch `EtwEventWrite` because what Windows doesn't know won't hurt us.
    *   **Defender Exclusion:** We ask Windows Defender nicely (via PowerShell) to please ignore our folder. It usually says yes.
*   **Rust-Powered Stability:** The main loop is in Rust, which means it will keep trying to restart the C# agent even if it crashes (which it will).
*   **Telegram C2:** Control everything via Telegram. Because why build a web UI when Mark Zuckerberg (or Pavel Durov) already did it for you?

---

## 🤡 The "Manage Your Expectations" Section

Before you go thinking you've found the next APT tool, please keep the following in mind:

1.  **Antivirus is smart, we are not:** While we have bypasses, Windows Defender is a billion-dollar product. If it catches you, don't be surprised.
2.  **Network issues:** If the internet drops for 0.001 seconds, the agent might decide to take a permanent nap. We added a retry loop, but even that has its limits.
3.  **The "3-Second Rule":** After installation, the UI closes in 3 seconds. Why 3? Because 2 felt too short and 4 felt like an eternity. 
4.  **Admin Rights:** If you don't run as Admin, nothing works. Period. Don't even try. It'll just show you a pretty black window and tell you "No."
5.  **It's a Lab Project:** This was built for fun and learning. Expect bugs. Many, many bugs. Some might even be accidental features.

---

## 🚀 How to Build (If you're feeling brave)

1.  **Configure your Bot:**
    *   Edit `MidnightAgent/Core/Config.cs` with your Telegram Bot Token and User ID.
2.  **Run the Magic Script:**
    *   Just run `build_all.bat`. It will compile the C# DLL, then the Rust Loader, and bundle them together into one glorious `SecurityHost.exe`.
3.  **Deploy:**
    *   Take the file from `MidnightLoader/target/release/midnight_loader.exe` (or rename it to `SecurityHost.exe`).
    *   Run as Admin.
    *   Pray to the God of Stealth.

---

## 📄 Final Warning
Actually, don't pray. Just assume it will work 50% of the time, 100% of the time. If you find a bug, fix it yourself or join the club of people who like to watch things burn.

**Happy Hacking!** 🌙🚀
