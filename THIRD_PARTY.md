# Third-party code in mele-mcp and lectool

mele-mcp and lectool are GPL-3.0, like the rest of this repo ([LICENSE](LICENSE)). This page lists what they build
with and what lands next to them in a build. MeleBridge, the ASI, has its own list:
[tools/mele-bridge/THIRD_PARTY.md](tools/mele-bridge/THIRD_PARTY.md).

The versions are the ones the projects resolve today (`dotnet list package --include-transitive` on
`tools/mele-mcp/src/MeleMcp/MeleMcp.csproj` and `tools/lectool/lectool.csproj`). Each license is the one the package's
`.nuspec` declares.

## LegendaryExplorerCore

| What | Version | License | How we use it |
|---|---|---|---|
| [LegendaryExplorerCore](https://github.com/ME3Tweaks/LegendaryExplorer) (LEC) | 6.5.0.0, the submodule at `tools/mele-mcp/external/LegendaryExplorer`, commit `43bf73a` | GPL-3.0 (`LICENSE` in the submodule) | Both tools build it from source (`WinRelease`, x64) and link it. It reads and writes the game's packages. |

The Legendary Explorer repo keeps the notices of code it includes in its `Licenses/` folder: the LZMA SDK (public
domain), MassEffectModder (GPL-3.0), MiniZip and zlib (zlib license), FastZlib (BSD 3-clause), Psychonauts Explorer
(MPL-2.0, ME1 audio and ISB parsing) and Peregrine66 (MIT, used by the Legendary Explorer app, not by LEC).

## NuGet packages

| Package | Version | License | Used by |
|---|---|---|---|
| [ModelContextProtocol](https://github.com/modelcontextprotocol/csharp-sdk), ModelContextProtocol.Core | 2.3.0 | Apache-2.0 | mele-mcp |
| [YamlDotNet](https://github.com/aaubry/YamlDotNet) | 16.3.0 | MIT | mele-mcp |
| [BCnEncoder.Net](https://github.com/Nominom/BCnEncoder.NET) | 2.2.1 | MIT or Unlicense | both, through LEC |
| BCnEncoder.Net.ImageSharp | 1.1.2 | MIT or Unlicense | both, through LEC |
| [CommunityToolkit.Diagnostics](https://github.com/CommunityToolkit/dotnet), CommunityToolkit.HighPerformance | 8.4.0 | MIT | both, through LEC |
| [Newtonsoft.Json](https://github.com/JamesNK/Newtonsoft.Json) | 13.0.4 | MIT | both, through LEC |
| [Serilog](https://github.com/serilog/serilog) | 4.3.0 | Apache-2.0 | both, through LEC |
| [SixLabors.ImageSharp](https://github.com/SixLabors/ImageSharp) | 3.1.12 | Six Labors Split License 1.0 (below) | both, through LEC |
| [Microsoft.IO.RecyclableMemoryStream](https://github.com/Microsoft/Microsoft.IO.RecyclableMemoryStream) | 3.0.1 | MIT | both, through LEC |
| Microsoft .NET packages (list below) | | MIT | see the list |

SixLabors.ImageSharp is under the Six Labors Split License, not MIT. Its text (`LICENSE` in the package) grants the
Apache License 2.0 when the package is used in software under an open source license, or as a transitive dependency;
other uses need a Six Labors commercial license. Both apply here: LEC pulls it in, and mele-mcp and lectool are
GPL-3.0.

The Microsoft .NET packages, all MIT:

| Packages | Version | Used by |
|---|---|---|
| Microsoft.Extensions.Hosting and what it pulls in: Microsoft.Extensions.Caching.Abstractions, .Configuration, .Configuration.Abstractions, .Configuration.Binder, .Configuration.CommandLine, .Configuration.EnvironmentVariables, .Configuration.FileExtensions, .Configuration.Json, .Configuration.UserSecrets, .DependencyInjection, .DependencyInjection.Abstractions, .Diagnostics, .Diagnostics.Abstractions, .FileProviders.Abstractions, .FileProviders.Physical, .FileSystemGlobbing, .Hosting.Abstractions, .Logging, .Logging.Abstractions, .Logging.Configuration, .Logging.Console, .Logging.Debug, .Logging.EventLog, .Logging.EventSource, .Options, .Options.ConfigurationExtensions, .Primitives | 10.0.13 | mele-mcp |
| System.Diagnostics.DiagnosticSource, System.Diagnostics.EventLog, System.IO.Pipelines, System.Net.ServerSentEvents, System.Text.Encodings.Web, System.Text.Json | 10.0.13 | mele-mcp |
| Microsoft.Extensions.AI.Abstractions (through ModelContextProtocol) | 10.11.0 | mele-mcp |
| System.Drawing.Common, Microsoft.Win32.SystemEvents, System.IO.Hashing | 10.0.2 | both, through LEC |
| System.Buffers | 4.6.1 | both, through LEC |
| Microsoft.Win32.Registry, System.Security.AccessControl, System.Security.Principal.Windows, Microsoft.NETCore.Platforms | 5.0.0 | both, through LEC |

LEC's build also runs Fody 6.6.4 and PropertyChanged.Fody 4.1.0 (both MIT). They rewrite LEC's assembly while it
builds and are referenced with `PrivateAssets="All"`, so neither lands in the output.

## Native DLLs

LEC copies three prebuilt Windows DLLs from `LegendaryExplorerCore/Libraries/Windows` into every build of mele-mcp
and lectool. They are the only native DLLs in the output. The `MeleMcp.exe` and `lectool.exe` launchers are the .NET
SDK's app host.

| DLL | What it is | License |
|---|---|---|
| `CompressionWrappers.dll` | Native decompression for packages, built from the MassEffectModder library (`Licenses/COPYING_MASSEFFECTMODDER.txt` in the Legendary Explorer repo) | GPL-3.0, per that notice |
| `TexConverter.dll` | Texture format conversion, called by LEC's `Textures/TexConverter.cs` | license not stated |
| `ISACTTools.dll` | An ISACT (LE1 audio) helper. LEC's own code does not call it; lectool's `ogg` command calls its `CreateIPSOgg` export. | license not stated |

## Oodle

Neither tool ships Oodle, and neither repo nor any release archive may contain it. LEC loads `oo2core_8_win64.dll`
from the game's `Binaries\Win64` folder at run time. It may also copy that file into the first folder of the .NET
native DLL search path, as a cache; with a framework-dependent build that is the .NET runtime's
`shared\Microsoft.NETCore.App\<version>` folder. Build a release archive from a clean publish folder, and check its
listing for `oo2core`.

## The .NET runtime

Both builds are framework-dependent: `MeleMcp.runtimeconfig.json` and `lectool.runtimeconfig.json` ask for
`Microsoft.NETCore.App` 8.0, which is not part of the output.
