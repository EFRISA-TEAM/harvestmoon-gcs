#ifndef UNICODE
#define UNICODE
#endif
#ifndef _UNICODE
#define _UNICODE
#endif
#include <windows.h>
#include <winhttp.h>
#include <bcrypt.h>
#include <shlobj.h>
#include <stdio.h>
#include <wchar.h>
#include "payload.h"

#define WM_FINISHED (WM_APP + 1)
static HWND window;
static volatile LONG cancelled;
static BOOL checkRuntime;
static wchar_t exePath[MAX_PATH], tempPath[MAX_PATH], versionDir[MAX_PATH];
static const wchar_t *failure;

static BOOL valid_payload(const wchar_t *path) {
    HANDLE file = CreateFileW(path, GENERIC_READ, FILE_SHARE_READ, NULL, OPEN_EXISTING, 0, NULL);
    if (file == INVALID_HANDLE_VALUE) return FALSE;
    BCRYPT_ALG_HANDLE alg = NULL;
    BCRYPT_HASH_HANDLE hash = NULL;
    BYTE digest[32], buffer[65536];
    DWORD read;
    BOOL ok = FALSE;
    if (BCryptOpenAlgorithmProvider(&alg, BCRYPT_SHA256_ALGORITHM, NULL, 0) < 0) goto end;
    if (BCryptCreateHash(alg, &hash, NULL, 0, NULL, 0, 0) < 0) goto end;
    for (;;) {
        if (!ReadFile(file, buffer, sizeof(buffer), &read, NULL)) goto end;
        if (!read) break;
        if (InterlockedCompareExchange(&cancelled, 0, 0)) goto end;
        if (BCryptHashData(hash, buffer, read, 0) < 0) goto end;
    }
    if (BCryptFinishHash(hash, digest, sizeof(digest), 0) < 0) goto end;
    wchar_t hex[65];
    for (int i = 0; i < 32; ++i) swprintf(hex + i * 2, 3, L"%02x", digest[i]);
    ok = wcscmp(hex, PAYLOAD_SHA256) == 0;
end:
    if (hash) BCryptDestroyHash(hash);
    if (alg) BCryptCloseAlgorithmProvider(alg, 0);
    CloseHandle(file);
    return ok;
}

static BOOL download(void) {
    URL_COMPONENTS parts = { .dwStructSize = sizeof(parts) };
    wchar_t host[256], path[2048];
    parts.lpszHostName = host; parts.dwHostNameLength = 256;
    parts.lpszUrlPath = path; parts.dwUrlPathLength = 2048;
    HINTERNET session = NULL, connection = NULL, request = NULL;
    HANDLE file = INVALID_HANDLE_VALUE;
    BOOL ok = FALSE;
    failure = L"Unduhan gagal. Periksa koneksi internet, lalu buka launcher kembali.";
    if (!WinHttpCrackUrl(PAYLOAD_URL, 0, 0, &parts) || parts.nScheme != INTERNET_SCHEME_HTTPS) goto end;
    session = WinHttpOpen(L"MoonHarvest-Launcher/1.0", WINHTTP_ACCESS_TYPE_AUTOMATIC_PROXY,
                         WINHTTP_NO_PROXY_NAME, WINHTTP_NO_PROXY_BYPASS, 0);
    if (!session) goto end;
    WinHttpSetTimeouts(session, 15000, 15000, 30000, 30000);
    connection = WinHttpConnect(session, host, parts.nPort, 0);
    if (!connection) goto end;
    request = WinHttpOpenRequest(connection, L"GET", path, NULL, WINHTTP_NO_REFERER,
                                 WINHTTP_DEFAULT_ACCEPT_TYPES, WINHTTP_FLAG_SECURE);
    if (!request) goto end;
    if (!WinHttpSendRequest(request, WINHTTP_NO_ADDITIONAL_HEADERS, 0, NULL, 0, 0, 0)
        || !WinHttpReceiveResponse(request, NULL)) goto end;
    DWORD status = 0, length = sizeof(status);
    if (!WinHttpQueryHeaders(request, WINHTTP_QUERY_STATUS_CODE | WINHTTP_QUERY_FLAG_NUMBER,
                             NULL, &status, &length, NULL) || status != 200) {
        failure = L"Paket aplikasi belum tersedia atau server menolak unduhan. Coba kembali setelah release tersedia.";
        goto end;
    }
    file = CreateFileW(tempPath, GENERIC_WRITE, 0, NULL, CREATE_ALWAYS, FILE_ATTRIBUTE_NORMAL, NULL);
    if (file == INVALID_HANDLE_VALUE) {
        failure = L"Tidak dapat menyimpan aplikasi. Periksa ruang kosong dan izin folder LocalAppData.";
        goto end;
    }
    BYTE buffer[65536];
    DWORD read, written;
    ULONGLONG total = 0;
    for (;;) {
        if (InterlockedCompareExchange(&cancelled, 0, 0)) goto end;
        if (!WinHttpReadData(request, buffer, sizeof(buffer), &read)) goto end;
        if (!read) break;
        if (!WriteFile(file, buffer, read, &written, NULL) || written != read) goto end;
        total += read;
        wchar_t title[128];
        swprintf(title, 128, L"MoonHarvest - mengunduh %.1f MB", (double)total / 1000000.0);
        SetWindowTextW(window, title);
    }
    ok = FlushFileBuffers(file);
end:
    if (file != INVALID_HANDLE_VALUE) CloseHandle(file);
    if (request) WinHttpCloseHandle(request);
    if (connection) WinHttpCloseHandle(connection);
    if (session) WinHttpCloseHandle(session);
    if (!ok) DeleteFileW(tempPath);
    return ok;
}

