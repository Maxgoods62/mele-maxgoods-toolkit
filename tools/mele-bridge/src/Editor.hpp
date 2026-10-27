#pragma once

#include "MeleBridge/Bridge.hpp"

#include <array>

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

    /// The map's session file as the re-apply passes left it (docs/07 §10). Rebuilt when it changes, then shared, not copied.
    struct SessionState
    {
        std::string File;
        /// Why the file could not be read; empty when it was, or when there is none.
        std::string Error;
        /// Entries written back onto their actor (or spawned again), and entries dropped from the file, since the map loaded.
        int Applied = 0;
        int Dropped = 0;
        /// Entries and edits still waiting for their actor, as "key: why".
        std::vector<std::string> Pending;
        /// The loaded levels the last pass saw: "BIOA_X", or "BIOA_X (hidden)" for one loaded but not visible.
        std::vector<std::string> Levels;
        /// The game frame of the last pass; 0 before the first.
        unsigned long long PassFrame = 0;
    };

    /// levels.json as the last map change read it (docs/07 §11): which level files are ours. Shared, not copied.
    struct LevelsState
    {
        std::string File;
        /// Why it was not read ("levels.json missing: run mod_build"); empty when it was.
        std::string Problem;
        /// The DLC folder our level files lie in.
        std::string Dlc;
        /// Its keys: the level files built from our YAMLs.
        std::vector<std::string> Levels;
        /// Its props and lights tags, all levels together: the built props the editor switches and the lights it lists.
        int Props = 0;
        int Lights = 0;
    };

    /// Props: a prop light, on the Gameplay_4 channel only (it lights props, never walls, pawns or vanilla meshes). Room: walls,
    /// vanilla meshes, pawns and props too (BSP, Static, Dynamic, CompositeDynamic and Gameplay_4). Stored by name, never as bits.
    enum class LightKind { Props, Room };

    /// The widest prop light Auto light and L make (docs/07 §11): a prop that needs more from where the view is gets none and
    /// keeps its light environment. A prop light lights every prop on Gameplay_4 within its radius, so a wider one adds to the
    /// props around it, each tuned against the vanilla mesh beside it (the in-game test's huge catalog props got 2421 and 1991,
    /// a whole area). 1024 is the engine's own default for a point light (a spawned one starts with it), three times the widest
    /// the freighter's crates needed (190-340), and 2.5 bounds radii reach it at a bounds radius of about 410: about 310 of the
    /// catalog's 354 meshes fit under it from every side and turn, tables and big crates included, and 330 from some side. The
    /// rest are over about 450 units across (rock piles, drills, market stalls, cargo containers, and the catalog's large
    /// ones, over 600 units: architecture), which a room light lights better, with the walls around them.
    constexpr float MaxAutoRadius = 1024;

    /// A light's settings as a session entry's light block holds them (docs/07 §11). Colour in 0-255; falloff is
    /// FalloffExponent.
    struct LightBlock
    {
        LightKind Kind = LightKind::Props;
        float Radius = 350;
        float Brightness = 0.15f;
        std::array<int, 3> Color{ 255, 255, 255 };
        float Falloff = 2;
        bool Enabled = true;

        bool operator==(LightBlock const&) const = default;
    };

    /// A light the editor lists (its own spawns and the YAMLs' lights: entries), for the icons and the Lights tab.
    struct LightIcon
    {
        /// Its path: what a click on the icon or its row sends back.
        std::string Path;
        std::string Tag;
        FVector Location{};
        LightBlock Light;
    };

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
        float SelectedDrawScale = 1;
        FVector SelectedDrawScale3D{ 1, 1, 1 };
        /// A pawn turns about the vertical only and is not scaled: npcs keep a yaw in the level YAML.
        bool SelectedPawn = false;
        bool SelectedStatic = false;
        std::string SelectedPath;
        std::string SelectedClass;
        std::string SelectedTag;
        /// Spawned by the editor this session.
        bool SelectedSpawned = false;
        /// Why Delete refuses the selection; empty when it can be deleted.
        std::string SelectedUndeletable;
        /// Why Move, Turn and Scale refuse it (a vanilla actor that is not a prop, a room shell: docs/07 §11); empty when they take it.
        std::string SelectedUnmovable;
        /// Whether an edit of a vanilla selection ships ("ships: ...", "dev-only: ..."); empty for an actor of our levels, a spawn,
        /// or an actor Move, Turn and Scale refuse (SelectedUnmovable).
        std::string SelectedShips;
        /// The file the selection's level was loaded from, while the game keeps its linker; empty otherwise.
        std::string SelectedFile;
        /// A light the editor lists: its settings, for the panel. It only moves.
        bool SelectedLight = false;
        LightBlock SelectedLightBlock;
        /// A prop the editor switches to prop lights (docs/07 §11): whether a prop light covers it, what it gets from prop lights
        /// (E) and what a fresh Auto light would give it (T), and that light's radius from where the view is: over
        /// MaxAutoRadius, Auto light and L add none and the target is nothing.
        bool SelectedProp = false;
        bool SelectedCovered = false;
        float SelectedReceived = 0;
        float SelectedTarget = 0;
        float SelectedAutoRadius = 0;
        /// Canvas.Project of the selection's location, in canvas pixels: the reference for the overlay's own projection.
        bool Projected = false;
        FVector Projection{};

        std::string LastPick;

        /// StaticMeshes in memory, for the Props list. Built when the list is opened or refreshed, then shared, not copied.
        std::shared_ptr<std::vector<std::string> const> Meshes;

        /// The lights the editor lists in visible levels, deleted ones left out.
        std::vector<LightIcon> Lights;
        /// Auto light: a fresh spawn that gets too little from prop lights gets one of its own.
        bool AutoLight = true;

        /// Actors spawned, moved, turned, scaled, deleted or re-applied from the session, and how many of them a Save would write
        /// (changed since the last Save; a spawn deleted before its first save leaves nothing).
        int Edits = 0;
        int Unsaved = 0;
        /// Why the last save left edits unsaved (a map's file could not be written); empty once a save wrote every map. Shown
        /// in the toolbar until then: the status line fades, and the first pass after a load replaces it.
        std::string SaveProblem;
        /// The last thing the editor did or refused, for a line under the toolbar; the frame tells a new one from an old one.
        std::string Status;
        unsigned long long StatusFrame = 0;

        /// What Undo and Redo would do ("move ed_crate_x_1"); empty when there is nothing to undo or redo.
        std::string UndoLabel;
        std::string RedoLabel;
        std::shared_ptr<SessionState const> Session;
        std::shared_ptr<LevelsState const> OurLevels;
    };

    Snapshot Latest();

    /// From the overlay to the game thread, run at the next BioHUD.PostRender.
    struct Command
    {
        /// Move, Rotate, Scale and Light are drag commands: the first one of a drag opens its undo step. EndDrag closes it and lets
        /// the paused world tick once, so the prop's light catches up. PickLight selects a listed light, Light sets the selected
        /// light's settings (the panel), AddLight spawns a prop light (On: a room light) where the view points, PropLight (L)
        /// adds a prop light for the selected prop, AutoLight turns Auto light on or off.
        enum class Kind { Close, FreeCam, Pick, Move, Rotate, Scale, Deselect, Speed, Spawn, Delete, Save, RefreshMeshes, Undo, Redo, EndDrag,
                          PickLight, Light, AddLight, PropLight, AutoLight };
        Kind Type;
        /// Pick: canvas pixels. Move: world location. Scale: DrawScale3D. Speed: wheel notches.
        float X = 0;
        float Y = 0;
        float Z = 0;
        /// Rotate: the whole rotation, in Unreal units (65536 = 360 degrees).
        FRotator Rotation{};
        bool On = false;
        /// Spawn: the StaticMesh's path in memory, as the In this level list shows it, or a catalog entry's vanilla path. PickLight:
        /// the light's path.
        std::string Text;
        /// Spawn from the catalog: the catalog package's path, which DynamicLoadObject reads when no level has Text in memory,
        /// and a vanilla file that has the mesh (the session's mesh.file, for editor_import). Empty for the In this level list.
        std::string Load;
        std::string Source;
        /// Light: the selected light's settings, all of them.
        LightBlock Light{};
    };

    void Post(Command const& command);

    /// %LOCALAPPDATA%\MeleBridge\LE1, outside the install: the session files and the prop catalog live there. Any thread.
    /// Throws BridgeError when LOCALAPPDATA is not set.
    std::wstring DataFolder();

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

        /// The Props catalog as the overlay last read it (-1: not read yet, or unreadable), and its thumbnails on the GPU.
        std::atomic<int> CatalogEntries{ -1 };
        std::atomic<int> Thumbnails{ 0 };
    };

    extern Counters Diag;

    /// Pipe thread: open, camera, selection, edits, undo, the session's re-apply, levels.json, calibration, threads, hooks and
    /// counters as JSON.
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
