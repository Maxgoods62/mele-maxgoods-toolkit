#include "MeleBridge/Bridge.hpp"

#include <thread>

// \\.\pipe\MeleBridge_LE1: newline-delimited JSON, one request line in, one reply line out, any number of clients at once.
// A client that subscribes gets no more replies: the connection streams one event line per watched call, plus a heartbeat
// line every 2 s when nothing happens, until the client closes it.
namespace MeleBridge
{
    namespace
    {
        constexpr DWORD BufferSize = 64 * 1024;
        constexpr size_t MaxRequest = 1024 * 1024;

        bool WriteLine(HANDLE pipe, std::string line)
        {
            line.push_back('\n');
            DWORD written = 0;
            return WriteFile(pipe, line.data(), static_cast<DWORD>(line.size()), &written, nullptr) && written == line.size();
        }

        void Stream(HANDLE pipe, Subscriber& sub)
        {
            auto quiet = std::chrono::steady_clock::now();
            while (true)
            {
                std::deque<std::string> lines;
                size_t dropped = 0;
                {
                    std::unique_lock lock{ sub.Lock };
                    sub.Ready.wait_for(lock, std::chrono::milliseconds(500), [&] { return !sub.Lines.empty(); });
                    lines.swap(sub.Lines);
                    std::swap(dropped, sub.Dropped);
                }
                if (dropped > 0 && !WriteLine(pipe, json{ { "dropped", dropped } }.dump())) return;
                for (auto& line : lines)
                {
                    if (!WriteLine(pipe, std::move(line))) return;
                }
                if (!lines.empty()) quiet = std::chrono::steady_clock::now();
                else if (std::chrono::steady_clock::now() - quiet > std::chrono::seconds(2))
                {
                    if (!WriteLine(pipe, R"({"heartbeat":true})")) return;
                    quiet = std::chrono::steady_clock::now();
                }
            }
        }

        void Serve(HANDLE pipe)
        {
            std::string pending;
            std::shared_ptr<Subscriber> subscription;
            char buffer[4096];
            DWORD read = 0;
            while (!subscription && ReadFile(pipe, buffer, sizeof buffer, &read, nullptr) && read > 0)
            {
                pending.append(buffer, read);
                if (pending.size() > MaxRequest) break;
                size_t newline;
                while (!subscription && (newline = pending.find('\n')) != std::string::npos)
                {
                    auto const line = pending.substr(0, newline);
                    pending.erase(0, newline + 1);
                    if (line.find_first_not_of(" \r\t") == std::string::npos) continue;

                    json reply;
                    json request;
                    try
                    {
                        request = json::parse(line);
                        reply = HandleRequest(request, subscription);
                    }
                    catch (std::exception const& e)
                    {
                        reply = json{ { "ok", false }, { "error", e.what() } };
                    }
                    if (request.is_object() && request.contains("id")) reply["id"] = request["id"];
                    if (!WriteLine(pipe, reply.dump(-1, ' ', false, json::error_handler_t::replace))) goto done;
                }
            }
            if (subscription) Stream(pipe, *subscription);
        done:
            if (subscription) subscription->Closed.store(true);
            FlushFileBuffers(pipe);
            DisconnectNamedPipe(pipe);
            CloseHandle(pipe);
        }

        void Listen()
        {
            auto const name = L"\\\\.\\pipe\\MeleBridge_" SDK_TARGET_NAME_W;
            while (true)
            {
                HANDLE pipe = CreateNamedPipeW(name, PIPE_ACCESS_DUPLEX, PIPE_TYPE_BYTE | PIPE_READMODE_BYTE | PIPE_WAIT | PIPE_REJECT_REMOTE_CLIENTS,
                                               PIPE_UNLIMITED_INSTANCES, BufferSize, BufferSize, 0, nullptr);
                if (pipe == INVALID_HANDLE_VALUE)
                {
                    LEASI_ERROR("CreateNamedPipe failed: {}", GetLastError());
                    std::this_thread::sleep_for(std::chrono::seconds(1));
                    continue;
                }
                if (ConnectNamedPipe(pipe, nullptr) || GetLastError() == ERROR_PIPE_CONNECTED)
                {
                    std::thread(Serve, pipe).detach();
                }
                else
                {
                    CloseHandle(pipe);
                }
            }
        }
    }

    void StartPipeServer()
    {
        std::thread(Listen).detach();
        LEASI_INFO(L"listening on \\\\.\\pipe\\MeleBridge_{}", SDK_TARGET_NAME_W);
    }
}
