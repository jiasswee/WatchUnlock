#include <windows.h>
#include <credentialprovider.h>
#include <ntsecapi.h>
#include <wincred.h>
#include <shlwapi.h>
#include <atomic>
#include <mutex>
#include <memory>
#include <string>
#include <vector>
#include <new>
#include <cstring>

// Registry installation scripts use the same CLSID. No provider filter is registered.
static const CLSID ProviderId = {0x18cc018c, 0x6398, 0x4e25, {0x84, 0x07, 0x3d, 0x4d, 0x89, 0x7e, 0x6f, 0x30}};
static std::atomic<long> objects{0};
static constexpr DWORD Magic = 0x31554357;

struct Handle
{
    HANDLE value = INVALID_HANDLE_VALUE;
    explicit Handle(HANDLE h = INVALID_HANDLE_VALUE) : value(h) {}
    ~Handle() { if (value && value != INVALID_HANDLE_VALUE) CloseHandle(value); }
    Handle(const Handle&) = delete;
    Handle& operator=(const Handle&) = delete;
};

struct Secret
{
    std::vector<BYTE> bytes;
    ~Secret() { Clear(); }
    void Clear() { if (!bytes.empty()) SecureZeroMemory(bytes.data(), bytes.size()); bytes.clear(); }
};

struct Config
{
    std::wstring sid, account, revision;
    static std::wstring Text(const wchar_t* name)
    {
        wchar_t text[256]{};
        DWORD size = sizeof(text);
        if (RegGetValueW(HKEY_LOCAL_MACHINE, L"SOFTWARE\\WatchUnlock", name,
            RRF_RT_REG_SZ | RRF_SUBKEY_WOW6464KEY, nullptr, text, &size) != ERROR_SUCCESS) return {};
        return text;
    }
    static bool Read(Config& config)
    {
        DWORD enabled = 0, size = sizeof(enabled);
        if (RegGetValueW(HKEY_LOCAL_MACHINE, L"SOFTWARE\\WatchUnlock", L"LoginEnabled",
            RRF_RT_REG_DWORD | RRF_SUBKEY_WOW6464KEY, nullptr, &enabled, &size) != ERROR_SUCCESS || enabled != 1) return false;
        config = {Text(L"AccountSid"), Text(L"AccountName"), Text(L"LoginRevision")};
        return !config.sid.empty() && config.sid.size() <= 92 && !config.account.empty() && !config.revision.empty();
    }
    bool Current() const
    {
        Config current;
        return Read(current) && current.sid == sid && current.account == account && current.revision == revision;
    }
};

static HRESULT WinError() { return HRESULT_FROM_WIN32(GetLastError()); }
static bool Continue(IQueryContinueWithStatus* query, HANDLE cancel, ULONGLONG deadline)
{
    return WaitForSingleObject(cancel, 0) == WAIT_TIMEOUT && GetTickCount64() < deadline &&
        (!query || query->QueryContinue() == S_OK);
}

// All I/O is cancellable. The event and OVERLAPPED remain alive until kernel completion.
static HRESULT Transfer(HANDLE pipe, void* data, DWORD size, bool write,
    IQueryContinueWithStatus* query, HANDLE cancel, ULONGLONG deadline)
{
    DWORD position = 0;
    Handle event(CreateEventW(nullptr, TRUE, FALSE, nullptr));
    if (!event.value) return WinError();
    while (position < size)
    {
        if (!Continue(query, cancel, deadline)) return HRESULT_FROM_WIN32(ERROR_CANCELLED);
        ResetEvent(event.value);
        OVERLAPPED operation{};
        operation.hEvent = event.value;
        DWORD transferred = 0;
        BOOL done = write ? WriteFile(pipe, static_cast<BYTE*>(data) + position, size - position, &transferred, &operation)
            : ReadFile(pipe, static_cast<BYTE*>(data) + position, size - position, &transferred, &operation);
        if (!done)
        {
            if (GetLastError() != ERROR_IO_PENDING) return WinError();
            while (WaitForSingleObject(event.value, 100) == WAIT_TIMEOUT)
            {
                if (!Continue(query, cancel, deadline))
                {
                    CancelIoEx(pipe, &operation);
                    GetOverlappedResult(pipe, &operation, &transferred, TRUE);
                    return HRESULT_FROM_WIN32(ERROR_CANCELLED);
                }
            }
            if (!GetOverlappedResult(pipe, &operation, &transferred, FALSE)) return WinError();
        }
        if (transferred == 0) return HRESULT_FROM_WIN32(ERROR_BROKEN_PIPE);
        position += transferred;
    }
    return S_OK;
}

