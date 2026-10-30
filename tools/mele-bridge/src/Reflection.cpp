#include "MeleBridge/Reflection.hpp"
#include "Common/Objects.hpp"

#include <algorithm>
#include <cwctype>
#include <memory>
#include <unordered_map>

namespace MeleBridge
{
    namespace
    {
        // UnrealScript property and function flags (UE3).
        constexpr unsigned long long CPF_OptionalParm = 0x10;
        constexpr unsigned long long CPF_Parm = 0x80;
        constexpr unsigned long long CPF_OutParm = 0x100;
        constexpr unsigned long long CPF_ReturnParm = 0x400;
        constexpr DWORD FUNC_Iterator = 0x4;
        constexpr DWORD FUNC_Latent = 0x8;

        constexpr int MaxArrayItems = 64;

        /// Latent natives need a state frame and iterators read the bytecode after the call: called from outside script, both crash.
        constexpr wchar_t const* LatentOrIterator[] = {
            L"Sleep", L"FinishAnim", L"FinishInterp", L"MoveTo", L"MoveToward", L"MoveToDirectNonPathPos", L"FinishRotation",
            L"WaitForLanding", L"WaitToSeeEnemy", L"AllActors", L"DynamicActors", L"ChildActors", L"BasedActors", L"TouchingActors",
            L"TraceActors", L"VisibleActors", L"VisibleCollidingActors", L"CollidingActors", L"OverlappingActors", L"ComponentList",
            L"AllOwnedComponents", L"LocalPlayerControllers", L"AllControllers", L"AllPawns", L"AllNavigationPoints",
            L"RadiusNavigationPoints", L"NavigationPointCheck",
        };

        /// TArray as it sits in game memory.
        struct RawArray
        {
            BYTE* Data;
            int Count;
            int Max;
        };

        /// FScriptDelegate.
        struct RawDelegate
        {
            UObject* Object;
            SFXName FunctionName;
        };

        enum class Kind { Bool, Byte, Int, Float, Name, Str, Struct, Array, Delegate, Interface, Object, Other };

        Kind KindOf(UProperty const* p)
        {
            if (p->IsA(UBoolProperty::StaticClass())) return Kind::Bool;
            if (p->IsA(UByteProperty::StaticClass())) return Kind::Byte;
            if (p->IsA(UIntProperty::StaticClass())) return Kind::Int;
            if (p->IsA(UFloatProperty::StaticClass())) return Kind::Float;
            if (p->IsA(UNameProperty::StaticClass())) return Kind::Name;
            if (p->IsA(UStrProperty::StaticClass())) return Kind::Str;
            if (p->IsA(UStructProperty::StaticClass())) return Kind::Struct;
            if (p->IsA(UArrayProperty::StaticClass())) return Kind::Array;
            if (p->IsA(UDelegateProperty::StaticClass())) return Kind::Delegate;
            if (p->IsA(UInterfaceProperty::StaticClass())) return Kind::Interface;
            if (p->IsA(UObjectProperty::StaticClass())) return Kind::Object;
            return Kind::Other;
        }

        std::wstring ToStd(FString const& s) { return std::wstring(s.Chars(), s.Length()); }

        std::wstring Lower(std::wstring_view s)
        {
            std::wstring out(s);
            std::transform(out.begin(), out.end(), out.begin(), [](wchar_t c) { return static_cast<wchar_t>(std::towlower(c)); });
            return out;
        }

        bool EqualsNoCase(std::wstring_view a, std::wstring_view b)
        {
            return a.size() == b.size() && std::equal(a.begin(), a.end(), b.begin(), [](wchar_t x, wchar_t y) { return std::towlower(x) == std::towlower(y); });
        }

