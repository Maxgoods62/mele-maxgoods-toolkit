#include "MeleBridge/Bridge.hpp"
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

        UClass* ClassNamed(std::wstring const& name)
        {
            size_t total = 0;
            auto const found = FindObjects(L"Class", name, L"", true, 50, total);
            for (auto const& f : found)
            {
                if (_wcsicmp(NameOf(f.Object).c_str(), name.c_str()) == 0) return static_cast<UClass*>(f.Object);
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
                return Ok({ { "bridge", VERSION_STRING_A }, { "map", ToUtf8(MapName()) }, { "objects", UObject::GObjObjects->Count() } });
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
                    sub->Watches.push_back({ f->Name, static_cast<UClass*>(f->Outer) });
                    resolved.push_back(ToUtf8(PathOf(f)));
                }
                AddSubscriber(sub);
                return Ok({ { "functions", resolved } });
            }, timeout);
            if (reply.value("ok", false)) subscription = sub;
            return reply;
        }

        throw BridgeError("unknown op " + ToUtf8(op) + " (ping, console, find, get, set, call, describe, subscribe)");
    }
}
