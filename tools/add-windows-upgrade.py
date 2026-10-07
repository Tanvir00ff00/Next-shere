from pathlib import Path
p=Path('third_party/open-quickshare/core_lib/src/hdl/inbound.rs')
s=p.read_text(encoding='utf-8')
# Make the portable length-prefixed channel helpers available on Windows.
s=s.replace('#[cfg(all(feature = "experimental", target_os = "linux"))]\npub(crate) async fn read_frame_from', 'pub(crate) async fn read_frame_from')
s=s.replace('#[cfg(all(feature = "experimental", target_os = "linux"))]\nasync fn send_frame_on', 'async fn send_frame_on')
s=s.replace('    socket: S,\n', '    socket: S,\n    peer_endpoint: String,\n',1)
s=s.replace('            socket,\n', '            socket,\n            peer_endpoint: String::new(),\n',1)
anchor='        // The sender\'s advertised LAN address picks the upgrade path later:'
s=s.replace(anchor,'        self.peer_endpoint = connection_request.endpoint_id().to_string();\n\n'+anchor,1)
linux=s[s.index('    async fn drain_prior_channel'):s.index('    /// Run the Wi-Fi bandwidth upgrade')]
# Never swap after incomplete drain: losing a sequence number corrupts this encrypted session.
linux=linux.replace('                    break;','                    return Err(anyhow!("Prior channel ended before upgrade confirmation"));',1)
linux=linux.replace('                    break;','                    return Err(anyhow!("Prior channel upgrade drain timed out"));',1)
linux=linux.replace('                        debug!("BWU drain: error processing frame: {e}");','                        return Err(e);')
hotspot=s[s.index('    async fn do_bwu_hotspot'):]
hotspot=hotspot[:hotspot.rindex('\n}')]
hotspot=hotspot.replace('    async fn do_bwu_hotspot(&mut self)', '    pub async fn upgrade_windows_hotspot(&mut self, ssid: &str, password: &str, gateway: &str, frequency: i32)')
start=hotspot.index('        let guard = match')
end=hotspot.index('        let port = listener.local_addr()',start)
hotspot=hotspot[:start]+'''        let listener = tokio::net::TcpListener::bind((gateway, 0)).await?;
'''+hotspot[end:]
hotspot=hotspot.replace('guard.ssid, guard.gateway','ssid, gateway').replace('guard.ssid.clone()','ssid.to_string()').replace('guard.password.clone()','password.to_string()').replace('guard.gateway.to_string()','gateway.to_string()').replace('guard.frequency','frequency')
hotspot=hotspot.replace('self.bwu_try_hotspot = crate::utils::local_ipv4().is_none();\n                        self.schedule_bwu_retry();','')
hotspot=hotspot.replace('self.bwu_try_hotspot = crate::utils::local_ipv4().is_none();\n                    self.schedule_bwu_retry();','')
start=hotspot.index('        // Plaintext CLIENT_INTRODUCTION')
end=hotspot.index('        send_frame_on',start)
hotspot=hotspot[:start]+'''        // The upgrade connection belongs to this transfer, then resumes with its existing keys.
        let intro = tokio::time::timeout(Duration::from_secs(5), read_frame_from(&mut tcp)).await??;
        let mut framed = (intro.len() as u32).to_be_bytes().to_vec();
        framed.extend_from_slice(&intro);
        if peek_client_introduction(&framed).as_deref() != Some(self.peer_endpoint.as_str()) {
            return Err(anyhow!("Upgrade endpoint does not match the BLE session"));
        }
'''+hotspot[end:]
hotspot=hotspot.replace('        self.hotspot_guard = Some(guard);\n','').replace('self.socket = crate::hdl::MigratableStream::Tcp(tcp);','self.socket = tcp;')
s+='\n// Windows BLE bridge to local Wi-Fi Direct GO, adapted from open-quickshare.\nimpl InboundRequest<TcpStream> {\n'+linux+hotspot+'\n}\n'
p.write_text(s,encoding='utf-8')
