# Third-party code in MeleBridge

MeleBridge is GPL-3.0, like the rest of this repo ([`../../LICENSE`](../../LICENSE)). It builds with, or follows, the
code below. None of it is part of this repo: `build.sh` clones or fetches the code it compiles,
at pinned commits.

| What | Where it comes from | License | How we use it |
|---|---|---|---|
| [Dear ImGui](https://github.com/ocornut/imgui) | v1.93.0, commit `c1aaac65e896a45f81f47a4d68f9959e0f86c859` | MIT | The editor overlay's windows. `build.sh` fetches it into `third_party/imgui`, next to the LExASIs checkout. The core files and `backends/imgui_impl_dx11.cpp` are compiled into `MeleBridge.asi`. |
| [ImGuizmo](https://github.com/CedricGuillemet/ImGuizmo) | commit `105e4fde45e4e74aae8973d98750820ef7aca7e2` | MIT | The gizmos: the red, green and blue move arrows, the turn rings and the scale handles. Fetched into `third_party/ImGuizmo`; `src/ImGuizmo.cpp` is compiled in. |
| [ME3Tweaks/LExASIs](https://github.com/ME3Tweaks/LExASIs) | commit `aaad03a` | none stated in the repo | The build tree: the `asi_target` CMake helper and the `Common` code (logger, memory and object helpers), which is compiled in. `build.sh` clones it with its submodules. |
| [LExSDKv2](https://github.com/llamathings/LExSDKv2) | the LExASIs submodule at `3885be1` | none: its README says no license is specified | The SDK (the game's classes in C++), compiled in. Its `External/SPI.h` is the ASI loader's plugin interface. |
| [spdlog](https://github.com/gabime/spdlog) | v1.14.1, the LExASIs submodule at `27cb4c7` | MIT | The log file, through LExASIs' `Common`. Compiled in. |
| [nlohmann/json](https://github.com/nlohmann/json) | v3.11.2, the copy in LExASIs at `Common/json.hpp` | MIT | The pipe's JSON requests and replies, and the session, `levels.json` and catalog files. Header only, compiled in. |
| [SuperAdjustmentStudio](https://github.com/Reyuu/SuperAdjustmentStudio) | `src/render/mouse.cpp`, commit `6859f5b` | GPL-3.0 | Not copied. `src/Input.cpp` follows its approach: hook DirectInput8 `GetDeviceState`/`GetDeviceData` from a device of our own, and zero the mouse state while the overlay has it. |

The MIT notices are in each project's license file (`LICENSE.txt` for Dear ImGui, `LICENSE` for ImGuizmo and spdlog)
and, for nlohmann/json, in the header of `json.hpp`.

No prebuilt `MeleBridge.asi` is published: it would contain the LExASIs and LExSDKv2 code, which states no license, so
you build it from source ([README](README.md#build-and-install)). If a binary is ever shared, ship the GPL-3.0 text and
the MIT notices of Dear ImGui, ImGuizmo, spdlog and nlohmann/json with it.
