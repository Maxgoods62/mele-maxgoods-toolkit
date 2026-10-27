#include "MeleBridge/Editor.hpp"
#include "MeleBridge/Reflection.hpp"
#include "Common/Objects.hpp"

#include <algorithm>
#include <chrono>
#include <cmath>
#include <cwctype>
#include <filesystem>
#include <format>
#include <fstream>
#include <map>
#include <set>
#include <unordered_map>

// The editor's game-thread half: open and close (F8, pause), the free camera, picking, moving, turning, spawning and deleting
// actors, saving those edits to a session file, and the snapshot the overlay draws from. Everything here runs inside the
// ProcessEvent hook, right after BioHUD.PostRender or Camera.UpdateCamera returned.
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
        /// How far ahead a spawn looks for a surface, and how far down for a floor.
        constexpr float SpawnReach = 3000, SpawnDrop = 3000;
        /// Packages of engine and editor helper meshes (gizmos, node icons, UI): not props.
        constexpr wchar_t const* NotProps[] = { L"EngineMeshes", L"EditorMeshes", L"NodeBuddies", L"GUI_", L"EngineResources", L"EditorResources" };

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
            UFunction* Spawn = nullptr;
            UFunction* Destroy = nullptr;
            UFunction* SetStaticMesh = nullptr;
            bool Tried = false;
        } Fn;

        /// An actor the editor spawned, moved, turned or deleted, with its latest state: what Save writes.
        struct Edit
        {
            AActor* Actor = nullptr;
            int Index = -1;
            UClass* Class = nullptr;
            /// The persistent level when it was edited: its session file.
            std::string Map;
            std::string Tag;
            std::string Path;
            /// The package (file) the actor is in.
            std::string Level;
            std::string ClassName;
            bool Spawned = false;
            std::string MeshPath;
            std::string MeshFile;
            FVector Location{};
            FRotator Rotation{};
            bool Deleted = false;
            bool Saved = false;
            /// Written to the session file at least once: editor_import may have made a props entry of it, so its delete is kept.
            bool EverSaved = false;
        };

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
            int WorldIndex = -1;

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

            std::vector<Edit> Edits;
            std::shared_ptr<std::vector<std::string> const> Meshes;
            /// The level file of an actor showing each mesh: where the import finds it.
            std::unordered_map<UObject*, std::string> MeshFiles;
            std::string Status;
            unsigned long long StatusFrame = 0;
            /// How often the game took the pause back while the editor was open.
            int PauseRetaken = 0;
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

        void Say(std::string text)
        {
            LEASI_INFO("editor: {}", text);
            G.Status = std::move(text);
            G.StatusFrame = G.Frame;
        }

        std::string NameText(SFXName const& name)
        {
            auto const text = name.Instanced();
            std::wstring wide(text.Chars(), text.Length());
            while (!wide.empty() && wide.back() == L'\0') wide.pop_back();
            return ToUtf8(wide);
        }

        /// The package (file) an object is in: the first part of its path.
        std::string LevelOf(UObject* object)
        {
            auto const path = PathOf(object);
            return ToUtf8(path.substr(0, path.find(L'.')));
        }

        bool Real(UObject* object) { return object != nullptr && !Common::IsDefaultObject(object) && !Common::IsDestroyed(object); }

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
                Fn.Spawn = FindFunctionByPath(L"Engine.Actor.Spawn");
                Fn.Destroy = FindFunctionByPath(L"Engine.Actor.Destroy");
                Fn.SetStaticMesh = FindFunctionByPath(L"Engine.DynamicSMActor.SetStaticMesh");
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
                    LEASI_INFO("editor: paused {} (bPlayersOnly was {}{})", ToUtf8(PathOf(world)), G.WasPlayersOnly,
                               G.WasPlayersOnly ? ": the game's own pause, given back on close unless the game ends it meanwhile" : "");
                }
            }
            else
            {
                SetFreeCam(pc, false);
                // After a map change the old WorldInfo is gone: RestorePause only writes to the one we paused if it is still there.
                if (G.PausedWorld != nullptr) LEASI_INFO("editor: bPlayersOnly back to {}; the game took the pause back {} times", G.WasPlayersOnly, G.PauseRetaken);
                RestorePause();
                G.PauseRetaken = 0;
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

        // ! Edits
        // ========================================

        /// The edit for an actor, made on first touch; its state is the actor's now.
        Edit& Touch(AActor* actor)
        {
            auto const it = std::find_if(G.Edits.begin(), G.Edits.end(), [&](Edit const& e) {
                return !e.Deleted && e.Actor == actor && e.Index == actor->ObjectInternalInteger && e.Class == actor->Class;
            });
            // Decided before emplace_back, which moves end(): comparing `it` after it would say the new edit was found.
            auto const isNew = it == G.Edits.end();
            auto& edit = isNew ? G.Edits.emplace_back() : *it;
            if (isNew)
            {
                edit.Actor = actor;
                edit.Index = actor->ObjectInternalInteger;
                edit.Class = actor->Class;
                edit.Map = ToUtf8(MapName());
                edit.Tag = NameText(actor->Tag);
                edit.Path = ToUtf8(PathOf(actor));
                edit.Level = LevelOf(actor);
                edit.ClassName = ToUtf8(NameOf(actor->Class));
            }
            edit.Location = actor->Location;
            edit.Rotation = actor->Rotation;
            edit.Saved = false;
            return edit;
        }

        Edit* SpawnedEdit(AActor* actor)
        {
            auto const it = std::find_if(G.Edits.begin(), G.Edits.end(), [&](Edit const& e) {
                return e.Spawned && !e.Deleted && e.Actor == actor && e.Index == actor->ObjectInternalInteger && e.Class == actor->Class;
            });
            return it != G.Edits.end() ? &*it : nullptr;
        }

        void UpdateComponents(AActor* actor)
        {
            AActor_execForceUpdateComponents_Parms update;
            update.bCollisionUpdate = 1;
            update.bTransformOnly = 0;
            CallProcessEvent(actor, Fn.ForceUpdateComponents, &update);
        }

        void Move(float x, float y, float z)
        {
            if (!SelectionAlive() || Fn.ForceUpdateComponents == nullptr) return;
            // As live_place does: Location raw, then the components follow (SetLocation refuses bStatic actors, and blocking ones
            // where they would overlap).
            G.Selected->Location = { x, y, z };
            UpdateComponents(G.Selected);
            Touch(G.Selected);
        }

        void Rotate(float yawDegrees)
        {
            if (!SelectionAlive() || Fn.ForceUpdateComponents == nullptr) return;
            auto rotation = G.Selected->Rotation;
            rotation.Yaw = static_cast<int>(std::lround(yawDegrees * DegreesToUnits));
            G.Selected->Rotation = rotation;
            UpdateComponents(G.Selected);
            Touch(G.Selected);
        }

        /// Why Delete refuses an actor, or empty. Level actors other than props are for Phase 4 (docs/07 §4).
        std::string Undeletable(AActor* actor)
        {
            if (actor->IsA(APawn::StaticClass()) || actor->IsA(AController::StaticClass())) return "pawns and controllers are not deleted";
            if (actor->bStatic) return "a static actor (baked into the level's lighting): hiding vanilla actors comes in Phase 4";
            if (actor->bNoDelete) return "the engine won't destroy it (bNoDelete)";
            return {};
        }

        void Delete()
        {
            if (!SelectionAlive() || Fn.Destroy == nullptr) return;
            auto const actor = G.Selected;
            if (auto const why = Undeletable(actor); !why.empty())
            {
                Say(std::format("not deleted: {}", why));
                return;
            }
            auto const path = ToUtf8(PathOf(actor));
            auto& edit = Touch(actor);
            AActor_execDestroy_Parms destroy;
            CallProcessEvent(actor, Fn.Destroy, &destroy);
            if (!destroy.ReturnValue)
            {
                Say(std::format("the engine refused to destroy {}", path));
                return;
            }
            edit.Deleted = true;
            edit.Saved = false;
            Select(nullptr);
            Say(std::format("deleted {}", path));
        }

        // ! Spawning
        // ========================================

        /// The StaticMeshes in memory, and for each the level file of an actor that shows it.
        void RefreshMeshes()
        {
            G.MeshFiles.clear();
            auto const map = ToUtf8(MapName());
            auto& objects = *UObject::GObjObjects;
            auto const meshClass = UStaticMesh::StaticClass();
            auto const componentClass = UStaticMeshComponent::StaticClass();
            std::vector<std::string> meshes;
            for (UINT i = 0; i < objects.Count(); i++)
            {
                auto const object = objects.GetData()[i];
                if (!Real(object)) continue;
                if (object->IsA(componentClass))
                {
                    auto const component = static_cast<UStaticMeshComponent*>(object);
                    if (component->StaticMesh != nullptr && Real(component->Owner))
                    {
                        // Another level before the persistent map: the mod builds that one, which has a mesh only from an earlier
                        // build or an editor spawn.
                        auto const level = LevelOf(component->Owner);
                        auto const it = G.MeshFiles.find(component->StaticMesh);
                        if (it == G.MeshFiles.end()) G.MeshFiles.emplace(component->StaticMesh, level);
                        else if (_stricmp(it->second.c_str(), map.c_str()) == 0) it->second = level;
                    }
                }
                else if (object->IsA(meshClass))
                {
                    auto const path = PathOf(object);
                    if (std::any_of(std::begin(NotProps), std::end(NotProps), [&](wchar_t const* p) { return path.starts_with(p); })) continue;
                    meshes.push_back(ToUtf8(path));
                }
            }
            std::sort(meshes.begin(), meshes.end(), [](std::string const& a, std::string const& b) { return _stricmp(a.c_str(), b.c_str()) < 0; });
            LEASI_INFO("editor: {} meshes in memory, {} shown by an actor", meshes.size(), G.MeshFiles.size());
            G.Meshes = std::make_shared<std::vector<std::string> const>(std::move(meshes));
        }

        std::string Lower(std::string text)
        {
            std::transform(text.begin(), text.end(), text.begin(), [](unsigned char c) { return static_cast<char>(std::tolower(c)); });
            return text;
        }

        /// Base 36 of the seconds since 2026 when this game run first spawned: tags from other runs can't collide, even with props
        /// already imported into a YAML whose actors aren't loaded.
        std::string RunStamp()
        {
            static std::string const stamp = [] {
                auto const seconds = std::chrono::duration_cast<std::chrono::seconds>(std::chrono::system_clock::now().time_since_epoch()).count() - 1767225600;
                auto value = static_cast<unsigned long long>(std::max<long long>(seconds, 0));
                std::string text;
                do
                {
                    text.insert(text.begin(), "0123456789abcdefghijklmnopqrstuvwxyz"[value % 36]);
                    value /= 36;
                } while (value != 0);
                return text;
            }();
            return stamp;
        }

        std::filesystem::path SessionFolder();

        /// The tags in a map's session file, if it can be read.
        void SessionTags(std::string const& map, std::set<std::string>& tags)
        {
            try
            {
                auto const file = SessionFolder() / (ToWide(map) + L".json");
                if (!std::filesystem::exists(file)) return;
                std::ifstream in(file, std::ios::binary);
                for (auto const& a : json::parse(in).at("actors")) tags.insert(Lower(a.value("tag", std::string{})));
            }
            catch (std::exception const&)
            {
                // Unreadable: Save moves it aside. The run stamp still keeps tags apart.
            }
        }

        /// ed_<mesh>_<run>_<n>, unused by any actor, edit or session entry (FNames ignore case, so the check does too): the tag is
        /// how a session entry finds its YAML entry again.
        std::string UniqueTag(std::wstring const& meshName)
        {
            std::string mesh;
            for (auto const c : meshName) mesh += c < 128 && (std::iswalnum(c) || c == L'_') ? static_cast<char>(c) : '_';
            if (mesh.size() > 32) mesh.resize(32);
            auto const base = std::format("ed_{}_{}", mesh, RunStamp());

            std::set<std::string> taken;
            for (auto const& e : G.Edits) taken.insert(Lower(e.Tag));
            SessionTags(ToUtf8(MapName()), taken);
            auto& objects = *UObject::GObjObjects;
            auto const actorClass = AActor::StaticClass();
            for (UINT i = 0; i < objects.Count(); i++)
            {
                auto const object = objects.GetData()[i];
                if (Real(object) && object->IsA(actorClass)) taken.insert(Lower(NameText(static_cast<AActor*>(object)->Tag)));
            }
            for (int n = 1;; n++)
            {
                auto candidate = std::format("{}_{}", base, n);
                if (!taken.contains(Lower(candidate))) return candidate;
            }
        }

        /// Where the view points: the surface ahead if it is a floor, otherwise the floor below a point just short of it.
        FVector SpawnPoint(APlayerController* pc, UCanvas* canvas)
        {
            UCanvas_execDeProject_Parms ray;
            ray.ScreenPos = { static_cast<float>(canvas->SizeX) * 0.5f, static_cast<float>(canvas->SizeY) * 0.5f };
            CallProcessEvent(canvas, Fn.DeProject, &ray);

            AActor_execTrace_Parms ahead;
            ahead.TraceStart = ray.WorldOrigin;
            ahead.TraceEnd = Add(ray.WorldOrigin, Scale(ray.WorldDirection, SpawnReach));
            ahead.bTraceActors = 1;
            ahead.ExtraTraceFlags = TRACEFLAG_Bullet;
            CallProcessEvent(pc, Fn.Trace, &ahead);
            if (ahead.ReturnValue != nullptr && ahead.HitNormal.Z > 0.7f) return ahead.HitLocation;

            auto const start = ahead.ReturnValue != nullptr ? Add(ahead.HitLocation, Scale(ray.WorldDirection, -60))
                                                            : Add(ray.WorldOrigin, Scale(ray.WorldDirection, 400));
            AActor_execTrace_Parms down;
            down.TraceStart = start;
            down.TraceEnd = Add(start, { 0, 0, -SpawnDrop });
            down.bTraceActors = 1;
            down.ExtraTraceFlags = TRACEFLAG_Bullet;
            CallProcessEvent(pc, Fn.Trace, &down);
            return down.ReturnValue != nullptr ? down.HitLocation : start;
        }

        /// A DynamicSMActor_Spawnable showing a mesh in memory: it has its own light environment, so it is lit where it stands.
        void Spawn(APlayerController* pc, UCanvas* canvas, std::string const& meshPath)
        {
            if (Fn.Spawn == nullptr || Fn.SetStaticMesh == nullptr || Fn.DeProject == nullptr || Fn.Trace == nullptr)
            {
                Say("spawning is off: Actor.Spawn, DynamicSMActor.SetStaticMesh, Canvas.DeProject or Actor.Trace was not found");
                return;
            }
            UObject* mesh = nullptr;
            try
            {
                mesh = FindByPath(ToWide(meshPath));
            }
            catch (std::exception const& e)
            {
                Say(std::format("not spawned: {}", e.what()));
                return;
            }
            if (!mesh->IsA(UStaticMesh::StaticClass()))
            {
                Say(std::format("not spawned: {} is not a StaticMesh", meshPath));
                return;
            }

            // Facing the camera.
            auto const cameraYaw = pc->PlayerCamera != nullptr ? pc->PlayerCamera->CameraCache.POV.Rotation.Yaw : pc->Rotation.Yaw;
            AActor_execSpawn_Parms spawn;
            spawn.SpawnClass = ADynamicSMActor_Spawnable::StaticClass();
            auto const tag = UniqueTag(NameOf(mesh));
            spawn.SpawnTag = SFXName(ToWide(tag).c_str(), 0);
            spawn.SpawnLocation = SpawnPoint(pc, canvas);
            spawn.SpawnRotation = { 0, (cameraYaw + 32768) & 0xFFFF, 0 };
            spawn.bNoCollisionFail = 1;
            CallProcessEvent(pc, Fn.Spawn, &spawn);
            auto const actor = spawn.ReturnValue;
            if (actor == nullptr)
            {
                Say(std::format("the engine refused to spawn {}", meshPath));
                return;
            }
            ADynamicSMActor_execSetStaticMesh_Parms set;
            set.NewMesh = static_cast<UStaticMesh*>(mesh);
            CallProcessEvent(actor, Fn.SetStaticMesh, &set);

            auto& edit = Touch(actor);
            edit.Spawned = true;
            edit.MeshPath = meshPath;
            auto const file = G.MeshFiles.find(mesh);
            edit.MeshFile = file != G.MeshFiles.end() ? file->second : std::string{};
            Select(actor);
            Say(std::format("spawned {} as {}", meshPath, tag));
        }

        // ! Save
        // ========================================

        std::filesystem::path SessionFolder()
        {
            wchar_t buffer[MAX_PATH];
            auto const length = GetEnvironmentVariableW(L"LOCALAPPDATA", buffer, MAX_PATH);
            if (length == 0 || length >= MAX_PATH) throw BridgeError("LOCALAPPDATA is not set");
            return std::filesystem::path(buffer) / L"MeleBridge" / L"LE1" / L"sessions";
        }

        /// Spawned actors by tag (their path changes with each spawn), level actors by path (their tag is often their class name).
        std::string KeyOf(json const& actor)
        {
            return actor.value("spawned", false) ? "tag:" + actor.value("tag", std::string{}) : "path:" + actor.value("path", std::string{});
        }

        json EntryOf(Edit const& e)
        {
            json mesh = nullptr;
            if (e.Spawned) mesh = { { "path", e.MeshPath }, { "file", e.MeshFile.empty() ? json(nullptr) : json(e.MeshFile) } };
            return {
                { "tag", e.Tag },
                { "path", e.Path },
                { "level", e.Level },
                { "class", e.ClassName },
                { "spawned", e.Spawned },
                { "mesh", mesh },
                { "location", json::array({ e.Location.X, e.Location.Y, e.Location.Z }) },
                { "rotation", json::array({ e.Rotation.Pitch, e.Rotation.Yaw, e.Rotation.Roll }) },
                { "deleted", e.Deleted },
            };
        }

        /// Merges one map's edits into its session file (%LOCALAPPDATA%\\MeleBridge\\LE1\\sessions\\<map>.json): the latest state
        /// of each actor, which mele-mcp's editor_import writes into the level YAML. Returns the file, and the path an unreadable
        /// earlier file was moved to (then every edit of the map is written, saved ones too, since the earlier file had them).
        std::pair<std::filesystem::path, std::filesystem::path> WriteSession(std::string const& map, std::vector<Edit*> const& unsaved,
                                                                            std::vector<Edit*> const& all)
        {
            if (map.empty()) throw BridgeError("the map name is empty");
            auto const folder = SessionFolder();
            std::filesystem::create_directories(folder);
            auto const file = folder / (ToWide(map) + L".json");

            std::map<std::string, json> actors;
            std::vector<std::string> order;
            std::filesystem::path bad;
            if (std::filesystem::exists(file))
            {
                try
                {
                    std::ifstream in(file, std::ios::binary);
                    auto const doc = json::parse(in);
                    for (auto const& a : doc.at("actors"))
                    {
                        auto const key = KeyOf(a);
                        if (!actors.contains(key)) order.push_back(key);
                        actors[key] = a;
                    }
                }
                catch (std::exception const& e)
                {
                    // Kept for a look, not overwritten.
                    actors.clear();
                    order.clear();
                    bad = file;
                    bad += std::format(L".bad-{}", GetTickCount64());
                    std::filesystem::rename(file, bad);
                    LEASI_WARN("editor: {} could not be read ({}), moved to {}", ToUtf8(file.wstring()), e.what(), ToUtf8(bad.wstring()));
                }
            }

            for (auto const e : bad.empty() ? unsaved : all)
            {
                auto const entry = EntryOf(*e);
                auto const key = KeyOf(entry);
                // A spawned actor deleted before any save leaves nothing. Once saved, it may have been imported (which drops its
                // entry from the file): it is written as deleted: true, for editor_import to mark its props entry removed.
                if (e->Spawned && e->Deleted && !e->EverSaved) continue;
                // Built and installed, a spawned actor is a level actor with the same tag: its spawned entry is history.
                if (!e->Spawned && !e->Tag.empty() && e->Tag != "None") actors.erase("tag:" + e->Tag);
                if (!actors.contains(key)) order.push_back(key);
                actors[key] = entry;
            }

            auto list = json::array();
            for (auto const& key : order)
            {
                if (auto const it = actors.find(key); it != actors.end()) list.push_back(it->second);
            }
            auto const now = std::chrono::floor<std::chrono::seconds>(std::chrono::system_clock::now());
            json const doc{ { "version", 1 }, { "map", map }, { "saved", std::format("{:%FT%TZ}", now) }, { "actors", list } };

            // Written beside it, then moved over it: a crash mid-write leaves the old file whole.
            auto temporary = file;
            temporary += L".tmp";
            {
                std::ofstream out(temporary, std::ios::binary | std::ios::trunc);
                out << doc.dump(2, ' ', false, json::error_handler_t::replace);
                out.close();
                if (out.fail()) throw BridgeError("could not write " + ToUtf8(temporary.wstring()));
            }
            if (!MoveFileExW(temporary.c_str(), file.c_str(), MOVEFILE_REPLACE_EXISTING | MOVEFILE_WRITE_THROUGH))
                throw BridgeError(std::format("could not replace {} (error {})", ToUtf8(file.wstring()), GetLastError()));
            return { file, bad };
        }

        void Save()
        {
            std::map<std::string, std::pair<std::vector<Edit*>, std::vector<Edit*>>> byMap;
            for (auto& e : G.Edits)
            {
                // Where it stands now: an arrow drag ends between saves.
                if (!e.Deleted && Alive(e.Actor, e.Index) && e.Actor->Class == e.Class)
                {
                    e.Location = e.Actor->Location;
                    e.Rotation = e.Actor->Rotation;
                }
                auto& [unsaved, all] = byMap[e.Map];
                all.push_back(&e);
                if (!e.Saved) unsaved.push_back(&e);
            }
            std::erase_if(byMap, [](auto const& m) { return m.second.first.empty(); });
            if (byMap.empty())
            {
                Say("nothing to save: no actor was spawned, moved, turned or deleted since the last save");
                return;
            }

            size_t count = 0;
            std::vector<std::string> written, problems;
            for (auto const& [map, edits] : byMap)
            {
                try
                {
                    auto const [file, bad] = WriteSession(map, edits.first, edits.second);
                    for (auto const e : edits.first) e->Saved = e->EverSaved = true;
                    count += edits.first.size();
                    written.push_back(ToUtf8(file.wstring()));
                    if (!bad.empty()) problems.push_back(std::format("the earlier file was unreadable, kept as {}", ToUtf8(bad.wstring())));
                }
                catch (std::exception const& e)
                {
                    problems.push_back(std::format("{} not saved: {}", map, e.what()));
                }
            }
            std::string text = count > 0 ? std::format("saved {} edit{} to {}", count, count == 1 ? "" : "s", written.front()) : "not saved";
            for (size_t i = 1; i < written.size(); i++) text += " and " + written[i];
            for (auto const& p : problems) text += "; " + p;
            if (count > 0 && problems.empty()) text += ". Next: editor_import in mele-mcp (level YAML, then build)";
            Say(text);
        }

        void Run(APlayerController* pc, UCanvas* canvas, Command const& c)
        {
            switch (c.Type)
            {
            case Command::Kind::Close: SetOpen(pc, false); break;
            case Command::Kind::FreeCam: SetFreeCam(pc, c.On); break;
            case Command::Kind::Pick: Pick(pc, canvas, c.X, c.Y); break;
            case Command::Kind::Move: Move(c.X, c.Y, c.Z); break;
            case Command::Kind::Rotate: Rotate(c.X); break;
            case Command::Kind::Deselect: Select(nullptr); break;
            case Command::Kind::Speed: G.Speed = std::clamp(G.Speed * std::pow(1.25f, c.X), MinSpeed, MaxSpeed); break;
            case Command::Kind::Spawn: Spawn(pc, canvas, c.Text); break;
            case Command::Kind::Delete: Delete(); break;
            case Command::Kind::Save: Save(); break;
            case Command::Kind::RefreshMeshes: RefreshMeshes(); break;
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
                s.SelectedRotation = actor->Rotation;
                s.SelectedStatic = actor->bStatic;
                s.SelectedPath = ToUtf8(PathOf(actor));
                s.SelectedClass = ToUtf8(NameOf(actor->Class));
                s.SelectedTag = NameText(actor->Tag);
                s.SelectedSpawned = SpawnedEdit(actor) != nullptr;
                s.SelectedUndeletable = Undeletable(actor);
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
            s.Meshes = G.Meshes;
            s.Edits = static_cast<int>(G.Edits.size());
            s.Unsaved = static_cast<int>(std::count_if(G.Edits.begin(), G.Edits.end(), [](Edit const& e) { return !e.Saved; }));
            s.Status = G.Status;
            s.StatusFrame = G.StatusFrame;
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
            if (pc->WorldInfo != G.World || (G.World != nullptr && pc->WorldInfo->ObjectInternalInteger != G.WorldIndex))
            {
                if (G.World != nullptr)
                {
                    if (G.Open) LEASI_INFO("editor: the world changed, editor closed");
                    SetOpen(pc, false);
                    Select(nullptr);
                    G.LastPick.clear();
                    // The meshes in memory changed; unsaved edits keep their map and are saved to its file.
                    G.Meshes.reset();
                    G.MeshFiles.clear();
                    // The old world's actors are gone: an edit matching a new actor in a reused slot would take its moves.
                    for (auto& e : G.Edits)
                    {
                        e.Actor = nullptr;
                        e.Index = -1;
                        e.Class = nullptr;
                    }
                }
                G.World = pc->WorldInfo;
                G.WorldIndex = pc->WorldInfo->ObjectInternalInteger;
            }

            // The game pauses this way too (seen while it was unfocused) and unpauses when it is done: the editor keeps its pause.
            if (G.Open && G.PausedWorld != nullptr && G.PausedWorld == pc->WorldInfo && !G.PausedWorld->bPlayersOnly)
            {
                // The game wrote 0: whatever pause it had when the editor opened is over, so closing gives back 0.
                if (G.WasPlayersOnly) LEASI_INFO("editor: the game ended its own pause while the editor was open; closing will give back 0");
                G.WasPlayersOnly = false;
                G.PausedWorld->bPlayersOnly = 1;
                if (G.PauseRetaken++ == 0) LEASI_INFO("editor: the game cleared bPlayersOnly while the editor was open; set again");
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
                // Ctrl+S saves: it doesn't also fly backwards.
                auto const along = (Down('W') ? 1.f : 0.f) - (Down('S') && !Down(VK_CONTROL) ? 1.f : 0.f);
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
        // Drags send one move or turn per frame: only the latest matters.
        auto const drag = command.Type == Command::Kind::Move || command.Type == Command::Kind::Rotate;
        if (drag && !Commands.empty() && Commands.back().Type == command.Type) Commands.back() = command;
        else if (command.Type == Command::Kind::Spawn)
        {
            // A double click on a list row is two clicks: one crate, not two.
            static std::string lastPath;
            static ULONGLONG lastTime = 0;
            auto const now = GetTickCount64();
            if (command.Text == lastPath && now - lastTime < GetDoubleClickTime()) return;
            lastPath = command.Text;
            lastTime = now;
            Commands.push_back(command);
        }
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
            { "status", s.Status },
            { "edits", { { "count", s.Edits }, { "unsaved", s.Unsaved } } },
            { "meshesInMemory", s.Meshes != nullptr ? json(s.Meshes->size()) : json(nullptr) },
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
            state["selection"] = { { "path", s.SelectedPath }, { "class", s.SelectedClass }, { "tag", s.SelectedTag },
                                   { "location", vector(s.SelectedLocation) },
                                   { "rotation", json::array({ s.SelectedRotation.Pitch, s.SelectedRotation.Yaw, s.SelectedRotation.Roll }) },
                                   { "static", s.SelectedStatic }, { "spawned", s.SelectedSpawned },
                                   { "undeletable", s.SelectedUndeletable.empty() ? json(nullptr) : json(s.SelectedUndeletable) } };
        }
        if (c.Valid)
        {
            state["calibration"] = { { "ours", json::array({ c.OursX, c.OursY }) }, { "canvasProject", json::array({ c.CanvasX, c.CanvasY }) },
                                     { "errorPixels", c.ErrorPixels } };
        }
        return state;
    }
}
