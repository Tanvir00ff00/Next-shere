use anyhow::{Context, Result};
use std::future::Future;
use rand::{Rng, distr::Alphanumeric};
use crate::{argument, emit};
use serde::Deserialize;
use serde_json::{json, Value};
use std::{pin::Pin, task::{Context as TaskContext, Poll}, time::Duration};
use tokio::{io::{AsyncRead, AsyncWrite, ReadBuf}, net::TcpStream, sync::{broadcast, mpsc, Mutex}, time::{Sleep, Instant}};
use rqs_lib::{RQS, Visibility, TransferState, OutboundPayload, hdl::{OutboundRequest, WifiUpgradable}, utils::{DeviceType, RemoteDeviceInfo}};

#[derive(Deserialize)]
#[serde(rename_all="camelCase")]
struct Request { address: String, target_name: String, source_name: String, files: Vec<String>, ble: bool }
#[derive(Debug)]
struct Transport { socket: TcpStream, ble: bool, deadline: Pin<Box<Sleep>>, replies: Mutex<mpsc::Receiver<Value>> }
impl AsyncRead for Transport {
    fn poll_read(mut self: Pin<&mut Self>, cx: &mut TaskContext<'_>, buf: &mut ReadBuf<'_>) -> Poll<std::io::Result<()>> {
        let result = Pin::new(&mut self.socket).poll_read(cx, buf);
        if result.is_ready() { self.deadline.as_mut().reset(Instant::now()+Duration::from_secs(90)); return result; }
        if self.deadline.as_mut().poll(cx).is_ready() { return Poll::Ready(Err(std::io::Error::new(std::io::ErrorKind::TimedOut,"Peer stopped responding"))); }
        Poll::Pending
    }
}
impl AsyncWrite for Transport {
    fn poll_write(mut self: Pin<&mut Self>, cx: &mut TaskContext<'_>, bytes: &[u8]) -> Poll<std::io::Result<usize>> { Pin::new(&mut self.socket).poll_write(cx,bytes) }
    fn poll_flush(mut self: Pin<&mut Self>, cx: &mut TaskContext<'_>) -> Poll<std::io::Result<()>> { Pin::new(&mut self.socket).poll_flush(cx) }
    fn poll_shutdown(mut self: Pin<&mut Self>, cx: &mut TaskContext<'_>) -> Poll<std::io::Result<()>> { Pin::new(&mut self.socket).poll_shutdown(cx) }
}
impl WifiUpgradable for Transport {
    fn is_low_bandwidth(&self) -> bool { self.ble }
    fn upgrade_to_tcp(&mut self, tcp: TcpStream) -> bool { self.socket=tcp;self.ble=false; true }
    fn host_wifi<'a>(&'a mut self) -> Pin<Box<dyn Future<Output=Result<Option<rqs_lib::hdl::HostedWifi>>>+Send+'a>> {
        Box::pin(async move {
            emit(json!({"type":"wifi-host"}));
            let reply=tokio::time::timeout(Duration::from_secs(25),self.replies.lock().await.recv()).await?.context("Wi-Fi controller closed")?;
            if reply["success"]!=true {anyhow::bail!("{}",reply["message"].as_str().unwrap_or("Local Wi-Fi hosting failed"));}
            let gateway:std::net::Ipv4Addr=reply["gateway"].as_str().context("Missing gateway")?.parse()?;
            let ssid=reply["ssid"].as_str().context("Missing SSID")?.to_owned();
            let password=reply["password"].as_str().context("Missing password")?.to_owned();
            if !gateway.is_private() || ssid.is_empty() || ssid.len()>32 || password.len()<8 || password.len()>63 {anyhow::bail!("Invalid local Wi-Fi hosting response");}
            Ok(Some(rqs_lib::hdl::HostedWifi{gateway,ssid,password,frequency:reply["frequency"].as_i64().unwrap_or(0) as i32}))
        })
    }
    fn join_wifi<'a>(&'a mut self, ssid: String, password: String, gateway: String, port: u16)
        -> Pin<Box<dyn std::future::Future<Output=Result<Option<TcpStream>>> + Send + 'a>> {
        Box::pin(async move {
            let ip: std::net::Ipv4Addr=gateway.parse()?;
            if !ip.is_private() || port==0 || ssid.len()>32 || password.len()>63 { anyhow::bail!("Invalid local Wi-Fi upgrade"); }
            emit(json!({"type":"wifi-join","ssid":ssid,"password":password,"gateway":gateway,"port":port}));
            let reply=tokio::time::timeout(Duration::from_secs(35),self.replies.lock().await.recv()).await?.context("Wi-Fi controller closed")?;
            if reply["success"]!=true { anyhow::bail!("{}",reply["message"].as_str().unwrap_or("Wi-Fi connection failed")); }
            let until=Instant::now()+Duration::from_secs(15);
            loop {
                match tokio::time::timeout(Duration::from_secs(3),TcpStream::connect((ip,port))).await {
                    Ok(Ok(tcp))=>return Ok(Some(tcp)),
                    _ if Instant::now()<until=>tokio::time::sleep(Duration::from_millis(400)).await,
                    _=>anyhow::bail!("Phone Wi-Fi endpoint is unreachable")
                }
            }
        })
    }
}

