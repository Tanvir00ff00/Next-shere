use anyhow::{Context, Result};
use rand::{Rng, distr::Alphanumeric};
use rqs_lib::{RQS, Visibility, TransferState};
use rqs_lib::channel::{ChannelMessage, Message, TransferAction, TransferKind};
use rqs_lib::hdl::{InboundRequest, info::{TransferPayload, TransferPayloadKind}};
use serde_json::{json, Value};
use std::{path::{Path, PathBuf}, sync::Arc, time::Duration};
use tokio::{io::{AsyncBufReadExt, BufReader}, net::{TcpListener, TcpStream}, sync::{broadcast, watch, Semaphore}};
use tokio_util::sync::CancellationToken;
use base64::{Engine, engine::general_purpose::STANDARD};
mod advertisement;
mod sending;
#[derive(Clone, serde::Deserialize)]
struct Hotspot { ssid: String, password: String, gateway: String, frequency: i32 }
#[derive(Clone)]
enum HotspotState { Waiting, Unavailable, Ready(Hotspot) }

fn emit(value: Value) { println!("{value}"); }
fn argument(name: &str) -> Option<String> { let a: Vec<_> = std::env::args().collect(); a.iter().position(|s| s == name).and_then(|i| a.get(i + 1).cloned()) }

#[tokio::main]
async fn main() -> Result<()> {
    tracing_subscriber::fmt().with_env_filter("warn").with_writer(std::io::stderr).init();
    if let Some(address) = argument("--test-send") { return test_send(&address).await; }
    if let Some(path) = argument("--send-request") { return sending::send(&path).await; }
    if std::env::args().any(|a|a=="--discover") { return sending::discover().await; }
    let name = argument("--name").unwrap_or_else(|| "Next Share".into());
    if name.as_bytes().len() > 100 { anyhow::bail!("Name too long"); }
    let root = PathBuf::from(argument("--staging").context("--staging is required")?);
    std::fs::create_dir_all(&root)?;
    let _settings = RQS::new(Visibility::Visible, None, Some(root.clone()), Some(name.clone()));
    let port: u16 = argument("--port").unwrap_or_else(|| "0".into()).parse()?;
    let loopback_only = std::env::args().any(|a|a=="--loopback-only");
    let listener = TcpListener::bind((if loopback_only {"127.0.0.1"}else{"0.0.0.0"}, port)).await?;
    let port = listener.local_addr()?.port();
    let bridge = TcpListener::bind(("127.0.0.1", 0)).await?;
    let bridge_port = bridge.local_addr()?.port();
    let (hotspot_tx, hotspot_rx) = watch::channel(HotspotState::Waiting);
    let endpoint: [u8; 4] = rand::rng().sample_iter(Alphanumeric).take(4).collect::<Vec<u8>>().try_into().unwrap();
    let endpoint_id = String::from_utf8(endpoint.to_vec())?;
    // Diagnostic mode isolates the BLE bootstrap without publishing a second,
    // direct LAN discovery route. The TCP listener remains for a later Wi-Fi upgrade.
    let ble_discovery_only = std::env::args().any(|a| a == "--ble-discovery-only");
    let daemon = if loopback_only || ble_discovery_only {None}else{Some(mdns_sd::ServiceDaemon::new()?)};
    let endpoint_info = rqs_lib::utils::gen_mdns_endpoint_info(3, &name);
    let info = mdns_sd::ServiceInfo::new("_FC9F5ED42C8A._tcp.local.", &rqs_lib::utils::gen_mdns_name(endpoint), &format!("nextshare-{endpoint_id}.local."), "", port,
        &[("n", endpoint_info.clone())][..])?.enable_addr_auto(mdns_sd::AddrType::V4);
    let full_name = info.get_fullname().to_owned();
    if let Some(daemon)=&daemon {daemon.register(info)?;}
    let stop = CancellationToken::new();
    let slots = Arc::new(Semaphore::new(4));
    let (messages, _) = broadcast::channel::<ChannelMessage>(4096);
    let mut events = messages.subscribe();
    let actions = messages.clone();
    tokio::spawn(async move {
        loop {
            match events.recv().await {
                Ok(event) => if let Message::Client(client) = event.msg {
                    if client.kind != TransferKind::Inbound { continue; }
                    let metadata = client.metadata.as_ref();
                    let source = metadata.and_then(|m| m.source.as_ref()).map(|s| s.name.as_str()).unwrap_or("Quick Share device");
                    let state = client.state.clone().unwrap_or_default();
                    let (file_name, file_count) = match metadata.and_then(|m| m.payload.as_ref()) {
                        Some(TransferPayload::Files(files)) => (files.first().map(|s| s.as_str()).unwrap_or("Files"), files.len()),
                        _ => ("Files", 0),
                    };
                    emit(json!({"type":"transfer","id":event.id,"state":format!("{state:?}"),"sender":source,"name":file_name,"fileCount":file_count,"received":metadata.map(|m| m.ack_bytes),"total":metadata.map(|m| m.total_bytes),"pin":metadata.and_then(|m| m.pin_code.as_ref())}));
                    if state == TransferState::WaitingForUserConsent {
                        let files_only = metadata.is_some_and(|m| matches!(m.payload_kind, TransferPayloadKind::Files));
                        let _ = actions.send(ChannelMessage { id: event.id.clone(), msg: Message::Lib { action: if files_only { TransferAction::ConsentAccept } else { TransferAction::ConsentDecline } } });
                    }
                    if state == TransferState::Finished {
                        if let Some(meta) = metadata {
                            if let (Some(TransferPayload::Files(files)), Some(destination)) = (&meta.payload, &meta.destination) {
                                let paths: Vec<PathBuf> = files.iter().map(|n| Path::new(destination).join(n)).collect();
                                let completed = json!({"type":"complete","id":event.id,"sender":source,"files":paths,"total":meta.total_bytes});
                                let marker = Path::new(destination).parent().unwrap().join(format!("{}.complete.json", event.id));
                                if let Err(e) = write_marker(&marker, &completed) { emit(json!({"type":"error","id":event.id,"message":e.to_string()})); }
                                else { emit(completed); }
                            }
                        }
                    }
                },
                Err(broadcast::error::RecvError::Lagged(n)) => emit(json!({"type":"error","message":format!("Event channel lagged by {n}")})),
                Err(_) => break,
            }
        }
    });
    let input_stop = stop.clone();
    let input_actions = messages.clone();
    tokio::spawn(async move {
        let mut lines = BufReader::new(tokio::io::stdin()).lines();
        while let Ok(Some(line)) = lines.next_line().await {
            if let Ok(command) = serde_json::from_str::<Value>(&line) {
                if command["type"] == "stop" { break; }
                if command["type"] == "hotspot" {
                    let value = match serde_json::from_value::<Hotspot>(command) { Ok(h) => HotspotState::Ready(h), Err(_) => HotspotState::Unavailable };
                    let _ = hotspot_tx.send(value);
                    continue;
                }
                if command["type"] == "hotspot-unavailable" { let _ = hotspot_tx.send(HotspotState::Unavailable); continue; }
                if command["type"] == "cancel" {
                    if let Some(id) = command["id"].as_str() { let _ = input_actions.send(ChannelMessage { id: id.into(), msg: Message::Lib { action: TransferAction::TransferCancel } }); }
                }
            }
        }
        input_stop.cancel();
    });
    let (advert, header) = advertisement::encode(endpoint, &endpoint_info);
    emit(json!({"type":"listening","name":name,"port":port,"bridgePort":bridge_port,"endpoint":endpoint_id,"endpointInfo":endpoint_info,"advertisement":STANDARD.encode(advert),"advertisementHeader":STANDARD.encode(header),"googleRequired":false,"transport":"WiFi LAN + Windows GATT bridge + WiFi Direct upgrade","lanDiscovery":!loopback_only && !ble_discovery_only,"sameNetworkRequired":false,"phoneInteroperabilityVerified":false}));
    loop {
        tokio::select! {
            _ = stop.cancelled() => break,
            accepted = listener.accept() => {
                let (socket, _) = accepted?;
                let Ok(permit) = slots.clone().try_acquire_owned() else { drop(socket); emit(json!({"type":"busy"})); continue; };
                let id = uuid::Uuid::new_v4().simple().to_string();
                emit(json!({"type":"connection","id":id,"transport":"Wi-Fi LAN"}));
                let sender = messages.clone();
                let cancellation = stop.clone();
                let hotspot = hotspot_rx.clone();
                tokio::spawn(async move { let _permit = permit; receive(socket, id, sender, cancellation, hotspot, false).await; });
            }
            accepted = bridge.accept() => {
                let (socket, _) = accepted?;
                let Ok(permit) = slots.clone().try_acquire_owned() else { drop(socket); continue; };
                let id = uuid::Uuid::new_v4().simple().to_string();
                emit(json!({"type":"connection","id":id,"transport":"BLE bridge"}));
                let sender = messages.clone(); let cancellation = stop.clone(); let hotspot = hotspot_rx.clone();
                tokio::spawn(async move { let _permit = permit; receive(socket, id, sender, cancellation, hotspot, true).await; });
            }
        }
    }
    if let Some(daemon)=daemon {let _ = daemon.unregister(&full_name);let _ = daemon.shutdown();}
    emit(json!({"type":"stopped"}));
    Ok(())
}

