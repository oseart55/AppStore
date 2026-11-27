/* AppStoreSetup - one-file setup for App Store on Windows Mobile / CE.
 *
 * Copyright (C) 2026 the CeHttps authors. GPLv3 (see LICENSE).
 *
 * App Store is a .NET Compact Framework 3.5 app, and many devices only have
 * CF 2.0 (Windows Mobile 6.0 / 6.1). An app built for 3.5 can't even start
 * there, so this setup is native code: it runs on any of these devices.
 *
 *   1. Install ClockSync (built in), which sets the clock now, at every boot
 *      and whenever the network connects. New and reset devices usually have
 *      the wrong date, and HTTPS certificate checks fail until it's right.
 *   2. If CF 3.5 isn't installed, unpack Microsoft's CF 3.5 installer (built
 *      into this exe) and run it with wceload.
 *   3. Install App Store (also built in) to \Application\AppStore, with a
 *      Start menu shortcut and the manifest App Store uses to update itself.
 *   4. Start App Store.
 *
 * AppStoreSetup.exe           ask, then do the above
 * AppStoreSetup.exe /quiet    no questions (still reports errors)
 */
#ifndef SETUP_TEST
#include <windows.h>
#endif
#include <stdio.h>
#include <string.h>
#include "resource.h"

#define TITLE       L"App Store Setup"
#define CF_KEY      L"SOFTWARE\\Microsoft\\.NETCompactFramework"
#define CAB_NAME    L"NETCFv35.wm.armv4i.cab"
#define APP_FOLDER  L"AppStore"
#define APP_EXE     L"AppStore.exe"
#define START_MENU  L"\\Windows\\Start Menu\\Programs"
#define SHORTCUT    L"\\Windows\\Start Menu\\Programs\\App Store.lnk"
#define CS_DIR      L"\\Application\\ClockSync"           /* fixed: ClockSync installs here */
#define CS_EXE      L"\\Application\\ClockSync\\ClockSync.exe"
#define CS_SHORTCUT L"\\Windows\\Start Menu\\Programs\\Sync Clock.lnk"
#define CS_WAIT_MS  (3 * 60 * 1000)    /* its install shows a message to tap OK on */
#define CF_WAIT_MS  (15 * 60 * 1000)   /* wceload gets up to 15 minutes        */
#define MEGABYTE    (1024 * 1024)

static int g_quiet = 0;
/* This exe's module handle. Windows CE needs it for FindResource and friends:
   unlike desktop Windows, NULL there doesn't mean "this program". */
static HINSTANCE g_inst = NULL;

/* ---- small helpers ---------------------------------------------------- */

static int ask(const WCHAR* text)
{
    if (g_quiet) return 1;
    return MessageBox(NULL, text, TITLE, MB_OKCANCEL | MB_ICONQUESTION | MB_SETFOREGROUND) == IDOK;
}

static void tell(const WCHAR* text, UINT icon)
{
    if (g_quiet && icon != MB_ICONERROR) return;
    MessageBox(NULL, text, TITLE, MB_OK | icon | MB_SETFOREGROUND);
}

static int file_exists(const WCHAR* path)
{
    return GetFileAttributes(path) != 0xFFFFFFFF;
}

static int dir_exists(const WCHAR* path)
{
    DWORD a = GetFileAttributes(path);
    return a != 0xFFFFFFFF && (a & FILE_ATTRIBUTE_DIRECTORY);
}

/* True if another program has the file open (e.g. App Store is running). */
static int in_use(const WCHAR* path)
{
    HANDLE f;
    if (!file_exists(path)) return 0;
    f = CreateFile(path, GENERIC_READ | GENERIC_WRITE, 0, NULL, OPEN_EXISTING, FILE_ATTRIBUTE_NORMAL, NULL);
    if (f == INVALID_HANDLE_VALUE) return 1;
    CloseHandle(f);
    return 0;
}

static const BYTE* resource(int id, DWORD* size)
{
    HRSRC r = FindResource(g_inst, MAKEINTRESOURCE(id), RT_RCDATA);
    HGLOBAL g;
    *size = 0;
    if (r == NULL) return NULL;
    g = LoadResource(g_inst, r);
    if (g == NULL) return NULL;
    *size = SizeofResource(g_inst, r);
    return (const BYTE*)LockResource(g);
}