pub async fn send(path: &str) -> Result<()> {
    let bytes=std::fs::read(path)?; if bytes.len()>1024*1024 { anyhow::bail!("Send request too large"); }
    let request:Request=serde_json::from_slice(&bytes)?;
    if request.files.is_empty() || request.files.len()>1000 { anyhow::bail!("Select 1–1000 files"); }
    let address:std::net::SocketAddr=request.address.parse()?;
    if request.ble && !address.ip().is_loopback() { anyhow::bail!("BLE bridge must be local"); }
    for file in &request.files { if !std::fs::metadata(file)?.is_file() { anyhow::bail!("Selected source is not a file"); } }
    let _settings=RQS::new(Visibility::Visible,None,None,Some(request.source_name));
    let (messages,mut events)=broadcast::channel::<rqs_lib::channel::ChannelMessage>(4096);
    tokio::spawn(async move {
        while let Ok(event)=events.recv().await {
            if let rqs_lib::channel::Message::Client(client)=event.msg {
                let meta=client.metadata.as_ref();
                emit(json!({"type":"send-progress","state":format!("{:?}",client.state.unwrap_or_default()),"sent":meta.map(|m|m.ack_bytes),"total":meta.map(|m|m.total_bytes),"pin":meta.and_then(|m|m.pin_code.as_ref())}));
            }
        }
    });
    let (replies_tx,replies)=mpsc::channel(1);
    // Tokio stdin uses an uncancellable blocking-pool read and can hold process exit
    // after Finished. A detached command thread lets the sender exit cleanly.
    std::thread::spawn(move || {
        use std::io::BufRead;
        for line in std::io::stdin().lock().lines() {
            let Ok(line)=line else {break};
            if line.len()>8192 {break;}
            if let Ok(value)=serde_json::from_str::<Value>(&line) { if (value["type"]=="wifi-ready"||value["type"]=="wifi-host-ready") && replies_tx.blocking_send(value).is_err() {break;} }
        }
    });
    let socket=tokio::time::timeout(Duration::from_secs(10),TcpStream::connect(address)).await??;
    let transport=Transport {socket,ble:request.ble,deadline:Box::pin(tokio::time::sleep(Duration::from_secs(90))),replies:Mutex::new(replies)};
    let endpoint:[u8;4]=rand::rng().sample_iter(Alphanumeric).take(4).collect::<Vec<u8>>().try_into().unwrap();
    let mut sender=OutboundRequest::new(endpoint,transport,"outgoing".into(),messages,OutboundPayload::Files(request.files),RemoteDeviceInfo { name:request.target_name,device_type:DeviceType::Phone });
    if request.ble { use rqs_lib::location_nearby_connections::bandwidth_upgrade_negotiation_frame::upgrade_path_info::Medium; sender.set_mediums(vec![Medium::Ble as i32,Medium::WifiLan as i32,Medium::WifiDirect as i32]); }
    sender.send_connection_request().await?;sender.send_ukey2_client_init().await?;
    loop {
        if let Err(error)=sender.handle().await { if sender.state.state!=TransferState::Finished { return Err(error); } }
        match sender.state.state {TransferState::Finished=>break,TransferState::Rejected=>anyhow::bail!("The device declined this transfer"),TransferState::Cancelled|TransferState::Disconnected=>anyhow::bail!("Transfer cancelled or disconnected"),_=>{}}
    }
    emit(json!({"type":"send-done","state":"Finished"})); Ok(())
}

pub async fn discover() -> Result<()> {
    let daemon=mdns_sd::ServiceDaemon::new()?;
    let events=daemon.browse("_FC9F5ED42C8A._tcp.local.")?;
    let seconds:u64=argument("--seconds").unwrap_or_else(||"30".into()).parse()?;
    let until=tokio::time::sleep(Duration::from_secs(seconds.min(3600)));tokio::pin!(until);
    loop {
        tokio::select! {
            _=&mut until=>break,
            event=events.recv_async()=>match event? {
                mdns_sd::ServiceEvent::ServiceResolved(info)=>{
                    let Some(property)=info.get_property("n") else {continue};
                    let Ok((_,name))=rqs_lib::utils::parse_mdns_endpoint_info(property.val_str()) else {continue};
                    if name.len()>256 || info.get_port()==0 {continue;}
                    for ip in info.get_addresses_v4() {
                        if !rqs_lib::utils::is_not_self_ip(ip) || !rqs_lib::utils::same_subnet(ip.octets()) {continue;}
                        emit(json!({"type":"device","id":info.get_fullname(),"name":name,"address":format!("{ip}:{}",info.get_port())}));
                    }
                },
                mdns_sd::ServiceEvent::ServiceRemoved(_,id)=>emit(json!({"type":"device-removed","id":id})),_=>{}
            }
        }
    }
    let _=daemon.shutdown();Ok(())
}

#[cfg(test)]
mod tests {
    #[test]
    fn p256_coordinate_padding_preserves_integer_and_width() {
        let x=vec![0x81;31];let y=vec![0x12;32];
        let short=rqs_lib::utils::p256_sec1(&x,&y).unwrap();
        let padded=rqs_lib::utils::p256_sec1(&[&[0u8][..],x.as_slice()].concat(),&y).unwrap();
        assert_eq!(short,padded);assert_eq!(short.len(),65);assert_eq!(short[1],0);assert_eq!(short[2],0x81);
        assert_eq!(rqs_lib::utils::p256_sec1(&[&[0u8][..],y.as_slice()].concat(),&y).unwrap(),rqs_lib::utils::p256_sec1(&y,&y).unwrap());
    }
    #[test]
    fn p256_coordinates_reject_empty_and_invalid_sign_extension() {
        for invalid in [vec![],vec![1;33],vec![0;34]] {assert!(rqs_lib::utils::p256_sec1(&invalid,&[1;32]).is_err());}
    }
}
