// HapticLink virtual pad: a UMDF2 HID minidriver that presents a wired DualSense
// (054C:0CE6) to Windows.
//
// Games read input report 0x01, which HapticLink feeds through shared memory (PadShared.h)
// from the real controller over Bluetooth. Game output reports (0x02: rumble, adaptive
// triggers, lights, haptics select) go the other way through a ring in the same section.
// Feature reads are answered from the real pad's copies when HapticLink provides them,
// else from built-in defaults.
//
// Written after Microsoft's vhidmini2 sample and punktfunk's pf-gamepad driver (see README).
// Each pad gets its own WUDFHost (UmdfHostProcessSharing=ProcessSharingDisabled in the INF).

// windows.h and ntstatus.h both define some STATUS_ codes; take them from ntstatus.h only.
#define WIN32_NO_STATUS
#include <windows.h>
#undef WIN32_NO_STATUS
#include <ntstatus.h>
#pragma warning(push)
#pragma warning(disable : 4324) // WDF's own headers pad aligned structures
#include <wdf.h>
#pragma warning(pop)
#include <hidport.h>
#include "PadShared.h"
#include "DualSense.h"

#ifndef CREATE_WAITABLE_TIMER_HIGH_RESOLUTION
#define CREATE_WAITABLE_TIMER_HIGH_RESOLUTION 0x00000002
#endif

#define DS_VID      0x054C
#define DS_PID      0x0CE6
#define DS_VERSION  0x0100

// A real DualSense on USB reports every 4 ms.
#define TICK_MS                 4
// How often to look for HapticLink's section while it isn't open.
#define OPEN_RETRY_TICKS        50

DRIVER_INITIALIZE DriverEntry;
EVT_WDF_DRIVER_DEVICE_ADD EvtDeviceAdd;
EVT_WDF_DEVICE_D0_ENTRY EvtDeviceD0Entry;
EVT_WDF_DEVICE_D0_EXIT EvtDeviceD0Exit;
EVT_WDF_OBJECT_CONTEXT_CLEANUP EvtDeviceCleanup;
EVT_WDF_IO_QUEUE_IO_DEVICE_CONTROL EvtIoDeviceControl;

typedef struct PAD_CONTEXT {
    WDFDEVICE       Device;
    WDFQUEUE        ReadQueue;      // manual queue holding hidclass's pending READ_REPORTs
    HANDLE          Section;
    PHPAD_SHARED*   Shared;         // set once by the tick thread, unmapped at cleanup
    HANDLE          Thread;
    volatile LONG   Stop;
    UCHAR           LastInput[64];  // tick thread only
} PAD_CONTEXT;

WDF_DECLARE_CONTEXT_TYPE_WITH_NAME(PAD_CONTEXT, PadGetContext);

static const HID_DESCRIPTOR DsHidDescriptor = {
    0x09,   // bLength
    0x21,   // bDescriptorType: HID
    0x0111, // bcdHID
    0x00,   // bCountry
    0x01,   // bNumDescriptors
    { 0x22, sizeof(DsReportDescriptor) },
};

static const WCHAR DsManufacturer[] = L"Sony Interactive Entertainment";
static const WCHAR DsProduct[] = L"DualSense Wireless Controller";
static const WCHAR DsSerial[] = L"HLINK-DS-0";

// ---------------------------------------------------------------------------------------------
// Shared memory

static PHPAD_SHARED* SharedIfReady(PAD_CONTEXT* ctx)
{
    PHPAD_SHARED* s = (PHPAD_SHARED*)InterlockedCompareExchangePointer((PVOID*)&ctx->Shared, NULL, NULL);
    if (s == NULL || s->Magic != PHPAD_MAGIC || s->Version != PHPAD_VERSION) {
        return NULL;
    }
    return s;
}

static void TryOpenShared(PAD_CONTEXT* ctx)
{
    HANDLE section = OpenFileMappingW(FILE_MAP_READ | FILE_MAP_WRITE, FALSE, PHPAD_SECTION_NAME);
    if (section == NULL) {
        return;
    }
    PVOID view = MapViewOfFile(section, FILE_MAP_READ | FILE_MAP_WRITE, 0, 0, PHPAD_SECTION_SIZE);
    if (view == NULL) {
        CloseHandle(section);
        return;
    }
    ctx->Section = section;
    InterlockedExchangePointer((PVOID*)&ctx->Shared, view);
}

