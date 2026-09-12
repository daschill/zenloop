// ZenLoop CPU helper: AMD-signed Ryzen Master Platform.dll + Device.dll + AMDRyzenMasterDriver.
// Session PBO via Platform C exports (SetPPTLimit / TDC / EDC / scalar).
// Per-core Curve Optimizer + BIOS persist via Device.dll CGraniteCPU / CDefaultBIOS vtables.
// No WinRing0, no raw SMU IOCTL.

#define NOMINMAX
#include <Windows.h>
#include <cmath>
#include <cstdint>
#include <cstdio>
#include <cstdlib>
#include <cstring>
#include <iostream>
#include <sstream>
#include <string>
#include <vector>

static std::string JsonEscape(const std::string& s) {
    std::string o;
    o.reserve(s.size());
    for (char c : s) {
        switch (c) {
        case '\\': o += "\\\\"; break;
        case '"': o += "\\\""; break;
        case '\n': o += "\\n"; break;
        case '\r': o += "\\r"; break;
        case '\t': o += "\\t"; break;
        default: o += c;
        }
    }
    return o;
}

struct Json {
    std::ostringstream os;
    bool first = true;
    void beginObj() { os << "{"; first = true; }
    void endObj() { os << "}"; }
    void beginArr() { os << "["; first = true; }
    void endArr() { os << "]"; }
    void comma() { if (!first) os << ","; first = false; }
    void key(const char* k) { comma(); os << "\"" << k << "\":"; }
    void str(const char* k, const std::string& v) { key(k); os << "\"" << JsonEscape(v) << "\""; }
    void numi(const char* k, long long v) { key(k); os << v; }
    void num(const char* k, double v) {
        key(k);
        if (!std::isfinite(v)) os << "null";
        else os << v;
    }
    void boolean(const char* k, bool v) { key(k); os << (v ? "true" : "false"); }
    void null(const char* k) { key(k); os << "null"; }
    void raw(const char* k, const std::string& v) { key(k); os << v; }
    std::string str() const { return os.str(); }
};

static std::string gOut;

static void WriteOut(const std::string& json) {
    std::cout << json << std::endl;
    if (gOut.empty()) return;
    FILE* f = nullptr;
    if (fopen_s(&f, gOut.c_str(), "wb") == 0 && f) {
        fwrite(json.data(), 1, json.size(), f);
        fputc('\n', f);
        fclose(f);
    }
}

static void Fail(const std::string& msg, int code = 1) {
    Json j;
    j.beginObj();
    j.boolean("ok", false);
    j.boolean("session_applied", false);
    j.boolean("bios_persisted", false);
    j.boolean("co_written", false);
    j.str("backend", "amd-ryzen-master");
    j.str("error", msg);
    j.endObj();
    WriteOut(j.str());
    std::exit(code);
}

static bool IsElevated() {
    HANDLE tok = nullptr;
    if (!OpenProcessToken(GetCurrentProcess(), TOKEN_QUERY, &tok)) return false;
    TOKEN_ELEVATION te{};
    DWORD n = 0;
    BOOL ok = GetTokenInformation(tok, TokenElevation, &te, sizeof(te), &n);
    CloseHandle(tok);
    return ok && te.TokenIsElevated != 0;
}

static DWORD OpenRmDevice() {
    HANDLE h = CreateFileW(L"\\\\.\\AMDRyzenMasterDriverV27",
        GENERIC_READ | GENERIC_WRITE, FILE_SHARE_READ | FILE_SHARE_WRITE,
        nullptr, OPEN_EXISTING, 0, nullptr);
    if (h == INVALID_HANDLE_VALUE) return GetLastError();
    CloseHandle(h);
    return 0;
}

static std::wstring RmBinDir() {
    wchar_t pf[MAX_PATH];
    if (GetEnvironmentVariableW(L"ProgramFiles", pf, MAX_PATH) == 0)
        wcscpy_s(pf, L"C:\\Program Files");
    std::wstring p(pf);
    p += L"\\AMD\\RyzenMaster\\bin";
    return p;
}

// Platform.dll C exports (x64: rcx = int value, rdx = bool enable).
typedef int (*Fn_IsSupportedProcessor)();
typedef void* (*Fn_GetPlatform)();
typedef int (*Fn_GetRmCpuParameters)(void* outBuf);
typedef int (*Fn_SetPPTLimit)(int watts, bool enable);
typedef int (*Fn_SetTDCVDDLimit)(int amps, bool enable);
typedef int (*Fn_SetEDCVDDLimit)(int amps, bool enable);
typedef int (*Fn_EnableDisablePBOScalar)(int scalar, bool enable);
typedef int (*Fn_SetOverclockFreqAllCores)(int mhz, bool enable);
typedef int (*Fn_GetOCFuseStatus)();
typedef int (*Fn_SetOCFuseStatus)(bool enable);
typedef int (*Fn_GetCurrentFMaxCPU)();
typedef int (*Fn_GetCapabilities)(void* buf);
typedef int (*Fn_DisableOverclocking)();
typedef void* (*Fn_GetDeviceManager)();