static DWORD WINAPI run(LPVOID unused) {
    (void)unused;
    HANDLE lock = CreateMutexW(NULL, FALSE, L"Local\\MoonHarvestPortableDownload");
    if (!lock) { failure = L"Tidak dapat membuat kunci unduhan."; goto done; }
    DWORD wait = WaitForSingleObject(lock, 0);
    if (wait != WAIT_OBJECT_0 && wait != WAIT_ABANDONED) {
        failure = L"Launcher MoonHarvest lain sedang berjalan. Tunggu hingga selesai.";
        CloseHandle(lock); goto done;
    }
    SetWindowTextW(window, L"MoonHarvest - memeriksa aplikasi");
    if (!valid_payload(exePath)) {
        if (InterlockedCompareExchange(&cancelled, 0, 0)) goto unlock;
        if (!download()) goto unlock;
        SetWindowTextW(window, L"MoonHarvest - memverifikasi unduhan");
        if (!valid_payload(tempPath)) {
            failure = L"Verifikasi SHA-256 gagal. File tidak dijalankan. Silakan coba lagi.";
            DeleteFileW(tempPath); goto unlock;
        }
        if (!MoveFileExW(tempPath, exePath, MOVEFILE_REPLACE_EXISTING | MOVEFILE_WRITE_THROUGH)) {
            failure = L"Tidak dapat menyimpan unduhan yang telah diverifikasi."; goto unlock;
        }
    }
    if (!InterlockedCompareExchange(&cancelled, 0, 0)) {
        STARTUPINFOW si = { .cb = sizeof(si) };
        PROCESS_INFORMATION pi;
        wchar_t command[MAX_PATH + 32];
        swprintf(command, MAX_PATH + 32, L"\"%ls\"%ls", exePath, checkRuntime ? L" --check-runtime" : L"");
        failure = NULL;
        if (CreateProcessW(exePath, command, NULL, NULL, FALSE, 0, NULL, versionDir, &si, &pi)) {
            DWORD waitForExit = WaitForSingleObject(pi.hProcess, checkRuntime ? 180000 : 5000);
            DWORD exitCode = 0;
            if (waitForExit == WAIT_OBJECT_0 && GetExitCodeProcess(pi.hProcess, &exitCode) && exitCode != 0)
                failure = checkRuntime ? L"Pemeriksaan menemukan masalah. Lihat MoonHarvest-check.json di Desktop." :
                    L"MoonHarvest berhenti saat mulai. Jalankan pemeriksaan runtime untuk melihat penyebabnya.";
            CloseHandle(pi.hThread); CloseHandle(pi.hProcess);
        } else failure = L"Aplikasi gagal dimulai. Pastikan Anda memakai Windows 10/11 64-bit.";
    }
unlock:
    ReleaseMutex(lock); CloseHandle(lock);
done:
    PostMessageW(window, WM_FINISHED, 0, 0);
    return 0;
}