        std::wstring TypeName(UProperty const* p)
        {
            std::wstring type;
            switch (KindOf(p))
            {
            case Kind::Bool: type = L"bool"; break;
            case Kind::Byte:
            {
                auto const e = static_cast<UByteProperty const*>(p)->Enum;
                type = e ? NameOf(e) : L"byte";
                break;
            }
            case Kind::Int: type = L"int"; break;
            case Kind::Float: type = L"float"; break;
            case Kind::Name: type = L"name"; break;
            case Kind::Str: type = L"string"; break;
            case Kind::Struct: type = L"struct " + NameOf(static_cast<UStructProperty const*>(p)->Struct); break;
            case Kind::Array: type = L"array<" + TypeName(static_cast<UArrayProperty const*>(p)->Inner) + L">"; break;
            case Kind::Delegate: type = L"delegate"; break;
            case Kind::Interface: type = L"interface " + NameOf(static_cast<UInterfaceProperty const*>(p)->InterfaceClass); break;
            case Kind::Object:
                type = p->IsA(UClassProperty::StaticClass())
                    ? L"class<" + NameOf(static_cast<UClassProperty const*>(p)->MetaClass) + L">"
                    : NameOf(static_cast<UObjectProperty const*>(p)->PropertyClass);
                break;
            default: type = NameOf(p->Class); break;
            }
            if (p->ArrayDim > 1) type += L"[" + std::to_wstring(p->ArrayDim) + L"]";
            return type;
        }

        /// Properties of a class or struct, inherited ones included, in the engine's link order.
        template <typename F>
        void ForEachProperty(UStruct const* scope, F&& f)
        {
            for (UProperty* p = scope->PropertyLink; p != nullptr; p = p->PropertyLinkNext) f(p);
        }

        /// A function's parameters, in declaration order.
        std::vector<UProperty*> ParamsOf(UFunction const* function)
        {
            std::vector<UProperty*> params;
            for (UField* f = function->Children; f != nullptr; f = f->Next)
            {
                if (f->IsA(UProperty::StaticClass()) && (static_cast<UProperty*>(f)->PropertyFlags & CPF_Parm)) params.push_back(static_cast<UProperty*>(f));
            }
            return params;
        }

        UProperty* FindProperty(UStruct const* scope, std::wstring_view name)
        {
            UProperty* found = nullptr;
            ForEachProperty(scope, [&](UProperty* p) { if (!found && EqualsNoCase(NameOf(p), name)) found = p; });
            if (!found) throw BridgeError("no property '" + ToUtf8(name) + "' in " + ToUtf8(NameOf(scope)));
            return found;
        }

        // ! Export
        // ========================================

        json ExportValue(UProperty const* p, BYTE* addr, int depth);

        json ExportStaticArray(UProperty const* p, BYTE* addr, int depth)
        {
            if (p->ArrayDim <= 1) return ExportValue(p, addr, depth);
            auto items = json::array();
            for (int i = 0; i < p->ArrayDim; i++) items.push_back(ExportValue(p, addr + static_cast<size_t>(i) * p->ElementSize, depth));
            return items;
        }

        json ExportValue(UProperty const* p, BYTE* addr, int depth)
        {
            switch (KindOf(p))
            {
            case Kind::Bool: return (*reinterpret_cast<DWORD*>(addr) & static_cast<UBoolProperty const*>(p)->BitMask) != 0;
            case Kind::Byte:
            {
                auto const value = *addr;
                auto const e = static_cast<UByteProperty const*>(p)->Enum;
                if (e != nullptr && value < e->Names.Count()) return ToUtf8(ToStd(e->Names.GetData()[value].ToString()));
                return value;
            }
            case Kind::Int: return *reinterpret_cast<int*>(addr);
            case Kind::Float: return *reinterpret_cast<float*>(addr);
            case Kind::Name: return ToUtf8(ToStd(reinterpret_cast<SFXName*>(addr)->ToString()));
            case Kind::Str: return ToUtf8(ToStd(*reinterpret_cast<FString*>(addr)));
            case Kind::Object:
            case Kind::Interface:
            {
                auto const object = *reinterpret_cast<UObject**>(addr);
                return object ? json(ToUtf8(PathOf(object))) : json(nullptr);
            }
            case Kind::Delegate:
            {
                auto const d = reinterpret_cast<RawDelegate*>(addr);
                if (d->Object == nullptr) return nullptr;
                return ToUtf8(PathOf(d->Object) + L"." + ToStd(d->FunctionName.ToString()));
            }
            case Kind::Struct:
            {
                auto const s = static_cast<UStructProperty const*>(p)->Struct;
                if (depth <= 0) return "{" + ToUtf8(NameOf(s)) + "}";
                auto members = json::object();
                ForEachProperty(s, [&](UProperty* m) { members[ToUtf8(NameOf(m))] = ExportStaticArray(m, addr + m->Offset, depth - 1); });
                return members;
            }
            case Kind::Array:
            {
                auto const a = reinterpret_cast<RawArray*>(addr);
                if (depth <= 0) return "[" + std::to_string(a->Count) + " items]";
                auto const inner = static_cast<UArrayProperty const*>(p)->Inner;
                auto items = json::array();
                auto const shown = std::min(a->Count, MaxArrayItems);
                for (int i = 0; i < shown; i++) items.push_back(ExportValue(inner, a->Data + static_cast<size_t>(i) * inner->ElementSize, depth - 1));
                if (a->Count > shown) items.push_back("... " + std::to_string(a->Count - shown) + " more");
                return items;
            }
            default: return "<" + ToUtf8(NameOf(p->Class)) + ">";
            }
        }

