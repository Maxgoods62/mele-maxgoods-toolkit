#pragma once

// LE2's facts. Include game/Game.hpp, not this file.

/// The F8 editor and its overlay: off. Editor.cpp's classes and offsets are LE1's.
#define MELEBRIDGE_EDITOR 0
/// The status op's squad screen: the same names as LE1 (GetScaleFormManager, GetPanelByTag, BioSFHandler_PartySelection).
#define MELEBRIDGE_PARTY_SELECT 1
/// BioWorldInfo.Tick's focus pause has no timer here: m_bAllowBrowserWheel gates it (SFXGame #32522).
#define MELEBRIDGE_FOCUS_TIMER 0

namespace MeleBridge::Game
{
    // BioWorldInfo's two focus flags as the SDK lays them out (SFXGame_classes.hpp:9398, m_bAllowBrowserWheel below it).
    inline constexpr int FocusFlagsAt = 0xB0C;
    inline constexpr DWORD PausedByFocusLossMask = 0x400;
    inline constexpr DWORD AllowBrowserWheelMask = 0x8000;

    /// What BioWorldInfo.Tick does when the focus comes back, with m_bPausedByFocusLoss (SFXGame #32522).
    inline void EndFocusPause(AWorldInfo* world) { world->bPlayersOnly = 0; }
}
