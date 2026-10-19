#include "pipe_client.h"

#include "log.h"

#include <windows.h>

namespace mdr
{
    namespace
    {
        constexpr size_t kMaxQueued = 64;
        constexpr size_t kMaxLine = 16 * 1024;
        constexpr ULONGLONG kPingEveryMs = 2000;
        constexpr ULONGLONG kDeadAfterMs = 8000;
        constexpr DWORD kRetryMs = 1500;
    }

    PipeClient::~PipeClient()
    {
        Stop(300);
    }

    void PipeClient::Start(const std::wstring& pipeName, std::string helloLine)
    {
        if (m_running.exchange(true)) return;
        m_name = L"\\\\.\\pipe\\" + pipeName;
        m_hello = std::move(helloLine);
        m_stopEvent = CreateEventW(nullptr, TRUE, FALSE, nullptr);
        m_sendEvent = CreateEventW(nullptr, FALSE, FALSE, nullptr);
        m_thread = std::thread([this] { Run(); });
    }

    void PipeClient::Stop(unsigned waitMs)
    {
        if (!m_running.exchange(false)) return;
        SetEvent(m_stopEvent);
        if (m_thread.joinable())
        {
            // Never hang the game on exit: wait briefly, then let the OS reap the thread.
            HANDLE h = m_thread.native_handle();
            if (WaitForSingleObject(h, waitMs) == WAIT_OBJECT_0) m_thread.join();
            else m_thread.detach();
        }
    }

    void PipeClient::Send(std::string line)
    {
        if (!m_connected.load()) return;
        {
            std::lock_guard lock(m_mutex);
            if (m_out.size() >= kMaxQueued) m_out.pop_front();
            m_out.push_back(std::move(line));
        }
        SetEvent(m_sendEvent);
    }

    bool PipeClient::PopInbound(std::string& line)
    {
        std::lock_guard lock(m_mutex);
        if (m_in.empty()) return false;
        line = std::move(m_in.front());
        m_in.pop_front();
        return true;
    }

    void PipeClient::Run()
    {
        bool loggedWaiting = false;
        while (WaitForSingleObject(m_stopEvent, 0) != WAIT_OBJECT_0)
        {
            HANDLE pipe = CreateFileW(m_name.c_str(), GENERIC_READ | GENERIC_WRITE, 0, nullptr, OPEN_EXISTING, FILE_FLAG_OVERLAPPED, nullptr);
            if (pipe == INVALID_HANDLE_VALUE)
            {
                DWORD err = GetLastError();
                if (!loggedWaiting)
                {
                    log::Info("BridgeHost not reachable yet (error %lu), retrying in the background", err);
                    loggedWaiting = true;
                }
                if (err == ERROR_PIPE_BUSY) WaitNamedPipeW(m_name.c_str(), 500);
                else WaitForSingleObject(m_stopEvent, kRetryMs);
                continue;
            }

            loggedWaiting = false;
            {
                std::lock_guard lock(m_mutex);
                m_out.clear();
                m_out.push_back(m_hello);
            }
            m_connected = true;
            ++m_epoch;
            log::Info("connected to BridgeHost");
            SetEvent(m_sendEvent);

            RunConnection(pipe);

            m_connected = false;
            CancelIoEx(pipe, nullptr);
            CloseHandle(pipe);
            {
                std::lock_guard lock(m_mutex);
                m_out.clear();
                m_in.push_back("{\"v\":1,\"type\":\"_disconnected\"}");
            }
            log::Info("disconnected from BridgeHost");
            WaitForSingleObject(m_stopEvent, 500);
        }
    }