        // ! Import
        // ========================================

        bool ParseBool(json const& v)
        {
            if (v.is_boolean()) return v.get<bool>();
            if (v.is_number()) return v.get<double>() != 0;
            if (v.is_string())
            {
                auto const s = Lower(ToWide(v.get<std::string>()));
                if (s == L"true" || s == L"1") return true;
                if (s == L"false" || s == L"0") return false;
            }
            throw BridgeError("not a bool: " + v.dump());
        }

        double ParseNumber(json const& v)
        {
            if (v.is_number()) return v.get<double>();
            if (v.is_boolean()) return v.get<bool>() ? 1 : 0;
            if (v.is_string())
            {
                try
                {
                    size_t used = 0;
                    auto const s = v.get<std::string>();
                    auto const d = std::stod(s, &used);
                    if (used == s.size()) return d;
                }
                catch (std::exception const&)
                {
                }
            }
            throw BridgeError("not a number: " + v.dump());
        }

        void ImportValue(UProperty const* p, BYTE* addr, json const& v)
        {
            switch (KindOf(p))
            {
            case Kind::Bool:
            {
                auto& word = *reinterpret_cast<DWORD*>(addr);
                auto const mask = static_cast<UBoolProperty const*>(p)->BitMask;
                word = ParseBool(v) ? (word | mask) : (word & ~mask);
                return;
            }
            case Kind::Byte:
            {
                auto const e = static_cast<UByteProperty const*>(p)->Enum;
                if (v.is_string() && e != nullptr)
                {
                    auto const name = ToWide(v.get<std::string>());
                    for (UINT i = 0; i < e->Names.Count(); i++)
                    {
                        if (EqualsNoCase(ToStd(e->Names.GetData()[i].ToString()), name))
                        {
                            *addr = static_cast<BYTE>(i);
                            return;
                        }
                    }
                }
                auto const d = ParseNumber(v);
                if (d < 0 || d > 255 || d != static_cast<int>(d)) throw BridgeError("not a byte or enum value: " + v.dump());
                *addr = static_cast<BYTE>(d);
                return;
            }
            case Kind::Int:
            {
                auto const d = ParseNumber(v);
                if (d != static_cast<int>(d)) throw BridgeError("not an int: " + v.dump());
                *reinterpret_cast<int*>(addr) = static_cast<int>(d);
                return;
            }
            case Kind::Float: *reinterpret_cast<float*>(addr) = static_cast<float>(ParseNumber(v)); return;
            case Kind::Name:
                if (!v.is_string()) throw BridgeError("a name is a string: " + v.dump());
                *reinterpret_cast<SFXName*>(addr) = SFXName(ToWide(v.get<std::string>()).c_str(), 0, true);
                return;
            case Kind::Str:
                if (!v.is_string()) throw BridgeError("a string property takes a string: " + v.dump());
                *reinterpret_cast<FString*>(addr) = ToWide(v.get<std::string>()).c_str();
                return;
            case Kind::Object:
            {
                auto& slot = *reinterpret_cast<UObject**>(addr);
                if (v.is_null() || (v.is_string() && (v.get<std::string>().empty() || v.get<std::string>() == "None")))
                {
                    slot = nullptr;
                    return;
                }
                if (!v.is_string()) throw BridgeError("an object property takes a path or null: " + v.dump());
                auto const object = FindByPath(ToWide(v.get<std::string>()));
                auto const required = static_cast<UObjectProperty const*>(p)->PropertyClass;
                if (required != nullptr && !object->IsA(required))
                    throw BridgeError(v.get<std::string>() + " is a " + ToUtf8(NameOf(object->Class)) + ", not a " + ToUtf8(NameOf(required)));
                slot = object;
                return;
            }
            case Kind::Struct:
            {
                if (!v.is_object()) throw BridgeError("a struct takes an object of members: " + v.dump());
                auto const s = static_cast<UStructProperty const*>(p)->Struct;
                for (auto const& [key, value] : v.items())
                {
                    auto const m = FindProperty(s, ToWide(key));
                    ImportValue(m, addr + m->Offset, value);
                }
                return;
            }
            case Kind::Array:
            {
                auto const a = reinterpret_cast<RawArray*>(addr);
                if (!v.is_array() || static_cast<int>(v.size()) != a->Count)
                    throw BridgeError("an array keeps its length here (" + std::to_string(a->Count) + "): set elements as Name[i]");
                auto const inner = static_cast<UArrayProperty const*>(p)->Inner;
                for (int i = 0; i < a->Count; i++) ImportValue(inner, a->Data + static_cast<size_t>(i) * inner->ElementSize, v[i]);
                return;
            }
            default: throw BridgeError("cannot set a " + ToUtf8(TypeName(p)));
            }
        }

