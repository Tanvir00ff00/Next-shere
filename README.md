# Next Share 0.4.7

দোকানের Windows PC-এর local file sharing app। LocalSend-এর মতো sidebar ও শান্ত teal theme; Material 3 controls ব্যবহার করা হয়েছে। Receive, Send, History ও Settings screen রয়েছে। নির্বাচিত Device Link logo EXE, window/taskbar, tray, sidebar ও receive notification-এ ব্যবহার হয়েছে। App খুললেই receiving ON; একটি power button Bluetooth ও নিজস্ব Quick Share receiver চালায়/বন্ধ করে।

## ইনস্টল ও চালু করুন

[Windows installer ও source download](https://github.com/Tanvir00ff00/Next-shere/releases/latest)

`NextShare-Setup-0.4.7.exe` চালিয়ে Windows UAC-এ অনুমতি দিন। নিজস্ব teal/Device Link design-এর animated installer welcome, live installation stages ও ready/error screen দেখায়। Rotating orbit, soft pulse ও moving progress strip চলতে থাকে; Windows reduced-animation preference মানা হয়। Installer `C:\Program Files\NextShare`-এ app, native receiver, read-only Offline Guard service, local-subnet firewall rules, Start menu/desktop shortcut ও uninstall entry তৈরি করে। Install/upgrade/uninstall system setup-এর জন্য Administrator permission লাগে; প্রতিদিনের app ও login startup সাধারণ user হিসেবেই চলে। Startup registry-তে `NextShare.exe --startup` থাকে: Windows login-এর পরে tray-তে receiving ON অবস্থায় শুরু হয়। Startup-এ একই app আগেই চললে দ্বিতীয় instance নীরবে বের হয়। App Windows computer name, save folder ও অন্য preferences আগের মতো রাখে।

Upgrade/uninstall-এর আগে installed Next Share tray থেকে Exit করুন; installer active app জোর করে বন্ধ করে না। Offline Guard boot-এ শুরু হয় এবং শুধু Internet Sharing-এর live read-only check করে। App সেটির process ID Windows Service Control Manager-এর সঙ্গে মিলিয়ে তারপর response গ্রহণ করে। Service Internet Sharing, router বা adapters পরিবর্তন করে না। App login-এর সময় radio readiness ব্যর্থ হলে তিনবার retry করে। সাধারণ Shortcut চালালে window খুলবে; receiving সবসময় প্রথমে ON থাকে।

`artifacts`-এর self-contained payload একই folder-এ তিনটি executable রেখে চালানো যায়; Windows service-টি installer দিয়ে setup করতে হবে। .NET আলাদা install করার প্রয়োজন নেই। User files, history ও preferences uninstall-এ রাখা হয়।

Settings-এর Next Share card-এ চলমান app-এর assembly থেকে Version দেখায়; নতুন build-এ এটি স্বয়ংক্রিয়ভাবে বদলায়। Settings-এ Device name (default: Windows computer name), save folder, System/Light/Dark theme, Teal/Blue/Violet color ও close-to-tray option রয়েছে। নতুন ফাইল সরাসরি নির্বাচিত folder-এ আসে; একই নাম থাকলে numbered suffix হয়। পূর্বের file overwrite হয় না। History-তে search, Bluetooth/Quick Share filter, show in folder, path copy ও history clear/Undo আছে। History clear করলে saved file মুছে যায় না। Preferences ও metadata AppData\Local\NextShare-এ থাকে। Tray right-click menu-তে Device Link logo, receiving status, rounded card, accent hover ও styled Exit আছে; app theme/accent অনুসরণ করে। Windows High Contrast-এ system menu rendering ব্যবহার হয়।

Compact window শুরুতে 830×560; minimum 640×440। Window size close/exit-এ মনে থাকে। Receive animation জায়গা অনুযায়ী পুরোটা scale হয়; ছোট width-এ sidebar icon rail হয়। Receiving ON থাকলে দুটি orbit ও soft glow চলতে থাকে; Windows reduced-animation preference মানা হয়। ফাইল আসা শুরু হলেই taskbar-এর ওপর নিজের ছোট desktop card দুই সেকেন্ড দেখায়, typing focus নেয় না এবং tray-তে থাকলেও কাজ করে। এটি app-এর transient card, Windows Notification Center-এ স্থায়ী entry নয়। ফাইল durable save হয়ে History/Receive-এ চলে আসার পরে দ্বিতীয় Received card দেখায়, নিজস্ব দুই সেকেন্ডের lifetime থাকে। প্রথম card আগে বন্ধ হয়ে গেলেও completion notification আসে। দ্রুত transfer-এ দুটি card একটির ওপর আরেকটি থাকে; একসঙ্গে একাধিক device-এর খবর count-এ একত্র হয়। Failed/cancelled transfer বা অসম্পূর্ণ Quick Share batch-এ success card আসে না। নিজের আরও স্পষ্ট 0.78-second glass chime embedded আছে; stereo left/right chime Windows Media Engine দিয়ে Settings-এর নির্বাচিত output-এ বাজে (default: Windows default output), chime-এর আগে/পরে 250ms silence থাকে, Windows system sound ব্যবহার করা হয় না; arrival burst sound 800ms-এ সীমিত; completion chime arrival chime শেষ হওয়া পর্যন্ত অপেক্ষা করে, একই burst একবার বাজে। Settings-এর Notifications section থেকে card ও sound আলাদাভাবে বন্ধ বা preview করা যায়। Settings-এ Sound output নির্বাচন ও Refresh outputs করা যায়। Preview-এর নিচে output device, mute বা playback error দেখা যায়; app system/mixer volume বদলায় না।

Receive screen-এ প্রতিটি active transfer-এর device, file, bytes, percentage ও Saving state আছে; unknown size-এ indeterminate progress, multiple devices-এ independent tracking। Active transfer থাকলে receiving animation দ্রুত চলে। Settings-এর Device name Quick Share discovery ও outgoing identity-তে ব্যবহার হয়; classic Bluetooth discovery name Windows-এর নাম অনুসরণ করে। নাম বদলাতে active transfers শেষ হতে হবে। Receiver information ইংরেজিতে লেখা। Playback status audio API-তে পাঠানোর ফল জানায়; physical audibility নিজে নিশ্চিত করে না।

## Receiver-এর বর্তমান অবস্থা

- **Bluetooth:** Windows RFCOMM/OBEX Object Push receiver। ব্যবহারকারীর Redmi থেকে JPEG receive আগে সফল হয়েছে। নিজের file execution নেই। চারটি connection-এর admission limit রয়েছে; real radio concurrency qualification বাকি।
- **Quick Share:** Google application ছাড়াই চলা নিজস্ব receiver, open-quickshare protocol core-এর Windows port ব্যবহার করে। Google install/run প্রয়োজন নেই। BLE GATT discovery + encrypted Nearby connection + local Wi-Fi Direct bandwidth upgrade যুক্ত হয়েছে। একই LAN-এ mDNS/TCP route-ও আছে। ব্যবহারকারী Google Android Quick Share থেকে সফল receive এবং Samsung থেকে Connecting/failure জানিয়েছেন। 0.3.3-তে Windows BLE reply-এর `0x8000000E`-এর জন্য current subscription refresh, bounded state retry, shared-characteristic serialization ও actual notification-size negotiation যুক্ত হয়েছে। Samsung-এর নতুন physical transfer এখনও নিশ্চিত নয়; model/One UI জানা নেই। সব ফোন, offline routing, speed বা radio concurrency qualification হয়নি।
- **AirDrop এবং QR:** transport এখনও যুক্ত হয়নি। iPhone-এর AirDrop বা ordinary Bluetooth receive এই build-এ দাবি করা হচ্ছে না।

0.4.6-এ Windows notification dispatch, delivery এবং result metadata আলাদাভাবে যাচাই হয়। শুধু operation তৈরি হওয়ার আগের rejected state retry হয়; asynchronous failure বা result getter error-এর পরে একই packet আবার পাঠানো হয় না। Windows Success জানানোর পরে BytesSent metadata unavailable হলে duplicate handshake তৈরি না করে এগোয়; readable byte count-এ truncation এখনও প্রত্যাখ্যান হয়। Installed 0.4.5-এর Google transfer log-এ প্রথম BLE reply-তেই failure দেখা গেছে, Wi-Fi transfer শুরু হওয়ার আগে। Metadata failure একটি সম্ভাব্য regression; নতুন build-এ আসল ফোনের পরীক্ষা ছাড়া root cause বা Google/Samsung compatibility নিশ্চিত বলা হচ্ছে না।

Google Quick Share একই radio দখল করলে নিজের BLE advertiser conflict করতে পারে। নিজস্ব receiver পরীক্ষা করতে Google-এর tray menu থেকে সম্পূর্ণ Exit করুন; শুধু window close যথেষ্ট নাও হতে পারে। Next Share নিজে Google process বন্ধ বা restart করে না।

Wi-Fi Direct hotspot transfer প্রয়োজন হলে শুরু হয়; app Windows Mobile Hotspot/NAT/Internet Sharing enable করে না। ইতিমধ্যে Internet Sharing সক্রিয় থাকলে offline hotspot প্রত্যাখ্যান করে। Installed Offline Guard service privileged sharing check করে; app-কে Run as administrator করতে হয় না। Service না থাকলে অথবা Internet Sharing state যাচাই ব্যর্থ হলে offline hotspot শুরু হয় না এবং Receiver details-এ installer repair করার কারণ দেখায়। Installer native backend executable-এর TCP ও UDP mDNS inbound rules LocalSubnet-এ সীমিত করে। ফোনের বাস্তব connection আচরণ ও routing qualification বাকি।

## Send

Send-এ File (multiple files), Folder, Text ও Paste আছে। Explorer থেকে file/folder app-এর যেকোনো জায়গায় drop করলে Send selection-এ যোগ হয়; সঙ্গে সঙ্গে পাঠানো হয় না। Windows Administrator mode-এ Explorer থেকে drag/drop block হতে পারে; তখন File/Folder picker ব্যবহার করুন অথবা app সাধারণ user হিসেবে চালান। Folder-এর nested contents একটি ZIP snapshot হিসেবে যায়। Text UTF-8 .txt এবং clipboard image original dimensions-এ PNG হয়। Clear/Remove শুধু selection ও app-এর তৈরি temporary file সরায়; source file সরায় না।

ফাইল বাছুন → ফোনে Bluetooth চালু বা Quick Share → Receive খুলুন → Refresh → সঠিক device card বাছুন → ফোনে transfer accept করুন। Bluetooth-এর প্রয়োজন হলে Windows pairing dialog শেষ করতে হবে। Classic Bluetooth list-এ Windows-এর পরিচিত/paired devices-ও থাকতে পারে; কেবল OBEX Object Push offer করা device-এ file পাঠানো যায়। iPhone Bluetooth file receiving offer করে না। Quick Share Wi-Fi ও Bluetooth route আলাদা করে দেখায়; একই ফোন একাধিক route-এ দেখা যেতে পারে। LocalSend-এর UI style ব্যবহার হয়েছে, LocalSend protocol compatibility যোগ হয়নি।

Google desktop app ছাড়াই নিজস্ব encrypted Quick Share sender, mDNS discovery ও Windows BLE GATT client চলে। LAN route-এ উভয় device একই local network-এ লাগবে; transfer-এ Internet লাগে না। Compatible GATT phones-এর ক্ষেত্রে BLE connection থেকে phone-hosted local Wi-Fi-এ joining অথবা dynamic role switch-এ PC-hosted Wi-Fi Direct upgrade যুক্ত হয়েছে। Temporary Wi-Fi joining manual; transfer শেষে আগের saved Wi-Fi reconnect চেষ্টা হয় এবং ব্যর্থ হলে status দেখায়। নতুন incoming transfer চললে Wi-Fi upgrade শুরু করে না। PC-hosted offline network Internet Sharing/NAT enable করে না এবং existing sharing থাকলে প্রত্যাখ্যান করে। Windows Wi-Fi/location permissions, radio capability, installed Offline Guard service এবং local receiver rules লাগে।

সব Android model-এর outbound compatibility নিশ্চিত নয়। L2CAP-only Quick Share Bluetooth transport-এর Windows sender যোগ হয়নি; সেসব ফোনে shared local Wi-Fi route ব্যবহার করতে হবে। Wi-Fi upgrade না হলে বড় (>1 MB) Quick Share BLE payload clean error দেয়। PIN, accepted-byte progress, Cancel ও remote completion tracking আছে; শুধু connection/discovery দেখে Sent দেখায় না। একটি outgoing batch চলে; receiving-এর বিদ্যমান concurrent handling থাকে। Windows network changes and actual phone transfer tests remain required.
## ফাইলের স্থায়িত্ব

প্রতি customer আলাদা session, bounded offers, safe filename validation, authenticated Quick Share completion, partial-file exclusion, SHA-256 ও atomic flat-file commit রয়েছে। Quick Share native staging থেকে শুধু protocol-completed batch import হয়; arbitrary folder watcher ব্যবহৃত হয় না। Durable completion marker restart recovery এবং duplicate detection দেয়। Local receive hash sender-এর original source equality-এর স্বাধীন প্রমাণ নয়।

## Build ও tests

.NET SDK 8, Windows SDK targeting support, Rust stable MSVC, Visual Studio C++ build tools এবং `protoc` প্রয়োজন। Source bundle-এ patched protocol core আছে; patch scripts আবার চালাবেন না।

GitHub থেকে clone করে প্রথমবার dependencies প্রস্তুত করুন (Internet প্রয়োজন):

```powershell
git clone https://github.com/Tanvir00ff00/Next-shere.git
cd Next-shere
.\tools\Prepare-NativeDependencies.ps1
```

এই script Cargo.lock অনুযায়ী native dependencies download করে local `.tools/native-vendor` ও `.cargo/config.toml` তৈরি করে। এগুলো Git-এ commit হয় না; release source ZIP-এ offline rebuild-এর জন্য থাকে। NSIS ও Python 3 installer build-এর জন্য প্রয়োজন।

```powershell
$env:PROTOC = 'C:\path\to\protoc.exe'
cargo build --release --locked --manifest-path src/NextShare.QuickShare.Native/Cargo.toml
dotnet run --project tests/NextShare.Tests -c Release -- --report artifacts/core-tests.json
dotnet publish src/NextShare.App -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o publish/NextShare-0.4.7
```

Build/test commands shared Core output ব্যবহার করে; sequentially চালান। Debug app build-এর আগে native debug backend build করুন।

Source ZIP-এ `.cargo/config.toml` ও `.tools/native-vendor` dependency sources রয়েছে। Native build-এ `--offline` যোগ করে registry access ছাড়াই build করা যায়; Rust toolchain ও protoc আগে install থাকতে হবে।

Packaged smoke test isolated data ব্যবহার করে; radio/Google settings পরিবর্তন করে না:

```powershell
.\publish\NextShare-0.4.7\NextShare.exe --smoke-ui-only --smoke-native-transfer --data-root H:\NextShare\artifacts\fresh-smoke
```

যাচাই: Core 60/60 passed, including notification delivery/metadata boundaries, readiness, capacity, four-peer serialization/isolation, bounded retries and cancellation। চারটি concurrent encrypted protocol sender থেকে আটটি ফাইল/2,097,706 bytes original hash-সহ flat destination-এ এসেছে। Material UI search, history clear-এর পরে file retention, persisted theme, dark/accent এবং master OFF/ON পরীক্ষা হয়েছে। 830×560, 820×552, 760×480, 640×440 ও 1100×740-এ animation clipping ও continuous motion পরীক্ষা হয়। তিনটি durable commit থেকে tray notification, coalescing, non-activation, দুই সেকেন্ড lifetime, embedded sound load এবং notification/sound toggles পরীক্ষা হয়। Smoke test speaker playback বন্ধ রাখে; sound-এর শ্রুতিগত মান operator preview-তে বিচার করবেন। Protocol sender test একটি software loopback test; customer phone বা throughput benchmark নয়।

- [বর্তমান own-receiver architecture](docs/ADR-002-own-quick-share.md)
- [UI tokens ও interaction](docs/ui-design.md)
- [Verification history](docs/verification.md)
- [Third-party notices ও source provenance](THIRD-PARTY-NOTICES.md)

Release-এর `NextShare-0.4.7-source.zip`-এ source ও GPL protocol backend-এর license রয়েছে।

Installer পুনরায় build করতে `tools\Build-Installer.ps1` চালান। এটি Core/Windows guard tests, self-contained publish, startup UI/protocol smoke, corresponding source bundle এবং NSIS compile চালায়। Build নিজে service/firewall/startup install করে না। বাস্তব UAC install/uninstall এবং reboot/login test operator চালাবেন।

