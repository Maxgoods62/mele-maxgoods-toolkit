#include "MeleBridge/Editor.hpp"
#include "MeleBridge/Reflection.hpp"
#include "Common/Objects.hpp"

#include <algorithm>
#include <cmath>
#include <format>

// The editor's game-thread half: open and close (F8, pause), the free camera, picking, moving the selection, and the snapshot
// the overlay draws from. Everything here runs inside the ProcessEvent hook, right after BioHUD.PostRender or
// Camera.UpdateCamera returned.
namespace MeleBridge::Editor
{
    std::atomic<bool> Open{ false };
    std::atomic<bool> WantMouse{ false };
    std::atomic<bool> WantKeyboard{ false };
    std::atomic<bool> Looking{ false };
    std::atomic<LONG> AnchorX{ 0 };
    std::atomic<LONG> AnchorY{ 0 };
    Counters Diag;

    namespace
    {
        constexpr float DegreesToUnits = 65536.f / 360.f;
        constexpr float DegreesToRadians = 3.14159265f / 180.f;
        constexpr int TRACEFLAG_Bullet = 1;
        /// Look speed (degrees per pixel of cursor travel) and fly speed bounds (units per second).
        constexpr float LookDegreesPerPixel = 0.15f;
        constexpr float MinSpeed = 50, MaxSpeed = 20000;
        /// Bounds bigger than this are rooms and shells, not props, for the click fallback.
        constexpr float MaxPropExtent = 3000;

        thread_local bool InEditor = false;

        std::mutex SharedLock;
        Snapshot Shared;
        std::deque<Command> Commands;
        Calibration LastCalibration;

        struct
        {
            UFunction* DeProject = nullptr;
            UFunction* Project = nullptr;
            UFunction* Trace = nullptr;
            UFunction* ForceUpdateComponents = nullptr;
            bool Tried = false;
        } Fn;

        /// Game thread only.
        struct
        {
            bool Open = false;
            bool F8Down = false;
            /// The world we paused, until its pause is given back (also after a failure closed the editor).
            AWorldInfo* PausedWorld = nullptr;
            int PausedIndex = -1;
            bool WasPlayersOnly = false;
            /// The player's world at the last tick: a new one means a map change or a load.
            AWorldInfo* World = nullptr;

            bool FreeCam = false;
            ACamera* Camera = nullptr;
            FVector Location{};
            float Yaw = 0;
            float Pitch = 0;
            float Fov = 70;
            float Speed = 600;
            LARGE_INTEGER LastTick{};

            AActor* Selected = nullptr;
            int SelectedIndex = -1;
            UClass* SelectedClass = nullptr;
            std::string LastPick;
            unsigned long long Frame = 0;
        } G;

        FVector Add(FVector a, FVector b) { return { a.X + b.X, a.Y + b.Y, a.Z + b.Z }; }
        FVector Scale(FVector v, float s) { return { v.X * s, v.Y * s, v.Z * s }; }

        bool Down(int key) { return (GetAsyncKeyState(key) & 0x8000) != 0; }
        bool Focused() { return GameWindow() != nullptr && GetForegroundWindow() == GameWindow(); }

        /// Rotation units wrapped to [-32768, 32767], in degrees.
        float Degrees(int units)
        {
            auto wrapped = units & 0xFFFF;
            if (wrapped > 32767) wrapped -= 65536;
            return static_cast<float>(wrapped) / DegreesToUnits;
        }

        /// Still the object the index pointed at: not destroyed, not replaced by a map change.
        bool Alive(UObject* object, int index)
        {
            auto& objects = *UObject::GObjObjects;
            return object != nullptr && index >= 0 && static_cast<UINT>(index) < objects.Count() && objects.GetData()[index] == object
                && !Common::IsDestroyed(object);
        }

        /// The selection, unless its slot was freed and reused: a new object at the same address and index has another class
        /// more often than not.
        bool SelectionAlive()
        {
            return Alive(G.Selected, G.SelectedIndex) && G.Selected->Class == G.SelectedClass;
        }

