# Built inside a checkout of ME3Tweaks/LExASIs (see ../build.sh), which provides asi_target, Common and LESDK.

# Dear ImGui and ImGuizmo (MIT, see ../THIRD_PARTY.md), fetched at pinned commits by build.sh. A library of their own: they are
# built with their defaults, not the ASI targets' /W4 /WX.
set (MELE_THIRD_PARTY "${CMAKE_SOURCE_DIR}/../third_party" CACHE PATH "Where build.sh put imgui and ImGuizmo")
add_library (MeleImGui STATIC
  "${MELE_THIRD_PARTY}/imgui/imgui.cpp"
  "${MELE_THIRD_PARTY}/imgui/imgui_demo.cpp"
  "${MELE_THIRD_PARTY}/imgui/imgui_draw.cpp"
  "${MELE_THIRD_PARTY}/imgui/imgui_tables.cpp"
  "${MELE_THIRD_PARTY}/imgui/imgui_widgets.cpp"
  "${MELE_THIRD_PARTY}/imgui/backends/imgui_impl_dx11.cpp"
  "${MELE_THIRD_PARTY}/ImGuizmo/src/ImGuizmo.cpp"
  )
target_include_directories (MeleImGui PUBLIC "${MELE_THIRD_PARTY}/imgui" "${MELE_THIRD_PARTY}/ImGuizmo/src")
set_target_properties (MeleImGui PROPERTIES FOLDER "External")

asi_target (MeleBridge
  SHARED
  GAMES "LE1"
  SOURCES
    "Bridge.hpp"
    "Console.cpp"
    "Editor.cpp"
    "Editor.hpp"
    "Entry.cpp"
    "GameThread.cpp"
    "Input.cpp"
    "Ops.cpp"
    "Overlay.cpp"
    "Pipe.cpp"
    "Reflection.cpp"
    "Reflection.hpp"
    "SharedVersion.h"
    "Status.cpp"
  LINKS
    "MeleImGui"
    "d3d11"
    "dxgi"
    "dxguid"
    "windowscodecs"
  VLINKS
    "Common"
    "LESDK"
  )

# The one file the game-install rule lets us copy into the ASI folder is MeleBridge.asi.
set_target_properties (MeleBridge-LE1 PROPERTIES OUTPUT_NAME "MeleBridge")
