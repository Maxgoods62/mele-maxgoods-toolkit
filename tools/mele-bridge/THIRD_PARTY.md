# Third-party code in MeleBridge

MeleBridge is GPL-3.0, like the rest of this repo (see [`../../LICENSE`](../../LICENSE)). It builds with, or follows, the code below.

| What | Where it comes from | License | How we use it |
|---|---|---|---|
| [Dear ImGui](https://github.com/ocornut/imgui) | v1.93.0, commit `c1aaac65e896a45f81f47a4d68f9959e0f86c859` | MIT | The editor overlay's windows. `build.sh` fetches it into `D:\MELE\asi-build\third_party\imgui`; core files and `backends/imgui_impl_dx11.cpp` are compiled into `MeleBridge.asi`. |
| [ImGuizmo](https://github.com/CedricGuillemet/ImGuizmo) | master, commit `105e4fde45e4e74aae8973d98750820ef7aca7e2` | MIT | The red, green and blue move arrows. Fetched into `D:\MELE\asi-build\third_party\ImGuizmo`; `src/ImGuizmo.cpp` is compiled in. |
| [SuperAdjustmentStudio](https://github.com/Reyuu/SuperAdjustmentStudio) | `src/render/mouse.cpp`, commit `6859f5b` | GPL-3.0 | Not copied. `src/Input.cpp` follows its approach: hook DirectInput8 `GetDeviceState`/`GetDeviceData` from a device of our own, and zero the mouse state while the overlay has it. |
| [ME3Tweaks/LExASIs](https://github.com/ME3Tweaks/LExASIs) | commit `aaad03a` | none stated in the repo | Build tree only: the SDK (LExSDKv2), the ASI loader interface (SPI), spdlog and the `asi_target` CMake helper. Not part of this repo; `build.sh` clones it. |

The MIT notices of Dear ImGui and ImGuizmo are in their `LICENSE.txt` and `LICENSE` files, which ship with any copy of their
sources. If `MeleBridge.asi` is ever shared, ship those two files and the GPL-3.0 text with it.
