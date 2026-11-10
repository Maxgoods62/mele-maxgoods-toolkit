#include "MeleBridge/Bridge.hpp"
#include "MeleBridge/Editor.hpp"
#include "MeleBridge/Reflection.hpp"
#include "MeleBridge/SharedVersion.h"
#include "Common/Objects.hpp"

namespace MeleBridge
{
    namespace
    {
        std::wstring Str(json const& request, char const* key, bool required = false)
        {
            auto const it = request.find(key);
            if (it == request.end() || it->is_null())
            {
                if (required) throw BridgeError(std::string("missing \"") + key + "\"");
                return {};
            }
            if (!it->is_string()) throw BridgeError(std::string("\"") + key + "\" is a string");
            return ToWide(it->get<std::string>());
        }

        /// A missing or null key gives the default.
        template <typename T>
        T Opt(json const& request, char const* key, T fallback)
        {
            auto const it = request.find(key);
            return it == request.end() || it->is_null() ? fallback : it->get<T>();
        }

        /// The loaded class of this name, compared whole and without case, over every object: the find op's substring
        /// search with a limit missed a class whose name many other class names contain, such as Light.
        UClass* ClassNamed(std::wstring const& name)
        {
            auto& objects = *UObject::GObjObjects;
            for (UINT i = 0; i < objects.Count(); i++)
            {
                auto const object = objects.GetData()[i];
                if (object != nullptr && object->IsA(UClass::StaticClass()) && _wcsicmp(NameOf(object).c_str(), name.c_str()) == 0)
                    return static_cast<UClass*>(object);
            }
            throw BridgeError("no class named " + ToUtf8(name) + " is loaded");
        }

        json Ok(json body = json::object())
        {
            body["ok"] = true;
            return body;
        }
    }

