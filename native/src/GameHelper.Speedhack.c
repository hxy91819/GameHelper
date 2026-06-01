#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <stdint.h>
#include <stdio.h>

#include "../third_party/minhook/include/MinHook.h"

#define GH_EXPORT __declspec(dllexport)
#define GH_MAX_SESSIONS 32
#define GH_MAP_MAGIC 0x47485348 /* GHSH */
#define GH_MAP_VERSION 1
#define GH_ATTACH_TIMEOUT_MS 6000
#define GH_DETACH_TIMEOUT_MS 5000
#define GH_COMMAND_TIMEOUT_MS 1200

typedef struct GH_SHARED_BLOCK
{
    volatile LONG magic;
    volatile LONG version;
    volatile LONG state;
    volatile LONG lastError;
    volatile LONG command;
    volatile LONG commandSeq;
    volatile LONG handledSeq;
    volatile LONG heartbeat;
    volatile LONG detachRequested;
    DWORD targetPid;
    double speed;
} GH_SHARED_BLOCK;

enum
{
    GH_STATE_INIT = 0,
    GH_STATE_READY = 1,
    GH_STATE_ERROR = -1,
    GH_STATE_STOPPED = 2,
};

enum
{
    GH_CMD_NONE = 0,
    GH_CMD_SET_SPEED = 1,
    GH_CMD_DETACH = 2,
};

enum
{
    GH_OK = 0,
    GH_ERR_INVALID_ARGUMENT = -1,
    GH_ERR_ALREADY_ATTACHED = -2,
    GH_ERR_PROCESS_OPEN = -3,
    GH_ERR_SESSION_FULL = -4,
    GH_ERR_MAPPING_CREATE = -5,
    GH_ERR_MAPPING_VIEW = -6,
    GH_ERR_INJECT_ALLOC = -7,
    GH_ERR_INJECT_WRITE = -8,
    GH_ERR_INJECT_THREAD = -9,
    GH_ERR_ATTACH_TIMEOUT = -10,
    GH_ERR_NOT_ATTACHED = -11,
    GH_ERR_SET_TIMEOUT = -12,
    GH_ERR_REMOTE_STATE = -13,
    GH_ERR_DETACH_TIMEOUT = -14,
    GH_ERR_PATH = -15,
};

typedef struct GH_SESSION
{
    int inUse;
    DWORD pid;
    HANDLE processHandle;
    HANDLE mappingHandle;
    GH_SHARED_BLOCK* shared;
} GH_SESSION;

static SRWLOCK g_sessionLock = SRWLOCK_INIT;
static GH_SESSION g_sessions[GH_MAX_SESSIONS];
static HMODULE g_selfModule = NULL;

/* Remote worker globals */
static HANDLE g_remoteMappingHandle = NULL;
static GH_SHARED_BLOCK* g_remoteShared = NULL;

/* Speed hack state (inside remote process) */
static double g_speed = 1.0;
static int g_hooksEnabled = 0;

typedef DWORD(WINAPI* tGetTickCount)(void);
typedef ULONGLONG(WINAPI* tGetTickCount64)(void);
typedef BOOL(WINAPI* tQueryPerformanceCounter)(LARGE_INTEGER*);
typedef DWORD(WINAPI* tTimeGetTime)(void);

static tGetTickCount g_origGetTickCount = NULL;
static tGetTickCount64 g_origGetTickCount64 = NULL;
static tQueryPerformanceCounter g_origQueryPerformanceCounter = NULL;
static tTimeGetTime g_origTimeGetTime = NULL;

static DWORD g_gtcBase = 0;
static DWORD g_gtcOffset = 0;
static ULONGLONG g_gtc64Base = 0;
static ULONGLONG g_gtc64Offset = 0;
static LARGE_INTEGER g_qpcBase;
static LARGE_INTEGER g_qpcOffset;
static DWORD g_tgtBase = 0;
static DWORD g_tgtOffset = 0;

static void GH_BuildMappingName(DWORD pid, wchar_t* buffer, size_t cchBuffer)
{
    _snwprintf(buffer, cchBuffer, L"Local\\GameHelper.Speedhack.%lu", (unsigned long)pid);
    buffer[cchBuffer - 1] = L'\0';
}

