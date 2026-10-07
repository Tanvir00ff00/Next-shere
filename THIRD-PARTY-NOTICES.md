# Third-party notices

## Own Quick Share protocol backend

`nextshare-quickshare.exe`, `src/NextShare.QuickShare.Native` and the modified `third_party/open-quickshare/core_lib` use GPL-3.0-only code derived from:

- open-quickshare: https://github.com/ignotusbucius/open-quickshare
- upstream commit: `5a31145163ee22ab9cf1c7d3dffd74355febe93f`
- rquickshare lineage: https://github.com/Martichou/rquickshare

Original copyright/license notices are retained. The GPL text is `third_party/open-quickshare/LICENSE` and is copied into the release's licenses directory. Corresponding modified source is supplied as `NextShare-0.4.8-source.zip`, including native Cargo.lock, protocol source, Windows adapters and build instructions. A consolidated vendor patch is in `third_party/patches/open-quickshare-windows.patch`. Port changes include Windows file/hostname APIs, safe staging/name/size checks, completion flushing, empty payload support, malformed-key rejection and Windows local-hotspot transport upgrade. The advertisement codec is adapted from the upstream BLE implementation. Outgoing changes add bounded mDNS discovery, Windows GATT client bridging, phone-hosted joining and PC-hosted Wi-Fi Direct callbacks, printable endpoint IDs, completion/cancellation control, and fixed-width P256 coordinate parsing.

Cargo.lock pins other dependencies, including https://github.com/Martichou/mdns-sd. Their upstream licenses/copyrights remain applicable. This app does not redistribute Google's Quick Share binary and is not an official Google product.

## Material WPF UI

MaterialDesignInXamlToolkit 5.3.2: https://github.com/MaterialDesignInXAML/MaterialDesignInXamlToolkit — MIT.
MaterialDesignColors 5.3.2 and Microsoft.Xaml.Behaviors.Wpf 1.1.77 are NuGet dependencies under their respective MIT licenses. NuGet metadata identifies the exact versions and licensing. Material icons originate from Material Design Icons (Pictogrammers), Apache-2.0; see https://github.com/Templarian/MaterialDesign.

## Build tools

Protobuf protoc 33.4 is used only at build time, not shipped in the runnable release. Its license is retained alongside the downloaded build tool. .NET runtime notices are supplied under the runtime's applicable MIT/third-party terms.

## Signature sound playback

NAudio.Wasapi and NAudio.Core 2.2.1 by Mark Heath: https://github.com/naudio/NAudio — MIT. The original license is included in licenses/NAudio-MIT.txt. The chime is original synthesized Next Share audio, generated from source.


## Installed Windows offline guard

System.ServiceProcess.ServiceController 8.0.1 and its .NET dependencies: Microsoft / .NET Foundation, MIT. License and runtime third-party notices are included in licenses/DotNet-MIT.txt and licenses/DotNet-THIRD-PARTY.txt. NuGet project references pin the package version.

The Windows installer is built with NSIS (https://nsis.sourceforge.io). NSIS runtime code is distributed under its zlib/libpng-style license; the notice is included in licenses/NSIS-zlib.txt. Installer sources and build scripts are included in the corresponding source ZIP.