static HRESULT VerifyService(HANDLE pipe)
{
    ULONG pid = 0;
    if (!GetNamedPipeServerProcessId(pipe, &pid)) return WinError();
    SC_HANDLE manager = OpenSCManagerW(nullptr, nullptr, SC_MANAGER_CONNECT);
    if (!manager) return WinError();
    SC_HANDLE service = OpenServiceW(manager, L"WatchUnlock", SERVICE_QUERY_STATUS);
    HRESULT result = service ? S_OK : WinError();
    if (service)
    {
        SERVICE_STATUS_PROCESS status{};
        DWORD size = 0;
        if (!QueryServiceStatusEx(service, SC_STATUS_PROCESS_INFO, reinterpret_cast<BYTE*>(&status), sizeof(status), &size)) result = WinError();
        else if (status.dwCurrentState != SERVICE_RUNNING || !pid || status.dwProcessId != pid) result = E_ACCESSDENIED;
        CloseServiceHandle(service);
    }
    CloseServiceHandle(manager);
    return result;
}

#pragma pack(push, 1)
struct Request { DWORD magic, scenario; GUID id; DWORD sidBytes; };
struct Reply { DWORD magic, result; GUID id; DWORD domainBytes, userBytes, passwordBytes; };
#pragma pack(pop)
static_assert(sizeof(Request) == 28 && sizeof(Reply) == 36);

static HRESULT Approve(const Config& config, CREDENTIAL_PROVIDER_USAGE_SCENARIO scenario,
    IQueryContinueWithStatus* query, HANDLE cancel, Secret& password, std::wstring& domain)
{
    constexpr auto name = L"\\\\.\\pipe\\WatchUnlock.Credential.v1";
    const auto deadline = GetTickCount64() + 90000;
    const auto connectDeadline = GetTickCount64() + 5000;
    HANDLE connected = INVALID_HANDLE_VALUE;
    while (Continue(query, cancel, connectDeadline))
    {
        connected = CreateFileW(name, GENERIC_READ | GENERIC_WRITE, 0, nullptr, OPEN_EXISTING,
            FILE_FLAG_OVERLAPPED | SECURITY_SQOS_PRESENT | SECURITY_IMPERSONATION, nullptr);
        if (connected != INVALID_HANDLE_VALUE) break;
        const DWORD error = GetLastError();
        if (error != ERROR_PIPE_BUSY && error != ERROR_FILE_NOT_FOUND) return HRESULT_FROM_WIN32(error);
        WaitForSingleObject(cancel, 100);
    }
    if (connected == INVALID_HANDLE_VALUE) return HRESULT_FROM_WIN32(ERROR_SERVICE_NOT_ACTIVE);
    Handle pipe(connected);
    HRESULT result = VerifyService(pipe.value);
    if (FAILED(result)) return result;
    Request request{Magic, static_cast<DWORD>(scenario), {}, static_cast<DWORD>(config.sid.size() * sizeof(wchar_t))};
    result = CoCreateGuid(&request.id);
    if (FAILED(result)) return result;
    result = Transfer(pipe.value, &request, sizeof(request), true, query, cancel, deadline);
    if (SUCCEEDED(result)) result = Transfer(pipe.value, const_cast<wchar_t*>(config.sid.data()), request.sidBytes, true, query, cancel, deadline);
    Reply reply{};
    if (SUCCEEDED(result)) result = Transfer(pipe.value, &reply, sizeof(reply), false, query, cancel, deadline);
    if (FAILED(result)) return result;
    if (reply.magic != Magic || reply.id != request.id || reply.result > 2 || reply.domainBytes > 256 ||
        reply.userBytes > 512 || reply.passwordBytes > 512 || (reply.domainBytes | reply.userBytes | reply.passwordBytes) % 2 != 0)
        return HRESULT_FROM_WIN32(ERROR_INVALID_DATA);
    if (reply.result != 0) return reply.result == 1 ? S_FALSE : E_FAIL;
    if (!reply.domainBytes || !reply.userBytes || !reply.passwordBytes) return HRESULT_FROM_WIN32(ERROR_INVALID_DATA);
    Secret payload;
    payload.bytes.resize(reply.domainBytes + reply.userBytes + reply.passwordBytes);
    result = Transfer(pipe.value, payload.bytes.data(), static_cast<DWORD>(payload.bytes.size()), false, query, cancel, deadline);
    if (FAILED(result)) return result;
    domain.assign(reinterpret_cast<wchar_t*>(payload.bytes.data()), reply.domainBytes / 2);
    std::wstring user(reinterpret_cast<wchar_t*>(payload.bytes.data() + reply.domainBytes), reply.userBytes / 2);
    wchar_t computer[MAX_COMPUTERNAME_LENGTH + 1]{};
    DWORD length = ARRAYSIZE(computer);
    if (!GetComputerNameW(computer, &length) || _wcsicmp(computer, domain.c_str()) != 0 ||
        user != config.account || domain.find(L'\0') != std::wstring::npos || user.find(L'\0') != std::wstring::npos || !config.Current()) return E_ACCESSDENIED;
    password.bytes.resize(reply.passwordBytes + 2);
    CopyMemory(password.bytes.data(), payload.bytes.data() + reply.domainBytes + reply.userBytes, reply.passwordBytes);
    for (size_t i = 0; i < reply.passwordBytes; i += 2)
        if (password.bytes[i] == 0 && password.bytes[i + 1] == 0) { password.Clear(); return E_ACCESSDENIED; }
    return S_OK;
}

