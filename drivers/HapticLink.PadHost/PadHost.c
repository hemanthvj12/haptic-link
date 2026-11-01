// HapticLinkPadHost.dll: creates and removes HapticLink's virtual DualSense device for the
// pad service.
//
// It exists because SwDeviceCreate needs its completion callback to live inside a module on
// disk: called from .NET with a runtime-generated callback it fails with ERROR_MOD_NOT_FOUND
// (0x8007007E) for every input, while the same call from C works (verified 2026-11-01). So the
// one call, and its callback, live here; the service P/Invokes PhPadCreate / PhPadClose.

#include <windows.h>
#include <swdevice.h>

#pragma comment(lib, "swdevice.lib")

typedef struct CREATE_WAIT {
    HANDLE  Done;
    HRESULT Result;
    WCHAR   InstanceId[200];
} CREATE_WAIT;

static VOID WINAPI OnCreated(HSWDEVICE device, HRESULT result, PVOID context, PCWSTR instanceId)
{
    UNREFERENCED_PARAMETER(device);
    CREATE_WAIT* wait = (CREATE_WAIT*)context;
    wait->Result = result;
    if (instanceId != NULL) {
        lstrcpynW(wait->InstanceId, instanceId, ARRAYSIZE(wait->InstanceId));
    }
    SetEvent(wait->Done);
}

// Creates a software device under the root and waits up to timeoutMs for PnP to enumerate it.
// hardwareIds / compatibleIds are multi-strings (double-NUL terminated); compatibleIds and
// location may be NULL. On success *device must later be passed to PhPadClose, which removes
// the device; instanceIdOut (optional) receives the PnP instance id.
__declspec(dllexport) HRESULT WINAPI PhPadCreate(
    PCWSTR enumerator, PCWSTR instanceId, PCZZWSTR hardwareIds, PCZZWSTR compatibleIds,
    const GUID* containerId, PCWSTR description, PCWSTR location, DWORD timeoutMs,
    HSWDEVICE* device, PWSTR instanceIdOut, DWORD instanceIdChars)
{
    if (device == NULL || enumerator == NULL || instanceId == NULL || hardwareIds == NULL) {
        return E_INVALIDARG;
    }
    *device = NULL;

    // Heap-allocated: if the wait times out, a late callback must not write to a dead stack
    // frame, so the block (and its event) are deliberately leaked in that case.
    CREATE_WAIT* wait = (CREATE_WAIT*)HeapAlloc(GetProcessHeap(), HEAP_ZERO_MEMORY, sizeof(CREATE_WAIT));
    if (wait == NULL) {
        return E_OUTOFMEMORY;
    }
    wait->Result = E_FAIL;
    wait->Done = CreateEventW(NULL, TRUE, FALSE, NULL);
    if (wait->Done == NULL) {
        HeapFree(GetProcessHeap(), 0, wait);
        return HRESULT_FROM_WIN32(GetLastError());
    }

    SW_DEVICE_CREATE_INFO info;
    ZeroMemory(&info, sizeof(info));
    info.cbSize = sizeof(info);
    info.pszInstanceId = instanceId;
    info.pszzHardwareIds = hardwareIds;
    info.pszzCompatibleIds = compatibleIds;
    info.pContainerId = containerId;
    info.CapabilityFlags = SWDeviceCapabilitiesRemovable | SWDeviceCapabilitiesSilentInstall | SWDeviceCapabilitiesDriverRequired;
    info.pszDeviceDescription = description;
    info.pszDeviceLocation = location;

    HSWDEVICE created = NULL;
    HRESULT hr = SwDeviceCreate(enumerator, L"HTREE\\ROOT\\0", &info, 0, NULL, OnCreated, wait, &created);
    if (FAILED(hr)) {
        CloseHandle(wait->Done);
        HeapFree(GetProcessHeap(), 0, wait);
        return hr;
    }

    if (WaitForSingleObject(wait->Done, timeoutMs) != WAIT_OBJECT_0) {
        SwDeviceClose(created); // removes the device; the callback may still come, so `wait` leaks
        return HRESULT_FROM_WIN32(ERROR_TIMEOUT);
    }

    hr = wait->Result;
    if (SUCCEEDED(hr)) {
        *device = created;
        if (instanceIdOut != NULL && instanceIdChars > 0) {
            lstrcpynW(instanceIdOut, wait->InstanceId, (int)instanceIdChars);
        }
    } else {
        SwDeviceClose(created);
    }
    CloseHandle(wait->Done);
    HeapFree(GetProcessHeap(), 0, wait);
    return hr;
}

// Removes a device made by PhPadCreate (its lifetime is tied to this handle).
__declspec(dllexport) void WINAPI PhPadClose(HSWDEVICE device)
{
    if (device != NULL) {
        SwDeviceClose(device);
    }
}
