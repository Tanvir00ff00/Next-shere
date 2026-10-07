from pathlib import Path
p = Path('third_party/open-quickshare/core_lib/src/hdl/inbound.rs')
s = p.read_text(encoding='utf-8')
s = s.replace('if dest.exists() {', 'if dest.exists() || files_name.iter().any(|n: &String| n.eq_ignore_ascii_case(&resolved_name)) {')
s = s.replace('if !dest.exists() {', 'if !dest.exists() && !files_name.iter().any(|n: &String| n.eq_ignore_ascii_case(&dest.file_name().unwrap().to_string_lossy())) {')
s = s.replace('total_bytes += info.total_size as u64;', 'total_bytes += info.total_size as u64;\n                if total_bytes > 16_u64 * 1024 * 1024 * 1024 { return Err(anyhow!("Batch too large")); }')
s = s.replace('if header.total_size() > SANE_FRAME_LENGTH.into() {', 'if header.total_size() < 0 || header.total_size() > SANE_FRAME_LENGTH.into() || self.state.payload_buffers.len() > 32 {')
s = s.replace('buffer.extend(body);', 'if buffer.len() + body.len() > SANE_FRAME_LENGTH as usize || buffer.len() + body.len() > header.total_size() as usize { return Err(anyhow!("Payload buffer too large")); }\n                            buffer.extend(body);')
s = s.replace('.as_ref()\n                                .unwrap()\n                                .write_all_at', '.as_ref()\n                                .ok_or_else(|| anyhow!("File payload before consent"))?\n                                .write_all_at')
s = s.replace('let peer_key = PublicKey::from_encoded_point(&encoded_point).unwrap();', 'let peer_key = Option::<PublicKey>::from(PublicKey::from_encoded_point(&encoded_point)).ok_or_else(|| anyhow!("Invalid peer curve point"))?;')
s = s.replace('response: Some(location_nearby_connections::connection_response_frame::ResponseStatus::Accept.into()),', 'response: Some(location_nearby_connections::connection_response_frame::ResponseStatus::Accept.into()),\n                    status: Some(0),\n                    multiplex_socket_bitmask: Some(0),')
p.write_text(s, encoding='utf-8')
# Port only the platform-independent advertisement codec from GPL-3.0 upstream.
s = Path('third_party/open-quickshare/core_lib/src/hdl/blea.rs').read_text(encoding='utf-8')
hashes = s[s.index('fn murmur3_x64_128_low'):s.index('#[derive(Debug, Clone)]\npub struct ReceiverAdvertiser')]
out = '''// Advertisement codec adapted from open-quickshare (GPL-3.0); see THIRD-PARTY-NOTICES.md.
use rand::RngCore;
use sha2::{Digest, Sha256};
use base64::{Engine, engine::general_purpose::URL_SAFE_NO_PAD};
const HEADER_VERSION_BYTE: u8 = 0x41;
const HEADER_EXTENDED_BIT: u8 = 0x10;
const BLOOM_FILTER_BYTES: usize = 10;
const ADVERTISEMENT_HASH_BYTES: usize = 4;
const DUMMY_SERVICE_ID_LEN: usize = 128;
const NEARBY_SERVICE_ID: &[u8] = b"NearbySharing";
pub fn encode(endpoint: [u8; 4], info: &str) -> (Vec<u8>, Vec<u8>) {
    let einfo = URL_SAFE_NO_PAD.decode(info).expect("Locally generated endpoint info");
    let mut inner = vec![0x23, 0xfc, 0x9f, 0x5e];
    inner.extend_from_slice(&endpoint);
    inner.push(einfo.len() as u8);
    inner.extend_from_slice(&einfo);
    inner.extend_from_slice(&[0; 8]);
    let mut full = vec![0x48, 0xfc, 0x9f, 0x5e];
    full.extend_from_slice(&(inner.len() as u32).to_be_bytes());
    full.extend_from_slice(&inner);
    full.extend_from_slice(&rand::random::<[u8; 2]>());
    full.push(0);
    let header = build_advertisement_header(&full, false, None);
    (full, header)
}
'''
Path('src/NextShare.QuickShare.Native/src/advertisement.rs').write_text(out+hashes, encoding='utf-8')