// Pack the SDK's local-account interactive logon structure with relative string offsets.
static HRESULT Serialize(const Config& config, CREDENTIAL_PROVIDER_USAGE_SCENARIO scenario,
    const std::wstring& domain, Secret& password, CREDENTIAL_PROVIDER_CREDENTIAL_SERIALIZATION* output)
{
    DWORD protectedChars = 0;
    auto plain = reinterpret_cast<wchar_t*>(password.bytes.data());
    if (CredProtectW(FALSE, plain, static_cast<DWORD>(password.bytes.size() / 2), nullptr, &protectedChars, nullptr) ||
        GetLastError() != ERROR_INSUFFICIENT_BUFFER || protectedChars == 0 || protectedChars > 16384) return E_FAIL;
    Secret protectedPassword;
    protectedPassword.bytes.resize(protectedChars * sizeof(wchar_t));
    auto protectedText = reinterpret_cast<wchar_t*>(protectedPassword.bytes.data());
    if (!CredProtectW(FALSE, plain, static_cast<DWORD>(password.bytes.size() / 2), protectedText, &protectedChars, nullptr)) return WinError();
    HANDLE lsa = nullptr;
    NTSTATUS status = LsaConnectUntrusted(&lsa);
    if (status != 0) return HRESULT_FROM_WIN32(LsaNtStatusToWinError(status));
    char packageName[] = "Negotiate";
    LSA_STRING package{static_cast<USHORT>(sizeof(packageName) - 1), static_cast<USHORT>(sizeof(packageName)), packageName};
    ULONG packageId = 0;
    status = LsaLookupAuthenticationPackage(lsa, &package, &packageId);
    LsaDeregisterLogonProcess(lsa);
    if (status != 0) return HRESULT_FROM_WIN32(LsaNtStatusToWinError(status));
    const auto domainBytes = domain.size() * 2, userBytes = config.account.size() * 2, passwordBytes = wcslen(protectedText) * 2;
    const auto size = sizeof(KERB_INTERACTIVE_UNLOCK_LOGON) + domainBytes + userBytes + passwordBytes;
    auto buffer = static_cast<BYTE*>(CoTaskMemAlloc(size));
    if (!buffer) return E_OUTOFMEMORY;
    ZeroMemory(buffer, size);
    auto logon = reinterpret_cast<KERB_INTERACTIVE_UNLOCK_LOGON*>(buffer);
    logon->Logon.MessageType = scenario == CPUS_UNLOCK_WORKSTATION ? KerbWorkstationUnlockLogon : KerbInteractiveLogon;
    size_t position = sizeof(*logon);
    auto pack = [&](UNICODE_STRING& target, const wchar_t* text, size_t bytes)
    {
        target.Length = target.MaximumLength = static_cast<USHORT>(bytes);
        target.Buffer = reinterpret_cast<PWSTR>(position);
        CopyMemory(buffer + position, text, bytes);
        position += bytes;
    };
    pack(logon->Logon.LogonDomainName, domain.data(), domainBytes);
    pack(logon->Logon.UserName, config.account.data(), userBytes);
    pack(logon->Logon.Password, protectedText, passwordBytes);
    output->ulAuthenticationPackage = packageId;
    output->clsidCredentialProvider = ProviderId;
    output->cbSerialization = static_cast<ULONG>(size);
    output->rgbSerialization = buffer;
    return S_OK;
}

