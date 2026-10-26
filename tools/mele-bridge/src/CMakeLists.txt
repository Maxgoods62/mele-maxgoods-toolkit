# Built inside a checkout of ME3Tweaks/LExASIs (see ../build.sh), which provides asi_target, Common and LESDK.
asi_target (MeleBridge
  SHARED
  GAMES "LE1"
  SOURCES
    "Bridge.hpp"
    "Console.cpp"
    "Entry.cpp"
    "GameThread.cpp"
    "Ops.cpp"
    "Pipe.cpp"
    "Reflection.cpp"
    "Reflection.hpp"
    "SharedVersion.h"
  VLINKS
    "Common"
    "LESDK"
  )

# The one file the game-install rule lets us copy into the ASI folder is MeleBridge.asi.
set_target_properties (MeleBridge-LE1 PROPERTIES OUTPUT_NAME "MeleBridge")