        /// Frees what ImportValue or a call put in a parameter block: strings and arrays live in the game's heap.
        void DestroyValue(UProperty const* p, BYTE* addr)
        {
            switch (KindOf(p))
            {
            case Kind::Str: std::destroy_at(reinterpret_cast<FString*>(addr)); break;
            case Kind::Struct:
                ForEachProperty(static_cast<UStructProperty const*>(p)->Struct, [&](UProperty* m) { DestroyValue(m, addr + m->Offset); });
                return;
            case Kind::Array:
            {
                auto const a = reinterpret_cast<RawArray*>(addr);
                auto const inner = static_cast<UArrayProperty const*>(p)->Inner;
                for (int i = 0; i < a->Count; i++) DestroyValue(inner, a->Data + static_cast<size_t>(i) * inner->ElementSize);
                if (a->Data != nullptr) (*GMalloc)->Free(a->Data);
                break;
            }
            default: return;
            }
            std::memset(addr, 0, static_cast<size_t>(p->ElementSize));
        }

        // ! Property paths
        // ========================================

        struct Target
        {
            UProperty const* Property;
            BYTE* Address;
            bool WholeStaticArray;
        };

        /// "A.B[2].C" from an object (or struct) at base.
        Target Walk(UStruct const* scope, BYTE* base, std::wstring const& path)
        {
            Target t{ nullptr, nullptr, false };
            size_t start = 0;
            while (start <= path.size())
            {
                auto const dot = path.find(L'.', start);
                auto segment = path.substr(start, dot == std::wstring::npos ? std::wstring::npos : dot - start);
                start = dot == std::wstring::npos ? path.size() + 1 : dot + 1;

                int index = -1;
                if (auto const bracket = segment.find(L'['); bracket != std::wstring::npos)
                {
                    if (segment.back() != L']') throw BridgeError("bad index in '" + ToUtf8(segment) + "'");
                    index = std::stoi(segment.substr(bracket + 1, segment.size() - bracket - 2));
                    segment.resize(bracket);
                }

                UProperty const* p = FindProperty(scope, segment);
                BYTE* addr = base + p->Offset;
                t.WholeStaticArray = p->ArrayDim > 1 && index < 0;
                if (index >= 0)
                {
                    if (p->ArrayDim > 1)
                    {
                        if (index >= p->ArrayDim) throw BridgeError(ToUtf8(segment) + " has " + std::to_string(p->ArrayDim) + " elements");
                        addr += static_cast<size_t>(index) * p->ElementSize;
                    }
                    else if (KindOf(p) == Kind::Array)
                    {
                        auto const a = reinterpret_cast<RawArray*>(addr);
                        if (index >= a->Count) throw BridgeError(ToUtf8(segment) + " has " + std::to_string(a->Count) + " elements");
                        p = static_cast<UArrayProperty const*>(p)->Inner;
                        addr = a->Data + static_cast<size_t>(index) * p->ElementSize;
                    }
                    else throw BridgeError(ToUtf8(segment) + " is not an array");
                }
                t.Property = p;
                t.Address = addr;

                if (start <= path.size())
                {
                    if (KindOf(p) != Kind::Struct || t.WholeStaticArray) throw BridgeError(ToUtf8(segment) + " is not a struct");
                    scope = static_cast<UStructProperty const*>(p)->Struct;
                    base = addr;
                }
            }
            return t;
        }