fn write_marker(path: &Path, value: &Value) -> Result<()> {
    use std::io::Write;
    let temp = path.with_extension("tmp");
    let mut file = std::fs::OpenOptions::new().create_new(true).write(true).open(&temp)?;
    file.write_all(value.to_string().as_bytes())?;
    file.sync_all()?;
    drop(file);
    std::fs::rename(temp, path)?;
    Ok(())
}

async fn test_send(address: &str) -> Result<()> {
    use rqs_lib::{hdl::OutboundRequest, OutboundPayload};
    use rqs_lib::utils::{DeviceType, RemoteDeviceInfo};
    let files: Vec<String> = std::env::args().skip_while(|s| s != "--files").skip(1).collect();
    if files.is_empty() { anyhow::bail!("--files required"); }
    let _settings = RQS::new(Visibility::Visible, None, None, Some("Protocol test sender".into()));
    let (messages, _) = broadcast::channel(4096);
    let socket = TcpStream::connect(address).await?;
    let mut sender = OutboundRequest::new(*b"TEST", socket, "test-client".into(), messages, OutboundPayload::Files(files), RemoteDeviceInfo { device_type: DeviceType::Laptop, name: "Next Share".into() });
    sender.send_connection_request().await?;
    sender.send_ukey2_client_init().await?;
    loop {
        match tokio::time::timeout(Duration::from_secs(45), sender.handle()).await? {
            Ok(()) => {},
            Err(e) => { if sender.state.state != TransferState::Finished { return Err(e); } break; }
        }
        if sender.state.state == TransferState::Finished { break; }
    }
    emit(json!({"type":"test-sent","state":format!("{:?}", sender.state.state)}));
    Ok(())
}