static int GH_GetSelfPath(wchar_t* buffer, size_t cchBuffer)
{
    DWORD copied;

    if (buffer == NULL || cchBuffer == 0 || g_selfModule == NULL)
    {
        return GH_ERR_PATH;
    }

    copied = GetModuleFileNameW(g_selfModule, buffer, (DWORD)cchBuffer);
    if (copied == 0 || copied >= cchBuffer)
    {
        return GH_ERR_PATH;
    }

    return GH_OK;
}

static GH_SESSION* GH_FindSessionUnlocked(DWORD pid)
{
    int i;
    for (i = 0; i < GH_MAX_SESSIONS; ++i)
    {
        if (g_sessions[i].inUse && g_sessions[i].pid == pid)
        {
            return &g_sessions[i];
        }
    }

    return NULL;
}

static GH_SESSION* GH_AllocSessionUnlocked(void)
{
    int i;
    for (i = 0; i < GH_MAX_SESSIONS; ++i)
    {
        if (!g_sessions[i].inUse)
        {
            g_sessions[i].inUse = 1;
            return &g_sessions[i];
        }
    }

    return NULL;
}

static void GH_ClearSessionUnlocked(GH_SESSION* session)
{
    if (session == NULL)
    {
        return;
    }

    session->inUse = 0;
    session->pid = 0;
    session->shared = NULL;

    if (session->mappingHandle != NULL)
    {
        CloseHandle(session->mappingHandle);
        session->mappingHandle = NULL;
    }

    if (session->processHandle != NULL)
    {
        CloseHandle(session->processHandle);
        session->processHandle = NULL;
    }
}

static void GH_CloseSessionResources(GH_SESSION* session)
{
    if (session == NULL)
    {
        return;
    }

    if (session->shared != NULL)
    {
        UnmapViewOfFile(session->shared);
        session->shared = NULL;
    }

    if (session->mappingHandle != NULL)
    {
        CloseHandle(session->mappingHandle);
        session->mappingHandle = NULL;
    }

    if (session->processHandle != NULL)
    {
        CloseHandle(session->processHandle);
        session->processHandle = NULL;
    }
}

static int GH_InjectSelf(HANDLE processHandle)
{
    wchar_t dllPath[MAX_PATH];
    SIZE_T pathBytes;
    LPVOID remoteBuffer = NULL;
    HANDLE threadHandle = NULL;
    DWORD remoteResult = 0;
    HMODULE kernel32 = NULL;
    LPVOID loadLibraryWPtr = NULL;
    SIZE_T written = 0;
    int status;

    status = GH_GetSelfPath(dllPath, MAX_PATH);
    if (status != GH_OK)
    {
        return status;
    }

    pathBytes = (wcslen(dllPath) + 1) * sizeof(wchar_t);

    remoteBuffer = VirtualAllocEx(processHandle, NULL, pathBytes, MEM_COMMIT | MEM_RESERVE, PAGE_READWRITE);
    if (remoteBuffer == NULL)
    {
        return GH_ERR_INJECT_ALLOC;
    }

    if (!WriteProcessMemory(processHandle, remoteBuffer, dllPath, pathBytes, &written) || written != pathBytes)
    {
        VirtualFreeEx(processHandle, remoteBuffer, 0, MEM_RELEASE);
        return GH_ERR_INJECT_WRITE;
    }

    kernel32 = GetModuleHandleW(L"kernel32.dll");
    loadLibraryWPtr = (kernel32 != NULL) ? (LPVOID)GetProcAddress(kernel32, "LoadLibraryW") : NULL;
    if (loadLibraryWPtr == NULL)
    {
        VirtualFreeEx(processHandle, remoteBuffer, 0, MEM_RELEASE);
        return GH_ERR_INJECT_THREAD;
    }

    threadHandle = CreateRemoteThread(processHandle, NULL, 0, (LPTHREAD_START_ROUTINE)loadLibraryWPtr, remoteBuffer, 0, NULL);
    if (threadHandle == NULL)
    {
        VirtualFreeEx(processHandle, remoteBuffer, 0, MEM_RELEASE);
        return GH_ERR_INJECT_THREAD;
    }

    if (WaitForSingleObject(threadHandle, GH_ATTACH_TIMEOUT_MS) != WAIT_OBJECT_0)
    {
        CloseHandle(threadHandle);
        VirtualFreeEx(processHandle, remoteBuffer, 0, MEM_RELEASE);
        return GH_ERR_ATTACH_TIMEOUT;
    }

    if (!GetExitCodeThread(threadHandle, &remoteResult) || remoteResult == 0)
    {
        CloseHandle(threadHandle);
        VirtualFreeEx(processHandle, remoteBuffer, 0, MEM_RELEASE);
        return GH_ERR_INJECT_THREAD;
    }

    CloseHandle(threadHandle);
    VirtualFreeEx(processHandle, remoteBuffer, 0, MEM_RELEASE);
    return GH_OK;
}

