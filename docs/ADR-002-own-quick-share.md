# ADR-002: Own Quick Share receiver and Material 3 UI

Date: 2026-10-05. Supersedes ADR-001's Google companion and compact-window implementation decisions. AirDrop/phone qualification boundaries remain.

The user explicitly authorized a receiver independent of Google's Windows app, then requested LocalSend-style Receive/History/Settings with Material theming.

Next Share owns a Rust protocol worker, Windows BLE GATT adapter and Wi-Fi Direct group owner. No Google executable is launched or controlled. The Rust worker uses the pinned open-quickshare GPL protocol core with a Windows portability/safety patch. WPF owns service lifecycle, receiving consent (ON authorizes shop files), durable import and the UI.

Paths: BLE legacy discovery/service-slot → per-device weave framing → loopback TCP bridge → UKEY2/AES/HMAC Nearby session → optional encrypted Wi-Fi Direct bandwidth-upgrade offer. LAN peers can reach the same protocol through mDNS/TCP. The local GO does not enable Internet Sharing; existing sharing causes rejection. Each bridge/client has independent framing/counters; four clients are admitted.

0.3.3 GATT reply policy: one shared notification pump serializes Windows characteristic operations across all peers, but every send targets only its intended subscribed client. It re-reads current subscriptions for each call, waits for an active CCCD/session, and negotiates against MaxNotificationSize rather than the ATT PDU alone. Initial write response/deferral is completed before notifications begin. Only the observed E_ILLEGAL_METHOD_CALL is retried (8 attempts, 100ms intervals, refreshed client); timeouts and failed delivery results terminate the peer because retrying them could duplicate an accepted packet. No broadcast fallback is used. Late single-byte weave ERROR commands from a phone's failed previous handshake are closed as peer errors, not parsed as new connection requests.

The protocol worker uses per-session staging and reports completion only after all offered file sizes and terminal payloads match and file handles are flushed. It writes a durable completion marker before announcing it. The coordinator validates the marker, session/path boundary and batch size, commits files to the selected flat destination, marks the batch imported and removes validated staging files. Incomplete transfers do not appear as printable files. File-name collisions get suffixes and cannot overwrite another customer's files.

The worker and platform adapter are separate processes/components for fault isolation. Master OFF cancels owned connections and stops only owned services. UI state distinguishes worker listening from BLE advertising and exposes failures. Google can conflict with the radio; users may exit its tray receiver to test this application. An app process running is not evidence of actual phone discovery.

MaterialDesignInXamlToolkit 5.3.2 supplies Material 3 controls. Shared tokens and persisted theme settings serve Receive, History and Settings. History shows real committed records and does not delete payloads when cleared.

Evidence: encrypted loopback transfers, four simultaneous protocol senders, BLE weave validation, storage fault cases, actual UI renders, BLE service publish and raw Wi-Fi Direct GO startup probes. Actual stock Android discovery/auto-upgrade/file receive, radio concurrency, sleep/wake and throughput remain unqualified. No AirDrop support is implied by the shared power control.