        void Select(AActor* actor)
        {
            G.Selected = actor;
            G.SelectedIndex = actor != nullptr ? actor->ObjectInternalInteger : -1;
            G.SelectedClass = actor != nullptr ? actor->Class : nullptr;
        }

        /// Gives the paused world its own bPlayersOnly back, if that world still exists. Called on close and after a failure, so
        /// it guards itself. No C++ object may live in this frame.
        void RestorePause()
        {
            __try
            {
                if (Alive(G.PausedWorld, G.PausedIndex)) G.PausedWorld->bPlayersOnly = G.WasPlayersOnly ? 1 : 0;
            }
            __except (GetExceptionCode() == EXCEPTION_ACCESS_VIOLATION ? EXCEPTION_EXECUTE_HANDLER : EXCEPTION_CONTINUE_SEARCH)
            {
            }
            G.PausedWorld = nullptr;
            G.PausedIndex = -1;
        }

        void ResolveFunctions()
        {
            if (Fn.Tried) return;
            Fn.Tried = true;
            try
            {
                Fn.DeProject = FindFunctionByPath(L"Engine.Canvas.DeProject");
                Fn.Project = FindFunctionByPath(L"Engine.Canvas.Project");
                Fn.Trace = FindFunctionByPath(L"Engine.Actor.Trace");
                Fn.ForceUpdateComponents = FindFunctionByPath(L"Engine.Actor.ForceUpdateComponents");
            }
            catch (std::exception const& e)
            {
                LEASI_ERROR("editor: {}", e.what());
            }
        }

        void SetFreeCam(APlayerController* pc, bool on)
        {
            if (on == G.FreeCam) return;
            if (on)
            {
                auto const camera = pc != nullptr ? pc->PlayerCamera : nullptr;
                if (camera == nullptr)
                {
                    LEASI_WARN("editor: no player camera, free cam stays off");
                    return;
                }
                // Start where the game camera is.
                auto const& pov = camera->CameraCache.POV;
                G.Location = pov.Location;
                G.Yaw = Degrees(pov.Rotation.Yaw);
                G.Pitch = Degrees(pov.Rotation.Pitch);
                G.Fov = pov.FOV > 1 ? pov.FOV : 70;
                G.Camera = camera;
                QueryPerformanceCounter(&G.LastTick);
            }
            else
            {
                G.Camera = nullptr;
                Looking.store(false);
            }
            G.FreeCam = on;
            LEASI_INFO("editor: free cam {}", on ? "on" : "off");
        }

        void SetOpen(APlayerController* pc, bool open)
        {
            if (open == G.Open) return;
            if (open)
            {
                ResolveFunctions();
                // Pause as photo mode does: only players tick. Their input is held back by Input.cpp.
                RestorePause();
                auto const world = pc->WorldInfo;
                if (world != nullptr)
                {
                    G.PausedWorld = world;
                    G.PausedIndex = world->ObjectInternalInteger;
                    G.WasPlayersOnly = world->bPlayersOnly;
                    world->bPlayersOnly = 1;
                }
            }
            else
            {
                SetFreeCam(pc, false);
                // After a map change the old WorldInfo is gone: RestorePause only writes to the one we paused if it is still there.
                RestorePause();
                Looking.store(false);
            }
            G.Open = open;
            Open.store(open, std::memory_order_release);
            HoldCursor(open);
            LEASI_INFO("editor {}", open ? "opened" : "closed");
        }

        bool Pickable(AActor* actor, APlayerController* pc)
        {
            return actor != nullptr && actor != pc && !actor->bDeleteMe && !actor->IsA(AWorldInfo::StaticClass());
        }