/* Writes data to path via path.new, then swaps it in. 1 on success. */
static int write_file(const WCHAR* path, const BYTE* data, DWORD size)
{
    WCHAR tmp[MAX_PATH];
    HANDLE f;
    DWORD done = 0, n;
    _snwprintf(tmp, MAX_PATH - 1, L"%s.new", path);
    tmp[MAX_PATH - 1] = 0;
    f = CreateFile(tmp, GENERIC_WRITE, 0, NULL, CREATE_ALWAYS, FILE_ATTRIBUTE_NORMAL, NULL);
    if (f == INVALID_HANDLE_VALUE) return 0;
    while (done < size) {
        DWORD chunk = size - done > 65536 ? 65536 : size - done;
        if (!WriteFile(f, data + done, chunk, &n, NULL) || n != chunk) {
            CloseHandle(f);
            DeleteFile(tmp);
            return 0;
        }
        done += n;
    }
    CloseHandle(f);
    if (file_exists(path) && !DeleteFile(path)) { DeleteFile(tmp); return 0; }
    return MoveFile(tmp, path) != 0;
}

static int write_text(const WCHAR* path, const char* text)
{
    return write_file(path, (const BYTE*)text, (DWORD)strlen(text));
}

/* Free space on the volume holding `path`, in bytes (0 if unknown). */
static unsigned __int64 free_space(const WCHAR* path)
{
    ULARGE_INTEGER avail, total, freeb;
    if (!GetDiskFreeSpaceEx(path, &avail, &total, &freeb)) return 0;
    return avail.QuadPart;
}

/* ---- .NET Compact Framework ------------------------------------------- */

/* The installed CF versions are value names under CF_KEY ("3.5.7283.0"). */
static int cf35_installed(WCHAR* versions, int cap)
{
    HKEY k;
    DWORD i;
    int found = 0, used = 0;
    if (versions && cap > 0) versions[0] = 0;
    if (RegOpenKeyEx(HKEY_LOCAL_MACHINE, CF_KEY, 0, 0, &k) != ERROR_SUCCESS) return 0;
    for (i = 0; ; i++) {
        WCHAR name[64];
        DWORD len = 64;
        if (RegEnumValue(k, i, name, &len, NULL, NULL, NULL, NULL) != ERROR_SUCCESS) break;
        if (name[0] < L'0' || name[0] > L'9') continue;
        if (wcsncmp(name, L"3.5.", 4) == 0) found = 1;
        if (versions && used + (int)len + 3 < cap) {
            if (used > 0) { wcscpy(versions + used, L", "); used += 2; }
            wcscpy(versions + used, name);
            used += len;
        }
    }
    RegCloseKey(k);
    return found;
}

/* Runs a program and waits for it. 1 if it ran and finished in time. */
static int run_wait(const WCHAR* exe, const WCHAR* args, DWORD timeoutMs)
{
    PROCESS_INFORMATION pi;
    DWORD r;
    memset(&pi, 0, sizeof(pi));
    if (!CreateProcess(exe, (LPWSTR)args, NULL, NULL, FALSE, 0, NULL, NULL, NULL, &pi)) return 0;
    r = WaitForSingleObject(pi.hProcess, timeoutMs);
    CloseHandle(pi.hThread);
    CloseHandle(pi.hProcess);
    return r == WAIT_OBJECT_0;
}

