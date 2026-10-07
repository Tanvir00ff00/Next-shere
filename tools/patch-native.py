from pathlib import Path
base = Path('third_party/open-quickshare/core_lib/src')
def edit(relative, old, new):
    path = base / relative
    data = path.read_text(encoding='utf-8')
    if old not in data: raise RuntimeError(f'Missing patch anchor {relative}: {old[:60]}')
    path.write_text(data.replace(old, new), encoding='utf-8')
edit('hdl/inbound.rs', 'use std::os::unix::fs::FileExt;', 'use std::io::{Seek, SeekFrom, Write};\ntrait FileExt { fn write_all_at(&self, data: &[u8], offset: u64) -> std::io::Result<()>; }\nimpl FileExt for File { fn write_all_at(&self, data: &[u8], offset: u64) -> std::io::Result<()> { let mut file = self.try_clone()?; file.seek(SeekFrom::Start(offset))?; file.write_all(data) } }')
edit('hdl/outbound.rs', 'use std::os::unix::fs::MetadataExt;', '')
edit('hdl/outbound.rs', '.size()', '.len()')
edit('hdl/inbound.rs', '    socket: S,', '    socket: S,\n    pub destination: std::path::PathBuf,')
edit('hdl/inbound.rs', '            socket,\n            state:', '            socket,\n            destination: get_download_dir().join(&id),\n            state:')
edit('hdl/inbound.rs', 'let mut dest = get_download_dir();', 'let mut dest = self.destination.clone();')
edit('hdl/inbound.rs', 'destination: Some(get_download_dir().to_string_lossy().into_owned()),', 'destination: Some(self.destination.to_string_lossy().into_owned()),')
edit('hdl/inbound.rs', '            for file in &introduction.file_metadata {', '''            if introduction.file_metadata.len() > 512 { return Err(anyhow!("Too many attachments")); }
            std::fs::create_dir_all(&self.destination)?;
            for file in &introduction.file_metadata {
                validate_received_name(file.name())?;
                if file.size() < 0 || file.size() > 16_i64 * 1024 * 1024 * 1024 { return Err(anyhow!("Attachment too large")); }
                if self.state.transferred_files.contains_key(&file.payload_id()) { return Err(anyhow!("Duplicate payload ID")); }''')
edit('hdl/inbound.rs', 'let resolved_name = dest_file_name(file.name(), file.mime_type());', 'let resolved_name = file.name().to_string();')
edit('hdl/inbound.rs', 'files_name.push(resolved_name.clone());', 'files_name.push(info_name);')
edit('hdl/inbound.rs', '                let info = InternalFileInfo {', '                let info_name = dest.file_name().ok_or_else(|| anyhow!("Missing name"))?.to_string_lossy().into_owned();\n                let info = InternalFileInfo {')
edit('hdl/inbound.rs', 'File::create(&mfi.file_url)?', 'std::fs::OpenOptions::new().create_new(true).write(true).open(&mfi.file_url)?')
edit('hdl/inbound.rs', '                            self.state.transferred_files.remove(&payload_id);', '''                            if file_internal.bytes_transferred != file_internal.total_size { return Err(anyhow!("Incomplete payload")); }
                            if let Some(file) = &file_internal.file { file.sync_all()?; }
                            self.state.transferred_files.remove(&payload_id);''')
edit('hdl/inbound.rs', 'const SANE_FRAME_LENGTH:', '''fn validate_received_name(name: &str) -> Result<(), anyhow::Error> {
    if name.is_empty() || name.chars().count() > 180 || name.ends_with('.') || name.ends_with(' ') || name.chars().any(|c| c < ' ' || "<>:\\\"/\\\\|?*".contains(c)) { return Err(anyhow!("Invalid filename")); }
    let stem = name.split('.').next().unwrap_or("").to_uppercase();
    if ["CON", "PRN", "AUX", "NUL"].contains(&stem.as_str()) || (1..=9).any(|i| stem == format!("COM{i}") || stem == format!("LPT{i}")) { return Err(anyhow!("Reserved filename")); }
    Ok(())
}

const SANE_FRAME_LENGTH:''')
edit('hdl/inbound.rs', 'location_nearby_connections::os_info::OsType::Linux.into()', 'location_nearby_connections::os_info::OsType::Windows.into()')
# HMAC remains verified before decryption in the upstream receive path.