        /// The nearest visible primitive whose bounds the ray enters, for meshes the trace does not hit (no collision).
        /// Bounds that hold the camera, or room-sized ones, are skipped.
        AActor* PickByBounds(FVector origin, FVector dir, APlayerController* pc, float& distance)
        {
            AActor* best = nullptr;
            distance = 1e30f;
            auto& objects = *UObject::GObjObjects;
            auto const primitive = UPrimitiveComponent::StaticClass();
            for (UINT i = 0; i < objects.Count(); i++)
            {
                auto const object = objects.GetData()[i];
                if (object == nullptr || !object->IsA(primitive) || Common::IsDefaultObject(object) || Common::IsDestroyed(object)) continue;
                auto const component = static_cast<UPrimitiveComponent*>(object);
                auto const owner = component->Owner;
                if (!component->bAttached || component->HiddenGame || !Pickable(owner, pc) || owner->bHidden) continue;

                auto const& b = component->Bounds;
                float const o[3] = { origin.X, origin.Y, origin.Z };
                float const d[3] = { dir.X, dir.Y, dir.Z };
                float const c[3] = { b.Origin.X, b.Origin.Y, b.Origin.Z };
                float const e[3] = { b.BoxExtent.X, b.BoxExtent.Y, b.BoxExtent.Z };
                if (std::max({ e[0], e[1], e[2] }) > MaxPropExtent) continue;

                // Slab test.
                float enter = 0, leave = 1e30f;
                bool inside = true, hit = true;
                for (int axis = 0; axis < 3 && hit; axis++)
                {
                    float const lo = c[axis] - e[axis], hi = c[axis] + e[axis];
                    if (o[axis] < lo || o[axis] > hi) inside = false;
                    if (std::fabs(d[axis]) < 1e-6f)
                    {
                        if (o[axis] < lo || o[axis] > hi) hit = false;
                        continue;
                    }
                    float t1 = (lo - o[axis]) / d[axis], t2 = (hi - o[axis]) / d[axis];
                    if (t1 > t2) std::swap(t1, t2);
                    enter = std::max(enter, t1);
                    leave = std::min(leave, t2);
                    if (enter > leave) hit = false;
                }
                if (!hit || inside || enter >= distance) continue;
                distance = enter;
                best = owner;
            }
            return best;
        }

        void Pick(APlayerController* pc, UCanvas* canvas, float x, float y)
        {
            if (Fn.DeProject == nullptr || Fn.Trace == nullptr)
            {
                G.LastPick = "picking is off: Canvas.DeProject or Actor.Trace was not found";
                return;
            }
            UCanvas_execDeProject_Parms ray;
            ray.ScreenPos = { x, y };
            CallProcessEvent(canvas, Fn.DeProject, &ray);

            AActor_execTrace_Parms trace;
            trace.TraceStart = ray.WorldOrigin;
            trace.TraceEnd = Add(ray.WorldOrigin, Scale(ray.WorldDirection, 100000));
            trace.bTraceActors = 1;
            trace.ExtraTraceFlags = TRACEFLAG_Bullet;
            CallProcessEvent(pc, Fn.Trace, &trace);

            auto hit = trace.ReturnValue;
            std::string how = "trace";
            if (hit != nullptr && trace.HitInfo.HitComponent != nullptr) how += ", " + ToUtf8(NameOf(trace.HitInfo.HitComponent));
            if (!Pickable(hit, pc))
            {
                float distance = 0;
                auto const traced = hit;
                hit = PickByBounds(ray.WorldOrigin, ray.WorldDirection, pc, distance);
                how = std::format("bounds at {:.0f} (trace: {})", distance, ToUtf8(PathOf(traced)));
            }
            Select(hit);
            if (hit == nullptr) G.LastPick = std::format("nothing under ({:.0f}, {:.0f})", x, y);
            else G.LastPick = std::format("{} ({})", ToUtf8(PathOf(hit)), how);
            LEASI_INFO("editor: pick ({:.0f}, {:.0f}) from ({:.0f}, {:.0f}, {:.0f}): {}", x, y, ray.WorldOrigin.X, ray.WorldOrigin.Y,
                       ray.WorldOrigin.Z, G.LastPick);
        }

        void Move(float x, float y, float z)
        {
            if (!SelectionAlive() || Fn.ForceUpdateComponents == nullptr) return;
            // As live_place does: Location raw, then the components follow (SetLocation refuses bStatic actors).
            G.Selected->Location = { x, y, z };
            AActor_execForceUpdateComponents_Parms update;
            update.bCollisionUpdate = 1;
            update.bTransformOnly = 0;
            CallProcessEvent(G.Selected, Fn.ForceUpdateComponents, &update);
        }