        UFunction* FindFunction(UClass const* cls, std::wstring_view name)
        {
            for (UStruct const* s = cls; s != nullptr; s = static_cast<UStruct const*>(s->SuperField))
            {
                for (UField* f = s->Children; f != nullptr; f = f->Next)
                {
                    if (f->IsA(UFunction::StaticClass()) && EqualsNoCase(NameOf(f), name)) return static_cast<UFunction*>(f);
                }
            }
            throw BridgeError("no function '" + ToUtf8(name) + "' in " + ToUtf8(NameOf(cls)) + " or its parents");
        }

        /// Path → (GObjects index, object), checked again on use: objects come and go with levels.
        std::unordered_map<std::wstring, std::pair<int, UObject*>> PathCache;
    }

    // ! Public
    // ========================================

    void InitializeGlobals(::LESDK::Initializer& Init)
    {
        Common::InitializeRequiredGlobals(Init);
        GWorld = Init.ResolveTyped<UWorld*>(BUILTIN_GWORLD_RVA);
        CHECK_RESOLVED(GWorld);
    }

    std::string ToUtf8(std::wstring_view text)
    {
        if (text.empty()) return {};
        auto const size = WideCharToMultiByte(CP_UTF8, 0, text.data(), static_cast<int>(text.size()), nullptr, 0, nullptr, nullptr);
        std::string out(static_cast<size_t>(size), '\0');
        WideCharToMultiByte(CP_UTF8, 0, text.data(), static_cast<int>(text.size()), out.data(), size, nullptr, nullptr);
        return out;
    }

    std::wstring ToWide(std::string_view text)
    {
        if (text.empty()) return {};
        auto const size = MultiByteToWideChar(CP_UTF8, 0, text.data(), static_cast<int>(text.size()), nullptr, 0);
        std::wstring out(static_cast<size_t>(size), L'\0');
        MultiByteToWideChar(CP_UTF8, 0, text.data(), static_cast<int>(text.size()), out.data(), size);
        return out;
    }

    std::wstring PathOf(UObject const* object) { return object ? ToStd(object->GetFullPath()) : L"None"; }

    std::wstring NameOf(UObject const* object) { return object ? ToStd(object->GetName()) : L"None"; }

    bool ContainsNoCase(std::wstring_view text, std::wstring_view part)
    {
        return std::search(text.begin(), text.end(), part.begin(), part.end(),
                           [](wchar_t x, wchar_t y) { return std::towlower(x) == std::towlower(y); }) != text.end();
    }

    bool StartsWithNoCase(std::wstring_view text, std::wstring_view start)
    {
        return text.size() >= start.size() && EqualsNoCase(text.substr(0, start.size()), start);
    }

    std::wstring MapName()
    {
        if (GWorld == nullptr || *GWorld == nullptr) return L"";
        auto const world = reinterpret_cast<UObject*>(*GWorld);
        return world->Outer ? NameOf(world->Outer) : L"";
    }

