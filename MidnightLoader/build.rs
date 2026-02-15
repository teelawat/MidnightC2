fn main() {
    // Only compile resources on Windows
    if cfg!(target_os = "windows") {
        let mut res = winres::WindowsResource::new();
        
        // Set manifest to require administrator
        res.set_manifest(r#"
<assembly xmlns="urn:schemas-microsoft-com:asm.v1" manifestVersion="1.0">
  <trustInfo xmlns="urn:schemas-microsoft-com:asm.v3">
    <security>
      <requestedPrivileges>
        <requestedExecutionLevel level="asInvoker" uiAccess="false"/>
      </requestedPrivileges>
    </security>
  </trustInfo>
</assembly>
"#);
        
        // Set icon and metadata (optional)
        res.set("ProductName", "Windows Security Service");
        res.set("FileDescription", "Microsoft Security Host");
        res.set("CompanyName", "Microsoft Corporation");
        res.set("LegalCopyright", "Copyright © Microsoft Corporation");
        
        // Compile
        res.compile().unwrap();
    }
}