        void Run(APlayerController* pc, UCanvas* canvas, Command const& c)
        {
            switch (c.Type)
            {
            case Command::Kind::Close: SetOpen(pc, false); break;
            case Command::Kind::FreeCam: SetFreeCam(pc, c.On); break;
            case Command::Kind::Pick: Pick(pc, canvas, c.X, c.Y); break;
            case Command::Kind::Move: Move(c.X, c.Y, c.Z); break;
            case Command::Kind::Deselect: Select(nullptr); break;
            case Command::Kind::Speed: G.Speed = std::clamp(G.Speed * std::pow(1.25f, c.X), MinSpeed, MaxSpeed); break;
            }
        }

        void Publish(APlayerController* pc, UCanvas* canvas)
        {
            Snapshot s;
            s.Frame = G.Frame;
            s.Open = G.Open;
            s.FreeCam = G.FreeCam;
            s.Speed = G.Speed;
            if (pc->PlayerCamera != nullptr) s.Pov = pc->PlayerCamera->CameraCache.POV;
            s.CanvasWidth = static_cast<float>(canvas->SizeX);
            s.CanvasHeight = static_cast<float>(canvas->SizeY);
            if (SelectionAlive())
            {
                auto const actor = G.Selected;
                s.Selected = true;
                s.SelectedLocation = actor->Location;
                s.SelectedStatic = actor->bStatic;
                s.SelectedPath = ToUtf8(PathOf(actor));
                s.SelectedClass = ToUtf8(NameOf(actor->Class));
                if (Fn.Project != nullptr)
                {
                    UCanvas_execProject_Parms project;
                    project.Location = actor->Location;
                    CallProcessEvent(canvas, Fn.Project, &project);
                    s.Projected = true;
                    s.Projection = project.ReturnValue;
                }
            }
            else
            {
                Select(nullptr);
            }
            s.LastPick = G.LastPick;
            std::scoped_lock lock{ SharedLock };
            Shared = std::move(s);
        }

        void Tick(ABioHUD* hud)
        {
            Diag.PostRenderThread.store(GetCurrentThreadId(), std::memory_order_relaxed);
            auto const pc = hud->PlayerOwner;
            auto const canvas = hud->Canvas;
            if (pc == nullptr || canvas == nullptr) return;
            G.Frame++;

            // A failure closed the editor without touching game memory: give the pause back now, from a known world.
            if (!G.Open && G.PausedWorld != nullptr) RestorePause();

            // A map change or a load: the camera, the selection and the paused world all belonged to the old one.
            if (pc->WorldInfo != G.World)
            {
                if (G.World != nullptr)
                {
                    if (G.Open) LEASI_INFO("editor: the world changed, editor closed");
                    SetOpen(pc, false);
                    Select(nullptr);
                    G.LastPick.clear();
                }
                G.World = pc->WorldInfo;
            }

            auto const f8 = Focused() && Down(VK_F8);
            if (f8 && !G.F8Down)
            {
                // Without the overlay the editor would hold input back with nothing on screen to close it.
                if (!G.Open && !Diag.OverlayReady.load()) LEASI_WARN("editor: the overlay is not running (see the swap chain lines above), F8 ignored");
                else SetOpen(pc, !G.Open);
            }
            G.F8Down = f8;
            if (G.Open && Diag.OverlayFailed.load())
            {
                LEASI_ERROR("editor: the overlay failed, editor closed");
                SetOpen(pc, false);
            }

            std::deque<Command> commands;
            {
                std::scoped_lock lock{ SharedLock };
                commands.swap(Commands);
            }
            if (G.Open)
            {
                for (auto const& c : commands)
                {
                    Run(pc, canvas, c);
                    if (!G.Open) break;
                }
            }
            Publish(pc, canvas);
        }

