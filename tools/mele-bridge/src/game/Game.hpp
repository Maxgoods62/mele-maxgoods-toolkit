#pragma once

#include <LESDK/Headers.hpp>

// What differs from one game to the next, picked by the SDK target this build is for. Each game's header holds the same
// names (namespace MeleBridge::Game) and the switches: MELEBRIDGE_EDITOR (the F8 editor and its overlay),
// MELEBRIDGE_PARTY_SELECT (the status op's squad screen) and MELEBRIDGE_FOCUS_TIMER (how keep_ticking gets past
// BioWorldInfo.Tick's focus pause: GameThread.cpp).
#if defined(SDK_TARGET_LE1)
    #include "MeleBridge/game/LE1.hpp"
#elif defined(SDK_TARGET_LE2)
    #include "MeleBridge/game/LE2.hpp"
#elif defined(SDK_TARGET_LE3)
    #include "MeleBridge/game/LE3.hpp"
#else
    #error "MeleBridge: no game/LE<n>.hpp for this SDK target"
#endif

#if !defined(MELEBRIDGE_EDITOR) || !defined(MELEBRIDGE_PARTY_SELECT) || !defined(MELEBRIDGE_FOCUS_TIMER)
    #error "MeleBridge: game/LE<n>.hpp must define MELEBRIDGE_EDITOR, MELEBRIDGE_PARTY_SELECT and MELEBRIDGE_FOCUS_TIMER"
#endif