// Copies the app's latest input report; FALSE while the app is mid-write or not there.
static BOOLEAN ReadSharedInput(PAD_CONTEXT* ctx, UCHAR out[64])
{
    PHPAD_SHARED* s = SharedIfReady(ctx);
    if (s == NULL) {
        return FALSE;
    }
    for (int attempt = 0; attempt < 4; attempt++) {
        uint32_t before = s->InputSeq;
        MemoryBarrier();
        if ((before & 1) == 0) {
            memcpy(out, (const void*)s->Input, 64);
            MemoryBarrier();
            if (s->InputSeq == before) {
                return out[0] == 0x01;
            }
        }
        YieldProcessor();
    }
    return FALSE;
}

static void PushOutput(PAD_CONTEXT* ctx, const UCHAR* report, ULONG length)
{
    PHPAD_SHARED* s = SharedIfReady(ctx);
    if (s == NULL || length == 0) {
        return;
    }
    // The default queue is parallel, so each write claims its own slot first. The slot's Seq is
    // set last; the app reads a slot only once Seq says it holds report number `head`.
    ULONG head = (ULONG)InterlockedIncrement((volatile LONG*)&s->OutputHead) - 1;
    PHPAD_SLOT* slot = &s->Output[head % PHPAD_RING_SLOTS];
    ULONG n = length > sizeof(slot->Data) ? (ULONG)sizeof(slot->Data) : length;
    slot->Seq = 0;
    MemoryBarrier();
    memcpy(slot->Data, report, n);
    slot->Length = n;
    MemoryBarrier();
    slot->Seq = head + 1;
}

// ---------------------------------------------------------------------------------------------
// Request helpers

static NTSTATUS CompleteWithBytes(WDFREQUEST request, const void* source, size_t count)
{
    WDFMEMORY memory;
    size_t capacity;
    NTSTATUS status = WdfRequestRetrieveOutputMemory(request, &memory);
    if (!NT_SUCCESS(status)) {
        return status;
    }
    WdfMemoryGetBuffer(memory, &capacity);
    if (capacity < count) {
        return STATUS_INVALID_BUFFER_SIZE;
    }
    status = WdfMemoryCopyFromBuffer(memory, 0, (PVOID)source, count);
    if (NT_SUCCESS(status)) {
        WdfRequestSetInformation(request, count);
    }
    return status;
}

// UMDF HID transfer to the caller: report id from the input buffer, data into the output buffer.
static NTSTATUS GetReadPacket(WDFREQUEST request, HID_XFER_PACKET* packet)
{
    WDFMEMORY in, out;
    size_t inLength, outLength;
    NTSTATUS status = WdfRequestRetrieveInputMemory(request, &in);
    if (!NT_SUCCESS(status)) {
        return status;
    }
    PVOID inBuffer = WdfMemoryGetBuffer(in, &inLength);
    if (inLength < 1) {
        return STATUS_INVALID_BUFFER_SIZE;
    }
    packet->reportId = *(PUCHAR)inBuffer;
    status = WdfRequestRetrieveOutputMemory(request, &out);
    if (!NT_SUCCESS(status)) {
        return status;
    }
    packet->reportBuffer = (PUCHAR)WdfMemoryGetBuffer(out, &outLength);
    packet->reportBufferLen = (ULONG)outLength;
    return STATUS_SUCCESS;
}

// UMDF HID transfer from the caller: the report (id included) is the input buffer.
static NTSTATUS GetWritePacket(WDFREQUEST request, HID_XFER_PACKET* packet)
{
    WDFMEMORY in;
    size_t inLength;
    NTSTATUS status = WdfRequestRetrieveInputMemory(request, &in);
    if (!NT_SUCCESS(status)) {
        return status;
    }
    packet->reportBuffer = (PUCHAR)WdfMemoryGetBuffer(in, &inLength);
    packet->reportBufferLen = (ULONG)inLength;
    packet->reportId = inLength > 0 ? packet->reportBuffer[0] : 0;
    return STATUS_SUCCESS;
}

static NTSTATUS WriteOutputReport(PAD_CONTEXT* ctx, WDFREQUEST request)
{
    HID_XFER_PACKET packet;
    NTSTATUS status = GetWritePacket(request, &packet);
    if (!NT_SUCCESS(status)) {
        return status;
    }
    if (packet.reportBufferLen < 2) {
        return STATUS_INVALID_BUFFER_SIZE;
    }
    PushOutput(ctx, packet.reportBuffer, packet.reportBufferLen);
    WdfRequestSetInformation(request, packet.reportBufferLen);
    return STATUS_SUCCESS;
}

