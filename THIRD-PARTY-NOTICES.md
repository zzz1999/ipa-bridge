# Third-party notices

IPA Bridge's Windows executable contains the Microsoft .NET Runtime and Windows Presentation Foundation (WPF). It can also integrate with independently maintained command-line tools that are downloaded only after an explicit user action or supplied by the user.

## Microsoft .NET Runtime

- Source: https://github.com/dotnet/runtime/tree/v8.0.29
- Runtime pack: Microsoft.NETCore.App.Runtime.win-x64 version 8.0.29
- Purpose: managed application runtime and base class libraries embedded in the self-contained Windows executable
- Open-source license and notices: The automatic build copies LICENSE.TXT and THIRD-PARTY-NOTICES.TXT directly from the exact restored 8.0.29 Windows x64 runtime pack and publishes them as DOTNET-RUNTIME-LICENSE.txt and DOTNET-RUNTIME-THIRD-PARTY-NOTICES.txt.

The MIT source and package notices do not describe every Windows-native binary in the self-contained executable. Microsoft identifies coreclr.dll and .NET runtimes embedded in Windows single-file applications as governed by the .NET Library License. The full [DOTNET-LIBRARY-LICENSE.txt](licenses/dotnet-8.0.29/DOTNET-LIBRARY-LICENSE.txt) is therefore also published with each automatic Release. See Microsoft's [.NET Windows license information](https://github.com/dotnet/core/blob/main/license-information-windows.md).

## Microsoft Windows Presentation Foundation

- Source: https://github.com/dotnet/wpf/tree/v8.0.29
- Runtime pack: Microsoft.WindowsDesktop.App.Runtime.win-x64 version 8.0.29
- Purpose: Windows desktop UI framework embedded in the self-contained executable
- Open-source license: The automatic build copies LICENSE directly from the exact restored 8.0.29 Windows Desktop runtime pack and publishes it as WPF-LICENSE.txt.
- Windows-native components: Microsoft identifies PresentationNative_cor3.dll, vcruntime140_cor3.dll, and wpfgfx_cor3.dll as governed by the .NET Library License. D3DCompiler_47_cor3.dll is governed by the Windows SDK License.
- Windows SDK terms: [WINDOWS-SDK-LICENSING-NOTICE.txt](licenses/dotnet-8.0.29/WINDOWS-SDK-LICENSING-NOTICE.txt) identifies D3DCompiler_47_cor3.dll and links to the authoritative Microsoft terms. Both that notice and DOTNET-LIBRARY-LICENSE.txt are published with each automatic Release.

## majd/ipatool

- Source: https://github.com/majd/ipatool
- Version pinned by this source tree: v2.3.1
- License: MIT
- Purpose: App Store authentication, search, licensing and encrypted IPA download
- Windows AMD64 archive: ipatool-2.3.1-windows-amd64.tar.gz, SHA-256 8e986ed9320f205bcd1fd24640ec46a5b92ff346425aff28d1103e57d2fdcadb
- Windows ARM64 archive: ipatool-2.3.1-windows-arm64.tar.gz, SHA-256 661ffbee49d25f46c463a2b38cd05b08048a4c939a194825b9e3316ad0867da9
- Distribution: After an explicit user action, the application downloads the architecture-specific asset directly from the official v2.3.1 GitHub Release and verifies it against the corresponding SHA-256 value embedded in this source tree. It does not rely on releases/latest or a mutable upstream checksum file.

## jkcoxson/idevice

- Source: https://github.com/jkcoxson/idevice
- Version pinned by this source tree: v0.1.65
- License: MIT
- Purpose: Windows communication with iOS device services and IPA installation
- Distribution: The application downloads the project's official `idevice-tools-windows-v0.1.65.zip` release asset only after an explicit user action and verifies SHA-256 `fbae49be4ca8fbbab716121a5a6d29445ec8b9fd4b5f01c0300bd912fae88356`.
- Status: Upstream describes the library and tooling as development/research stage; IPA Bridge treats this as a pinned convenience backend rather than an Apple-supported component.

## Optional libimobiledevice compatibility

IPA Bridge can invoke a user-supplied `libimobiledevice` / `ideviceinstaller` tool directory. IPA Bridge does not download or redistribute that toolchain.

- Source: https://github.com/libimobiledevice/libimobiledevice
- Installer source: https://github.com/libimobiledevice/ideviceinstaller
- Licenses: Components use LGPL and GPL licenses; consult the exact binaries supplied by the user.

Apple, iPhone, iPad, iOS, iPadOS and App Store are trademarks of Apple Inc. IPA Bridge is an independent project and is not affiliated with, endorsed by, or sponsored by Apple Inc.