class Credential final : public ICredentialProviderCredential2
{
    std::atomic<ULONG> refs{1};
    Config config;
    CREDENTIAL_PROVIDER_USAGE_SCENARIO scenario;
    std::mutex state;
    ICredentialProviderEvents* notifications = nullptr;
    UINT_PTR notificationContext = 0;
    struct Attempt
    {
        Handle cancel{CreateEventW(nullptr, TRUE, FALSE, nullptr)};
        Secret password;
        std::wstring domain;
        ULONGLONG approvedAt = 0;
        HRESULT outcome = E_PENDING;
        bool running = true;
    };
    std::shared_ptr<Attempt> active;
    struct Work
    {
        Credential* owner;
        std::shared_ptr<Attempt> attempt;
        IStream* stream;
        UINT_PTR context;
        HMODULE module;
    };
    void Clear()
    {
        if (active) { SetEvent(active->cancel.value); active->password.Clear(); active.reset(); }
    }
    ~Credential() { Clear(); if (notifications) notifications->Release(); --objects; }
    static void ProcessWork(Work* work)
    {
        auto owner = work->owner;
        HRESULT initialized = CoInitializeEx(nullptr, COINIT_MULTITHREADED);
        ICredentialProviderEvents* events = nullptr;
        HRESULT result = initialized;
        if (SUCCEEDED(initialized)) result = CoGetInterfaceAndReleaseStream(work->stream, IID_ICredentialProviderEvents, reinterpret_cast<void**>(&events));
        else work->stream->Release();
        Secret received;
        std::wstring domain;
        try { if (SUCCEEDED(result)) result = Approve(owner->config, owner->scenario, nullptr, work->attempt->cancel.value, received, domain); }
        catch (const std::bad_alloc&) { result = E_OUTOFMEMORY; }
        catch (...) { result = E_FAIL; }
        bool notify = false;
        {
            std::lock_guard<std::mutex> lock(owner->state);
            if (owner->active == work->attempt && WaitForSingleObject(work->attempt->cancel.value, 0) == WAIT_TIMEOUT)
            {
                work->attempt->outcome = result;
                work->attempt->running = false;
                if (result == S_OK)
                {
                    work->attempt->password.bytes = std::move(received.bytes);
                    work->attempt->domain = std::move(domain);
                    work->attempt->approvedAt = GetTickCount64();
                }
                notify = true;
            }
        }
        received.Clear();
        if (events)
        {
            if (notify && WaitForSingleObject(work->attempt->cancel.value, 0) == WAIT_TIMEOUT) events->CredentialsChanged(work->context);
            events->Release();
        }
        if (SUCCEEDED(initialized)) CoUninitialize();
    }
    static DWORD WINAPI Run(void* parameter)
    {
        auto work = static_cast<Work*>(parameter);
        auto module = work->module;
        auto owner = work->owner;
        ProcessWork(work);
        delete work;
        owner->Release();
        // The DLL remains loaded through the final instructions of this worker thread.
        FreeLibraryAndExitThread(module, 0);
    }
    HRESULT Start()
    {
        if (!notifications) return E_UNEXPECTED;
        auto attempt = std::make_shared<Attempt>();
        if (!attempt->cancel.value) return E_OUTOFMEMORY;
        IStream* stream = nullptr;
        HRESULT result = CoMarshalInterThreadInterfaceInStream(IID_ICredentialProviderEvents, notifications, &stream);
        if (FAILED(result)) return result;
        HMODULE module = nullptr;
        if (!GetModuleHandleExW(GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS, reinterpret_cast<LPCWSTR>(&Run), &module))
        { stream->Release(); return WinError(); }
        auto work = new (std::nothrow) Work{this, attempt, stream, notificationContext, module};
        if (!work) { stream->Release(); FreeLibrary(module); return E_OUTOFMEMORY; }
        Clear(); active = attempt;
        AddRef();
        Handle thread(CreateThread(nullptr, 0, &Run, work, 0, nullptr));
        if (!thread.value)
        {
            result = WinError();
            Clear(); Release(); stream->Release(); delete work; FreeLibrary(module);
            return result;
        }
        return S_OK;
    }
public:
    Credential(Config value, CREDENTIAL_PROVIDER_USAGE_SCENARIO usage) : config(std::move(value)), scenario(usage) { ++objects; }
    IFACEMETHODIMP QueryInterface(REFIID iid, void** output) override
    {
        if (!output) return E_POINTER;
        *output = nullptr;
        if (iid == IID_IUnknown || iid == IID_ICredentialProviderCredential || iid == IID_ICredentialProviderCredential2)
            *output = static_cast<ICredentialProviderCredential2*>(this);
        else return E_NOINTERFACE;
        AddRef(); return S_OK;
    }
    IFACEMETHODIMP_(ULONG) AddRef() override { return ++refs; }
    IFACEMETHODIMP_(ULONG) Release() override { auto count = --refs; if (!count) delete this; return count; }
    IFACEMETHODIMP Advise(ICredentialProviderCredentialEvents*) override { return S_OK; }
    IFACEMETHODIMP UnAdvise() override { return Disconnect(); }
    IFACEMETHODIMP SetSelected(BOOL* automatic) override { if (!automatic) return E_POINTER; *automatic = Ready(); return S_OK; }
    IFACEMETHODIMP SetDeselected() override { return Disconnect(); }
    IFACEMETHODIMP GetUserSid(PWSTR* sid) override { return SHStrDupW(config.sid.c_str(), sid); }
    IFACEMETHODIMP GetFieldState(DWORD field, CREDENTIAL_PROVIDER_FIELD_STATE* visible, CREDENTIAL_PROVIDER_FIELD_INTERACTIVE_STATE* interactive) override
    {
        if (!visible || !interactive) return E_POINTER;
        if (field >= 4) return E_INVALIDARG;
        *visible = field < 2 ? CPFS_DISPLAY_IN_BOTH : CPFS_DISPLAY_IN_SELECTED_TILE;
        *interactive = CPFIS_NONE; return S_OK;
    }
    IFACEMETHODIMP GetStringValue(DWORD field, PWSTR* text) override
    {
        if (field == 1) return SHStrDupW(L"使用手表解锁", text);
        if (field == 2)
        {
            std::lock_guard<std::mutex> lock(state);
            if (active && active->running) return SHStrDupW(L"正在请求确认；可等待回复，或切换登录方式取消。", text);
            if (active && active->outcome != S_OK) return SHStrDupW(L"本次未获确认或手机不可用。点击箭头重试，或使用密码/PIN。", text);
            return SHStrDupW(L"点击箭头，再从手机或手表回复已设置的文字。", text);
        }
        if (field == 3) return SHStrDupW(L"请求解锁", text);
        return E_INVALIDARG;
    }
    IFACEMETHODIMP GetBitmapValue(DWORD field, HBITMAP* bitmap) override
    {
        if (!bitmap) return E_POINTER;
        *bitmap = nullptr;
        if (field != 0) return E_INVALIDARG;
        BITMAPINFO info{};
        info.bmiHeader.biSize = sizeof(BITMAPINFOHEADER);
        info.bmiHeader.biWidth = 64; info.bmiHeader.biHeight = -64;
        info.bmiHeader.biPlanes = 1; info.bmiHeader.biBitCount = 32;
        void* pixels = nullptr;
        *bitmap = CreateDIBSection(nullptr, &info, DIB_RGB_COLORS, &pixels, nullptr, 0);
        if (!*bitmap) return WinError();
        auto colors = static_cast<DWORD*>(pixels);
        for (int y = 0; y < 64; ++y) for (int x = 0; x < 64; ++x)
        {
            bool face = x >= 18 && x < 46 && y >= 19 && y < 45;
            bool strap = x >= 26 && x < 38 && y >= 7 && y < 57;
            colors[y * 64 + x] = (face || strap) ? 0x00ffffff : 0x00346ecc;
        }
        return S_OK;
    }
    IFACEMETHODIMP GetSubmitButtonValue(DWORD field, DWORD* adjacent) override
    { if (!adjacent) return E_POINTER; if (field != 3) return E_INVALIDARG; *adjacent = 2; return S_OK; }
    IFACEMETHODIMP GetCheckboxValue(DWORD, BOOL*, PWSTR*) override { return E_NOTIMPL; }
    IFACEMETHODIMP GetComboBoxValueCount(DWORD, DWORD*, DWORD*) override { return E_NOTIMPL; }
    IFACEMETHODIMP GetComboBoxValueAt(DWORD, DWORD, PWSTR*) override { return E_NOTIMPL; }
    IFACEMETHODIMP SetStringValue(DWORD, PCWSTR) override { return E_NOTIMPL; }
    IFACEMETHODIMP SetCheckboxValue(DWORD, BOOL) override { return E_NOTIMPL; }
    IFACEMETHODIMP SetComboBoxSelectedValue(DWORD, DWORD) override { return E_NOTIMPL; }
    IFACEMETHODIMP CommandLinkClicked(DWORD) override { return E_NOTIMPL; }