static DWORD WINAPI GH_HookGetTickCount(void)
{
    return g_gtcOffset + (DWORD)((g_origGetTickCount() - g_gtcBase) * g_speed);
}

static ULONGLONG WINAPI GH_HookGetTickCount64(void)
{
    return g_gtc64Offset + (ULONGLONG)((g_origGetTickCount64() - g_gtc64Base) * g_speed);
}

static BOOL WINAPI GH_HookQueryPerformanceCounter(LARGE_INTEGER* outCounter)
{
    LARGE_INTEGER current;

    g_origQueryPerformanceCounter(&current);
    outCounter->QuadPart = g_qpcOffset.QuadPart + (LONGLONG)((current.QuadPart - g_qpcBase.QuadPart) * g_speed);
    return TRUE;
}

static DWORD WINAPI GH_HookTimeGetTime(void)
{
    if (g_origTimeGetTime == NULL)
    {
        return g_gtcOffset + (DWORD)((g_origGetTickCount() - g_gtcBase) * g_speed);
    }

    return g_tgtOffset + (DWORD)((g_origTimeGetTime() - g_tgtBase) * g_speed);
}

static void GH_UpdateSpeed(double newSpeed)
{
    LARGE_INTEGER logicalQpc;

    if (newSpeed <= 0.0)
    {
        newSpeed = 1.0;
    }

    if (g_hooksEnabled)
    {
        g_gtcOffset = GH_HookGetTickCount();
        g_gtcBase = g_origGetTickCount();

        if (g_origGetTickCount64 != NULL)
        {
            g_gtc64Offset = GH_HookGetTickCount64();
            g_gtc64Base = g_origGetTickCount64();
        }

        GH_HookQueryPerformanceCounter(&logicalQpc);
        g_qpcOffset = logicalQpc;
        g_origQueryPerformanceCounter(&g_qpcBase);

        if (g_origTimeGetTime != NULL)
        {
            g_tgtOffset = GH_HookTimeGetTime();
            g_tgtBase = g_origTimeGetTime();
        }
    }

    g_speed = newSpeed;
}

static int GH_EnableHooks(void)
{
    HMODULE kernel32;
    HMODULE winmm;

    kernel32 = GetModuleHandleW(L"kernel32.dll");
    if (kernel32 == NULL)
    {
        return GH_ERR_REMOTE_STATE;
    }

    g_origGetTickCount = (tGetTickCount)GetProcAddress(kernel32, "GetTickCount");
    g_origGetTickCount64 = (tGetTickCount64)GetProcAddress(kernel32, "GetTickCount64");
    g_origQueryPerformanceCounter = (tQueryPerformanceCounter)GetProcAddress(kernel32, "QueryPerformanceCounter");

    if (g_origGetTickCount == NULL || g_origQueryPerformanceCounter == NULL)
    {
        return GH_ERR_REMOTE_STATE;
    }

    winmm = GetModuleHandleW(L"winmm.dll");
    if (winmm == NULL)
    {
        winmm = LoadLibraryW(L"winmm.dll");
    }

    if (winmm != NULL)
    {
        g_origTimeGetTime = (tTimeGetTime)GetProcAddress(winmm, "timeGetTime");
    }

    g_gtcBase = g_origGetTickCount();
    g_gtcOffset = g_gtcBase;

    if (g_origGetTickCount64 != NULL)
    {
        g_gtc64Base = g_origGetTickCount64();
        g_gtc64Offset = g_gtc64Base;
    }

    g_origQueryPerformanceCounter(&g_qpcBase);
    g_qpcOffset = g_qpcBase;

    if (g_origTimeGetTime != NULL)
    {
        g_tgtBase = g_origTimeGetTime();
        g_tgtOffset = g_tgtBase;
    }

    if (MH_Initialize() != MH_OK)
    {
        return GH_ERR_REMOTE_STATE;
    }

    if (MH_CreateHook((LPVOID)g_origGetTickCount, (LPVOID)GH_HookGetTickCount, (LPVOID*)&g_origGetTickCount) != MH_OK)
    {
        MH_Uninitialize();
        return GH_ERR_REMOTE_STATE;
    }

    if (g_origGetTickCount64 != NULL)
    {
        if (MH_CreateHook((LPVOID)g_origGetTickCount64, (LPVOID)GH_HookGetTickCount64, (LPVOID*)&g_origGetTickCount64) != MH_OK)
        {
            MH_Uninitialize();
            return GH_ERR_REMOTE_STATE;
        }
    }

    if (MH_CreateHook((LPVOID)g_origQueryPerformanceCounter, (LPVOID)GH_HookQueryPerformanceCounter, (LPVOID*)&g_origQueryPerformanceCounter) != MH_OK)
    {
        MH_Uninitialize();
        return GH_ERR_REMOTE_STATE;
    }

    if (g_origTimeGetTime != NULL)
    {
        (void)MH_CreateHook((LPVOID)g_origTimeGetTime, (LPVOID)GH_HookTimeGetTime, (LPVOID*)&g_origTimeGetTime);
    }

    if (MH_EnableHook(MH_ALL_HOOKS) != MH_OK)
    {
        MH_Uninitialize();
        return GH_ERR_REMOTE_STATE;
    }

    g_hooksEnabled = 1;
    g_speed = 1.0;
    return GH_OK;
}