        void Fly(ACamera* camera)
        {
            Diag.CameraThread.store(GetCurrentThreadId(), std::memory_order_relaxed);
            if (!G.FreeCam || camera != G.Camera) return;

            LARGE_INTEGER now, frequency;
            QueryPerformanceCounter(&now);
            QueryPerformanceFrequency(&frequency);
            auto const dt = std::clamp(static_cast<float>(now.QuadPart - G.LastTick.QuadPart) / static_cast<float>(frequency.QuadPart), 0.f, 0.1f);
            G.LastTick = now;

            auto const focused = Focused();
            if (focused && Looking.load())
            {
                // The cursor goes back to where the button went down each frame: what it travelled is the look.
                POINT cursor;
                GetCursorPos(&cursor);
                auto const dx = cursor.x - AnchorX.load(), dy = cursor.y - AnchorY.load();
                if (dx != 0 || dy != 0)
                {
                    G.Yaw += static_cast<float>(dx) * LookDegreesPerPixel;
                    G.Pitch = std::clamp(G.Pitch - static_cast<float>(dy) * LookDegreesPerPixel, -89.f, 89.f);
                    MoveCursor(AnchorX.load(), AnchorY.load());
                }
            }
            G.Yaw = std::remainder(G.Yaw, 360.f);

            auto const yaw = G.Yaw * DegreesToRadians, pitch = G.Pitch * DegreesToRadians;
            FVector const forward{ std::cos(pitch) * std::cos(yaw), std::cos(pitch) * std::sin(yaw), std::sin(pitch) };
            FVector const right{ -std::sin(yaw), std::cos(yaw), 0 };
            if (focused && !WantKeyboard.load())
            {
                auto const along = (Down('W') ? 1.f : 0.f) - (Down('S') ? 1.f : 0.f);
                auto const side = (Down('D') ? 1.f : 0.f) - (Down('A') ? 1.f : 0.f);
                auto const up = (Down('E') ? 1.f : 0.f) - (Down('Q') ? 1.f : 0.f);
                auto const speed = G.Speed * (Down(VK_SHIFT) ? 4.f : 1.f) * (Down(VK_CONTROL) ? 0.25f : 1.f) * dt;
                G.Location = Add(G.Location, Scale(Add(Add(Scale(forward, along), Scale(right, side)), FVector{ 0, 0, up }), speed));
            }

            // GetPlayerViewPoint and GetFOVAngle read this cache for the rest of the frame: the rendered view follows.
            auto& pov = camera->CameraCache.POV;
            pov.Location = G.Location;
            pov.Rotation = { static_cast<int>(G.Pitch * DegreesToUnits), static_cast<int>(G.Yaw * DegreesToUnits), 0 };
            pov.FOV = G.Fov;
        }

        void TickEntry(UObject* hud)
        {
            try
            {
                Tick(static_cast<ABioHUD*>(hud));
            }
            catch (std::exception const& e)
            {
                LEASI_ERROR("editor tick: {}", e.what());
            }
        }

        void FlyEntry(UObject* camera)
        {
            try
            {
                Fly(static_cast<ACamera*>(camera));
            }
            catch (std::exception const& e)
            {
                LEASI_ERROR("editor camera: {}", e.what());
            }
        }

        /// An access violation in editor code fails the editor, not the game. No C++ object may live in this frame.
        bool Guarded(void (*work)(UObject*), UObject* context)
        {
            __try
            {
                work(context);
                return true;
            }
            __except (GetExceptionCode() == EXCEPTION_ACCESS_VIOLATION ? EXCEPTION_EXECUTE_HANDLER : EXCEPTION_CONTINUE_SEARCH)
            {
                return false;
            }
        }

        /// After an access violation: close without touching game memory. The pause is given back at the next tick (G.PausedWorld
        /// stays set for it), the cursor right away.
        void Fail()
        {
            LEASI_ERROR("editor: access violation on the game thread, editor closed");
            G.Open = false;
            G.FreeCam = false;
            G.Camera = nullptr;
            Select(nullptr);
            Open.store(false);
            Looking.store(false);
            HoldCursor(false);
            std::scoped_lock lock{ SharedLock };
            Commands.clear();
        }
    }