    json HandleRequest(json const& request, std::shared_ptr<Subscriber>& subscription)
    {
        auto const op = Str(request, "op", true);
        auto const timeout = std::chrono::milliseconds(Opt(request, "timeoutMs", 5000));

        if (op == L"ping")
        {
            return RunOnGameThread([] {
                return Ok({ { "bridge", VERSION_STRING_A }, { "protocol", MELE_PROTOCOL_VERSION }, { "map", ToUtf8(MapName()) }, { "objects", UObject::GObjObjects->Count() } });
            }, timeout);
        }

        if (op == L"console")
        {
            auto const command = Str(request, "command", true);
            return RunOnGameThread([command] { return Ok(RunConsoleCommand(command)); }, timeout);
        }

        if (op == L"find")
        {
            auto const cls = Str(request, "class");
            auto const name = Str(request, "name");
            auto const path = Str(request, "path");
            auto const defaults = Opt(request, "defaults", false);
            auto const limit = Opt(request, "limit", 100);
            return RunOnGameThread([=] {
                size_t total = 0;
                auto const found = FindObjects(cls, name, path, defaults, static_cast<size_t>(std::max(limit, 0)), total);
                auto objects = json::array();
                for (auto const& f : found) objects.push_back({ { "path", ToUtf8(f.Path) }, { "class", ToUtf8(NameOf(f.Object->Class)) } });
                return Ok({ { "total", total }, { "objects", objects } });
            }, timeout);
        }

        if (op == L"get")
        {
            auto const object = Str(request, "object", true);
            auto const property = Str(request, "property");
            auto const depth = Opt(request, "depth", property.empty() ? 1 : 3);
            return RunOnGameThread([=] { return Ok({ { "value", GetProperty(FindByPath(object), property, depth) } }); }, timeout);
        }

        if (op == L"set")
        {
            auto const object = Str(request, "object", true);
            auto const property = Str(request, "property", true);
            if (!request.contains("value")) throw BridgeError("missing \"value\"");
            auto const value = request["value"];
            return RunOnGameThread([=] { return Ok({ { "value", SetProperty(FindByPath(object), property, value) } }); }, timeout);
        }

        if (op == L"call")
        {
            auto const object = Str(request, "object", true);
            auto const function = Str(request, "function", true);
            auto const args = Opt(request, "args", json::array());
            return RunOnGameThread([=] { return Ok(CallFunction(FindByPath(object), function, args)); }, timeout);
        }

        if (op == L"describe")
        {
            auto const object = Str(request, "object");
            auto const cls = Str(request, "class");
            auto const functions = Opt(request, "functions", true);
            if (object.empty() == cls.empty()) throw BridgeError("give an object or a class");
            return RunOnGameThread([=] {
                auto const target = object.empty() ? ClassNamed(cls) : FindByPath(object)->Class;
                return Ok(Describe(target, functions));
            }, timeout);
        }

        if (op == L"subscribe")
        {
            if (!request.contains("functions") || !request["functions"].is_array() || request["functions"].empty())
                throw BridgeError("give \"functions\": a list of function paths, e.g. [\"Engine.SequenceOp.Activated\"]");
            auto sub = std::make_shared<Subscriber>();
            sub->ObjectContains = Str(request, "objectContains");
            sub->WithParams = Opt(request, "params", true);
            auto const names = request["functions"];
            auto reply = RunOnGameThread([sub, names] {
                auto resolved = json::array();
                for (auto const& n : names)
                {
                    auto const f = FindFunctionByPath(ToWide(n.get<std::string>()));
                    // A function declared in a state (Package.Class.State.Function) has the state as its outer, which no object
                    // IsA: the class that holds the state is the one calls match on, and the state's name keeps the class's own
                    // function and its other states' out of the stream (Publish).
                    Watch watch{ f->Name, nullptr };
                    auto owner = f->Outer;
                    if (owner != nullptr && owner->IsA(UState::StaticClass()) && !owner->IsA(UClass::StaticClass()))
                    {
                        watch.InState = true;
                        watch.State = owner->Name;
                    }
                    while (owner != nullptr && !owner->IsA(UClass::StaticClass())) owner = owner->Outer;
                    if (owner == nullptr) throw BridgeError(ToUtf8(PathOf(f)) + " is in no class");
                    watch.Owner = static_cast<UClass*>(owner);
                    sub->Watches.push_back(watch);
                    resolved.push_back(ToUtf8(PathOf(f)));
                }
                AddSubscriber(sub);
                return Ok({
                    { "functions", resolved },
                    // GameThread.cpp's ProcessEvent hook says why.
                    { "skipped", "calls made while a request runs on the game thread (a call op's function and what it calls through "
                                 "ProcessEvent before it returns, a console command's, status's reads) and the in-game editor's own "
                                 "calls; what a request sets off later is streamed" },
                });
            }, timeout);
            if (reply.value("ok", false)) subscription = sub;
            // A subscription answered as still running is added when its job ends, with no client to read it: closed, the game
            // thread drops it at its next event.
            else sub->Closed.store(true);
            return reply;
        }

        if (op == L"editor")
        {
            // Atomics and a locked snapshot: no game-thread round trip, so it answers even while the game is paused or loading.
            return Ok(Editor::State());
        }

        if (op == L"status")
        {
            return RunOnGameThread([] { return Ok(Status()); }, timeout);
        }

        if (op == L"keep_ticking")
        {
            // On resolves BioWorldInfo's fields and ends a focus-loss pause already there: game thread. Off and a read (no "on")
            // only touch atomics, so they answer while the game loads, like editor.
            auto const it = request.find("on");
            auto const read = it == request.end() || it->is_null();
            if (!read && !it->is_boolean()) throw BridgeError("\"on\" is a bool");
            if (!read && it->get<bool>())
            {
                return RunOnGameThread([] {
                    auto const world = CurrentWorldInfo();
                    auto const cleared = KeepTickingOn(world);
                    auto reply = KeepTickingState();
                    reply["clearedFocusPause"] = cleared;
                    reply["world"] = world ? json(ToUtf8(PathOf(world))) : json(nullptr);
                    return Ok(reply);
                }, timeout);
            }
            if (!read) KeepTickingOff();
            auto reply = KeepTickingState();
            reply["clearedFocusPause"] = nullptr;
            reply["world"] = nullptr;
            return Ok(reply);
        }

        throw BridgeError("unknown op " + ToUtf8(op) + " (ping, console, find, get, set, call, describe, subscribe, editor, status, keep_ticking)");
    }
}