    bool PipeClient::RunConnection(void* pipeHandle)
    {
        HANDLE pipe = pipeHandle;
        OVERLAPPED rov{};
        OVERLAPPED wov{};
        rov.hEvent = CreateEventW(nullptr, TRUE, FALSE, nullptr);
        wov.hEvent = CreateEventW(nullptr, TRUE, FALSE, nullptr);
        char buffer[4096];
        std::string partial;
        bool readPending = false;
        ULONGLONG lastRx = GetTickCount64();
        ULONGLONG lastPing = lastRx;
        bool ok = true;

        // An overlapped write that did not finish must be cancelled and drained before
        // the OVERLAPPED (on this stack frame) goes away.
        auto abortWrite = [&]()
        {
            CancelIoEx(pipe, &wov);
            DWORD n = 0;
            GetOverlappedResult(pipe, &wov, &n, TRUE);
        };

        while (ok)
        {
            if (!readPending)
            {
                ResetEvent(rov.hEvent);
                if (!ReadFile(pipe, buffer, sizeof(buffer), nullptr, &rov) && GetLastError() != ERROR_IO_PENDING)
                    break;
                readPending = true;
            }

            HANDLE waits[3] = {m_stopEvent, rov.hEvent, m_sendEvent};
            DWORD w = WaitForMultipleObjects(3, waits, FALSE, 500);
            if (w == WAIT_OBJECT_0)
            {
                // graceful goodbye on shutdown (best effort, never blocks long)
                static const char bye[] = "{\"v\":1,\"type\":\"bye\"}\n";
                ResetEvent(wov.hEvent);
                if (WriteFile(pipe, bye, sizeof(bye) - 1, nullptr, &wov) || GetLastError() == ERROR_IO_PENDING)
                {
                    if (WaitForSingleObject(wov.hEvent, 200) != WAIT_OBJECT_0) abortWrite();
                }
                break;
            }

            if (w == WAIT_OBJECT_0 + 1)
            {
                DWORD n = 0;
                if (!GetOverlappedResult(pipe, &rov, &n, FALSE)) break;
                readPending = false;
                if (n == 0) continue;
                lastRx = GetTickCount64();
                partial.append(buffer, n);
                size_t pos;
                while ((pos = partial.find('\n')) != std::string::npos)
                {
                    std::string line = partial.substr(0, pos);
                    partial.erase(0, pos + 1);
                    if (!line.empty() && line.back() == '\r') line.pop_back();
                    if (line.empty()) continue;
                    if (line.find("\"type\":\"ping\"") != std::string::npos)
                    {
                        std::lock_guard lock(m_mutex);
                        m_out.push_back("{\"v\":1,\"type\":\"pong\"}");
                        SetEvent(m_sendEvent);
                        continue;
                    }
                    std::lock_guard lock(m_mutex);
                    if (m_in.size() < 256) m_in.push_back(std::move(line));
                }
                if (partial.size() > kMaxLine) break; // protocol violation
            }

            // flush outgoing queue
            for (;;)
            {
                std::string line;
                {
                    std::lock_guard lock(m_mutex);
                    if (m_out.empty()) break;
                    line = std::move(m_out.front());
                    m_out.pop_front();
                }
                line += '\n';
                ResetEvent(wov.hEvent);
                DWORD written = 0;
                if (!WriteFile(pipe, line.data(), static_cast<DWORD>(line.size()), nullptr, &wov))
                {
                    if (GetLastError() != ERROR_IO_PENDING)
                    {
                        ok = false;
                        break;
                    }
                    HANDLE ws[2] = {wov.hEvent, m_stopEvent};
                    if (WaitForMultipleObjects(2, ws, FALSE, 1000) != WAIT_OBJECT_0)
                    {
                        abortWrite();
                        ok = false;
                        break;
                    }
                }
                if (!GetOverlappedResult(pipe, &wov, &written, FALSE))
                {
                    ok = false;
                    break;
                }
            }

            ULONGLONG now = GetTickCount64();
            if (now - lastPing > kPingEveryMs)
            {
                lastPing = now;
                std::lock_guard lock(m_mutex);
                m_out.push_back("{\"v\":1,\"type\":\"ping\"}");
                SetEvent(m_sendEvent);
            }
            if (now - lastRx > kDeadAfterMs)
            {
                log::Info("BridgeHost stopped answering");
                break;
            }
        }

        if (readPending)
        {
            CancelIoEx(pipe, &rov);
            DWORD n;
            GetOverlappedResult(pipe, &rov, &n, TRUE);
        }
        CloseHandle(rov.hEvent);
        CloseHandle(wov.hEvent);
        return ok;
    }
}