    void SetNotifications(ICredentialProviderEvents* events, UINT_PTR context)
    {
        std::lock_guard<std::mutex> lock(state);
        if (notifications) notifications->Release();
        notifications = events; notificationContext = context;
        if (notifications) notifications->AddRef();
    }
    bool Ready()
    {
        std::lock_guard<std::mutex> lock(state);
        return active && !active->running && active->outcome == S_OK && active->approvedAt &&
            GetTickCount64() - active->approvedAt <= 5000 && config.Current();
    }
    HRESULT Disconnect() { std::lock_guard<std::mutex> lock(state); Clear(); return S_OK; }
    IFACEMETHODIMP GetSerialization(CREDENTIAL_PROVIDER_GET_SERIALIZATION_RESPONSE* response,
        CREDENTIAL_PROVIDER_CREDENTIAL_SERIALIZATION* output, PWSTR* text, CREDENTIAL_PROVIDER_STATUS_ICON* icon) override
    {
        if (!response || !output || !text || !icon) return E_POINTER;
        *response = CPGSR_NO_CREDENTIAL_NOT_FINISHED; *output = {}; *text = nullptr; *icon = CPSI_ERROR;
        std::lock_guard<std::mutex> lock(state);
        if (!config.Current())
        {
            Clear();
            return SHStrDupW(L"配置已改变，请使用密码/PIN，登录后检查设置。", text);
        }
        if (!active || (!active->running && active->outcome != S_OK))
        {
            HRESULT result = E_FAIL;
            try { result = Start(); } catch (...) { result = E_OUTOFMEMORY; }
            *icon = SUCCEEDED(result) ? CPSI_NONE : CPSI_ERROR;
            return SHStrDupW(SUCCEEDED(result) ? L"正在请求手机确认，收到通知后请在 10 秒内回复。"
                : L"无法发起请求，请使用密码/PIN。", text);
        }
        if (active->running) { *icon = CPSI_NONE; return S_OK; }
        if (!active->approvedAt || active->password.bytes.empty() || GetTickCount64() - active->approvedAt > 5000)
        { Clear(); return SHStrDupW(L"确认已过期，请再次请求或使用密码/PIN。", text); }
        HRESULT result = E_FAIL;
        try { result = Serialize(config, scenario, active->domain, active->password, output); }
        catch (...) { result = E_FAIL; }
        Clear(); // This approval can serialize exactly once.
        if (FAILED(result)) return SHStrDupW(L"凭据提交失败，请使用密码/PIN。", text);
        *response = CPGSR_RETURN_CREDENTIAL_FINISHED; *icon = CPSI_NONE; return S_OK;
    }
    IFACEMETHODIMP ReportResult(NTSTATUS status, NTSTATUS, PWSTR* text, CREDENTIAL_PROVIDER_STATUS_ICON* icon) override
    {
        if (!text || !icon) return E_POINTER;
        *text = nullptr; *icon = CPSI_NONE;
        Disconnect();
        if (status != 0) { *icon = CPSI_ERROR; return SHStrDupW(L"Windows 未接受保存的凭据。请使用密码/PIN，登录后重新保存账户密码。", text); }
        return S_OK;
    }
};