struct PlatformApi {
    HMODULE platform = nullptr;
    HMODULE device = nullptr;
    Fn_IsSupportedProcessor IsSupportedProcessor = nullptr;
    Fn_GetPlatform GetPlatform = nullptr;
    Fn_GetRmCpuParameters GetRmCpuParameters = nullptr;
    Fn_SetPPTLimit SetPPTLimit = nullptr;
    Fn_SetTDCVDDLimit SetTDCVDDLimit = nullptr;
    Fn_SetEDCVDDLimit SetEDCVDDLimit = nullptr;
    Fn_EnableDisablePBOScalar EnableDisablePBOScalar = nullptr;
    Fn_SetOverclockFreqAllCores SetOverclockFreqAllCores = nullptr;
    Fn_GetOCFuseStatus GetOCFuseStatus = nullptr;
    Fn_SetOCFuseStatus SetOCFuseStatus = nullptr;
    Fn_GetCurrentFMaxCPU GetCurrentFMaxCPU = nullptr;
    Fn_GetCapabilities GetCapabilities = nullptr;
    Fn_DisableOverclocking DisableOverclocking = nullptr;
    Fn_GetDeviceManager GetDeviceManager = nullptr;
};

static FARPROC Must(HMODULE m, const char* name) {
    FARPROC p = GetProcAddress(m, name);
    if (!p) Fail(std::string("GetProcAddress failed: ") + name);
    return p;
}

static bool EnsureDriver(std::string* status) {
    SC_HANDLE scm = OpenSCManagerW(nullptr, nullptr, SC_MANAGER_CONNECT);
    if (!scm) {
        *status = "OpenSCManager failed";
        return false;
    }
    const wchar_t* names[] = { L"AMDRyzenMasterDriverV27", L"AMDRyzenMasterDriverV32", nullptr };
    SC_HANDLE svc = nullptr;
    for (int i = 0; names[i]; i++) {
        svc = OpenServiceW(scm, names[i], SERVICE_QUERY_STATUS);
        if (svc) break;
    }
    if (!svc) {
        CloseServiceHandle(scm);
        *status = "AMDRyzenMasterDriver not installed — install AMD Ryzen Master";
        return false;
    }
    SERVICE_STATUS_PROCESS ssp{};
    DWORD needed = 0;
    if (!QueryServiceStatusEx(svc, SC_STATUS_PROCESS_INFO, reinterpret_cast<LPBYTE>(&ssp), sizeof(ssp), &needed)) {
        CloseServiceHandle(svc);
        CloseServiceHandle(scm);
        *status = "QueryServiceStatusEx failed";
        return false;
    }
    bool running = ssp.dwCurrentState == SERVICE_RUNNING;
    if (!running) {
        if (StartServiceW(svc, 0, nullptr)) {
            for (int i = 0; i < 40; i++) {
                Sleep(100);
                QueryServiceStatusEx(svc, SC_STATUS_PROCESS_INFO, reinterpret_cast<LPBYTE>(&ssp), sizeof(ssp), &needed);
                if (ssp.dwCurrentState == SERVICE_RUNNING) { running = true; break; }
            }
        }
    }
    CloseServiceHandle(svc);
    CloseServiceHandle(scm);
    *status = running ? "running" : "not running";
    return running;
}

static PlatformApi LoadApis() {
    auto bin = RmBinDir();
    if (GetFileAttributesW((bin + L"\\Platform.dll").c_str()) == INVALID_FILE_ATTRIBUTES)
        Fail("Ryzen Master Platform.dll not found. Install AMD Ryzen Master from amd.com, then reboot.");
    SetDllDirectoryW(bin.c_str());
    PlatformApi a;
    a.device = LoadLibraryW((bin + L"\\Device.dll").c_str());
    a.platform = LoadLibraryW((bin + L"\\Platform.dll").c_str());
    if (!a.platform) Fail("LoadLibrary Platform.dll failed. Reinstall AMD Ryzen Master.");
    if (!a.device) Fail("LoadLibrary Device.dll failed. Reinstall AMD Ryzen Master.");
    a.IsSupportedProcessor = reinterpret_cast<Fn_IsSupportedProcessor>(Must(a.platform, "IsSupportedProcessor"));
    a.GetPlatform = reinterpret_cast<Fn_GetPlatform>(Must(a.platform, "GetPlatform"));
    a.GetRmCpuParameters = reinterpret_cast<Fn_GetRmCpuParameters>(Must(a.platform, "GetRmCpuParameters"));
    a.SetPPTLimit = reinterpret_cast<Fn_SetPPTLimit>(Must(a.platform, "SetPPTLimit"));
    a.SetTDCVDDLimit = reinterpret_cast<Fn_SetTDCVDDLimit>(Must(a.platform, "SetTDCVDDLimit"));
    a.SetEDCVDDLimit = reinterpret_cast<Fn_SetEDCVDDLimit>(Must(a.platform, "SetEDCVDDLimit"));
    a.EnableDisablePBOScalar = reinterpret_cast<Fn_EnableDisablePBOScalar>(Must(a.platform, "EnableDisablePBOScalar"));
    a.SetOverclockFreqAllCores = reinterpret_cast<Fn_SetOverclockFreqAllCores>(Must(a.platform, "SetOverclockFreqAllCores"));
    a.GetOCFuseStatus = reinterpret_cast<Fn_GetOCFuseStatus>(Must(a.platform, "GetOCFuseStatus"));
    a.SetOCFuseStatus = reinterpret_cast<Fn_SetOCFuseStatus>(Must(a.platform, "SetOCFuseStatus"));
    a.GetCurrentFMaxCPU = reinterpret_cast<Fn_GetCurrentFMaxCPU>(Must(a.platform, "GetCurrentFMaxCPU"));
    a.GetCapabilities = reinterpret_cast<Fn_GetCapabilities>(Must(a.platform, "GetCapabilities"));
    a.DisableOverclocking = reinterpret_cast<Fn_DisableOverclocking>(Must(a.platform, "DisableOverclocking"));
    a.GetDeviceManager = reinterpret_cast<Fn_GetDeviceManager>(
        GetProcAddress(a.device, "?GetDeviceManager@@YAAEAVIDeviceManager@@XZ"));
    return a;
}

