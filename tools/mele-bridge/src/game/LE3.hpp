#pragma once

// LE3's facts. Include game/Game.hpp, not this file.

/// The F8 editor and its overlay: off. Editor.cpp's classes and offsets are LE1's.
#define MELEBRIDGE_EDITOR 0
/// The status op's squad screen: off. LE3 has no BioSFHandler_PartySelection, and no GetScaleFormManager or GetPanelByTag.
#define MELEBRIDGE_PARTY_SELECT 0
/// BioWorldInfo.Tick's focus pause has no timer here: m_bAllowBrowserWheel gates it (SFXGame #18103).
#define MELEBRIDGE_FOCUS_TIMER 0

namespace MeleBridge::Game
{
    // BioWorldInfo's two focus flags as the SDK lays them out (SFXGame_classes.hpp:12373, m_bAllowBrowserWheel below it).
    inline constexpr int FocusFlagsAt = 0x9C8;
    inline constexpr DWORD PausedByFocusLossMask = 0x8000;
    inline constexpr DWORD AllowBrowserWheelMask = 0x40000;

    /// BioWorldInfo.PauseGame(FALSE) (SFXGame #18161), which Tick calls here: it leaves bPlayersOnly alone when an SFXGRI has
    /// bPauseForCommand off, and then Tick's PauseGame(TRUE) never set it either.
    inline void EndFocusPause(AWorldInfo* world)
    {
        auto const gri = world->GRI;
        if (gri == nullptr || !gri->IsA(ASFXGRI::StaticClass()) || static_cast<ASFXGRI*>(gri)->bPauseForCommand != 0) world->bPlayersOnly = 0;
    }
}
