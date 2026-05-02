#include <stdint.h>
#include <stddef.h>
#include <string.h>
#include <windows.h>

#define XXH_PRIME64_1 11400714785074694791ULL
#define XXH_PRIME64_2 14029467366897019727ULL
#define XXH_PRIME64_3 1609587929392839161ULL
#define XXH_PRIME64_4 9650029242287828579ULL
#define XXH_PRIME64_5 2870177450012600261ULL

static inline uint64_t XXH64_rotl(uint64_t x, int r) {
    return (x << r) | (x >> (64 - r));
}

static inline uint64_t XXH64_round(uint64_t acc, uint64_t input) {
    acc += input * XXH_PRIME64_2;
    acc = XXH64_rotl(acc, 31);
    acc *= XXH_PRIME64_1;
    return acc;
}

static inline uint64_t XXH64_mergeRound(uint64_t acc, uint64_t val) {
    val = XXH64_round(0, val);
    acc ^= val;
    acc = acc * XXH_PRIME64_1 + XXH_PRIME64_4;
    return acc;
}

struct XXH64_State {
    uint64_t v1, v2, v3, v4;
    uint64_t totalLen;
    uint8_t mem[32];
    uint32_t memsize;
};

static void XXH64_Reset(XXH64_State* state, uint64_t seed) {
    state->v1 = seed + XXH_PRIME64_1 + XXH_PRIME64_2;
    state->v2 = seed + XXH_PRIME64_2;
    state->v3 = seed + 0;
    state->v4 = seed - XXH_PRIME64_1;
    state->totalLen = 0;
    state->memsize = 0;
}

static void XXH64_Update(XXH64_State* state, const uint8_t* input, size_t length) {
    state->totalLen += length;
    const uint8_t* p = input;
    const uint8_t* const bEnd = p + length;

    if (state->memsize + length < 32) {
        memcpy(state->mem + state->memsize, input, length);
        state->memsize += (uint32_t)length;
        return;
    }

    if (state->memsize > 0) {
        memcpy(state->mem + state->memsize, p, 32 - state->memsize);
        state->v1 = XXH64_round(state->v1, *(const uint64_t*)(state->mem + 0));
        state->v2 = XXH64_round(state->v2, *(const uint64_t*)(state->mem + 8));
        state->v3 = XXH64_round(state->v3, *(const uint64_t*)(state->mem + 16));
        state->v4 = XXH64_round(state->v4, *(const uint64_t*)(state->mem + 24));
        p += 32 - state->memsize;
        state->memsize = 0;
    }

    if (p <= bEnd - 32) {
        const uint8_t* const limit = bEnd - 32;
        do {
            state->v1 = XXH64_round(state->v1, *(const uint64_t*)p); p += 8;
            state->v2 = XXH64_round(state->v2, *(const uint64_t*)p); p += 8;
            state->v3 = XXH64_round(state->v3, *(const uint64_t*)p); p += 8;
            state->v4 = XXH64_round(state->v4, *(const uint64_t*)p); p += 8;
        } while (p <= limit);
    }

    if (p < bEnd) {
        memcpy(state->mem, p, (size_t)(bEnd - p));
        state->memsize = (uint32_t)(bEnd - p);
    }
}

static uint64_t XXH64_Digest(const XXH64_State* state) {
    uint64_t h64;
    if (state->totalLen >= 32) {
        h64 = XXH64_rotl(state->v1, 1) + XXH64_rotl(state->v2, 7) + XXH64_rotl(state->v3, 12) + XXH64_rotl(state->v4, 18);
        h64 = XXH64_mergeRound(h64, state->v1);
        h64 = XXH64_mergeRound(h64, state->v2);
        h64 = XXH64_mergeRound(h64, state->v3);
        h64 = XXH64_mergeRound(h64, state->v4);
    } else {
        h64 = state->v3 + XXH_PRIME64_5;
    }

    h64 += state->totalLen;

    const uint8_t* p = state->mem;
    const uint8_t* const bEnd = p + state->memsize;

    while (p + 8 <= bEnd) {
        uint64_t k1 = XXH64_round(0, *(const uint64_t*)p);
        h64 ^= k1;
        h64 = XXH64_rotl(h64, 27) * XXH_PRIME64_1 + XXH_PRIME64_4;
        p += 8;
    }

    if (p + 4 <= bEnd) {
        h64 ^= (uint64_t)(*(const uint32_t*)p) * XXH_PRIME64_1;
        h64 = XXH64_rotl(h64, 23) * XXH_PRIME64_2 + XXH_PRIME64_3;
        p += 4;
    }

    while (p < bEnd) {
        h64 ^= (*p) * XXH_PRIME64_5;
        h64 = XXH64_rotl(h64, 11) * XXH_PRIME64_1;
        p++;
    }

    h64 ^= h64 >> 33;
    h64 *= XXH_PRIME64_2;
    h64 ^= h64 >> 29;
    h64 *= XXH_PRIME64_3;
    h64 ^= h64 >> 32;

    return h64;
}

extern "C" {
    __declspec(dllexport) int __stdcall XXH64_HashFile(const wchar_t* filePath, uint64_t* outHash) {
        HANDLE hFile = CreateFileW(filePath, GENERIC_READ, FILE_SHARE_READ, NULL, OPEN_EXISTING, FILE_FLAG_SEQUENTIAL_SCAN, NULL);
        if (hFile == INVALID_HANDLE_VALUE) return (int)GetLastError();

        LARGE_INTEGER fileSize;
        if (!GetFileSizeEx(hFile, &fileSize)) {
            CloseHandle(hFile);
            return (int)GetLastError();
        }

        XXH64_State state;
        XXH64_Reset(&state, 0);

        if (fileSize.QuadPart == 0) {
            *outHash = XXH64_Digest(&state);
            CloseHandle(hFile);
            return 0;
        }

        HANDLE hMapping = CreateFileMappingW(hFile, NULL, PAGE_READONLY, 0, 0, NULL);
        if (hMapping == NULL) {
            CloseHandle(hFile);
            return (int)GetLastError();
        }

        SYSTEM_INFO sysInfo;
        GetSystemInfo(&sysInfo);
        const uint64_t allocationGranularity = sysInfo.dwAllocationGranularity;
        const uint64_t chunkSize = 512 * 1024 * 1024;

        uint64_t offset = 0;
        while (offset < (uint64_t)fileSize.QuadPart) {
            uint64_t remaining = (uint64_t)fileSize.QuadPart - offset;
            uint64_t viewSize = (remaining < chunkSize) ? remaining : chunkSize;

            uint64_t alignedOffset = (offset / allocationGranularity) * allocationGranularity;
            uint32_t alignmentDist = (uint32_t)(offset - alignedOffset);
            uint64_t fullViewSize = viewSize + alignmentDist;

            const uint8_t* fileData = (const uint8_t*)MapViewOfFile(hMapping, FILE_MAP_READ, (DWORD)(alignedOffset >> 32), (DWORD)(alignedOffset & 0xFFFFFFFF), (SIZE_T)fullViewSize);

            if (fileData == NULL) {
                CloseHandle(hMapping);
                CloseHandle(hFile);
                return (int)GetLastError();
            }

            XXH64_Update(&state, fileData + alignmentDist, (size_t)viewSize);

            UnmapViewOfFile(fileData);
            offset += viewSize;
        }

        *outHash = XXH64_Digest(&state);

        CloseHandle(hMapping);
        CloseHandle(hFile);
        return 0;
    }
}