static LRESULT CALLBACK window_proc(HWND hwnd, UINT msg, WPARAM wp, LPARAM lp) {
    if (msg == WM_CLOSE) {
        InterlockedExchange(&cancelled, 1);
        SetWindowTextW(hwnd, L"MoonHarvest - membatalkan, mohon tunggu...");
        return 0;
    }
    if (msg == WM_FINISHED) {
        if (failure && !InterlockedCompareExchange(&cancelled, 0, 0))
            MessageBoxW(hwnd, failure, L"MoonHarvest", MB_OK | MB_ICONERROR);
        DestroyWindow(hwnd); return 0;
    }
    if (msg == WM_DESTROY) { PostQuitMessage(0); return 0; }
    return DefWindowProcW(hwnd, msg, wp, lp);
}

int WINAPI wWinMain(HINSTANCE instance, HINSTANCE previous, PWSTR args, int show) {
    (void)previous; (void)args;
    checkRuntime = wcscmp(args, L"--check-runtime") == 0;
    wchar_t local[MAX_PATH];
    if (FAILED(SHGetFolderPathW(NULL, CSIDL_LOCAL_APPDATA, NULL, 0, local))) return 1;
    if (wcslen(local) + 110 >= MAX_PATH) return 1;
    swprintf(versionDir, MAX_PATH, L"%ls\\MoonHarvest\\%ls", local, PAYLOAD_SHA256);
    if (SHCreateDirectoryExW(NULL, versionDir, NULL) != ERROR_SUCCESS && GetFileAttributesW(versionDir) == INVALID_FILE_ATTRIBUTES) {
        MessageBoxW(NULL, L"Tidak dapat membuat folder aplikasi di LocalAppData.", L"MoonHarvest", MB_ICONERROR); return 1;
    }
    swprintf(exePath, MAX_PATH, L"%ls\\MoonHarvest.exe", versionDir);
    swprintf(tempPath, MAX_PATH, L"%ls\\download.part", versionDir);
    if (wcscmp(args, L"--verify-cache") == 0) return valid_payload(exePath) ? 0 : 2;
    WNDCLASSW cls = { .lpfnWndProc = window_proc, .hInstance = instance,
        .lpszClassName = L"MoonHarvestLauncher", .hbrBackground = (HBRUSH)(COLOR_WINDOW + 1),
        .hCursor = LoadCursorW(NULL, IDC_ARROW), .hIcon = LoadIconW(instance, MAKEINTRESOURCEW(1)) };
    if (!RegisterClassW(&cls)) return 1;
    window = CreateWindowW(cls.lpszClassName, L"MoonHarvest", WS_OVERLAPPED | WS_CAPTION | WS_SYSMENU,
                            CW_USEDEFAULT, CW_USEDEFAULT, 520, 165, NULL, NULL, instance, NULL);
    if (!window) return 1;
    CreateWindowW(L"STATIC", L"Menyiapkan MoonHarvest lengkap.\nUnduhan pertama termasuk Python dan AI; sediakan 4 GB ruang kosong.\nSetelah tersimpan, aplikasi dapat dibuka tanpa unduhan ulang.",
                  WS_CHILD | WS_VISIBLE, 20, 20, 475, 80, window, NULL, instance, NULL);
    ShowWindow(window, show);
    HANDLE worker = CreateThread(NULL, 0, run, NULL, 0, NULL);
    if (!worker) { DestroyWindow(window); return 1; }
    MSG msg;
    while (GetMessageW(&msg, NULL, 0, 0) > 0) { TranslateMessage(&msg); DispatchMessageW(&msg); }
    WaitForSingleObject(worker, INFINITE); CloseHandle(worker);
    return failure ? 1 : 0;
}