    bool Busy() { return InEditor; }

    void AfterProcessEvent(UObject* context, UFunction* function, void* parms)
    {
        LEASI_UNUSED(parms);
        static SFXName const PostRender{ L"PostRender", 0 };
        static SFXName const UpdateCamera{ L"UpdateCamera", 0 };

        void (*work)(UObject*) = nullptr;
        if (function->Name == PostRender && context->IsA(ABioHUD::StaticClass())) work = TickEntry;
        else if (function->Name == UpdateCamera && G.FreeCam && context->IsA(ACamera::StaticClass())) work = FlyEntry;
        if (work == nullptr) return;

        InEditor = true;
        auto const ok = Guarded(work, context);
        InEditor = false;
        if (!ok) Fail();
    }

    Snapshot Latest()
    {
        std::scoped_lock lock{ SharedLock };
        return Shared;
    }

    void Post(Command const& command)
    {
        std::scoped_lock lock{ SharedLock };
        // Drags send one move per frame: only the latest matters.
        if (command.Type == Command::Kind::Move && !Commands.empty() && Commands.back().Type == Command::Kind::Move) Commands.back() = command;
        else Commands.push_back(command);
    }

    void SetCalibration(Calibration const& calibration)
    {
        std::scoped_lock lock{ SharedLock };
        LastCalibration = calibration;
    }

    json State()
    {
        Snapshot s;
        Calibration c;
        {
            std::scoped_lock lock{ SharedLock };
            s = Shared;
            c = LastCalibration;
        }
        auto vector = [](FVector v) { return json::array({ v.X, v.Y, v.Z }); };
        json state{
            { "open", Open.load() },
            { "freeCam", s.FreeCam },
            { "frame", s.Frame },
            { "speed", s.Speed },
            { "camera", { { "location", vector(s.Pov.Location) },
                          { "rotation", json::array({ s.Pov.Rotation.Pitch, s.Pov.Rotation.Yaw, s.Pov.Rotation.Roll }) },
                          { "fov", s.Pov.FOV } } },
            { "canvas", json::array({ s.CanvasWidth, s.CanvasHeight }) },
            { "backbuffer", json::array({ Diag.BackbufferWidth.load(), Diag.BackbufferHeight.load() }) },
            { "lastPick", s.LastPick },
            { "selection", nullptr },
            { "calibration", nullptr },
            { "threads", { { "present", Diag.PresentThread.load() }, { "postRender", Diag.PostRenderThread.load() },
                           { "updateCamera", Diag.CameraThread.load() }, { "window", Diag.WindowThread.load() } } },
            { "hooks", { { "present", Diag.PresentHooked.load() }, { "present1", Diag.Present1Hooked.load() },
                         { "resizeBuffers", Diag.ResizeHooked.load() }, { "directInput", Diag.DirectInputHooked.load() },
                         { "cursor", Diag.CursorHooked.load() }, { "window", Diag.WindowHooked.load() },
                         { "overlayReady", Diag.OverlayReady.load() }, { "overlayFailed", Diag.OverlayFailed.load() } } },
            { "counters", { { "presents", Diag.Presents.load() }, { "overlayFrames", Diag.OverlayFrames.load() },
                            { "swallowedMessages", Diag.SwallowedMessages.load() },
                            { "zeroedDeviceStates", Diag.ZeroedDeviceStates.load() },
                            { "zeroedDeviceData", Diag.ZeroedDeviceData.load() },
                            { "blockedCursorCalls", Diag.BlockedCursorCalls.load() } } },
        };
        if (s.Selected)
        {
            state["selection"] = { { "path", s.SelectedPath }, { "class", s.SelectedClass }, { "location", vector(s.SelectedLocation) },
                                   { "static", s.SelectedStatic } };
        }
        if (c.Valid)
        {
            state["calibration"] = { { "ours", json::array({ c.OursX, c.OursY }) }, { "canvasProject", json::array({ c.CanvasX, c.CanvasY }) },
                                     { "errorPixels", c.ErrorPixels } };
        }
        return state;
    }
}