static NTSTATUS GetFeature(PAD_CONTEXT* ctx, WDFREQUEST request)
{
    HID_XFER_PACKET packet;
    NTSTATUS status = GetReadPacket(request, &packet);
    if (!NT_SUCCESS(status)) {
        return status;
    }

    PHPAD_SHARED* s = SharedIfReady(ctx);
    uint32_t mask = s != NULL ? s->FeatureMask : 0;
    const UCHAR* blob = NULL;
    ULONG length = 0;
    switch (packet.reportId) {
    case 0x05:
        blob = (mask & PHPAD_FEATURE_05) ? s->Feature05 : DsFeatureCalibration;
        length = sizeof(DsFeatureCalibration);
        break;
    case 0x09:
        blob = (mask & PHPAD_FEATURE_09) ? s->Feature09 : DsFeaturePairing;
        length = sizeof(DsFeaturePairing);
        break;
    case 0x20:
        blob = (mask & PHPAD_FEATURE_20) ? s->Feature20 : DsFeatureFirmware;
        length = sizeof(DsFeatureFirmware);
        break;
    default:
        break;
    }

    if (packet.reportBufferLen == 0) {
        return STATUS_INVALID_BUFFER_SIZE;
    }
    if (blob == NULL) {
        // Other feature reports (test/factory ones): answer zeros of the requested size.
        memset(packet.reportBuffer, 0, packet.reportBufferLen);
        packet.reportBuffer[0] = packet.reportId;
        WdfRequestSetInformation(request, packet.reportBufferLen);
        return STATUS_SUCCESS;
    }
    if (packet.reportBufferLen < length) {
        return STATUS_INVALID_BUFFER_SIZE;
    }
    memcpy(packet.reportBuffer, blob, length);
    packet.reportBuffer[0] = packet.reportId;
    WdfRequestSetInformation(request, length);
    return STATUS_SUCCESS;
}

static NTSTATUS SetFeature(WDFREQUEST request)
{
    // Accepted and ignored: the pad's settings live on the real controller.
    HID_XFER_PACKET packet;
    NTSTATUS status = GetWritePacket(request, &packet);
    if (NT_SUCCESS(status)) {
        WdfRequestSetInformation(request, packet.reportBufferLen);
    }
    return status;
}

static NTSTATUS GetInputReport(PAD_CONTEXT* ctx, WDFREQUEST request)
{
    HID_XFER_PACKET packet;
    NTSTATUS status = GetReadPacket(request, &packet);
    if (!NT_SUCCESS(status)) {
        return status;
    }
    UCHAR report[64];
    if (!ReadSharedInput(ctx, report)) {
        memcpy(report, DsNeutralInput, sizeof(report));
    }
    if (packet.reportBufferLen < sizeof(report)) {
        return STATUS_INVALID_BUFFER_SIZE;
    }
    memcpy(packet.reportBuffer, report, sizeof(report));
    WdfRequestSetInformation(request, sizeof(report));
    return STATUS_SUCCESS;
}

static NTSTATUS GetString(WDFREQUEST request)
{
    WDFMEMORY in;
    size_t inLength;
    NTSTATUS status = WdfRequestRetrieveInputMemory(request, &in);
    if (!NT_SUCCESS(status)) {
        return status;
    }
    PVOID inBuffer = WdfMemoryGetBuffer(in, &inLength);
    if (inLength < sizeof(ULONG)) {
        return STATUS_INVALID_BUFFER_SIZE;
    }
    switch (*(PULONG)inBuffer & 0xFFFF) {
    case HID_STRING_ID_IMANUFACTURER:
        return CompleteWithBytes(request, DsManufacturer, sizeof(DsManufacturer));
    case HID_STRING_ID_IPRODUCT:
        return CompleteWithBytes(request, DsProduct, sizeof(DsProduct));
    case HID_STRING_ID_ISERIALNUMBER:
        return CompleteWithBytes(request, DsSerial, sizeof(DsSerial));
    default:
        return STATUS_INVALID_PARAMETER;
    }
}

// ---------------------------------------------------------------------------------------------
// The tick thread: delivers one input report per tick, like a real pad does.

