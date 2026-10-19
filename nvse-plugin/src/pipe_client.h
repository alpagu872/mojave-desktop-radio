#pragma once
// Background named-pipe client. The game's main thread only touches two mutex-protected queues
// (Send / PopInbound), so no game frame ever waits on I/O. Reconnects automatically.

#include <atomic>
#include <deque>
#include <mutex>
#include <string>
#include <thread>

namespace mdr
{
    class PipeClient
    {
    public:
        PipeClient() = default;
        ~PipeClient();
        PipeClient(const PipeClient&) = delete;
        PipeClient& operator=(const PipeClient&) = delete;

        // helloLine is sent first on every (re)connection.
        void Start(const std::wstring& pipeName, std::string helloLine);
        void Stop(unsigned waitMs);

        // Queue a JSON line (without '\n'). Dropped while disconnected (state is re-sent on reconnect).
        void Send(std::string line);
        bool PopInbound(std::string& line);

        bool IsConnected() const { return m_connected.load(); }
        // Incremented on every successful connection.
        unsigned Epoch() const { return m_epoch.load(); }

    private:
        void Run();
        bool RunConnection(void* pipe);

        std::wstring m_name;
        std::string m_hello;
        std::thread m_thread;
        void* m_stopEvent = nullptr;
        void* m_sendEvent = nullptr;
        std::mutex m_mutex;
        std::deque<std::string> m_out;
        std::deque<std::string> m_in;
        std::atomic<bool> m_connected{false};
        std::atomic<unsigned> m_epoch{0};
        std::atomic<bool> m_running{false};
    };
}
