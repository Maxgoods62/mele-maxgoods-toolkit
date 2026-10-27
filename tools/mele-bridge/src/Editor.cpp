#include "MeleBridge/Editor.hpp"
#include "MeleBridge/Reflection.hpp"
#include "Common/Objects.hpp"

#include <algorithm>
#include <chrono>
#include <cmath>
#include <cstring>
#include <cwctype>
#include <deque>
#include <filesystem>
#include <format>
#include <fstream>
#include <map>
#include <optional>
#include <set>
#include <unordered_map>
#include <unordered_set>
#include <utility>

// The editor's game-thread half: open and close (F8, pause), the free camera, picking, moving, turning, scaling, spawning (a
// mesh in memory, or one loaded from the catalog package) and deleting (hiding) actors, undo and redo, saving those edits to a
// session file and re-applying that file when a map loads, and the snapshot the overlay draws from. Everything here runs
// inside the ProcessEvent hook, right after BioHUD.PostRender or Camera.UpdateCamera returned.
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
        constexpr double Pi = 3.14159265358979323846;
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
        /// The package catalog_build ports the catalog's meshes into ("package" in catalog.json). A mesh in memory under it is the
        /// catalog's copy: only its catalog entry knows the vanilla mesh and file a session names.
        constexpr char const CatalogPrefix[] = "MeleCatalog_Core.";
        /// The session format Save writes; the bridge reads version 1 (Phase 1's) too.
        constexpr int SessionVersion = 2;
        /// A drag command that moves less than this changes nothing: a click on a handle without motion still moves it by the
        /// float noise of ImGuizmo's ray against its plane.
        constexpr float LocationNoise = 0.01f;
        constexpr float ScaleNoise = 1e-4f;
        /// Undo keeps this many steps; the oldest go first.
        constexpr size_t MaxSteps = 200;
        /// A re-apply pass waits for the loaded levels to stay the same this long: streaming brings them in a few at a time.
        constexpr ULONGLONG SettleMs = 500;
        /// It also waits for a level being added to the world or taken from it, but not longer than this: a level that stays half
        /// added would keep the session from ever being re-applied.
        constexpr ULONGLONG UnsettledMs = 5000;

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
            UFunction* DynamicLoadObject = nullptr;
            UFunction* SetHidden = nullptr;
            UFunction* SetCollision = nullptr;
            bool Tried = false;
        } Fn;

        /// An actor's transform as an edit, a session entry and an undo step hold it. Rotation in Unreal units (65536 = 360°).
        struct Pose
        {
            FVector Location{};
            FRotator Rotation{};
            float DrawScale = 1;
            FVector DrawScale3D{ 1, 1, 1 };
        };

        /// The three flags a delete changes, as the actor has them while shown: undo puts them back.
        struct HideFlags
        {
            bool Hidden = false;
            bool CollideActors = true;
            bool BlockActors = true;
        };

        /// An actor the editor spawned, moved, turned, scaled or deleted, or one the session file named: everything its session
        /// entry holds. Every change writes these fields as well as the actor, so a save never needs the actor.
        struct Edit
        {
            AActor* Actor = nullptr;
            int Index = -1;
            UClass* Class = nullptr;
            /// The level the actor is in (its Outer): when that level leaves the loaded set, the edit goes back to pending.
            UObject* LevelObject = nullptr;
            int LevelIndex = -1;
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
            /// The catalog package's copy, which DynamicLoadObject reads when no level has the mesh; empty for a mesh spawned from
            /// the In this level list.
            std::string MeshLoad;
            Pose Now;
            bool Deleted = false;
            /// What the YAML holds, for editor_import's three-way merge: the actor before the editor first changed it. None for a
            /// spawn never imported.
            std::optional<Pose> Base;
            /// Whether the YAML removes it (removed: true: the build leaves it hidden without collision), as an import said: its
            /// base.deleted, or the deleted of an entry it marked. Save writes it back as base.deleted. A base the editor took from
            /// an actor: false when the actor was shown (the build that made it did not remove it). Hidden: true when a pass dropped
            /// the entry on it (the YAML built it so), else unknown (the game may have hidden it), and then left out: editor_import
            /// merges removed two-way without it.
            std::optional<bool> BaseHidden;
            /// The states the installed level may still show of it while an import's install is pending: imported.before of each
            /// import since the editor last made the entry anew (an import keeps one, Save carries them over as installed). Save
            /// leaves imported out (a saved edit is a new state), so the next import's before is the base, and one of these builds
            /// may be the installed one.
            json Installed = json::array();
            /// The session entry the edit was made from, while the edit is still that entry (saved, not changed since): a built
            /// copy that turns up later is judged against it as the entry would have been. Cleared once judged against an actor,
            /// or once saved.
            json Source;
            /// The pose last written to the session file or read from it. A field still at it was not changed in game since:
            /// Save takes the file's value for it, which an import may have moved on.
            std::optional<Pose> SavedPose;
            HideFlags ShownFlags;
            bool Saved = false;
            /// Written to the session file at least once: editor_import may have made a props entry of it, so its delete is kept.
            bool EverSaved = false;
            /// No actor until a pass finds one: its level left the loaded set, or its spawn could not be spawned again yet.
            bool Pending = false;
            std::string PendingWhy;
            /// A spawn a pass spawned again, or a deleted spawn without an actor: a built copy with its tag that appears later takes
            /// the edit over.
            bool Rebindable = false;
            /// On a built copy of the spawn: when the copy's level leaves, the edit waits for it instead of spawning again.
            bool OnCopy = false;
        };

        /// An edit's state as an undo step holds it: shown or not, the three flags it has when shown, the transform.
        struct StepState
        {
            bool Shown = true;
            HideFlags ShownFlags;
            Pose Transform;
        };

        enum class StepKind { Spawn, Delete, Move, Turn, Scale };

        /// One undo step: a spawn, a delete or a drag.
        struct Step
        {
            /// The edit's index: edits are erased only on a map change, which clears the steps too.
            size_t Target = 0;
            StepKind Kind = StepKind::Move;
            StepState Before;
            StepState After;
        };

        /// A session file's entry waiting for its actor; it becomes an edit once a pass applies it.
        struct Entry
        {
            json Data;
            std::string Key;
            std::string Why;
            /// Applied or dropped by a pass: taken out of the list when the pass ends, or by the next one if it faulted first.
            bool Done = false;
        };

        /// A loaded level: the persistent one (the WorldInfo's Outer), or a streaming level's LoadedLevel.
        struct LevelSeen
        {
            UObject* Level = nullptr;
            int Index = -1;
            bool Visible = true;
            /// Not being added to the world or taken from it: bShouldBeVisible and bIsVisible agree. Adding attaches the level's
            /// components over several frames, and only sets bIsVisible at the end.
            bool Settled = true;

            bool operator==(LevelSeen const&) const = default;
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
            /// We let the paused world tick once (TickOnce): the next tick takes the pause back without counting it as the game's.
            bool TickingOnce = false;
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
            std::deque<Step> Undo;
            std::deque<Step> Redo;
            /// The step of the drag under way, opened by its first Move, Rotate or Scale; closed by EndDrag and before any other
            /// command.
            std::optional<Step> Drag;

            std::shared_ptr<std::vector<std::string> const> Meshes;
            /// The level file of an actor showing each mesh, by the mesh's lower-case path: where the import finds it. Not by
            /// pointer: a mesh nothing shows any more is collected, and one loaded later (a catalog mesh) can take its address.
            std::unordered_map<std::string, std::string> MeshFiles;
            std::string Status;
            unsigned long long StatusFrame = 0;
            /// Why the last save left edits unsaved (a map's file could not be written), until a save writes every map: the status
            /// line fades, and the first pass after a load replaces it.
            std::string SaveProblem;
            /// How often the game took the pause back while the editor was open.
            int PauseRetaken = 0;

            /// The map whose session the passes re-apply, its file, and its entries still waiting for their actor.
            std::string SessionMap;
            std::string SessionFile;
            std::string SessionError;
            std::vector<Entry> Entries;
            int Applied = 0;
            int Dropped = 0;
            /// The loaded levels at the last tick, and as the last pass saw them.
            std::vector<LevelSeen> Levels;
            std::vector<std::string> PassLevels;
            unsigned long long PassFrame = 0;
            /// The first tick after a load runs a pass; after that, a change of the loaded levels does, once they settle.
            bool PassNow = false;
            bool PassWanted = false;
            ULONGLONG LevelsChangedAt = 0;
            /// The last pass faulted: not tried again until the loaded levels change.
            bool PassFaulted = false;
            /// The SessionState the snapshot shares, rebuilt when it is stale.
            std::shared_ptr<SessionState const> Summary;
            bool SummaryStale = true;
            /// catalog.json's load path by lower-case vanilla mesh, for spawning a session's props again; read once.
            std::unordered_map<std::string, std::string> CatalogLoads;
            bool CatalogRead = false;
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

        /// A refusal a drag would repeat every frame: said once while it is the status line.
        void SayOnce(std::string text)
        {
            if (text != G.Status) Say(std::move(text));
        }

        std::string NameText(SFXName const& name)
        {
            auto const text = name.Instanced();
            std::wstring wide(text.Chars(), text.Length());
            while (!wide.empty() && wide.back() == L'\0') wide.pop_back();
            return ToUtf8(wide);
        }

        std::string Lower(std::string text)
        {
            std::transform(text.begin(), text.end(), text.begin(), [](unsigned char c) { return static_cast<char>(std::tolower(c)); });
            return text;
        }

        bool SameText(std::string const& a, std::string const& b) { return _stricmp(a.c_str(), b.c_str()) == 0; }

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

        /// A tag that names one actor: not empty, not None and not the class name (an actor's tag is its class name by default).
        bool RealTag(std::string const& tag, std::string const& className)
        {
            return !tag.empty() && !SameText(tag, "None") && !SameText(tag, className);
        }

        // ! Comparing states
        // ========================================

        bool Near(FVector a, FVector b, float within)
        {
            return std::fabs(a.X - b.X) <= within && std::fabs(a.Y - b.Y) <= within && std::fabs(a.Z - b.Z) <= within;
        }

        /// FRotationMatrix's rows: the forward (X), right (Y) and up (Z) axes, in double.
        void AxesOf(FRotator const& rotation, double (&axes)[3][3])
        {
            constexpr double Radians = Pi / 32768.0;
            auto const p = rotation.Pitch * Radians, y = rotation.Yaw * Radians, r = rotation.Roll * Radians;
            auto const sp = std::sin(p), cp = std::cos(p), sy = std::sin(y), cy = std::cos(y), sr = std::sin(r), cr = std::cos(r);
            double const out[3][3] = { { cp * cy, cp * sy, sp },
                                       { sr * sp * cy - cr * sy, sr * sp * sy + cr * cy, -sr * cp },
                                       { -(cr * sp * cy + sr * sy), cy * sr - cr * sp * sy, cr * cp } };
            std::memcpy(axes, out, sizeof out);
        }

        /// The same orientation, as editor_import compares rotations: each axis within 0.02 degrees of the other's, so (180, 0, 0)
        /// and (0, 180, 180) agree, and so do 200 and -160 degrees of yaw.
        bool SameOrientation(FRotator const& a, FRotator const& b)
        {
            double x[3][3], y[3][3];
            AxesOf(a, x);
            AxesOf(b, y);
            auto const limit = std::cos(0.02 * Pi / 180.0);
            for (int i = 0; i < 3; i++)
            {
                if (x[i][0] * y[i][0] + x[i][1] * y[i][1] + x[i][2] * y[i][2] < limit) return false;
            }
            return true;
        }

        /// What an undo step tells apart: a drag that came back where it started changed nothing.
        bool SameStep(StepState const& a, StepState const& b)
        {
            return a.Shown == b.Shown && a.ShownFlags.Hidden == b.ShownFlags.Hidden && a.ShownFlags.CollideActors == b.ShownFlags.CollideActors
                && a.ShownFlags.BlockActors == b.ShownFlags.BlockActors && Near(a.Transform.Location, b.Transform.Location, LocationNoise)
                && SameOrientation(a.Transform.Rotation, b.Transform.Rotation) && std::fabs(a.Transform.DrawScale - b.Transform.DrawScale) <= ScaleNoise
                && Near(a.Transform.DrawScale3D, b.Transform.DrawScale3D, ScaleNoise);
        }

        /// Locations as editor_import compares them: within 0.01 once both are rounded to 2 decimals.
        bool SameLocation(FVector a, FVector b)
        {
            auto const same = [](float x, float y) { return std::fabs(std::round(x * 100.0) - std::round(y * 100.0)) <= 1.0; };
            return same(a.X, b.X) && same(a.Y, b.Y) && same(a.Z, b.Z);
        }

        /// Whether an actor already shows a state, as editor_import counts two states equal: the location to 2 decimals, the
        /// rotation as an orientation, the scales within 0.001. A deleted state is a hidden actor, wherever it stands.
        bool Has(AActor* actor, Pose const& pose, bool deleted)
        {
            if ((actor->bHidden != 0) != deleted) return false;
            if (deleted) return true;
            auto const same = [](float a, float b) { return std::fabs(a - b) <= 0.001f + 1e-6f; };
            return SameLocation(actor->Location, pose.Location) && SameOrientation(actor->Rotation, pose.Rotation) && same(actor->DrawScale, pose.DrawScale)
                && same(actor->DrawScale3D.X, pose.DrawScale3D.X) && same(actor->DrawScale3D.Y, pose.DrawScale3D.Y)
                && same(actor->DrawScale3D.Z, pose.DrawScale3D.Z);
        }

        // ! Session entries as JSON
        // ========================================

        std::string Text(json const& object, char const* key)
        {
            auto const it = object.find(key);
            return it != object.end() && it->is_string() ? it->get<std::string>() : std::string{};
        }

        bool Flag(json const& object, char const* key)
        {
            auto const it = object.find(key);
            return it != object.end() && it->is_boolean() && it->get<bool>();
        }

        /// Three numbers, if that is what the key holds.
        bool Numbers(json const& object, char const* key, double (&out)[3])
        {
            auto const it = object.find(key);
            if (it == object.end() || !it->is_array() || it->size() != 3) return false;
            for (size_t i = 0; i < 3; i++)
            {
                if (!(*it)[i].is_number()) return false;
                out[i] = (*it)[i].get<double>();
            }
            return true;
        }

        json VectorJson(FVector v) { return json::array({ v.X, v.Y, v.Z }); }
        json RotatorJson(FRotator r) { return json::array({ r.Pitch, r.Yaw, r.Roll }); }

        /// A pose as an entry (and its base) writes it: location, rotation, scale (DrawScale), scale3d (DrawScale3D).
        json PoseJson(Pose const& pose)
        {
            return { { "location", VectorJson(pose.Location) }, { "rotation", RotatorJson(pose.Rotation) }, { "scale", pose.DrawScale },
                     { "scale3d", VectorJson(pose.DrawScale3D) } };
        }

        /// A pose from an entry, its base or imported.before. A field it lacks (a version 1 entry has no scales) is the fallback's:
        /// the actor's own, so nothing the entry doesn't name is written.
        Pose PoseFrom(json const& object, Pose const& fallback)
        {
            auto pose = fallback;
            double v[3];
            if (Numbers(object, "location", v)) pose.Location = { static_cast<float>(v[0]), static_cast<float>(v[1]), static_cast<float>(v[2]) };
            if (Numbers(object, "rotation", v))
                pose.Rotation = { static_cast<int>(std::lround(v[0])), static_cast<int>(std::lround(v[1])), static_cast<int>(std::lround(v[2])) };
            if (auto const scale = object.find("scale"); scale != object.end() && scale->is_number()) pose.DrawScale = scale->get<float>();
            if (Numbers(object, "scale3d", v)) pose.DrawScale3D = { static_cast<float>(v[0]), static_cast<float>(v[1]), static_cast<float>(v[2]) };
            return pose;
        }

        /// The states the installed level may show of an entry's actor while an install is pending: its imported.before (written
        /// by editor_import), and the installed ones Save carried over from earlier imports (editor_import leaves them as they are).
        json Candidates(json const& entry)
        {
            auto list = json::array();
            auto const add = [&](json const& state) {
                if (state.is_object() && std::find(list.begin(), list.end(), state) == list.end()) list.push_back(state);
            };
            if (auto const imported = entry.find("imported"); imported != entry.end() && imported->is_object())
            {
                if (auto const before = imported->find("before"); before != imported->end()) add(*before);
            }
            if (auto const installed = entry.find("installed"); installed != entry.end() && installed->is_array())
            {
                for (auto const& state : *installed) add(state);
            }
            return list;
        }

        /// Spawned actors by tag (their path changes with each spawn), level actors by path (their tag is often their class name).
        std::string KeyOf(json const& actor)
        {
            return Flag(actor, "spawned") ? "tag:" + Text(actor, "tag") : "path:" + Text(actor, "path");
        }

        std::string KeyOf(Edit const& e) { return e.Spawned ? "tag:" + e.Tag : "path:" + e.Path; }

        /// How the status line and undo name an edit: its tag when it is a real one, else its object name.
        std::string Called(Edit const& e)
        {
            if (RealTag(e.Tag, e.ClassName)) return e.Tag;
            return e.Path.substr(e.Path.rfind('.') + 1);
        }

        // ! Levels
        // ========================================

        /// The loaded levels: the persistent one, then each streaming level's LoadedLevel with whether it is visible, and whether
        /// it is still being added to the world or taken from it.
        std::vector<LevelSeen> ScanLevels(AWorldInfo* world)
        {
            std::vector<LevelSeen> levels;
            auto const levelClass = ULevel::StaticClass();
            auto const persistent = world->Outer;
            if (Real(persistent) && persistent->IsA(levelClass)) levels.push_back({ persistent, persistent->ObjectInternalInteger, true, true });
            for (auto const streaming : world->StreamingLevels)
            {
                if (streaming == nullptr) continue;
                UObject* const level = streaming->LoadedLevel;
                if (!Real(level) || !level->IsA(levelClass)) continue;
                auto const visible = streaming->bIsVisible != 0;
                auto const settled = (streaming->bShouldBeVisible != 0) == visible;
                auto const known = std::find_if(levels.begin(), levels.end(), [&](LevelSeen const& l) { return l.Level == level; });
                if (known != levels.end())
                {
                    known->Visible = known->Visible || visible;
                    known->Settled = known->Settled && settled;
                }
                else levels.push_back({ level, level->ObjectInternalInteger, visible, settled });
            }
            return levels;
        }

        /// Whether an actor's components may be updated: not for an actor of a streaming level that is loaded but not visible.
        /// Adding that level to the world attaches its actors' components where the actors are.
        bool Visible(AActor* actor)
        {
            for (auto const& level : G.Levels)
            {
                if (level.Level == actor->Outer) return level.Visible;
            }
            // A level the world doesn't list: updated, as Phase 1 did.
            return true;
        }

        // ! Edits
        // ========================================

        Pose PoseOf(AActor* actor) { return { actor->Location, actor->Rotation, actor->DrawScale, actor->DrawScale3D }; }
        HideFlags FlagsOf(AActor* actor) { return { actor->bHidden != 0, actor->bCollideActors != 0, actor->bBlockActors != 0 }; }

        size_t IndexOf(Edit const& edit) { return static_cast<size_t>(&edit - G.Edits.data()); }

        /// Still on the actor it was bound to: not pending, the actor neither destroyed nor replaced.
        bool Bound(Edit const& e) { return !e.Pending && Alive(e.Actor, e.Index) && e.Actor->Class == e.Class; }

        void Detach(Edit& e)
        {
            e.Actor = nullptr;
            e.Index = -1;
            e.Class = nullptr;
            e.LevelObject = nullptr;
            e.LevelIndex = -1;
        }

        /// An edit takes an actor as it stands, shown: hidden by the game (Kismet) is not deleted by the editor, and the flags it
        /// has are the ones undo gives back. The Put that follows hides it if the edit is deleted.
        void Bind(Edit& e, AActor* actor)
        {
            e.Actor = actor;
            e.Index = actor->ObjectInternalInteger;
            e.Class = actor->Class;
            e.LevelObject = actor->Outer;
            e.LevelIndex = actor->Outer != nullptr ? actor->Outer->ObjectInternalInteger : -1;
            e.Pending = false;
            e.PendingWhy.clear();
            e.Deleted = false;
            e.ShownFlags = FlagsOf(actor);
        }

        /// The edit bound to an actor, if any.
        Edit* EditOf(AActor* actor)
        {
            auto const it = std::find_if(G.Edits.begin(), G.Edits.end(), [&](Edit const& e) {
                return !e.Pending && e.Actor == actor && e.Index == actor->ObjectInternalInteger && e.Class == actor->Class;
            });
            return it != G.Edits.end() ? &*it : nullptr;
        }

        /// A new edit of an untouched, shown actor, before anything writes to it: its state now, and for a level actor its base
        /// (what the YAML holds; the build that made it shown did not remove it). Adding an edit moves the others: hold indexes
        /// across it, not pointers.
        Edit& NewEdit(AActor* actor)
        {
            auto& edit = G.Edits.emplace_back();
            Bind(edit, actor);
            edit.Map = ToUtf8(MapName());
            edit.Tag = NameText(actor->Tag);
            edit.Path = ToUtf8(PathOf(actor));
            edit.Level = LevelOf(actor);
            edit.ClassName = ToUtf8(NameOf(actor->Class));
            edit.Now = PoseOf(actor);
            // Spawn() takes them back: the YAML has nothing of a new spawn.
            edit.Base = edit.Now;
            edit.BaseHidden = false;
            return edit;
        }

        std::string Awaiting(AActor* actor);

        /// The edit for an actor, made on first touch (Move, Rotate, Scale and Delete only touch a shown actor). Null, said with
        /// say, while a session entry or an edit waiting for its actor names it (Awaiting): the next tick's pass applies it.
        Edit* Touch(AActor* actor, char const* verb, void (*say)(std::string) = SayOnce)
        {
            if (auto const found = EditOf(actor)) return found;
            if (auto const why = Awaiting(actor); !why.empty())
            {
                say(std::format("not {}: {}", verb, why));
                G.PassNow = true;
                return nullptr;
            }
            return &NewEdit(actor);
        }

        void UpdateComponents(AActor* actor)
        {
            if (Fn.ForceUpdateComponents == nullptr) return;
            AActor_execForceUpdateComponents_Parms update;
            update.bCollisionUpdate = 1;
            update.bTransformOnly = 0;
            CallProcessEvent(actor, Fn.ForceUpdateComponents, &update);
        }

        /// bHidden, bCollideActors, bBlockActors: through SetHidden and SetCollision (opcode natives) when the actor's level is
        /// visible, so the engine updates its collision and touching; raw in a level loaded but not visible.
        void WriteFlags(AActor* actor, HideFlags const& flags, bool visible)
        {
            if (visible && Fn.SetHidden != nullptr && Fn.SetCollision != nullptr)
            {
                AActor_execSetHidden_Parms hide;
                hide.bNewHidden = flags.Hidden ? 1 : 0;
                CallProcessEvent(actor, Fn.SetHidden, &hide);
                AActor_execSetCollision_Parms collision;
                collision.bNewColActors = flags.CollideActors ? 1 : 0;
                collision.bNewBlockActors = flags.BlockActors ? 1 : 0;
                // Its third flag as it is: the parameter is not optional to the native, which would clear it.
                collision.bNewIgnoreEncroachers = actor->bIgnoreEncroachers;
                CallProcessEvent(actor, Fn.SetCollision, &collision);
                return;
            }
            actor->bHidden = flags.Hidden ? 1 : 0;
            actor->bCollideActors = flags.CollideActors ? 1 : 0;
            actor->bBlockActors = flags.BlockActors ? 1 : 0;
        }

        /// Deleted is hidden without collision, not destroyed: undo shows it again with the flags it had, and a build hides a
        /// removed: true prop the same way. Writes the actor and the edit.
        void SetDeleted(Edit& e, bool deleted, bool visible)
        {
            if (deleted == e.Deleted) return;
            if (deleted)
            {
                e.ShownFlags = FlagsOf(e.Actor);
                WriteFlags(e.Actor, { true, false, false }, visible);
            }
            else
            {
                WriteFlags(e.Actor, e.ShownFlags, visible);
            }
            e.Deleted = deleted;
        }

        /// Writes a whole state to an edit's actor and to the edit (undo, redo, re-apply): Location, Rotation, DrawScale and
        /// DrawScale3D raw, hidden or shown, then the components follow, unless the actor's level is loaded but not visible.
        void Put(Edit& e, Pose const& pose, bool deleted)
        {
            auto const actor = e.Actor;
            auto const visible = Visible(actor);
            actor->Location = pose.Location;
            actor->Rotation = pose.Rotation;
            actor->DrawScale = pose.DrawScale;
            actor->DrawScale3D = pose.DrawScale3D;
            SetDeleted(e, deleted, visible);
            e.Now = pose;
            if (visible) UpdateComponents(actor);
        }

        /// The build hid the actor (removed: true leaves it hidden without collision): the edit takes it as deleted, with its
        /// class's flags as the ones it has shown, so a Put that shows it shows it with collision. Bind takes a hidden actor as
        /// shown (the game hides actors too); only the YAML's own removed says the build did it.
        void TakeBuiltHidden(Edit& e)
        {
            auto const defaults = e.Actor->Class != nullptr ? static_cast<AActor*>(e.Actor->Class->ClassDefaultObject) : nullptr;
            e.ShownFlags = { false, defaults == nullptr || defaults->bCollideActors != 0, defaults == nullptr || defaults->bBlockActors != 0 };
            e.Deleted = true;
        }

        // ! Undo
        // ========================================

        StepState StateOf(Edit const& e) { return { !e.Deleted, e.ShownFlags, e.Now }; }

        /// "move ed_crate_x_1": what the toolbar's Undo and Redo name.
        std::string Label(Step const& step)
        {
            constexpr char const* Kinds[] = { "spawn", "delete", "move", "turn", "scale" };
            return std::format("{} {}", Kinds[static_cast<int>(step.Kind)], step.Target < G.Edits.size() ? Called(G.Edits[step.Target]) : "?");
        }

        /// A new step clears what could be redone.
        void PushStep(Step step)
        {
            G.Redo.clear();
            G.Undo.push_back(std::move(step));
            while (G.Undo.size() > MaxSteps) G.Undo.pop_front();
        }

        /// Ends the drag step under way: its after is the edit's state now, read from the edit only, so Fail() can call this. A
        /// drag that came back where it started leaves no step.
        void CloseDrag()
        {
            if (!G.Drag) return;
            auto step = std::move(*G.Drag);
            G.Drag.reset();
            if (step.Target >= G.Edits.size()) return;
            step.After = StateOf(G.Edits[step.Target]);
            if (!SameStep(step.Before, step.After)) PushStep(std::move(step));
        }

        /// The first drag command of a drag opens its step, with the edit's state before it; one for another edit or tool closes
        /// the step under way first.
        void OpenDrag(Edit const& edit, StepKind kind)
        {
            auto const index = IndexOf(edit);
            if (G.Drag && (G.Drag->Target != index || G.Drag->Kind != kind)) CloseDrag();
            if (!G.Drag) G.Drag = Step{ index, kind, StateOf(edit), {} };
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
            G.TickingOnce = false;
        }

        /// Lets the paused world tick once. A DynamicLightEnvironment only updates in Tick: a prop spawned while paused renders
        /// black, and a moved one keeps the light of where it was, until the world ticks. The next editor tick takes the pause
        /// back (seen in game: one tick lights it).
        void TickOnce()
        {
            if (!G.Open || !Alive(G.PausedWorld, G.PausedIndex) || !G.PausedWorld->bPlayersOnly) return;
            G.PausedWorld->bPlayersOnly = 0;
            G.TickingOnce = true;
        }

        /// Once, at the first tick: the re-apply pass needs them whether the editor was opened or not. Each is looked up on its
        /// own, so a missing one turns off only what needs it.
        void ResolveFunctions()
        {
            if (Fn.Tried) return;
            Fn.Tried = true;
            auto const find = [](wchar_t const* path) -> UFunction* {
                try
                {
                    return FindFunctionByPath(path);
                }
                catch (std::exception const& e)
                {
                    LEASI_ERROR("editor: {}", e.what());
                    return nullptr;
                }
            };
            Fn.DeProject = find(L"Engine.Canvas.DeProject");
            Fn.Project = find(L"Engine.Canvas.Project");
            Fn.Trace = find(L"Engine.Actor.Trace");
            Fn.ForceUpdateComponents = find(L"Engine.Actor.ForceUpdateComponents");
            Fn.Spawn = find(L"Engine.Actor.Spawn");
            Fn.Destroy = find(L"Engine.Actor.Destroy");
            Fn.SetStaticMesh = find(L"Engine.DynamicSMActor.SetStaticMesh");
            Fn.DynamicLoadObject = find(L"Core.Object.DynamicLoadObject");
            Fn.SetHidden = find(L"Engine.Actor.SetHidden");
            Fn.SetCollision = find(L"Engine.Actor.SetCollision");
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
                CloseDrag();
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

        // ! Changes
        // ========================================

        /// The selection, for a drag command: none when nothing is selected, or when it is hidden (deleted; undo shows it again).
        AActor* DragTarget(char const* verb)
        {
            if (!SelectionAlive() || Fn.ForceUpdateComponents == nullptr) return nullptr;
            if (G.Selected->bHidden)
            {
                SayOnce(std::format("not {}: {} is hidden", verb, ToUtf8(NameOf(G.Selected))));
                return nullptr;
            }
            return G.Selected;
        }

        /// The overlay sends only the field its tool changes, and only that field is written: raw, then the components follow.
        void Move(FVector to)
        {
            auto const actor = DragTarget("moved");
            // A click on an arrow without motion changes nothing, and makes no edit.
            if (actor == nullptr || Near(actor->Location, to, LocationNoise)) return;
            // As live_place does: Location raw (SetLocation refuses bStatic actors, and blocking ones where they would overlap).
            auto const edit = Touch(actor, "moved");
            if (edit == nullptr) return;
            OpenDrag(*edit, StepKind::Move);
            actor->Location = to;
            UpdateComponents(actor);
            edit->Now.Location = to;
            edit->Saved = false;
        }

        void Rotate(FRotator to)
        {
            auto const actor = DragTarget("turned");
            if (actor == nullptr) return;
            // npcs keep a yaw in the level YAML: a pawn turns about the vertical only.
            if (actor->IsA(APawn::StaticClass()))
            {
                to.Pitch = actor->Rotation.Pitch;
                to.Roll = actor->Rotation.Roll;
            }
            // The overlay reads the rotation back from a matrix: the same orientation may come back as other numbers.
            if (SameOrientation(actor->Rotation, to)) return;
            auto const edit = Touch(actor, "turned");
            if (edit == nullptr) return;
            OpenDrag(*edit, StepKind::Turn);
            actor->Rotation = to;
            UpdateComponents(actor);
            edit->Now.Rotation = to;
            edit->Saved = false;
        }

        /// DrawScale3D only: DrawScale is never changed by the editor.
        void Rescale(FVector to)
        {
            auto const actor = DragTarget("scaled");
            if (actor == nullptr) return;
            if (actor->IsA(APawn::StaticClass()))
            {
                SayOnce("not scaled: npcs keep a yaw and no scale");
                return;
            }
            auto const positive = [](float v) { return std::isfinite(v) && v > 0; };
            if (!positive(to.X) || !positive(to.Y) || !positive(to.Z))
            {
                SayOnce("not scaled: a DrawScale3D axis would be zero or negative");
                return;
            }
            if (Near(actor->DrawScale3D, to, ScaleNoise)) return;
            auto const edit = Touch(actor, "scaled");
            if (edit == nullptr) return;
            OpenDrag(*edit, StepKind::Scale);
            actor->DrawScale3D = to;
            UpdateComponents(actor);
            edit->Now.DrawScale3D = to;
            edit->Saved = false;
        }

        /// Why Delete refuses an actor, or empty. Level actors other than props are for Phase 4 (docs/07 §4).
        std::string Undeletable(AActor* actor)
        {
            if (actor->IsA(APawn::StaticClass()) || actor->IsA(AController::StaticClass())) return "pawns and controllers are not deleted";
            if (actor->bStatic) return "a static actor (baked into the level's lighting): hiding vanilla actors comes in Phase 4";
            if (actor->bNoDelete) return "the engine won't destroy it (bNoDelete)";
            return {};
        }

        /// Hides the selection without collision (SetHidden, SetCollision), so undo can show it again.
        void Delete()
        {
            if (!SelectionAlive() || Fn.ForceUpdateComponents == nullptr) return;
            auto const actor = G.Selected;
            if (auto const why = Undeletable(actor); !why.empty())
            {
                Say(std::format("not deleted: {}", why));
                return;
            }
            if (Fn.SetHidden == nullptr || Fn.SetCollision == nullptr)
            {
                Say("not deleted: Actor.SetHidden or Actor.SetCollision was not found");
                return;
            }
            if (actor->bHidden)
            {
                Say(std::format("not deleted: {} is hidden already", ToUtf8(NameOf(actor))));
                return;
            }
            auto const touched = Touch(actor, "deleted", Say);
            if (touched == nullptr) return;
            auto& edit = *touched;
            auto const before = StateOf(edit);
            auto const visible = Visible(actor);
            SetDeleted(edit, true, visible);
            if (visible) UpdateComponents(actor);
            edit.Saved = false;
            PushStep({ IndexOf(edit), StepKind::Delete, before, StateOf(edit) });
            Select(nullptr);
            Say(std::format("deleted {} (Ctrl+Z brings it back)", Called(edit)));
        }

        /// Undo (or redo) the last step: its state is written back to the actor and the edit, which is unsaved again.
        void UndoStep(bool redo)
        {
            auto& from = redo ? G.Redo : G.Undo;
            auto& to = redo ? G.Undo : G.Redo;
            if (from.empty())
            {
                Say(redo ? "nothing to redo" : "nothing to undo");
                return;
            }
            auto step = std::move(from.back());
            from.pop_back();
            auto const label = Label(step);
            if (step.Target >= G.Edits.size() || !Bound(G.Edits[step.Target]))
            {
                Say(std::format("{} {} dropped: its actor is gone", redo ? "redo" : "undo", label));
                return;
            }
            auto& edit = G.Edits[step.Target];
            auto const& state = redo ? step.After : step.Before;
            edit.ShownFlags = state.ShownFlags;
            Put(edit, state.Transform, !state.Shown);
            edit.Saved = false;
            Select(state.Shown ? edit.Actor : nullptr);
            TickOnce();
            Say(std::format("{} {}", redo ? "redid" : "undid", label));
            to.push_back(std::move(step));
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
            // By pointer within this pass only: nothing is collected while it runs.
            std::unordered_map<UObject*, std::string> shownBy;
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
                        auto const it = shownBy.find(component->StaticMesh);
                        if (it == shownBy.end()) shownBy.emplace(component->StaticMesh, level);
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
            for (auto const& [mesh, level] : shownBy) G.MeshFiles.emplace(Lower(ToUtf8(PathOf(mesh))), level);
            std::sort(meshes.begin(), meshes.end(), [](std::string const& a, std::string const& b) { return _stricmp(a.c_str(), b.c_str()) < 0; });
            LEASI_INFO("editor: {} meshes in memory, {} shown by an actor", meshes.size(), G.MeshFiles.size());
            G.Meshes = std::make_shared<std::vector<std::string> const>(std::move(meshes));
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

        std::filesystem::path SessionFolder() { return std::filesystem::path(DataFolder()) / L"sessions"; }
        std::filesystem::path SessionPath(std::string const& map) { return SessionFolder() / (ToWide(map) + L".json"); }

        /// The tags in a map's session file, if it can be read.
        void SessionTags(std::string const& map, std::set<std::string>& tags)
        {
            try
            {
                auto const file = SessionPath(map);
                if (!std::filesystem::exists(file)) return;
                std::ifstream in(file, std::ios::binary);
                for (auto const& a : json::parse(in).at("actors")) tags.insert(Lower(Text(a, "tag")));
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

        /// A mesh from the catalog package by DynamicLoadObject: that mesh with its materials, not the rest of the file. Null when
        /// the package is not installed or has no such mesh. A loaded mesh nothing shows is collected within a minute: the caller
        /// spawns and sets it in the same tick.
        UStaticMesh* LoadMesh(APlayerController* pc, std::string const& path, double& ms)
        {
            ms = -1;
            if (Fn.DynamicLoadObject == nullptr || path.empty()) return nullptr;
            // A static function: any object will do. Core.Default__Object is the one tested in game.
            UObject* caller = UObject::StaticClass()->ClassDefaultObject;
            if (caller == nullptr) caller = pc;
            auto const meshClass = UStaticMesh::StaticClass();
            LARGE_INTEGER start, end, frequency;
            QueryPerformanceCounter(&start);
            UObject_execDynamicLoadObject_Parms load;
            load.ObjectName = ToWide(path).c_str();
            load.ObjectClass = meshClass;
            // Null instead of an error dialog when the catalog package is not installed or has no such mesh.
            load.MayFail = 1;
            CallProcessEvent(caller, Fn.DynamicLoadObject, &load);
            // A union member: the parameter block's destructor leaves it, and the string is on the game's heap.
            std::destroy_at(&load.ObjectName);
            QueryPerformanceCounter(&end);
            QueryPerformanceFrequency(&frequency);
            ms = static_cast<double>(end.QuadPart - start.QuadPart) * 1000.0 / static_cast<double>(frequency.QuadPart);

            auto const loaded = load.ReturnValue;
            if (loaded == nullptr || !loaded->IsA(meshClass))
            {
                LEASI_WARN("editor: DynamicLoadObject({}) returned {} in {:.1f} ms", path, ToUtf8(PathOf(loaded)), ms);
                return nullptr;
            }
            LEASI_INFO("editor: loaded {} from the catalog in {:.1f} ms", ToUtf8(PathOf(loaded)), ms);
            return static_cast<UStaticMesh*>(loaded);
        }

        /// The mesh a Spawn asks for: the one in memory at its path, else, for a catalog entry, the catalog package's copy. Null
        /// once Say() told why.
        UStaticMesh* MeshToSpawn(APlayerController* pc, Command const& c, double& loadMs)
        {
            loadMs = -1;
            // The overlay spawns an In this level row under the catalog package as its catalog entry. One no entry loads (the
            // catalog was rebuilt, or can't be read, since the copy loaded) comes here as is: the session would name the copy.
            if (_strnicmp(c.Text.c_str(), CatalogPrefix, sizeof CatalogPrefix - 1) == 0)
            {
                Say(std::format("not spawned: {} is the catalog package's copy, and no entry of the catalog as last read loads it: "
                                "spawn the prop from the Catalog tab",
                                c.Text));
                return nullptr;
            }
            UObject* found = nullptr;
            std::string missing;
            try
            {
                found = FindByPath(ToWide(c.Text));
            }
            catch (std::exception const& e)
            {
                missing = e.what();
            }
            auto const meshClass = UStaticMesh::StaticClass();
            if (Real(found) && found->IsA(meshClass)) return static_cast<UStaticMesh*>(found);
            if (c.Load.empty())
            {
                // The In this level list: its meshes were in memory when it was read.
                if (found == nullptr) Say(std::format("not spawned: {}", missing));
                else if (!Real(found)) Say(std::format("not spawned: {} is being unloaded (Refresh the list)", c.Text));
                else Say(std::format("not spawned: {} is not a StaticMesh", c.Text));
                return nullptr;
            }
            if (Fn.DynamicLoadObject == nullptr)
            {
                Say("not spawned: Core.Object.DynamicLoadObject was not found, so catalog props no level has in memory can't load");
                return nullptr;
            }
            auto const loaded = LoadMesh(pc, c.Load, loadMs);
            if (loaded == nullptr)
            {
                auto const name = c.Load.substr(c.Load.rfind('.') + 1);
                Say(std::format("not spawned: {} is not in memory, and not in the catalog package ({}): install the Mele Catalog mod "
                                "(mod_install, game closed) and restart the game",
                                name, c.Load));
            }
            return loaded;
        }

        /// A DynamicSMActor_Spawnable with a tag, showing a mesh: it has its own light environment, so it is lit where it stands
        /// once the world ticks. Null when the engine refuses.
        AActor* SpawnShowing(APlayerController* pc, UStaticMesh* mesh, std::string const& tag, FVector location, FRotator rotation)
        {
            AActor_execSpawn_Parms spawn;
            spawn.SpawnClass = ADynamicSMActor_Spawnable::StaticClass();
            spawn.SpawnTag = SFXName(ToWide(tag).c_str(), 0);
            spawn.SpawnLocation = location;
            spawn.SpawnRotation = rotation;
            spawn.bNoCollisionFail = 1;
            CallProcessEvent(pc, Fn.Spawn, &spawn);
            auto const actor = spawn.ReturnValue;
            if (actor == nullptr) return nullptr;
            ADynamicSMActor_execSetStaticMesh_Parms set;
            set.NewMesh = mesh;
            CallProcessEvent(actor, Fn.SetStaticMesh, &set);
            return actor;
        }

        /// A prop where the view points, facing the camera. The mesh is one in memory (the In this level list, or a catalog entry
        /// a level has), or a catalog entry's copy loaded from the catalog package; the session names the vanilla mesh and a
        /// vanilla file either way.
        void Spawn(APlayerController* pc, UCanvas* canvas, Command const& c)
        {
            if (Fn.Spawn == nullptr || Fn.SetStaticMesh == nullptr || Fn.DeProject == nullptr || Fn.Trace == nullptr)
            {
                Say("spawning is off: Actor.Spawn, DynamicSMActor.SetStaticMesh, Canvas.DeProject or Actor.Trace was not found");
                return;
            }
            double loadMs = -1;
            auto const mesh = MeshToSpawn(pc, c, loadMs);
            if (mesh == nullptr) return;

            auto const cameraYaw = pc->PlayerCamera != nullptr ? pc->PlayerCamera->CameraCache.POV.Rotation.Yaw : pc->Rotation.Yaw;
            auto const tag = UniqueTag(NameOf(mesh));
            auto const actor = SpawnShowing(pc, mesh, tag, SpawnPoint(pc, canvas), { 0, (cameraYaw + 32768) & 0xFFFF, 0 });
            if (actor == nullptr)
            {
                Say(std::format("the engine refused to spawn {}", c.Text));
                return;
            }

            // New: no session entry names it (its tag is unique), and nothing was applied to it.
            auto& edit = NewEdit(actor);
            edit.Spawned = true;
            // The YAML has nothing of it until editor_import makes a props entry of it.
            edit.Base.reset();
            edit.BaseHidden.reset();
            edit.MeshPath = c.Text;
            edit.MeshLoad = c.Load;
            if (!c.Source.empty())
            {
                edit.MeshFile = c.Source;
            }
            else
            {
                auto const file = G.MeshFiles.find(Lower(c.Text));
                edit.MeshFile = file != G.MeshFiles.end() ? file->second : std::string{};
            }
            // Undone, the prop is hidden; a spawn deleted before any save leaves nothing in the session.
            PushStep({ IndexOf(edit), StepKind::Spawn, { false, edit.ShownFlags, edit.Now }, StateOf(edit) });
            Select(actor);
            // Paused, the new actor has no light yet: it would render black.
            TickOnce();
            Say(loadMs >= 0 ? std::format("spawned {} as {} (loaded from the catalog in {:.0f} ms)", c.Text, tag, loadMs)
                            : std::format("spawned {} as {}", c.Text, tag));
        }

        // ! Save
        // ========================================

        /// A session entry, version 2 (docs/07 §10). imported is left out: a saved edit is a new state. What an install may still
        /// show goes into installed instead, and the base keeps whether the YAML removes the actor when an import said so.
        json EntryOf(Edit const& e)
        {
            auto const orNull = [](std::string const& text) { return text.empty() ? json(nullptr) : json(text); };
            json mesh = nullptr;
            if (e.Spawned) mesh = { { "path", e.MeshPath }, { "file", orNull(e.MeshFile) }, { "load", orNull(e.MeshLoad) } };
            json base = nullptr;
            if (e.Base)
            {
                base = PoseJson(*e.Base);
                if (e.BaseHidden) base["deleted"] = *e.BaseHidden;
            }
            json entry{
                { "tag", e.Tag },
                { "path", e.Path },
                { "level", e.Level },
                { "class", e.ClassName },
                { "spawned", e.Spawned },
                { "mesh", mesh },
                { "location", VectorJson(e.Now.Location) },
                { "rotation", RotatorJson(e.Now.Rotation) },
                { "scale", e.Now.DrawScale },
                { "scale3d", VectorJson(e.Now.DrawScale3D) },
                { "deleted", e.Deleted },
                { "base", base },
            };
            if (!e.Installed.empty()) entry["installed"] = e.Installed;
            return entry;
        }

        /// A spawn deleted before it was ever saved: Save writes nothing of it (no YAML can have it).
        bool LeavesNothing(Edit const& e) { return e.Spawned && e.Deleted && !e.EverSaved; }

        bool Same(FVector a, FVector b) { return a.X == b.X && a.Y == b.Y && a.Z == b.Z; }
        bool Same(FRotator a, FRotator b) { return a.Pitch == b.Pitch && a.Yaw == b.Yaw && a.Roll == b.Roll; }

        /// Before Save writes an edit over the file's entry for it: each field the editor didn't change since the edit was last
        /// saved or read takes the entry's value, which an import may have moved on (a YAML value the merge kept, onConflict=yaml
        /// included). The actor follows when the edit has one. Returns whether anything changed.
        bool TakeFileValues(Edit& e, json const& entry)
        {
            if (!e.SavedPose) return false;
            auto const& saved = *e.SavedPose;
            auto const file = PoseFrom(entry, e.Now);
            auto now = e.Now;
            if (Same(now.Location, saved.Location)) now.Location = file.Location;
            if (Same(now.Rotation, saved.Rotation)) now.Rotation = file.Rotation;
            if (now.DrawScale == saved.DrawScale) now.DrawScale = file.DrawScale;
            if (Same(now.DrawScale3D, saved.DrawScale3D)) now.DrawScale3D = file.DrawScale3D;
            if (Same(now.Location, e.Now.Location) && Same(now.Rotation, e.Now.Rotation) && now.DrawScale == e.Now.DrawScale
                && Same(now.DrawScale3D, e.Now.DrawScale3D))
                return false;
            LEASI_INFO("editor: {} takes the session file's values for what it didn't change since its last save (an import moved them on)", Called(e));
            if (Bound(e)) Put(e, now, e.Deleted);
            else e.Now = now;
            return true;
        }

        /// Written beside it, then moved over it: a crash mid-write leaves the old file whole.
        void WriteFile(std::filesystem::path const& file, json const& doc)
        {
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
        }

        struct SessionWrite
        {
            std::filesystem::path File;
            /// Where an unreadable earlier file was moved; empty when there was none.
            std::filesystem::path Bad;
            /// The edits written: a spawn deleted before it was ever saved is not.
            std::vector<Edit*> Written;
            /// Edits that took values an import wrote into the file.
            int TookFileValues = 0;
        };

        /// Merges one map's edits into its session file (%LOCALAPPDATA%\\MeleBridge\\LE1\\sessions\\<map>.json): the latest state
        /// of each actor, which mele-mcp's editor_import writes into the level YAML. Returns the file, the path an unreadable
        /// earlier file was moved to (then every edit of the map is written, saved ones too, since the earlier file had them), and
        /// the edits written; with none (only spawns deleted before their first save), the file is not rewritten. A file of a
        /// version this bridge doesn't know is left alone.
        SessionWrite WriteSession(std::string const& map, std::vector<Edit*> const& unsaved, std::vector<Edit*> const& all)
        {
            if (map.empty()) throw BridgeError("the map name is empty");
            auto const folder = SessionFolder();
            std::filesystem::create_directories(folder);
            auto const file = SessionPath(map);

            std::map<std::string, json> actors;
            std::vector<std::string> order;
            std::filesystem::path bad;
            auto version = SessionVersion;
            if (std::filesystem::exists(file))
            {
                try
                {
                    std::ifstream in(file, std::ios::binary);
                    auto const doc = json::parse(in);
                    version = doc.value("version", 1);
                    if (version == 1 || version == SessionVersion)
                    {
                        for (auto const& a : doc.at("actors"))
                        {
                            auto const key = KeyOf(a);
                            if (!actors.contains(key)) order.push_back(key);
                            actors[key] = a;
                        }
                    }
                }
                catch (std::exception const& e)
                {
                    // Kept for a look, not overwritten.
                    actors.clear();
                    order.clear();
                    version = SessionVersion;
                    bad = file;
                    bad += std::format(L".bad-{}", GetTickCount64());
                    std::filesystem::rename(file, bad);
                    LEASI_WARN("editor: {} could not be read ({}), moved to {}", ToUtf8(file.wstring()), e.what(), ToUtf8(bad.wstring()));
                }
            }
            if (version != 1 && version != SessionVersion)
                throw BridgeError(std::format("{} is version {}, and this bridge reads versions 1 and 2: left as it is", ToUtf8(file.wstring()), version));

            // The file's entries as read: an import may have moved an entry's values and base on since the edit last saw it.
            auto const read = actors;
            SessionWrite result{ file, bad, {}, 0 };
            for (auto const e : bad.empty() ? unsaved : all)
            {
                auto const key = KeyOf(*e);
                // A spawned actor deleted before any save leaves nothing. Once saved, it may have been imported (and its entry
                // marked so): it is written as deleted: true, for editor_import to mark its props entry removed.
                if (LeavesNothing(*e)) continue;
                if (auto const it = read.find(key); it != read.end())
                {
                    auto const& entry = it->second;
                    if (TakeFileValues(*e, entry)) result.TookFileValues++;
                    if (auto const base = entry.find("base"); base != entry.end())
                    {
                        if (base->is_object())
                        {
                            e->Base = PoseFrom(*base, e->Base.value_or(e->Now));
                            // Whether the YAML removes it: the base says so (editor_import writes it). An entry an import marked
                            // without it holds what it wrote into the YAML: deleted is removed: true there.
                            if (base->contains("deleted")) e->BaseHidden = Flag(*base, "deleted");
                            else if (entry.contains("imported")) e->BaseHidden = Flag(entry, "deleted");
                        }
                        else
                        {
                            e->Base.reset();
                            e->BaseHidden.reset();
                        }
                    }
                    // What an install may still show, from this import and the ones before: the next import's before is the base.
                    for (auto const& state : Candidates(entry))
                    {
                        if (std::find(e->Installed.begin(), e->Installed.end(), state) == e->Installed.end()) e->Installed.push_back(state);
                    }
                }
                // Built and installed, a spawned actor is a level actor with the same tag: its spawned entry is history.
                if (!e->Spawned && !e->Tag.empty() && e->Tag != "None") actors.erase("tag:" + e->Tag);
                if (!actors.contains(key)) order.push_back(key);
                actors[key] = EntryOf(*e);
                result.Written.push_back(e);
            }
            if (result.Written.empty() && bad.empty()) return result;

            auto list = json::array();
            for (auto const& key : order)
            {
                if (auto const it = actors.find(key); it != actors.end()) list.push_back(it->second);
            }
            auto const now = std::chrono::floor<std::chrono::seconds>(std::chrono::system_clock::now());
            WriteFile(file, json{ { "version", SessionVersion }, { "map", map }, { "saved", std::format("{:%FT%TZ}", now) }, { "actors", list } });
            return result;
        }

        struct SaveResult
        {
            /// No edit was unsaved.
            bool Nothing = false;
            /// Entries written, and the unsaved spawns deleted before their first save, which leave nothing.
            size_t Count = 0;
            size_t LeftNothing = 0;
            /// The files written.
            std::vector<std::string> Written;
            std::vector<std::string> Problems;
            /// Maps whose file could not be written: their edits stay unsaved.
            std::set<std::string> Failed;
            /// Edits that took values an import wrote into the file.
            int TookFileValues = 0;
        };

        /// Writes each map's unsaved edits from their stored state (no actor is read). The session's waiting entries a write
        /// replaced are dropped from the pending list. A map whose file could not be written keeps its edits unsaved, and
        /// G.SaveProblem says so until a save writes every map.
        SaveResult SaveAll()
        {
            SaveResult result;
            std::map<std::string, std::pair<std::vector<Edit*>, std::vector<Edit*>>> byMap;
            for (auto& e : G.Edits)
            {
                auto& [unsaved, all] = byMap[e.Map];
                all.push_back(&e);
                if (!e.Saved) unsaved.push_back(&e);
            }
            std::erase_if(byMap, [](auto const& m) { return m.second.first.empty(); });
            if (byMap.empty())
            {
                result.Nothing = true;
                return result;
            }
            std::vector<std::string> failures;
            for (auto const& [map, edits] : byMap)
            {
                try
                {
                    auto const write = WriteSession(map, edits.first, edits.second);
                    std::set<std::string> keys;
                    for (auto const e : edits.first)
                    {
                        // A spawn deleted before it was ever saved is not written: saved, but still never saved once.
                        e->Saved = true;
                        keys.insert(KeyOf(*e));
                        if (!e->Spawned && !e->Tag.empty() && e->Tag != "None") keys.insert("tag:" + e->Tag);
                        if (LeavesNothing(*e)) result.LeftNothing++;
                    }
                    for (auto const e : write.Written)
                    {
                        e->EverSaved = true;
                        e->SavedPose = e->Now;
                        // A new state now, newer than any build: no longer judged as the entry it was read from.
                        e->Source = nullptr;
                    }
                    if (map == G.SessionMap) std::erase_if(G.Entries, [&](Entry const& entry) { return keys.contains(entry.Key); });
                    // What the file got: after an unreadable earlier file, the saved edits are written again too.
                    result.Count += write.Written.size();
                    result.TookFileValues += write.TookFileValues;
                    if (!write.Written.empty() || !write.Bad.empty()) result.Written.push_back(ToUtf8(write.File.wstring()));
                    if (!write.Bad.empty()) result.Problems.push_back(std::format("the earlier file was unreadable, kept as {}", ToUtf8(write.Bad.wstring())));
                }
                catch (std::exception const& error)
                {
                    failures.push_back(std::format("{} not saved: {}", map, error.what()));
                    result.Problems.push_back(failures.back());
                    result.Failed.insert(map);
                }
            }
            G.SaveProblem.clear();
            for (size_t i = 0; i < failures.size(); i++) G.SaveProblem += (i > 0 ? "; " : "") + failures[i];
            if (!failures.empty()) G.SaveProblem += ". The edits are kept, and the next Save tries again";
            G.SummaryStale = true;
            return result;
        }

        /// "1 spawn deleted before its first save leaves nothing": what a save's count leaves out.
        std::string Unwritten(size_t count)
        {
            return count == 1 ? "1 spawn deleted before its first save leaves nothing"
                              : std::format("{} spawns deleted before their first save leave nothing", count);
        }

        void Save()
        {
            auto const result = SaveAll();
            if (result.Nothing)
            {
                Say("nothing to save: no actor was spawned, moved, turned, scaled or deleted since the last save");
                return;
            }
            std::string text;
            if (result.Count > 0 && !result.Written.empty())
            {
                text = std::format("saved {} edit{} to {}", result.Count, result.Count == 1 ? "" : "s", result.Written.front());
                for (size_t i = 1; i < result.Written.size(); i++) text += " and " + result.Written[i];
                if (result.LeftNothing > 0) text += std::format(" ({})", Unwritten(result.LeftNothing));
            }
            else if (result.Failed.empty()) text = result.LeftNothing > 0 ? "nothing written: " + Unwritten(result.LeftNothing) : "nothing written";
            else text = "not saved";
            if (result.TookFileValues > 0)
                text += std::format(" ({} took values an import wrote into the file, where they hadn't changed since)", result.TookFileValues);
            for (auto const& p : result.Problems) text += "; " + p;
            if (result.Count > 0 && result.Problems.empty()) text += ". Next: editor_import in mele-mcp (level YAML, then build)";
            // Paused, an actor that took a location from the file keeps the light of where it was.
            if (result.TookFileValues > 0) TickOnce();
            Say(text);
        }

        // ! Re-apply
        // ========================================

        /// An access violation in editor code fails that code, not the game. No C++ object may live in this frame.
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

        /// The new map's session file, read into the pending list. Edits of that map kept from a save that failed go back to
        /// pending too: they are newer than the file, whose entries for them are skipped.
        void ReadSession(std::string const& map)
        {
            G.Entries.clear();
            G.SessionMap = map;
            G.SessionFile.clear();
            G.SessionError.clear();
            G.Applied = G.Dropped = 0;
            G.PassLevels.clear();
            G.PassFrame = 0;
            G.SummaryStale = true;
            if (map.empty()) return;

            std::set<std::string> kept;
            for (auto& e : G.Edits)
            {
                if (e.Map != map) continue;
                kept.insert(KeyOf(e));
                if (e.Spawned && e.Deleted) e.Rebindable = true;
                else
                {
                    e.Pending = true;
                    e.PendingWhy = "kept from a save that failed, waiting for its actor";
                }
            }
            try
            {
                auto const file = SessionPath(map);
                G.SessionFile = ToUtf8(file.wstring());
                if (!std::filesystem::exists(file)) return;
                std::ifstream in(file, std::ios::binary);
                auto const doc = json::parse(in);
                auto const version = doc.value("version", 1);
                if (version != 1 && version != SessionVersion) throw BridgeError(std::format("version {}, and this bridge reads versions 1 and 2", version));
                for (auto const& a : doc.at("actors"))
                {
                    auto key = KeyOf(a);
                    if (kept.contains(key)) continue;
                    G.Entries.push_back({ a, std::move(key), "not matched yet" });
                }
                LEASI_INFO("editor: session {}: {} entries to re-apply", G.SessionFile, G.Entries.size());
            }
            catch (std::exception const& e)
            {
                G.Entries.clear();
                G.SessionError = std::format("can't be read: {}", e.what());
                LEASI_WARN("editor: session {} {}", G.SessionFile, G.SessionError);
            }
        }

        /// The loaded or visible levels changed. Edits whose level left go back to pending with their stored state (their actor
        /// leaves with the level) and their undo steps are dropped; edits of a level that turned visible update their components;
        /// a pass runs once the set settles.
        void LevelsChanged(std::vector<LevelSeen> levels)
        {
            auto const before = std::exchange(G.Levels, std::move(levels));
            G.LevelsChangedAt = GetTickCount64();
            G.PassWanted = true;
            G.PassFaulted = false;
            G.SummaryStale = true;
            auto const loaded = [](UObject* level, int index) {
                return std::any_of(G.Levels.begin(), G.Levels.end(), [&](LevelSeen const& l) { return l.Level == level && l.Index == index; });
            };
            for (auto const& old : before)
            {
                if (loaded(old.Level, old.Index)) continue;
                for (size_t i = 0; i < G.Edits.size(); i++)
                {
                    auto& e = G.Edits[i];
                    if (e.Actor == nullptr || e.Pending || e.LevelObject != old.Level || e.LevelIndex != old.Index) continue;
                    if (G.Drag && G.Drag->Target == i) G.Drag.reset();
                    if (G.Selected == e.Actor) Select(nullptr);
                    std::erase_if(G.Undo, [i](Step const& s) { return s.Target == i; });
                    std::erase_if(G.Redo, [i](Step const& s) { return s.Target == i; });
                    LEASI_INFO("editor: {} left with {}; its edit waits for the level to come back", Called(e), e.Level);
                    Detach(e);
                    if (e.Spawned && e.Deleted) e.Rebindable = true;
                    else
                    {
                        e.Pending = true;
                        e.PendingWhy = std::format("{} is not loaded", e.Level);
                    }
                }
            }
            // A level that turned visible: its actors' components were attached over several frames, some maybe before an edit
            // wrote its actor raw (a level not visible gets no component update). Updated again, they take the raw transform
            // and flags.
            for (auto const& level : G.Levels)
            {
                if (!level.Visible) continue;
                auto const was = std::find_if(before.begin(), before.end(), [&](LevelSeen const& l) { return l.Level == level.Level && l.Index == level.Index; });
                if (was == before.end() || was->Visible) continue;
                for (auto const& e : G.Edits)
                {
                    if (Bound(e) && e.LevelObject == level.Level && e.LevelIndex == level.Index) UpdateComponents(e.Actor);
                }
            }
        }

        /// The actors of the loaded levels, as one pass sees them (ULevelBase::Actors, not every object in memory).
        struct LoadedActors
        {
            std::vector<AActor*> All;
            /// By lower-case tag text.
            std::unordered_map<std::string, std::vector<AActor*>> ByTag;
            /// By lower-case path, filled on first use: most entries match by tag.
            std::unordered_map<std::string, AActor*> ByPath;
            bool PathsRead = false;
            /// Lower-case package names of the loaded levels.
            std::set<std::string> Levels;
            /// Actors an edit has.
            std::unordered_set<AActor*> Taken;

            std::vector<AActor*> const& Tagged(std::string const& tag) const
            {
                static std::vector<AActor*> const none;
                auto const it = ByTag.find(Lower(tag));
                return it != ByTag.end() ? it->second : none;
            }

            AActor* AtPath(std::string const& path)
            {
                if (!PathsRead)
                {
                    PathsRead = true;
                    for (auto const actor : All) ByPath.emplace(Lower(ToUtf8(PathOf(actor))), actor);
                }
                auto const it = ByPath.find(Lower(path));
                return it != ByPath.end() ? it->second : nullptr;
            }
        };

        LoadedActors ReadLoadedActors()
        {
            LoadedActors loaded;
            for (auto const& level : G.Levels)
            {
                loaded.Levels.insert(Lower(LevelOf(level.Level)));
                for (auto const actor : static_cast<ULevelBase*>(level.Level)->Actors)
                {
                    if (!Real(actor) || actor->bDeleteMe) continue;
                    loaded.All.push_back(actor);
                    loaded.ByTag[Lower(NameText(actor->Tag))].push_back(actor);
                }
            }
            for (auto const& e : G.Edits)
            {
                if (Bound(e)) loaded.Taken.insert(e.Actor);
            }
            return loaded;
        }

        /// The loaded actor an entry or a detached edit names (docs/07 §10). A spawn: the one actor with its tag (a built copy). A
        /// level actor: by tag when exactly one loaded actor has it, it is a real tag, and that actor is in the entry's level and
        /// of its class (the same tag in another level is another actor while the entry's level isn't loaded); else by path with
        /// the same class. Null, with why, when there is none or another edit has it.
        AActor* Match(LoadedActors& loaded, bool spawned, std::string const& tag, std::string const& path, std::string const& className,
                      std::string& why)
        {
            auto const& tagged = loaded.Tagged(tag);
            if (spawned)
            {
                AActor* copy = nullptr;
                size_t count = 0;
                for (auto const actor : tagged)
                {
                    if (loaded.Taken.contains(actor)) continue;
                    copy = actor;
                    count++;
                }
                if (count == 1) return copy;
                why = count == 0 ? "no loaded actor has its tag" : std::format("{} loaded actors have its tag", count);
                return nullptr;
            }
            auto const level = path.substr(0, path.find('.'));
            if (RealTag(tag, className) && tagged.size() == 1 && SameText(LevelOf(tagged[0]), level)
                && SameText(ToUtf8(NameOf(tagged[0]->Class)), className))
            {
                if (!loaded.Taken.contains(tagged[0])) return tagged[0];
                why = "the actor with its tag has another edit";
                return nullptr;
            }
            auto const actor = loaded.AtPath(path);
            auto const actorClass = actor != nullptr ? ToUtf8(NameOf(actor->Class)) : std::string{};
            if (actor != nullptr && SameText(actorClass, className))
            {
                if (!loaded.Taken.contains(actor)) return actor;
                why = "the actor at its path has another edit";
                return nullptr;
            }
            if (!loaded.Levels.contains(Lower(level))) why = std::format("{} is not loaded", level);
            else if (actor != nullptr) why = std::format("the actor at its path is a {}, not a {}", actorClass, className);
            else why = RealTag(tag, className) ? "no loaded actor at its path, and not exactly one with its tag in its level" : "no loaded actor at its path";
            return nullptr;
        }

        /// An entry a pass can apply: one with a path, a location and a rotation.
        bool Valid(json const& entry)
        {
            double v[3];
            return entry.is_object() && !Text(entry, "path").empty() && Numbers(entry, "location", v) && Numbers(entry, "rotation", v);
        }

        /// Why no edit may be made of this actor yet, or empty: a session entry still waiting names it (the first pass waits for
        /// the loaded levels to settle, a level that streams in for half a second), or an edit waiting for its actor (its level
        /// came back) or for its built copy (a spawn spawned again) does. An edit made of the actor as it stands would replace
        /// that entry or edit at the next Save, each field the user did not touch taking the actor's stale value.
        std::string Awaiting(AActor* actor)
        {
            struct Named
            {
                bool Spawned;
                std::string Tag, Path, ClassName;
            };
            auto const tag = NameText(actor->Tag);
            auto const path = ToUtf8(PathOf(actor));
            // Only one that has its tag or its path can match it: the loaded actors are read only then.
            std::vector<Named> named;
            auto const add = [&](bool spawned, std::string const& t, std::string const& p, std::string const& c) {
                if ((!t.empty() && SameText(t, tag)) || SameText(p, path)) named.push_back({ spawned, t, p, c });
            };
            for (auto const& entry : G.Entries)
            {
                if (!entry.Done && Valid(entry.Data)) add(Flag(entry.Data, "spawned"), Text(entry.Data, "tag"), Text(entry.Data, "path"), Text(entry.Data, "class"));
            }
            for (auto const& e : G.Edits)
            {
                if (e.Map == G.SessionMap && (e.Pending || e.Rebindable)) add(e.Spawned, e.Tag, e.Path, e.ClassName);
            }
            if (named.empty()) return {};
            auto loaded = ReadLoadedActors();
            for (auto const& n : named)
            {
                // As the pass will match it: an entry that names it only as one of two actors with its tag waits anyway.
                std::string why;
                if (Match(loaded, n.Spawned, n.Tag, n.Path, n.ClassName, why) != actor) continue;
                auto const name = ToUtf8(NameOf(actor));
                return G.PassFaulted ? std::format("{}'s session entry is not applied yet, and the last re-apply pass failed (tried again when the loaded levels change)", name)
                                     : std::format("{}'s session entry is still being applied, try again in a moment", name);
            }
            return {};
        }

        enum class Verdict { Apply, Drop };

        /// Rules 1 and 2 of docs/07 §10, for an entry and the actor it matched (null: a spawn no loaded actor has the tag of).
        /// builtHidden: the actor is hidden, and the YAML's state it was judged by says the YAML removes it (the build hid it, not
        /// the game): an entry that shows it shows it again.
        Verdict Judge(json const& entry, AActor* actor, std::string& why, bool& builtHidden)
        {
            auto const has = [&](json const& state) { return actor != nullptr && Has(actor, PoseFrom(state, PoseOf(actor)), Flag(state, "deleted")); };
            auto const hid = [&](json const& state) { return actor != nullptr && actor->bHidden && state.is_object() && Flag(state, "deleted"); };
            // The base says whether the YAML removes it (rule 3: an entry saved since an import keeps it).
            auto const base = entry.find("base");
            builtHidden = base != entry.end() && hid(*base);
            if (auto const imported = entry.find("imported"); imported != entry.end())
            {
                // editor_import put the entry in the YAML. The actor has it: installed. The actor has what the YAML held before (or
                // what an earlier import's YAML held, or, for a spawn, no actor has its tag yet): the install is still to come.
                // Anything else: the YAML was built again since.
                if (has(entry))
                {
                    why = "installed: the level has it";
                    return Verdict::Drop;
                }
                if (actor == nullptr) return Verdict::Apply;
                for (auto const& state : Candidates(entry))
                {
                    if (!has(state)) continue;
                    builtHidden = builtHidden || hid(state);
                    return Verdict::Apply;
                }
                why = "the level was built from other YAML since the import";
                return Verdict::Drop;
            }
            // Version 1, no base: Phase 1's import may have put it in the level already.
            if (base == entry.end() && has(entry))
            {
                why = "the level has it already";
                return Verdict::Drop;
            }
            return Verdict::Apply;
        }

        /// An edit from a session entry, as the file has it: saved, and saved once (editor_import may have imported it). It keeps
        /// the entry until a pass judges it against an actor (a spawn spawned again meets its built copy later).
        Edit EditFrom(json const& entry)
        {
            Edit e;
            e.Map = G.SessionMap;
            e.Tag = Text(entry, "tag");
            e.Path = Text(entry, "path");
            e.Level = Text(entry, "level");
            if (e.Level.empty()) e.Level = e.Path.substr(0, e.Path.find('.'));
            e.ClassName = Text(entry, "class");
            e.Spawned = Flag(entry, "spawned");
            if (auto const mesh = entry.find("mesh"); mesh != entry.end() && mesh->is_object())
            {
                e.MeshPath = Text(*mesh, "path");
                e.MeshFile = Text(*mesh, "file");
                e.MeshLoad = Text(*mesh, "load");
            }
            if (auto const base = entry.find("base"); base != entry.end() && base->is_object())
            {
                // As Save reads it: the base's deleted, else what an import's entry wrote into the YAML.
                if (base->contains("deleted")) e.BaseHidden = Flag(*base, "deleted");
                else if (entry.contains("imported")) e.BaseHidden = Flag(entry, "deleted");
            }
            e.Installed = Candidates(entry);
            e.Source = entry;
            e.Saved = e.EverSaved = true;
            return e;
        }

        /// The StaticMeshes in memory at these lower-case paths, from one walk of the objects.
        std::unordered_map<std::string, UStaticMesh*> MeshesAt(std::unordered_set<std::string> const& paths)
        {
            std::unordered_map<std::string, UStaticMesh*> found;
            if (paths.empty()) return found;
            auto& objects = *UObject::GObjObjects;
            auto const meshClass = UStaticMesh::StaticClass();
            for (UINT i = 0; i < objects.Count() && found.size() < paths.size(); i++)
            {
                auto const object = objects.GetData()[i];
                if (!Real(object) || !object->IsA(meshClass)) continue;
                auto path = Lower(ToUtf8(PathOf(object)));
                if (paths.contains(path)) found.emplace(std::move(path), static_cast<UStaticMesh*>(object));
            }
            return found;
        }

        /// The catalog package's path for a vanilla mesh, from catalog.json, which the game thread reads once for itself (the
        /// overlay has its own copy). Empty when the catalog has no such mesh.
        std::string CatalogLoad(std::string const& mesh)
        {
            if (!G.CatalogRead)
            {
                G.CatalogRead = true;
                try
                {
                    auto const file = std::filesystem::path(DataFolder()) / L"catalog" / L"catalog.json";
                    if (std::filesystem::exists(file))
                    {
                        std::ifstream in(file, std::ios::binary);
                        auto const doc = json::parse(in);
                        if (doc.value("version", 0) != 1) throw BridgeError("not version 1");
                        for (auto const& e : doc.at("entries"))
                        {
                            auto const path = Text(e, "mesh"), load = Text(e, "load");
                            if (!path.empty() && !load.empty()) G.CatalogLoads.emplace(Lower(path), load);
                        }
                        LEASI_INFO("editor: catalog read for re-applying sessions: {} props", G.CatalogLoads.size());
                    }
                }
                catch (std::exception const& e)
                {
                    LEASI_WARN("editor: catalog.json not read for re-applying sessions: {}", e.what());
                }
            }
            auto const it = G.CatalogLoads.find(Lower(mesh));
            return it != G.CatalogLoads.end() ? it->second : std::string{};
        }

        /// Our own spawn, replaced by the built copy: destroyed, or hidden if the engine refuses.
        void DestroySpawn(AActor* actor)
        {
            if (Fn.Destroy != nullptr)
            {
                AActor_execDestroy_Parms destroy;
                CallProcessEvent(actor, Fn.Destroy, &destroy);
                if (destroy.ReturnValue) return;
            }
            WriteFlags(actor, { true, false, false }, Visible(actor));
            UpdateComponents(actor);
        }

        /// Spawns again the spawns no loaded actor stands for, where the session put them: the mesh in memory (one walk for all of
        /// them), else the catalog package's copy by mesh.load, else by the catalog entry with that vanilla mesh. Returns how many.
        int Respawn(APlayerController* pc, std::vector<size_t> const& which)
        {
            if (which.empty()) return 0;
            if (Fn.Spawn == nullptr || Fn.SetStaticMesh == nullptr)
            {
                for (auto const i : which) G.Edits[i].PendingWhy = "spawning is off: Actor.Spawn or DynamicSMActor.SetStaticMesh was not found";
                return 0;
            }
            std::unordered_set<std::string> wanted;
            for (auto const i : which)
            {
                if (!G.Edits[i].MeshPath.empty()) wanted.insert(Lower(G.Edits[i].MeshPath));
                if (!G.Edits[i].MeshLoad.empty()) wanted.insert(Lower(G.Edits[i].MeshLoad));
            }
            auto const inMemory = MeshesAt(wanted);
            auto const memory = [&](std::string const& path) -> UStaticMesh* {
                auto const it = inMemory.find(Lower(path));
                return it != inMemory.end() ? it->second : nullptr;
            };

            int count = 0;
            for (auto const i : which)
            {
                auto& e = G.Edits[i];
                if (e.MeshPath.empty())
                {
                    e.PendingWhy = "no mesh to spawn it with (its entry has no mesh.path)";
                    continue;
                }
                double ms = -1;
                auto mesh = memory(e.MeshPath);
                if (mesh == nullptr && !e.MeshLoad.empty())
                {
                    mesh = memory(e.MeshLoad);
                    if (mesh == nullptr) mesh = LoadMesh(pc, e.MeshLoad, ms);
                }
                if (mesh == nullptr)
                {
                    auto const load = CatalogLoad(e.MeshPath);
                    if (!load.empty() && !SameText(load, e.MeshLoad)) mesh = LoadMesh(pc, load, ms);
                }
                if (mesh == nullptr)
                {
                    e.PendingWhy = std::format("{} is not in memory, and the catalog package does not load it", e.MeshPath);
                    continue;
                }
                auto const actor = SpawnShowing(pc, mesh, e.Tag, e.Now.Location, e.Now.Rotation);
                if (actor == nullptr)
                {
                    e.PendingWhy = "the engine refused to spawn it again";
                    continue;
                }
                auto const pose = e.Now;
                Bind(e, actor);
                // Before anything else touches it: a built copy must still take over if the Put faults.
                e.Rebindable = true;
                Put(e, pose, false);
                count++;
                LEASI_INFO("editor: spawned {} again as {}", e.Tag, ToUtf8(PathOf(actor)));
            }
            return count;
        }

        /// A version 1 entry's base (rule 2): the actor as the YAML built it, before applying. Shown, the build did not remove it;
        /// hidden, the game may have hidden it: whether the YAML removes it stays unknown.
        void TakeBase(Edit& e, AActor* actor)
        {
            e.Base = PoseOf(actor);
            if (!actor->bHidden) e.BaseHidden = false;
        }

        /// An edit takes over the loaded actor it waited for: a spawn's built copy (ours, spawned again, is destroyed), or the
        /// actor of a level that came back. An edit that is still the session entry it was made from is judged first, as the
        /// entry would have been (a spawn spawned again had no actor to judge it by). Dropped, the edit takes the actor as the
        /// level built it, without writing it, and the entry goes to dropped; else the edit's state is written, and an edit of a
        /// version 1 entry with no base yet takes the actor's (rule 2), whether it changed since the read or not. Returns whether
        /// it was written.
        bool TakeOver(Edit& e, AActor* actor, LoadedActors& loaded, std::vector<json>& dropped)
        {
            auto verdict = Verdict::Apply;
            auto builtHidden = false;
            std::string why;
            // Changed since it was read, the edit is newer than any build: always written.
            if (e.Saved && !e.Source.is_null()) verdict = Judge(e.Source, actor, why, builtHidden);
            auto const source = std::exchange(e.Source, nullptr);
            auto const pose = e.Now;
            auto const deleted = e.Deleted;
            if (Bound(e))
            {
                if (G.Selected == e.Actor) Select(nullptr);
                DestroySpawn(e.Actor);
            }
            Bind(e, actor);
            loaded.Taken.insert(actor);
            e.Rebindable = false;
            if (e.Spawned) e.OnCopy = true;
            if (verdict == Verdict::Drop)
            {
                LEASI_INFO("editor: session entry {} dropped on {}: {}", KeyOf(e), ToUtf8(PathOf(actor)), why);
                // What the YAML built; a built copy hidden is one the YAML removes.
                if (actor->bHidden) TakeBuiltHidden(e);
                e.Now = PoseOf(actor);
                e.Base = e.Now;
                e.BaseHidden = actor->bHidden != 0;
                e.Installed = json::array();
                e.SavedPose.reset();
                dropped.push_back(source);
                return false;
            }
            // Before the write: the copy as built.
            if (!e.Base && source.is_object() && !source.contains("base")) TakeBase(e, actor);
            if (builtHidden) TakeBuiltHidden(e);
            Put(e, pose, deleted);
            return true;
        }

        /// Removes the entries a pass dropped from the map's session file, each only if the file still has it as it was read (a
        /// save or an import since leaves it alone). Same temp-file write as Save.
        void DropEntries(std::string const& map, std::vector<json> const& dropped)
        {
            try
            {
                auto const file = SessionPath(map);
                if (!std::filesystem::exists(file)) return;
                json doc;
                {
                    std::ifstream in(file, std::ios::binary);
                    doc = json::parse(in);
                }
                auto const version = doc.value("version", 1);
                if (version != 1 && version != SessionVersion) return;
                auto gone = dropped;
                auto left = json::array();
                for (auto& a : doc.at("actors"))
                {
                    auto const it = std::find(gone.begin(), gone.end(), a);
                    if (it != gone.end())
                    {
                        gone.erase(it);
                        continue;
                    }
                    left.push_back(std::move(a));
                }
                auto const removed = dropped.size() - gone.size();
                if (removed == 0) return;
                doc["actors"] = std::move(left);
                WriteFile(file, doc);
                LEASI_INFO("editor: {} entr{} dropped from {}", removed, removed == 1 ? "y" : "ies", ToUtf8(file.wstring()));
            }
            catch (std::exception const& e)
            {
                LEASI_WARN("editor: dropped entries not removed from {}'s session: {}", map, e.what());
            }
        }

        /// One re-apply pass (docs/07 §10): built copies take over spawned-again spawns, detached edits find their actor again,
        /// and the session's entries are matched, judged and applied, dropped, or left waiting.
        void Pass(APlayerController* pc)
        {
            G.PassFrame = G.Frame;
            G.SummaryStale = true;
            // Entries a pass that faulted had applied or dropped already.
            std::erase_if(G.Entries, [](Entry const& entry) { return entry.Done; });
            G.PassLevels.clear();
            for (auto const& level : G.Levels)
            {
                auto const name = LevelOf(level.Level);
                G.PassLevels.push_back(level.Visible ? name : name + " (hidden)");
            }
            auto loaded = ReadLoadedActors();
            int applied = 0, rebound = 0;
            std::vector<size_t> respawn;
            std::vector<json> dropped;

            // A built copy with a spawn's tag appeared: the edit moves to it, and our own spawn goes.
            for (size_t i = 0; i < G.Edits.size(); i++)
            {
                auto& e = G.Edits[i];
                if (e.Map != G.SessionMap || !e.Spawned || !e.Rebindable) continue;
                std::string why;
                auto const copy = Match(loaded, true, e.Tag, e.Path, e.ClassName, why);
                if (copy == nullptr) continue;
                if (TakeOver(e, copy, loaded, dropped)) rebound++;
                LEASI_INFO("editor: {} is on its built copy {} now", e.Tag, ToUtf8(PathOf(copy)));
            }

            // Edits whose level came back, and spawns not spawned again yet.
            for (size_t i = 0; i < G.Edits.size(); i++)
            {
                auto& e = G.Edits[i];
                if (e.Map != G.SessionMap || !e.Pending) continue;
                std::string why;
                auto const actor = Match(loaded, e.Spawned, e.Tag, e.Path, e.ClassName, why);
                if (actor == nullptr && e.Spawned && !loaded.Tagged(e.Tag).empty())
                {
                    // Two built copies, or a copy another edit has: which one is ours is not for a pass to guess.
                    e.PendingWhy = why;
                }
                else if (actor != nullptr)
                {
                    if (TakeOver(e, actor, loaded, dropped)) applied++;
                }
                else if (e.Spawned && !e.OnCopy) respawn.push_back(i);
                else e.PendingWhy = why;
            }

            // The session file's entries. Each stays in the list, done or with why it waits, until the pass ends: a pass that
            // faults midway leaves the list whole.
            for (auto& entry : G.Entries)
            {
                auto const& a = entry.Data;
                if (!Valid(a))
                {
                    entry.Why = "malformed: an entry needs a path, a location [x, y, z] and a rotation [pitch, yaw, roll]";
                    continue;
                }
                auto const spawned = Flag(a, "spawned");
                std::string why;
                auto const tag = Text(a, "tag");
                auto const actor = Match(loaded, spawned, tag, Text(a, "path"), Text(a, "class"), why);
                // A level actor not loaded waits; so does a spawn that more than one loaded actor has the tag of.
                if (actor == nullptr && (!spawned || !loaded.Tagged(tag).empty()))
                {
                    entry.Why = why;
                    continue;
                }
                auto builtHidden = false;
                auto const verdict = Judge(a, actor, why, builtHidden);
                entry.Done = true;
                if (verdict == Verdict::Drop)
                {
                    LEASI_INFO("editor: session entry {} dropped: {}", entry.Key, why);
                    dropped.push_back(a);
                    continue;
                }

                // Applied: an edit from now on, carrying the entry's fields unchanged.
                auto const pose = PoseFrom(a, actor != nullptr ? PoseOf(actor) : Pose{});
                auto const deleted = Flag(a, "deleted");
                auto& e = G.Edits.emplace_back(EditFrom(a));
                e.Now = pose;
                e.SavedPose = pose;
                if (auto const base = a.find("base"); base == a.end())
                {
                    // Version 1: the base is the actor before applying, which is what the YAML built (a spawn spawned again takes
                    // its built copy's when that shows up: TakeOver).
                    if (actor != nullptr) TakeBase(e, actor);
                }
                else if (base->is_object())
                {
                    e.Base = PoseFrom(*base, actor != nullptr ? PoseOf(actor) : pose);
                }
                if (actor != nullptr)
                {
                    // Judged against its actor: the edit no longer is the entry for a later pass.
                    e.Source = nullptr;
                    Bind(e, actor);
                    loaded.Taken.insert(actor);
                    e.OnCopy = spawned;
                    if (builtHidden) TakeBuiltHidden(e);
                    Put(e, pose, deleted);
                    applied++;
                    LEASI_INFO("editor: re-applied {} to {}", entry.Key, ToUtf8(PathOf(actor)));
                }
                else if (deleted)
                {
                    // A deleted spawn with no actor: kept as a tombstone (written again as deleted if saved), until a built copy shows.
                    e.Deleted = true;
                    e.Rebindable = true;
                }
                else
                {
                    e.Pending = true;
                    e.PendingWhy = "to be spawned again";
                    respawn.push_back(G.Edits.size() - 1);
                }
            }
            std::erase_if(G.Entries, [](Entry const& entry) { return entry.Done; });

            auto const spawned = Respawn(pc, respawn);
            if (!dropped.empty()) DropEntries(G.SessionMap, dropped);
            G.Applied += applied + spawned + rebound;
            G.Dropped += static_cast<int>(dropped.size());
            // Paused, a prop spawned again has no light yet, and one moved keeps the light of where it was.
            if (applied + spawned + rebound > 0) TickOnce();

            auto const waitingCount = G.Entries.size()
                + static_cast<size_t>(std::count_if(G.Edits.begin(), G.Edits.end(), [](Edit const& e) { return e.Map == G.SessionMap && e.Pending; }));
            if (applied + spawned + rebound > 0 || !dropped.empty())
            {
                auto text = std::format("session re-applied: {} applied", applied + spawned + rebound);
                if (spawned > 0) text += std::format(" ({} spawned again)", spawned);
                if (rebound > 0) text += std::format(" ({} now on their built copy)", rebound);
                if (!dropped.empty()) text += std::format(", {} dropped (installed, or built from other YAML since)", dropped.size());
                if (waitingCount > 0) text += std::format(", {} waiting for their actor", waitingCount);
                Say(text);
            }
            else
            {
                LEASI_INFO("editor: re-apply pass over {} levels: nothing applied, {} waiting", G.Levels.size(), waitingCount);
            }
        }

        void PassEntry(UObject* pc)
        {
            try
            {
                Pass(static_cast<APlayerController*>(pc));
            }
            catch (std::exception const& e)
            {
                G.PassFaulted = true;
                LEASI_ERROR("editor: re-apply pass: {}", e.what());
                Say(std::format("the session was not re-applied: {} (tried again when the loaded levels change)", e.what()));
            }
        }

        /// Entries waiting, edits waiting for their actor, or spawns a built copy may still take over.
        bool PassNeeded()
        {
            if (std::any_of(G.Entries.begin(), G.Entries.end(), [](Entry const& entry) { return !entry.Done; })) return true;
            return std::any_of(G.Edits.begin(), G.Edits.end(), [](Edit const& e) { return e.Map == G.SessionMap && (e.Pending || e.Rebindable); });
        }

        /// A pass that faults is not retried until the loaded levels change again.
        void RunPass(APlayerController* pc)
        {
            if (!Guarded(PassEntry, pc))
            {
                G.PassFaulted = true;
                LEASI_ERROR("editor: access violation in a re-apply pass; not tried again until the loaded levels change");
                Say("the session was not re-applied: access violation (tried again when the loaded levels change)");
            }
            G.SummaryStale = true;
        }

        /// A map change or a load (docs/07 §10). Every edit lets go of its actor (the old world's actors are gone, and a new actor
        /// in a reused slot would take its moves); the unsaved ones are saved from their stored state; the edits of each map
        /// written are cleared, those of a map whose file could not be written stay, detached and unsaved, for the next Save to
        /// retry; undo starts empty; and the new map's session waits for the first pass.
        void ChangeWorld(APlayerController* pc)
        {
            if (G.World != nullptr)
            {
                if (G.Open) LEASI_INFO("editor: the world changed, editor closed");
                SetOpen(pc, false);
                Select(nullptr);
                G.LastPick.clear();
                // The meshes in memory changed.
                G.Meshes.reset();
                G.MeshFiles.clear();
                for (auto& e : G.Edits)
                {
                    Detach(e);
                    e.Pending = e.Rebindable = false;
                }
                auto const saved = SaveAll();
                std::erase_if(G.Edits, [&](Edit const& e) { return !saved.Failed.contains(e.Map); });
                G.Undo.clear();
                G.Redo.clear();
                G.Drag.reset();
                if (!saved.Problems.empty())
                {
                    std::string text = "the map changed: ";
                    for (size_t i = 0; i < saved.Problems.size(); i++) text += (i > 0 ? "; " : "") + saved.Problems[i];
                    if (!saved.Failed.empty()) text += ". Those edits are kept, and the next Save tries again";
                    Say(text);
                }
                else if (saved.Count > 0 && !saved.Written.empty())
                {
                    Say(std::format("the map changed: saved {} edit{} to {}", saved.Count, saved.Count == 1 ? "" : "s", saved.Written.front()));
                }
            }
            G.World = pc->WorldInfo;
            G.WorldIndex = pc->WorldInfo->ObjectInternalInteger;
            G.Levels = ScanLevels(pc->WorldInfo);
            G.LevelsChangedAt = GetTickCount64();
            G.PassFaulted = false;
            G.PassWanted = false;
            G.PassNow = true;
            ReadSession(ToUtf8(MapName()));
        }

        void Run(APlayerController* pc, UCanvas* canvas, Command const& c)
        {
            // A drag's step closes before anything else runs: an undo or a delete mid-drag acts on the drag as a whole.
            auto const drag = c.Type == Command::Kind::Move || c.Type == Command::Kind::Rotate || c.Type == Command::Kind::Scale;
            if (!drag) CloseDrag();
            switch (c.Type)
            {
            case Command::Kind::Close: SetOpen(pc, false); break;
            case Command::Kind::FreeCam: SetFreeCam(pc, c.On); break;
            case Command::Kind::Pick: Pick(pc, canvas, c.X, c.Y); break;
            case Command::Kind::Move: Move({ c.X, c.Y, c.Z }); break;
            case Command::Kind::Rotate: Rotate(c.Rotation); break;
            case Command::Kind::Scale: Rescale({ c.X, c.Y, c.Z }); break;
            case Command::Kind::Deselect: Select(nullptr); break;
            case Command::Kind::Speed: G.Speed = std::clamp(G.Speed * std::pow(1.25f, c.X), MinSpeed, MaxSpeed); break;
            case Command::Kind::Spawn: Spawn(pc, canvas, c); break;
            case Command::Kind::Delete: Delete(); break;
            case Command::Kind::Save: Save(); break;
            case Command::Kind::RefreshMeshes: RefreshMeshes(); break;
            case Command::Kind::Undo: UndoStep(false); break;
            case Command::Kind::Redo: UndoStep(true); break;
            // Its step closed above. Paused, the prop keeps the light of where it was until the world ticks.
            case Command::Kind::EndDrag: TickOnce(); break;
            }
        }

        std::shared_ptr<SessionState const> Summarize()
        {
            auto state = std::make_shared<SessionState>();
            state->File = G.SessionFile;
            state->Error = G.SessionError;
            state->Applied = G.Applied;
            state->Dropped = G.Dropped;
            for (auto const& entry : G.Entries)
            {
                if (!entry.Done) state->Pending.push_back(std::format("{}: {}", entry.Key, entry.Why));
            }
            for (auto const& e : G.Edits)
            {
                if (e.Map == G.SessionMap && e.Pending) state->Pending.push_back(std::format("{}: {}", KeyOf(e), e.PendingWhy));
            }
            state->Levels = G.PassLevels;
            state->PassFrame = G.PassFrame;
            return state;
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
                auto const edit = EditOf(actor);
                s.Selected = true;
                s.SelectedLocation = actor->Location;
                s.SelectedRotation = actor->Rotation;
                s.SelectedDrawScale = actor->DrawScale;
                s.SelectedDrawScale3D = actor->DrawScale3D;
                s.SelectedPawn = actor->IsA(APawn::StaticClass());
                s.SelectedStatic = actor->bStatic;
                s.SelectedPath = ToUtf8(PathOf(actor));
                s.SelectedClass = ToUtf8(NameOf(actor->Class));
                s.SelectedTag = NameText(actor->Tag);
                s.SelectedSpawned = edit != nullptr && edit->Spawned;
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
            // What a Save would write: a spawn deleted before its first save leaves nothing.
            s.Unsaved = static_cast<int>(std::count_if(G.Edits.begin(), G.Edits.end(), [](Edit const& e) { return !e.Saved && !LeavesNothing(e); }));
            s.SaveProblem = G.SaveProblem;
            s.Status = G.Status;
            s.StatusFrame = G.StatusFrame;
            if (!G.Undo.empty()) s.UndoLabel = Label(G.Undo.back());
            if (!G.Redo.empty()) s.RedoLabel = Label(G.Redo.back());
            if (G.SummaryStale || G.Summary == nullptr)
            {
                G.Summary = Summarize();
                G.SummaryStale = false;
            }
            s.Session = G.Summary;
            std::scoped_lock lock{ SharedLock };
            Shared = std::move(s);
        }

        void Tick(ABioHUD* hud)
        {
            Diag.PostRenderThread.store(GetCurrentThreadId(), std::memory_order_relaxed);
            auto const pc = hud->PlayerOwner;
            auto const canvas = hud->Canvas;
            if (pc == nullptr || canvas == nullptr || pc->WorldInfo == nullptr) return;
            G.Frame++;
            ResolveFunctions();

            // A failure closed the editor without touching game memory: give the pause back now, from a known world.
            if (!G.Open && G.PausedWorld != nullptr) RestorePause();

            // A map change or a load: the camera, the selection, the paused world and every edit's actor belonged to the old one.
            if (pc->WorldInfo != G.World || pc->WorldInfo->ObjectInternalInteger != G.WorldIndex) ChangeWorld(pc);
            else if (auto levels = ScanLevels(pc->WorldInfo); levels != G.Levels) LevelsChanged(std::move(levels));

            // The game pauses this way too (seen while it was unfocused) and unpauses when it is done: the editor keeps its pause.
            if (G.Open && G.PausedWorld != nullptr && G.PausedWorld == pc->WorldInfo)
            {
                // Our own one-tick release (a spawn, the end of a drag, an undo, a pass) is over: not the game's doing.
                auto const ownTick = std::exchange(G.TickingOnce, false);
                if (!G.PausedWorld->bPlayersOnly)
                {
                    G.PausedWorld->bPlayersOnly = 1;
                    if (!ownTick)
                    {
                        // The game wrote 0: whatever pause it had when the editor opened is over, so closing gives back 0.
                        if (G.WasPlayersOnly) LEASI_INFO("editor: the game ended its own pause while the editor was open; closing will give back 0");
                        G.WasPlayersOnly = false;
                        if (G.PauseRetaken++ == 0) LEASI_INFO("editor: the game cleared bPlayersOnly while the editor was open; set again");
                    }
                }
            }

            // Once on the first tick after a load, then when the loaded levels changed and stayed the same a moment, open or not;
            // not while a level is still being added to the world or taken from it, unless it stays so. After the pause is taken
            // back above: the world tick a pass lets through (TickOnce) is taken back at the next tick, not this one.
            auto const quiet = GetTickCount64() - G.LevelsChangedAt;
            auto const steady = quiet >= UnsettledMs || std::all_of(G.Levels.begin(), G.Levels.end(), [](LevelSeen const& l) { return l.Settled; });
            if (steady && (G.PassNow || (G.PassWanted && quiet >= SettleMs)))
            {
                G.PassNow = G.PassWanted = false;
                if (!G.PassFaulted && PassNeeded()) RunPass(pc);
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

        /// After an access violation: close without touching game memory. The drag step closes from the edit's stored state; the
        /// pause is given back at the next tick (G.PausedWorld stays set for it), the cursor right away.
        void Fail()
        {
            LEASI_ERROR("editor: access violation on the game thread, editor closed");
            CloseDrag();
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
        // Drags send one move, turn or scale per frame: only the latest matters.
        auto const drag = command.Type == Command::Kind::Move || command.Type == Command::Kind::Rotate || command.Type == Command::Kind::Scale;
        if (drag && !Commands.empty() && Commands.back().Type == command.Type) Commands.back() = command;
        else if (command.Type == Command::Kind::Spawn)
        {
            // A double click on a list row or a catalog cell is two clicks: one crate, not two.
            static std::string lastKey;
            static ULONGLONG lastTime = 0;
            auto const key = command.Text + '\n' + command.Load;
            auto const now = GetTickCount64();
            if (key == lastKey && now - lastTime < GetDoubleClickTime()) return;
            lastKey = key;
            lastTime = now;
            Commands.push_back(command);
        }
        else Commands.push_back(command);
    }

    std::wstring DataFolder()
    {
        wchar_t buffer[MAX_PATH];
        auto const length = GetEnvironmentVariableW(L"LOCALAPPDATA", buffer, MAX_PATH);
        if (length == 0 || length >= MAX_PATH) throw BridgeError("LOCALAPPDATA is not set");
        return (std::filesystem::path(buffer) / L"MeleBridge" / L"LE1").wstring();
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
        auto orNull = [](std::string const& text) { return text.empty() ? json(nullptr) : json(text); };
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
            { "edits", { { "count", s.Edits }, { "unsaved", s.Unsaved }, { "saveProblem", orNull(s.SaveProblem) } } },
            { "undo", { { "undo", orNull(s.UndoLabel) }, { "redo", orNull(s.RedoLabel) } } },
            { "session", nullptr },
            { "meshesInMemory", s.Meshes != nullptr ? json(s.Meshes->size()) : json(nullptr) },
            { "catalog", { { "entries", Diag.CatalogEntries.load() >= 0 ? json(Diag.CatalogEntries.load()) : json(nullptr) },
                           { "thumbnails", Diag.Thumbnails.load() } } },
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
        if (s.Session != nullptr)
        {
            auto const& session = *s.Session;
            state["session"] = { { "file", orNull(session.File) }, { "error", orNull(session.Error) }, { "applied", session.Applied },
                                 { "dropped", session.Dropped }, { "pending", session.Pending }, { "levels", session.Levels },
                                 { "passFrame", session.PassFrame } };
        }
        if (s.Selected)
        {
            state["selection"] = { { "path", s.SelectedPath }, { "class", s.SelectedClass }, { "tag", s.SelectedTag },
                                   { "location", vector(s.SelectedLocation) },
                                   { "rotation", json::array({ s.SelectedRotation.Pitch, s.SelectedRotation.Yaw, s.SelectedRotation.Roll }) },
                                   { "drawScale", s.SelectedDrawScale }, { "drawScale3D", vector(s.SelectedDrawScale3D) },
                                   { "pawn", s.SelectedPawn }, { "static", s.SelectedStatic }, { "spawned", s.SelectedSpawned },
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