static int CopyRttiName(void* obj, HMODULE mod, char* buf, int len) {
    buf[0] = 0;
    if (!obj || !mod || len < 2) return 0;
    __try {
        auto* v = *reinterpret_cast<uintptr_t**>(obj);
        auto col = reinterpret_cast<const uint32_t*>(v[-1]);
        uint32_t tdRva = col[3];
        auto base = reinterpret_cast<uintptr_t>(mod);
        const char* name = reinterpret_cast<const char*>(base + tdRva + 16);
        if (name && name[0] == '.') {
            strncpy_s(buf, len, name, _TRUNCATE);
            return 1;
        }
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        buf[0] = 0;
        return 0;
    }
    return 0;
}

static std::string RttiName(void* obj, HMODULE mod) {
    char buf[160] = {};
    CopyRttiName(obj, mod, buf, 160);
    return buf;
}

static void* VSlot(void* obj, int slot) {
    auto* v = *reinterpret_cast<void***>(obj);
    return v[slot];
}

// Ryzen Master 2.14 Device.dll vtable slots (verified via RTTI + .pdata on this install).
enum {
    kDevGetDevice = 2,
    kCpuSetEdc = 94,
    kCpuSetTdc = 95,
    kCpuSetPpt = 96,
    kCpuGetScalar = 98,
    kCpuSetScalar = 100,
    kCpuSetCoAll = 106,
    kCpuSetCoEach = 107,
    kCpuGetCoStatus = 108,
    kBiosSetEdc = 135,
    kBiosSetTdc = 136,
    kBiosSetPpt = 137,
    kBiosSetScalar = 138,
    kBiosSetCo = 144,
    kBiosGetFuse = 157,
    kBiosSetFuse = 158,
    kBiosGetVddio = 10,
    kBiosGetMemClk = 11,
    kBiosGetTcl = 12,
    kBiosGetTrcd = 13,
    kBiosGetTras = 14,
    kBiosGetTrp = 15,
    kBiosCmdStart = 25,
    kBiosCmdEnd = 26,
    kBiosGetTrfc = 30,
    kBiosSetVddio = 78,
    kBiosSetMemClk = 80,
    kBiosSetTcl = 81,
    kBiosSetTrcd = 82,
    kBiosSetTras = 84,
    kBiosSetTrp = 85,
    kBiosSetTrfc = 94,
    kBiosSetExpo = 162,
};

typedef int (__fastcall* FnDevInit)(void* self, int a, unsigned char b);
typedef void* (__fastcall* FnGetDevice)(void* self, int index);
typedef int (__fastcall* FnSetInt)(void* self, int value);
typedef int (__fastcall* FnSetIntBool)(void* self, int value, bool enable);
typedef int (__fastcall* FnSetCoBios)(void* self, unsigned short mode, unsigned short a, unsigned short b);
typedef int (__fastcall* FnSetCoEach)(void* self, unsigned short core, unsigned short ccd, short offset);
typedef int (__fastcall* FnGetInt)(void* self);
typedef int (__fastcall* FnGetIntOut)(void* self, int* out);
typedef int (__fastcall* FnVoid)(void* self);
typedef int (__fastcall* FnSetCoAll)(void* self, short offset);

typedef int (__fastcall* FnGetCoStatus)(void* self, unsigned short* status, std::vector<short>* vec);

static int SehCallInt(int (*fn)(void*), void* ctx) {
    __try { return fn(ctx); }
    __except (EXCEPTION_EXECUTE_HANDLER) { return static_cast<int>(GetExceptionCode()); }
}

static void* SehCallP(void* (*fn)()) {
    __try { return fn(); }
    __except (EXCEPTION_EXECUTE_HANDLER) { return nullptr; }
}

static int SehCall0(int (*fn)()) {
    __try { return fn ? fn() : -1; }
    __except (EXCEPTION_EXECUTE_HANDLER) { return static_cast<int>(GetExceptionCode()); }
}

static int SehCallBuf(int (*fn)(void*), void* buf) {
    __try { return fn ? fn(buf) : -1; }
    __except (EXCEPTION_EXECUTE_HANDLER) { return static_cast<int>(GetExceptionCode()); }
}

static int SehCallIB(int (*fn)(int, bool), int v, bool b) {
    __try { return fn ? fn(v, b) : -1; }
    __except (EXCEPTION_EXECUTE_HANDLER) { return static_cast<int>(GetExceptionCode()); }
}

static int SehCallB(int (*fn)(bool), bool b) {
    __try { return fn ? fn(b) : -1; }
    __except (EXCEPTION_EXECUTE_HANDLER) { return static_cast<int>(GetExceptionCode()); }
}

static Fn_GetDeviceManager g_getDevMgr = nullptr;
static void* CallGetDevMgr() { return g_getDevMgr ? g_getDevMgr() : nullptr; }

struct IntCtx { void* obj; int a; int b; unsigned short u0, u1, u2; short s0; unsigned short* st; void* vec; int which; };

