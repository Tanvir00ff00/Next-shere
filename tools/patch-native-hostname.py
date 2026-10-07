from pathlib import Path
base=Path('third_party/open-quickshare/core_lib')
cargo=base/'Cargo.toml'
data=cargo.read_text().replace('sys_metrics = { git = "https://github.com/Martichou/sys_metrics" }\n','')
cargo.write_text(data)
for p in (base/'src').rglob('*.rs'):
    data=p.read_text()
    data=data.replace('sys_metrics::host::get_hostname()', 'crate::utils::host_name()')
    p.write_text(data)
p=base/'src/utils.rs'
p.write_text(p.read_text()+'\npub fn host_name() -> Result<String, anyhow::Error> { Ok(std::env::var("COMPUTERNAME").or_else(|_| std::env::var("HOSTNAME")).unwrap_or_else(|_| "Next Share".into())) }\n')