    UObject* FindByPath(std::wstring const& rawPath)
    {
        // Also takes "Class Path", as some tools print it.
        auto path = rawPath;
        if (auto const space = path.find(L' '); space != std::wstring::npos) path = path.substr(space + 1);
        auto const key = Lower(path);
        auto& objects = *UObject::GObjObjects;

        if (auto const hit = PathCache.find(key); hit != PathCache.end())
        {
            auto const [index, object] = hit->second;
            if (index < static_cast<int>(objects.Count()) && objects.GetData()[index] == object && EqualsNoCase(PathOf(object), path)) return object;
            PathCache.erase(hit);
        }

        auto const dot = path.rfind(L'.');
        auto const name = dot == std::wstring::npos ? path : path.substr(dot + 1);
        for (int i = 0; i < static_cast<int>(objects.Count()); i++)
        {
            auto const object = objects.GetData()[i];
            if (object == nullptr || !EqualsNoCase(NameOf(object), name) || !EqualsNoCase(PathOf(object), path)) continue;
            PathCache[key] = { i, object };
            return object;
        }
        throw BridgeError("no object at " + ToUtf8(path) + " (paths as find returns them)");
    }

    std::vector<FoundObject> FindObjects(std::wstring const& className, std::wstring const& nameContains, std::wstring const& pathContains,
                                         bool includeDefaults, size_t limit, size_t& total)
    {
        if (className.empty() && nameContains.empty() && pathContains.empty()) throw BridgeError("give a class, a name or a path filter");

        auto& objects = *UObject::GObjObjects;
        std::vector<UClass*> classes;
        if (!className.empty())
        {
            for (UINT i = 0; i < objects.Count(); i++)
            {
                auto const object = objects.GetData()[i];
                if (object != nullptr && object->IsA(UClass::StaticClass()) && EqualsNoCase(NameOf(object), className)) classes.push_back(static_cast<UClass*>(object));
            }
            if (classes.empty()) throw BridgeError("no class named " + ToUtf8(className) + " is loaded");
        }

        std::vector<FoundObject> found;
        total = 0;
        for (UINT i = 0; i < objects.Count(); i++)
        {
            auto const object = objects.GetData()[i];
            if (object == nullptr || Common::IsDestroyed(object)) continue;
            if (!includeDefaults && Common::IsDefaultObject(object)) continue;
            if (!classes.empty() && std::none_of(classes.begin(), classes.end(), [&](UClass* c) { return object->IsA(c); })) continue;
            if (!nameContains.empty() && !ContainsNoCase(NameOf(object), nameContains)) continue;
            auto path = PathOf(object);
            if (!pathContains.empty() && !ContainsNoCase(path, pathContains)) continue;
            if (++total <= limit) found.push_back({ object, std::move(path) });
        }
        return found;
    }

    json GetProperty(UObject* object, std::wstring const& path, int depth)
    {
        if (path.empty())
        {
            auto all = json::object();
            ForEachProperty(object->Class, [&](UProperty* p) { all[ToUtf8(NameOf(p))] = ExportStaticArray(p, reinterpret_cast<BYTE*>(object) + p->Offset, depth); });
            return all;
        }
        auto const t = Walk(object->Class, reinterpret_cast<BYTE*>(object), path);
        return t.WholeStaticArray ? ExportStaticArray(t.Property, t.Address, depth) : ExportValue(t.Property, t.Address, depth);
    }

    int PropertyOffset(UStruct const* scope, std::wstring_view name)
    {
        int offset = -1;
        ForEachProperty(scope, [&](UProperty* p) { if (offset < 0 && EqualsNoCase(NameOf(p), name)) offset = p->Offset; });
        return offset;
    }

    bool BoolFieldOf(UStruct const* scope, std::wstring_view name, int& offset, DWORD& mask)
    {
        UProperty* found = nullptr;
        ForEachProperty(scope, [&](UProperty* p) { if (!found && EqualsNoCase(NameOf(p), name)) found = p; });
        if (found == nullptr || KindOf(found) != Kind::Bool) return false;
        offset = found->Offset;
        mask = static_cast<UBoolProperty const*>(found)->BitMask;
        return true;
    }