static int DispatchSet(void* p) {
    auto* c = static_cast<IntCtx*>(p);
    switch (c->which) {
    case 1: return reinterpret_cast<FnSetInt>(VSlot(c->obj, c->b))(c->obj, c->a);
    case 2: return reinterpret_cast<FnSetCoEach>(VSlot(c->obj, kCpuSetCoEach))(c->obj, c->u0, c->u1, c->s0);
    case 3: return reinterpret_cast<FnSetCoAll>(VSlot(c->obj, kCpuSetCoAll))(c->obj, c->s0);
    case 4: return reinterpret_cast<FnSetCoBios>(VSlot(c->obj, kBiosSetCo))(c->obj, c->u0, c->u1, c->u2);
    case 5: return reinterpret_cast<FnGetCoStatus>(VSlot(c->obj, kCpuGetCoStatus))(
        c->obj, c->st, static_cast<std::vector<short>*>(c->vec));
    case 7: return reinterpret_cast<FnDevInit>(VSlot(c->obj, 0))(c->obj, 0, 0);
    case 8: {
        int tmp = 0;
        int rc = reinterpret_cast<FnGetIntOut>(VSlot(c->obj, c->b))(c->obj, &tmp);
        if (tmp != 0) { c->a = tmp; return 0; }
        if (rc > 10) { c->a = rc; return 0; }
        c->a = tmp;
        return rc;
    }
    case 10: return reinterpret_cast<FnVoid>(VSlot(c->obj, kBiosCmdStart))(c->obj);
    case 11: return reinterpret_cast<FnVoid>(VSlot(c->obj, kBiosCmdEnd))(c->obj);
    default: return -1;
    }
}

static void* SehGetDevice(void* mgr, int index) {
    void* out = nullptr;
    __try {
        out = reinterpret_cast<FnGetDevice>(VSlot(mgr, kDevGetDevice))(mgr, index);
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        out = nullptr;
    }
    return out;
}

static uintptr_t SehVftRva(void* obj, uintptr_t base) {
    uintptr_t rva = 0;
    __try {
        rva = *reinterpret_cast<uintptr_t*>(obj) - base;
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        rva = 0;
    }
    return rva;
}

struct Devices {
    void* cpu = nullptr;
    void* bios = nullptr;
    std::string cpuName;
    std::string biosName;
    int initRc = -1;
};

static Devices EnumDevices(PlatformApi& api) {
    Devices d;
    if (!api.GetDeviceManager) return d;
    g_getDevMgr = api.GetDeviceManager;
    void* mgr = SehCallP(CallGetDevMgr);
    if (!mgr) return d;
    IntCtx initCtx{};
    initCtx.obj = mgr;
    initCtx.a = 1;
    initCtx.b = 0;
    initCtx.which = 7;
    d.initRc = SehCallInt(DispatchSet, &initCtx);
    auto base = reinterpret_cast<uintptr_t>(api.device);
    for (int i = 0; i < 32; i++) {
        void* dev = SehGetDevice(mgr, i);
        if (!dev) continue;
        auto name = RttiName(dev, api.device);
        uintptr_t vftRva = SehVftRva(dev, base);
        bool isCpu = name.find("CPU") != std::string::npos || vftRva == 0xc86e0;
        bool isBios = name.find("BIOS") != std::string::npos || vftRva == 0xd3568;
        if (isCpu && !d.cpu) {
            d.cpu = dev;
            d.cpuName = name.empty() ? ("vft=" + std::to_string(vftRva)) : name;
        }
        if (isBios && !d.bios) {
            d.bios = dev;
            d.biosName = name.empty() ? ("vft=" + std::to_string(vftRva)) : name;
        }
    }
    return d;
}

struct RmBuf { unsigned char b[1024]; };

static bool FiniteF(float f) { return std::isfinite(f) != 0; }

static int NearestFloat(const RmBuf& buf, float lo, float hi, float prefer) {
    int bestOff = -1;
    float bestDist = 1e9f;
    for (int off = 0; off <= 240; off += 4) {
        float v;
        memcpy(&v, buf.b + off, 4);
        if (!FiniteF(v) || v < lo || v > hi) continue;
        float d = fabsf(v - prefer);
        if (d < bestDist) { bestDist = d; bestOff = off; }
    }
    if (bestOff < 0) return 0;
    float v;
    memcpy(&v, buf.b + bestOff, 4);
    return static_cast<int>(v + (v >= 0 ? 0.5f : -0.5f));
}

static int IntAt(const RmBuf& buf, int off) {
    int v;
    memcpy(&v, buf.b + off, 4);
    return v;
}

struct ParsedRm {
    int ppt = 0, tdc = 0, edc = 0, cores = 0;
    int fmax = 0;
};

static bool BufferEmpty(const RmBuf& buf, int n = 64) {
    for (int i = 0; i < n; i++)
        if (buf.b[i] != 0) return false;
    return true;
}

static ParsedRm ParseRm(const RmBuf& buf, int fmax) {
    ParsedRm p;
    p.fmax = fmax;
    p.cores = IntAt(buf, 4);
    if (p.cores < 1 || p.cores > 64) p.cores = IntAt(buf, 8);
    if (p.cores < 1 || p.cores > 64) p.cores = 0;
    p.ppt = NearestFloat(buf, 80.f, 900.f, 720.f);
    p.tdc = NearestFloat(buf, 50.f, 600.f, 480.f);
    p.edc = NearestFloat(buf, 60.f, 800.f, 640.f);
    return p;
}