static DWORD WINAPI TickThread(LPVOID parameter)
{
    PAD_CONTEXT* ctx = (PAD_CONTEXT*)parameter;
    // WUDFHost runs on the 15.6 ms system tick; a high-resolution timer keeps 4 ms.
    HANDLE timer = CreateWaitableTimerExW(NULL, NULL, CREATE_WAITABLE_TIMER_HIGH_RESOLUTION, TIMER_ALL_ACCESS);
    if (timer == NULL) {
        timer = CreateWaitableTimerExW(NULL, NULL, 0, TIMER_ALL_ACCESS);
    }
    if (timer == NULL) {
        return 1;
    }
    LARGE_INTEGER due;
    due.QuadPart = -10000LL * TICK_MS;
    SetWaitableTimer(timer, &due, TICK_MS, NULL, NULL, FALSE);
    SetThreadPriority(GetCurrentThread(), THREAD_PRIORITY_TIME_CRITICAL);

    ULONG ticks = 0;
    while (InterlockedCompareExchange(&ctx->Stop, 0, 0) == 0) {
        WaitForSingleObject(timer, 100);
        ticks++;

        if (ctx->Shared == NULL) {
            if (ticks % OPEN_RETRY_TICKS == 1) {
                TryOpenShared(ctx);
            }
        } else {
            InterlockedIncrement((volatile LONG*)&ctx->Shared->DriverHeartbeat);
        }

        UCHAR report[64];
        if (ReadSharedInput(ctx, report)) {
            memcpy(ctx->LastInput, report, sizeof(report));
        } else if (SharedIfReady(ctx) == NULL) {
            memcpy(ctx->LastInput, DsNeutralInput, sizeof(DsNeutralInput));
        }

        WDFREQUEST request;
        if (NT_SUCCESS(WdfIoQueueRetrieveNextRequest(ctx->ReadQueue, &request))) {
            NTSTATUS status = CompleteWithBytes(request, ctx->LastInput, sizeof(ctx->LastInput));
            WdfRequestComplete(request, status);
        }
    }

    CancelWaitableTimer(timer);
    CloseHandle(timer);
    return 0;
}

NTSTATUS EvtDeviceD0Entry(WDFDEVICE device, WDF_POWER_DEVICE_STATE previousState)
{
    UNREFERENCED_PARAMETER(previousState);
    PAD_CONTEXT* ctx = PadGetContext(device);
    InterlockedExchange(&ctx->Stop, 0);
    ctx->Thread = CreateThread(NULL, 0, TickThread, ctx, 0, NULL);
    return ctx->Thread != NULL ? STATUS_SUCCESS : STATUS_INSUFFICIENT_RESOURCES;
}

NTSTATUS EvtDeviceD0Exit(WDFDEVICE device, WDF_POWER_DEVICE_STATE targetState)
{
    UNREFERENCED_PARAMETER(targetState);
    PAD_CONTEXT* ctx = PadGetContext(device);
    InterlockedExchange(&ctx->Stop, 1);
    if (ctx->Thread != NULL) {
        WaitForSingleObject(ctx->Thread, INFINITE);
        CloseHandle(ctx->Thread);
        ctx->Thread = NULL;
    }
    return STATUS_SUCCESS;
}

void EvtDeviceCleanup(WDFOBJECT object)
{
    PAD_CONTEXT* ctx = PadGetContext((WDFDEVICE)object);
    if (ctx->Shared != NULL) {
        UnmapViewOfFile(ctx->Shared);
        ctx->Shared = NULL;
    }
    if (ctx->Section != NULL) {
        CloseHandle(ctx->Section);
        ctx->Section = NULL;
    }
}

// ---------------------------------------------------------------------------------------------
// Requests from hidclass (forwarded as device-control by mshidumdf)

