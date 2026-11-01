#include "MeleBridge/Bridge.hpp"

#if MELEBRIDGE_EDITOR

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
// mesh in memory, or one loaded from the catalog package) and deleting (hiding) actors, which vanilla actors it may change
// (levels.json says which levels are ours, Kismet which ones the story holds), lights (spawned, moved, set, switched off; ours
// through their own writer, our NPCs' through the kept one, as offsets their NPC carries; the levels' own lights, components of
// their static light collections, through the kept one too, one light at a time) and the prop lighting switch, effects (moved,
// turned, hidden, then run for a moment), undo and redo, saving those edits to a session file and re-applying that file when a
// map loads, and the snapshot the overlay draws from. Everything here runs inside the ProcessEvent hook, right after
// BioHUD.PostRender or Camera.UpdateCamera returned.

// The SDK's C++ layouts must match the engine's offsets (the SDK's own comments) for every field read here directly. Not all
// do: LE1's FRwVector3 is 12 bytes in C++ and 16 in the engine, so ParticleSystem (it holds an FBioRwBox) has every later field
// 8 bytes off, and ParticleSystemComponent's Template and bAutoActivate are off too. Those are read where the engine put them
// (TemplateOf, AutoActivates, EmittersOf); the ones below are checked to match.
static_assert(offsetof(AEmitter, ParticleSystemComponent) == 0x284);
static_assert(offsetof(USeqVar_Object, ObjValue) == 0xAC);
static_assert(offsetof(USeqVar_ObjectList, ObjList) == 0xB4);
static_assert(offsetof(UParticleEmitter, LODLevels) == 0x70);
static_assert(offsetof(UParticleLODLevel, RequiredModule) == 0xE8);
static_assert(offsetof(UParticleModuleRequired, EmitterLoops) == 0xC4);
// A level light's place (Phase 5b): the SDK pads PointLightComponent with a "missed offset" before these.
static_assert(offsetof(UActorComponent, Owner) == 0x78);
static_assert(offsetof(ULightComponent, LightToWorld) == 0xD0);
static_assert(offsetof(ULightComponent, LightingChannels) == 0x194);
static_assert(offsetof(UPointLightComponent, CachedParentToWorld) == 0x210);
static_assert(offsetof(UPointLightComponent, Translation) == 0x260);
static_assert(offsetof(UPointLightComponent, Radius) == 0x280);
static_assert(sizeof(FMatrix) == 0x40);

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
        /// The session formats Save writes (docs/design/in-game-editor.md §12): version 3 when an entry (its base, imported.before or installed
        /// included) holds an npc or vanilla light, which a Phase 4 bridge would rewrite with its own light writer, channels and
        /// shadows included; else 2. The bridge reads version 1 (Phase 1's) to 3.
        constexpr int SessionVersion = 2, KeptLightVersion = 3;
        /// After an effect step the paused world runs this long (TickFor), so the effect's particles show where it is now.
        constexpr ULONGLONG EffectRunMs = 1500;
        /// After a step of a baked or character-only level light, the light environments in its reach are reset and the paused
        /// world runs this long (docs/design/in-game-editor.md §12, spike S2): an environment updates only in Tick, fully at most once a second
        /// (MinTimeBetweenFullUpdates 1.0 on a mod NPC's), and blends over 0.3 s.
        constexpr ULONGLONG LightRunMs = 1500;
        /// An environment is reset when its owner stands within the light's radius plus this (a pawn's own size).
        constexpr float EnvironmentReach = 100;
        /// An untouched level light is listed within this of the view (30 m); the overlay draws at most 150 of them.
        constexpr float LevelLightReach = 3000;
        /// A level light whose collection's matrix scales it by more than this is not moved (docs/design/in-game-editor.md §12): its Translation would
        /// be scaled too. None in the freighter or its rig (spike S0).
        constexpr float MaxParentScale = 1e-3f;
        /// levels.json is checked for a new write time this often while the editor is open.
        constexpr ULONGLONG LevelsCheckMs = 1000;
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

        /// FLightingChannelContainer's bits (Engine_structs.hpp): bInitialized, BSP, Static, Dynamic, CompositeDynamic, then the
        /// named channels; Gameplay_4 (the SDK's Gameplay03) is free in every scanned level (docs/design/in-game-editor.md §11).
        constexpr DWORD ChannelInitialized = 0x1, ChannelBsp = 0x2, ChannelStatic = 0x4, ChannelDynamic = 0x8, ChannelCompositeDynamic = 0x10;
        constexpr DWORD ChannelGameplay4 = 0x02000000;
        constexpr DWORD RoomChannels = ChannelBsp | ChannelStatic | ChannelDynamic | ChannelCompositeDynamic;
        /// A covered prop's mesh: lit by prop lights (and room lights) only.
        constexpr DWORD CoveredMeshChannels = ChannelInitialized | ChannelGameplay4;
        /// A light covers a prop when its falloff at the prop's bounds centre is at least this (d <= 0.71 R at falloff 2).
        constexpr float CoverShare = 0.25f;
        /// Auto light adds a light when a fresh spawn gets less than this share of its target from prop lights; a prop drag that
        /// leaves it under the other is said on the status line.
        constexpr float AutoLightShare = 0.6f;
        constexpr float DragWarnShare = 0.5f;
        /// Auto light's radius is 2.5 bounds radii, never under this (nor over MaxAutoRadius: a prop that needs more gets none).
        constexpr float MinAutoRadius = 120;
        /// Nor under its distance to the prop's bounds centre over this: (1 - 0.65^2)^2 = 0.33 there at falloff 2, so the light
        /// covers its prop. A turned or tilted prop's world box grows and its sphere doesn't, so its light may stand farther out
        /// than 2.5 radii cover.
        constexpr float AutoReach = 0.65f;
        /// A prop light's brightness and colour with no prop light to take them from: one measurement (docs/design/in-game-editor.md §11 spike: radius
        /// 350, about 210 units above the vanilla crate's pivot, 1.01 of its brightness), not a calibration.
        constexpr float SeedBrightness = 0.15f;
        /// A seed whose brightness times luminance is under this gives nothing (brightness 0, or black): a new prop light never
        /// takes it, since it would cover its prop and leave it black.
        constexpr float DarkSeed = 1e-3f;
        /// The panel's ranges, which a Light command is held to.
        constexpr float MinLightRadius = 16, MaxLightRadius = 4096, MaxBrightness = 10, MinFalloff = 1, MaxFalloff = 8;
        static_assert(MinAutoRadius <= MaxAutoRadius && MaxAutoRadius <= MaxLightRadius, "an Auto light's radius is one the panel can set");
        /// The Lights tab's buttons: a light this high over where the view points. A prop light the spike's radius (350); a room
        /// light wider and brighter, a start to tune from.
        constexpr float PropLightHeight = 150, PropLightRadius = 350;
        constexpr float RoomLightHeight = 250, RoomLightRadius = 600, RoomLightBrightness = 0.5f;
        /// A mesh's channels when neither the switch nor its archetype has better: what the engine gives a dynamic mesh.
        constexpr DWORD DynamicMeshChannels = ChannelInitialized | ChannelDynamic | ChannelCompositeDynamic;
        /// An NPC light (docs/design/in-game-editor.md §12) has a state when it stands within this of the state's location on each axis: offsets
        /// written to 2 decimals move a built light by up to 0.007 per axis, and a float at the Normandy's heights steps by 0.004.
        constexpr float NpcLightNoise = 0.05f;
        /// The pose check: an NPC's pawn with no edit stands within this many units and degrees of its built pose, or its lights
        /// are not edited (their offsets are measured from that pose).
        constexpr float MaxNpcDrift = 5, MaxNpcTurn = 2;
        /// Add light's light: offset [forward, right, up] (60, 0, 80), radius and brightness of a mod NPC's face light.
        constexpr float NpcLightForward = 60, NpcLightUp = 80, NpcLightRadius = 100, NpcLightBrightness = 1.2f;

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
            UFunction* SetEnvironmentEnabled = nullptr;
            UFunction* KillParticlesForced = nullptr;
            UFunction* ActivateSystem = nullptr;
            UFunction* SetTranslation = nullptr;
            UFunction* ForceUpdate = nullptr;
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

        /// What a session entry holds of an actor's state, as last written to the session file or read from it: a field still at
        /// it was not changed in game since, and Save takes the file's value for it, which an import may have moved on.
        struct EntryState
        {
            Pose Transform;
            std::optional<LightBlock> Light;
            bool Deleted = false;
            /// An NPC light's offset (NpcLink).
            std::optional<FVector> Offset;
        };

        /// An NPC light's link to its NPC (docs/design/in-game-editor.md §12): the pawn's tag, and the light's offset from the NPC's reference pose as
        /// [forward, right, up] in X, Y and Z, turned by the NPC's yaw.
        struct NpcLink
        {
            std::string Tag;
            FVector Offset{};
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
            /// A light's settings (docs/design/in-game-editor.md §11); none for anything else. Of a light's pose only the location counts. Its on/off is
            /// Light->Enabled: Deleted is only a spawned light the editor deleted, and the actor shows Enabled && !Deleted. Its
            /// kind is its source's (§12), read once: Npc and Vanilla lights go through the kept writer (ApplyLight).
            std::optional<LightBlock> Light;
            /// An NPC light's NPC and offset (§12); none for any other actor. The location stays absolute: the reference pose
            /// plus the offset turned by its yaw.
            std::optional<NpcLink> Npc;
            /// An effect (an Emitter, §12): a step of it kills and restarts its particles, then the world runs a moment (Rerun).
            bool Effect = false;
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
            /// A light's base light block: what the YAML's lights: entry holds.
            std::optional<LightBlock> BaseLight;
            /// An NPC light's base offset: what the YAML's npcs.<name>.lights item holds (levels.json's, for a built one).
            std::optional<FVector> BaseOffset;
            /// The states the installed level may still show of it while an import's install is pending: imported.before of each
            /// import since the editor last made the entry anew (an import keeps one, Save carries them over as installed). Save
            /// leaves imported out (a saved edit is a new state), so the next import's before is the base, and one of these builds
            /// may be the installed one.
            json Installed = json::array();
            /// The session entry the edit was made from, while the edit is still that entry (saved, not changed since): a built
            /// copy that turns up later is judged against it as the entry would have been. Cleared once judged against an actor,
            /// or once saved.
            json Source;
            /// The state last written to the session file or read from it (pose, light block, deleted).
            std::optional<EntryState> SavedState;
            HideFlags ShownFlags;
            /// The room-shell test's measure (docs/design/in-game-editor.md §11): the largest half-extent of its primitives' world boxes with the actor
            /// as its level file has it, taken when the edit bound it, before anything wrote to it. None when its level was not
            /// visible then (its components not attached: no bounds yet).
            std::optional<float> FileExtent;
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
            /// A light Auto light made for a fresh spawn: that prop's edit (docs/design/in-game-editor.md §11's link). It holds while both were never saved
            /// and the light is unedited (a tuned light is the user's own and never goes with a prop); meanwhile a drag of the prop
            /// takes the light along (Follow).
            std::optional<size_t> MadeFor;
            /// A linked auto light Follow deleted, as its prop got too big for one prop light from any side, or an undo or redo
            /// of that drag: a later drag of the prop takes it along again, and brings it back once the prop fits. A light the
            /// user deleted stays deleted.
            bool DragDeleted = false;
            /// A light of a level's static light collection (docs/design/in-game-editor.md §12, 5b): the edit's target is that component (Comp, its slot
            /// and class), and Actor is its collection, which nothing here ever writes (the hard guard). Component stays set while
            /// the edit waits for its level. Its collection's object name and its light type (B, C, U, D; 0 unknown) go into its
            /// session entry. Its path is the component's; it has no tag, a rotation of zeros and scales of 1.
            UPointLightComponent* Comp = nullptr;
            int CompIndex = -1;
            UClass* CompClass = nullptr;
            bool Component = false;
            std::string Owner;
            char Type = 0;
            /// Where it stood, and its radius, at its last light-environment reset (Reset): the next one resets the environments
            /// there too, around its old place.
            std::optional<std::pair<FVector, float>> ResetFrom;
        };

        /// An edit's state as an undo step holds it: shown or not, the three flags it has when shown, the transform, a light's
        /// settings, and an NPC light's offset.
        struct StepState
        {
            bool Shown = true;
            HideFlags ShownFlags;
            Pose Transform;
            std::optional<LightBlock> Light;
            std::optional<FVector> Offset;
        };

        /// Light: a change of a light's settings (the panel, or a built light switched off by Delete).
        enum class StepKind { Spawn, Delete, Move, Turn, Scale, Light };

        /// An NPC light a move or a turn of its NPC carried (docs/design/in-game-editor.md §12): the actor as an edit keeps it (index and class), its
        /// offset, and where it stood before the step and after it. After is the NPC's pose after the step plus the offset turned
        /// by its yaw, computed from the pawn's edit (CloseDrag reads no game memory). A light with an edit is written through
        /// it, its offset kept; one without gets no edit (the build places it from the NPC's pose).
        struct CarriedLight
        {
            AActor* Actor = nullptr;
            int Index = -1;
            UClass* Class = nullptr;
            FVector Offset{};
            FVector Before{};
            FVector After{};
        };

        /// One undo step: a spawn, a delete, a drag or a light change.
        struct Step
        {
            /// The edit's index: edits are erased only on a map change, which clears the steps too.
            size_t Target = 0;
            StepKind Kind = StepKind::Move;
            StepState Before;
            StepState After;
            /// The auto light that goes with a spawn or a delete of its prop (docs/design/in-game-editor.md §11's link), written with it while the link
            /// still holds; or the one that followed a drag of its prop, written with it whenever it has its actor (AlsoOf).
            std::optional<size_t> Also;
            StepState AlsoBefore;
            StepState AlsoAfter;
            /// The lights a move or a turn of one of our NPCs carried (§12).
            std::vector<CarriedLight> Carried;
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

        /// A light of an NPC of ours as the last build placed it (levels.json npcs, docs/design/in-game-editor.md §12): its tag as the file has it, and
        /// its offset [forward, right, up] (X, Y, Z) from the NPC's Location, turned by its yaw.
        struct BuiltNpcLight
        {
            std::string Tag;
            FVector Offset{};
        };

        /// An NPC of one of our levels as the last build placed it (levels.json npcs): the level YAML's key ("guard"), the pawn's
        /// tag as the file has it, its built Location (the collision centre) and yaw (65536 = 360°), its lights, possibly none,
        /// and the level it is built into, as levels.json names it. Held by index or looked up again, never by pointer across a
        /// tick: CheckLevels reads the file again.
        struct BuiltNpc
        {
            std::string Name;
            std::string Tag;
            FVector Location{};
            int Yaw = 0;
            std::vector<BuiltNpcLight> Lights;
            std::string Level;
        };

        /// levels.json (docs/design/in-game-editor.md §11, §12), read on each map change and whenever its write time changes (CheckLevels): the level
        /// files built from our YAMLs, the vanilla files each streams, and the NPCs each has. Without it, every actor keeps Phase
        /// 3's rules.
        struct LevelsFile
        {
            bool Read = false;
            /// Why it was not read, as the status line says it.
            std::string Problem;
            /// The file's write time when it was read; none when it was missing.
            std::optional<std::filesystem::file_time_type> WriteTime;
            /// The DLC folder our level files lie in; empty when the file doesn't say.
            std::string Dlc;
            /// The vanilla files each level streams, by the level's lower-case name.
            std::unordered_map<std::string, std::vector<std::string>> Streams;
            /// The tags of its built props (the editor switches them to prop lights) and of its lights: entries (the editor lists
            /// them), lower-case, by the level's lower-case name.
            std::unordered_map<std::string, std::unordered_set<std::string>> Props;
            std::unordered_map<std::string, std::unordered_set<std::string>> Lights;
            /// Its NPCs, by the level's lower-case name (the level the pawn is built into), in the file's order of tags.
            std::unordered_map<std::string, std::vector<BuiltNpc>> Npcs;
            /// Its NPCs' lights by lower-case tag, by the level's lower-case name: the NPC's index in Npcs and the light's in its
            /// Lights.
            std::unordered_map<std::string, std::unordered_map<std::string, std::pair<size_t, size_t>>> NpcLights;
            /// Its characterLights copies (Phase 5b): the StaticLightCollectionActor's lower-case object name, and the vanilla file
            /// it was copied from, by the level's lower-case name.
            std::unordered_map<std::string, std::unordered_map<std::string, std::string>> CharacterLights;
            std::shared_ptr<LevelsState const> Summary;
        };

        /// Where a tracked light comes from (docs/design/in-game-editor.md §12): ours (kind Props or Room by its channels), an NPC's, or a level's.
        enum class LightSource { Ours, Npc, Vanilla };

        /// An actor the props switch, the light lists, the effect list or the NPC list track, as long as its slot holds it. A level
        /// light (5b) is its collection (Actor) and its component (Comp, its slot and class), with its light type, its
        /// collection's object name (Owner), and, for one of the map's characterLights copies (the rig), the vanilla file it was
        /// copied from (Rig).
        struct Tracked
        {
            AActor* Actor = nullptr;
            int Index = -1;
            UClass* Class = nullptr;
            /// Its path, read once when tracked: the icons and the lists name it every frame.
            std::string Path;
            /// A light's source; an NPC light's NPC, the pawn's tag as levels.json has it (and an NPC's pawn's own tag so).
            LightSource Source = LightSource::Ours;
            std::string Npc;
            /// An effect's object name, its template's name and its level file (a fork's vanilla file), whether it stands in a
            /// fork, and why the editor leaves it alone (Refusal; empty when it edits it), read once. An NPC's pawn's level file.
            std::string Name;
            std::string Template;
            std::string Level;
            bool Forked = false;
            std::string Refusal;
            UPointLightComponent* Comp = nullptr;
            int CompIndex = -1;
            UClass* CompClass = nullptr;
            char Type = 0;
            std::string Owner;
            std::string Rig;
        };

        /// A light as the prop lighting rule sees it (docs/design/in-game-editor.md §11): where, its settings as the component has them (on is bEnabled),
        /// and whether its channels include Gameplay_4.
        struct LightSeen
        {
            AActor* Actor = nullptr;
            FVector Location{};
            LightBlock Light;
            bool Gameplay4 = false;
        };

        /// A prop's bounds: the box of its shown meshes and the radius of the sphere around them.
        struct PropBounds
        {
            FVector Centre{};
            FVector Extent{};
            float Radius = 0;
        };

        /// Where Auto light and L put a prop light, and its radius.
        struct Placement
        {
            FVector Location{};
            float Radius = 0;
        };

        /// The brightness and colour a new prop light takes.
        struct Seed
        {
            float Brightness = SeedBrightness;
            std::array<int, 3> Color{ 255, 255, 255 };
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
            /// We let the paused world run until then (TickFor, GetTickCount64 time; 0: not running): the pause is taken back once
            /// it is over, not counted as the game's either.
            ULONGLONG RunUntil = 0;
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
            /// A selected level light (docs/design/in-game-editor.md §12, 5b): the component, its slot and class; Selected is then its collection.
            UPointLightComponent* SelectedComp = nullptr;
            int SelectedCompIndex = -1;
            UClass* SelectedCompClass = nullptr;
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
            LevelsFile Ours;
            /// When levels.json's write time was last checked (CheckLevels).
            ULONGLONG LevelsCheckedAt = 0;
            /// The actors the loaded levels' Kismet names (docs/design/in-game-editor.md §12), with their slot and the variable that names them
            /// ("Sequence_1791.SeqVar_Object_0"). Read again when first needed after a map change or a level turned visible.
            std::unordered_map<UObject*, std::pair<int, std::string>> Kismet;
            bool KismetStale = true;

            /// Our listed lights, our NPCs' lights (docs/design/in-game-editor.md §12: apart, so the props switch never walks them), the switched props
            /// and the effects of the loaded levels (§11, §12), read again when stale: after a map change, a change of the loaded
            /// levels, a pass, a spawn, a new levels.json.
            std::vector<Tracked> Lights;
            std::vector<Tracked> OtherLights;
            std::vector<Tracked> Props;
            std::vector<Tracked> Effects;
            /// Our NPCs' pawns in the loaded levels (levels.json npcs): the pose check reads them (§12).
            std::vector<Tracked> Pawns;
            /// The point and spot lights of the loaded levels' static light collections (5b), the map's characterLights copies
            /// included.
            std::vector<Tracked> LevelLights;
            bool TrackedStale = true;
            /// Level lights whose light environments are to be reset at the end of the tick, by edit index (Reset). Set where game
            /// memory may not be touched (CloseDrag runs in Fail()).
            std::set<size_t> Resets;
            /// The selected U light's special channels and the actors on them (the panel's banner), and the light they were
            /// read for: read once per selection.
            UObject* ChannelsOf = nullptr;
            int ChannelsIndex = -1;
            std::string Channels;
            std::string ChannelActors;
            /// Add light said once in this game run that a YAML with its own lightTemplate may build another look (§12).
            bool TemplateNoted = false;
            /// Effects whose particles are to be killed and started again at the end of the tick, by edit index (Rerun). Set where
            /// game memory may not be touched (CloseDrag runs in Fail()).
            std::set<size_t> Rerun;
            /// Props whose switch is to be checked at the end of the tick, by edit index, or all of them. Set where game memory
            /// may not be touched (CloseDrag runs in Fail()), done by Reswitch().
            std::set<size_t> Reswitch;
            bool ReswitchAll = false;
            /// A prop drag's share of its target before the drag (-1: not a switched prop), and the closed drag's, for the warning.
            float DragShare = -1;
            std::optional<std::pair<size_t, float>> DragWarn;
            /// The side of its prop (horizontal, unit length) the followed light stood on when the drag began: Follow's place for
            /// it when the view now needs a radius over MaxAutoRadius. Zero when none.
            FVector DragSide{};
            /// A mesh component's channels before the switch first covered it, by component, while its slot holds it: uncovered,
            /// they come back.
            std::unordered_map<UObject*, std::pair<int, DWORD>> MeshChannels;
            /// The brightness and colour of the last prop light edited in this map; reset on a map change.
            std::optional<Seed> LastSeed;
            bool AutoLight = true;
            /// Where the view is at this tick (the free cam's, else the game camera's): Auto light places toward it.
            FVector ViewLocation{};
            int ViewYaw = 0;
            /// The props switch faulted: not tried again until the loaded levels change.
            bool ReswitchFaulted = false;
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
            return Alive(G.Selected, G.SelectedIndex) && G.Selected->Class == G.SelectedClass
                && (G.SelectedComp == nullptr || (Alive(G.SelectedComp, G.SelectedCompIndex) && G.SelectedComp->Class == G.SelectedCompClass));
        }

        void Select(AActor* actor)
        {
            G.Selected = actor;
            G.SelectedIndex = actor != nullptr ? actor->ObjectInternalInteger : -1;
            G.SelectedClass = actor != nullptr ? actor->Class : nullptr;
            G.SelectedComp = nullptr;
            G.SelectedCompIndex = -1;
            G.SelectedCompClass = nullptr;
        }

        /// A level light (docs/design/in-game-editor.md §12, 5b): its collection is the selected actor, the component the light.
        void SelectLight(AActor* owner, UPointLightComponent* light)
        {
            Select(owner);
            G.SelectedComp = light;
            G.SelectedCompIndex = light->ObjectInternalInteger;
            G.SelectedCompClass = light->Class;
        }

        /// A tag that names one actor: not empty, not None and not the class name (an actor's tag is its class name by default).
        bool RealTag(std::string const& tag, std::string const& className)
        {
            return !tag.empty() && !SameText(tag, "None") && !SameText(tag, className);
        }

        // ! Forks
        // ========================================

        /// A file or map name without its BIOA_ prefix (any case), as fork names drop it.
        std::string WithoutBioa(std::string const& name)
        {
            return name.size() >= 5 && _strnicmp(name.c_str(), "BIOA_", 5) == 0 ? name.substr(5) : name;
        }

        /// The fork of a vanilla level file in a map (docs/design/in-game-editor.md §11): a renamed copy the map streams instead, named
        /// <map minus BIOA_>_<file minus BIOA_> (BIOA_MYM01 and BIOA_FRE31_00_LAY: MYM01_FRE31_00_LAY), as level_build and
        /// editor_import name it. Nothing stores the pair: the rule is the link.
        std::string ForkName(std::string const& map, std::string const& file) { return WithoutBioa(map) + "_" + WithoutBioa(file); }

        /// The vanilla file a level forks in a map: <map minus BIOA_>_<rest> is BIOA_<rest>, or <rest> when that already starts
        /// with BIO (a file that had no BIOA_ to drop). Empty when the level is not a fork by the rule.
        std::string VanillaOf(std::string const& level, std::string const& map)
        {
            if (map.empty()) return {};
            auto const prefix = WithoutBioa(map) + "_";
            if (level.size() <= prefix.size() || _strnicmp(level.c_str(), prefix.c_str(), prefix.size()) != 0) return {};
            auto const rest = level.substr(prefix.size());
            return _strnicmp(rest.c_str(), "BIO", 3) == 0 ? rest : "BIOA_" + rest;
        }

        /// A path whose level (the part before the first dot) is a fork of the map, named by the vanilla file instead: session
        /// entries name a vanilla actor by its vanilla path, also while it stands in a fork. Other paths stay as they are.
        std::string Canonical(std::string const& path, std::string const& map)
        {
            auto const dot = path.find('.');
            auto const vanilla = VanillaOf(path.substr(0, dot), map);
            return vanilla.empty() ? path : vanilla + (dot == std::string::npos ? std::string{} : path.substr(dot));
        }

        /// The same path in the map's fork of its level: where a vanilla actor stands once the map streams the fork.
        std::string ForkPath(std::string const& path, std::string const& map)
        {
            auto const dot = path.find('.');
            return ForkName(map, path.substr(0, dot)) + (dot == std::string::npos ? std::string{} : path.substr(dot));
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

        /// Two light blocks as editor_import compares them, on/off left out: the radius within 0.01, brightness and falloff within
        /// 0.001, colour and kind exact. Kinds are stored ones (§12): an npc entry matches its light read back with its source.
        bool SameLightFields(LightBlock const& a, LightBlock const& b)
        {
            return a.Kind == b.Kind && a.Color == b.Color && std::fabs(a.Radius - b.Radius) <= 0.01f + 1e-5f
                && std::fabs(a.Brightness - b.Brightness) <= 0.001f + 1e-6f && std::fabs(a.Falloff - b.Falloff) <= 0.001f + 1e-6f;
        }

        bool SameLight(LightBlock const& a, LightBlock const& b) { return a.Enabled == b.Enabled && SameLightFields(a, b); }

        bool SameLight(std::optional<LightBlock> const& a, std::optional<LightBlock> const& b)
        {
            return a.has_value() == b.has_value() && (!a || SameLight(*a, *b));
        }

        /// What an undo step tells apart: a drag that came back where it started changed nothing.
        bool SameStep(StepState const& a, StepState const& b)
        {
            return a.Shown == b.Shown && a.ShownFlags.Hidden == b.ShownFlags.Hidden && a.ShownFlags.CollideActors == b.ShownFlags.CollideActors
                && a.ShownFlags.BlockActors == b.ShownFlags.BlockActors && Near(a.Transform.Location, b.Transform.Location, LocationNoise)
                && SameOrientation(a.Transform.Rotation, b.Transform.Rotation) && std::fabs(a.Transform.DrawScale - b.Transform.DrawScale) <= ScaleNoise
                && Near(a.Transform.DrawScale3D, b.Transform.DrawScale3D, ScaleNoise) && SameLight(a.Light, b.Light)
                && a.Offset.has_value() == b.Offset.has_value() && (!a.Offset || Near(*a.Offset, *b.Offset, LocationNoise));
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

        char const* KindName(LightKind kind)
        {
            switch (kind)
            {
            case LightKind::Room: return "room";
            case LightKind::Npc: return "npc";
            case LightKind::Vanilla: return "vanilla";
            default: return "props";
            }
        }

        /// A kind by its name; none for another name.
        std::optional<LightKind> KindNamed(std::string const& name)
        {
            for (auto const kind : { LightKind::Props, LightKind::Room, LightKind::Npc, LightKind::Vanilla })
            {
                if (name == KindName(kind)) return kind;
            }
            return std::nullopt;
        }

        /// The kinds that keep their channels, shadows and level filter (docs/design/in-game-editor.md §12): only the kept writer writes them.
        bool KeptKind(LightKind kind) { return kind == LightKind::Npc || kind == LightKind::Vanilla; }

        /// A light block as an entry (its base, imported.before, installed) writes it: kind by name, colour as three integers.
        json LightJson(LightBlock const& light)
        {
            return { { "kind", KindName(light.Kind) }, { "radius", light.Radius }, { "brightness", light.Brightness },
                     { "color", json::array({ light.Color[0], light.Color[1], light.Color[2] }) }, { "falloff", light.Falloff },
                     { "enabled", light.Enabled } };
        }

        /// An NPC light's block as an entry (its base, imported.before, installed) writes it beside its light block (§12): the
        /// pawn's tag and the offset [forward, right, up].
        json NpcJson(NpcLink const& npc) { return { { "tag", npc.Tag }, { "offset", VectorJson(npc.Offset) } }; }

        /// The light block of an entry, its base, imported.before or one of installed. A field it lacks or can't be read is the
        /// fallback's (the actor's own, or the edit's), as PoseFrom does; a colour component is rounded and held to 0-255.
        LightBlock LightFrom(json const& state, LightBlock const& fallback)
        {
            auto light = fallback;
            auto const block = state.find("light");
            if (block == state.end() || !block->is_object()) return light;
            auto const number = [&](char const* key, float& out) {
                if (auto const it = block->find(key); it != block->end() && it->is_number()) out = static_cast<float>(it->get<double>());
            };
            if (auto const kind = KindNamed(Text(*block, "kind"))) light.Kind = *kind;
            number("radius", light.Radius);
            number("brightness", light.Brightness);
            number("falloff", light.Falloff);
            if (double rgb[3]; Numbers(*block, "color", rgb))
            {
                for (size_t i = 0; i < 3; i++) light.Color[i] = static_cast<int>(std::clamp(std::lround(rgb[i]), 0L, 255L));
            }
            if (auto const enabled = block->find("enabled"); enabled != block->end() && enabled->is_boolean()) light.Enabled = enabled->get<bool>();
            return light;
        }

        /// Whether an entry (or a state of it) is a light's: it has a light block.
        bool HasLightBlock(json const& state)
        {
            auto const block = state.find("light");
            return block != state.end() && block->is_object();
        }

        /// The kind an entry (or a state of it) names in its light block; none without one.
        std::optional<LightKind> KindIn(json const& state)
        {
            if (!HasLightBlock(state)) return std::nullopt;
            return KindNamed(Text(state.at("light"), "kind"));
        }

        /// The NPC block of an entry, its base, imported.before or one of installed (§12): the pawn's tag and the offset. A field
        /// it lacks is the fallback's; none when the state has no npc block and there is no fallback.
        std::optional<NpcLink> NpcFrom(json const& state, std::optional<NpcLink> const& fallback)
        {
            auto const block = state.find("npc");
            if (block == state.end() || !block->is_object()) return fallback;
            auto npc = fallback.value_or(NpcLink{});
            if (auto const tag = Text(*block, "tag"); !tag.empty()) npc.Tag = tag;
            if (double v[3]; Numbers(*block, "offset", v)) npc.Offset = { static_cast<float>(v[0]), static_cast<float>(v[1]), static_cast<float>(v[2]) };
            return npc;
        }

        /// Whether an entry holds a light a Phase 4 bridge would rewrite with its own writer (§12): light.kind npc or vanilla, in
        /// the entry, its base, imported.before or one of installed.
        bool HoldsKeptLight(json const& entry)
        {
            auto const kept = [](json const& state) {
                if (!state.is_object()) return false;
                auto const kind = KindIn(state);
                return kind && KeptKind(*kind);
            };
            if (!entry.is_object()) return false;
            if (kept(entry)) return true;
            if (auto const base = entry.find("base"); base != entry.end() && kept(*base)) return true;
            if (auto const imported = entry.find("imported"); imported != entry.end() && imported->is_object())
            {
                if (auto const before = imported->find("before"); before != imported->end() && kept(*before)) return true;
            }
            if (auto const installed = entry.find("installed"); installed != entry.end() && installed->is_array())
            {
                if (std::any_of(installed->begin(), installed->end(), kept)) return true;
            }
            return false;
        }

        /// The version a session file of these entries is written as (§12): 3 when one holds an npc or vanilla light, else 2.
        int VersionFor(json const& actors)
        {
            return std::any_of(actors.begin(), actors.end(), HoldsKeptLight) ? KeptLightVersion : SessionVersion;
        }

        /// A session file version this bridge reads: 1 (Phase 1's) to 3.
        bool KnownVersion(int version) { return version >= 1 && version <= KeptLightVersion; }

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

        /// Spawned actors by tag (their path changes with each spawn), level actors by path (their tag is often their class name):
        /// the canonical one, the vanilla path of an actor in a fork of the session's map.
        std::string KeyOf(json const& actor, std::string const& map)
        {
            return Flag(actor, "spawned") ? "tag:" + Text(actor, "tag") : "path:" + Canonical(Text(actor, "path"), map);
        }

        /// An edit's path is canonical already (NewEdit, EditFrom).
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

        // ! Lights
        // ========================================

        /// A level's static light collection (docs/design/in-game-editor.md §12, 5b): a Light actor whose lights are its components. Nothing here writes
        /// one (Shielded): a reattach of it reattaches its hundred lights.
        bool IsCollection(UObject* object)
        {
            static UClass* const collection = AStaticLightCollectionActor::StaticClass();
            return object != nullptr && object->IsA(collection);
        }

        /// A light actor's point light component; null for any other actor (a light collection included), and for a light of
        /// another shape.
        UPointLightComponent* PointOf(AActor* actor)
        {
            if (actor == nullptr || !actor->IsA(ALight::StaticClass()) || IsCollection(actor)) return nullptr;
            auto const component = static_cast<ALight*>(actor)->LightComponent;
            return Real(component) && component->IsA(UPointLightComponent::StaticClass()) ? static_cast<UPointLightComponent*>(component) : nullptr;
        }

        /// A point light: what the editor spawns and the build copies. Every light is bHidden by class default, so nothing reads a
        /// light's bHidden as deleted, and nothing hides one (docs/design/in-game-editor.md §11).
        bool IsLight(AActor* actor) { return PointOf(actor) != nullptr; }

        DWORD BitsOf(FLightingChannelContainer const& channels)
        {
            static_assert(sizeof(FLightingChannelContainer) == sizeof(DWORD));
            DWORD bits;
            std::memcpy(&bits, &channels, sizeof bits);
            return bits;
        }

        void SetBits(FLightingChannelContainer& channels, DWORD bits) { std::memcpy(&channels, &bits, sizeof bits); }

        /// A kind's channels, always initialized: props {Gameplay_4}; room {BSP, Static, Dynamic, CompositeDynamic, Gameplay_4}.
        DWORD ChannelsOf(LightKind kind) { return ChannelInitialized | ChannelGameplay4 | (kind == LightKind::Room ? RoomChannels : 0); }

        /// The kind a light's channels make: a room light when it lights walls, vanilla meshes or pawns. Only for ours: an NPC's
        /// light is on Dynamic, and would read as a room light.
        LightKind KindOf(DWORD bits) { return (bits & RoomChannels) != 0 ? LightKind::Room : LightKind::Props; }

        /// The source a stored kind says (docs/design/in-game-editor.md §12).
        LightSource SourceOf(LightKind kind)
        {
            if (kind == LightKind::Npc) return LightSource::Npc;
            return kind == LightKind::Vanilla ? LightSource::Vanilla : LightSource::Ours;
        }

        /// A point (or spot) light component's settings, with a kind; enabled is its bEnabled.
        LightBlock FieldsOf(UPointLightComponent const* component, LightKind kind)
        {
            LightBlock light;
            light.Kind = kind;
            light.Radius = component->Radius;
            light.Brightness = component->Brightness;
            light.Color = { component->LightColor.R, component->LightColor.G, component->LightColor.B };
            light.Falloff = component->FalloffExponent;
            light.Enabled = component->bEnabled != 0;
            return light;
        }

        /// A light's settings as its component has them; enabled is the component's bEnabled. Its kind is its source's (§12):
        /// ours by their channels, an NPC's or a level's by where it is, never by its channels.
        LightBlock LightOf(AActor* actor, LightSource source)
        {
            auto const component = PointOf(actor);
            if (component == nullptr) return {};
            return FieldsOf(component, source == LightSource::Npc       ? LightKind::Npc
                                       : source == LightSource::Vanilla ? LightKind::Vanilla
                                                                        : KindOf(BitsOf(component->LightingChannels)));
        }

        /// A level light's settings (5b): the kind is vanilla.
        LightBlock LevelLightOf(UPointLightComponent const* light) { return FieldsOf(light, LightKind::Vanilla); }

        /// A level light's type (docs/design/in-game-editor.md §12, 5b), from its live fields by level_build's rule and in its order: B baked (built into
        /// the lightmaps, which keep it on the walls whatever the editor does), D dynamic (bForceDynamicLight, or the Dynamic
        /// channel: drawn live), U on a special channel (Skybox, Unnamed, Cinematic, Gameplay, Crowd: it lights only meshes on
        /// it), else C (BSP, Static, CompositeDynamic: characters and props through their light environments).
        char TypeOf(UPointLightComponent const* light)
        {
            if (light->bHasLightEverBeenBuiltIntoLightMap && light->UseDirectLightMap) return 'B';
            auto const bits = BitsOf(light->LightingChannels) & ~ChannelInitialized;
            if (light->bForceDynamicLight || (bits & ChannelDynamic) != 0) return 'D';
            if ((bits & ~(ChannelBsp | ChannelStatic | ChannelCompositeDynamic)) != 0) return 'U';
            return 'C';
        }

        /// A U light's channels outside the four base ones and bInitialized.
        DWORD SpecialBits(UPointLightComponent const* light) { return BitsOf(light->LightingChannels) & ~(ChannelInitialized | RoomChannels); }

        /// Channel bits by the names UnrealEd gives them ("Unnamed_6", "Gameplay_4"), comma-separated: FLightingChannelContainer's
        /// bits 5 to 26 (Engine_structs.hpp: Skybox, Unnamed..Unnamed05, Cinematic..Cinematic09, Gameplay..Gameplay03, Crowd).
        std::string ChannelNames(DWORD bits)
        {
            std::string names;
            auto const add = [&](std::string name) { names += (names.empty() ? "" : ", ") + name; };
            for (int bit = 5; bit <= 26; bit++)
            {
                if ((bits & (1u << bit)) == 0) continue;
                if (bit == 5) add("Skybox");
                else if (bit <= 11) add(std::format("Unnamed_{}", bit - 5));
                else if (bit <= 21) add(std::format("Cinematic_{}", bit - 11));
                else if (bit <= 25) add(std::format("Gameplay_{}", bit - 21));
                else add("Crowd");
            }
            return names;
        }

        /// A matrix row's first three numbers.
        FVector RowOf(FPlane const& row) { return { row.X, row.Y, row.Z }; }

        /// Where a level light stands (docs/design/in-game-editor.md §12, 5b): its Translation through the matrix its collection's file gives it at load
        /// (CachedParentToWorld, row vectors: T.x X + T.y Y + T.z Z + W), which is LightToWorld's origin; read so, it is right while
        /// its level is loaded but not visible too (no transform update yet). A matrix not set (all zeros) reads LightToWorld.
        FVector PlaceOfLight(UPointLightComponent const* light)
        {
            auto const& m = light->CachedParentToWorld;
            auto const x = RowOf(m.XPlane), y = RowOf(m.YPlane), z = RowOf(m.ZPlane);
            if (x.X == 0 && x.Y == 0 && x.Z == 0 && y.X == 0 && y.Y == 0 && y.Z == 0) return RowOf(light->LightToWorld.WPlane);
            auto const& t = light->Translation;
            return { t.X * x.X + t.Y * y.X + t.Z * z.X + m.WPlane.X, t.X * x.Y + t.Y * y.Y + t.Z * z.Y + m.WPlane.Y,
                     t.X * x.Z + t.Y * y.Z + t.Z * z.Z + m.WPlane.Z };
        }

        /// How far its collection's matrix scales a level light: the largest of each row's length less 1.
        float ParentScaleOf(UPointLightComponent const* light)
        {
            auto const& m = light->CachedParentToWorld;
            auto most = 0.0;
            for (auto const row : { &m.XPlane, &m.YPlane, &m.ZPlane })
                most = std::max(most, std::fabs(std::sqrt(double(row->X) * row->X + double(row->Y) * row->Y + double(row->Z) * row->Z) - 1));
            return static_cast<float>(most);
        }

        /// The Translation that puts a level light at a world place: (world - origin) through the inverse of the matrix's 3x3
        /// (Cramer's rule on its rows X, Y, Z: T = ((d . Y x Z), (X . d x Z), (X . Y x d)) / (X . Y x Z)), the full inverse, as
        /// level_build computes it from the file's matrix. False when the 3x3 is singular.
        bool TranslationFor(UPointLightComponent const* light, FVector world, FVector& out)
        {
            auto const& m = light->CachedParentToWorld;
            using V = std::array<double, 3>;
            auto const row = [](FPlane const& p) { return V{ p.X, p.Y, p.Z }; };
            auto const cross = [](V const& a, V const& b) { return V{ a[1] * b[2] - a[2] * b[1], a[2] * b[0] - a[0] * b[2], a[0] * b[1] - a[1] * b[0] }; };
            auto const dot = [](V const& a, V const& b) { return a[0] * b[0] + a[1] * b[1] + a[2] * b[2]; };
            auto const x = row(m.XPlane), y = row(m.YPlane), z = row(m.ZPlane);
            V const d{ double(world.X) - m.WPlane.X, double(world.Y) - m.WPlane.Y, double(world.Z) - m.WPlane.Z };
            auto const det = dot(x, cross(y, z));
            if (std::fabs(det) < 1e-9) return false;
            out = { static_cast<float>(dot(d, cross(y, z)) / det), static_cast<float>(dot(x, cross(d, z)) / det), static_cast<float>(dot(x, cross(y, d)) / det) };
            return true;
        }

        /// The hard guard (docs/design/in-game-editor.md §12, 5b): nothing writes a static light collection, no pose, no flags, no reattach (one
        /// reattaches all its lights, about a hundred). True for one, said once per collection in the log.
        bool Shielded(AActor* actor, char const* what)
        {
            if (!IsCollection(actor)) return false;
            static std::unordered_set<std::string> said;
            if (auto path = ToUtf8(PathOf(actor)); said.insert(path).second)
                LEASI_WARN("editor: {} is a light collection: {} of it was not written (its lights are edited one by one)", path, what);
            return true;
        }

        bool OurLight(AActor* actor);

        /// The kept writer (docs/design/in-game-editor.md §12), for an NPC's or a level's light: its channels, shadows, level filter and colour alpha
        /// stay as built, and each of radius, brightness, colour, falloff and on/off is written only when the wanted value
        /// differs from the live one (the radius by more than 0.01, brightness and falloff by more than 0.001, the colour
        /// exactly). A field an entry lacks was filled from the light itself (LightFrom), so it is never written: an untouched
        /// colour from a prefab archetype stays the archetype's, on a first edit, an undo or a pass after a reload. The actor
        /// shows enabled && !deleted, on the component and the actor. The caller reattaches once, unless the actor's level is
        /// loaded but not visible. A level light (5b) has only the component written (WriteKeptFields): its collection is never
        /// touched.
        void WriteKeptFields(UPointLightComponent* component, LightBlock const& light, bool deleted)
        {
            auto const differs = [](float now, float wanted, float within) { return !(std::fabs(now - wanted) <= within); };
            if (differs(component->Radius, light.Radius, 0.01f + 1e-5f)) component->Radius = light.Radius;
            if (differs(component->Brightness, light.Brightness, 0.001f + 1e-6f)) component->Brightness = light.Brightness;
            if (differs(component->FalloffExponent, light.Falloff, 0.001f + 1e-6f)) component->FalloffExponent = light.Falloff;
            auto& colour = component->LightColor;
            if (colour.R != light.Color[0] || colour.G != light.Color[1] || colour.B != light.Color[2])
            {
                // Its alpha stays: the default template's is 0.
                colour.R = static_cast<unsigned char>(light.Color[0]);
                colour.G = static_cast<unsigned char>(light.Color[1]);
                colour.B = static_cast<unsigned char>(light.Color[2]);
            }
            auto const on = light.Enabled && !deleted ? 1u : 0u;
            if (component->bEnabled != on) component->bEnabled = on;
        }

        void WriteKeptLight(AActor* actor, LightBlock const& light, bool deleted)
        {
            auto const component = PointOf(actor);
            if (component == nullptr) return;
            WriteKeptFields(component, light, deleted);
            auto const on = light.Enabled && !deleted ? 1u : 0u;
            if (static_cast<ALight*>(actor)->bEnabled != on) static_cast<ALight*>(actor)->bEnabled = on;
        }

        /// Writes a light's settings raw, with every field a spawned light would otherwise take from its class default (it starts
        /// casting shadows, on BSP, Static, Dynamic and CompositeDynamic, colour alpha 0): what level_build writes for a lights:
        /// entry. The actor shows enabled && !deleted, on the component and the actor. The caller reattaches once, unless the
        /// actor's level is loaded but not visible. Only for ours (§12): an npc or vanilla kind, or a light OurLight doesn't take
        /// (neither a lights: entry of levels.json, an editor spawn, nor an ours-kind edit of a light of our levels), is left as it
        /// is (said once in the log), so a call the kind dispatch (ApplyLight) missed never rewrites an NPC's or a level's
        /// channels and shadows.
        void WriteLight(AActor* actor, LightBlock const& light, bool deleted)
        {
            auto const component = PointOf(actor);
            if (component == nullptr) return;
            if (KeptKind(light.Kind) || !OurLight(actor))
            {
                static std::unordered_set<std::string> said;
                if (auto path = ToUtf8(PathOf(actor)); said.insert(path).second)
                    LEASI_WARN("editor: {} (kind {}) is not a light of ours: its writer left it as it is", path, KindName(light.Kind));
                return;
            }
            auto const on = light.Enabled && !deleted;
            component->Radius = light.Radius;
            component->Brightness = light.Brightness;
            // FColor is B, G, R, A in memory.
            component->LightColor = { static_cast<unsigned char>(light.Color[2]), static_cast<unsigned char>(light.Color[1]),
                                      static_cast<unsigned char>(light.Color[0]), 255 };
            component->FalloffExponent = light.Falloff;
            component->CastShadows = 0;
            component->bOnlyAffectSameAndSpecifiedLevels = 0;
            component->bForceDynamicLight = 1;
            SetBits(component->LightingChannels, ChannelsOf(light.Kind));
            component->bEnabled = on ? 1 : 0;
            static_cast<ALight*>(actor)->bEnabled = on ? 1 : 0;
        }

        /// Writes a light's settings through the writer its stored kind takes (docs/design/in-game-editor.md §12): ours through WriteLight, an NPC's
        /// or a level's through the kept writer. Every write of a light's settings comes here.
        void ApplyLight(AActor* actor, LightBlock const& light, bool deleted)
        {
            if (KeptKind(light.Kind)) WriteKeptLight(actor, light, deleted);
            else WriteLight(actor, light, deleted);
        }

        /// Whether a light already shows a state (docs/design/in-game-editor.md §11), as editor_import counts two equal: the location to 2 decimals, the
        /// component's bEnabled as light.enabled && !deleted, each light field within editor_import's tolerances. Its rotation, its
        /// scales and bHidden don't count. A field the state lacks is the light's own. The light is read with the source the
        /// state's kind names (§12), so an npc state compares with the light read as npc; an npc state's location is had within
        /// 0.05 (NpcLightNoise), since the build places the light from 2-decimal offsets. A level light (5b) is compared the same
        /// way, by its component and where its Translation puts it (HasLevelLight).
        bool ShowsLight(LightBlock const& now, bool on, FVector place, json const& state, bool deleted)
        {
            auto const kind = KindIn(state);
            auto const light = LightFrom(state, now);
            double v[3];
            auto const location = Numbers(state, "location", v) ? FVector{ static_cast<float>(v[0]), static_cast<float>(v[1]), static_cast<float>(v[2]) } : place;
            auto const placed = kind == LightKind::Npc ? Near(place, location, NpcLightNoise) : SameLocation(place, location);
            return placed && on == (light.Enabled && !deleted) && SameLightFields(now, light);
        }

        bool HasLight(AActor* actor, json const& state, bool deleted)
        {
            auto const component = PointOf(actor);
            if (component == nullptr) return false;
            auto const kind = KindIn(state);
            return ShowsLight(LightOf(actor, kind ? SourceOf(*kind) : LightSource::Ours), component->bEnabled != 0, actor->Location, state, deleted);
        }

        bool HasLevelLight(UPointLightComponent const* light, json const& state, bool deleted)
        {
            return ShowsLight(LevelLightOf(light), light->bEnabled != 0, PlaceOfLight(light), state, deleted);
        }

        float Distance(FVector a, FVector b)
        {
            return static_cast<float>(std::sqrt(double(a.X - b.X) * (a.X - b.X) + double(a.Y - b.Y) * (a.Y - b.Y) + double(a.Z - b.Z) * (a.Z - b.Z)));
        }

        /// A light's falloff at a distance (docs/design/in-game-editor.md §11): (1 - (d/R)^2)^falloff, 0 from the radius out. The bridge and level_build
        /// use this one rule.
        float Attenuation(float distance, float radius, float falloff)
        {
            if (!(radius > 0) || distance >= radius) return 0;
            auto const x = double(distance) / radius;
            return static_cast<float>(std::pow(1.0 - x * x, double(falloff)));
        }

        /// Rec. 709 luminance of a colour, each component over 255.
        float Luminance(std::array<int, 3> const& color)
        {
            return (0.2126f * static_cast<float>(color[0]) + 0.7152f * static_cast<float>(color[1]) + 0.0722f * static_cast<float>(color[2])) / 255.f;
        }

        /// A light reaches a prop when its channels include Gameplay_4 (both kinds' do) and its falloff at the prop's bounds centre
        /// is at least 0.25; it covers it when it also is enabled. The build's rule (LevelBuilder.SwitchLighting).
        bool Reaches(LightSeen const& light, FVector centre)
        {
            return light.Gameplay4 && Attenuation(Distance(light.Location, centre), light.Light.Radius, light.Light.Falloff) >= CoverShare;
        }

        bool Covers(LightSeen const& light, FVector centre) { return light.Light.Enabled && Reaches(light, centre); }

        /// What a prop gets from prop lights (E): brightness times luminance times falloff, over the enabled props-kind lights. Room
        /// lights don't count: they light the vanilla mesh beside it as well, so they add to both sides of the match.
        float Received(std::vector<LightSeen> const& lights, FVector centre)
        {
            auto sum = 0.f;
            for (auto const& l : lights)
            {
                if (!l.Light.Enabled || l.Light.Kind != LightKind::Props) continue;
                sum += l.Light.Brightness * Luminance(l.Light.Color) * Attenuation(Distance(l.Location, centre), l.Light.Radius, l.Light.Falloff);
            }
            return sum;
        }

        /// Where Auto light and L put a prop light (docs/design/in-game-editor.md §11): outside the bounds box's near face toward the camera, horizontally,
        /// by half the bounds radius, at the box's top plus half the bounds radius; radius 2.5 bounds radii, never under 120, and
        /// never under its distance to the bounds centre over AutoReach, so it always covers the prop (the box of a turned prop is
        /// wider than its sphere). A camera straight above stands for the direction it looks from.
        Placement AutoPlacement(PropBounds const& bounds, FVector camera, int cameraYaw)
        {
            auto dx = double(camera.X) - bounds.Centre.X, dy = double(camera.Y) - bounds.Centre.Y;
            auto length = std::hypot(dx, dy);
            if (length < 1)
            {
                auto const yaw = cameraYaw * Pi / 32768.0;
                dx = -std::cos(yaw);
                dy = -std::sin(yaw);
                length = 1;
            }
            dx /= length;
            dy /= length;
            // Where the ray from the centre toward the camera leaves the box.
            auto exit = 1e30;
            if (std::fabs(dx) > 1e-6) exit = std::min(exit, bounds.Extent.X / std::fabs(dx));
            if (std::fabs(dy) > 1e-6) exit = std::min(exit, bounds.Extent.Y / std::fabs(dy));
            auto const out = exit + 0.5 * bounds.Radius;
            FVector const at{ static_cast<float>(bounds.Centre.X + dx * out), static_cast<float>(bounds.Centre.Y + dy * out),
                              bounds.Centre.Z + bounds.Extent.Z + 0.5f * bounds.Radius };
            return { at, std::max({ MinAutoRadius, 2.5f * bounds.Radius, Distance(at, bounds.Centre) / AutoReach }) };
        }

        /// What a fresh Auto light would give a prop (T): the seed's brightness and colour, Auto light's place and radius (at),
        /// falloff 2. Nothing for a prop that needs a radius over MaxAutoRadius: Auto light and L add no light for it, so there
        /// is nothing to fall short of (no "L adds one", no drag warning).
        float Target(PropBounds const& bounds, Placement const& at, Seed const& seed)
        {
            if (at.Radius > MaxAutoRadius) return 0;
            return seed.Brightness * Luminance(seed.Color) * Attenuation(Distance(at.Location, bounds.Centre), at.Radius, 2);
        }

        /// Why Auto light and L add no light for a prop that needs a radius over MaxAutoRadius from where the view is.
        std::string TooBig(std::string const& prop, float radius)
        {
            return std::format("{} is too big for one prop light: from here it needs radius {:.0f}, over the cap of {:.0f} (a prop light "
                               "lights every prop within its radius)",
                               prop, radius, MaxAutoRadius);
        }

        /// What a prop too big for one prop light is told to use instead.
        constexpr char const RoomLightHint[] = "a room light (Lights tab) lights it with the walls around it";

        /// E over T; a target of nothing is met (no light could add to it).
        float ShareOf(float received, float target) { return target > 0 ? received / target : 1.f; }

        // ! Edits
        // ========================================

        Pose PoseOf(AActor* actor) { return { actor->Location, actor->Rotation, actor->DrawScale, actor->DrawScale3D }; }
        HideFlags FlagsOf(AActor* actor) { return { actor->bHidden != 0, actor->bCollideActors != 0, actor->bBlockActors != 0 }; }

        size_t IndexOf(Edit const& edit) { return static_cast<size_t>(&edit - G.Edits.data()); }

        /// A level light's pose as an edit holds it (docs/design/in-game-editor.md §12, 5b): where it stands, a rotation of zeros and scales of 1 (an
        /// entry needs them, and its aim is not edited).
        Pose LightPose(UPointLightComponent const* light) { return { PlaceOfLight(light), {}, 1, { 1, 1, 1 } }; }

        /// Still on the actor it was bound to: not pending, the actor neither destroyed nor replaced; a level light's component
        /// too.
        bool Bound(Edit const& e)
        {
            return !e.Pending && Alive(e.Actor, e.Index) && e.Actor->Class == e.Class
                && (!e.Component || (Alive(e.Comp, e.CompIndex) && e.Comp->Class == e.CompClass));
        }

        void Detach(Edit& e)
        {
            e.Actor = nullptr;
            e.Index = -1;
            e.Class = nullptr;
            e.LevelObject = nullptr;
            e.LevelIndex = -1;
            e.Comp = nullptr;
            e.CompIndex = -1;
            e.CompClass = nullptr;
        }

        float ExtentOf(AActor* actor);
        LightSource SourceOf(AActor* actor);

        /// An edit takes an actor as it stands, shown: hidden by the game (Kismet) is not deleted by the editor, and the flags it
        /// has are the ones undo gives back. The Put that follows hides it if the edit is deleted. Every caller binds an actor
        /// nothing has written to yet (a level's, as its file has it, or a fresh spawn): its bounds are taken then.
        void Bind(Edit& e, AActor* actor)
        {
            e.Effect = actor->IsA(AEmitter::StaticClass());
            e.Actor = actor;
            e.Index = actor->ObjectInternalInteger;
            e.Class = actor->Class;
            e.LevelObject = actor->Outer;
            e.LevelIndex = actor->Outer != nullptr ? actor->Outer->ObjectInternalInteger : -1;
            e.Pending = false;
            e.PendingWhy.clear();
            e.Deleted = false;
            // A light's bHidden is its class default, not a delete: Put switches a light off instead of hiding it. Its edit has its
            // settings; one from an entry without them (none should be) takes the light's own, with its source's kind.
            e.ShownFlags = IsLight(actor) ? HideFlags{} : FlagsOf(actor);
            if (IsLight(actor) && !e.Light) e.Light = LightOf(actor, SourceOf(actor));
            e.FileExtent.reset();
            if (auto const extent = Visible(actor) ? ExtentOf(actor) : 0.f; extent > 0) e.FileExtent = extent;
        }

        /// A level light's edit takes its component as it stands (docs/design/in-game-editor.md §12, 5b), its collection as its actor, which nothing
        /// writes. Its edit has its settings; one from an entry without them (none should be) takes the light's own. Its last
        /// reset is where it stands now: the first reset after a change takes the environments there too.
        void BindLight(Edit& e, AActor* owner, UPointLightComponent* light)
        {
            e.Effect = false;
            e.Actor = owner;
            e.Index = owner->ObjectInternalInteger;
            e.Class = owner->Class;
            e.LevelObject = owner->Outer;
            e.LevelIndex = owner->Outer != nullptr ? owner->Outer->ObjectInternalInteger : -1;
            e.Comp = light;
            e.CompIndex = light->ObjectInternalInteger;
            e.CompClass = light->Class;
            e.Component = true;
            e.Pending = false;
            e.PendingWhy.clear();
            e.Deleted = false;
            e.ShownFlags = {};
            if (!e.Light) e.Light = LevelLightOf(light);
            e.Type = TypeOf(light);
            e.FileExtent.reset();
            e.ResetFrom = std::pair{ PlaceOfLight(light), light->Radius };
        }

        /// The edit bound to an actor, if any; never a level light's (its actor is its collection: LightEditOf).
        Edit* EditOf(AActor* actor)
        {
            auto const it = std::find_if(G.Edits.begin(), G.Edits.end(), [&](Edit const& e) {
                return !e.Pending && !e.Component && e.Actor == actor && e.Index == actor->ObjectInternalInteger && e.Class == actor->Class;
            });
            return it != G.Edits.end() ? &*it : nullptr;
        }

        /// The edit bound to a level light (5b), if any.
        Edit* LightEditOf(UPointLightComponent* light)
        {
            auto const it = std::find_if(G.Edits.begin(), G.Edits.end(), [&](Edit const& e) {
                return !e.Pending && e.Component && e.Comp == light && e.CompIndex == light->ObjectInternalInteger && e.CompClass == light->Class;
            });
            return it != G.Edits.end() ? &*it : nullptr;
        }

        /// Whether an actor's tag is in one of levels.json's lists (props or lights) for its level, as lower-case instanced text.
        bool Listed(std::unordered_map<std::string, std::unordered_set<std::string>> const& lists, AActor* actor)
        {
            auto const level = lists.find(Lower(LevelOf(actor)));
            return level != lists.end() && level->second.contains(Lower(NameText(actor->Tag)));
        }

        std::pair<BuiltNpc const*, BuiltNpcLight const*> NpcLightOf(AActor* actor);

        /// A light the editor lists and lets be picked (docs/design/in-game-editor.md §11, §12): one it spawned (or a session entry's light), a built
        /// lights: entry of our YAMLs (levels.json's lights tags of its level), or a light of one of our NPCs (levels.json's npcs
        /// lights of its level). Vanilla light actors are none of these.
        bool ListedLight(AActor* actor)
        {
            if (!IsLight(actor)) return false;
            auto const edit = EditOf(actor);
            return (edit != nullptr && edit->Light) || Listed(G.Ours.Lights, actor) || NpcLightOf(actor).first != nullptr;
        }

        bool Phase3Rules(AActor* actor);

        /// A light our own writer may write (docs/design/in-game-editor.md §12's hard guard): an editor spawn, or a built lights: entry of our YAMLs.
        bool OurLight(AActor* actor)
        {
            auto const edit = EditOf(actor);
            if ((edit != nullptr && edit->Spawned) || Listed(G.Ours.Lights, actor)) return true;
            // A light of our levels with an ours-kind edit that levels.json doesn't name (missing, or a build since renamed it):
            // ListedLight lets it be picked and Phase 4 wrote it. Never an NPC's (NpcLightOf) or a vanilla level's (Phase3Rules).
            return edit != nullptr && edit->Light && !KeptKind(edit->Light->Kind) && NpcLightOf(actor).first == nullptr && Phase3Rules(actor);
        }

        /// The NPC of our levels an actor is a light of, and that light's entry (levels.json npcs, docs/design/in-game-editor.md §12): the actor's tag
        /// is in its own level's npcs.*.lights. Nulls for any other actor.
        std::pair<BuiltNpc const*, BuiltNpcLight const*> NpcLightOf(AActor* actor)
        {
            auto const name = Lower(LevelOf(actor));
            auto const level = G.Ours.NpcLights.find(name);
            auto const npcs = G.Ours.Npcs.find(name);
            if (level == G.Ours.NpcLights.end() || npcs == G.Ours.Npcs.end()) return {};
            auto const light = level->second.find(Lower(NameText(actor->Tag)));
            if (light == level->second.end()) return {};
            auto const& npc = npcs->second[light->second.first];
            return { &npc, &npc.Lights[light->second.second] };
        }

        /// Where an untouched light comes from (docs/design/in-game-editor.md §12): an NPC's when levels.json names it among its level's npcs lights,
        /// else ours. An edited light's source is its edit's stored kind (SourceOf(kind)).
        LightSource SourceOf(AActor* actor) { return NpcLightOf(actor).first != nullptr ? LightSource::Npc : LightSource::Ours; }

        // ! NPC lights
        // ========================================

        /// An NPC's name as the Lights tab and the status line say it: its YAML key with a capital ("guard": "Guard").
        std::string Title(std::string name)
        {
            if (!name.empty()) name[0] = static_cast<char>(std::toupper(static_cast<unsigned char>(name[0])));
            return name;
        }

        /// The NPC levels.json lists with this tag (docs/design/in-game-editor.md §12): an NPC's tag names one pawn across our YAMLs. Null when none
        /// does. Looked up again whenever needed (a new levels.json replaces them all), from the file alone: Save calls it while
        /// the world's levels may be gone.
        BuiltNpc const* NpcNamed(std::string const& tag)
        {
            for (auto const& [level, npcs] : G.Ours.Npcs)
            {
                auto const npc = std::find_if(npcs.begin(), npcs.end(), [&](BuiltNpc const& n) { return SameText(n.Tag, tag); });
                if (npc != npcs.end()) return &*npc;
            }
            return nullptr;
        }

        /// The NPC levels.json lists for a pawn: one of its own level's npcs, by tag. Null for any other actor.
        BuiltNpc const* NpcOfPawn(AActor* actor)
        {
            if (actor == nullptr || !actor->IsA(APawn::StaticClass())) return nullptr;
            auto const npcs = G.Ours.Npcs.find(Lower(LevelOf(actor)));
            if (npcs == G.Ours.Npcs.end()) return nullptr;
            auto const tag = NameText(actor->Tag);
            auto const npc = std::find_if(npcs->second.begin(), npcs->second.end(), [&](BuiltNpc const& n) { return SameText(n.Tag, tag); });
            return npc != npcs->second.end() ? &*npc : nullptr;
        }

        /// The edit of an NPC's pawn in this session, if the editor moved or turned it: the edit of the session's map with the
        /// pawn's tag in the NPC's level, waiting for its actor or not.
        Edit* PawnEditOf(BuiltNpc const& npc)
        {
            auto const it = std::find_if(G.Edits.begin(), G.Edits.end(), [&](Edit const& e) {
                return !e.Light && !e.Effect && e.Map == G.SessionMap && SameText(e.Tag, npc.Tag) && SameText(e.Level, npc.Level);
            });
            return it != G.Edits.end() ? &*it : nullptr;
        }

        /// Where an NPC's light offsets are measured from (docs/design/in-game-editor.md §12): its pawn's Location (the collision centre) and yaw.
        struct NpcPose
        {
            FVector Location{};
            int Yaw = 0;
        };

        /// An NPC's reference pose: its pawn's edit in this session when it has one, else its built pose from levels.json; never
        /// the live pawn.
        NpcPose ReferencePose(BuiltNpc const& npc)
        {
            if (auto const pawn = PawnEditOf(npc)) return { pawn->Now.Location, pawn->Now.Rotation.Yaw };
            return { npc.Location, npc.Yaw };
        }

        /// A place's offset [forward, right, up] from a pose, as level_build's formula inverted: d = L - P turned back by the yaw,
        /// f = dx cos t + dy sin t, r = -dx sin t + dy cos t, u = dz, t = yaw 2 pi / 65536.
        FVector OffsetOf(NpcPose const& pose, FVector at)
        {
            auto const t = pose.Yaw * Pi / 32768.0;
            auto const dx = double(at.X) - pose.Location.X, dy = double(at.Y) - pose.Location.Y;
            return { static_cast<float>(dx * std::cos(t) + dy * std::sin(t)), static_cast<float>(-dx * std::sin(t) + dy * std::cos(t)),
                     static_cast<float>(double(at.Z) - pose.Location.Z) };
        }

        /// The place an offset [forward, right, up] gives from a pose, as level_build places an NPC's light: P + (f cos t - r sin t,
        /// f sin t + r cos t, u).
        FVector PlaceOf(NpcPose const& pose, FVector offset)
        {
            auto const t = pose.Yaw * Pi / 32768.0;
            return { static_cast<float>(pose.Location.X + offset.X * std::cos(t) - offset.Y * std::sin(t)),
                     static_cast<float>(pose.Location.Y + offset.X * std::sin(t) + offset.Y * std::cos(t)), static_cast<float>(double(pose.Location.Z) + offset.Z) };
        }

        /// An NPC light's NPC and offset: its edit's, else levels.json's for a built one. None for any other light, and for an
        /// edited light whose edit has none.
        std::optional<NpcLink> NpcLinkOf(AActor* actor)
        {
            if (auto const edit = EditOf(actor); edit != nullptr && edit->Light) return edit->Npc;
            if (auto const [npc, light] = NpcLightOf(actor); npc != nullptr) return NpcLink{ npc->Tag, light->Offset };
            return std::nullopt;
        }

        /// The place of an NPC light's edit at its NPC's reference pose plus its offset (docs/design/in-game-editor.md §12), into at. Whether that
        /// moves it: the pawn's edit may have moved since the entry was saved, or npc_place built the NPC elsewhere. An NPC
        /// levels.json doesn't list leaves at as it is.
        bool NpcPlace(Edit const& e, FVector& at)
        {
            if (!e.Npc) return false;
            auto const npc = NpcNamed(e.Npc->Tag);
            if (npc == nullptr) return false;
            auto const place = PlaceOf(ReferencePose(*npc), e.Npc->Offset);
            if (Near(place, at, LocationNoise)) return false;
            at = place;
            return true;
        }

        /// A prop the switch puts on prop lights (docs/design/in-game-editor.md §11): an editor spawn, or a built prop of our levels (levels.json's props
        /// tags of its level). Usables, live_place copies and vanilla meshes keep their light environment.
        bool SwitchedProp(AActor* actor)
        {
            if (IsLight(actor)) return false;
            auto const edit = EditOf(actor);
            return (edit != nullptr && edit->Spawned) || Listed(G.Ours.Props, actor);
        }

        /// A new edit of an untouched, shown actor, before anything writes to it: its state now, and for a level actor its base
        /// (what the YAML holds; the build that made it shown did not remove it). A light's state is its settings, its on/off the
        /// component's bEnabled; its base is never removed (a light is switched off, never removed). A built NPC light's NPC and
        /// offset are levels.json's, now and in its base, and its base location is levels.json's pose plus that offset (§12).
        /// Adding an edit moves the others: hold indexes across it, not pointers.
        Edit& NewEdit(AActor* actor)
        {
            auto& edit = G.Edits.emplace_back();
            Bind(edit, actor);
            edit.Map = ToUtf8(MapName());
            edit.Tag = NameText(actor->Tag);
            // An actor in a fork is saved under its vanilla path and file: the YAML's vanilla: section names it so.
            edit.Path = Canonical(ToUtf8(PathOf(actor)), edit.Map);
            edit.Level = edit.Path.substr(0, edit.Path.find('.'));
            edit.ClassName = ToUtf8(NameOf(actor->Class));
            edit.Now = PoseOf(actor);
            // Spawn() takes them back: the YAML has nothing of a new spawn.
            edit.Base = edit.Now;
            edit.BaseHidden = false;
            edit.BaseLight = edit.Light;
            if (edit.Light && edit.Light->Kind == LightKind::Npc)
            {
                if (auto const [npc, light] = NpcLightOf(actor); npc != nullptr)
                {
                    edit.Npc = NpcLink{ npc->Tag, light->Offset };
                    edit.BaseOffset = light->Offset;
                    // What the last build shows: levels.json's pose plus its offset. A move or a turn of the NPC (the editor's, or a
                    // pass applying its entry) carries a light with no edit raw, so where it stands now need not be where the install
                    // has it; Mark copies this base into imported.before, which Judge compares with the installed light.
                    edit.Base->Location = PlaceOf(NpcPose{ npc->Location, npc->Yaw }, light->Offset);
                }
            }
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

        /// A new edit of an untouched level light (docs/design/in-game-editor.md §12, 5b): its state now, which is also its base (what the level file or
        /// the YAML holds). Saved under its canonical path (a fork's light by its vanilla file), with its collection's name.
        Edit& NewLightEdit(AActor* owner, UPointLightComponent* light)
        {
            auto& edit = G.Edits.emplace_back();
            BindLight(edit, owner, light);
            edit.Map = ToUtf8(MapName());
            edit.Path = Canonical(ToUtf8(PathOf(light)), edit.Map);
            edit.Level = edit.Path.substr(0, edit.Path.find('.'));
            edit.ClassName = ToUtf8(NameOf(light->Class));
            edit.Owner = ToUtf8(NameOf(owner));
            edit.Now = LightPose(light);
            edit.Base = edit.Now;
            edit.BaseHidden = false;
            edit.BaseLight = edit.Light;
            return edit;
        }

        std::string AwaitingLight(UPointLightComponent* light);

        /// Touch for a level light: its edit, made on first touch, unless a session entry or an edit waiting for its level names it.
        Edit* TouchLight(AActor* owner, UPointLightComponent* light, char const* verb, void (*say)(std::string) = SayOnce)
        {
            if (auto const found = LightEditOf(light)) return found;
            if (auto const why = AwaitingLight(light); !why.empty())
            {
                say(std::format("not {}: {}", verb, why));
                G.PassNow = true;
                return nullptr;
            }
            return &NewLightEdit(owner, light);
        }

        /// Moves a level light alone (docs/design/in-game-editor.md §12, 5b; spike S1): the Translation that puts it there through its collection's matrix,
        /// through SetTranslation, which updates that one light (a reattach of the collection would redo all of them). Raw while its
        /// level is loaded but not visible: it takes its place when it attaches. False, writing nothing, when the matrix can't be
        /// inverted.
        bool MoveLight(UPointLightComponent* light, FVector to, bool visible)
        {
            FVector translation;
            if (!TranslationFor(light, to, translation)) return false;
            if (visible && Fn.SetTranslation != nullptr)
            {
                UPointLightComponent_execSetTranslation_Parms set;
                set.NewTranslation = translation;
                CallProcessEvent(light, Fn.SetTranslation, &set);
            }
            else light->Translation = translation;
            return true;
        }

        /// The one-light refresh (spike S1): ForceUpdate(false) reattaches this light alone, so its fields show (a raw write shows
        /// nothing until then).
        void RefreshLight(UPointLightComponent* light)
        {
            if (Fn.ForceUpdate == nullptr) return;
            UActorComponent_execForceUpdate_Parms update;
            update.bTransformOnly = 0;
            CallProcessEvent(light, Fn.ForceUpdate, &update);
        }

        void UpdateComponents(AActor* actor)
        {
            if (Fn.ForceUpdateComponents == nullptr || Shielded(actor, "a reattach")) return;
            AActor_execForceUpdateComponents_Parms update;
            update.bCollisionUpdate = 1;
            update.bTransformOnly = 0;
            CallProcessEvent(actor, Fn.ForceUpdateComponents, &update);
        }

        /// bHidden, bCollideActors, bBlockActors: through SetHidden and SetCollision (opcode natives) when the actor's level is
        /// visible, so the engine updates its collision and touching; raw in a level loaded but not visible.
        void WriteFlags(AActor* actor, HideFlags const& flags, bool visible)
        {
            // A light is bHidden by class default and has no collision: it is switched off instead (WriteLight).
            if (IsLight(actor) || Shielded(actor, "bHidden and collision")) return;
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
            if (e.Light)
            {
                // A spawned light the editor deleted is switched off, not hidden: bEnabled is enabled && !deleted. The caller
                // reattaches (a level light's: RefreshLight; Delete switches one off through its settings, never this).
                e.Deleted = deleted;
                if (e.Component) WriteKeptFields(e.Comp, *e.Light, deleted);
                else ApplyLight(e.Actor, *e.Light, deleted);
                return;
            }
            if (Shielded(e.Actor, "a delete")) return;
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
        /// DrawScale3D raw, hidden or shown, then the components follow, unless the actor's level is loaded but not visible. A
        /// light: its location and its settings (the edit's Light), on as Light->Enabled && !deleted, then one reattach; its
        /// rotation and scales stay the actor's (only the location counts). A level light (5b): its place through MoveLight, its
        /// settings through the kept writer, then the one-light refresh; its collection is never written. A place its
        /// collection's matrix can't reach leaves it where it is.
        void Put(Edit& e, Pose const& pose, bool deleted)
        {
            auto const actor = e.Actor;
            auto const visible = Visible(actor);
            if (e.Component)
            {
                auto place = PlaceOfLight(e.Comp);
                // The place asked for, not the one read back through the matrix: a float off would read as a change at the next Save.
                if (Near(place, pose.Location, LocationNoise) || MoveLight(e.Comp, pose.Location, visible)) place = pose.Location;
                e.Now = { place, {}, 1, { 1, 1, 1 } };
                e.Deleted = deleted;
                if (e.Light) WriteKeptFields(e.Comp, *e.Light, deleted);
                if (visible) RefreshLight(e.Comp);
                return;
            }
            if (Shielded(actor, "a pose")) return;
            if (e.Light)
            {
                actor->Location = pose.Location;
                e.Now = PoseOf(actor);
                e.Deleted = deleted;
                ApplyLight(actor, *e.Light, deleted);
                if (visible) UpdateComponents(actor);
                return;
            }
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
            // Never for a light: lights are bHidden by class default, and the YAML never removes one.
            if (e.Light) return;
            auto const defaults = e.Actor->Class != nullptr ? static_cast<AActor*>(e.Actor->Class->ClassDefaultObject) : nullptr;
            e.ShownFlags = { false, defaults == nullptr || defaults->bCollideActors != 0, defaults == nullptr || defaults->bBlockActors != 0 };
            e.Deleted = true;
        }

        // ! Undo
        // ========================================

        /// An NPC light's offset, as EntryState and StepState keep it; none for any other edit.
        std::optional<FVector> NpcOffset(Edit const& e) { return e.Npc ? std::optional<FVector>(e.Npc->Offset) : std::nullopt; }

        StepState StateOf(Edit const& e) { return { !e.Deleted, e.ShownFlags, e.Now, e.Light, NpcOffset(e) }; }

        std::optional<size_t> LinkOf(Edit const& light);

        /// The light a step writes besides its prop, if it still does. A spawn's or a delete's auto light only while the link
        /// holds: undone after a save or a tuning, the prop goes and the light, now the user's own, stays. A light that followed a
        /// drag of its prop whenever it has its actor: undo and redo only put back what the drag did to it, and the light is
        /// there again as the drag found it or left it once every later step is undone, link or not.
        std::optional<size_t> AlsoOf(Step const& step)
        {
            if (!step.Also || *step.Also >= G.Edits.size() || !Bound(G.Edits[*step.Also])) return std::nullopt;
            auto const drag = step.Kind == StepKind::Move || step.Kind == StepKind::Turn || step.Kind == StepKind::Scale;
            if (!drag && LinkOf(G.Edits[*step.Also]) != step.Target) return std::nullopt;
            return step.Also;
        }

        /// "move ed_crate_x_1", "spawn ed_crate_x_1 and ed_light_x_2", "turn mymod_npc_02 with its 2 lights": what the toolbar's
        /// Undo and Redo name. A step's light only when the step writes it (AlsoOf).
        std::string Label(Step const& step)
        {
            constexpr char const* Kinds[] = { "spawn", "delete", "move", "turn", "scale", "change" };
            std::string kind = Kinds[static_cast<int>(step.Kind)];
            auto const& before = step.Before.Light;
            auto const& after = step.After.Light;
            if (step.Kind == StepKind::Light && before && after && before->Enabled != after->Enabled) kind = after->Enabled ? "switch on" : "switch off";
            auto text = std::format("{} {}", kind, step.Target < G.Edits.size() ? Called(G.Edits[step.Target]) : "?");
            if (auto const also = AlsoOf(step)) text += " and " + Called(G.Edits[*also]);
            if (step.Carried.size() == 1) text += " with its light";
            else if (step.Carried.size() > 1) text += std::format(" with its {} lights", step.Carried.size());
            return text;
        }

        /// The props whose prop lighting an edit's change may change (docs/design/in-game-editor.md §11): an ours light's change any prop's, a prop's
        /// change its own. An NPC's or a level's light is never on the prop lights' channel (§12). An effect's change kills and
        /// restarts its particles (§12). A level light's change resets the light environments in its reach (5b). Checked at the
        /// end of the tick (Reswitch, Rerun, Reset): this only takes note, since CloseDrag runs in Fail(), which must not touch
        /// game memory.
        void NoteChanged(size_t index)
        {
            if (index >= G.Edits.size()) return;
            auto const& e = G.Edits[index];
            if (e.Component) G.Resets.insert(index);
            else if (e.Light)
            {
                if (!KeptKind(e.Light->Kind)) G.ReswitchAll = true;
            }
            else if (e.Effect) G.Rerun.insert(index);
            else G.Reswitch.insert(index);
        }

        /// A new step clears what could be redone.
        void PushStep(Step step)
        {
            G.Redo.clear();
            G.Undo.push_back(std::move(step));
            while (G.Undo.size() > MaxSteps) G.Undo.pop_front();
        }

        /// Ends the drag step under way: its after is the edit's state now, the followed light's (Follow), and each carried
        /// light's place at the NPC's pose now (Carry), read from the edits only, so Fail() can call this. The light stays in the
        /// step only if it changed; a drag that changed neither leaves no step.
        void CloseDrag()
        {
            if (!G.Drag) return;
            auto step = std::move(*G.Drag);
            G.Drag.reset();
            auto const share = std::exchange(G.DragShare, -1.f);
            if (step.Target >= G.Edits.size()) return;
            step.After = StateOf(G.Edits[step.Target]);
            NpcPose const pose{ step.After.Transform.Location, step.After.Transform.Rotation.Yaw };
            for (auto& c : step.Carried) c.After = PlaceOf(pose, c.Offset);
            if (step.Also)
            {
                if (*step.Also < G.Edits.size()) step.AlsoAfter = StateOf(G.Edits[*step.Also]);
                if (*step.Also >= G.Edits.size() || SameStep(step.AlsoBefore, step.AlsoAfter)) step.Also.reset();
            }
            // A prop that came back where it started while its light did not (the view moved mid-drag) still leaves a step.
            if (SameStep(step.Before, step.After) && !step.Also) return;
            NoteChanged(step.Target);
            if (step.Also) NoteChanged(*step.Also);
            // A prop's drag: the reswitch says so when it leaves the prop under half its target (docs/design/in-game-editor.md §11).
            if (share >= 0) G.DragWarn = { step.Target, share };
            PushStep(std::move(step));
        }

        std::optional<float> PropShare(AActor* actor);
        std::optional<size_t> LinkedLight(size_t prop);
        std::string NeededBy(AActor* light, AActor* prop);
        bool Covered(AActor* prop);
        FVector SideOf(AActor* prop, FVector light);
        std::vector<CarriedLight> LightsOf(std::string const& tag);

        /// The first drag command of a drag opens its step, with the edit's state before it; one for another edit or tool closes
        /// the step under way first. A move or a turn of one of our NPCs carries its NPC lights (docs/design/in-game-editor.md §12): each joins the step
        /// with its offset and where it stands (Carry). A prop's share of its target is taken then, for the warning when the drag
        /// ends. A prop's linked auto light joins the step, to follow it (Follow), with the side of the prop it stands on, unless
        /// another shown prop needs the light where it is (NeededBy), as a delete of the prop keeps it then. One a drag deleted
        /// (DragDeleted) joins only while no other light covers the prop: a light added for it since (L, a room light) is the
        /// user's choice.
        void OpenDrag(Edit const& edit, StepKind kind)
        {
            auto const index = IndexOf(edit);
            if (G.Drag && (G.Drag->Target != index || G.Drag->Kind != kind)) CloseDrag();
            if (G.Drag) return;
            G.Drag = Step{ index, kind, StateOf(edit), {} };
            G.DragShare = -1;
            G.DragSide = {};
            if ((kind == StepKind::Move || kind == StepKind::Turn) && Bound(edit))
            {
                if (auto const npc = NpcOfPawn(edit.Actor)) G.Drag->Carried = LightsOf(npc->Tag);
            }
            if (!edit.Light && Bound(edit))
            {
                if (auto const share = PropShare(edit.Actor)) G.DragShare = *share;
                if (auto const light = LinkedLight(index))
                {
                    auto const& made = G.Edits[*light];
                    if (made.Deleted && Covered(edit.Actor)) return;
                    if (auto const other = NeededBy(made.Actor, edit.Actor); !other.empty())
                    {
                        Say(std::format("{} stays where it is: {} needs it", Called(made), other));
                        return;
                    }
                    G.Drag->Also = light;
                    G.Drag->AlsoBefore = StateOf(made);
                    G.DragSide = SideOf(edit.Actor, made.Now.Location);
                }
            }
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
            G.RunUntil = 0;
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

        /// Lets the paused world run for a while (docs/design/in-game-editor.md §12): an effect's particles only move in Tick, so a moved or restarted
        /// effect shows where it is now only once the world has run. The editor's own release, beside TickOnce: the editor
        /// sets bPlayersOnly again at every tick, so an outside write would last one frame. The takeback in Tick waits for the
        /// deadline (a later call only moves it on), then takes the pause back without counting it as the game's. Returns
        /// whether the world runs. Never while the game had its own pause when the editor opened: for the whole release the
        /// takeback can't tell a 0 the game writes (its pause over) from ours, so closing would give back a pause it ended.
        bool TickFor(ULONGLONG ms)
        {
            if (!G.Open || !Alive(G.PausedWorld, G.PausedIndex)) return false;
            if (G.WasPlayersOnly) return false;
            G.PausedWorld->bPlayersOnly = 0;
            G.RunUntil = std::max(G.RunUntil, GetTickCount64() + ms);
            return true;
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
            Fn.SetEnvironmentEnabled = find(L"Engine.LightEnvironmentComponent.SetEnabled");
            Fn.KillParticlesForced = find(L"Engine.ParticleSystemComponent.KillParticlesForced");
            Fn.ActivateSystem = find(L"Engine.ParticleSystemComponent.ActivateSystem");
            Fn.SetTranslation = find(L"Engine.PointLightComponent.SetTranslation");
            Fn.ForceUpdate = find(L"Engine.ActorComponent.ForceUpdate");
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

        void CheckLevels(bool now);

        void SetOpen(APlayerController* pc, bool open)
        {
            if (open == G.Open) return;
            if (open)
            {
                ResolveFunctions();
                // npc_place and mod_build rewrite it while the map runs (docs/design/in-game-editor.md §12).
                CheckLevels(true);
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
                // Until then the editor can't tell our levels from vanilla ones: every actor keeps Phase 3's rules.
                if (!G.Ours.Read && !G.Ours.Problem.empty()) Say(G.Ours.Problem);
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
        /// Bounds that hold the camera, or room-sized ones, are skipped, and so are particle systems (docs/design/in-game-editor.md §12): an effect's
        /// box (a dust card's is about 1000 units across) would take the click from a crate behind it. Effects are picked by
        /// their icon.
        AActor* PickByBounds(FVector origin, FVector dir, APlayerController* pc, float& distance)
        {
            AActor* best = nullptr;
            distance = 1e30f;
            auto& objects = *UObject::GObjObjects;
            auto const primitive = UPrimitiveComponent::StaticClass();
            auto const particles = UParticleSystemComponent::StaticClass();
            for (UINT i = 0; i < objects.Count(); i++)
            {
                auto const object = objects.GetData()[i];
                if (object == nullptr || !object->IsA(primitive) || Common::IsDefaultObject(object) || Common::IsDestroyed(object)) continue;
                if (object->IsA(particles)) continue;
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

        // ! Vanilla actors
        // ========================================

        /// levels.json's content into ours and its keys: a missing key, or one of another type, reads as empty, and an unknown
        /// one is skipped. Tags are kept lower-case: FNames ignore case. Throws BridgeError on a version this bridge doesn't know.
        void ParseLevels(json const& doc, LevelsFile& ours, std::vector<std::string>& keys)
        {
            if (!doc.is_object()) throw BridgeError("not a JSON object");
            if (auto const version = doc.find("version"); version != doc.end() && (!version->is_number() || version->get<double>() != 1))
                throw BridgeError(std::format("version {}, and this bridge reads version 1", version->dump()));
            ours.Dlc = Text(doc, "dlc");
            auto const levels = doc.find("levels");
            if (levels == doc.end() || !levels->is_object()) return;
            for (auto const& item : levels->items())
            {
                auto const key = Lower(item.key());
                auto& streams = ours.Streams[key];
                keys.push_back(item.key());
                auto const& level = item.value();
                if (!level.is_object()) continue;
                if (auto const list = level.find("streams"); list != level.end() && list->is_array())
                {
                    for (auto const& name : *list)
                    {
                        if (name.is_string()) streams.push_back(name.get<std::string>());
                    }
                }
                // The props the switch may change and the lights the editor lists (docs/design/in-game-editor.md §11), by tag.
                for (auto const& [field, tags] : { std::pair{ "props", &ours.Props }, std::pair{ "lights", &ours.Lights } })
                {
                    auto const list = level.find(field);
                    if (list == level.end() || !list->is_array()) continue;
                    auto& set = (*tags)[key];
                    for (auto const& tag : *list)
                    {
                        if (tag.is_string()) set.insert(Lower(tag.get<std::string>()));
                    }
                }
                // Its NPCs as the build placed them, with their lights' offsets (docs/design/in-game-editor.md §12). One without a name, a location or a
                // yaw is skipped, and so is a light without three numbers.
                if (auto const npcs = level.find("npcs"); npcs != level.end() && npcs->is_object())
                {
                    auto& built = ours.Npcs[key];
                    auto& lights = ours.NpcLights[key];
                    for (auto const& npc : npcs->items())
                    {
                        auto const& value = npc.value();
                        double v[3];
                        auto const yaw = value.find("yaw");
                        if (!value.is_object() || Text(value, "name").empty() || !Numbers(value, "location", v) || yaw == value.end() || !yaw->is_number())
                            continue;
                        BuiltNpc n{ Text(value, "name"), npc.key(), { static_cast<float>(v[0]), static_cast<float>(v[1]), static_cast<float>(v[2]) },
                                    static_cast<int>(std::lround(yaw->get<double>())), {}, item.key() };
                        if (auto const list = value.find("lights"); list != value.end() && list->is_object())
                        {
                            for (auto const& light : list->items())
                            {
                                auto const& offset = light.value();
                                if (!offset.is_array() || offset.size() != 3 || !std::all_of(offset.begin(), offset.end(), [](json const& x) { return x.is_number(); }))
                                    continue;
                                lights[Lower(light.key())] = { built.size(), n.Lights.size() };
                                n.Lights.push_back({ light.key(), { offset[0].get<float>(), offset[1].get<float>(), offset[2].get<float>() } });
                            }
                        }
                        built.push_back(std::move(n));
                    }
                }
                // Its characterLights copies (Phase 5b): their lights are listed as the rig's, and ship into this level.
                if (auto const copies = level.find("characterLights"); copies != level.end() && copies->is_object())
                {
                    auto& map = ours.CharacterLights[key];
                    for (auto const& copy : copies->items())
                    {
                        if (copy.value().is_string()) map[Lower(copy.key())] = copy.value().get<std::string>();
                    }
                }
            }
        }

        /// A file's write time; none when it is missing or the time can't be read.
        std::optional<std::filesystem::file_time_type> LevelsWriteTime(std::filesystem::path const& file)
        {
            std::error_code error;
            auto const time = std::filesystem::last_write_time(file, error);
            if (error) return std::nullopt;
            return time;
        }

        /// levels.json (docs/design/in-game-editor.md §11), read again on each map change, and when its write time changes (CheckLevels): level_build
        /// rewrites it, npc_place too. A missing or broken file reads as unknown, and every actor keeps Phase 3's rules.
        void ReadLevels()
        {
            auto& ours = G.Ours;
            ours = {};
            auto summary = std::make_shared<LevelsState>();
            std::optional<std::filesystem::file_time_type> time;
            try
            {
                auto const file = std::filesystem::path(DataFolder()) / L"levels.json";
                summary->File = ToUtf8(file.wstring());
                // Before the read: a write that lands meanwhile is seen at the next check.
                time = LevelsWriteTime(file);
                if (!std::filesystem::exists(file))
                {
                    ours.Problem = "levels.json missing: run mod_build";
                }
                else
                {
                    std::ifstream in(file, std::ios::binary);
                    ParseLevels(json::parse(in), ours, summary->Levels);
                    ours.Read = true;
                }
            }
            catch (std::exception const& e)
            {
                ours = {};
                summary->Levels.clear();
                ours.Problem = std::format("levels.json can't be read ({}): run mod_build", e.what());
            }
            ours.WriteTime = time;
            summary->Problem = ours.Problem;
            summary->Dlc = ours.Dlc;
            for (auto const& [level, tags] : ours.Props) summary->Props += static_cast<int>(tags.size());
            for (auto const& [level, tags] : ours.Lights) summary->Lights += static_cast<int>(tags.size());
            for (auto const& [level, npcs] : ours.Npcs) summary->Npcs += static_cast<int>(npcs.size());
            for (auto const& [level, lights] : ours.NpcLights) summary->NpcLights += static_cast<int>(lights.size());
            for (auto const& [level, copies] : ours.CharacterLights) summary->CharacterLights += static_cast<int>(copies.size());
            ours.Summary = std::move(summary);
            if (ours.Read)
                LEASI_INFO("editor: {}: {} levels of ours, in {}; {} built props switched, {} lights listed, {} npcs with {} lights", ours.Summary->File,
                           ours.Streams.size(), ours.Dlc.empty() ? "no dlc folder" : ours.Dlc, ours.Summary->Props, ours.Summary->Lights, ours.Summary->Npcs,
                           ours.Summary->NpcLights);
            else
                LEASI_WARN("editor: {}; until then every actor keeps Phase 3's rules", ours.Problem);
        }

        /// levels.json again when its write time changed since it was read (docs/design/in-game-editor.md §12): npc_place builds, which rewrites it,
        /// and moves the pawn and its lights live, so the NPCs' built poses must follow. On F8 open (now) and once a second while
        /// the editor is open; the map change reads it anyway. The lights, props, effects and NPCs are tracked again, and a pass
        /// tries the NPC light entries that waited for their NPC's pose (the pose check) again.
        void CheckLevels(bool now)
        {
            auto const tick = GetTickCount64();
            if (!now && tick - G.LevelsCheckedAt < LevelsCheckMs) return;
            G.LevelsCheckedAt = tick;
            std::optional<std::filesystem::file_time_type> time;
            try
            {
                time = LevelsWriteTime(std::filesystem::path(DataFolder()) / L"levels.json");
            }
            catch (std::exception const&)
            {
                // No LOCALAPPDATA: ReadLevels said so at the map change.
                return;
            }
            if (time == G.Ours.WriteTime) return;
            LEASI_INFO("editor: levels.json changed since it was read: read again");
            ReadLevels();
            G.TrackedStale = true;
            G.ReswitchAll = true;
            G.PassWanted = true;
        }

        /// The file an object was loaded from: its linker's, while the game keeps the linker attached. Empty otherwise: a cooked
        /// game may detach a level's linker once it is loaded, and an actor spawned at run time never had one.
        std::string FileOf(UObject* object)
        {
            // Looked up once: a class FindClass doesn't find is looked for again on each call, through every object.
            static UClass* const linkerClass = ULinker::StaticClass();
            auto const linker = object != nullptr ? object->Linker : nullptr;
            if (!Real(linker) || !linker->IsA(linkerClass)) return {};
            std::wstring file(linker->Filename.Chars(), linker->Filename.Length());
            while (!file.empty() && file.back() == L'\0') file.pop_back();
            return ToUtf8(file);
        }

        /// Whether a file's path has this folder as one of its parts (either slash, any case).
        bool InFolder(std::string const& file, std::string const& folder)
        {
            for (size_t start = 0;;)
            {
                auto const end = file.find_first_of("\\/", start);
                if (SameText(file.substr(start, end == std::string::npos ? std::string::npos : end - start), folder)) return true;
                if (end == std::string::npos) return false;
                start = end + 1;
            }
        }

        /// One of our levels (docs/design/in-game-editor.md §11): a levels.json key, or a file in our DLC folder that is not a fork by the rule (a level
        /// of ours built before levels.json had it). The level by name, and its object (an actor's Outer) for its linker's file.
        bool OurLevel(std::string const& level, UObject* levelObject)
        {
            if (G.Ours.Streams.contains(Lower(level))) return true;
            if (G.Ours.Dlc.empty() || !VanillaOf(level, ToUtf8(MapName())).empty()) return false;
            return InFolder(FileOf(levelObject), G.Ours.Dlc);
        }

        /// What to do when OurLevel can't tell, or empty: a level that is not a levels.json key, not a fork by the rule, not a file
        /// a key streams (those are vanilla), and whose linker is detached, so its file is unknown. A level of ours that no build
        /// since Phase 4 has written into levels.json (a level_build of another YAML writes only its own key) then reads as
        /// vanilla: the gate refuses its npcs and usables, and its edits read as dev-only. Refusal and ShipsOf say so.
        std::string Unsure(std::string const& level, UObject* levelObject)
        {
            if (!G.Ours.Read || G.Ours.Streams.contains(Lower(level)) || !VanillaOf(level, ToUtf8(MapName())).empty()) return {};
            for (auto const& [key, streams] : G.Ours.Streams)
            {
                if (std::any_of(streams.begin(), streams.end(), [&](std::string const& file) { return SameText(file, level); })) return {};
            }
            if (!FileOf(levelObject).empty()) return {};
            return std::format("{} is not in levels.json and its file can't be read: if it is ours, run mod_build", level);
        }

        bool SpawnedByEditor(AActor* actor)
        {
            auto const edit = EditOf(actor);
            return edit != nullptr && edit->Spawned;
        }

        /// Changed under Phase 3's rules (docs/design/in-game-editor.md §10): an editor spawn, an actor of one of our levels, or any actor while
        /// levels.json is missing. Any other actor is a vanilla one, which the refusal gate below decides on.
        bool Phase3Rules(AActor* actor)
        {
            return !G.Ours.Read || SpawnedByEditor(actor) || OurLevel(LevelOf(actor), actor->Outer);
        }

        /// The classes of a vanilla level the editor changes: props, and effects (docs/design/in-game-editor.md §12: moved, turned, hidden; EffectRefusal
        /// gates them first). Matched up the class chain, by name.
        constexpr char const* EditableClasses[] = { "StaticMeshActor", "BioInert", "Emitter" };

        /// Why the editor leaves a vanilla actor of a class alone: the first of these up its class chain says it.
        struct ClassReason
        {
            char const* Class;
            char const* Why;
        };
        constexpr ClassReason ClassReasons[] = {
            { "BioDoor", "doors carry Kismet and door volumes" },
            { "BioUseable", "usables carry Kismet" },
            { "BioArtPlaceable", "placeables carry Kismet" },
            { "Pawn", "npcs carry Kismet and AI" },
            { "Controller", "npcs carry Kismet and AI" },
            { "CoverLink", "the AI's cover" },
            { "CoverSlotMarker", "the AI's cover" },
            { "PathNode", "the AI's paths" },
            { "NavigationPoint", "the AI's paths" },
            { "BioTriggerStream", "it streams the map's levels" },
            { "Volume", "volumes carry Kismet, streaming and physics" },
            { "Brush", "the level's geometry" },
            { "StaticLightCollectionActor", "the level's light collection: its lights are edited one by one (Lights tab)" },
            { "Light", "a vanilla light actor; no map of ours streams one, so it can't ship" },
            { "InterpActor", "Kismet moves it" },
            { "SkeletalMeshActor", "Kismet and matinees animate it" },
            { "Trigger", "triggers carry Kismet" },
        };

        /// "a BioDoor", "an Emitter".
        std::string Article(std::string const& name)
        {
            auto const vowel = !name.empty() && std::strchr("AEIOUaeiou", name[0]) != nullptr;
            return (vowel ? "an " : "a ") + name;
        }

        /// Why a vanilla actor's class keeps the editor away ("a BioDoor: doors carry Kismet and door volumes"), or empty for a
        /// prop: a StaticMeshActor or a BioInert, or a class derived from one.
        std::string ClassRefusal(UClass* cls)
        {
            char const* why = nullptr;
            for (UStruct const* s = cls; s != nullptr; s = static_cast<UStruct const*>(s->SuperField))
            {
                auto const name = ToUtf8(NameOf(s));
                auto const named = [&](char const* c) { return SameText(name, c); };
                if (std::any_of(std::begin(EditableClasses), std::end(EditableClasses), named)) return {};
                auto const known = std::find_if(std::begin(ClassReasons), std::end(ClassReasons), [&](ClassReason const& r) { return named(r.Class); });
                if (why == nullptr && known != std::end(ClassReasons)) why = known->Why;
            }
            if (why == nullptr) why = "only StaticMeshActors, BioInerts and Emitters of a vanilla level are edited";
            return std::format("{}: {}", Article(ToUtf8(NameOf(cls))), why);
        }

        /// Loaded from a level file, not spawned at run time (by the game, or live_place): it has a linker, or the load flags its
        /// export gave it (RF_LoadForClient, RF_LoadForServer, RF_LoadForEdit: a cooked game may detach a level's linker once
        /// the level is loaded). Spawn sets neither.
        bool FromFile(AActor* actor)
        {
            constexpr unsigned long long LoadFlags = 0x0007000000000000;
            return actor->Linker != nullptr || (static_cast<unsigned long long>(actor->ObjectFlags) & LoadFlags) != 0;
        }

        /// An actor's primitive components: Components (its file's) and AllComponents (those attached at run time too).
        std::vector<UPrimitiveComponent*> PrimitivesOf(AActor* actor)
        {
            std::vector<UPrimitiveComponent*> found;
            auto const primitive = UPrimitiveComponent::StaticClass();
            for (auto const list : { &actor->Components, &actor->AllComponents })
            {
                for (auto const component : *list)
                {
                    if (!Real(component) || !component->IsA(primitive)) continue;
                    auto const p = static_cast<UPrimitiveComponent*>(component);
                    if (std::find(found.begin(), found.end(), p) == found.end()) found.push_back(p);
                }
            }
            return found;
        }

        /// A particle system: its box is where its particles fly (a dust card's is about 1000 units across), not a size, so the
        /// room-shell test leaves it out (docs/design/in-game-editor.md §12) and a wide effect never reads as a room shell.
        bool Particles(UPrimitiveComponent* p)
        {
            static UClass* const particles = UParticleSystemComponent::StaticClass();
            return p->IsA(particles);
        }

        /// The largest half-extent of an actor's primitives' world boxes (Bounds.BoxExtent: each part's box turned and scaled as the
        /// actor stands now), particle systems left out; 0 with none.
        float ExtentOf(AActor* actor)
        {
            auto extent = 0.f;
            for (auto const p : PrimitivesOf(actor))
            {
                if (!Particles(p)) extent = std::max({ extent, p->Bounds.BoxExtent.X, p->Bounds.BoxExtent.Y, p->Bounds.BoxExtent.Z });
            }
            return extent;
        }

        /// The room-shell test's measure (docs/design/in-game-editor.md §11): ExtentOf the actor as its level file has it, before the editor's own edits,
        /// as editor_import and the build measure it at the file's pose. A scale or a turn made in the editor then never turns a
        /// prop into a room shell halfway through a drag, nor locks it after one. An actor with no edit stands as its file has
        /// it; an edit took the measure when it bound the actor (Bind). One bound while its level was not visible (a pass wrote
        /// its pose before its components attached): today's boxes, each world axis scaled back by the base's total scale over
        /// today's, the local axes' ratios weighted by how much of each lies along that world axis (exact for a turn by right
        /// angles). A turn since the base can't be taken back out of a world box: it stays in the measure.
        float FileExtentOf(AActor* actor)
        {
            auto const edit = EditOf(actor);
            if (edit != nullptr && edit->FileExtent) return *edit->FileExtent;
            double back[3] = { 1, 1, 1 };
            if (edit != nullptr && edit->Base)
            {
                auto const& base = *edit->Base;
                double const was[3] = { base.DrawScale * base.DrawScale3D.X, base.DrawScale * base.DrawScale3D.Y, base.DrawScale * base.DrawScale3D.Z };
                double const now[3] = { actor->DrawScale * actor->DrawScale3D.X, actor->DrawScale * actor->DrawScale3D.Y, actor->DrawScale * actor->DrawScale3D.Z };
                double axes[3][3];
                AxesOf(actor->Rotation, axes);
                for (int world = 0; world < 3; world++)
                {
                    auto ratio = 0.0;
                    for (int local = 0; local < 3; local++)
                    {
                        auto const r = was[local] / now[local];
                        ratio += axes[local][world] * axes[local][world] * (std::isfinite(r) && r > 0 ? r : 1);
                    }
                    back[world] = ratio;
                }
            }
            auto extent = 0.0;
            for (auto const p : PrimitivesOf(actor))
            {
                if (Particles(p)) continue;
                auto const& e = p->Bounds.BoxExtent;
                extent = std::max({ extent, e.X * back[0], e.Y * back[1], e.Z * back[2] });
            }
            return static_cast<float>(extent);
        }

        /// Why Delete refuses an actor under Phase 3's rules (docs/design/in-game-editor.md §10).
        std::string Undeletable(AActor* actor)
        {
            if (actor->IsA(APawn::StaticClass()) || actor->IsA(AController::StaticClass())) return "pawns and controllers are not deleted";
            if (actor->bStatic)
            {
                return G.Ours.Read ? "a static actor of one of our levels (baked into its lighting): change it in its YAML"
                                   : "a static actor (baked into the level's lighting): " + G.Ours.Problem;
            }
            if (actor->bNoDelete) return "the engine won't destroy it (bNoDelete)";
            return {};
        }

        /// The Kismet set (docs/design/in-game-editor.md §12): every object a SeqVar_Object's ObjValue or a SeqVar_ObjectList's ObjList names, in the
        /// Kismet of a loaded level (the persistent one or a streaming level's), with the variable that names it. Read when first
        /// needed after a map change or a level turned visible. Only the loaded levels: a variable of a level not loaded is
        /// missed, and level_build's fork, which reads the whole file, has the last word.
        void ReadKismet()
        {
            G.KismetStale = false;
            G.Kismet.clear();
            std::unordered_set<std::string> levels;
            for (auto const& level : G.Levels) levels.insert(Lower(LevelOf(level.Level)));
            auto& objects = *UObject::GObjObjects;
            auto const variableClass = USeqVar_Object::StaticClass();
            auto const listClass = USeqVar_ObjectList::StaticClass();
            auto variables = 0;
            for (UINT i = 0; i < objects.Count(); i++)
            {
                auto const object = objects.GetData()[i];
                if (!Real(object) || !object->IsA(variableClass) || !levels.contains(Lower(LevelOf(object)))) continue;
                auto const variable = static_cast<USeqVar_Object*>(object);
                auto const holder = std::format("{}.{}", ToUtf8(NameOf(object->Outer)), ToUtf8(NameOf(object)));
                auto const add = [&](UObject* held) {
                    if (Real(held)) G.Kismet.try_emplace(held, held->ObjectInternalInteger, holder);
                };
                add(variable->ObjValue);
                if (object->IsA(listClass))
                {
                    for (auto const held : static_cast<USeqVar_ObjectList*>(object)->ObjList) add(held);
                }
                variables++;
            }
            LEASI_INFO("editor: Kismet of {} loaded levels: {} object variables name {} objects", levels.size(), variables, G.Kismet.size());
        }

        /// The Kismet variable that names an actor ("Sequence_1791.SeqVar_Object_0"); empty when none of the loaded levels' does.
        std::string KismetHolder(AActor* actor)
        {
            if (G.KismetStale) ReadKismet();
            auto const it = G.Kismet.find(actor);
            return it != G.Kismet.end() && it->second.first == actor->ObjectInternalInteger ? it->second.second : std::string{};
        }

        /// The refusal of an actor Kismet holds (docs/design/in-game-editor.md §12), for effects and props alike: an import and a build would refuse
        /// its edit anyway.
        std::string KismetRefusal(AActor* actor)
        {
            auto const holder = KismetHolder(actor);
            return holder.empty() ? holder : std::format("Kismet holds it ({}): the story moves, hides or switches it", holder);
        }

        /// A ParticleSystemComponent's Template, read where the engine put it: the SDK's C++ field is elsewhere (top of file).
        UParticleSystem* TemplateOf(UParticleSystemComponent* component)
        {
            static int const at = PropertyOffset(UParticleSystemComponent::StaticClass(), L"Template");
            if (!Real(component) || at < 0) return nullptr;
            auto const system = *reinterpret_cast<UParticleSystem**>(reinterpret_cast<BYTE*>(component) + at);
            return Real(system) ? system : nullptr;
        }

        /// Its bAutoActivate, through reflection (a bit of a bitfield the SDK's C++ layout puts elsewhere).
        bool AutoActivates(UParticleSystemComponent* component)
        {
            return Real(component) && GetProperty(component, L"bAutoActivate", 0) == true;
        }

        /// Why the editor leaves an effect alone (docs/design/in-game-editor.md §12), in any level, ours included; empty when it edits it. One the game
        /// made at run time (an impact, a shield) has no load flags: no level file has it, and its entry would never match after a
        /// reload. One Kismet holds, or one that is off until Kismet or a matinee starts it, is the story's.
        std::string EffectRefusal(AActor* actor)
        {
            if (!FromFile(actor)) return "made at run time by the game (an impact, a shield, a footstep), not part of the map";
            if (auto why = KismetRefusal(actor); !why.empty()) return why;
            auto const component = static_cast<AEmitter*>(actor)->ParticleSystemComponent;
            if (!Real(component) || TemplateOf(component) == nullptr || !AutoActivates(component)) return "off until Kismet or a matinee starts it";
            return {};
        }

        /// Why the vanilla gate refuses an actor (docs/design/in-game-editor.md §11); empty when it may change: a prop or an effect (by class) loaded
        /// from its file, that no loaded Kismet holds (§12), not a room shell (measured as its file has it: FileExtentOf), and, to
        /// be deleted, with no part that stays drawn while it is hidden. bStatic and bNoDelete no longer refuse it: hiding
        /// destroys nothing.
        std::string VanillaRefusal(AActor* actor, bool deleting)
        {
            if (deleting && (actor->IsA(APawn::StaticClass()) || actor->IsA(AController::StaticClass()))) return "pawns and controllers are not deleted";
            if (auto why = ClassRefusal(actor->Class); !why.empty()) return why;
            if (!FromFile(actor)) return "spawned at run time: no level file has it";
            if (auto why = KismetRefusal(actor); !why.empty()) return why;
            // Its size in metres: a box twice its extent.
            if (auto const extent = FileExtentOf(actor); extent > MaxPropExtent)
                return std::format("a room shell ({:.0f} m): hiding it opens the room", extent * 2 / 100);
            if (deleting)
            {
                auto const parts = PrimitivesOf(actor);
                if (std::any_of(parts.begin(), parts.end(), [](UPrimitiveComponent* p) { return p->bIgnoreOwnerHidden != 0; }))
                    return "a part of it ignores its owner's bHidden (bIgnoreOwnerHidden): hidden, it would stay drawn";
            }
            return {};
        }

        /// Why the editor refuses to delete an actor (deleting), or to move, turn or scale it; empty when it may (docs/design/in-game-editor.md §11). An
        /// editor spawn and an actor of our levels keep Phase 3's rules, and so does every actor while levels.json is missing. Any
        /// other actor is a vanilla one: VanillaRefusal. When the bridge can't tell whether its level is ours (Unsure), the
        /// refusal says what to do.
        std::string NameFor(AActor* actor);
        std::string PoseRefusal(std::string const& tag);

        std::string Refusal(AActor* actor, bool deleting)
        {
            // Never one, in our levels too (the rig's copy is in our map): a pose or a reattach of it moves all its lights (5b).
            if (IsCollection(actor)) return ClassRefusal(actor->Class);
            if (IsLight(actor))
            {
                // A light is never hidden nor destroyed: Delete switches it off, so bStatic and bNoDelete don't matter
                // (docs/design/in-game-editor.md §11). Only a listed one is edited, a spawned one already deleted is not deleted
                // again, and a light of the YAML already off is not switched off again (that would only make an edit that changes
                // nothing). One of our levels that no levels.json entry names was built before the entry was (§12); one of a
                // vanilla level is a light actor no map of ours streams. An NPC's light is edited only while its NPC stands where
                // its offsets are measured from (the pose check).
                if (!ListedLight(actor))
                    return Phase3Rules(actor) ? "no levels.json entry names this light (run mod_build)"
                                              : "a vanilla light actor; no map of ours streams one, so it can't ship";
                if (auto const link = NpcLinkOf(actor))
                {
                    if (auto why = PoseRefusal(link->Tag); !why.empty()) return why;
                }
                auto const edit = EditOf(actor);
                if (deleting && edit != nullptr && edit->Deleted) return std::format("{} is deleted already", Called(*edit));
                if (deleting && (edit == nullptr || !edit->Spawned)
                    && !(edit != nullptr && edit->Light ? edit->Light->Enabled : LightOf(actor, SourceOf(actor)).Enabled))
                    return std::format("{} is switched off already (a light of the YAML is switched off, never removed)",
                                       edit != nullptr ? Called(*edit) : NameFor(actor));
                return {};
            }
            // An effect is gated in our levels too (docs/design/in-game-editor.md §12): the game spawns impacts and shields into the map's own level.
            if (actor->IsA(AEmitter::StaticClass()))
            {
                if (auto why = EffectRefusal(actor); !why.empty()) return why;
            }
            if (Phase3Rules(actor)) return deleting ? Undeletable(actor) : std::string{};
            auto why = VanillaRefusal(actor, deleting);
            if (why.empty()) return why;
            if (auto const unsure = Unsure(LevelOf(actor), actor->Outer); !unsure.empty()) why += "; " + unsure;
            return why;
        }

        /// Whether the running map streams a file now: a WorldInfo.StreamingLevels entry names it.
        bool StreamsNow(AWorldInfo* world, std::string const& file)
        {
            for (auto const streaming : world->StreamingLevels)
            {
                if (streaming != nullptr && SameText(NameText(streaming->PackageName), file)) return true;
            }
            return false;
        }

        /// Whether an edit of a vanilla actor ships (docs/design/in-game-editor.md §11): the actor stands in a fork already, or in a vanilla file the
        /// running map streams (its levels.json streams), which the next build forks. Anything else is dev-only: the bridge
        /// re-applies it, no build ships it (and when the bridge can't tell whether the level is ours, Unsure says what to do).
        /// No text for an editor spawn or an actor of our levels.
        struct Shipping
        {
            bool Ships = false;
            std::string Text;
        };

        Shipping ShipsOf(AActor* actor, AWorldInfo* world)
        {
            if (SpawnedByEditor(actor)) return {};
            if (!G.Ours.Read) return { false, "ships: unknown, " + G.Ours.Problem };
            auto const level = LevelOf(actor);
            if (OurLevel(level, actor->Outer)) return {};
            auto const map = ToUtf8(MapName());
            if (!VanillaOf(level, map).empty()) return { true, std::format("ships: forked into {}", level) };
            if (auto const ours = G.Ours.Streams.find(Lower(map)); ours != G.Ours.Streams.end())
            {
                auto const& streams = ours->second;
                if (std::any_of(streams.begin(), streams.end(), [&](std::string const& file) { return SameText(file, level); }))
                    return { true, std::format("ships: {} streams it (forked on the next build)", map) };
            }
            else if (OurLevel(map, world->Outer) && StreamsNow(world, level))
            {
                // A map of ours built before levels.json had it: whether its YAML streams the file, the next build tells.
                return { false, std::format("ships if {}'s YAML streams it: {} is not in levels.json yet, run mod_build", map, map) };
            }
            auto text = std::format("dev-only: no map of ours streams {}", level);
            if (auto const unsure = Unsure(level, actor->Outer); !unsure.empty()) text += "; " + unsure;
            return { false, std::move(text) };
        }

        // ! Level lights
        // ========================================

        /// A level light's command (docs/design/in-game-editor.md §12, 5b): the panel's change, a move, or a delete (it switches the light off).
        enum class LightOp { Change, Move, Delete };

        /// The vanilla file a collection of one of our levels was copied from (levels.json characterLights: the rig); empty for any
        /// other collection.
        std::string RigSourceOf(AActor* owner)
        {
            auto const level = G.Ours.CharacterLights.find(Lower(LevelOf(owner)));
            if (level == G.Ours.CharacterLights.end()) return {};
            auto const it = level->second.find(Lower(ToUtf8(NameOf(owner))));
            return it != level->second.end() ? it->second : std::string{};
        }

        /// Why the editor leaves a level light alone for a command (docs/design/in-game-editor.md §12, 5b), or empty. A collection of one of our levels
        /// that no characterLights item names: no build writes its lights. A move of one its collection's matrix scales: its
        /// Translation would be scaled too. A delete of one already off. A light of a file no map of ours streams is edited all the
        /// same: its ships line says dev-only.
        std::string LightRefusal(AActor* owner, UPointLightComponent* light, LightOp op)
        {
            if (G.Ours.Read && OurLevel(LevelOf(owner), owner->Outer) && RigSourceOf(owner).empty())
                return std::format("{} is a light collection of {} that no characterLights item names: no build writes its lights",
                                   ToUtf8(NameOf(owner)), LevelOf(owner));
            if (op == LightOp::Move && ParentScaleOf(light) > MaxParentScale) return "its light collection scales it";
            if (op == LightOp::Delete)
            {
                auto const edit = LightEditOf(light);
                if (!(edit != nullptr && edit->Light ? edit->Light->Enabled : light->bEnabled != 0))
                    return std::format("{} is switched off already (Enabled in the panel switches it on)", ToUtf8(NameOf(light)));
            }
            return {};
        }

        /// Whether a level light's edit ships (docs/design/in-game-editor.md §12, 5b): a characterLights copy's is built into the map; any other ships as
        /// an edit of its collection would (ShipsOf: in a fork, or dev-only).
        std::string LightShips(AActor* owner, AWorldInfo* world)
        {
            if (auto const source = RigSourceOf(owner); !source.empty())
                return std::format("ships: built into {} (characterLights copy of {})", LevelOf(owner), source);
            return ShipsOf(owner, world).Text;
        }

        /// A level light's collection and component, as an entry or a pass finds them.
        struct LevelLight
        {
            AActor* Owner = nullptr;
            UPointLightComponent* Light = nullptr;
        };

        /// The point and spot lights of a static light collection: its Components (at run time LightComponents is empty,
        /// docs/design/in-game-editor.md §12). Its sun and sky lights are other classes, so never among them.
        template <typename Each>
        void LightsIn(AActor* owner, Each const& each)
        {
            static UClass* const point = UPointLightComponent::StaticClass();
            for (auto const component : owner->Components)
            {
                if (Real(component) && component->IsA(point)) each(static_cast<UPointLightComponent*>(component));
            }
        }

        // ! Prop lighting
        // ========================================

        /// What the switch writes of a prop (docs/design/in-game-editor.md §11): its static mesh components that show a mesh through a dynamic light
        /// environment, and those environments. None for a mesh lit by its lightmap (a StaticMeshActor's): the switch leaves it.
        struct PropParts
        {
            std::vector<UStaticMeshComponent*> Meshes;
            std::vector<ULightEnvironmentComponent*> Environments;
        };

        PropParts PartsOf(AActor* actor)
        {
            PropParts parts;
            auto const meshClass = UStaticMeshComponent::StaticClass();
            auto const environmentClass = UDynamicLightEnvironmentComponent::StaticClass();
            for (auto const p : PrimitivesOf(actor))
            {
                auto const environment = p->LightEnvironment;
                if (!p->IsA(meshClass) || static_cast<UStaticMeshComponent*>(p)->StaticMesh == nullptr || !Real(environment) || !environment->IsA(environmentClass))
                    continue;
                parts.Meshes.push_back(static_cast<UStaticMeshComponent*>(p));
                if (std::find(parts.Environments.begin(), parts.Environments.end(), environment) == parts.Environments.end()) parts.Environments.push_back(environment);
            }
            return parts;
        }

        /// A prop's bounds: the box around its meshes' world boxes, and the sphere around their spheres. None while a mesh is not
        /// attached (its level is loaded but not visible): the engine computes bounds when it attaches.
        std::optional<PropBounds> BoundsOf(PropParts const& parts)
        {
            if (parts.Meshes.empty()) return std::nullopt;
            FVector lo{ 1e30f, 1e30f, 1e30f }, hi{ -1e30f, -1e30f, -1e30f };
            for (auto const mesh : parts.Meshes)
            {
                if (!mesh->bAttached) return std::nullopt;
                auto const& b = mesh->Bounds;
                lo = { std::min(lo.X, b.Origin.X - b.BoxExtent.X), std::min(lo.Y, b.Origin.Y - b.BoxExtent.Y), std::min(lo.Z, b.Origin.Z - b.BoxExtent.Z) };
                hi = { std::max(hi.X, b.Origin.X + b.BoxExtent.X), std::max(hi.Y, b.Origin.Y + b.BoxExtent.Y), std::max(hi.Z, b.Origin.Z + b.BoxExtent.Z) };
            }
            PropBounds bounds;
            bounds.Centre = { (lo.X + hi.X) / 2, (lo.Y + hi.Y) / 2, (lo.Z + hi.Z) / 2 };
            bounds.Extent = { (hi.X - lo.X) / 2, (hi.Y - lo.Y) / 2, (hi.Z - lo.Z) / 2 };
            for (auto const mesh : parts.Meshes) bounds.Radius = std::max(bounds.Radius, Distance(bounds.Centre, mesh->Bounds.Origin) + mesh->Bounds.SphereRadius);
            return bounds;
        }

        bool StillThere(Tracked const& t)
        {
            return Alive(t.Actor, t.Index) && t.Actor->Class == t.Class && !t.Actor->bDeleteMe
                && (t.Comp == nullptr || (Alive(t.Comp, t.CompIndex) && t.Comp->Class == t.CompClass));
        }

        /// How the status line names an actor with no edit: its tag when it is a real one, else its object name.
        std::string NameFor(AActor* actor)
        {
            auto const tag = NameText(actor->Tag);
            return RealTag(tag, ToUtf8(NameOf(actor->Class))) ? tag : ToUtf8(NameOf(actor));
        }

        /// The listed lights, our NPCs' lights, the switched props, the effects and our NPCs' pawns of the loaded levels
        /// (ListedLight, NpcLightOf, SwitchedProp, NpcOfPawn, docs/design/in-game-editor.md §12), read again when stale. A level's tag lists are looked
        /// up once per level, so no actor's path is built but a tracked one's, once. An NPC light is one whose edit has the npc
        /// kind, or whose tag levels.json names among its own level's npcs lights: it goes in OtherLights, which the props switch
        /// never walks. The point and spot lights of each static light collection go in LevelLights (5b), with their type, a
        /// characterLights copy's with the file it was copied from.
        void RefreshTracked()
        {
            if (!G.TrackedStale) return;
            G.TrackedStale = false;
            G.Lights.clear();
            G.OtherLights.clear();
            G.Props.clear();
            G.Effects.clear();
            G.Pawns.clear();
            G.LevelLights.clear();
            std::unordered_map<AActor*, Edit const*> edits;
            for (auto const& e : G.Edits)
            {
                // A level light's actor is its collection.
                if (Bound(e) && !e.Component) edits.emplace(e.Actor, &e);
            }
            auto const tags = [](std::unordered_map<std::string, std::unordered_set<std::string>> const& lists, std::string const& level) {
                auto const it = lists.find(level);
                return it != lists.end() ? &it->second : nullptr;
            };
            auto const map = ToUtf8(MapName());
            auto const emitterClass = AEmitter::StaticClass();
            auto const pawnClass = APawn::StaticClass();
            for (auto const& level : G.Levels)
            {
                auto const file = LevelOf(level.Level);
                auto const name = Lower(file);
                auto const lightTags = tags(G.Ours.Lights, name);
                auto const propTags = tags(G.Ours.Props, name);
                auto const npcLights = G.Ours.NpcLights.find(name);
                auto const npcs = G.Ours.Npcs.find(name);
                for (auto const actor : static_cast<ULevelBase*>(level.Level)->Actors)
                {
                    if (!Real(actor) || actor->bDeleteMe) continue;
                    auto const found = edits.find(actor);
                    auto const edit = found != edits.end() ? found->second : nullptr;
                    auto const tagged = [&](std::unordered_set<std::string> const* list) { return list != nullptr && list->contains(Lower(NameText(actor->Tag))); };
                    auto const track = [&](std::vector<Tracked>& list) -> Tracked& {
                        list.push_back({ actor, actor->ObjectInternalInteger, actor->Class, ToUtf8(PathOf(actor)) });
                        return list.back();
                    };
                    // One of its level's npcs, for a pawn.
                    auto const ourNpc = [&]() -> BuiltNpc const* {
                        if (npcs == G.Ours.Npcs.end() || !actor->IsA(pawnClass)) return nullptr;
                        auto const tag = NameText(actor->Tag);
                        auto const it = std::find_if(npcs->second.begin(), npcs->second.end(), [&](BuiltNpc const& n) { return SameText(n.Tag, tag); });
                        return it != npcs->second.end() ? &*it : nullptr;
                    };
                    if (IsCollection(actor))
                    {
                        auto const owner = ToUtf8(NameOf(actor));
                        auto const rig = RigSourceOf(actor);
                        LightsIn(actor, [&](UPointLightComponent* light) {
                            Tracked t{ actor, actor->ObjectInternalInteger, actor->Class, ToUtf8(PathOf(light)), LightSource::Vanilla };
                            t.Name = ToUtf8(NameOf(light));
                            t.Level = Canonical(file, map);
                            t.Forked = !VanillaOf(file, map).empty();
                            t.Comp = light;
                            t.CompIndex = light->ObjectInternalInteger;
                            t.CompClass = light->Class;
                            t.Type = TypeOf(light);
                            t.Owner = owner;
                            t.Rig = rig;
                            G.LevelLights.push_back(std::move(t));
                        });
                    }
                    else if (IsLight(actor))
                    {
                        // Its NPC, when levels.json names it among its level's npcs lights.
                        BuiltNpc const* npc = nullptr;
                        if (npcLights != G.Ours.NpcLights.end() && npcs != G.Ours.Npcs.end())
                        {
                            if (auto const it = npcLights->second.find(Lower(NameText(actor->Tag))); it != npcLights->second.end())
                                npc = &npcs->second[it->second.first];
                        }
                        if (edit != nullptr && edit->Light && KeptKind(edit->Light->Kind))
                        {
                            auto& t = track(G.OtherLights);
                            t.Source = SourceOf(edit->Light->Kind);
                            if (edit->Npc) t.Npc = edit->Npc->Tag;
                        }
                        else if (npc != nullptr)
                        {
                            auto& t = track(G.OtherLights);
                            t.Source = LightSource::Npc;
                            t.Npc = npc->Tag;
                        }
                        else if ((edit != nullptr && edit->Light) || tagged(lightTags))
                        {
                            track(G.Lights);
                        }
                    }
                    else if (actor->IsA(emitterClass))
                    {
                        auto& t = track(G.Effects);
                        t.Name = ToUtf8(NameOf(actor));
                        auto const component = static_cast<AEmitter*>(actor)->ParticleSystemComponent;
                        auto const system = TemplateOf(component);
                        t.Template = system != nullptr ? ToUtf8(NameOf(system)) : "no template";
                        // A fork's effects by their vanilla file: what the session and the YAML name.
                        t.Level = Canonical(file, map);
                        t.Forked = !VanillaOf(file, map).empty();
                        t.Refusal = Refusal(actor, false);
                    }
                    else if (auto const npc = ourNpc(); npc != nullptr)
                    {
                        auto& t = track(G.Pawns);
                        t.Npc = npc->Tag;
                        t.Level = file;
                    }
                    else if ((edit != nullptr && edit->Spawned) || tagged(propTags))
                    {
                        G.Props.push_back({ actor, actor->ObjectInternalInteger, actor->Class });
                    }
                }
            }
        }

        /// The pawn of an NPC in the loaded levels: the one with its tag in its level. Null when its level is not loaded.
        AActor* PawnOf(BuiltNpc const& npc)
        {
            RefreshTracked();
            for (auto const& t : G.Pawns)
            {
                if (StillThere(t) && SameText(t.Npc, npc.Tag) && SameText(t.Level, npc.Level)) return t.Actor;
            }
            return nullptr;
        }

        /// Why an NPC's lights are not edited now (docs/design/in-game-editor.md §12), or empty. Their offsets are measured from the NPC's reference
        /// pose: levels.json must list the NPC, and its pawn, unless the editor moved or turned it in this session, must stand
        /// within 5 units and 2 degrees of its built pose. The game never moves our NPCs (spike S5); npc_place does, and rewrites
        /// levels.json, which CheckLevels reads again. That covers npc_place only while the map stays loaded: npc_place builds
        /// but doesn't install, so a reload puts the pawn back where the install has it, away from levels.json's new pose.
        std::string PoseRefusal(std::string const& tag)
        {
            if (!G.Ours.Read) return G.Ours.Problem;
            auto const npc = NpcNamed(tag);
            if (npc == nullptr) return std::format("levels.json has no NPC {} (built before Phase 5): run mod_build", tag);
            if (PawnEditOf(*npc) != nullptr) return {};
            auto const pawn = PawnOf(*npc);
            auto const name = Title(npc->Name);
            if (pawn == nullptr) return std::format("{}'s pawn ({}) is not in the loaded levels", name, npc->Tag);
            constexpr char const Fix[] = "install the last build (mod_install, game closed): a reload shows the installed level, not npc_place's build";
            if (auto const drift = Distance(pawn->Location, npc->Location); drift > MaxNpcDrift)
                return std::format("{} stands {:.1f} from where levels.json puts them (the last build): {}", name, drift, Fix);
            if (auto const turn = std::fabs(Degrees(pawn->Rotation.Yaw - npc->Yaw)); turn > MaxNpcTurn)
                return std::format("{} is turned {:.1f}\u00b0 from where levels.json puts them (the last build): {}", name, turn, Fix);
            return {};
        }

        /// An NPC's lights in the loaded levels (OtherLights), deleted spawns included, as a move or a turn of the NPC carries
        /// them: each with its offset (its edit's, else levels.json's) and where it stands now.
        std::vector<CarriedLight> LightsOf(std::string const& tag)
        {
            RefreshTracked();
            std::vector<CarriedLight> lights;
            for (auto const& t : G.OtherLights)
            {
                if (t.Source != LightSource::Npc || !SameText(t.Npc, tag) || !StillThere(t)) continue;
                auto const link = NpcLinkOf(t.Actor);
                if (!link) continue;
                lights.push_back({ t.Actor, t.Index, t.Class, link->Offset, t.Actor->Location, t.Actor->Location });
            }
            return lights;
        }

        /// Puts a carried light at a place: through its edit when it has one (Put: the location, then one reattach), which is
        /// unsaved then and keeps its offset; else raw Location and a component update, as a built light moves live (spike S5).
        void PutCarried(CarriedLight const& c, FVector at)
        {
            if (!Alive(c.Actor, c.Index) || c.Actor->Class != c.Class) return;
            if (auto const edit = EditOf(c.Actor))
            {
                auto pose = edit->Now;
                pose.Location = at;
                Put(*edit, pose, edit->Deleted);
                edit->Saved = false;
                return;
            }
            c.Actor->Location = at;
            if (Visible(c.Actor)) UpdateComponents(c.Actor);
        }

        /// A move or a turn of one of our NPCs takes its lights along (docs/design/in-game-editor.md §12), at each drag command: each to the pawn's
        /// pose now plus its offset.
        void Carry()
        {
            if (!G.Drag || G.Drag->Carried.empty() || G.Drag->Target >= G.Edits.size()) return;
            auto const& pawn = G.Edits[G.Drag->Target];
            NpcPose const pose{ pawn.Now.Location, pawn.Now.Rotation.Yaw };
            for (auto& c : G.Drag->Carried)
            {
                c.After = PlaceOf(pose, c.Offset);
                if (Alive(c.Actor, c.Index) && c.Actor->Class == c.Class && !Near(c.Actor->Location, c.After, LocationNoise)) PutCarried(c, c.After);
            }
        }

        /// Puts each light of an NPC at its reference pose plus its offset (docs/design/in-game-editor.md §12): after a pass wrote the NPC's edit.
        void PlaceLights(std::string const& tag)
        {
            auto const npc = NpcNamed(tag);
            if (npc == nullptr) return;
            auto const pose = ReferencePose(*npc);
            for (auto const& c : LightsOf(tag))
            {
                if (auto const at = PlaceOf(pose, c.Offset); !Near(c.Before, at, LocationNoise)) PutCarried(c, at);
            }
        }

        /// The listed lights of the visible levels as the prop lighting rule sees them, on or off. A light of a level loaded but not
        /// visible lights nothing: its components are not attached. Ours only: an NPC's light (OtherLights) is on Dynamic, never
        /// on the prop lights' channel.
        std::vector<LightSeen> CoverLights()
        {
            RefreshTracked();
            std::vector<LightSeen> lights;
            for (auto const& t : G.Lights)
            {
                if (!StillThere(t) || !Visible(t.Actor)) continue;
                auto const component = PointOf(t.Actor);
                if (component == nullptr) continue;
                lights.push_back({ t.Actor, t.Actor->Location, LightOf(t.Actor, LightSource::Ours), (BitsOf(component->LightingChannels) & ChannelGameplay4) != 0 });
            }
            return lights;
        }

        /// Whether a seed gives nothing: brightness 0, or black (DarkSeed).
        bool Dark(Seed const& seed) { return seed.Brightness * Luminance(seed.Color) < DarkSeed; }

        /// A new prop light's brightness and colour (docs/design/in-game-editor.md §11): the last prop light edited in this map, else the nearest prop
        /// light that is on, else 0.15 white. One that gives nothing is passed over: the new light would cover its prop, turn it
        /// black and make its target nothing, so the selection would read 100%.
        Seed SeedFor(FVector at, std::vector<LightSeen> const& lights)
        {
            if (G.LastSeed && !Dark(*G.LastSeed)) return *G.LastSeed;
            LightSeen const* nearest = nullptr;
            for (auto const& l : lights)
            {
                if (!l.Light.Enabled || l.Light.Kind != LightKind::Props || Dark({ l.Light.Brightness, l.Light.Color })) continue;
                if (nearest == nullptr || Distance(l.Location, at) < Distance(nearest->Location, at)) nearest = &l;
            }
            return nearest != nullptr ? Seed{ nearest->Light.Brightness, nearest->Light.Color } : Seed{};
        }

        /// How prop lights light a switched prop: whether one covers it (when none does, the switched-off ones that would, as the
        /// build's note names them), what it gets from them (E), where a fresh Auto light would stand and its radius from where the
        /// view is (over MaxAutoRadius, Auto light and L add none), and what it would give it (T).
        struct PropLighting
        {
            PropBounds Bounds;
            bool Covered = false;
            std::vector<AActor*> CoveringOff;
            float Received = 0;
            Placement Auto;
            float Target = 0;
        };

        /// Whether a light counts for a prop. A built prop (levels.json props) is switched at its build by the lights: of its own
        /// file only, so in game only its own level's lights and the editor's spawned ones (imported with it) count; a spawn counts
        /// them all.
        bool CountsFor(LightSeen const& light, AActor* prop)
        {
            if (auto const edit = EditOf(prop); edit != nullptr && edit->Spawned) return true;
            auto const edit = EditOf(light.Actor);
            return (edit != nullptr && edit->Spawned) || Lower(LevelOf(light.Actor)) == Lower(LevelOf(prop));
        }

        std::vector<LightSeen> LightsFor(AActor* prop, std::vector<LightSeen> const& lights)
        {
            std::vector<LightSeen> counted;
            std::copy_if(lights.begin(), lights.end(), std::back_inserter(counted), [&](LightSeen const& l) { return CountsFor(l, prop); });
            return counted;
        }

        /// None for an actor the switch leaves alone, and for a prop with no bounds yet.
        std::optional<PropLighting> LightingOf(AActor* actor, std::vector<LightSeen> const& all)
        {
            if (!SwitchedProp(actor) || !Visible(actor)) return std::nullopt;
            auto const bounds = BoundsOf(PartsOf(actor));
            if (!bounds) return std::nullopt;
            auto const lights = LightsFor(actor, all);
            PropLighting lighting;
            lighting.Bounds = *bounds;
            lighting.Covered = std::any_of(lights.begin(), lights.end(), [&](LightSeen const& l) { return Covers(l, bounds->Centre); });
            if (!lighting.Covered)
            {
                for (auto const& l : lights)
                {
                    if (!l.Light.Enabled && Reaches(l, bounds->Centre)) lighting.CoveringOff.push_back(l.Actor);
                }
            }
            lighting.Received = Received(lights, bounds->Centre);
            lighting.Auto = AutoPlacement(*bounds, G.ViewLocation, G.ViewYaw);
            lighting.Target = Target(*bounds, lighting.Auto, SeedFor(bounds->Centre, lights));
            return lighting;
        }

        /// E over T for a switched prop; none for anything else.
        std::optional<float> PropShare(AActor* actor)
        {
            auto const lighting = LightingOf(actor, CoverLights());
            if (!lighting) return std::nullopt;
            return ShareOf(lighting->Received, lighting->Target);
        }

        /// Whether a light that counts for a switched prop covers it now.
        bool Covered(AActor* actor)
        {
            auto const lighting = LightingOf(actor, CoverLights());
            return lighting && lighting->Covered;
        }

        /// A mesh on the prop lights' channel alone, bInitialized aside.
        bool OnPropChannel(DWORD bits) { return (bits & ~ChannelInitialized) == ChannelGameplay4; }

        /// The channels a mesh goes back to once no prop light covers it: its own from before the switch first covered it, else its
        /// archetype's (a prop the build covered: the build writes the archetype's back too), else a dynamic mesh's.
        DWORD UncoveredChannels(UStaticMeshComponent* mesh)
        {
            if (auto const it = G.MeshChannels.find(mesh); it != G.MeshChannels.end() && it->second.first == mesh->ObjectInternalInteger) return it->second.second;
            auto const archetype = mesh->ObjectArchetype;
            if (archetype != nullptr && archetype->IsA(UPrimitiveComponent::StaticClass()))
            {
                auto const bits = BitsOf(static_cast<UPrimitiveComponent*>(archetype)->LightingChannels);
                if (!OnPropChannel(bits) && (bits & ~ChannelInitialized) != 0) return bits;
            }
            return DynamicMeshChannels;
        }

        /// SetEnabled when the function was found: the environment drops or takes its lights. Raw otherwise.
        void SetEnvironment(ULightEnvironmentComponent* environment, bool on)
        {
            if (Fn.SetEnvironmentEnabled == nullptr)
            {
                environment->bEnabled = on ? 1 : 0;
                return;
            }
            ULightEnvironmentComponent_execSetEnabled_Parms parms;
            parms.bNewEnabled = on ? 1 : 0;
            CallProcessEvent(environment, Fn.SetEnvironmentEnabled, &parms);
        }

        /// The switch of docs/design/in-game-editor.md §11 for one shown prop of a visible level: covered by a light, its light environment off and its
        /// meshes on Gameplay_4 alone; not covered, its environment on and its meshes back on their own channels. A prop already so
        /// is left alone. The environment off first, then the channels, two reattaches (one leaves the environment's lights on the
        /// old channels: spike), then the environment on again when uncovered. Returns whether it wrote anything.
        bool SwitchProp(AActor* actor, std::vector<LightSeen> const& lights)
        {
            if (actor->bHidden || !Visible(actor)) return false;
            auto const parts = PartsOf(actor);
            auto const bounds = BoundsOf(parts);
            if (!bounds) return false;
            auto const covered = std::any_of(lights.begin(), lights.end(), [&](LightSeen const& l) { return CountsFor(l, actor) && Covers(l, bounds->Centre); });
            auto const right = std::all_of(parts.Environments.begin(), parts.Environments.end(), [&](ULightEnvironmentComponent* e) { return (e->bEnabled != 0) != covered; })
                               && std::all_of(parts.Meshes.begin(), parts.Meshes.end(), [&](UStaticMeshComponent* m) { return OnPropChannel(BitsOf(m->LightingChannels)) == covered; });
            if (right) return false;
            for (auto const environment : parts.Environments) SetEnvironment(environment, false);
            for (auto const mesh : parts.Meshes)
            {
                auto const bits = BitsOf(mesh->LightingChannels);
                if (covered)
                {
                    if (!OnPropChannel(bits)) G.MeshChannels[mesh] = { mesh->ObjectInternalInteger, bits };
                    SetBits(mesh->LightingChannels, CoveredMeshChannels);
                }
                else if (OnPropChannel(bits))
                {
                    SetBits(mesh->LightingChannels, UncoveredChannels(mesh));
                }
            }
            UpdateComponents(actor);
            UpdateComponents(actor);
            if (!covered)
            {
                for (auto const environment : parts.Environments) SetEnvironment(environment, true);
            }
            return true;
        }

        /// At the end of the tick: the switch of the props a change, a pass or a spawn asked for (NoteChanged, ReswitchAll), then the
        /// line for a prop drag that left its prop under half its target.
        void Reswitch()
        {
            if (!G.ReswitchAll && G.Reswitch.empty() && !G.DragWarn) return;
            auto const lights = CoverLights();
            std::vector<AActor*> props;
            if (G.ReswitchAll)
            {
                for (auto const& t : G.Props)
                {
                    if (StillThere(t)) props.push_back(t.Actor);
                }
            }
            else
            {
                for (auto const i : G.Reswitch)
                {
                    if (i < G.Edits.size() && Bound(G.Edits[i]) && SwitchedProp(G.Edits[i].Actor)) props.push_back(G.Edits[i].Actor);
                }
            }
            G.ReswitchAll = false;
            G.Reswitch.clear();
            auto switched = 0;
            for (auto const actor : props)
            {
                if (SwitchProp(actor, lights)) switched++;
            }
            if (switched > 0)
            {
                LEASI_INFO("editor: {} prop{} switched between prop lights and their light environment", switched, switched == 1 ? "" : "s");
                // Paused, a light environment turned on keeps its old light until the world ticks.
                TickOnce();
            }
            auto const warn = std::exchange(G.DragWarn, std::nullopt);
            if (!warn || warn->first >= G.Edits.size() || !Bound(G.Edits[warn->first])) return;
            auto const& e = G.Edits[warn->first];
            if (auto const lighting = LightingOf(e.Actor, lights))
            {
                auto const share = ShareOf(lighting->Received, lighting->Target);
                if (share < DragWarnShare && warn->second >= DragWarnShare)
                    Say(std::format("{} gets {:.0f}% of its target from prop lights here: L adds one", Called(e), share * 100));
            }
        }

        /// Reswitch, guarded on its own: a fault leaves it off until the loaded levels change, as a pass's does. No C++ object may
        /// live in Guarded's frame, so the work is here.
        void ReswitchEntry(UObject*)
        {
            try
            {
                Reswitch();
            }
            catch (std::exception const& e)
            {
                LEASI_ERROR("editor: prop lighting: {}", e.what());
            }
        }

        // ! Effects
        // ========================================

        /// Whether an effect's template loops (docs/design/in-game-editor.md §12): one of its emitters repeats for ever (EmitterLoops 0 in its first
        /// level of detail's required module). Else it ends, and a moved one shows nothing once it has.
        bool Loops(UParticleSystem* system)
        {
            static int const at = PropertyOffset(UParticleSystem::StaticClass(), L"Emitters");
            if (!Real(system) || at < 0) return false;
            // Not system->Emitters: the SDK's C++ ParticleSystem is 8 bytes short before it (top of file).
            auto const& emitters = *reinterpret_cast<TArray<UParticleEmitter*>*>(reinterpret_cast<BYTE*>(system) + at);
            for (auto const emitter : emitters)
            {
                if (!Real(emitter) || emitter->LODLevels.Count() == 0) continue;
                auto const level = emitter->LODLevels(0);
                if (Real(level) && Real(level->RequiredModule) && level->RequiredModule->EmitterLoops == 0) return true;
            }
            return false;
        }

        /// What the world's run after a step shows (Rerun, Reset): added to the status line of what the step said this tick (a
        /// delete, an undo), and only it logged (the step's line is), or said on its own (the end of a drag). Nothing when empty.
        void SayAfter(std::string what)
        {
            if (what.empty()) return;
            if (G.StatusFrame != G.Frame || G.Status.empty()) return Say(std::move(what));
            LEASI_INFO("editor: {}", what);
            G.Status += "; " + what;
        }

        /// At the end of the tick: each effect a step changed (NoteChanged: a move, a turn, a delete, an undo or redo, a pass that
        /// applied one) kills its particles and starts again where it is now (docs/design/in-game-editor.md §12), then the paused world runs 1.5 s
        /// (TickFor) so they show. Without the kill, the old particles stay where they were (spike S4). A deleted (hidden) one is
        /// only killed: it shows nothing, and SetHidden and the kill show at once while paused (S4), so it lets no world run.
        /// Live only: nothing of it ships.
        void Rerun()
        {
            if (G.Rerun.empty()) return;
            auto const which = std::exchange(G.Rerun, {});
            if (Fn.KillParticlesForced == nullptr || Fn.ActivateSystem == nullptr) return;
            auto count = 0;
            for (auto const i : which)
            {
                if (i >= G.Edits.size() || !G.Edits[i].Effect || !Bound(G.Edits[i]) || !Visible(G.Edits[i].Actor)) continue;
                auto const& e = G.Edits[i];
                auto const component = static_cast<AEmitter*>(e.Actor)->ParticleSystemComponent;
                if (!Real(component)) continue;
                UParticleSystemComponent_execKillParticlesForced_Parms kill;
                CallProcessEvent(component, Fn.KillParticlesForced, &kill);
                if (!e.Deleted)
                {
                    UParticleSystemComponent_execActivateSystem_Parms activate;
                    activate.bFlagAsJustAttached = 0;
                    CallProcessEvent(component, Fn.ActivateSystem, &activate);
                    count++;
                }
            }
            if (count == 0) return;
            // TickFor refuses while the game had its own pause when F8 opened: the particles show once the editor closes.
            SayAfter(TickFor(EffectRunMs) ? "effects run 1.5 s"
                     : G.WasPlayersOnly   ? "effects not run: the game had paused the world when F8 opened"
                                          : "");
        }

        /// At the end of the tick (docs/design/in-game-editor.md §12, 5b): after a step of a baked or character light (B, C: NoteChanged), each enabled
        /// light environment whose owner stands within the light's reach before or after the step is reset (SetEnabled false,
        /// then true), then the paused world runs 1.5 s (TickFor): an environment takes in a light only while the world ticks
        /// (spike S2). A U or D light has a proxy, which its refresh updated. One switched to prop lights (§11) is off: left so.
        /// Live only: nothing of it ships.
        void Reset()
        {
            if (G.Resets.empty()) return;
            auto const which = std::exchange(G.Resets, {});
            std::vector<std::pair<FVector, float>> spheres;
            for (auto const i : which)
            {
                if (i >= G.Edits.size() || !G.Edits[i].Component || !Bound(G.Edits[i])) continue;
                auto& e = G.Edits[i];
                auto const now = std::pair{ PlaceOfLight(e.Comp), e.Comp->Radius };
                auto const was = std::exchange(e.ResetFrom, now);
                if ((e.Type != 'B' && e.Type != 'C') || !Visible(e.Actor)) continue;
                spheres.push_back(now);
                if (was && (!Near(was->first, now.first, LocationNoise) || was->second != now.second)) spheres.push_back(*was);
            }
            if (spheres.empty()) return;
            auto& objects = *UObject::GObjObjects;
            auto const environmentClass = UDynamicLightEnvironmentComponent::StaticClass();
            auto count = 0;
            for (UINT i = 0; i < objects.Count(); i++)
            {
                auto const object = objects.GetData()[i];
                if (!Real(object) || !object->IsA(environmentClass)) continue;
                auto const environment = static_cast<ULightEnvironmentComponent*>(object);
                auto const owner = environment->Owner;
                if (!environment->bEnabled || !Real(owner) || owner->bDeleteMe || !Visible(owner)) continue;
                auto const reached = std::any_of(spheres.begin(), spheres.end(), [&](auto const& sphere) {
                    return Distance(owner->Location, sphere.first) <= sphere.second + EnvironmentReach;
                });
                if (!reached) continue;
                SetEnvironment(environment, false);
                SetEnvironment(environment, true);
                count++;
            }
            LEASI_INFO("editor: {} light environment{} reset after a level light step", count, count == 1 ? "" : "s");
            if (count == 0 || !G.Open) return;
            SayAfter(TickFor(LightRunMs) ? std::format("characters lit again: {} light environment{} reset, the world runs 1.5 s", count, count == 1 ? "" : "s")
                     : G.WasPlayersOnly  ? "characters not updated: the game had paused the world when F8 opened (they update once it runs)"
                                         : "");
        }

        /// Reset, guarded on its own: a fault drops the resets it had to do.
        void ResetEntry(UObject*)
        {
            try
            {
                Reset();
            }
            catch (std::exception const& e)
            {
                LEASI_ERROR("editor: light environments: {}", e.what());
            }
        }

        /// Rerun, guarded on its own: a fault drops the effects it had to run.
        void RerunEntry(UObject*)
        {
            try
            {
                Rerun();
            }
            catch (std::exception const& e)
            {
                LEASI_ERROR("editor: effects: {}", e.what());
            }
        }

        /// The prop an auto light was made for, while the link holds (docs/design/in-game-editor.md §11): neither ever saved, and the light unedited (a
        /// move or a panel change clears MadeFor).
        std::optional<size_t> LinkOf(Edit const& light)
        {
            if (!light.MadeFor || light.EverSaved || *light.MadeFor >= G.Edits.size() || G.Edits[*light.MadeFor].EverSaved) return std::nullopt;
            return light.MadeFor;
        }

        /// A prop's linked auto light that a drag of the prop takes along: the link holds, the light has its actor, and it is
        /// shown or a drag deleted it (DragDeleted). A light the user deleted stays so; Ctrl+Z brings it back, linked.
        std::optional<size_t> LinkedLight(size_t prop)
        {
            for (size_t i = 0; i < G.Edits.size(); i++)
            {
                auto const& light = G.Edits[i];
                if (light.Light && (!light.Deleted || light.DragDeleted) && LinkOf(light) == prop && Bound(light)) return i;
            }
            return std::nullopt;
        }

        /// A shown switched prop other than this one that needs the light, by name: the light covers it and no other light that
        /// counts for it does, so without the light it would go back to its light environment. Delete keeps a linked light for
        /// it, and a drag leaves it where it is. A prop another light covers too doesn't need it: it only loses brightness that
        /// light added on top of its own (a prop spawned beside one already lit gets a light that covers both). Empty for none.
        std::string NeededBy(AActor* light, AActor* prop)
        {
            auto const lights = CoverLights();
            auto const seen = std::find_if(lights.begin(), lights.end(), [&](LightSeen const& l) { return l.Actor == light; });
            if (seen == lights.end()) return {};
            for (auto const& t : G.Props)
            {
                if (!StillThere(t) || t.Actor == prop || t.Actor->bHidden || !Visible(t.Actor)) continue;
                auto const bounds = BoundsOf(PartsOf(t.Actor));
                if (!bounds || !CountsFor(*seen, t.Actor) || !Covers(*seen, bounds->Centre)) continue;
                auto const others = std::any_of(lights.begin(), lights.end(), [&](LightSeen const& l) {
                    return l.Actor != light && CountsFor(l, t.Actor) && Covers(l, bounds->Centre);
                });
                if (others) continue;
                auto const edit = EditOf(t.Actor);
                return edit != nullptr ? Called(*edit) : NameFor(t.Actor);
            }
            return {};
        }

        /// The side of a prop a light stands on: the horizontal direction from the prop's bounds centre to it, unit length. Zero
        /// when the prop has no bounds or the light stands right over its centre.
        FVector SideOf(AActor* prop, FVector light)
        {
            auto const bounds = BoundsOf(PartsOf(prop));
            if (!bounds) return {};
            auto const dx = light.X - bounds->Centre.X, dy = light.Y - bounds->Centre.Y;
            auto const length = std::hypot(dx, dy);
            return length >= 1 ? FVector{ dx / length, dy / length, 0 } : FVector{};
        }

        /// Where a followed light goes (Follow): the first of these that needs no radius over MaxAutoRadius.
        /// - Where a fresh Auto light would, from the view now (see Follow).
        /// - From the side the light stood on when the drag began (DragSide): a view moved to a long side or a corner of the
        ///   prop can need more than the side the light was placed from, and a move changes nothing of what that side needs.
        /// - From the box's narrower side, toward the view: the ray leaves the box soonest there, so it needs the smallest radius
        ///   any view gives.
        /// Over MaxAutoRadius still, the last: no side fits one prop light, and its radius is the least the prop needs.
        Placement FollowPlacement(PropBounds const& bounds)
        {
            auto at = AutoPlacement(bounds, G.ViewLocation, G.ViewYaw);
            if (at.Radius <= MaxAutoRadius) return at;
            // A point 1000 units out on a side stands for a camera there: AutoPlacement only takes its direction.
            auto const from = [&](FVector side) { return AutoPlacement(bounds, Add(bounds.Centre, Scale(side, 1000)), G.ViewYaw); };
            if (G.DragSide.X != 0 || G.DragSide.Y != 0)
            {
                at = from(G.DragSide);
                if (at.Radius <= MaxAutoRadius) return at;
            }
            auto const alongX = bounds.Extent.X <= bounds.Extent.Y;
            auto const toward = (alongX ? G.ViewLocation.X - bounds.Centre.X : G.ViewLocation.Y - bounds.Centre.Y) < 0 ? -1.f : 1.f;
            return from(alongX ? FVector{ toward, 0, 0 } : FVector{ 0, toward, 0 });
        }

        /// A drag of a prop takes its linked auto light along (docs/design/in-game-editor.md §11's link), at each drag command, once the prop's
        /// components were updated (its bounds are where it is now): the light goes where a fresh Auto light would, for the prop's
        /// bounds now and from the view now, with the radius that gives; its brightness, colour, falloff and kind stay.
        /// - The view now, not the light's old side: T is measured from it, so the light meets T by construction and neither the
        ///   drag warning nor the uncovered switch fires; and it lights the faces the user looks at. A kept side could give an
        ///   elongated prop about half its target (a light at the end of a 3:1 crate seen from its long side).
        /// - Every command, not only the last: the drag's step holds the light from its first command (OpenDrag) and closes from
        ///   the edits alone (CloseDrag runs in Fail(), which must not touch game memory), and the prop, lit by the light live,
        ///   stays lit while it moves instead of darkening until the release.
        /// When the view now needs a radius over MaxAutoRadius, the light keeps its side, or failing that takes the side that needs
        /// the least (FollowPlacement): where the view stands never deletes a light that fits from some side. While none does, the
        /// prop is too big for one prop light and the light is deleted, as Auto light would add none (DragDeleted); it comes back
        /// once the prop fits, within this drag or a later one, and Ctrl+Z brings both back. Either way the prop is switched at the
        /// end of this tick (to its light environment, and back), not at the release, so it is never left on a light that is off.
        void Follow()
        {
            if (!G.Drag || !G.Drag->Also) return;
            auto const target = G.Drag->Target, also = *G.Drag->Also;
            if (target >= G.Edits.size() || also >= G.Edits.size()) return;
            auto const& prop = G.Edits[target];
            auto& light = G.Edits[also];
            if (!Bound(prop) || !Bound(light) || !light.Light) return;
            auto const bounds = BoundsOf(PartsOf(prop.Actor));
            if (!bounds) return;
            auto const at = FollowPlacement(*bounds);
            auto const visible = Visible(light.Actor);
            if (at.Radius > MaxAutoRadius)
            {
                // Said once, when it goes: the needed radius changes with every command.
                if (light.Deleted) return;
                SetDeleted(light, true, visible);
                light.DragDeleted = true;
                if (visible) UpdateComponents(light.Actor);
                light.Saved = false;
                NoteChanged(also);
                Say(std::format("{} is too big for one prop light from any side (radius {:.0f} at least, over {:.0f}): its Auto light {} is "
                                "deleted with this drag, and comes back once it fits (Ctrl+Z brings both back); {}",
                                Called(prop), at.Radius, MaxAutoRadius, Called(light), RoomLightHint));
                return;
            }
            auto const back = light.Deleted;
            if (!back && Near(light.Actor->Location, at.Location, LocationNoise) && std::fabs(light.Light->Radius - at.Radius) <= 0.01f) return;
            light.Light->Radius = at.Radius;
            auto pose = light.Now;
            pose.Location = at.Location;
            Put(light, pose, false);
            light.DragDeleted = false;
            light.Saved = false;
            if (back)
            {
                NoteChanged(also);
                Say(std::format("{} fits one prop light again: {} follows it", Called(prop), Called(light)));
            }
        }

        // ========================================

        /// The selection, for a drag command: none when nothing is selected, when it is hidden (deleted; undo shows it again), or
        /// when the editor refuses to change it (a vanilla actor that is not a prop, a room shell: Refusal). A light is bHidden by
        /// class default: it is moved on or off, and only a spawned light the editor deleted is refused.
        AActor* DragTarget(char const* verb)
        {
            if (!SelectionAlive() || Fn.ForceUpdateComponents == nullptr) return nullptr;
            if (IsLight(G.Selected))
            {
                if (auto const edit = EditOf(G.Selected); edit != nullptr && edit->Deleted)
                {
                    SayOnce(std::format("not {}: {} is deleted (Ctrl+Z brings it back)", verb, Called(*edit)));
                    return nullptr;
                }
            }
            else if (G.Selected->bHidden)
            {
                SayOnce(std::format("not {}: {} is hidden", verb, ToUtf8(NameOf(G.Selected))));
                return nullptr;
            }
            if (auto const why = Refusal(G.Selected, false); !why.empty())
            {
                SayOnce(std::format("not {}: {}", verb, why));
                return nullptr;
            }
            return G.Selected;
        }

        /// A panel command's settings held to the panel's ranges against the light's now: a field the command changes is clamped,
        /// one it sends back as the light has it passes as it is.
        LightBlock Held(LightBlock wanted, LightBlock const& now)
        {
            auto const held = [](float value, float low, float high, float was) {
                if (value == was) return value;
                return std::isfinite(value) ? std::clamp(value, low, high) : was;
            };
            wanted.Radius = held(wanted.Radius, MinLightRadius, MaxLightRadius, now.Radius);
            wanted.Brightness = held(wanted.Brightness, 0, MaxBrightness, now.Brightness);
            wanted.Falloff = held(wanted.Falloff, MinFalloff, MaxFalloff, now.Falloff);
            for (auto& c : wanted.Color) c = std::clamp(c, 0, 255);
            return wanted;
        }

        /// The selected level light, for a command (docs/design/in-game-editor.md §12, 5b): none when no level light is selected, or when the editor
        /// refuses it this command (said once, or with say).
        UPointLightComponent* LightTarget(char const* verb, LightOp op, void (*say)(std::string) = SayOnce)
        {
            if (!SelectionAlive() || G.SelectedComp == nullptr) return nullptr;
            if (auto const why = LightRefusal(G.Selected, G.SelectedComp, op); !why.empty())
            {
                say(std::format("not {}: {}", verb, why));
                return nullptr;
            }
            return G.SelectedComp;
        }

        /// A move of a level light (docs/design/in-game-editor.md §12, 5b): each drag command moves that light alone (MoveLight); the light environments
        /// in its reach are reset once the drag ends (CloseDrag, Reset).
        void MoveLevelLight(FVector to)
        {
            auto const light = LightTarget("moved", LightOp::Move);
            if (light == nullptr || Near(PlaceOfLight(light), to, LocationNoise)) return;
            if (Fn.SetTranslation == nullptr)
            {
                SayOnce("not moved: PointLightComponent.SetTranslation was not found");
                return;
            }
            FVector translation;
            if (!TranslationFor(light, to, translation))
            {
                SayOnce("not moved: its light collection's matrix can't be inverted");
                return;
            }
            auto const owner = G.Selected;
            auto const edit = TouchLight(owner, light, "moved");
            if (edit == nullptr) return;
            OpenDrag(*edit, StepKind::Move);
            MoveLight(light, to, Visible(owner));
            edit->Now.Location = to;
            edit->Saved = false;
        }

        /// The panel on a level light (docs/design/in-game-editor.md §12, 5b): the kept writer, then the one-light refresh (a U or D light shows it at
        /// once; a B or C one has no proxy, and characters take it when the drag ends: Reset). Its kind stays vanilla.
        void SetLevelLight(LightBlock wanted)
        {
            auto const light = LightTarget("changed", LightOp::Change);
            if (light == nullptr) return;
            auto const owner = G.Selected;
            auto const existing = LightEditOf(light);
            auto const now = existing != nullptr && existing->Light ? *existing->Light : LevelLightOf(light);
            wanted = Held(wanted, now);
            wanted.Kind = LightKind::Vanilla;
            if (wanted == now) return;
            auto const edit = TouchLight(owner, light, "changed");
            if (edit == nullptr || !edit->Light || wanted == *edit->Light) return;
            OpenDrag(*edit, StepKind::Light);
            edit->Light = wanted;
            WriteKeptFields(light, wanted, edit->Deleted);
            if (Visible(owner)) RefreshLight(light);
            edit->Saved = false;
        }

        /// Delete of a level light (docs/design/in-game-editor.md §12, 5b): switched off, a light step, never hidden nor destroyed; a baked one for
        /// characters and props only (the walls keep their lightmaps). LightRefusal turns down one already off.
        void DeleteLevelLight()
        {
            auto const light = LightTarget("deleted", LightOp::Delete, Say);
            if (light == nullptr) return;
            auto const owner = G.Selected;
            auto const touched = TouchLight(owner, light, "deleted", Say);
            if (touched == nullptr || !touched->Light) return;
            auto& edit = *touched;
            auto const before = StateOf(edit);
            edit.Light->Enabled = false;
            WriteKeptFields(light, *edit.Light, edit.Deleted);
            if (Visible(owner)) RefreshLight(light);
            edit.Saved = false;
            PushStep({ IndexOf(edit), StepKind::Light, before, StateOf(edit) });
            NoteChanged(IndexOf(edit));
            Select(nullptr);
            auto text = std::format("switched {} off (Ctrl+Z switches it on)", Called(edit));
            if (edit.Type == 'B') text += "; switched off for characters and props only: the walls stay lit";
            Say(std::move(text));
        }

        /// The overlay sends only the field its tool changes, and only that field is written: raw, then the components follow.
        void Move(FVector to)
        {
            if (G.SelectedComp != nullptr) return MoveLevelLight(to);
            auto const actor = DragTarget("moved");
            // A click on an arrow without motion changes nothing, and makes no edit.
            if (actor == nullptr || Near(actor->Location, to, LocationNoise)) return;
            // As live_place does: Location raw (SetLocation refuses bStatic actors, and blocking ones where they would overlap).
            auto const edit = Touch(actor, "moved");
            if (edit == nullptr) return;
            OpenDrag(*edit, StepKind::Move);
            // A built NPC light (a Toggleable, not bMovable) moves live this way too (spike S5).
            actor->Location = to;
            UpdateComponents(actor);
            edit->Now.Location = to;
            edit->Saved = false;
            // A moved NPC light has a new offset from its NPC's reference pose (docs/design/in-game-editor.md §12); Refusal made sure levels.json lists
            // the NPC.
            if (edit->Npc)
            {
                if (auto const npc = NpcNamed(edit->Npc->Tag)) edit->Npc->Offset = OffsetOf(ReferencePose(*npc), to);
            }
            // A moved auto light is the user's own: it no longer goes with its prop.
            edit->MadeFor.reset();
            Follow();
            Carry();
        }

        void Rotate(FRotator to)
        {
            if (G.SelectedComp != nullptr)
            {
                if (SelectionAlive())
                    SayOnce(G.SelectedComp->IsA(USpotLightComponent::StaticClass()) ? "not turned: a spot light's aim is not edited (its cone stays as built)"
                                                                                    : "not turned: a light only moves (a point light shines all ways)");
                return;
            }
            auto const actor = DragTarget("turned");
            if (actor == nullptr) return;
            if (IsLight(actor))
            {
                SayOnce("not turned: a light only moves (a point light shines all ways)");
                return;
            }
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
            Follow();
            Carry();
        }

        /// DrawScale3D only: DrawScale is never changed by the editor.
        void Rescale(FVector to)
        {
            if (G.SelectedComp != nullptr)
            {
                if (SelectionAlive()) SayOnce("not scaled: a light only moves (its radius is in the panel)");
                return;
            }
            auto const actor = DragTarget("scaled");
            if (actor == nullptr) return;
            if (IsLight(actor))
            {
                SayOnce("not scaled: a light only moves (its radius is in the panel)");
                return;
            }
            if (actor->IsA(APawn::StaticClass()))
            {
                SayOnce("not scaled: npcs keep a yaw and no scale");
                return;
            }
            if (actor->IsA(AEmitter::StaticClass()))
            {
                SayOnce("not scaled: an effect moves and turns (how its particles follow a scale is unchecked)");
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
            Follow();
        }

        /// The panel's settings for the selected light, all of them: a drag command, one step from the first change to the release.
        /// A field the command changes is held to the panel's range; one it sends back as the light has it passes as it is, so a
        /// lights: entry built outside the ranges (a radius of 6000) keeps it when another control moves. A command that changes
        /// nothing makes no edit. A prop light's brightness and colour seed the next prop light of the map. The kind is the
        /// light's source's (docs/design/in-game-editor.md §12): only ours switch between prop and room lights, and an NPC's or a level's light keeps
        /// its kind whatever the command says. A level light's goes to SetLevelLight.
        void SetLight(LightBlock wanted)
        {
            if (G.SelectedComp != nullptr) return SetLevelLight(wanted);
            auto const actor = DragTarget("changed");
            if (actor == nullptr || !IsLight(actor)) return;
            auto const existing = EditOf(actor);
            auto const now = existing != nullptr && existing->Light ? *existing->Light : LightOf(actor, SourceOf(actor));
            wanted = Held(wanted, now);
            if (KeptKind(now.Kind) || KeptKind(wanted.Kind)) wanted.Kind = now.Kind;
            if (wanted == now) return;
            auto const edit = Touch(actor, "changed");
            if (edit == nullptr || !edit->Light || wanted == *edit->Light) return;
            OpenDrag(*edit, StepKind::Light);
            edit->Light = wanted;
            ApplyLight(actor, wanted, edit->Deleted);
            if (Visible(actor)) UpdateComponents(actor);
            edit->Saved = false;
            // Tuned, an auto light is the user's own.
            edit->MadeFor.reset();
            if (wanted.Kind == LightKind::Props) G.LastSeed = Seed{ wanted.Brightness, wanted.Color };
        }

        /// Delete of a listed light (docs/design/in-game-editor.md §11): switched off, never destroyed nor hidden, so undo writes the old state back to the
        /// same actor. A built light: light.enabled false, a light step. A spawned light: deleted, like a spawned prop. Refusal has
        /// turned down a built light already off before anything touches it: it would leave an edit that changes nothing.
        void DeleteLight(AActor* actor)
        {
            auto const touched = Touch(actor, "deleted", Say);
            if (touched == nullptr || !touched->Light) return;
            auto& edit = *touched;
            auto const before = StateOf(edit);
            auto const visible = Visible(actor);
            auto kind = StepKind::Delete;
            std::string text;
            if (edit.Spawned)
            {
                SetDeleted(edit, true, visible);
                text = std::format("deleted {} (Ctrl+Z brings it back)", Called(edit));
                // Never saved, a Save leaves nothing of it (LeavesNothing): an added NPC light kept off is unticked, not deleted.
                if (edit.Npc && !edit.EverSaved) text += "; to keep it switched off, untick Enabled instead";
            }
            else
            {
                edit.Light->Enabled = false;
                ApplyLight(actor, *edit.Light, edit.Deleted);
                kind = StepKind::Light;
                text = std::format("switched {} off: a light of the YAML is switched off, never removed (Ctrl+Z switches it on)", Called(edit));
            }
            if (visible) UpdateComponents(actor);
            edit.Saved = false;
            PushStep({ IndexOf(edit), kind, before, StateOf(edit) });
            NoteChanged(IndexOf(edit));
            Select(nullptr);
            Say(std::move(text));
        }

        /// Hides the selection without collision (SetHidden, SetCollision), so undo can show it again. A light is switched off
        /// instead (DeleteLight). A prop's auto light goes with it in the same step while the link holds, unless another shown
        /// prop needs it (NeededBy).
        void Delete(AWorldInfo* world)
        {
            if (!SelectionAlive() || Fn.ForceUpdateComponents == nullptr) return;
            if (G.SelectedComp != nullptr) return DeleteLevelLight();
            auto const actor = G.Selected;
            if (auto const why = Refusal(actor, true); !why.empty())
            {
                Say(std::format("not deleted: {}", why));
                return;
            }
            if (IsLight(actor))
            {
                DeleteLight(actor);
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
            Step step{ IndexOf(edit), StepKind::Delete, before, StateOf(edit) };
            NoteChanged(step.Target);
            // Its auto light, while the link holds: deleted with it, unless another shown prop needs it.
            std::string kept;
            for (size_t i = 0; i < G.Edits.size(); i++)
            {
                auto& light = G.Edits[i];
                if (LinkOf(light) != step.Target || !Bound(light) || light.Deleted) continue;
                if (auto const other = NeededBy(light.Actor, actor); !other.empty())
                {
                    kept = std::format("; {} stays: {} needs it", Called(light), other);
                    break;
                }
                step.Also = i;
                step.AlsoBefore = StateOf(light);
                SetDeleted(light, true, Visible(light.Actor));
                if (Visible(light.Actor)) UpdateComponents(light.Actor);
                light.Saved = false;
                step.AlsoAfter = StateOf(light);
                NoteChanged(i);
                break;
            }
            auto const withLight = step.Also.has_value();
            PushStep(std::move(step));
            Select(nullptr);
            auto text = std::format("deleted {}{} (Ctrl+Z brings {} back)", Called(edit), withLight ? " and its light" : "", withLight ? "both" : "it");
            // A vanilla static actor (ours keep Phase 3's refusal): hidden live, its shadow stays baked in the floor's lightmap and
            // PhysX keeps the body it made at level start. A dev-only one has no build to wait for.
            if (actor->bStatic)
            {
                text += ShipsOf(actor, world).Ships ? ": its baked shadow stays; its physics body stays until the build"
                                                     : ": its baked shadow and its physics body stay";
            }
            Say(text + kept);
        }

        /// Writes an undo step's state back to an edit's actor and the edit, which is unsaved again: a light's settings too, and
        /// an NPC light's offset. A light deleted so is not one a drag deleted, unless UndoStep says so.
        void Restore(Edit& e, StepState const& state)
        {
            e.ShownFlags = state.ShownFlags;
            if (state.Light) e.Light = state.Light;
            if (state.Offset && e.Npc) e.Npc->Offset = *state.Offset;
            Put(e, state.Transform, !state.Shown);
            e.DragDeleted = false;
            e.Saved = false;
            NoteChanged(IndexOf(e));
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
            Restore(edit, state);
            // Its auto light, when the step still writes it (AlsoOf): a spawn's or a delete's while the link holds, a drag's always.
            if (auto const also = AlsoOf(step))
            {
                auto& light = G.Edits[*also];
                Restore(light, redo ? step.AlsoAfter : step.AlsoBefore);
                // Deleted in a drag's state, the drag's follow deleted it (only such a deleted light follows): the next drag of
                // its prop brings it back once the prop fits, as after that drag.
                if (step.Kind == StepKind::Move || step.Kind == StepKind::Turn || step.Kind == StepKind::Scale) light.DragDeleted = light.Deleted;
            }
            // The lights a move or a turn of an NPC carried (docs/design/in-game-editor.md §12), where the step found or left them.
            for (auto const& c : step.Carried) PutCarried(c, redo ? c.After : c.Before);
            // A level light is switched off, never hidden: it stays selectable.
            if (edit.Component) SelectLight(edit.Actor, edit.Comp);
            else Select(state.Shown ? edit.Actor : nullptr);
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
                        // build or an editor spawn. A fork by its vanilla file, which has the same mesh.
                        auto const level = Canonical(LevelOf(component->Owner), map);
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

        /// <base>_<n>, unused by any actor, edit or session entry (FNames ignore case, so the check does too): the tag is how a
        /// session entry finds its YAML entry again.
        std::string FreeTag(std::string const& base)
        {
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

        /// ed_<mesh>_<run>_<n> (FreeTag).
        std::string UniqueTag(std::wstring const& meshName)
        {
            std::string mesh;
            for (auto const c : meshName) mesh += c < 128 && (std::iswalnum(c) || c == L'_') ? static_cast<char>(c) : '_';
            if (mesh.size() > 32) mesh.resize(32);
            return FreeTag(std::format("ed_{}_{}", mesh, RunStamp()));
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

        /// A PointLightMovable with a tag, through CallSpawnWithoutNoDelete: Light's default is bNoDelete, which Spawn refuses
        /// (docs/design/in-game-editor.md §11). The editor's lights and the ones a pass spawns again both come from here. Null
        /// when the engine refuses.
        AActor* SpawnLight(APlayerController* pc, std::string const& tag, FVector location)
        {
            auto const cls = APointLightMovable::StaticClass();
            auto const defaults = cls != nullptr ? static_cast<AActor*>(cls->ClassDefaultObject) : nullptr;
            if (Fn.Spawn == nullptr || defaults == nullptr) return nullptr;
            AActor_execSpawn_Parms spawn;
            spawn.SpawnClass = cls;
            spawn.SpawnTag = SFXName(ToWide(tag).c_str(), 0);
            spawn.SpawnLocation = location;
            spawn.bNoCollisionFail = 1;
            CallSpawnWithoutNoDelete(pc, Fn.Spawn, &spawn, defaults);
            return spawn.ReturnValue;
        }

        /// A light for one of our NPCs (docs/design/in-game-editor.md §12): SpawnLight's PointLightMovable with the look of the build's default NPC
        /// light template (LE1CP's PointLightToggleable_7 through Engine.pcc's archetypes): channels {bInitialized, Dynamic}, no
        /// dynamic shadows, bForceDynamicLight, no level filter, white with alpha 0, falloff 2. The class defaults would cast
        /// shadows on all four base channels. Its settings then go through the kept writer, which keeps this look. Add light and
        /// a pass that spawns an npc entry again both come here. Null when the engine refuses.
        AActor* SpawnNpcLight(APlayerController* pc, std::string const& tag, FVector location)
        {
            auto const actor = SpawnLight(pc, tag, location);
            auto const component = PointOf(actor);
            if (component == nullptr) return actor;
            SetBits(component->LightingChannels, ChannelInitialized | ChannelDynamic);
            component->CastDynamicShadows = 0;
            component->bForceDynamicLight = 1;
            component->bOnlyAffectSameAndSpecifiedLevels = 0;
            // FColor is B, G, R, A in memory.
            component->LightColor = { 255, 255, 255, 0 };
            component->FalloffExponent = 2;
            return actor;
        }

        /// A light of the editor's with these settings, every field written (nothing depends on the class defaults), and its edit: a
        /// spawn the YAML has nothing of. Its edit's index, or none with why (the caller says it: a spawn's status line carries
        /// it). Adding an edit moves the others.
        std::optional<size_t> NewLight(APlayerController* pc, LightBlock const& light, FVector at, std::string& why)
        {
            if (Fn.Spawn == nullptr)
            {
                why = "Actor.Spawn was not found";
                return std::nullopt;
            }
            auto const tag = UniqueTag(L"light");
            auto const actor = SpawnLight(pc, tag, at);
            if (!IsLight(actor))
            {
                why = "the engine refused to spawn a PointLightMovable";
                return std::nullopt;
            }
            // Its edit first: our writer writes only a light of ours, an editor spawn among them (docs/design/in-game-editor.md §12).
            auto& edit = NewEdit(actor);
            edit.Spawned = true;
            edit.Light = light;
            edit.Base.reset();
            edit.BaseHidden.reset();
            edit.BaseLight.reset();
            ApplyLight(actor, light, false);
            UpdateComponents(actor);
            G.TrackedStale = true;
            G.ReswitchAll = true;
            return IndexOf(edit);
        }

        /// A prop light for a prop (Auto light, L): AutoPlacement's place and radius, the seed's brightness and colour, falloff 2.
        /// None, with why, when the prop has no bounds yet, or when it needs a radius over MaxAutoRadius from here (TooBig): then
        /// it keeps what lights it, its light environment unless a light someone placed covers it.
        std::optional<size_t> LightFor(APlayerController* pc, AActor* prop, std::string& why)
        {
            auto const lights = CoverLights();
            auto const lighting = LightingOf(prop, lights);
            if (!lighting)
            {
                why = std::format("{} has no bounds yet (its level is not visible)", NameFor(prop));
                return std::nullopt;
            }
            auto const& at = lighting->Auto;
            if (at.Radius > MaxAutoRadius)
            {
                auto const big = TooBig(NameFor(prop), at.Radius);
                why = lighting->Covered ? std::format("{}; {}", big, RoomLightHint) : std::format("{}. It keeps its light environment: {}", big, RoomLightHint);
                return std::nullopt;
            }
            auto const seed = SeedFor(lighting->Bounds.Centre, lights);
            LightBlock light;
            light.Brightness = seed.Brightness;
            light.Color = seed.Color;
            light.Radius = at.Radius;
            light.Falloff = 2;
            return NewLight(pc, light, at.Location, why);
        }

        /// A new light's undo step: undone, it is deleted (a spawn deleted before its first save leaves nothing).
        Step SpawnStep(Edit const& e) { return { IndexOf(e), StepKind::Spawn, { false, e.ShownFlags, e.Now, e.Light, NpcOffset(e) }, StateOf(e) }; }

        /// The Lights tab's Prop light and Room light: a light over where the view points, selected for the panel. A prop light
        /// takes the seed's brightness and colour.
        void AddLight(APlayerController* pc, UCanvas* canvas, bool room)
        {
            if (Fn.DeProject == nullptr || Fn.Trace == nullptr)
            {
                Say("no light added: Canvas.DeProject or Actor.Trace was not found");
                return;
            }
            auto const floor = SpawnPoint(pc, canvas);
            LightBlock light;
            if (room)
            {
                light.Kind = LightKind::Room;
                light.Radius = RoomLightRadius;
                light.Brightness = RoomLightBrightness;
            }
            else
            {
                auto const seed = SeedFor(floor, CoverLights());
                light.Radius = PropLightRadius;
                light.Brightness = seed.Brightness;
                light.Color = seed.Color;
            }
            std::string why;
            auto const index = NewLight(pc, light, Add(floor, { 0, 0, room ? RoomLightHeight : PropLightHeight }), why);
            if (!index)
            {
                Say("no light added: " + why);
                return;
            }
            auto const& edit = G.Edits[*index];
            PushStep(SpawnStep(edit));
            Select(edit.Actor);
            TickOnce();
            Say(std::format("added {} light {} (radius {:.0f}, brightness {:.2f}): the panel tunes it", room ? "a room" : "a prop", Called(edit), light.Radius,
                            light.Brightness));
        }

        /// L: a prop light for the selected prop, whatever it gets already, unless it is too big for one (LightFor). The new light is
        /// selected, for the panel.
        void PropLight(APlayerController* pc)
        {
            if (!SelectionAlive())
            {
                Say("L: select a prop first");
                return;
            }
            auto const prop = G.Selected;
            if (!SwitchedProp(prop))
            {
                Say(std::format("L: prop lights light the editor's spawns and the props of our YAMLs, and {} is neither", NameFor(prop)));
                return;
            }
            std::string why;
            auto const index = LightFor(pc, prop, why);
            if (!index)
            {
                Say("no light added: " + why);
                return;
            }
            auto const& edit = G.Edits[*index];
            PushStep(SpawnStep(edit));
            Select(edit.Actor);
            TickOnce();
            Say(std::format("added prop light {} for {} (radius {:.0f}, brightness {:.2f})", Called(edit), NameFor(prop), edit.Light->Radius, edit.Light->Brightness));
        }

        /// Add light (docs/design/in-game-editor.md §12): a light for one of our NPCs, the one an NPC section of the Lights tab names (tag), or the
        /// selected pawn (no tag), at offset [60, 0, 80] from its reference pose, radius 100, brightness 1.2, with the default
        /// template's look (SpawnNpcLight), tagged <npc tag>_light_<run>_<n> and selected for the panel. One step: undone, it is
        /// deleted. No tick: an NPC light reaches its NPC at once, paused (spike S5), and a tick could move NPCs.
        void AddNpcLight(APlayerController* pc, std::string const& tag)
        {
            if (Fn.Spawn == nullptr)
            {
                Say("not added: Actor.Spawn was not found");
                return;
            }
            std::string npcTag;
            if (tag.empty())
            {
                if (!SelectionAlive() || !G.Selected->IsA(APawn::StaticClass()))
                {
                    Say("not added: select one of our NPCs, or use Add light on its section of the Lights tab");
                    return;
                }
                auto const pawn = G.Selected;
                if (!G.Ours.Read)
                {
                    Say("not added: " + G.Ours.Problem);
                    return;
                }
                auto const npc = NpcOfPawn(pawn);
                if (npc == nullptr)
                {
                    // A pawn of our levels' files (the YAML's npcs: build them) that levels.json doesn't list was built before
                    // levels.json listed npcs; any other is the game's.
                    if (OurLevel(LevelOf(pawn), pawn->Outer) && FromFile(pawn))
                        Say(std::format("not added: levels.json has no NPC {} (built before Phase 5): run mod_build", NameText(pawn->Tag)));
                    else
                        Say(std::format("not added: only our NPCs (npcs: in a level YAML) take NPC lights; {} is the game's", ToUtf8(NameOf(pawn))));
                    return;
                }
                npcTag = npc->Tag;
            }
            else
            {
                auto const npc = NpcNamed(tag);
                if (npc == nullptr)
                {
                    Say(std::format("not added: levels.json has no NPC {} (built before Phase 5): run mod_build", tag));
                    return;
                }
                npcTag = npc->Tag;
            }
            if (auto const why = PoseRefusal(npcTag); !why.empty())
            {
                Say("not added: " + why);
                return;
            }
            auto const npc = NpcNamed(npcTag);
            if (npc == nullptr) return;
            auto const name = Title(npc->Name);
            FVector const offset{ NpcLightForward, 0, NpcLightUp };
            auto const lightTag = FreeTag(std::format("{}_light_{}", npcTag, RunStamp()));
            auto const actor = SpawnNpcLight(pc, lightTag, PlaceOf(ReferencePose(*npc), offset));
            if (!IsLight(actor))
            {
                Say("not added: the engine refused to spawn a PointLightMovable");
                return;
            }
            LightBlock light;
            light.Kind = LightKind::Npc;
            light.Radius = NpcLightRadius;
            light.Brightness = NpcLightBrightness;
            light.Falloff = 2;
            // A spawn the YAML has nothing of: no base until editor_import makes an npcs.<name>.lights item of it.
            auto& edit = NewEdit(actor);
            edit.Spawned = true;
            edit.Light = light;
            edit.Npc = NpcLink{ npcTag, offset };
            edit.Base.reset();
            edit.BaseHidden.reset();
            edit.BaseLight.reset();
            edit.BaseOffset.reset();
            ApplyLight(actor, light, false);
            UpdateComponents(actor);
            G.TrackedStale = true;
            PushStep(SpawnStep(edit));
            Select(edit.Actor);
            auto text = std::format("added light {} to {} at [{:g}, 0, {:g}] (radius {:.0f}, brightness {:g}): the panel tunes it", lightTag, name,
                                    NpcLightForward, NpcLightUp, NpcLightRadius, NpcLightBrightness);
            if (!std::exchange(G.TemplateNoted, true))
                text += ". It has the default NPC light template's look: a YAML with its own lightTemplate may build it with another";
            Say(text);
        }

        /// A click on a light's icon, or on its row in the Lights tab: the light the editor tracks at that path (ours, an NPC's, or
        /// a level's: its component, its collection the selected actor).
        void PickLight(std::string const& path)
        {
            RefreshTracked();
            for (auto const list : { &G.Lights, &G.OtherLights })
            {
                for (auto const& t : *list)
                {
                    if (!StillThere(t) || !SameText(t.Path, path)) continue;
                    Select(t.Actor);
                    G.LastPick = std::format("{} (light)", path);
                    return;
                }
            }
            for (auto const& t : G.LevelLights)
            {
                if (!StillThere(t) || !SameText(t.Path, path)) continue;
                SelectLight(t.Actor, t.Comp);
                G.LastPick = std::format("{} (level light, {})", path, t.Type);
                return;
            }
            G.LastPick = std::format("{}: not a light the editor lists any more", path);
        }

        /// A click on an effect's icon, or on its row in the Effects tab (docs/design/in-game-editor.md §12): the effect at that path, refused or not
        /// (the Selection window says why).
        void PickEffect(std::string const& path)
        {
            RefreshTracked();
            for (auto const& t : G.Effects)
            {
                if (!StillThere(t) || !SameText(t.Path, path)) continue;
                Select(t.Actor);
                G.LastPick = std::format("{} (effect)", path);
                return;
            }
            G.LastPick = std::format("{}: not an effect of the loaded levels any more", path);
        }

        /// A prop where the view points, facing the camera. The mesh is one in memory (the In this level list, or a catalog entry
        /// a level has), or a catalog entry's copy loaded from the catalog package; the session names the vanilla mesh and a
        /// vanilla file either way. With Auto light on, a spawn that no light covers, or that gets under 0.6 of its target from prop
        /// lights, gets a prop light of its own, in the spawn's step and linked to it (docs/design/in-game-editor.md §11), unless it is too big for one.
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
            auto const index = IndexOf(edit);
            G.TrackedStale = true;
            NoteChanged(index);
            // Only a fresh spawn: a re-applied one's light comes back from its own entry. A spawn no light covers gets one whatever its
            // share: it would keep its light environment, brighter than vanilla. A fresh Auto light always covers its prop, so every
            // spawn ends up switched, except one too big for one prop light: it keeps its light environment (its target is nothing,
            // so only an uncovered one asks), and the status line says why.
            std::optional<size_t> light;
            std::string unlit;
            if (G.AutoLight)
            {
                if (auto const lighting = LightingOf(actor, CoverLights());
                    lighting && (!lighting->Covered || ShareOf(lighting->Received, lighting->Target) < AutoLightShare))
                    light = LightFor(pc, actor, unlit);
            }
            // Undone, the prop is hidden; a spawn deleted before any save leaves nothing in the session. Its auto light goes with it.
            auto const& prop = G.Edits[index];
            Step step{ index, StepKind::Spawn, { false, prop.ShownFlags, prop.Now }, StateOf(prop) };
            std::string lit;
            if (light)
            {
                auto& made = G.Edits[*light];
                made.MadeFor = index;
                auto const madeStep = SpawnStep(made);
                step.Also = light;
                step.AlsoBefore = madeStep.Before;
                step.AlsoAfter = madeStep.After;
                lit = std::format(", with prop light {} (Auto light)", Called(made));
            }
            else if (!unlit.empty())
            {
                lit = "; Auto light added none: " + unlit;
            }
            PushStep(std::move(step));
            Select(actor);
            // Paused, the new actor has no light yet: it would render black.
            TickOnce();
            auto const loaded = loadMs >= 0 ? std::format(" (loaded from the catalog in {:.0f} ms)", loadMs) : std::string{};
            Say(std::format("spawned {} as {}{}{}", c.Text, tag, loaded, lit));
        }

        // ! Save
        // ========================================

        /// A session entry, version 2 or 3 (docs/design/in-game-editor.md §10, §12). imported is left out: a saved edit is a new state. What an install
        /// may still show goes into installed instead, and the base keeps whether the YAML removes the actor when an import said
        /// so. A light's entry (§11) has no mesh, and a light block beside its pose and in its base; its rotation and scales are
        /// the actor's. An NPC light's (§12) also has an npc block (the pawn's tag and the offset), in its base too. A level
        /// light's (5b) has no tag, its component's path and class, its collection as owner, a zero rotation and scale 1.
        json EntryOf(Edit const& e)
        {
            auto const orNull = [](std::string const& text) { return text.empty() ? json(nullptr) : json(text); };
            json mesh = nullptr;
            if (e.Spawned && !e.Light) mesh = { { "path", e.MeshPath }, { "file", orNull(e.MeshFile) }, { "load", orNull(e.MeshLoad) } };
            json base = nullptr;
            if (e.Base)
            {
                base = PoseJson(*e.Base);
                if (e.BaseHidden) base["deleted"] = *e.BaseHidden;
                if (e.Light && e.BaseLight) base["light"] = LightJson(*e.BaseLight);
                if (e.Npc && e.BaseOffset) base["npc"] = NpcJson({ e.Npc->Tag, *e.BaseOffset });
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
            if (e.Light) entry["light"] = LightJson(*e.Light);
            if (e.Npc) entry["npc"] = NpcJson(*e.Npc);
            // A level light's (5b): its collection's name, and its type in its light blocks, for editor_import to say what ships.
            if (e.Component)
            {
                entry["owner"] = e.Owner;
                auto const type = std::string(1, e.Type != 0 ? e.Type : 'C');
                if (entry.contains("light")) entry["light"]["type"] = type;
                if (base.is_object() && base.contains("light")) entry["base"]["light"]["type"] = type;
            }
            if (!e.Installed.empty()) entry["installed"] = e.Installed;
            return entry;
        }

        /// A spawn deleted before it was ever saved: Save writes nothing of it (no YAML can have it).
        bool LeavesNothing(Edit const& e) { return e.Spawned && e.Deleted && !e.EverSaved; }

        bool Same(FVector a, FVector b) { return a.X == b.X && a.Y == b.Y && a.Z == b.Z; }
        bool Same(FRotator a, FRotator b) { return a.Pitch == b.Pitch && a.Yaw == b.Yaw && a.Roll == b.Roll; }

        /// Before Save writes an edit over the file's entry for it: each field the editor didn't change since the edit was last
        /// saved or read takes the entry's value, which an import may have moved on (a YAML value the merge kept, onConflict=yaml
        /// included): the pose's, each of a light block's, and deleted (docs/design/in-game-editor.md §11). The actor follows when the edit has one (a
        /// light's settings, then one reattach). Returns whether anything changed.
        bool TakeFileValues(Edit& e, json const& entry)
        {
            if (!e.SavedState) return false;
            auto const& saved = *e.SavedState;
            auto const file = PoseFrom(entry, e.Now);
            auto now = e.Now;
            if (Same(now.Location, saved.Transform.Location)) now.Location = file.Location;
            if (Same(now.Rotation, saved.Transform.Rotation)) now.Rotation = file.Rotation;
            if (now.DrawScale == saved.Transform.DrawScale) now.DrawScale = file.DrawScale;
            if (Same(now.DrawScale3D, saved.Transform.DrawScale3D)) now.DrawScale3D = file.DrawScale3D;
            auto light = e.Light;
            if (light && saved.Light && HasLightBlock(entry))
            {
                auto const& was = *saved.Light;
                auto const there = LightFrom(entry, *light);
                if (light->Kind == was.Kind) light->Kind = there.Kind;
                if (light->Radius == was.Radius) light->Radius = there.Radius;
                if (light->Brightness == was.Brightness) light->Brightness = there.Brightness;
                if (light->Color == was.Color) light->Color = there.Color;
                if (light->Falloff == was.Falloff) light->Falloff = there.Falloff;
                if (light->Enabled == was.Enabled) light->Enabled = there.Enabled;
            }
            // An NPC light's offset (§12), as a pose field. Its location follows the offset and its NPC's reference pose (the
            // pawn's edit may have taken the file's pose just before), not the file's location. A place within noise of where it
            // stands is no change: a dragged light's offset is measured from where it stands.
            auto npc = e.Npc;
            if (npc && saved.Offset && Same(npc->Offset, *saved.Offset)) npc = NpcFrom(entry, npc);
            if (npc && e.Npc)
            {
                now.Location = e.Now.Location;
                if (auto const built = NpcNamed(npc->Tag))
                {
                    if (auto const place = PlaceOf(ReferencePose(*built), npc->Offset); !Near(place, now.Location, LocationNoise)) now.Location = place;
                }
            }
            auto deleted = e.Deleted;
            if (auto const flag = entry.find("deleted"); deleted == saved.Deleted && flag != entry.end() && flag->is_boolean()) deleted = flag->get<bool>();
            auto const sameNpc = npc.has_value() == e.Npc.has_value() && (!npc || (npc->Tag == e.Npc->Tag && Same(npc->Offset, e.Npc->Offset)));
            if (Same(now.Location, e.Now.Location) && Same(now.Rotation, e.Now.Rotation) && now.DrawScale == e.Now.DrawScale
                && Same(now.DrawScale3D, e.Now.DrawScale3D) && light == e.Light && sameNpc && deleted == e.Deleted)
                return false;
            LEASI_INFO("editor: {} takes the session file's values for what it didn't change since its last save (an import moved them on)", Called(e));
            e.Light = light;
            e.Npc = npc;
            if (Bound(e))
            {
                Put(e, now, deleted);
                NoteChanged(IndexOf(e));
            }
            else
            {
                e.Now = now;
                e.Deleted = deleted;
            }
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
        /// version this bridge doesn't know is left alone. The file is written as version 3 when an entry holds an npc or vanilla
        /// light, else 2 (VersionFor, docs/design/in-game-editor.md §12).
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
                    if (KnownVersion(version))
                    {
                        for (auto const& a : doc.at("actors"))
                        {
                            auto const key = KeyOf(a, map);
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
            if (!KnownVersion(version))
                throw BridgeError(std::format("{} is version {}, and this bridge reads versions 1 to 3: left as it is", ToUtf8(file.wstring()), version));

            // The file's entries as read: an import may have moved an entry's values and base on since the edit last saw it.
            auto const read = actors;
            SessionWrite result{ file, bad, {}, 0 };
            auto const& edits = bad.empty() ? unsaved : all;
            // First each edit takes the file's values it didn't change, pawns before lights: an NPC light's place is its NPC's
            // reference pose plus its offset, and the pose is the pawn's edit's (§12). A pawn of ours that took a location or a yaw
            // then takes its lights along, as a pass does: an edit of one is unsaved then (written here when it is in the list,
            // else at the next Save), one without an edit moves raw. Only a pawn with its actor: a map change saves when the old
            // world's levels may be gone, and the pass that binds a pawn again places its lights.
            auto ordered = edits;
            std::stable_partition(ordered.begin(), ordered.end(), [](Edit const* e) { return !e->Light; });
            std::set<std::string> carriers;
            for (auto const e : ordered)
            {
                if (LeavesNothing(*e)) continue;
                auto const it = read.find(KeyOf(*e));
                if (it == read.end()) continue;
                auto const was = e->Now;
                if (!TakeFileValues(*e, it->second)) continue;
                result.TookFileValues++;
                if (e->Light || !Bound(*e) || (Same(e->Now.Location, was.Location) && e->Now.Rotation.Yaw == was.Rotation.Yaw)) continue;
                if (auto const npc = NpcOfPawn(e->Actor)) carriers.insert(npc->Tag);
            }
            if (!carriers.empty())
            {
                G.TrackedStale = true;
                for (auto const& tag : carriers) PlaceLights(tag);
            }
            for (auto const e : edits)
            {
                auto const key = KeyOf(*e);
                // A spawned actor deleted before any save leaves nothing. Once saved, it may have been imported (and its entry
                // marked so): it is written as deleted: true, for editor_import to mark its props entry removed.
                if (LeavesNothing(*e)) continue;
                if (auto const it = read.find(key); it != read.end())
                {
                    auto const& entry = it->second;
                    if (auto const base = entry.find("base"); base != entry.end())
                    {
                        if (base->is_object())
                        {
                            e->Base = PoseFrom(*base, e->Base.value_or(e->Now));
                            // Whether the YAML removes it: the base says so (editor_import writes it). An entry an import marked
                            // without it holds what it wrote into the YAML: deleted is removed: true there.
                            if (base->contains("deleted")) e->BaseHidden = Flag(*base, "deleted");
                            else if (entry.contains("imported")) e->BaseHidden = Flag(entry, "deleted");
                            // A light's: what the YAML's lights: entry holds; an NPC light's offset, its npcs.<name>.lights item's.
                            if (e->Light && HasLightBlock(*base)) e->BaseLight = LightFrom(*base, e->BaseLight.value_or(*e->Light));
                            if (e->Npc)
                            {
                                if (auto const npc = NpcFrom(*base, std::nullopt)) e->BaseOffset = npc->Offset;
                            }
                        }
                        else
                        {
                            e->Base.reset();
                            e->BaseHidden.reset();
                            e->BaseLight.reset();
                            e->BaseOffset.reset();
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
            WriteFile(file, json{ { "version", VersionFor(list) }, { "map", map }, { "saved", std::format("{:%FT%TZ}", now) }, { "actors", list } });
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
                        e->SavedState = EntryState{ e->Now, e->Light, e->Deleted, NpcOffset(*e) };
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

        /// Where the last access violation in editor code happened: the faulting instruction, the frames under it (unwound from
        /// the fault's own context) and the address it touched. Plain data: the filter runs before the stack unwinds.
        struct Fault
        {
            void* Frames[24];
            int Count;
            ULONG_PTR Write, Target;
        };
        Fault LastFault{};

        int FaultFilter(EXCEPTION_POINTERS* info)
        {
            if (info->ExceptionRecord->ExceptionCode != EXCEPTION_ACCESS_VIOLATION) return EXCEPTION_CONTINUE_SEARCH;
            auto const& record = *info->ExceptionRecord;
            LastFault.Write = record.NumberParameters > 0 ? record.ExceptionInformation[0] : 0;
            LastFault.Target = record.NumberParameters > 1 ? record.ExceptionInformation[1] : 0;
            LastFault.Count = 0;
            CONTEXT context = *info->ContextRecord;
            while (LastFault.Count < static_cast<int>(std::size(LastFault.Frames)) && context.Rip != 0)
            {
                LastFault.Frames[LastFault.Count++] = reinterpret_cast<void*>(context.Rip);
                DWORD64 imageBase = 0;
                auto const function = RtlLookupFunctionEntry(context.Rip, &imageBase, nullptr);
                if (function == nullptr) break;
                void* handlerData = nullptr;
                DWORD64 establisher = 0;
                RtlVirtualUnwind(UNW_FLAG_NHANDLER, imageBase, context.Rip, function, &context, &handlerData, &establisher, nullptr);
            }
            return EXCEPTION_EXECUTE_HANDLER;
        }

        /// The last fault for the log: "read of 0x10 at MeleBridge.asi+0x1a2b3 < MeleBridge.asi+0x4c5d6 < ...". Offsets into
        /// MeleBridge.asi resolve against the staged PDB with llvm-symbolizer --relative-address (README, Diagnostics).
        std::string FaultText()
        {
            auto text = std::format("{} of 0x{:x} at ", LastFault.Write == 1 ? "write" : LastFault.Write == 8 ? "execute" : "read", LastFault.Target);
            for (int i = 0; i < LastFault.Count; ++i)
            {
                HMODULE module = nullptr;
                wchar_t file[MAX_PATH] = {};
                if (GetModuleHandleExW(GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS | GET_MODULE_HANDLE_EX_FLAG_UNCHANGED_REFCOUNT,
                                       static_cast<LPCWSTR>(LastFault.Frames[i]), &module) && GetModuleFileNameW(module, file, MAX_PATH) > 0)
                    text += std::format("{}{}+0x{:x}", i > 0 ? " < " : "", ToUtf8(std::filesystem::path(file).filename().wstring()),
                                        reinterpret_cast<uintptr_t>(LastFault.Frames[i]) - reinterpret_cast<uintptr_t>(module));
                else text += std::format("{}0x{:x}", i > 0 ? " < " : "", reinterpret_cast<uintptr_t>(LastFault.Frames[i]));
            }
            return text;
        }

        /// An access violation in editor code fails that code, not the game. No C++ object may live in this frame.
        bool Guarded(void (*work)(UObject*), UObject* context)
        {
            __try
            {
                work(context);
                return true;
            }
            __except (FaultFilter(GetExceptionInformation()))
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
                if (!KnownVersion(version)) throw BridgeError(std::format("version {}, and this bridge reads versions 1 to 3", version));
                for (auto const& a : doc.at("actors"))
                {
                    auto key = KeyOf(a, map);
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
            G.ReswitchFaulted = false;
            G.SummaryStale = true;
            G.TrackedStale = true;
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
            // and flags. Its Kismet is read with the others' when next needed (docs/design/in-game-editor.md §12).
            for (auto const& level : G.Levels)
            {
                if (!level.Visible) continue;
                auto const was = std::find_if(before.begin(), before.end(), [&](LevelSeen const& l) { return l.Level == level.Level && l.Index == level.Index; });
                if (was != before.end() && was->Visible) continue;
                G.KismetStale = true;
                if (was == before.end()) continue;
                for (auto const& e : G.Edits)
                {
                    if (!Bound(e) || e.LevelObject != level.Level || e.LevelIndex != level.Index) continue;
                    // A level light alone: a reattach of its collection would redo all of its lights (docs/design/in-game-editor.md §12, 5b).
                    if (e.Component) RefreshLight(e.Comp);
                    else UpdateComponents(e.Actor);
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
            /// The point and spot lights of the loaded collections by lower-case path (5b), filled on first use; those an edit has.
            std::unordered_map<std::string, LevelLight> LightsByPath;
            bool LightPathsRead = false;
            std::unordered_set<UPointLightComponent*> TakenLights;

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

            LevelLight LightAt(std::string const& path)
            {
                if (!LightPathsRead)
                {
                    LightPathsRead = true;
                    for (auto const actor : All)
                    {
                        if (!IsCollection(actor)) continue;
                        LightsIn(actor, [&](UPointLightComponent* light) { LightsByPath.emplace(Lower(ToUtf8(PathOf(light))), LevelLight{ actor, light }); });
                    }
                }
                auto const it = LightsByPath.find(Lower(path));
                return it != LightsByPath.end() ? it->second : LevelLight{};
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
                if (!Bound(e)) continue;
                // A level light's actor is its collection, which other lights of it share.
                if (e.Component) loaded.TakenLights.insert(e.Comp);
                else loaded.Taken.insert(e.Actor);
            }
            return loaded;
        }

        /// The loaded actor an entry or a detached edit names (docs/design/in-game-editor.md §10). A spawn: the one actor with its tag (a built copy). A
        /// level actor: by tag when exactly one loaded actor has it, it is a real tag, and that actor is in the entry's level and
        /// of its class (the same tag in another level is another actor while the entry's level isn't loaded); else by path with
        /// the same class. The entry's level is its vanilla file, or the running map's fork of it (§11), found by the rule: the
        /// path, then the same path in the fork. Null, with why, when there is none or another edit has it.
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
            auto const map = ToUtf8(MapName());
            auto const level = path.substr(0, path.find('.'));
            auto const fork = ForkName(map, level);
            auto const inLevel = [&](AActor* candidate) {
                auto const in = LevelOf(candidate);
                return SameText(in, level) || SameText(in, fork);
            };
            if (RealTag(tag, className) && tagged.size() == 1 && inLevel(tagged[0]) && SameText(ToUtf8(NameOf(tagged[0]->Class)), className))
            {
                if (!loaded.Taken.contains(tagged[0])) return tagged[0];
                why = "the actor with its tag has another edit";
                return nullptr;
            }
            auto actor = loaded.AtPath(path);
            if (actor == nullptr) actor = loaded.AtPath(ForkPath(path, map));
            auto const actorClass = actor != nullptr ? ToUtf8(NameOf(actor->Class)) : std::string{};
            if (actor != nullptr && SameText(actorClass, className))
            {
                if (!loaded.Taken.contains(actor)) return actor;
                why = "the actor at its path has another edit";
                return nullptr;
            }
            if (!loaded.Levels.contains(Lower(level)) && !loaded.Levels.contains(Lower(fork))) why = std::format("{} is not loaded", level);
            else if (actor != nullptr) why = std::format("the actor at its path is a {}, not a {}", actorClass, className);
            else why = RealTag(tag, className) ? "no loaded actor at its path, and not exactly one with its tag in its level" : "no loaded actor at its path";
            return nullptr;
        }

        /// The loaded level light an entry or a detached edit names (5b): by path with the same class, then the same path in the
        /// running map's fork of its level. None, with why, when there is none or another edit has it.
        LevelLight MatchLight(LoadedActors& loaded, std::string const& path, std::string const& className, std::string& why)
        {
            auto const map = ToUtf8(MapName());
            auto const level = path.substr(0, path.find('.'));
            auto const fork = ForkName(map, level);
            auto found = loaded.LightAt(path);
            if (found.Light == nullptr) found = loaded.LightAt(ForkPath(path, map));
            auto const lightClass = found.Light != nullptr ? ToUtf8(NameOf(found.Light->Class)) : std::string{};
            if (found.Light != nullptr && SameText(lightClass, className))
            {
                if (!loaded.TakenLights.contains(found.Light)) return found;
                why = "the light at its path has another edit";
                return {};
            }
            if (!loaded.Levels.contains(Lower(level)) && !loaded.Levels.contains(Lower(fork))) why = std::format("{} is not loaded", level);
            else if (found.Light != nullptr) why = std::format("the light at its path is a {}, not a {}", lightClass, className);
            else why = "no loaded light at its path";
            return {};
        }

        /// A level light's entry (5b): it names its collection.
        bool LevelLightEntry(json const& entry) { return !Text(entry, "owner").empty(); }

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
            // Canonical: an entry names an actor in a fork by its vanilla path.
            auto const map = ToUtf8(MapName());
            auto const path = Canonical(ToUtf8(PathOf(actor)), map);
            // Only one that has its tag or its path can match it: the loaded actors are read only then.
            std::vector<Named> named;
            auto const add = [&](bool spawned, std::string const& t, std::string const& p, std::string const& c) {
                if ((!t.empty() && SameText(t, tag)) || SameText(Canonical(p, map), path)) named.push_back({ spawned, t, p, c });
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

        /// Awaiting for a level light (5b): a session entry or a waiting edit with its canonical path, which only it can match.
        std::string AwaitingLight(UPointLightComponent* light)
        {
            auto const map = ToUtf8(MapName());
            auto const path = Canonical(ToUtf8(PathOf(light)), map);
            auto const names = [&](std::string const& p) { return SameText(Canonical(p, map), path); };
            auto const waits = std::any_of(G.Entries.begin(), G.Entries.end(), [&](Entry const& entry) {
                return !entry.Done && Valid(entry.Data) && LevelLightEntry(entry.Data) && names(Text(entry.Data, "path"));
            }) || std::any_of(G.Edits.begin(), G.Edits.end(), [&](Edit const& e) { return e.Map == G.SessionMap && e.Pending && e.Component && names(e.Path); });
            if (!waits) return {};
            auto const name = ToUtf8(NameOf(light));
            return G.PassFaulted ? std::format("{}'s session entry is not applied yet, and the last re-apply pass failed (tried again when the loaded levels change)", name)
                                 : std::format("{}'s session entry is still being applied, try again in a moment", name);
        }

        enum class Verdict { Apply, Drop };

        /// Rules 1 and 2 of docs/design/in-game-editor.md §10, for an entry and the actor it matched (null: a spawn no loaded actor has the tag of).
        /// builtHidden: the actor is hidden, and the YAML's state it was judged by says the YAML removes it (the build hid it, not
        /// the game): an entry that shows it shows it again. A light is compared by HasLight, and is never built hidden: every
        /// light is bHidden by class default (§11).
        template <typename Has, typename Hid>
        Verdict JudgeBy(json const& entry, bool target, Has const& has, Hid const& hid, std::string& why, bool& builtHidden)
        {
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
                if (!target) return Verdict::Apply;
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

        Verdict Judge(json const& entry, AActor* actor, std::string& why, bool& builtHidden)
        {
            auto const light = IsLight(actor);
            auto const has = [&](json const& state) {
                if (actor == nullptr) return false;
                return light ? HasLight(actor, state, Flag(state, "deleted")) : Has(actor, PoseFrom(state, PoseOf(actor)), Flag(state, "deleted"));
            };
            auto const hid = [&](json const& state) { return actor != nullptr && !light && actor->bHidden && state.is_object() && Flag(state, "deleted"); };
            return JudgeBy(entry, actor != nullptr, has, hid, why, builtHidden);
        }

        /// Judge for a level light's entry (5b): compared by HasLevelLight; never built hidden (it is switched off, never hidden).
        Verdict JudgeLight(json const& entry, UPointLightComponent const* light, std::string& why)
        {
            auto builtHidden = false;
            auto const has = [&](json const& state) { return HasLevelLight(light, state, Flag(state, "deleted")); };
            return JudgeBy(entry, true, has, [](json const&) { return false; }, why, builtHidden);
        }

        /// An edit from a session entry, as the file has it: saved, and saved once (editor_import may have imported it). It keeps
        /// the entry until a pass judges it against an actor (a spawn spawned again meets its built copy later).
        Edit EditFrom(json const& entry)
        {
            Edit e;
            e.Map = G.SessionMap;
            e.Tag = Text(entry, "tag");
            // Canonical, as NewEdit makes them: an entry that named the fork is saved under the vanilla path.
            e.Path = Canonical(Text(entry, "path"), e.Map);
            e.Level = Canonical(Text(entry, "level"), e.Map);
            if (e.Level.empty()) e.Level = e.Path.substr(0, e.Path.find('.'));
            e.ClassName = Text(entry, "class");
            e.Spawned = Flag(entry, "spawned");
            if (auto const mesh = entry.find("mesh"); mesh != entry.end() && mesh->is_object())
            {
                e.MeshPath = Text(*mesh, "path");
                e.MeshFile = Text(*mesh, "file");
                e.MeshLoad = Text(*mesh, "load");
            }
            // A light's settings (docs/design/in-game-editor.md §11); a field the block lacks is a fresh light's. An NPC light's NPC and offset (§12).
            if (HasLightBlock(entry)) e.Light = LightFrom(entry, LightBlock{});
            if (e.Light) e.Npc = NpcFrom(entry, std::nullopt);
            // A level light's collection and type (5b); BindLight reads the type again from the light.
            if (LevelLightEntry(entry))
            {
                e.Component = true;
                e.Owner = Text(entry, "owner");
                e.Npc.reset();
                if (e.Light)
                {
                    e.Light->Kind = LightKind::Vanilla;
                    if (auto const type = Text(entry.at("light"), "type"); type.size() == 1) e.Type = type[0];
                }
            }
            if (auto const base = entry.find("base"); base != entry.end() && base->is_object())
            {
                // As Save reads it: the base's deleted, else what an import's entry wrote into the YAML.
                if (base->contains("deleted")) e.BaseHidden = Flag(*base, "deleted");
                else if (entry.contains("imported")) e.BaseHidden = Flag(entry, "deleted");
                if (e.Light && HasLightBlock(*base))
                {
                    e.BaseLight = LightFrom(*base, *e.Light);
                    if (e.Component) e.BaseLight->Kind = LightKind::Vanilla;
                }
                if (e.Npc)
                {
                    if (auto const npc = NpcFrom(*base, std::nullopt)) e.BaseOffset = npc->Offset;
                }
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

        /// An edit's own spawn, replaced by the built copy: destroyed, or hidden if the engine refuses (a light switched off: it is
        /// never hidden; through the writer its kind takes, docs/design/in-game-editor.md §12).
        void DestroySpawn(Edit const& e)
        {
            auto const actor = e.Actor;
            if (Fn.Destroy != nullptr)
            {
                AActor_execDestroy_Parms destroy;
                CallProcessEvent(actor, Fn.Destroy, &destroy);
                if (destroy.ReturnValue) return;
            }
            if (IsLight(actor)) ApplyLight(actor, LightOf(actor, e.Light ? SourceOf(e.Light->Kind) : SourceOf(actor)), true);
            else WriteFlags(actor, { true, false, false }, Visible(actor));
            UpdateComponents(actor);
        }

        /// Spawns again the spawns no loaded actor stands for, where the session put them: the mesh in memory (one walk for all of
        /// them), else the catalog package's copy by mesh.load, else by the catalog entry with that vanilla mesh. Returns how many.
        int Respawn(APlayerController* pc, std::vector<size_t> const& which)
        {
            // Lights first: a light has no mesh, and comes back through the same helper as the editor's (docs/design/in-game-editor.md §11), an NPC's
            // with the NPC light look (§12), not the class defaults.
            int count = 0;
            std::vector<size_t> props;
            for (auto const i : which)
            {
                auto& e = G.Edits[i];
                if (!e.Light)
                {
                    props.push_back(i);
                    continue;
                }
                auto const actor = e.Light->Kind == LightKind::Npc ? SpawnNpcLight(pc, e.Tag, e.Now.Location) : SpawnLight(pc, e.Tag, e.Now.Location);
                if (!IsLight(actor))
                {
                    e.PendingWhy = Fn.Spawn == nullptr ? "spawning is off: Actor.Spawn was not found" : "the engine refused to spawn it again";
                    continue;
                }
                auto const pose = e.Now;
                Bind(e, actor);
                e.Rebindable = true;
                Put(e, pose, false);
                count++;
                LEASI_INFO("editor: spawned {} again as {}", e.Tag, ToUtf8(PathOf(actor)));
            }
            if (props.empty()) return count;
            if (Fn.Spawn == nullptr || Fn.SetStaticMesh == nullptr)
            {
                for (auto const i : props) G.Edits[i].PendingWhy = "spawning is off: Actor.Spawn or DynamicSMActor.SetStaticMesh was not found";
                return count;
            }
            std::unordered_set<std::string> wanted;
            for (auto const i : props)
            {
                if (!G.Edits[i].MeshPath.empty()) wanted.insert(Lower(G.Edits[i].MeshPath));
                if (!G.Edits[i].MeshLoad.empty()) wanted.insert(Lower(G.Edits[i].MeshLoad));
            }
            auto const inMemory = MeshesAt(wanted);
            auto const memory = [&](std::string const& path) -> UStaticMesh* {
                auto const it = inMemory.find(Lower(path));
                return it != inMemory.end() ? it->second : nullptr;
            };

            for (auto const i : props)
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
        /// hidden, the game may have hidden it: whether the YAML removes it stays unknown. A light: its settings as built, on as
        /// its bEnabled; the YAML never removes one.
        void TakeBase(Edit& e, AActor* actor)
        {
            e.Base = PoseOf(actor);
            if (IsLight(actor))
            {
                e.BaseLight = LightOf(actor, e.Light ? SourceOf(e.Light->Kind) : SourceOf(actor));
                e.BaseHidden = false;
            }
            else if (!actor->bHidden) e.BaseHidden = false;
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
                DestroySpawn(e);
            }
            Bind(e, actor);
            loaded.Taken.insert(actor);
            e.Rebindable = false;
            if (e.Spawned) e.OnCopy = true;
            if (verdict == Verdict::Drop)
            {
                LEASI_INFO("editor: session entry {} dropped on {}: {}", KeyOf(e), ToUtf8(PathOf(actor)), why);
                // What the YAML built; a built copy hidden is one the YAML removes. A light: its settings as built, switched off
                // being light.enabled, never deleted, read with its source (§12); an NPC light's offset, levels.json's.
                auto const light = IsLight(actor);
                if (light) e.Light = e.BaseLight = LightOf(actor, e.Light ? SourceOf(e.Light->Kind) : SourceOf(actor));
                else if (actor->bHidden) TakeBuiltHidden(e);
                if (auto const [npc, built] = light ? NpcLightOf(actor) : std::pair<BuiltNpc const*, BuiltNpcLight const*>{}; npc != nullptr)
                {
                    e.Npc = NpcLink{ npc->Tag, built->Offset };
                    e.BaseOffset = built->Offset;
                }
                e.Now = PoseOf(actor);
                e.Base = e.Now;
                e.BaseHidden = !light && actor->bHidden != 0;
                e.Installed = json::array();
                e.SavedState.reset();
                dropped.push_back(source);
                return false;
            }
            // Before the write: the copy as built.
            if (!e.Base && source.is_object() && !source.contains("base")) TakeBase(e, actor);
            if (builtHidden) TakeBuiltHidden(e);
            // An NPC light stands at its NPC's reference pose plus its offset (docs/design/in-game-editor.md §12): the pawn's edit may have moved since.
            auto place = pose;
            if (NpcPlace(e, place.Location)) e.Saved = false;
            Put(e, place, deleted);
            return true;
        }

        /// TakeBase for a level light (5b): where it stands and its settings, as its level file has it.
        void TakeLightBase(Edit& e, UPointLightComponent* light)
        {
            e.Base = LightPose(light);
            e.BaseLight = LevelLightOf(light);
            e.BaseHidden = false;
        }

        /// TakeOver for a level light whose level came back (5b): judged first while it is still its session entry; dropped, the
        /// edit takes the light as its level built it, without writing it; else the edit's state is written. Returns whether it
        /// was written.
        bool TakeOverLight(Edit& e, LevelLight found, LoadedActors& loaded, std::vector<json>& dropped)
        {
            auto verdict = Verdict::Apply;
            std::string why;
            if (e.Saved && !e.Source.is_null()) verdict = JudgeLight(e.Source, found.Light, why);
            auto const source = std::exchange(e.Source, nullptr);
            auto const pose = e.Now;
            BindLight(e, found.Owner, found.Light);
            loaded.TakenLights.insert(found.Light);
            if (verdict == Verdict::Drop)
            {
                LEASI_INFO("editor: session entry {} dropped on {}: {}", KeyOf(e), ToUtf8(PathOf(found.Light)), why);
                e.Light = LevelLightOf(found.Light);
                TakeLightBase(e, found.Light);
                e.Now = *e.Base;
                e.Installed = json::array();
                e.SavedState.reset();
                dropped.push_back(source);
                return false;
            }
            if (!e.Base) TakeLightBase(e, found.Light);
            Put(e, pose, false);
            return true;
        }

        /// Removes the entries a pass dropped from the map's session file, each only if the file still has it as it was read (a
        /// save or an import since leaves it alone). Same temp-file write as Save. A version 2 or 3 file's version follows what
        /// is left (VersionFor, docs/design/in-game-editor.md §12); a version 1 file stays 1.
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
                if (!KnownVersion(version)) return;
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
                if (version != 1) doc["version"] = VersionFor(left);
                doc["actors"] = std::move(left);
                WriteFile(file, doc);
                LEASI_INFO("editor: {} entr{} dropped from {}", removed, removed == 1 ? "y" : "ies", ToUtf8(file.wstring()));
            }
            catch (std::exception const& e)
            {
                LEASI_WARN("editor: dropped entries not removed from {}'s session: {}", map, e.what());
            }
        }

        /// A level light's session entry in a pass (5b): matched by path, judged, then dropped, or applied as an edit bound to the
        /// light (its fields the entry's, any it lacks the light's own); waits while its level is not loaded. Returns whether it
        /// was applied.
        bool ApplyLightEntry(Entry& entry, LoadedActors& loaded, std::vector<json>& dropped, std::string& why)
        {
            auto const& a = entry.Data;
            auto const found = MatchLight(loaded, Text(a, "path"), Text(a, "class"), why);
            if (found.Light == nullptr)
            {
                entry.Why = why;
                return false;
            }
            auto const verdict = JudgeLight(a, found.Light, why);
            entry.Done = true;
            if (verdict == Verdict::Drop)
            {
                LEASI_INFO("editor: session entry {} dropped: {}", entry.Key, why);
                dropped.push_back(a);
                return false;
            }
            auto& e = G.Edits.emplace_back(EditFrom(a));
            auto const pose = PoseFrom(a, LightPose(found.Light));
            if (e.Light)
            {
                e.Light = LightFrom(a, LevelLightOf(found.Light));
                e.Light->Kind = LightKind::Vanilla;
            }
            e.SavedState = EntryState{ pose, e.Light, false, std::nullopt };
            if (auto const base = a.find("base"); base != a.end() && base->is_object()) e.Base = PoseFrom(*base, LightPose(found.Light));
            else TakeLightBase(e, found.Light);
            e.Source = nullptr;
            BindLight(e, found.Owner, found.Light);
            loaded.TakenLights.insert(found.Light);
            Put(e, pose, false);
            NoteChanged(IndexOf(e));
            LEASI_INFO("editor: re-applied {} to {}", entry.Key, ToUtf8(PathOf(found.Light)));
            return true;
        }

        /// One re-apply pass (docs/design/in-game-editor.md §10): built copies take over spawned-again spawns, detached edits find their actor again,
        /// and the session's entries are matched, judged and applied, dropped, or left waiting. An NPC light's entry or edit waits
        /// while its NPC fails the pose check, and is placed at its NPC's reference pose plus its offset; a pawn of ours the pass
        /// wrote takes its lights along (§12).
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
            // The tags of our NPCs whose pawn the pass wrote: their lights follow at the end.
            std::set<std::string> carriers;
            auto const carrier = [&](AActor* actor) {
                if (auto const npc = NpcOfPawn(actor)) carriers.insert(npc->Tag);
            };

            // A built copy with a spawn's tag appeared: the edit moves to it, and our own spawn goes. An NPC light's stays on
            // our spawn while its NPC fails the pose check.
            for (size_t i = 0; i < G.Edits.size(); i++)
            {
                auto& e = G.Edits[i];
                if (e.Map != G.SessionMap || !e.Spawned || !e.Rebindable) continue;
                std::string why;
                auto const copy = Match(loaded, true, e.Tag, e.Path, e.ClassName, why);
                if (copy == nullptr || (e.Npc && !PoseRefusal(e.Npc->Tag).empty())) continue;
                if (TakeOver(e, copy, loaded, dropped)) rebound++;
                LEASI_INFO("editor: {} is on its built copy {} now", e.Tag, ToUtf8(PathOf(copy)));
            }

            // Edits whose level came back, and spawns not spawned again yet. An NPC light's waits while its NPC fails the pose
            // check, and is spawned again where its NPC's reference pose puts it.
            for (size_t i = 0; i < G.Edits.size(); i++)
            {
                auto& e = G.Edits[i];
                if (e.Map != G.SessionMap || !e.Pending) continue;
                std::string why;
                if (e.Component)
                {
                    auto const found = MatchLight(loaded, e.Path, e.ClassName, why);
                    if (found.Light == nullptr) e.PendingWhy = why;
                    else if (TakeOverLight(e, found, loaded, dropped))
                    {
                        applied++;
                        NoteChanged(i);
                    }
                    continue;
                }
                auto const actor = Match(loaded, e.Spawned, e.Tag, e.Path, e.ClassName, why);
                auto const wait = e.Npc ? PoseRefusal(e.Npc->Tag) : std::string{};
                if (actor == nullptr && e.Spawned && !loaded.Tagged(e.Tag).empty())
                {
                    // Two built copies, or a copy another edit has: which one is ours is not for a pass to guess.
                    e.PendingWhy = why;
                }
                else if (!wait.empty() && (actor != nullptr || (e.Spawned && !e.OnCopy)))
                {
                    e.PendingWhy = wait;
                }
                else if (actor != nullptr)
                {
                    if (!TakeOver(e, actor, loaded, dropped)) continue;
                    applied++;
                    // An effect's particles start again where it is now (docs/design/in-game-editor.md §12).
                    NoteChanged(i);
                    carrier(actor);
                }
                else if (e.Spawned && !e.OnCopy)
                {
                    if (NpcPlace(e, e.Now.Location)) e.Saved = false;
                    respawn.push_back(i);
                }
                else e.PendingWhy = why;
            }

            // The session file's entries. Each stays in the list, done or with why it waits, until the pass ends: a pass that
            // faults midway leaves the list whole. Our NPCs' lights come last: their place is their NPC's reference pose, which a
            // pawn's entry sets (§12).
            std::vector<Entry*> order;
            for (auto& entry : G.Entries)
            {
                if (KindIn(entry.Data) != LightKind::Npc) order.push_back(&entry);
            }
            for (auto& entry : G.Entries)
            {
                if (KindIn(entry.Data) == LightKind::Npc) order.push_back(&entry);
            }
            for (auto const next : order)
            {
                auto& entry = *next;
                auto const& a = entry.Data;
                if (!Valid(a))
                {
                    entry.Why = "malformed: an entry needs a path, a location [x, y, z] and a rotation [pitch, yaw, roll]";
                    continue;
                }
                std::string why;
                if (LevelLightEntry(a))
                {
                    if (ApplyLightEntry(entry, loaded, dropped, why)) applied++;
                    continue;
                }
                auto const spawned = Flag(a, "spawned");
                auto const tag = Text(a, "tag");
                auto const actor = Match(loaded, spawned, tag, Text(a, "path"), Text(a, "class"), why);
                // A level actor not loaded waits; so does a spawn that more than one loaded actor has the tag of.
                if (actor == nullptr && (!spawned || !loaded.Tagged(tag).empty()))
                {
                    entry.Why = why;
                    continue;
                }
                // An NPC light waits while its NPC fails the pose check (docs/design/in-game-editor.md §12): its offsets are measured from a pose the
                // pawn is not at.
                if (auto const link = KindIn(a) == LightKind::Npc ? NpcFrom(a, std::nullopt) : std::nullopt)
                {
                    if (auto wait = PoseRefusal(link->Tag); !wait.empty())
                    {
                        entry.Why = std::move(wait);
                        continue;
                    }
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

                // Applied: an edit from now on, carrying the entry's fields unchanged, but an NPC light's place: its NPC's reference
                // pose plus its offset (§12), unsaved when that moved it.
                auto const pose = PoseFrom(a, actor != nullptr ? PoseOf(actor) : Pose{});
                auto const deleted = Flag(a, "deleted");
                auto& e = G.Edits.emplace_back(EditFrom(a));
                auto place = pose;
                if (NpcPlace(e, place.Location)) e.Saved = false;
                e.Now = place;
                // A field the light block lacks is the light's own, as a pose field is, read with the entry's source (§12).
                if (e.Light && IsLight(actor)) e.Light = LightFrom(a, LightOf(actor, SourceOf(e.Light->Kind)));
                e.SavedState = EntryState{ pose, e.Light, deleted, NpcOffset(e) };
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
                    Put(e, place, deleted);
                    applied++;
                    NoteChanged(IndexOf(e));
                    carrier(actor);
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
            // Our NPCs the pass moved or turned take their lights along, spawned again ones included (docs/design/in-game-editor.md §12).
            if (!carriers.empty())
            {
                G.TrackedStale = true;
                for (auto const& tag : carriers) PlaceLights(tag);
            }
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
                LEASI_ERROR("editor: access violation in a re-apply pass ({}); not tried again until the loaded levels change", FaultText());
                Say("the session was not re-applied: access violation (tried again when the loaded levels change)");
            }
            G.SummaryStale = true;
        }

        /// A map change or a load (docs/design/in-game-editor.md §10). Every edit lets go of its actor (the old world's actors are gone, and a new actor
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
                // The kept edits moved: an index into them no longer holds.
                for (auto& e : G.Edits) e.MadeFor.reset();
                G.Undo.clear();
                G.Redo.clear();
                G.Drag.reset();
                G.Reswitch.clear();
                G.Rerun.clear();
                G.Resets.clear();
                G.LevelLights.clear();
                G.ChannelsOf = nullptr;
                G.DragWarn.reset();
                G.DragShare = -1;
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
            // The props, lights and effects of the old world are gone, and a new prop light's seed is this map's (docs/design/in-game-editor.md §11). So is
            // its Kismet (§12).
            G.Lights.clear();
            G.OtherLights.clear();
            G.Props.clear();
            G.Effects.clear();
            G.Pawns.clear();
            G.TrackedStale = true;
            G.Kismet.clear();
            G.KismetStale = true;
            G.MeshChannels.clear();
            G.LastSeed.reset();
            G.ReswitchFaulted = false;
            // A build since the last map may have added a level of ours, or a stream.
            ReadLevels();
            ReadSession(ToUtf8(MapName()));
        }

        void Run(APlayerController* pc, UCanvas* canvas, Command const& c)
        {
            // A drag's step closes before anything else runs: an undo or a delete mid-drag acts on the drag as a whole.
            auto const drag = c.Type == Command::Kind::Move || c.Type == Command::Kind::Rotate || c.Type == Command::Kind::Scale || c.Type == Command::Kind::Light;
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
            case Command::Kind::Delete: Delete(pc->WorldInfo); break;
            case Command::Kind::Save: Save(); break;
            case Command::Kind::RefreshMeshes: RefreshMeshes(); break;
            case Command::Kind::Undo: UndoStep(false); break;
            case Command::Kind::Redo: UndoStep(true); break;
            // Its step closed above. Paused, the prop keeps the light of where it was until the world ticks.
            case Command::Kind::EndDrag: TickOnce(); break;
            case Command::Kind::PickLight: PickLight(c.Text); break;
            case Command::Kind::Light: SetLight(c.Light); break;
            case Command::Kind::AddLight: AddLight(pc, canvas, c.On); break;
            case Command::Kind::PropLight: PropLight(pc); break;
            case Command::Kind::PickEffect: PickEffect(c.Text); break;
            case Command::Kind::AddNpcLight: AddNpcLight(pc, c.Text); break;
            case Command::Kind::AutoLight:
                G.AutoLight = c.On;
                Say(c.On ? "Auto light on: a fresh spawn that gets too little from prop lights gets one of its own"
                         : "Auto light off: L adds a prop light for the selected prop");
                break;
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

        /// The actors of the visible levels with a part on one of these channels, for a U light's banner (5b): "BioDoor_0 …
        /// BioDoor_7 (8)", or up to three by name; empty for none.
        std::string ActorsOnChannels(DWORD bits)
        {
            std::vector<std::string> names;
            for (auto const& level : G.Levels)
            {
                if (!level.Visible) continue;
                for (auto const actor : static_cast<ULevelBase*>(level.Level)->Actors)
                {
                    if (!Real(actor) || actor->bDeleteMe || IsCollection(actor)) continue;
                    auto const parts = PrimitivesOf(actor);
                    if (std::any_of(parts.begin(), parts.end(), [&](UPrimitiveComponent* p) { return (BitsOf(p->LightingChannels) & bits) != 0; }))
                        names.push_back(ToUtf8(NameOf(actor)));
                }
            }
            if (names.empty()) return {};
            std::sort(names.begin(), names.end(), [](std::string const& a, std::string const& b) {
                return a.size() != b.size() ? a.size() < b.size() : _stricmp(a.c_str(), b.c_str()) < 0;
            });
            if (names.size() > 3) return std::format("{} … {} ({})", names.front(), names.back(), names.size());
            std::string text;
            for (auto const& name : names) text += (text.empty() ? "" : ", ") + name;
            return text;
        }

        /// A selected level light, for the Selection window and the panel (docs/design/in-game-editor.md §12, 5b): the component where its Translation
        /// puts it, with no rotation and scale 1, its collection as its tag, its type, its refusals per command, its ships line
        /// when the panel edits it, its base when it has an edit, and a U light's channels (read once per selection).
        void PublishLevelLight(Snapshot& s, AWorldInfo* world, UCanvas* canvas)
        {
            auto const owner = G.Selected;
            auto const light = G.SelectedComp;
            auto const edit = LightEditOf(light);
            auto const place = PlaceOfLight(light);
            s.Selected = true;
            s.SelectedLocation = place;
            s.SelectedRotation = {};
            s.SelectedDrawScale = 1;
            s.SelectedDrawScale3D = { 1, 1, 1 };
            s.SelectedStatic = true;
            s.SelectedPath = ToUtf8(PathOf(light));
            s.SelectedClass = ToUtf8(NameOf(light->Class));
            s.SelectedTag = ToUtf8(NameOf(owner));
            s.SelectedComponent = true;
            s.SelectedType = TypeOf(light);
            s.SelectedSpot = light->IsA(USpotLightComponent::StaticClass());
            s.SelectedRig = !RigSourceOf(owner).empty();
            s.SelectedUnmovable = LightRefusal(owner, light, LightOp::Move);
            s.SelectedUndeletable = LightRefusal(owner, light, LightOp::Delete);
            s.SelectedUnchangeable = LightRefusal(owner, light, LightOp::Change);
            if (s.SelectedUnchangeable.empty()) s.SelectedShips = LightShips(owner, world);
            s.SelectedFile = FileOf(owner->Outer);
            s.SelectedLight = true;
            s.SelectedLightBlock = edit != nullptr && edit->Light ? *edit->Light : LevelLightOf(light);
            if (edit != nullptr && edit->Base && edit->BaseLight)
            {
                s.SelectedHasBase = true;
                s.SelectedBaseLight = *edit->BaseLight;
                s.SelectedBaseLocation = edit->Base->Location;
            }
            if (s.SelectedType == 'U')
            {
                if (G.ChannelsOf != light || G.ChannelsIndex != light->ObjectInternalInteger)
                {
                    G.ChannelsOf = light;
                    G.ChannelsIndex = light->ObjectInternalInteger;
                    G.Channels = ChannelNames(SpecialBits(light));
                    G.ChannelActors = ActorsOnChannels(SpecialBits(light));
                }
                s.SelectedChannel = G.Channels;
                s.SelectedChannelActors = G.ChannelActors;
            }
            if (Fn.Project != nullptr)
            {
                UCanvas_execProject_Parms project;
                project.Location = place;
                CallProcessEvent(canvas, Fn.Project, &project);
                s.Projected = true;
                s.Projection = project.ReturnValue;
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
            // The icons and the selection's E/T only while the overlay draws them.
            auto const lights = G.Open ? CoverLights() : std::vector<LightSeen>{};
            if (SelectionAlive() && G.SelectedComp != nullptr) PublishLevelLight(s, pc->WorldInfo, canvas);
            else if (SelectionAlive())
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
                s.SelectedUndeletable = Refusal(actor, true);
                s.SelectedUnmovable = Refusal(actor, false);
                // Only for an actor the editor changes: a refused one has no edit to ship.
                if (s.SelectedUnmovable.empty()) s.SelectedShips = ShipsOf(actor, pc->WorldInfo).Text;
                s.SelectedFile = FileOf(actor->Outer);
                if (ListedLight(actor))
                {
                    s.SelectedLight = true;
                    s.SelectedLightBlock = edit != nullptr && edit->Light ? *edit->Light : LightOf(actor, SourceOf(actor));
                    // An NPC's light (docs/design/in-game-editor.md §12): its NPC, its offset, and the reference pose the overlay marks.
                    if (auto const link = NpcLinkOf(actor))
                    {
                        s.SelectedNpcTag = link->Tag;
                        s.SelectedOffset = link->Offset;
                        if (auto const npc = NpcNamed(link->Tag))
                        {
                            auto const pose = ReferencePose(*npc);
                            s.SelectedNpcName = Title(npc->Name);
                            s.SelectedHasPose = true;
                            s.SelectedPoseLocation = pose.Location;
                            s.SelectedPoseYaw = pose.Yaw;
                        }
                    }
                }
                else if (actor->IsA(AEmitter::StaticClass()))
                {
                    // Its template, and whether it loops or ends (docs/design/in-game-editor.md §12): one that ends shows nothing once its 1.5 s ran.
                    auto const component = static_cast<AEmitter*>(actor)->ParticleSystemComponent;
                    auto const system = TemplateOf(component);
                    s.SelectedEffect = true;
                    s.SelectedTemplate = system != nullptr ? ToUtf8(NameOf(system)) : "no template";
                    s.SelectedLoops = Loops(system);
                }
                else if (auto const lighting = G.Open ? LightingOf(actor, lights) : std::nullopt)
                {
                    s.SelectedProp = true;
                    s.SelectedCovered = lighting->Covered;
                    for (auto const light : lighting->CoveringOff)
                    {
                        s.SelectedCoveringOff += (s.SelectedCoveringOff.empty() ? "" : ", ") + NameFor(light);
                    }
                    s.SelectedReceived = lighting->Received;
                    s.SelectedTarget = lighting->Target;
                    s.SelectedAutoRadius = lighting->Auto.Radius;
                }
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
            if (G.Open)
            {
                // Ours, then the NPCs' and the levels' (OtherLights), of the visible levels, by the paths read when tracked.
                for (auto const list : { &G.Lights, &G.OtherLights })
                {
                    for (auto const& t : *list)
                    {
                        if (!StillThere(t) || !Visible(t.Actor)) continue;
                        // A spawned light the editor deleted is left out: undo brings it back.
                        auto const edit = EditOf(t.Actor);
                        if (edit != nullptr && edit->Deleted) continue;
                        s.Lights.push_back({ t.Path, NameText(t.Actor->Tag), t.Actor->Location,
                                             edit != nullptr && edit->Light ? *edit->Light : LightOf(t.Actor, t.Source), t.Npc, edit != nullptr,
                                             edit != nullptr && !edit->Saved });
                    }
                }
                // The level lights of the visible levels (5b): the rig's and edited ones always, others within 30 m of the view.
                for (auto const& t : G.LevelLights)
                {
                    if (!StillThere(t) || !Visible(t.Actor)) continue;
                    auto const edit = LightEditOf(t.Comp);
                    auto const place = PlaceOfLight(t.Comp);
                    auto const metres = Distance(place, G.ViewLocation) / 100;
                    if (t.Rig.empty() && edit == nullptr && metres * 100 > LevelLightReach) continue;
                    LightIcon icon{ t.Path, t.Name, place, edit != nullptr && edit->Light ? *edit->Light : LevelLightOf(t.Comp), {}, edit != nullptr,
                                    edit != nullptr && !edit->Saved };
                    icon.Type = t.Type;
                    icon.Rig = !t.Rig.empty();
                    icon.Collection = t.Owner;
                    icon.Metres = metres;
                    s.Lights.push_back(std::move(icon));
                }
                // Our NPCs of the visible levels, lights or not, for the Lights tab's sections (docs/design/in-game-editor.md §12); then the NPC of a
                // listed light whose NPC's level is not visible (Away), or that levels.json no longer lists, so the light still
                // has a section.
                for (auto const& level : G.Levels)
                {
                    if (!level.Visible) continue;
                    auto const npcs = G.Ours.Npcs.find(Lower(LevelOf(level.Level)));
                    if (npcs == G.Ours.Npcs.end()) continue;
                    for (auto const& npc : npcs->second) s.Npcs.push_back({ Title(npc.Name), npc.Tag, npc.Level });
                }
                for (auto const& l : s.Lights)
                {
                    if (l.Npc.empty() || std::any_of(s.Npcs.begin(), s.Npcs.end(), [&](NpcSection const& n) { return SameText(n.Tag, l.Npc); })) continue;
                    if (auto const npc = NpcNamed(l.Npc)) s.Npcs.push_back({ Title(npc->Name), npc->Tag, npc->Level, true });
                    else s.Npcs.push_back({ {}, l.Npc, {} });
                }
                // The effects of the visible levels, nearest first. A deleted one stays listed: it is only hidden.
                for (auto const& t : G.Effects)
                {
                    if (!StillThere(t) || !Visible(t.Actor)) continue;
                    auto const edit = EditOf(t.Actor);
                    auto const deleted = edit != nullptr && edit->Deleted;
                    s.Effects.push_back({ t.Path, t.Name, t.Template, t.Level, t.Actor->Location, Distance(t.Actor->Location, G.ViewLocation) / 100,
                                          t.Refusal, deleted, !deleted && t.Actor->bHidden, t.Forked, edit != nullptr, edit != nullptr && !edit->Saved });
                }
                std::sort(s.Effects.begin(), s.Effects.end(), [](EffectIcon const& a, EffectIcon const& b) { return a.Metres < b.Metres; });
            }
            s.AutoLight = G.AutoLight;
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
            s.OurLevels = G.Ours.Summary;
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
            // The free cam writes the camera's cache: the view Auto light places toward.
            if (pc->PlayerCamera != nullptr)
            {
                G.ViewLocation = pc->PlayerCamera->CameraCache.POV.Location;
                G.ViewYaw = pc->PlayerCamera->CameraCache.POV.Rotation.Yaw;
            }
            else
            {
                G.ViewLocation = pc->Location;
                G.ViewYaw = pc->Rotation.Yaw;
            }

            // The game pauses this way too (seen while it was unfocused) and unpauses when it is done: the editor keeps its pause.
            if (G.Open && G.PausedWorld != nullptr && G.PausedWorld == pc->WorldInfo)
            {
                // Our own one-tick release (a spawn, the end of a drag, an undo, a pass) is over: not the game's doing. Our timed
                // release (TickFor, an effect step) runs until its deadline, then is over the same way.
                auto const ownTick = std::exchange(G.TickingOnce, false);
                auto const ownRun = G.RunUntil != 0;
                auto const running = ownRun && GetTickCount64() < G.RunUntil;
                if (ownRun && !running) G.RunUntil = 0;
                if (!G.PausedWorld->bPlayersOnly && !running)
                {
                    G.PausedWorld->bPlayersOnly = 1;
                    if (!ownTick && !ownRun)
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
                // After each pass, and each time the loaded levels settle: every switched prop, for the props and lights that came,
                // went or were re-applied (docs/design/in-game-editor.md §11).
                G.TrackedStale = true;
                G.ReswitchAll = true;
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
            // npc_place may have rebuilt it meanwhile (docs/design/in-game-editor.md §12).
            if (G.Open) CheckLevels(false);

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
            if (!G.ReswitchFaulted && !Guarded(ReswitchEntry, pc))
            {
                G.ReswitchFaulted = true;
                G.ReswitchAll = false;
                G.Reswitch.clear();
                G.DragWarn.reset();
                LEASI_ERROR("editor: access violation in the prop lighting switch ({}); not tried again until the loaded levels change", FaultText());
                Say("props are not switched to prop lights: access violation (tried again when the loaded levels change)");
            }
            if (!G.Rerun.empty() && !Guarded(RerunEntry, pc))
            {
                G.Rerun.clear();
                LEASI_ERROR("editor: access violation while restarting effects ({})", FaultText());
                Say("effects not restarted: access violation");
            }
            if (!G.Resets.empty() && !Guarded(ResetEntry, pc))
            {
                G.Resets.clear();
                LEASI_ERROR("editor: access violation while resetting light environments ({})", FaultText());
                Say("characters not updated: access violation");
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
            LEASI_ERROR("editor: access violation on the game thread ({}), editor closed", FaultText());
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
        // Drags send one move, turn, scale or light change per frame: only the latest matters.
        auto const drag = command.Type == Command::Kind::Move || command.Type == Command::Kind::Rotate || command.Type == Command::Kind::Scale
                       || command.Type == Command::Kind::Light;
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
            { "levelsJson", nullptr },
            { "meshesInMemory", s.Meshes != nullptr ? json(s.Meshes->size()) : json(nullptr) },
            { "autoLight", s.AutoLight },
            { "lights", json::array() },
            { "npcs", json::array() },
            { "effects", json::array() },
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
        if (s.OurLevels != nullptr)
        {
            auto const& levels = *s.OurLevels;
            state["levelsJson"] = { { "file", orNull(levels.File) }, { "problem", orNull(levels.Problem) }, { "dlc", orNull(levels.Dlc) },
                                    { "levels", levels.Levels }, { "props", levels.Props }, { "lights", levels.Lights }, { "npcs", levels.Npcs },
                                    { "npcLights", levels.NpcLights }, { "characterLights", levels.CharacterLights } };
        }
        auto const lightJson = [](LightBlock const& l) {
            return json{ { "kind", KindName(l.Kind) }, { "radius", l.Radius }, { "brightness", l.Brightness },
                         { "color", json::array({ l.Color[0], l.Color[1], l.Color[2] }) }, { "falloff", l.Falloff }, { "enabled", l.Enabled } };
        };
        for (auto const& l : s.Lights)
        {
            json light{ { "path", l.Path }, { "tag", l.Tag }, { "location", vector(l.Location) }, { "light", lightJson(l.Light) },
                        { "npc", orNull(l.Npc) }, { "edited", l.Edited }, { "unsaved", l.Unsaved } };
            // A level light's type, rig, collection and distance (5b).
            if (l.Type != 0)
            {
                light["type"] = std::string(1, l.Type);
                light["rig"] = l.Rig;
                light["collection"] = l.Collection;
                light["metres"] = l.Metres;
            }
            state["lights"].push_back(std::move(light));
        }
        for (auto const& n : s.Npcs)
            state["npcs"].push_back({ { "name", orNull(n.Name) }, { "tag", n.Tag }, { "level", orNull(n.Level) }, { "away", n.Away } });
        for (auto const& e : s.Effects)
        {
            state["effects"].push_back({ { "path", e.Path }, { "name", e.Name }, { "template", e.Template }, { "level", e.Level },
                                         { "location", vector(e.Location) }, { "metres", e.Metres }, { "refusal", orNull(e.Refusal) },
                                         { "deleted", e.Deleted }, { "hidden", e.Hidden }, { "forked", e.Forked }, { "edited", e.Edited },
                                         { "unsaved", e.Unsaved } });
        }
        if (s.Selected)
        {
            state["selection"] = { { "path", s.SelectedPath }, { "class", s.SelectedClass }, { "tag", s.SelectedTag },
                                   { "location", vector(s.SelectedLocation) },
                                   { "rotation", json::array({ s.SelectedRotation.Pitch, s.SelectedRotation.Yaw, s.SelectedRotation.Roll }) },
                                   { "drawScale", s.SelectedDrawScale }, { "drawScale3D", vector(s.SelectedDrawScale3D) },
                                   { "pawn", s.SelectedPawn }, { "static", s.SelectedStatic }, { "spawned", s.SelectedSpawned },
                                   { "undeletable", orNull(s.SelectedUndeletable) }, { "unmovable", orNull(s.SelectedUnmovable) },
                                   { "ships", orNull(s.SelectedShips) }, { "file", orNull(s.SelectedFile) }, { "light", nullptr },
                                   { "npc", nullptr }, { "propLighting", nullptr }, { "effect", nullptr } };
            if (s.SelectedLight) state["selection"]["light"] = lightJson(s.SelectedLightBlock);
            // An NPC light's NPC, its offset [forward, right, up] and the reference pose it is measured from (docs/design/in-game-editor.md §12).
            if (!s.SelectedNpcTag.empty())
                state["selection"]["npc"] = { { "name", orNull(s.SelectedNpcName) }, { "tag", s.SelectedNpcTag }, { "offset", vector(s.SelectedOffset) },
                                              { "pose", s.SelectedHasPose ? json{ { "location", vector(s.SelectedPoseLocation) }, { "yaw", s.SelectedPoseYaw } } : json(nullptr) } };
            if (s.SelectedEffect) state["selection"]["effect"] = { { "template", s.SelectedTemplate }, { "loops", s.SelectedLoops } };
            // A level light (5b): the selection is its component; its collection is the tag.
            if (s.SelectedComponent)
            {
                state["selection"]["levelLight"] = { { "type", std::string(1, s.SelectedType) }, { "spot", s.SelectedSpot }, { "rig", s.SelectedRig },
                                                     { "unchangeable", orNull(s.SelectedUnchangeable) },
                                                     { "base", s.SelectedHasBase ? json{ { "location", vector(s.SelectedBaseLocation) }, { "light", lightJson(s.SelectedBaseLight) } } : json(nullptr) },
                                                     { "channels", orNull(s.SelectedChannel) }, { "channelActors", orNull(s.SelectedChannelActors) } };
            }
            if (s.SelectedProp)
                state["selection"]["propLighting"] = { { "covered", s.SelectedCovered }, { "received", s.SelectedReceived }, { "target", s.SelectedTarget },
                                                        { "autoRadius", s.SelectedAutoRadius }, { "coveringOff", s.SelectedCoveringOff } };
        }
        if (c.Valid)
        {
            state["calibration"] = { { "ours", json::array({ c.OursX, c.OursY }) }, { "canvasProject", json::array({ c.CanvasX, c.CanvasY }) },
                                     { "errorPixels", c.ErrorPixels } };
        }
        return state;
    }
}

#endif