static void GH_DisableHooks(void)
{
    if (!g_hooksEnabled)
    {
        return;
    }

    MH_DisableHook(MH_ALL_HOOKS);
    MH_Uninitialize();
    g_hooksEnabled = 0;
}

static DWORD WINAPI GH_RemoteWorkerThread(LPVOID lpParam)
{
    (void)lpParam;

    if (g_remoteShared == NULL)
    {
        return 0;
    }

    if (GH_EnableHooks() != GH_OK)
    {
        g_remoteShared->lastError = GH_ERR_REMOTE_STATE;
        g_remoteShared->state = GH_STATE_ERROR;
    }
    else
    {
        LONG lastSeq = g_remoteShared->commandSeq;
        g_remoteShared->state = GH_STATE_READY;

        for (;;)
        {
            LONG seq;
            LONG command;

            if (g_remoteShared->detachRequested)
            {
                break;
            }

            seq = g_remoteShared->commandSeq;
            command = g_remoteShared->command;

            if (seq != lastSeq)
            {
                if (command == GH_CMD_SET_SPEED)
                {
                    GH_UpdateSpeed(g_remoteShared->speed);
                }
                else if (command == GH_CMD_DETACH)
                {
                    break;
                }

                g_remoteShared->handledSeq = seq;
                lastSeq = seq;
            }

            InterlockedIncrement(&g_remoteShared->heartbeat);
            Sleep(10);
        }

        GH_DisableHooks();
        g_remoteShared->state = GH_STATE_STOPPED;
        g_remoteShared->detachRequested = 1;
    }

    if (g_remoteShared != NULL)
    {
        UnmapViewOfFile(g_remoteShared);
        g_remoteShared = NULL;
    }

    if (g_remoteMappingHandle != NULL)
    {
        CloseHandle(g_remoteMappingHandle);
        g_remoteMappingHandle = NULL;
    }

    FreeLibraryAndExitThread(g_selfModule, 0);
    return 0;
}

static void GH_TryStartRemoteWorker(void)
{
    wchar_t mappingName[64];
    HANDLE mappingHandle;
    GH_SHARED_BLOCK* shared;
    DWORD currentPid;
    HANDLE threadHandle;

    currentPid = GetCurrentProcessId();
    GH_BuildMappingName(currentPid, mappingName, sizeof(mappingName) / sizeof(mappingName[0]));

    mappingHandle = OpenFileMappingW(FILE_MAP_ALL_ACCESS, FALSE, mappingName);
    if (mappingHandle == NULL)
    {
        return;
    }

    shared = (GH_SHARED_BLOCK*)MapViewOfFile(mappingHandle, FILE_MAP_ALL_ACCESS, 0, 0, sizeof(GH_SHARED_BLOCK));
    if (shared == NULL)
    {
        CloseHandle(mappingHandle);
        return;
    }

    if (shared->magic != GH_MAP_MAGIC || shared->version != GH_MAP_VERSION || shared->targetPid != currentPid)
    {
        UnmapViewOfFile(shared);
        CloseHandle(mappingHandle);
        return;
    }

    g_remoteMappingHandle = mappingHandle;
    g_remoteShared = shared;

    threadHandle = CreateThread(NULL, 0, GH_RemoteWorkerThread, NULL, 0, NULL);
    if (threadHandle == NULL)
    {
        shared->lastError = GH_ERR_REMOTE_STATE;
        shared->state = GH_STATE_ERROR;
        UnmapViewOfFile(shared);
        CloseHandle(mappingHandle);
        g_remoteShared = NULL;
        g_remoteMappingHandle = NULL;
        return;
    }

    CloseHandle(threadHandle);
}