class Provider final : public ICredentialProvider, public ICredentialProviderSetUserArray
{
    std::atomic<ULONG> refs{1};
    CREDENTIAL_PROVIDER_USAGE_SCENARIO scenario = CPUS_INVALID;
    ICredentialProviderUserArray* users = nullptr;
    Credential* credential = nullptr;
    ICredentialProviderEvents* events = nullptr;
    UINT_PTR context = 0;
    std::wstring revision;
    void Clear() { if (credential) { credential->Disconnect(); credential->SetNotifications(nullptr, 0); credential->Release(); credential = nullptr; } }
    ~Provider() { Clear(); if (users) users->Release(); if (events) events->Release(); --objects; }
public:
    Provider() { ++objects; }
    IFACEMETHODIMP QueryInterface(REFIID iid, void** output) override
    {
        if (!output) return E_POINTER;
        *output = nullptr;
        if (iid == IID_IUnknown || iid == IID_ICredentialProvider) *output = static_cast<ICredentialProvider*>(this);
        else if (iid == IID_ICredentialProviderSetUserArray) *output = static_cast<ICredentialProviderSetUserArray*>(this);
        else return E_NOINTERFACE;
        AddRef(); return S_OK;
    }
    IFACEMETHODIMP_(ULONG) AddRef() override { return ++refs; }
    IFACEMETHODIMP_(ULONG) Release() override { auto count = --refs; if (!count) delete this; return count; }
    IFACEMETHODIMP SetUsageScenario(CREDENTIAL_PROVIDER_USAGE_SCENARIO usage, DWORD) override
    { Clear(); scenario = usage; return usage == CPUS_LOGON || usage == CPUS_UNLOCK_WORKSTATION ? S_OK : E_NOTIMPL; }
    IFACEMETHODIMP SetSerialization(const CREDENTIAL_PROVIDER_CREDENTIAL_SERIALIZATION*) override { return E_NOTIMPL; }
    IFACEMETHODIMP Advise(ICredentialProviderEvents* value, UINT_PTR adviseContext) override
    {
        if (events) events->Release();
        events = value; context = adviseContext;
        if (events) events->AddRef();
        if (credential) credential->SetNotifications(events, context);
        return S_OK;
    }
    IFACEMETHODIMP UnAdvise() override { Clear(); if (events) { events->Release(); events = nullptr; } return S_OK; }
    IFACEMETHODIMP SetUserArray(ICredentialProviderUserArray* value) override
    { Clear(); if (users) users->Release(); users = value; if (users) users->AddRef(); return S_OK; }
    IFACEMETHODIMP GetFieldDescriptorCount(DWORD* count) override { if (!count) return E_POINTER; *count = 4; return S_OK; }
    IFACEMETHODIMP GetFieldDescriptorAt(DWORD field, CREDENTIAL_PROVIDER_FIELD_DESCRIPTOR** output) override
    {
        if (!output) return E_POINTER;
        *output = nullptr;
        if (field >= 4) return E_INVALIDARG;
        auto descriptor = static_cast<CREDENTIAL_PROVIDER_FIELD_DESCRIPTOR*>(CoTaskMemAlloc(sizeof(CREDENTIAL_PROVIDER_FIELD_DESCRIPTOR)));
        if (!descriptor) return E_OUTOFMEMORY;
        *descriptor = {};
        descriptor->dwFieldID = field;
        const CREDENTIAL_PROVIDER_FIELD_TYPE types[] = {CPFT_TILE_IMAGE, CPFT_LARGE_TEXT, CPFT_SMALL_TEXT, CPFT_SUBMIT_BUTTON};
        descriptor->cpft = types[field];
        HRESULT result = SHStrDupW(field == 3 ? L"请求解锁" : L"WatchUnlock", &descriptor->pszLabel);
        if (FAILED(result)) { CoTaskMemFree(descriptor); return result; }
        *output = descriptor; return S_OK;
    }
    IFACEMETHODIMP GetCredentialCount(DWORD* count, DWORD* selected, BOOL* automatic) override
    {
        if (!count || !selected || !automatic) return E_POINTER;
        *count = 0; *selected = CREDENTIAL_PROVIDER_NO_DEFAULT; *automatic = FALSE;
        Config config;
        if (!users || (scenario != CPUS_LOGON && scenario != CPUS_UNLOCK_WORKSTATION) || !Config::Read(config)) { Clear(); return S_OK; }
        DWORD size = 0;
        bool matched = false;
        if (FAILED(users->GetCount(&size))) return S_OK;
        for (DWORD i = 0; i < size && !matched; ++i)
        {
            ICredentialProviderUser* user = nullptr;
            if (SUCCEEDED(users->GetAt(i, &user)))
            {
                PWSTR sid = nullptr;
                if (SUCCEEDED(user->GetSid(&sid))) { matched = config.sid == sid; CoTaskMemFree(sid); }
                user->Release();
            }
        }
        if (!matched) { Clear(); return S_OK; }
        if (!credential || revision != config.revision)
        {
            Clear(); revision = config.revision;
            credential = new (std::nothrow) Credential(std::move(config), scenario);
            if (!credential) return E_OUTOFMEMORY;
            credential->SetNotifications(events, context);
        }
        *count = 1;
        // Auto-submit is allowed only for the fresh result of an explicit click on this tile.
        if (credential->Ready()) { *selected = 0; *automatic = TRUE; }
        return S_OK;
    }
    IFACEMETHODIMP GetCredentialAt(DWORD index, ICredentialProviderCredential** output) override
    {
        if (!output) return E_POINTER;
        *output = nullptr;
        if (index != 0 || !credential) return E_INVALIDARG;
        return credential->QueryInterface(IID_ICredentialProviderCredential, reinterpret_cast<void**>(output));
    }
};