    json SetProperty(UObject* object, std::wstring const& path, json const& value)
    {
        auto const t = Walk(object->Class, reinterpret_cast<BYTE*>(object), path);
        if (t.WholeStaticArray) throw BridgeError(ToUtf8(path) + " is a static array: set one element, e.g. " + ToUtf8(path) + "[0]");
        ImportValue(t.Property, t.Address, value);
        return ExportValue(t.Property, t.Address, 3);
    }

    void CallProcessEvent(UObject* object, UFunction* function, void* parms)
    {
        // ProcessEvent refuses a function with an opcode number (iNative). With it zeroed for the call, ProcessEvent calls Func
        // (GNatives[iNative]) like any other native: LEX Interop and every UE3 SDK do this. Flags are restored as they were;
        // the SDK's generated wrappers instead leave every flag bit set.
        auto const native = function->iNative;
        auto const flags = function->FunctionFlags;
        function->iNative = 0;
        __try
        {
            object->ProcessEvent(function, parms, nullptr);
        }
        __finally
        {
            function->iNative = native;
            function->FunctionFlags = flags;
        }
    }

    void CallSpawnWithoutNoDelete(UObject* object, UFunction* function, void* parms, AActor* classDefault)
    {
        // AActor.bNoDelete: 0x270, mask 0x4 (Engine_classes.hpp). A bit-field write changes that bit alone. The failure to cover
        // is an access violation caught by the caller's __except: under /EHsc that unwind skips C++ destructors, so a scope
        // guard could miss it, and a default left without bNoDelete would make every light of the class loaded later
        // deletable. No C++ object may live in this frame.
        auto const saved = classDefault->bNoDelete;
        classDefault->bNoDelete = 0;
        __try
        {
            CallProcessEvent(object, function, parms);
        }
        __finally
        {
            classDefault->bNoDelete = saved;
        }
    }

    json CallFunction(UObject* object, std::wstring const& name, json const& args)
    {
        auto const function = FindFunction(object->Class, name);
        // The SDK's generated wrappers (LEX Interop calls ConsoleCommand through one) leave every flag bit set: no flag is known then,
        // so the names of the engine's latent and iterator natives back the flags up.
        if (function->FunctionFlags != 0xFFFFFFFF && (function->FunctionFlags & (FUNC_Iterator | FUNC_Latent)))
            throw BridgeError(ToUtf8(name) + " is latent or an iterator: it cannot be called from outside script");
        if (std::any_of(std::begin(LatentOrIterator), std::end(LatentOrIterator), [&](wchar_t const* n) { return EqualsNoCase(NameOf(function), n); }))
            throw BridgeError(ToUtf8(name) + " is latent or an iterator: it cannot be called from outside script");
        if (!args.is_array()) throw BridgeError("args is a list");

        auto const params = ParamsOf(function);
        std::vector<UProperty*> inputs;
        UProperty* returned = nullptr;
        for (auto p : params) (p->PropertyFlags & CPF_ReturnParm) ? void(returned = p) : inputs.push_back(p);
        if (args.size() > inputs.size()) throw BridgeError(ToUtf8(name) + " takes " + std::to_string(inputs.size()) + " arguments");

        std::vector<BYTE> block(std::max<size_t>(function->ParmsSize, 16), 0);
        auto const destroyAll = [&] { for (auto p : params) DestroyValue(p, block.data() + p->Offset); };
        try
        {
            for (size_t i = 0; i < inputs.size(); i++)
            {
                auto const p = inputs[i];
                if (i < args.size()) ImportValue(p, block.data() + p->Offset, args[i]);
                else if (!(p->PropertyFlags & (CPF_OptionalParm | CPF_OutParm)))
                    throw BridgeError("missing argument " + std::to_string(i + 1) + ": " + ToUtf8(TypeName(p)) + " " + ToUtf8(NameOf(p)));
            }
        }
        catch (...)
        {
            destroyAll();
            throw;
        }

        CallProcessEvent(object, function, block.data());

        json result = json::object();
        if (returned != nullptr) result["return"] = ExportValue(returned, block.data() + returned->Offset, 3);
        auto out = json::object();
        for (auto p : inputs)
        {
            if (p->PropertyFlags & CPF_OutParm) out[ToUtf8(NameOf(p))] = ExportValue(p, block.data() + p->Offset, 3);
        }
        if (!out.empty()) result["out"] = out;
        destroyAll();
        return result;
    }

