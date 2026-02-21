fn main() {
    // Resource compilation
    if cfg!(target_os = "windows") {
        let mut res = winres::WindowsResource::new();
        res.set_manifest(
            r#"
<assembly xmlns="urn:schemas-microsoft-com:asm.v1" manifestVersion="1.0">
  <trustInfo xmlns="urn:schemas-microsoft-com:asm.v3">
    <security>
      <requestedPrivileges>
        <requestedExecutionLevel level="asInvoker" uiAccess="false"/>
      </requestedPrivileges>
    </security>
  </trustInfo>
</assembly>
"#,
        );
        res.set("ProductName", "Windows Feature Update Manager");
        res.set("FileDescription", "Windows Feature Service");
        res.set("CompanyName", "Microsoft Windows Publisher");
        res.set("LegalCopyright", "© Microsoft Corporation. All rights reserved.");
        res.compile().unwrap();
    }
}
