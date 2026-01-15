# MidnightC2

MidnightC2 is a powerful, C#-based Command & Control (C2) framework designed for authorized red team operations and security research. It leverages **Telegram** as a communication channel, providing a stealthy and reliable way to manage remote agents.

## ⚠️ Disclaimer
**This project is for Educational Purposes and Authorized Security Testing ONLY.**
The developer is not responsible for any misuse of this tool. Do not use this against systems you do not own or do not have explicit permission to test.

## 🚀 Features

MidnightC2 comes packed with advanced post-exploitation features:

### 🖥️ Remote Access & Control
- **Remote Shell**: Execute system commands silently.
- **Process Management**: List and kill running processes.
- **File Manager**: Upload and download files from the target.
- **VNC & AnyDesk**: Full remote desktop control capabilities.

### 🕵️ Surveillance & Monitoring
- **Webcam Streaming**: Live stream or capture photos from connected webcams.
- **Screen Streaming**: View the target's screen in real-time.
- **Keylogger**: Capture keystrokes (supports offline logging).
- **Location Tracking**: Approximate geolocation of the target.
- **System Information**: Gather detailed OS and hardware specs.

### 🔐 Data Extraction
- **Cookie Stealer**: Extract cookies from Chrome, Edge, and Brave browsers.
- **Bypass Capability**: Uses remote debugging protocol to bypass browser encryption (App-Bound encryption).

### 🛡️ Persistence & Stealth
- **Startup Persistence**: Automatically runs on system boot.
- **Self Destruct**: Remotely remove the agent and all traces.
- **Anti-Analysis**: (Optional) Sandbox and VM detection.

## 🛠️ Build Instructions

### Prerequisites
- Visual Studio 2019/2022 (Community or higher)
- .NET Framework 4.7.2 or higher
- Telegram Bot Token & Chat ID

### How to Build
1. **Clone the Repository**
   ```bash
   git clone https://github.com/teelawat/MidnightC2.git
   ```
2. **Open the Solution**
   Open `MidnightC2.sln` in Visual Studio.
3. **Build the Builder**
   - Select `Release` configuration.
   - Build the `MidnightBuilder` project.
4. **Run the Builder**
   - Navigate to `MidnightBuilder/bin/Release` (or run from VS).
   - Enter your **Telegram Bot Token**.
   - Enter your **Telegram User ID**.
   - Specify the output filename.
5. **Deploy**
   - The verified payload will be generated in the `Output` folder.

## 📡 Communication
All commands and results are sent via your private Telegram Bot. This ensures traffic looks like legitimate HTTPS application traffic.

## 🤝 Contributing
Pull requests are welcome. For major changes, please open an issue first to discuss what you would like to change.

## 📜 License
This project is licensed under the MIT License - see the LICENSE file for details.