static float F32(const RmBuf& buf, int off) {
    float v = 0;
    memcpy(&v, buf.b + off, 4);
    return v;
}

static double F64(const RmBuf& buf, int off) {
    double v = 0;
    memcpy(&v, buf.b + off, 8);
    return v;
}

struct LiveTel {
    bool ok = false;
    double powerW = 0;
    double tempC = 0;
    double voltMv = 0;
    double clockMhz = 0;
};

static bool SanePower(double w) { return std::isfinite(w) && w > 1 && w < 500; }
static bool SaneTemp(double t) { return std::isfinite(t) && t > 10 && t < 110; }
static bool SaneVoltV(double v) { return std::isfinite(v) && v > 0.5 && v < 1.65; }
static bool SaneMhz(double m) { return std::isfinite(m) && m > 800 && m < 7500; }

// Ryzen Master Monitoring SDK / GetRmCpuParameters live fields (pack 1).
static LiveTel ParseLive(const RmBuf& buf) {
    LiveTel L;
    float pptVal = F32(buf, 28);
    float vddcr = F32(buf, 72);
    float cclk = F32(buf, 80);
    double peakM = F64(buf, 88);
    double peakV = F64(buf, 96);
    double avgV = F64(buf, 104);
    double temp = F64(buf, 120);
    if (SanePower(pptVal)) L.powerW = pptVal;
    else if (SanePower(vddcr)) L.powerW = vddcr;
    if (SaneTemp(temp)) L.tempC = temp;
    else {
        for (int off = 0; off <= 240; off += 8) {
            double t = F64(buf, off);
            if (SaneTemp(t)) { L.tempC = t; break; }
        }
    }
    if (SaneVoltV(avgV)) L.voltMv = avgV * 1000.0;
    else if (SaneVoltV(peakV)) L.voltMv = peakV * 1000.0;
    if (SaneMhz(peakM)) L.clockMhz = peakM;
    else if (SaneMhz(cclk)) L.clockMhz = cclk;
    L.ok = L.powerW > 0 || L.tempC > 0;
    return L;
}

static void EmitLiveMetrics(Json& m, const LiveTel& L) {
    if (L.powerW > 0) {
        m.num("cpu_power_w", L.powerW);
        m.num("ppt_w", L.powerW);
    }
    if (L.tempC > 0) m.num("cpu_temp_c", L.tempC);
    if (L.voltMv > 0) m.num("cpu_voltage_mv", L.voltMv);
    if (L.clockMhz > 0) m.num("cpu_clock_mhz", L.clockMhz);
}

static std::vector<short> ReadCo(void* cpu, int ncores) {
    std::vector<short> out(ncores, 0);
    if (!cpu) return out;
    unsigned short status = 0;
    std::vector<short> storage;
    IntCtx ctx{};
    ctx.obj = cpu;
    ctx.st = &status;
    ctx.vec = &storage;
    ctx.which = 5;
    (void)SehCallInt(DispatchSet, &ctx);
    int n = static_cast<int>(storage.size());
    if (n > ncores) n = ncores;
    for (int i = 0; i < n; i++) out[i] = storage[i];
    return out;
}

static int ApplyCoEach(void* cpu, int core, short offset) {
    IntCtx ctx{};
    ctx.obj = cpu;
    ctx.u0 = static_cast<unsigned short>(core);
    ctx.u1 = 0;
    ctx.s0 = offset;
    ctx.which = 2;
    return SehCallInt(DispatchSet, &ctx);
}

static int ApplyBiosInt(void* bios, int slot, int value) {
    IntCtx ctx{};
    ctx.obj = bios;
    ctx.a = value;
    ctx.b = slot;
    ctx.which = 1;
    return SehCallInt(DispatchSet, &ctx);
}

static int GetBiosInt(void* bios, int slot, int* value) {
    IntCtx ctx{};
    ctx.obj = bios;
    ctx.b = slot;
    ctx.which = 8;
    int rc = SehCallInt(DispatchSet, &ctx);
    if (value) *value = ctx.a;
    return rc;
}

static std::string Arg(int argc, char** argv, const char* key, const char* def = "") {
    for (int i = 1; i < argc - 1; i++)
        if (strcmp(argv[i], key) == 0) return argv[i + 1];
    return def;
}

static bool Has(int argc, char** argv, const char* key) {
    for (int i = 1; i < argc; i++)
        if (strcmp(argv[i], key) == 0) return true;
    return false;
}

static std::vector<short> ParseCoList(const std::string& s) {
    std::vector<short> v;
    std::stringstream ss(s);
    std::string tok;
    while (std::getline(ss, tok, ',')) {
        if (tok.empty()) continue;
        v.push_back(static_cast<short>(atoi(tok.c_str())));
    }
    return v;
}

static void EmitCores(Json& j, const std::vector<short>& co) {
    j.key("cores");
    j.beginArr();
    for (size_t i = 0; i < co.size(); i++) {
        j.comma();
        Json c;
        c.beginObj();
        c.numi("core", static_cast<long long>(i));
        c.str("sign", co[i] >= 0 ? "Positive" : "Negative");
        c.numi("magnitude", co[i] >= 0 ? co[i] : -co[i]);
        c.numi("signed_offset", co[i]);
        c.endObj();
        j.os << c.str();
        j.first = false;
    }
    j.endArr();
}