static int install_cf35(WCHAR* why, int cap)
{
    const BYTE* cab;
    DWORD size;
    WCHAR cabPath[MAX_PATH], args[MAX_PATH + 32];

    cab = resource(IDR_NETCF, &size);
    if (cab == NULL || size == 0) { wcsncpy(why, L"The CF 3.5 installer is missing from this setup.", cap); return 0; }

    /* The CAB, plus what it unpacks to (about 6 MB), plus room to spare. */
    if (free_space(L"\\") != 0 && free_space(L"\\") < (unsigned __int64)size + 10 * MEGABYTE) {
        _snwprintf(why, cap, L"Not enough free storage: .NET CF 3.5 needs about %d MB.", (int)(size / MEGABYTE) + 10);
        return 0;
    }

    CreateDirectory(L"\\Temp", NULL);
    _snwprintf(cabPath, MAX_PATH - 1, L"\\Temp\\%s", CAB_NAME);
    cabPath[MAX_PATH - 1] = 0;
    if (!write_file(cabPath, cab, size)) { wcsncpy(why, L"Couldn't write the CF 3.5 installer to \\Temp.", cap); return 0; }

    /* /noui: no questions; it installs to the device. wceload deletes the CAB
       when it's done. */
    _snwprintf(args, MAX_PATH + 31, L"/noui \"%s\"", cabPath);
    args[MAX_PATH + 31] = 0;
    SetCursor(LoadCursor(NULL, IDC_WAIT));
    if (!run_wait(L"\\Windows\\wceload.exe", args, CF_WAIT_MS)) {
        SetCursor(NULL);
        DeleteFile(cabPath);
        wcsncpy(why, L"The CF 3.5 installer (wceload) didn't run or didn't finish.", cap);
        return 0;
    }
    SetCursor(NULL);
    DeleteFile(cabPath);    /* in case wceload kept it */

    if (!cf35_installed(NULL, 0)) {
        wcsncpy(why, L"The CF 3.5 installer finished, but CF 3.5 isn't registered yet.", cap);
        return 0;
    }
    return 1;
}

/* ---- App Store --------------------------------------------------------- */

/* The file version from a PE image's version resource: "v1.2.3", or "". */
static void exe_version(const BYTE* d, DWORD n, char* out, int cap)
{
    DWORD pe, sections, optSize, table, s, i;
    out[0] = 0;
    if (n < 0x40 || d[0] != 'M' || d[1] != 'Z') return;
    pe = d[0x3C] | (d[0x3D] << 8) | (d[0x3E] << 16) | ((DWORD)d[0x3F] << 24);
    if (pe + 24 > n || memcmp(d + pe, "PE\0\0", 4) != 0) return;
    sections = d[pe + 6] | (d[pe + 7] << 8);
    optSize = d[pe + 20] | (d[pe + 21] << 8);
    table = pe + 24 + optSize;
    for (s = 0; s < sections && table + s * 40 + 40 <= n; s++) {
        const BYTE* h = d + table + s * 40;
        DWORD size, raw;
        if (memcmp(h, ".rsrc", 5) != 0) continue;
        size = h[16] | (h[17] << 8) | (h[18] << 16) | ((DWORD)h[19] << 24);
        raw = h[20] | (h[21] << 8) | (h[22] << 16) | ((DWORD)h[23] << 24);
        for (i = raw; i + 16 <= raw + size && i + 16 <= n; i += 4) {
            if (d[i] == 0xBD && d[i + 1] == 0x04 && d[i + 2] == 0xEF && d[i + 3] == 0xFE) {
                DWORD ms = d[i + 8] | (d[i + 9] << 8) | (d[i + 10] << 16) | ((DWORD)d[i + 11] << 24);
                DWORD ls = d[i + 12] | (d[i + 13] << 8) | (d[i + 14] << 16) | ((DWORD)d[i + 15] << 24);
                _snprintf(out, cap - 1, "v%u.%u.%u", (unsigned)(ms >> 16), (unsigned)(ms & 0xFFFF), (unsigned)(ls >> 16));
                out[cap - 1] = 0;
                return;
            }
        }
    }
}

/* A CE shortcut is a text file: <length>#<command line>. */
static int write_shortcut(const WCHAR* lnk, const WCHAR* target)
{
    char cmd[300], body[320];
    int n = _snprintf(cmd, sizeof(cmd) - 1, "\"%S\"", target);
    if (n < 0) return 0;
    cmd[n] = 0;
    n = _snprintf(body, sizeof(body) - 1, "%d#%s", n, cmd);
    if (n < 0) return 0;
    body[n] = 0;
    CreateDirectory(START_MENU, NULL);
    return write_text(lnk, body);
}

static void app_folder(WCHAR* dir, int cap)
{
    /* \Application survives a clean boot on Motorola/Zebra devices. */
    _snwprintf(dir, cap - 1, L"%s\\%s", dir_exists(L"\\Application") ? L"\\Application" : L"\\Program Files", APP_FOLDER);
    dir[cap - 1] = 0;
}

