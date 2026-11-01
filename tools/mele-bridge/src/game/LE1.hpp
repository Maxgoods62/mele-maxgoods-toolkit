#pragma once

// LE1's facts. Include game/Game.hpp, not this file.

/// The F8 editor and its overlay (Editor.cpp, Overlay.cpp, Input.cpp): written against LE1's classes and offsets.
#define MELEBRIDGE_EDITOR 1
/// The status op's squad screen: a ScaleForm panel with a BioSFHandler_PartySelection handler.
#define MELEBRIDGE_PARTY_SELECT 1
/// BioWorldInfo.Tick's focus pause asks CanPause, which m_fNoBrowserWheelTimer turns off.
#define MELEBRIDGE_FOCUS_TIMER 1

namespace MeleBridge::Game
{
    /// What BioWorldInfo.Tick does when the focus comes back, with m_bPausedByFocusLoss (SFXGame #25278).
    inline void EndFocusPause(AWorldInfo* world) { world->bPlayersOnly = 0; }
}