GH_EXPORT int gh_speedhack_attach(int processId)
{
    HANDLE processHandle = NULL;
    HANDLE mappingHandle = NULL;
    GH_SHARED_BLOCK* shared = NULL;
    GH_SESSION* session = NULL;
    wchar_t mappingName[64];
    int injectCode;
    DWORD waited = 0;

    if (processId <= 0)
    {
        return GH_ERR_INVALID_ARGUMENT;
    }

    AcquireSRWLockExclusive(&g_sessionLock);
    if (GH_FindSessionUnlocked((DWORD)processId) != NULL)
    {
        ReleaseSRWLockExclusive(&g_sessionLock);
        return GH_ERR_ALREADY_ATTACHED;
    }
    ReleaseSRWLockExclusive(&g_sessionLock);

    processHandle = OpenProcess(
        PROCESS_CREATE_THREAD |
        PROCESS_QUERY_INFORMATION |
        PROCESS_VM_OPERATION |
        PROCESS_VM_WRITE |
        PROCESS_VM_READ |
        SYNCHRONIZE,
        FALSE,
        (DWORD)processId);

    if (processHandle == NULL)
    {
        return GH_ERR_PROCESS_OPEN;
    }

    GH_BuildMappingName((DWORD)processId, mappingName, sizeof(mappingName) / sizeof(mappingName[0]));

    mappingHandle = CreateFileMappingW(INVALID_HANDLE_VALUE, NULL, PAGE_READWRITE, 0, sizeof(GH_SHARED_BLOCK), mappingName);
    if (mappingHandle == NULL)
    {
        CloseHandle(processHandle);
        return GH_ERR_MAPPING_CREATE;
    }

    shared = (GH_SHARED_BLOCK*)MapViewOfFile(mappingHandle, FILE_MAP_ALL_ACCESS, 0, 0, sizeof(GH_SHARED_BLOCK));
    if (shared == NULL)
    {
        CloseHandle(mappingHandle);
        CloseHandle(processHandle);
        return GH_ERR_MAPPING_VIEW;
    }

    ZeroMemory(shared, sizeof(GH_SHARED_BLOCK));
    shared->magic = GH_MAP_MAGIC;
    shared->version = GH_MAP_VERSION;
    shared->state = GH_STATE_INIT;
    shared->targetPid = (DWORD)processId;
    shared->speed = 1.0;

    injectCode = GH_InjectSelf(processHandle);
    if (injectCode != GH_OK)
    {
        UnmapViewOfFile(shared);
        CloseHandle(mappingHandle);
        CloseHandle(processHandle);
        return injectCode;
    }

    while (waited < GH_ATTACH_TIMEOUT_MS)
    {
        DWORD waitResult = WaitForSingleObject(processHandle, 10);

        if (shared->state == GH_STATE_READY)
        {
            break;
        }

        if (shared->state == GH_STATE_ERROR)
        {
            int remoteCode = shared->lastError;
            UnmapViewOfFile(shared);
            CloseHandle(mappingHandle);
            CloseHandle(processHandle);
            return (remoteCode != 0) ? remoteCode : GH_ERR_REMOTE_STATE;
        }

        if (waitResult == WAIT_OBJECT_0)
        {
            UnmapViewOfFile(shared);
            CloseHandle(mappingHandle);
            CloseHandle(processHandle);
            return GH_ERR_ATTACH_TIMEOUT;
        }

        waited += 10;
    }

    if (shared->state != GH_STATE_READY)
    {
        UnmapViewOfFile(shared);
        CloseHandle(mappingHandle);
        CloseHandle(processHandle);
        return GH_ERR_ATTACH_TIMEOUT;
    }

    AcquireSRWLockExclusive(&g_sessionLock);
    session = GH_AllocSessionUnlocked();
    if (session == NULL)
    {
        ReleaseSRWLockExclusive(&g_sessionLock);
        UnmapViewOfFile(shared);
        CloseHandle(mappingHandle);
        CloseHandle(processHandle);
        return GH_ERR_SESSION_FULL;
    }

    session->pid = (DWORD)processId;
    session->processHandle = processHandle;
    session->mappingHandle = mappingHandle;
    session->shared = shared;
    ReleaseSRWLockExclusive(&g_sessionLock);

    return GH_OK;
}