static int install_app(const WCHAR* dir, WCHAR* exePath, WCHAR* why, int cap)
{
    const BYTE* app;
    DWORD size;
    WCHAR manifestPath[MAX_PATH];
    char manifest[1024], version[32], lnkA[MAX_PATH];
    SYSTEMTIME now;

    app = resource(IDR_APPSTORE, &size);
    if (app == NULL || size == 0) { wcsncpy(why, L"App Store is missing from this setup.", cap); return 0; }

    _snwprintf(exePath, MAX_PATH - 1, L"%s\\%s", dir, APP_EXE);
    exePath[MAX_PATH - 1] = 0;
    if (in_use(exePath)) { wcsncpy(why, L"App Store is running. Close it, then run Setup again.", cap); return 0; }

    CreateDirectory(dir, NULL);
    if (!dir_exists(dir)) { _snwprintf(why, cap, L"Couldn't create %s.", dir); return 0; }
    if (!write_file(exePath, app, size)) { _snwprintf(why, cap, L"Couldn't write %s.", exePath); return 0; }

    /* The same manifest App Store writes when it installs an app, so it
       recognises itself as installed and can update itself later. */
    exe_version(app, size, version, sizeof(version));
    GetLocalTime(&now);
    _snprintf(lnkA, sizeof(lnkA) - 1, "%S", SHORTCUT); lnkA[sizeof(lnkA) - 1] = 0;
    _snprintf(manifest, sizeof(manifest) - 1,
        "# Written by App Store Setup. Lists the files it installed, so they can be updated and removed.\r\n"
        "id=appstore\r\nname=App Store\r\ntag=%s\r\nexe=%S\r\nshortcut=%s\r\nuninstall=\r\n"
        "files=%S\r\nkeep=store.cfg\r\ninstalled=%04d-%02d-%02d %02d:%02d:%02d\r\n",
        version, APP_EXE, lnkA, APP_EXE,
        now.wYear, now.wMonth, now.wDay, now.wHour, now.wMinute, now.wSecond);
    manifest[sizeof(manifest) - 1] = 0;
    _snwprintf(manifestPath, MAX_PATH - 1, L"%s\\appstore.inf", dir);
    manifestPath[MAX_PATH - 1] = 0;
    write_text(manifestPath, manifest);     /* nice to have; App Store works without it */

    if (!write_shortcut(SHORTCUT, exePath)) {
        wcsncpy(why, L"App Store is installed, but the Start menu shortcut couldn't be written.", cap);
        return 2;
    }
    return 1;
}

/* ---- ClockSync ---------------------------------------------------------- */

/* Returns 1 installed, 2 already installed (left alone), 0 failed (why set). */
static int install_clocksync(WCHAR* why, int cap)
{
    const BYTE* cs;
    DWORD size;
    WCHAR manifestPath[MAX_PATH];
    char manifest[600], lnkA[MAX_PATH];
    SYSTEMTIME now;

    if (file_exists(CS_EXE)) return 2;     /* don't replace a (maybe newer) copy */
    cs = resource(IDR_CLOCKSYNC, &size);
    if (cs == NULL || size == 0) { wcsncpy(why, L"ClockSync is missing from this setup.", cap); return 0; }

    CreateDirectory(CS_DIR, NULL);
    if (!write_file(CS_EXE, cs, size)) { _snwprintf(why, cap, L"Couldn't write %s.", CS_EXE); return 0; }

    /* "/install" adds the boot shortcuts and the network-connect trigger, then
       sets the clock once (it shows a message first, which the user closes). */
    if (!run_wait(CS_EXE, L"/install", CS_WAIT_MS)) {
        wcsncpy(why, L"ClockSync didn't finish installing.", cap);
        return 0;
    }

    /* Let App Store know it's installed (the same manifest it writes). */
    GetLocalTime(&now);
    _snprintf(lnkA, sizeof(lnkA) - 1, "%S", CS_SHORTCUT); lnkA[sizeof(lnkA) - 1] = 0;
    _snprintf(manifest, sizeof(manifest) - 1,
        "# Written by App Store Setup. Lists the files it installed, so they can be updated and removed.\r\n"
        "id=clocksync\r\nname=ClockSync\r\ntag=%s\r\nexe=ClockSync.exe\r\nshortcut=%s\r\nuninstall=/uninstall\r\n"
        "files=ClockSync.exe\r\nkeep=ClockSync.ini\r\ninstalled=%04d-%02d-%02d %02d:%02d:%02d\r\n",
        CLOCKSYNC_TAG, lnkA, now.wYear, now.wMonth, now.wDay, now.wHour, now.wMinute, now.wSecond);
    manifest[sizeof(manifest) - 1] = 0;
    _snwprintf(manifestPath, MAX_PATH - 1, L"%s\\appstore.inf", CS_DIR);
    manifestPath[MAX_PATH - 1] = 0;
    write_text(manifestPath, manifest);
    write_shortcut(CS_SHORTCUT, CS_EXE);   /* tap it to sync now */
    return 1;
}