void EvtIoDeviceControl(WDFQUEUE queue, WDFREQUEST request, size_t outputBufferLength,
                        size_t inputBufferLength, ULONG ioControlCode)
{
    UNREFERENCED_PARAMETER(outputBufferLength);
    UNREFERENCED_PARAMETER(inputBufferLength);
    PAD_CONTEXT* ctx = PadGetContext(WdfIoQueueGetDevice(queue));
    NTSTATUS status;

    switch (ioControlCode) {
    case IOCTL_HID_GET_DEVICE_DESCRIPTOR:
        status = CompleteWithBytes(request, &DsHidDescriptor, DsHidDescriptor.bLength);
        break;
    case IOCTL_HID_GET_DEVICE_ATTRIBUTES: {
        HID_DEVICE_ATTRIBUTES attributes;
        RtlZeroMemory(&attributes, sizeof(attributes));
        attributes.Size = sizeof(attributes);
        attributes.VendorID = DS_VID;
        attributes.ProductID = DS_PID;
        attributes.VersionNumber = DS_VERSION;
        status = CompleteWithBytes(request, &attributes, sizeof(attributes));
        break;
    }
    case IOCTL_HID_GET_REPORT_DESCRIPTOR:
        status = CompleteWithBytes(request, DsReportDescriptor, sizeof(DsReportDescriptor));
        break;
    case IOCTL_HID_READ_REPORT:
        status = WdfRequestForwardToIoQueue(request, ctx->ReadQueue);
        if (NT_SUCCESS(status)) {
            return; // completed by the tick thread
        }
        break;
    case IOCTL_HID_WRITE_REPORT:
    case IOCTL_UMDF_HID_SET_OUTPUT_REPORT:
        status = WriteOutputReport(ctx, request);
        break;
    case IOCTL_UMDF_HID_GET_FEATURE:
        status = GetFeature(ctx, request);
        break;
    case IOCTL_UMDF_HID_SET_FEATURE:
        status = SetFeature(request);
        break;
    case IOCTL_UMDF_HID_GET_INPUT_REPORT:
        status = GetInputReport(ctx, request);
        break;
    case IOCTL_HID_GET_STRING:
        status = GetString(request);
        break;
    default:
        status = STATUS_NOT_IMPLEMENTED;
        break;
    }
    WdfRequestComplete(request, status);
}

// ---------------------------------------------------------------------------------------------
// Driver and device setup

NTSTATUS EvtDeviceAdd(WDFDRIVER driver, PWDFDEVICE_INIT deviceInit)
{
    UNREFERENCED_PARAMETER(driver);

    // A HID minidriver sits under mshidumdf as a filter-style FDO.
    WdfFdoInitSetFilter(deviceInit);

    WDF_PNPPOWER_EVENT_CALLBACKS pnpPower;
    WDF_PNPPOWER_EVENT_CALLBACKS_INIT(&pnpPower);
    pnpPower.EvtDeviceD0Entry = EvtDeviceD0Entry;
    pnpPower.EvtDeviceD0Exit = EvtDeviceD0Exit;
    WdfDeviceInitSetPnpPowerEventCallbacks(deviceInit, &pnpPower);

    WDF_OBJECT_ATTRIBUTES attributes;
    WDF_OBJECT_ATTRIBUTES_INIT_CONTEXT_TYPE(&attributes, PAD_CONTEXT);
    attributes.EvtCleanupCallback = EvtDeviceCleanup;

    WDFDEVICE device;
    NTSTATUS status = WdfDeviceCreate(&deviceInit, &attributes, &device);
    if (!NT_SUCCESS(status)) {
        return status;
    }
    PAD_CONTEXT* ctx = PadGetContext(device);
    ctx->Device = device;
    memcpy(ctx->LastInput, DsNeutralInput, sizeof(DsNeutralInput));

    WDF_IO_QUEUE_CONFIG queueConfig;
    WDF_IO_QUEUE_CONFIG_INIT_DEFAULT_QUEUE(&queueConfig, WdfIoQueueDispatchParallel);
    // hidclass sends internal IOCTLs, which UMDF can't take; mshidumdf turns them into
    // device-control requests.
    queueConfig.EvtIoDeviceControl = EvtIoDeviceControl;
    status = WdfIoQueueCreate(device, &queueConfig, WDF_NO_OBJECT_ATTRIBUTES, WDF_NO_HANDLE);
    if (!NT_SUCCESS(status)) {
        return status;
    }

    WDF_IO_QUEUE_CONFIG_INIT(&queueConfig, WdfIoQueueDispatchManual);
    return WdfIoQueueCreate(device, &queueConfig, WDF_NO_OBJECT_ATTRIBUTES, &ctx->ReadQueue);
}

NTSTATUS DriverEntry(PDRIVER_OBJECT driverObject, PUNICODE_STRING registryPath)
{
    WDF_DRIVER_CONFIG config;
    WDF_DRIVER_CONFIG_INIT(&config, EvtDeviceAdd);
    return WdfDriverCreate(driverObject, registryPath, WDF_NO_OBJECT_ATTRIBUTES, &config, WDF_NO_HANDLE);
}