GH_EXPORT int gh_speedhack_set_speed(int processId, double multiplier)
{
    GH_SESSION* session;
    LONG seq;
    DWORD waited = 0;

    if (processId <= 0 || multiplier <= 0.0)
    {
        return GH_ERR_INVALID_ARGUMENT;
    }

    AcquireSRWLockShared(&g_sessionLock);
    session = GH_FindSessionUnlocked((DWORD)processId);
    if (session == NULL || session->shared == NULL)
    {
        ReleaseSRWLockShared(&g_sessionLock);
        return GH_ERR_NOT_ATTACHED;
    }

    session->shared->speed = multiplier;
    session->shared->command = GH_CMD_SET_SPEED;
    seq = InterlockedIncrement(&session->shared->commandSeq);
    ReleaseSRWLockShared(&g_sessionLock);

    while (waited < GH_COMMAND_TIMEOUT_MS)
    {
        LONG handled = session->shared->handledSeq;
        LONG state = session->shared->state;

        if (handled >= seq)
        {
            return GH_OK;
        }

        if (state == GH_STATE_ERROR)
        {
            int remoteCode = session->shared->lastError;
            return (remoteCode != 0) ? remoteCode : GH_ERR_REMOTE_STATE;
        }

        if (state == GH_STATE_STOPPED)
        {
            return GH_ERR_NOT_ATTACHED;
        }

        Sleep(5);
        waited += 5;
    }

    return GH_ERR_SET_TIMEOUT;
}

GH_EXPORT int gh_speedhack_detach(int processId)
{
    GH_SESSION localCopy;
    GH_SESSION* session;
    DWORD waited = 0;

    if (processId <= 0)
    {
        return GH_ERR_INVALID_ARGUMENT;
    }

    ZeroMemory(&localCopy, sizeof(localCopy));

    AcquireSRWLockExclusive(&g_sessionLock);
    session = GH_FindSessionUnlocked((DWORD)processId);
    if (session == NULL || session->shared == NULL)
    {
        ReleaseSRWLockExclusive(&g_sessionLock);
        return GH_ERR_NOT_ATTACHED;
    }

    localCopy = *session;
    session->inUse = 0;
    session->pid = 0;
    session->processHandle = NULL;
    session->mappingHandle = NULL;
    session->shared = NULL;
    ReleaseSRWLockExclusive(&g_sessionLock);

    localCopy.shared->command = GH_CMD_DETACH;
    localCopy.shared->detachRequested = 1;
    InterlockedIncrement(&localCopy.shared->commandSeq);

    while (waited < GH_DETACH_TIMEOUT_MS)
    {
        if (localCopy.shared->state == GH_STATE_STOPPED)
        {
            break;
        }

        if (WaitForSingleObject(localCopy.processHandle, 10) == WAIT_OBJECT_0)
        {
            break;
        }

        waited += 10;
    }

    if (localCopy.shared != NULL)
    {
        UnmapViewOfFile(localCopy.shared);
        localCopy.shared = NULL;
    }

    if (localCopy.mappingHandle != NULL)
    {
        CloseHandle(localCopy.mappingHandle);
        localCopy.mappingHandle = NULL;
    }

    if (localCopy.processHandle != NULL)
    {
        CloseHandle(localCopy.processHandle);
        localCopy.processHandle = NULL;
    }

    if (waited >= GH_DETACH_TIMEOUT_MS)
    {
        return GH_ERR_DETACH_TIMEOUT;
    }

    return GH_OK;
}

BOOL WINAPI DllMain(HINSTANCE hInstance, DWORD reason, LPVOID reserved)
{
    (void)reserved;

    if (reason == DLL_PROCESS_ATTACH)
    {
        g_selfModule = (HMODULE)hInstance;
        DisableThreadLibraryCalls((HMODULE)hInstance);
        GH_TryStartRemoteWorker();
    }

    return TRUE;
}