    json ExportParams(UFunction* function, BYTE* parms)
    {
        auto values = json::object();
        for (auto p : ParamsOf(function))
        {
            if (!(p->PropertyFlags & CPF_ReturnParm)) values[ToUtf8(NameOf(p))] = ExportValue(p, parms + p->Offset, 1);
        }
        return values;
    }

    json Describe(UClass* cls, bool withFunctions)
    {
        auto chain = json::array();
        for (UStruct const* s = cls; s != nullptr; s = static_cast<UStruct const*>(s->SuperField)) chain.push_back(ToUtf8(NameOf(s)));

        auto properties = json::array();
        ForEachProperty(cls, [&](UProperty* p) {
            properties.push_back({ { "name", ToUtf8(NameOf(p)) }, { "type", ToUtf8(TypeName(p)) }, { "owner", ToUtf8(NameOf(p->Outer)) } });
        });

        json result{ { "class", ToUtf8(PathOf(cls)) }, { "chain", chain }, { "properties", properties } };
        if (!withFunctions) return result;

        auto functions = json::array();
        std::vector<std::wstring> seen;
        for (UStruct const* s = cls; s != nullptr; s = static_cast<UStruct const*>(s->SuperField))
        {
            for (UField* f = s->Children; f != nullptr; f = f->Next)
            {
                if (!f->IsA(UFunction::StaticClass())) continue;
                auto const name = NameOf(f);
                if (std::find(seen.begin(), seen.end(), Lower(name)) != seen.end()) continue;
                seen.push_back(Lower(name));

                auto const fn = static_cast<UFunction*>(f);
                std::wstring signature;
                std::wstring returns = L"void";
                for (auto p : ParamsOf(fn))
                {
                    if (p->PropertyFlags & CPF_ReturnParm)
                    {
                        returns = TypeName(p);
                        continue;
                    }
                    if (!signature.empty()) signature += L", ";
                    if (p->PropertyFlags & CPF_OptionalParm) signature += L"optional ";
                    if (p->PropertyFlags & CPF_OutParm) signature += L"out ";
                    signature += TypeName(p) + L" " + NameOf(p);
                }
                functions.push_back({ { "name", ToUtf8(name) }, { "owner", ToUtf8(NameOf(s)) },
                                      { "signature", ToUtf8(returns + L" " + name + L"(" + signature + L")") } });
            }
        }
        result["functions"] = functions;
        return result;
    }

    UFunction* FindFunctionByPath(std::wstring const& rawPath)
    {
        auto path = rawPath;
        if (path.starts_with(L"Function ")) path = path.substr(9);
        auto& objects = *UObject::GObjObjects;
        auto const dot = path.rfind(L'.');
        auto const name = dot == std::wstring::npos ? path : path.substr(dot + 1);
        for (UINT i = 0; i < objects.Count(); i++)
        {
            auto const object = objects.GetData()[i];
            if (object == nullptr || !object->IsA(UFunction::StaticClass()) || !EqualsNoCase(NameOf(object), name)) continue;
            auto const full = PathOf(object);
            // "SequenceOp.Activated" matches "Engine.SequenceOp.Activated".
            if (EqualsNoCase(full, path) || (full.size() > path.size() && full[full.size() - path.size() - 1] == L'.'
                                             && EqualsNoCase(std::wstring_view(full).substr(full.size() - path.size()), path)))
                return static_cast<UFunction*>(object);
        }
        throw BridgeError("no function at " + ToUtf8(path) + " is loaded");
    }
}
