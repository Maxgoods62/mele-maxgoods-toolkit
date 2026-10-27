#pragma once

#include "MeleBridge/Bridge.hpp"

// The in-game editor (F8): see docs/07-ingame-editor.md. Three threads meet here:
// - the game thread (ProcessEvent hook) does everything that touches game objects (Editor.cpp);
// - the render thread (IDXGISwapChain::Present hook) draws the ImGui overlay from a snapshot and posts commands (Overlay.cpp);
// - the window thread queues mouse and key messages for ImGui and keeps them from the game while the editor is open (Input.cpp).
namespace MeleBridge::Editor
{
    // ! Shared state
    // ========================================

    /// The editor is open: set by the game thread, read everywhere (input is held back from the game while it is set).
    extern std::atomic<bool> Open;

    /// Set by the render thread from ImGui after each frame: the cursor is over a window, or a text field has focus.
    extern std::atomic<bool> WantMouse;
    extern std::atomic<bool> WantKeyboard;

    /// The right mouse button is held for free-cam look: the game thread turns cursor moves away from Anchor into look.
    extern std::atomic<bool> Looking;
    extern std::atomic<LONG> AnchorX;
    extern std::atomic<LONG> AnchorY;

    /// What the game thread saw at the end of its last BioHUD.PostRender, for the overlay.
    struct Snapshot
    {
        unsigned long long Frame = 0;
        bool Open = false;
        bool FreeCam = false;
        float Speed = 0;
        FTPOV Pov{};
        float CanvasWidth = 0;
        float CanvasHeight = 0;

        bool Selected = false;
        FVector SelectedLocation{};
        FRotator SelectedRotation{};
        bool SelectedStatic = false;
        std::string SelectedPath;
        std::string SelectedClass;
        std::string SelectedTag;
        /// Spawned by the editor this session.
        bool SelectedSpawned = false;
        /// Why Delete refuses the selection; empty when it can be deleted.
        std::string SelectedUndeletable;
        /// Canvas.Project of the selection's location, in canvas pixels: the reference for the overlay's own projection.
        bool Projected = false;
        FVector Projection{};

        std::string LastPick;

        /// StaticMeshes in memory, for the Props list. Built when the list is opened or refreshed, then shared, not copied.
        std::shared_ptr<std::vector<std::string> const> Meshes;

        /// Actors spawned, moved, turned or deleted, and how many of them changed since the last Save.
        int Edits = 0;
        int Unsaved = 0;
        /// The last thing the editor did or refused, for a line under the toolbar; the frame tells a new one from an old one.
        std::string Status;
        unsigned long long StatusFrame = 0;
    };

    Snapshot Latest();

    /// From the overlay to the game thread, run at the next BioHUD.PostRender.
    struct Command
    {
        enum class Kind { Close, FreeCam, Pick, Move, Rotate, Deselect, Speed, Spawn, Delete, Save, RefreshMeshes };
        Kind Type;
        /// Pick: canvas pixels. Move: world location. Rotate: yaw in degrees. Speed: wheel notches.
        float X = 0;
        float Y = 0;
        float Z = 0;
        bool On = false;
        /// Spawn: the StaticMesh's path, as the Props list shows it.
        std::string Text;
    };

    void Post(Command const& command);

    /// The overlay's projection of the selection against Canvas.Project, for diagnostics.
    struct Calibration
    {
        bool Valid = false;
        float OursX = 0, OursY = 0;
        float CanvasX = 0, CanvasY = 0;
        float ErrorPixels = 0;
    };

    void SetCalibration(Calibration const& calibration);

    // ! Diagnostics
    // ========================================

    struct Counters
    {
        std::atomic<DWORD> PresentThread{ 0 };
        std::atomic<DWORD> PostRenderThread{ 0 };
        std::atomic<DWORD> CameraThread{ 0 };
        std::atomic<DWORD> WindowThread{ 0 };

        std::atomic<bool> PresentHooked{ false };
        std::atomic<bool> Present1Hooked{ false };
        std::atomic<bool> ResizeHooked{ false };
        std::atomic<bool> DirectInputHooked{ false };
        std::atomic<bool> CursorHooked{ false };
        std::atomic<bool> WindowHooked{ false };
        std::atomic<bool> OverlayReady{ false };
        std::atomic<bool> OverlayFailed{ false };

        std::atomic<unsigned long long> Presents{ 0 };
        std::atomic<unsigned long long> OverlayFrames{ 0 };
        std::atomic<unsigned long long> SwallowedMessages{ 0 };
        std::atomic<unsigned long long> ZeroedDeviceStates{ 0 };
        std::atomic<unsigned long long> ZeroedDeviceData{ 0 };
        std::atomic<unsigned long long> BlockedCursorCalls{ 0 };

        std::atomic<int> BackbufferWidth{ 0 };
        std::atomic<int> BackbufferHeight{ 0 };
    };

    extern Counters Diag;

    /// Pipe thread: open, camera, selection, edits, calibration, threads, hooks and counters as JSON.
    json State();

    // ! Game thread (Editor.cpp)
    // ========================================

    /// Called by the ProcessEvent hook after every call it lets through; reacts to BioHUD.PostRender and Camera.UpdateCamera.
    void AfterProcessEvent(UObject* context, UFunction* function, void* parms);

    /// Set while the editor's own game-thread code runs: the ProcessEvent hook then only forwards (no jobs, no events, no editor).
    bool Busy();

    // ! Render thread (Overlay.cpp)
    // ========================================

    void InstallOverlay(::LESDK::Initializer& Init);

    // ! Input (Input.cpp)
    // ========================================

    struct InputEvent
    {
        UINT Message;
        WPARAM WParam;
        LPARAM LParam;
        bool Ctrl, Shift, Alt;
    };

    void InstallInput(::LESDK::Initializer& Init);

    /// Render thread, first Present: route the game window's messages through ours.
    void SubclassWindow(HWND window);
    HWND GameWindow();

    /// Render thread: the messages queued since the last frame.
    std::vector<InputEvent> DrainInput();

    /// The cursor calls the game is kept from while the editor is open; ours go through.
    BOOL MoveCursor(int x, int y);
    /// Game thread, on open and close: keep the cursor in the window while open, give the game its own clip back after.
    void HoldCursor(bool hold);
}