async fn receive(socket: TcpStream, id: String, sender: broadcast::Sender<ChannelMessage>, stop: CancellationToken, mut hotspot: watch::Receiver<HotspotState>, offline: bool) {
    let mut receiver = InboundRequest::new(socket, id.clone(), sender);
    let mut upgrade_attempted = false;
    loop {
        tokio::select! {
            _ = stop.cancelled() => break,
            result = tokio::time::timeout(Duration::from_secs(90), receiver.handle()) => match result {
                Ok(Ok(())) => {},
                Ok(Err(e)) => {
                    if receiver.state.state != TransferState::Finished { emit(json!({"type":"error","id":id,"message":e.to_string()})); }
                    break;
                },
                Err(_) => { emit(json!({"type":"error","id":id,"message":"Connection idle timeout"})); break; }
            }
        }
        if offline && !upgrade_attempted && receiver.state.state == TransferState::ReceivingFiles {
            upgrade_attempted = true;
            emit(json!({"type":"need-hotspot","id":id}));
            let configuration = tokio::select! {
                _ = stop.cancelled() => break,
                result = tokio::time::timeout(Duration::from_secs(20), hotspot.wait_for(|s| !matches!(s, HotspotState::Waiting))) => match result { Ok(Ok(value)) => Some(value.clone()), _ => None }
            };
            if let Some(HotspotState::Ready(h)) = configuration {
                emit(json!({"type":"upgrade","id":id,"state":"OfferingLocalWifi"}));
                let result = tokio::select! { _ = stop.cancelled() => break, r = receiver.upgrade_windows_hotspot(&h.ssid, &h.password, &h.gateway, h.frequency) => r };
                if let Err(e) = result { if receiver.state.state != TransferState::Finished { emit(json!({"type":"error","id":id,"message":e.to_string()})); } break; }
            }
        }
    }
}