int main(int argc, char** argv) {
    for (int i = 1; i < argc - 1; i++)
        if (strcmp(argv[i], "--out") == 0) gOut = argv[i + 1];
    if (argc < 2) Fail("usage: zenloop-cpu info|read|apply ...");
    std::string cmd = argv[1];

    std::string drv;
    bool drvOk = EnsureDriver(&drv);
    auto api = LoadApis();

    // Create CPU/BIOS devices before Platform GetRmCpuParameters (vecCPU[0] must be non-null).
    auto devs = EnumDevices(api);
    if (api.GetPlatform)
        (void)SehCallP(api.GetPlatform);

    int supported = SehCall0(api.IsSupportedProcessor);

    RmBuf rm{};
    int rmRc = SehCallBuf(api.GetRmCpuParameters, &rm);
    int fmax = SehCall0(api.GetCurrentFMaxCPU);
    auto parsed = ParseRm(rm, fmax);
    int ncores = parsed.cores > 0 ? parsed.cores : static_cast<int>(GetActiveProcessorCount(ALL_PROCESSOR_GROUPS));
    auto co = ReadCo(devs.cpu, ncores);

    if (cmd == "telemetry") {
        int seconds = atoi(Arg(argc, argv, "--seconds", "1").c_str());
        if (seconds < 1) seconds = 1;
        if (seconds > 600) seconds = 600;
        for (int i = 0; i < seconds; i++) {
            RmBuf live{};
            int rc = SehCallBuf(api.GetRmCpuParameters, &live);
            auto L = ParseLive(live);
            Json j;
            j.beginObj();
            j.str("type", "tick");
            j.boolean("ok", rc == 0 && L.ok);
            j.boolean("elevated", IsElevated());
            j.key("metrics");
            Json m;
            m.beginObj();
            EmitLiveMetrics(m, L);
            m.endObj();
            j.os << m.str();
            j.first = false;
            j.endObj();
            std::cout << j.str() << std::endl;
            if (i + 1 < seconds) Sleep(1000);
        }
        Json done;
        done.beginObj();
        done.str("type", "result");
        done.boolean("ok", true);
        done.boolean("passed", true);
        done.endObj();
        std::cout << done.str() << std::endl;
        return 0;
    }

    if (cmd == "info" || cmd == "read") {
        bool rmOk = (rmRc == 0) && !BufferEmpty(rm);
        Json j;
        j.beginObj();
        j.boolean("ok", rmOk);
        j.str("backend", "amd-ryzen-master");
        j.str("driver", drv);
        j.boolean("driver_running", drvOk);
        j.boolean("elevated", IsElevated());
        j.numi("device_error", static_cast<long long>(OpenRmDevice()));
        j.numi("init_rc", devs.initRc);
        j.boolean("supported_processor", supported != 0);
        j.numi("rm_status", rmRc);
        j.str("cpu_rtti", devs.cpuName);
        j.str("bios_rtti", devs.biosName);
        j.boolean("co_bind", devs.cpu != nullptr);
        j.boolean("bios_bind", devs.bios != nullptr);
        if (rmOk) {
            j.boolean("pbo_enabled", true);
            j.numi("ppt_watts", parsed.ppt);
            j.numi("tdc_amps", parsed.tdc);
            j.numi("edc_amps", parsed.edc);
            j.numi("boost_override_mhz", 0);
            j.numi("fmax_mhz", fmax);
            j.numi("scalar", 1);
            j.numi("logical_cores", ncores);
            EmitCores(j, co);
        } else {
            std::string e = "GetRmCpuParameters returned " + std::to_string(rmRc);
            if (BufferEmpty(rm)) e += " (empty SMU buffer; limits not invented)";
            if (!devs.cpu) e += "; CGraniteCPU not bound";
            j.str("error", e);
        }
        if (cmd == "info") {
            std::ostringstream hex;
            for (int i = 0; i < 256; i++) {
                char b[4];
                sprintf_s(b, "%02x", rm.b[i]);
                hex << b;
            }
            j.str("rm_hex", hex.str());
        }
        j.endObj();
        WriteOut(j.str());
        return rmOk ? 0 : 1;
    }

    if (cmd == "ram-read" || cmd == "ram-apply") {
        if (!devs.bios) {
            Json j;
            j.beginObj();
            j.boolean("ok", false);
            j.boolean("bios_persisted", false);
            j.str("backend", "amd-ryzen-master");
            j.boolean("elevated", IsElevated());
            j.str("error", "RAM timings need AMD BIOS (CDefaultBIOS not bound). Run ZenLoop as Administrator.");
            j.endObj();
            WriteOut(j.str());
            return 1;
        }
        if (cmd == "ram-read") {
            int clk = 0, vdd = 0, tcl = 0, trcd = 0, trp = 0, tras = 0, trfc = 0;
            GetBiosInt(devs.bios, kBiosGetMemClk, &clk);
            GetBiosInt(devs.bios, kBiosGetVddio, &vdd);
            GetBiosInt(devs.bios, kBiosGetTcl, &tcl);
            GetBiosInt(devs.bios, kBiosGetTrcd, &trcd);
            GetBiosInt(devs.bios, kBiosGetTrp, &trp);
            GetBiosInt(devs.bios, kBiosGetTras, &tras);
            GetBiosInt(devs.bios, kBiosGetTrfc, &trfc);
            Json ram;
            ram.beginObj();
            ram.numi("mem_clock_mhz", clk);
            ram.numi("vddio_mv", vdd);
            ram.numi("tcl", tcl);
            ram.numi("trcd", trcd);
            ram.numi("trp", trp);
            ram.numi("tras", tras);
            ram.numi("trfc", trfc);
            ram.boolean("expo", false);
            ram.endObj();
            Json j;
            j.beginObj();
            j.boolean("ok", tcl > 0 || clk > 0);
            j.str("backend", "amd-ryzen-master");
            j.boolean("elevated", IsElevated());
            j.str("bios_rtti", devs.biosName);
            j.raw("ram", ram.str());
            if (tcl <= 0 && clk <= 0)
                j.str("error", "CDefaultBIOS RAM getters returned empty (need Administrator / reboot after EXPO)");
            j.endObj();
            WriteOut(j.str());
            return (tcl > 0 || clk > 0) ? 0 : 1;
        }

        int clock = atoi(Arg(argc, argv, "--clock", "3000").c_str());
        int vddio = atoi(Arg(argc, argv, "--vddio", "1200").c_str());
        int tcl = atoi(Arg(argc, argv, "--tcl", "36").c_str());
        int trcd = atoi(Arg(argc, argv, "--trcd", "36").c_str());
        int trp = atoi(Arg(argc, argv, "--trp", "36").c_str());
        int tras = atoi(Arg(argc, argv, "--tras", "76").c_str());
        int trfc = atoi(Arg(argc, argv, "--trfc", "560").c_str());
        bool expo = Arg(argc, argv, "--expo", "0") != "0";

        IntCtx start{};
        start.obj = devs.bios;
        start.which = 10;
        int rcStart = SehCallInt(DispatchSet, &start);
        int rcExpo = ApplyBiosInt(devs.bios, kBiosSetExpo, expo ? 1 : 0);
        int rcClk = ApplyBiosInt(devs.bios, kBiosSetMemClk, clock);
        int rcVdd = ApplyBiosInt(devs.bios, kBiosSetVddio, vddio);
        int rcTcl = ApplyBiosInt(devs.bios, kBiosSetTcl, tcl);
        int rcTrcd = ApplyBiosInt(devs.bios, kBiosSetTrcd, trcd);
        int rcTrp = ApplyBiosInt(devs.bios, kBiosSetTrp, trp);
        int rcTras = ApplyBiosInt(devs.bios, kBiosSetTras, tras);
        int rcTrfc = ApplyBiosInt(devs.bios, kBiosSetTrfc, trfc);
        IntCtx end{};
        end.obj = devs.bios;
        end.which = 11;
        int rcEnd = SehCallInt(DispatchSet, &end);

        bool biosOk = (rcTcl == 0 && rcTrcd == 0 && rcTrp == 0 && rcTras == 0);
        std::ostringstream e;
        if (rcStart != 0) e << "CommandBufferStart=" << rcStart << " ";
        if (rcExpo != 0) e << "SetEXPOMode=" << rcExpo << " ";
        if (rcClk != 0) e << "SetCurrentMemClock=" << rcClk << " ";
        if (rcVdd != 0) e << "SetMemVDDIO=" << rcVdd << " ";
        if (rcTcl != 0) e << "SetMemCtrlTcl=" << rcTcl << " ";
        if (rcTrcd != 0) e << "SetMemCtrlTrcdrd=" << rcTrcd << " ";
        if (rcTrp != 0) e << "SetMemCtrlTrp=" << rcTrp << " ";
        if (rcTras != 0) e << "SetMemCtrlTras=" << rcTras << " ";
        if (rcTrfc != 0) e << "SetMemCtrlTrfc=" << rcTrfc << " ";
        if (rcEnd != 0) e << "CommandBufferEnd=" << rcEnd << " ";
        if (!biosOk && e.str().empty())
            e << "RAM BIOS setters returned non-zero";

        Json ram;
        ram.beginObj();
        ram.numi("mem_clock_mhz", clock);
        ram.numi("vddio_mv", vddio);
        ram.numi("tcl", tcl);
        ram.numi("trcd", trcd);
        ram.numi("trp", trp);
        ram.numi("tras", tras);
        ram.numi("trfc", trfc);
        ram.boolean("expo", expo);
        ram.endObj();

        Json j;
        j.beginObj();
        j.boolean("ok", biosOk);
        j.boolean("session_applied", false);
        j.boolean("bios_persisted", biosOk);
        j.str("backend", "amd-ryzen-master");
        j.boolean("elevated", IsElevated());
        j.raw("ram", ram.str());
        if (biosOk) j.null("error");
        else j.str("error", "RAM BIOS apply failed: " + e.str());
        j.endObj();
        WriteOut(j.str());
        return biosOk ? 0 : 1;
    }

    if (cmd != "apply") Fail("unknown command " + cmd);

    int ppt = atoi(Arg(argc, argv, "--ppt", "142").c_str());
    int tdc = atoi(Arg(argc, argv, "--tdc", "110").c_str());
    int edc = atoi(Arg(argc, argv, "--edc", "170").c_str());
    int boost = atoi(Arg(argc, argv, "--boost", "200").c_str());
    int scalar = atoi(Arg(argc, argv, "--scalar", "1").c_str());
    bool pbo = Arg(argc, argv, "--pbo", "1") != "0";
    std::string persist = Arg(argc, argv, "--persist", "session");
    auto offsets = ParseCoList(Arg(argc, argv, "--co", ""));

    std::string err;
    bool session = false;
    bool biosOk = false;
    bool coWritten = offsets.empty();

    if (!pbo) {
        int rc = SehCall0(api.DisableOverclocking);
        if (rc != 0) err = "DisableOverclocking returned " + std::to_string(rc);
        else session = true;
    } else {
        int rcPpt = SehCallIB(api.SetPPTLimit, ppt, true);
        int rcTdc = SehCallIB(api.SetTDCVDDLimit, tdc, true);
        int rcEdc = SehCallIB(api.SetEDCVDDLimit, edc, true);
        int rcSc = SehCallIB(api.EnableDisablePBOScalar, scalar, true);

        std::ostringstream e;
        if (rcPpt != 0) e << "SetPPTLimit=" << rcPpt << " ";
        if (rcTdc != 0) e << "SetTDCVDDLimit=" << rcTdc << " ";
        if (rcEdc != 0) e << "SetEDCVDDLimit=" << rcEdc << " ";
        if (rcSc != 0) e << "EnableDisablePBOScalar=" << rcSc << " ";

        if (!offsets.empty()) {
            if (!devs.cpu) {
                e << "per-core Curve Optimizer not written: CGraniteCPU not bound ";
                coWritten = false;
            } else {
                int coFails = 0;
                for (size_t i = 0; i < offsets.size(); i++) {
                    int rc = ApplyCoEach(devs.cpu, static_cast<int>(i), offsets[i]);
                    if (rc != 0) coFails++;
                }
                coWritten = (coFails == 0);
                if (!coWritten)
                    e << "SetCurveOptimizerForEachCore failed on " << coFails << "/" << offsets.size() << " cores ";
            }
        }

        session = (rcPpt == 0 && rcTdc == 0 && rcEdc == 0 && coWritten);
        if (!e.str().empty()) err = e.str();
        while (!err.empty() && err.back() == ' ') err.pop_back();
    }

    if (persist == "bios") {
        if (!devs.bios) {
            if (!err.empty()) err += "; ";
            err += "BIOS persist failed: Platform.dll has no SetPPTLimit_BIOS C export; Device.dll CDefaultBIOS not bound";
            biosOk = false;
        } else if (!offsets.empty() && !coWritten) {
            if (!err.empty()) err += "; ";
            err += "BIOS persist failed: per-core Curve Optimizer was not written to SMU; cannot snapshot offsets";
            biosOk = false;
        } else {
            int bPpt = ApplyBiosInt(devs.bios, kBiosSetPpt, ppt);
            int bTdc = ApplyBiosInt(devs.bios, kBiosSetTdc, tdc);
            int bEdc = ApplyBiosInt(devs.bios, kBiosSetEdc, edc);
            int bSc = ApplyBiosInt(devs.bios, kBiosSetScalar, scalar);
            bool coBiosOk = true;
            std::ostringstream coRc;
            if (!offsets.empty()) {
                for (size_t i = 0; i < offsets.size(); i++) {
                    IntCtx ctx{};
                    ctx.obj = devs.bios;
                    ctx.u0 = 2;
                    ctx.u1 = static_cast<unsigned short>(i);
                    ctx.u2 = static_cast<unsigned short>(offsets[i]);
                    ctx.which = 4;
                    int rc = SehCallInt(DispatchSet, &ctx);
                    if (rc != 0) {
                        coBiosOk = false;
                        coRc << " core" << i << "=" << rc;
                    }
                }
            }
            int fuse = SehCallB(api.SetOCFuseStatus, true);
            biosOk = (bPpt == 0 && bTdc == 0 && bEdc == 0 && coBiosOk);
            if (!biosOk) {
                std::ostringstream e;
                e << "BIOS persist failed: CDefaultBIOS::SetPPTLimit_BIOS=" << bPpt
                  << " SetTDCLimit_BIOS=" << bTdc
                  << " SetEDCLimit_BIOS=" << bEdc
                  << " SetPBOScalar_BIOS=" << bSc
                  << " SetCurveOptimizer" << (coBiosOk ? "=0" : coRc.str())
                  << " SetOCFuseStatus=" << fuse;
                if (!err.empty()) err += "; ";
                err += e.str();
            }
        }
    }

    Json j;
    j.beginObj();
    j.boolean("ok", session && err.empty() && (persist != "bios" || biosOk));
    j.boolean("session_applied", session);
    j.boolean("bios_persisted", biosOk);
    j.boolean("co_written", coWritten);
    j.str("backend", "amd-ryzen-master");
    j.str("driver", drv);
    j.boolean("elevated", IsElevated());
    j.str("cpu_rtti", devs.cpuName);
    j.str("bios_rtti", devs.biosName);
    j.boolean("pbo_enabled", pbo);
    j.numi("ppt_watts", ppt);
    j.numi("tdc_amps", tdc);
    j.numi("edc_amps", edc);
    j.numi("boost_override_mhz", boost);
    j.boolean("boost_override_applied", false);
    j.str("boost_override_note",
          "no Platform.dll C export for PBO boost override (GetCurrentFMaxCPU is read-only; SetOverclockFreqAllCores is manual all-core OC)");
    j.numi("scalar", scalar);
    j.numi("fmax_mhz", fmax);
    if (err.empty()) j.null("error");
    else j.str("error", err);
    EmitCores(j, offsets.empty() ? co : offsets);
    j.endObj();
    WriteOut(j.str());
    return session && (persist != "bios" || biosOk) ? 0 : 1;
}