class Factory final : public IClassFactory
{
    std::atomic<ULONG> refs{1};
    ~Factory() { --objects; }
public:
    Factory() { ++objects; }
    IFACEMETHODIMP QueryInterface(REFIID iid, void** output) override
    {
        if (!output) return E_POINTER;
        *output = nullptr;
        if (iid != IID_IUnknown && iid != IID_IClassFactory) return E_NOINTERFACE;
        *output = static_cast<IClassFactory*>(this); AddRef(); return S_OK;
    }
    IFACEMETHODIMP_(ULONG) AddRef() override { return ++refs; }
    IFACEMETHODIMP_(ULONG) Release() override { auto count = --refs; if (!count) delete this; return count; }
    IFACEMETHODIMP CreateInstance(IUnknown* outer, REFIID iid, void** output) override
    {
        if (!output) return E_POINTER;
        *output = nullptr;
        if (outer) return CLASS_E_NOAGGREGATION;
        auto provider = new (std::nothrow) Provider();
        if (!provider) return E_OUTOFMEMORY;
        HRESULT result = provider->QueryInterface(iid, output);
        provider->Release(); return result;
    }
    IFACEMETHODIMP LockServer(BOOL lock) override { if (lock) ++objects; else --objects; return S_OK; }
};

extern "C" HRESULT __stdcall DllGetClassObject(REFCLSID clsid, REFIID iid, void** output)
{
    if (!output) return E_POINTER;
    *output = nullptr;
    if (clsid != ProviderId) return CLASS_E_CLASSNOTAVAILABLE;
    auto factory = new (std::nothrow) Factory();
    if (!factory) return E_OUTOFMEMORY;
    HRESULT result = factory->QueryInterface(iid, output);
    factory->Release(); return result;
}
extern "C" HRESULT __stdcall DllCanUnloadNow() { return objects == 0 ? S_OK : S_FALSE; }
