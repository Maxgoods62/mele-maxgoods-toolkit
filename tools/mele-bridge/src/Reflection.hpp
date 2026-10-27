#pragma once

#include "MeleBridge/Bridge.hpp"

#include <string>
#include <vector>

// UnrealScript reflection over the SDK's memory layouts: object lookup by path, property values to and from JSON, function
// calls through ProcessEvent. The engine's own ImportText/ExportText are virtuals the SDK does not map, so values are converted
// here. Game thread only.
namespace MeleBridge
{
    void InitializeGlobals(::LESDK::Initializer& Init);

    std::wstring PathOf(UObject const* object);
    std::wstring NameOf(UObject const* object);
    bool ContainsNoCase(std::wstring_view text, std::wstring_view part);

    /// The persistent level's package, e.g. BIOA_CLD01.
    std::wstring MapName();

    /// An object by its full path (Outer.Outer.Name, as find returns it), case-insensitive. Throws BridgeError.
    UObject* FindByPath(std::wstring const& path);

    struct FoundObject
    {
        UObject* Object;
        std::wstring Path;
    };

    /// Objects whose class is, or derives from, a class named <paramref name="className"/> (any package), and whose name or
    /// path contains the filters. Class default objects only when asked.
    std::vector<FoundObject> FindObjects(std::wstring const& className, std::wstring const& nameContains, std::wstring const& pathContains,
                                         bool includeDefaults, size_t limit, size_t& total);

    /// Every property of an object, or one: "Location", "Location.X", "Array[2]", "Struct.Array[0].Member".
    json GetProperty(UObject* object, std::wstring const& path, int depth);

    /// Sets one property (same paths as GetProperty) and returns its new value.
    json SetProperty(UObject* object, std::wstring const& path, json const& value);

    /// Calls a function of the object's class (or its parents) by name through ProcessEvent; args in declaration order.
    /// Opcode natives (SetLocation, Trace, Destroy) work too; latent and iterator functions are refused.
    json CallFunction(UObject* object, std::wstring const& function, json const& args);

    /// ProcessEvent with a ready parameter block, opcode natives included. No check: the caller knows the function.
    void CallProcessEvent(UObject* object, UFunction* function, void* parms);

    /// CallProcessEvent of a spawn (Actor.Spawn) with the bNoDelete bit of the spawned class's default object cleared for the
    /// call only: Spawn refuses a class whose default is bNoDelete (every light but SpotLightMovable inherits it from Light), and
    /// the instance copies the cleared bit, so it can be destroyed later. Only that bit is put back, whatever happens.
    void CallSpawnWithoutNoDelete(UObject* object, UFunction* function, void* parms, AActor* classDefault);

    /// A function's parameters (not the return value) from a ProcessEvent parameter block.
    json ExportParams(UFunction* function, BYTE* parms);

    /// Class chain, properties with types, functions with parameters.
    json Describe(UClass* cls, bool withFunctions);

    /// A UFunction by path, e.g. "Engine.SequenceOp.Activated" or "SequenceOp.Activated" (any package).
    UFunction* FindFunctionByPath(std::wstring const& path);
}