/* ---- main --------------------------------------------------------------- */

/* Windows CE's WinMain takes a wide command line. SETUP_DESKTOP_TEST lets
   the file be compile-checked with a desktop Windows compiler. */
#ifdef SETUP_DESKTOP_TEST
#define SETUP_ENTRY wWinMain
#else
#define SETUP_ENTRY WinMain
#endif

int WINAPI SETUP_ENTRY(HINSTANCE inst, HINSTANCE prev, LPTSTR cmdLine, int show)
{
    WCHAR versions[128], dir[MAX_PATH], exePath[MAX_PATH], msg[700], why[300];
    DWORD cabSize = 0, appSize = 0;
    const BYTE* app;
    char appVersion[32];
    WCHAR appVersionW[32];
    int haveCf, haveCs, r, cs = -1;
    WCHAR csNote[200];
    (void)prev; (void)show;
    g_inst = inst;

    g_quiet = cmdLine != NULL && _wcsnicmp(cmdLine, L"/quiet", 6) == 0;

    haveCf = cf35_installed(versions, 128);
    resource(IDR_NETCF, &cabSize);
    app = resource(IDR_APPSTORE, &appSize);
    appVersion[0] = 0;
    if (app != NULL) exe_version(app, appSize, appVersion, sizeof(appVersion));
    _snwprintf(appVersionW, 31, L"%S", appVersion);
    appVersionW[31] = 0;
    app_folder(dir, MAX_PATH);
    haveCs = file_exists(CS_EXE);
    csNote[0] = 0;

    _snwprintf(msg, 699,
        L"This sets up App Store %s on this device.\r\n\r\n"
        L"ClockSync (keeps the clock right): %s\r\n"
        L".NET Compact Framework 3.5: %s\r\n"
        L"App Store goes into: %s\r\n\r\nContinue?",
        appVersionW,
        haveCs ? L"already installed" : L"will be installed - tap OK on its message",
        haveCf ? L"already installed" : L"will be installed (about 1-2 minutes)",
        dir);
    msg[699] = 0;
    if (!ask(msg)) return 0;

    /* First, so the clock is right before App Store makes its first HTTPS
       request. A failure here isn't fatal: App Store also checks the clock. */
    if (!haveCs) {
        cs = install_clocksync(why, 300);
        if (cs == 0) {
            _snwprintf(csNote, 199, L"\r\n\r\nClockSync wasn't installed: %s", why);
            csNote[199] = 0;
        }
    }

    if (!haveCf) {
        if (!install_cf35(why, 300)) {
            _snwprintf(msg, 699,
                L"%s\r\n\r\nInstalled versions: %s\r\n\r\nSoft-reset the device and run Setup again. "
                L"If it still fails, copy %s to the device and tap it to install CF 3.5 by hand.",
                why, versions[0] ? versions : L"none", CAB_NAME);
            msg[699] = 0;
            tell(msg, MB_ICONERROR);
            return 1;
        }
    }

    r = install_app(dir, exePath, why, 300);
    if (r == 0) { tell(why, MB_ICONERROR); return 1; }

    _snwprintf(msg, 699,
        L"App Store is installed%s%s.\r\n\r\n%sStart it any time from Start > Programs > App Store.%s",
        haveCf ? L"" : L", with .NET CF 3.5",
        cs == 1 ? L" and ClockSync" : L"",
        r == 2 ? L"(The Start menu shortcut couldn't be written.)\r\n" : L"",
        csNote);
    msg[699] = 0;
    tell(msg, MB_ICONINFORMATION);

    {
        PROCESS_INFORMATION pi;
        memset(&pi, 0, sizeof(pi));
        if (CreateProcess(exePath, (LPWSTR)L"", NULL, NULL, FALSE, 0, NULL, NULL, NULL, &pi)) {
            CloseHandle(pi.hThread);
            CloseHandle(pi.hProcess);
        }
    }
    return 0;
}
