
# MidnightC2

**MidnightC2** is a lightweight, stealthy Remote Administration Tool (RAT) / Command and Control (C2) agent that utilizes **Telegram** as its communication channel. This allows for secure, encrypted, and convenient control of remote machines directly from your Telegram app.

> **⚠️ DISCLAIMER: THIS SOFTWARE IS FOR EDUCATIONAL PURPOSES AND ETHICAL SECURITY TESTING ONLY. DO NOT USE ON SYSTEMS YOU DO NOT OWN OR HAVE EXPLICIT PERMISSION TO TEST. THE AUTHOR IS NOT RESPONSIBLE FOR ANY MISUSE.**

## ✨ Features

MidnightC2 comes packed with a wide range of powerful features for system administration and monitoring:

*   **Remote Shell**: Execute system commands (CMD/PowerShell) remotely.
*   **File Management**:
    *   Download files from the target.
    *   Upload files to the target.
    *   Navigate directories (`cd`, `ls`).
*   **Surveillance**:
    *   **Screenshot**: Capture not-detected screenshots.
    *   **Webcam**: Snap pictures or stream webcam feed.
    *   **Keylogger**: Log keystrokes.
    *   **Location**: Get approximate device location.
*   **System Interaction**:
    *   **Process Manager**: List and kill processes.
    *   **System Info**: Get detailed hardware and OS information.
    *   **Power Control**: Shutdown or Restart the machine.
    *   **Self Destruct**: Uninstall and remove traces of the agent.
*   **Network & Connectivity**:
    *   **Reverse Shell**: Spawn a persistent reverse shell session.
    *   **VNC**: Remote desktop viewing.
    *   **AnyDesk**: Enable/Configure AnyDesk for remote access.
    *   **FTP**: FTP server capabilities.
*   **Browser Data**:
    *   **Cookie Stealer**: Extract browser cookies.
    *   **Clear Cookies**: Wipe browser data.
*   **Other**:
    *   **Wallpaper**: Change the desktop wallpaper.
    *   **Job Management**: Manage background tasks.

## 🚀 Getting Started

### Prerequisites

*   Windows OS
*   [.NET Framework 4.7.2](https://dotnet.microsoft.com/download/dotnet-framework/net472) or higher
*   [Visual Studio 2019/2022](https://visualstudio.microsoft.com/) (for building)

### Installation & Building

You can build the agent easily using the included **MidnightBuilder** tool.

1.  **Clone the repository:**
    ```bash
    git clone https://github.com/teelawat/MidnightC2.git
    cd MidnightC2
    ```

2.  **Create a Telegram Bot:**
    *   Open Telegram and search for `@BotFather`.
    *   Send `/newbot` and follow the instructions to get your **HTTP API Token**.
    *   Get your numeric **User ID** (you can use `@userinfobot` to find this).

3.  **Build the Agent:**
    *   Open the `MidnightC2.sln` in Visual Studio and build the **MidnightBuilder** project.
    *   Run `MidnightBuilder.exe` (located in `MidnightBuilder/bin/Debug` or `Release`).
    *   Follow the on-screen prompts:
        *   Enter your **Bot Token**.
        *   Enter your **User ID**.
        *   Specify the output filename (e.g., `SecurityHost.exe`).
    *   The builder will automatically compile the agent with your configuration injected.

4.  **Output:**
    *   The compiled agent will be available in the `Output` folder.

## 📖 Usage

1.  **Deploy**: Transfer the generated executable to the target machine.
2.  **Run**: Execute the file. It will run silently in the background (unless configured otherwise).
3.  **Control**:
    *   Open your Telegram Bot.
    *   The bot will send a "Online" notification when the agent connects.
    *   Type `/help` to see a full list of available commands.

### Common Commands

*   `/help` - Show all commands.
*   `/shell <command>` - Run a CMD command.
*   `/screenshot` - Take a screenshot.
*   `/download <path>` - Download a file from the target.
*   `/upload` - Upload a file (reply to the file with this command).
*   `/kill` - Kill the agent process.
*   `/selfdestruct` - Remove the agent from the system.

## 🛠 Project Structure

*   `MidnightAgent/`: The core agent source code.
    *   `Features/`: Individual feature implementations (plugins).
    *   `Core/`: Core logic and configuration.
*   `MidnightBuilder/`: The builder tool to configure and compile the agent.

## 🤝 Contributing

Contributions are welcome! Please feel free to submit a Pull Request.

## 📄 License

This project is licensed under the MIT License - see the LICENSE file for details.
