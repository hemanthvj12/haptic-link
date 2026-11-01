// The shared-memory channel between the HapticLink service and the virtual pad driver.
//
// The service creates the section (named below) with a DACL that lets SYSTEM and LOCAL SERVICE
// (WUDFHost, where the driver runs) read and write it; the driver opens it by name. One section
// per pad. All fields are little-endian; the C# side (VirtualPadChannel) mirrors these offsets
// exactly, and a test checks the two agree.
#pragma once
#include <stdint.h>

#define PHPAD_MAGIC         0x31504C48u   // "HLP1"
#define PHPAD_VERSION       1u
#define PHPAD_SECTION_NAME  L"Global\\HapticLinkPad0"
#define PHPAD_SECTION_SIZE  4096u
#define PHPAD_RING_SLOTS    16u

#define PHPAD_FEATURE_05    0x1u   // Feature05 holds the real pad's calibration
#define PHPAD_FEATURE_09    0x2u
#define PHPAD_FEATURE_20    0x4u

#pragma pack(push, 1)

typedef struct PHPAD_SLOT {
    volatile uint32_t Seq;         // report number + 1, written last (0 while being filled)
    uint32_t Length;               // bytes used in Data, report id included
    uint8_t  Data[64];
} PHPAD_SLOT;

typedef struct PHPAD_SHARED {
    uint32_t          Magic;       //    0, written last by the app once the rest is set up
    uint32_t          Version;     //    4
    volatile uint32_t InputSeq;    //    8, seqlock: odd while the app writes Input
    uint8_t           Input[64];   //   12, USB input report 0x01, id included
    volatile uint32_t DriverHeartbeat; // 76, the driver bumps it every tick
    volatile uint32_t OutputHead;  //   80, output reports claimed so far; report n is in slot n % 16
    PHPAD_SLOT        Output[PHPAD_RING_SLOTS]; // 84, game output reports (0x02), id included
    volatile uint32_t FeatureMask; // 1236, which of the blobs below the app filled in
    uint8_t           Feature05[64]; // 1240
    uint8_t           Feature09[64]; // 1304
    uint8_t           Feature20[64]; // 1368
} PHPAD_SHARED;

#pragma pack(pop)

static_assert(sizeof(PHPAD_SLOT) == 72, "slot size");
static_assert(sizeof(PHPAD_SHARED) == 1432, "shared layout");
static_assert(sizeof(PHPAD_SHARED) <= PHPAD_SECTION_SIZE, "fits the section");
