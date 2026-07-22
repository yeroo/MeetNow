#include "settings.h"
#include "json.h"
#include "str.h"
#include <windows.h>
#include <shlobj.h>

namespace mn {

std::wstring settingsDir() {
    PWSTR localAppData = nullptr;
    std::wstring dir;
    if (SUCCEEDED(SHGetKnownFolderPath(FOLDERID_LocalAppData, 0, nullptr, &localAppData))) {
        dir = localAppData;
        dir += L"\\MeetNow";
    }
    if (localAppData) CoTaskMemFree(localAppData);
    return dir;
}

std::string readFileBytes(const std::wstring& path) {
    HANDLE h = CreateFileW(path.c_str(), GENERIC_READ, FILE_SHARE_READ | FILE_SHARE_WRITE,
                           nullptr, OPEN_EXISTING, FILE_ATTRIBUTE_NORMAL, nullptr);
    if (h == INVALID_HANDLE_VALUE) return {};
    LARGE_INTEGER size{};
    GetFileSizeEx(h, &size);
    std::string data;
    if (size.QuadPart > 0 && size.QuadPart < 64 * 1024 * 1024) {
        data.resize((size_t)size.QuadPart);
        DWORD read = 0;
        if (!ReadFile(h, data.data(), (DWORD)data.size(), &read, nullptr)) data.clear();
        else data.resize(read);
    }
    CloseHandle(h);
    return data;
}

bool writeFileAtomic(const std::wstring& path, const void* data, size_t size) {
    const std::wstring dir = settingsDir();
    if (dir.empty()) return false;
    SHCreateDirectoryExW(nullptr, dir.c_str(), nullptr);

    const std::wstring tmp = path + L".tmp";
    HANDLE h = CreateFileW(tmp.c_str(), GENERIC_WRITE, 0, nullptr, CREATE_ALWAYS,
                           FILE_ATTRIBUTE_NORMAL, nullptr);
    if (h == INVALID_HANDLE_VALUE) return false;
    DWORD written = 0;
    const BOOL ok = WriteFile(h, data, (DWORD)size, &written, nullptr);
    CloseHandle(h);
    if (!ok || written != size) { DeleteFileW(tmp.c_str()); return false; }
    return MoveFileExW(tmp.c_str(), path.c_str(), MOVEFILE_REPLACE_EXISTING) != 0;
}

Settings loadSettings() {
    Settings s;
    const std::string bytes = readFileBytes(settingsDir() + L"\\settings.json");
    if (bytes.empty()) return s;
    const auto root = json::parse(bytes);
    if (!root || root->type != json::Type::Object) return s;
    // Optional overrides; absent in the stock C# settings.json, so defaults
    // above are what actually runs.
    if (const auto v = root->getString(L"GraphAuthority"); !v.empty()) s.authority = v;
    if (const auto v = root->getString(L"GraphClientId"); !v.empty()) s.clientId = v;
    if (const auto v = root->getString(L"GraphScope"); !v.empty()) s.scope = v;
    return s;
}

} // namespace mn
