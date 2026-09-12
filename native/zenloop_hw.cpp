// ZenLoop hardware helper: AMD ADLX GPU tuning + CPU/GPU stress.
// Links against the ADLX SDK (headers only; amdadlx64.dll comes from Adrenalin).

#define NOMINMAX
#include <Windows.h>
#include <immintrin.h>
#include <algorithm>
#include <atomic>
#include <cmath>
#include <cstdint>
#include <cstring>
#include <iostream>
#include <sstream>
#include <string>
#include <thread>
#include <vector>

#include "SDK/ADLXHelper/Windows/Cpp/ADLXHelper.h"
#include "SDK/Include/IGPUAutoTuning.h"
#include "SDK/Include/IGPUManualFanTuning.h"
#include "SDK/Include/IGPUManualGFXTuning.h"
#include "SDK/Include/IGPUManualPowerTuning.h"
#include "SDK/Include/IGPUManualVRAMTuning.h"
#include "SDK/Include/IGPUTuning.h"
#include "SDK/Include/IPerformanceMonitoring.h"
#include "SDK/Include/IPerformanceMonitoring2.h"
#include "SDK/Include/ISystem3.h"

using namespace adlx;

static ADLXHelper g_adlx;

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
        default: o += c; break;
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
    void num(const char* k, double v) {
        key(k);
        if (!std::isfinite(v)) os << "null";
        else os << v;
    }
    void numi(const char* k, long long v) { key(k); os << v; }
    void boolean(const char* k, bool v) { key(k); os << (v ? "true" : "false"); }
    void null(const char* k) { key(k); os << "null"; }
    void raw(const char* k, const std::string& v) { key(k); os << v; }
    std::string str() const { return os.str(); }
};

static void Fail(const std::string& msg, int code = 1) {
    Json j;
    j.beginObj();
    j.boolean("ok", false);
    j.str("error", msg);
    j.endObj();
    std::cout << j.str() << std::endl;
    std::exit(code);
}

static bool Ok(ADLX_RESULT r) { return ADLX_SUCCEEDED(r); }

struct GpuCtx {
    IADLXGPUPtr gpu;
    IADLXGPUTuningServicesPtr tune;
    IADLXPerformanceMonitoringServicesPtr perf;
    adlx_uint index = 0;
    std::string name;
    ADLX_GPU_TYPE type = GPUTYPE_UNDEFINED;
    adlx_uint vramMB = 0;
};

static IADLXGPUPtr PickGpu(IADLXGPUListPtr gpus, int requested) {
    IADLXGPUPtr best;
    adlx_uint bestVram = 0;
    int i = 0;
    for (adlx_uint it = gpus->Begin(); it != gpus->End(); ++it, ++i) {
        IADLXGPUPtr g;
        if (!Ok(gpus->At(it, &g)) || g == nullptr) continue;
        if (requested >= 0 && i != requested) continue;
        ADLX_GPU_TYPE t = GPUTYPE_UNDEFINED;
        g->Type(&t);
        adlx_uint vram = 0;
        g->TotalVRAM(&vram);
        if (requested >= 0) return g;
        if (t == GPUTYPE_DISCRETE && vram >= bestVram) {
            bestVram = vram;
            best = g;
        }
    }
    if (best == nullptr) gpus->At(gpus->Begin(), &best);
    return best;
}

static GpuCtx InitGpu(int requestedIndex) {
    ADLX_RESULT r = g_adlx.Initialize();
    if (!Ok(r)) Fail("ADLX initialize failed. Install AMD Software (Adrenalin Edition) so amdadlx64.dll is available, then reboot and retry.");
    GpuCtx c;
    IADLXSystem* sys = g_adlx.GetSystemServices();
    if (!sys) Fail("ADLX system services unavailable");
    if (!Ok(sys->GetGPUTuningServices(&c.tune)) || c.tune == nullptr)
        Fail("GPU tuning services unavailable");
    sys->GetPerformanceMonitoringServices(&c.perf);
    IADLXGPUListPtr gpus;
    if (!Ok(sys->GetGPUs(&gpus)) || gpus == nullptr || gpus->Empty())
        Fail("No AMD GPU found");
    c.gpu = PickGpu(gpus, requestedIndex);
    if (c.gpu == nullptr) Fail("Failed to select GPU");
    const char* nm = nullptr;
    c.gpu->Name(&nm);
    c.name = nm ? nm : "AMD GPU";
    c.gpu->Type(&c.type);
    c.gpu->TotalVRAM(&c.vramMB);
    return c;
}

static std::string TypeName(ADLX_GPU_TYPE t) {
    if (t == GPUTYPE_DISCRETE) return "discrete";
    if (t == GPUTYPE_INTEGRATED) return "integrated";
    return "unknown";
}

static IADLXManualGraphicsTuning2Ptr GetGfx2(GpuCtx& c) {
    adlx_bool supported = false;
    c.tune->IsSupportedManualGFXTuning(c.gpu, &supported);
    if (!supported) return nullptr;
    IADLXInterfacePtr ifc;
    if (!Ok(c.tune->GetManualGFXTuning(c.gpu, &ifc)) || ifc == nullptr) return nullptr;
    IADLXManualGraphicsTuning2Ptr gfx2(ifc);
    return gfx2;
}

static IADLXManualVRAMTuning2Ptr GetVram2(GpuCtx& c) {
    adlx_bool supported = false;
    c.tune->IsSupportedManualVRAMTuning(c.gpu, &supported);
    if (!supported) return nullptr;
    IADLXInterfacePtr ifc;
    if (!Ok(c.tune->GetManualVRAMTuning(c.gpu, &ifc)) || ifc == nullptr) return nullptr;
    IADLXManualVRAMTuning2Ptr v(ifc);
    return v;
}

static IADLXManualPowerTuningPtr GetPower(GpuCtx& c) {
    adlx_bool supported = false;
    c.tune->IsSupportedManualPowerTuning(c.gpu, &supported);
    if (!supported) return nullptr;
    IADLXInterfacePtr ifc;
    if (!Ok(c.tune->GetManualPowerTuning(c.gpu, &ifc)) || ifc == nullptr) return nullptr;
    IADLXManualPowerTuningPtr p(ifc);
    return p;
}

static IADLXManualFanTuningPtr GetFan(GpuCtx& c) {
    adlx_bool supported = false;
    c.tune->IsSupportedManualFanTuning(c.gpu, &supported);
    if (!supported) return nullptr;
    IADLXInterfacePtr ifc;
    if (!Ok(c.tune->GetManualFanTuning(c.gpu, &ifc)) || ifc == nullptr) return nullptr;
    IADLXManualFanTuningPtr f(ifc);
    return f;
}

static std::string TimingName(ADLX_MEMORYTIMING_DESCRIPTION d) {
    switch (d) {
    case MEMORYTIMING_DEFAULT: return "default";
    case MEMORYTIMING_FAST_TIMING: return "fast";
    case MEMORYTIMING_FAST_TIMING_LEVEL_2: return "fast2";
    case MEMORYTIMING_AUTOMATIC: return "auto";
    default: return "other";
    }
}

static void FillRange(Json& j, const char* key, const ADLX_IntRange& r, bool valid) {
    if (!valid) {
        j.null(key);
        return;
    }
    j.key(key);
    Json inner;
    inner.beginObj();
    inner.numi("min", r.minValue);
    inner.numi("max", r.maxValue);
    inner.numi("step", r.step);
    inner.endObj();
    j.os << inner.str();
    j.first = false;
}

static void CollectTuning(GpuCtx& c, Json& j) {
    j.key("tuning");
    Json t;
    t.beginObj();
    Json ranges;
    ranges.beginObj();
    auto gfx = GetGfx2(c);
    if (gfx) {
        adlx_int v = 0;
        ADLX_IntRange rng{};
        if (Ok(gfx->GetGPUMinFrequency(&v))) t.numi("min_mhz", v);
        if (Ok(gfx->GetGPUMaxFrequency(&v))) t.numi("max_mhz", v);
        if (Ok(gfx->GetGPUVoltage(&v))) t.numi("voltage_mv", v);
        bool okR = Ok(gfx->GetGPUMinFrequencyRange(&rng));
        FillRange(ranges, "min_mhz", rng, okR);
        okR = Ok(gfx->GetGPUMaxFrequencyRange(&rng));
        FillRange(ranges, "max_mhz", rng, okR);
        okR = Ok(gfx->GetGPUVoltageRange(&rng));
        FillRange(ranges, "voltage_mv", rng, okR);
        IADLXManualGraphicsTuning2_1Ptr gfx21(gfx);
        if (gfx21) {
            if (Ok(gfx21->GetGPUMaxFrequencyDefault(&v))) t.numi("default_max_mhz", v);
            if (Ok(gfx21->GetGPUMinFrequencyDefault(&v))) t.numi("default_min_mhz", v);
            if (Ok(gfx21->GetGPUVoltageDefault(&v))) t.numi("default_voltage_mv", v);
        }
    }
    auto vram = GetVram2(c);
    if (vram) {
        adlx_int v = 0;
        ADLX_IntRange rng{};
        if (Ok(vram->GetMaxVRAMFrequency(&v))) t.numi("vram_mhz", v);
        ADLX_MEMORYTIMING_DESCRIPTION td = MEMORYTIMING_DEFAULT;
        if (Ok(vram->GetMemoryTimingDescription(&td))) t.str("memory_timing", TimingName(td));
        bool okR = Ok(vram->GetMaxVRAMFrequencyRange(&rng));
        FillRange(ranges, "vram_mhz", rng, okR);
    }
    auto pwr = GetPower(c);
    if (pwr) {
        adlx_int v = 0;
        ADLX_IntRange rng{};
        if (Ok(pwr->GetPowerLimit(&v))) t.numi("power_limit_pct", v);
        bool okR = Ok(pwr->GetPowerLimitRange(&rng));
        FillRange(ranges, "power_limit_pct", rng, okR);
    }
    ranges.endObj();
    t.raw("ranges", ranges.str());
    adlx_bool factory = false;
    c.tune->IsAtFactory(c.gpu, &factory);
    t.boolean("at_factory", factory);
    t.endObj();
    j.os << t.str();
    j.first = false;
}

static std::string FanJson(GpuCtx& c) {
    Json f;
    f.beginObj();
    auto fan = GetFan(c);
    if (!fan) {
        f.boolean("supported", false);
        f.endObj();
        return f.str();
    }
    f.boolean("supported", true);
    adlx_bool zero = false;
    if (Ok(fan->IsSupportedZeroRPM(&zero)) && Ok(fan->GetZeroRPMState(&zero)))
        f.boolean("zero_rpm", zero);
    ADLX_IntRange speedR{}, tempR{};
    if (Ok(fan->GetFanTuningRanges(&speedR, &tempR))) {
        FillRange(f, "speed_range", speedR, true);
        FillRange(f, "temp_range", tempR, true);
    }
    IADLXManualFanTuningStateListPtr states;
    if (Ok(fan->GetFanTuningStates(&states)) && states) {
        f.key("points");
        Json arr;
        arr.beginArr();
        for (adlx_uint i = states->Begin(); i != states->End(); ++i) {
            IADLXManualFanTuningStatePtr st;
            if (!Ok(states->At(i, &st)) || !st) continue;
            adlx_int speed = 0, temp = 0;
            st->GetFanSpeed(&speed);
            st->GetTemperature(&temp);
            if (!arr.first) arr.os << ",";
            arr.first = false;
            Json pt;
            pt.beginObj();
            pt.numi("temp", temp);
            pt.numi("speed", speed);
            pt.endObj();
            arr.os << pt.str();
        }
        arr.endArr();
        f.os << arr.str();
        f.first = false;
    }
    f.endObj();
    return f.str();
}

static ADLX_RESULT ApplyFan(GpuCtx& c, const std::vector<std::pair<int, int>>& points, int zeroRpm) {
    auto fan = GetFan(c);
    if (!fan) return ADLX_FAIL;
    ADLX_RESULT last = ADLX_OK;
    if (zeroRpm >= 0) {
        last = fan->SetZeroRPMState(zeroRpm != 0);
        if (last == ADLX_RESET_NEEDED) return last;
    }
    if (points.empty()) return last;
    IADLXManualFanTuningStateListPtr states;
    last = fan->GetEmptyFanTuningStates(&states);
    if (!Ok(last) || !states) last = fan->GetFanTuningStates(&states);
    if (!Ok(last) || !states) return last;
    adlx_uint n = states->Size();
    for (adlx_uint i = states->Begin(); i != states->End(); ++i) {
        IADLXManualFanTuningStatePtr st;
        if (!Ok(states->At(i, &st)) || !st) continue;
        size_t idx = (size_t)i;
        if (idx >= points.size()) idx = points.size() - 1;
        last = st->SetTemperature(points[idx].first);
        if (last == ADLX_RESET_NEEDED) return last;
        last = st->SetFanSpeed(points[idx].second);
        if (last == ADLX_RESET_NEEDED) return last;
        (void)n;
    }
    adlx_int errIdx = -1;
    last = fan->IsValidFanTuningStates(states, &errIdx);
    if (!Ok(last)) return last;
    return fan->SetFanTuningStates(states);
}

static void CollectMetrics(GpuCtx& c, Json& j) {
    j.key("metrics");
    Json m;
    m.beginObj();
    if (c.perf == nullptr) {
        m.endObj();
        j.os << m.str();
        j.first = false;
        return;
    }
    IADLXGPUMetricsPtr metrics;
    if (!Ok(c.perf->GetCurrentGPUMetrics(c.gpu, &metrics)) || metrics == nullptr) {
        m.endObj();
        j.os << m.str();
        j.first = false;
        return;
    }
    adlx_double d = 0;
    adlx_int i = 0;
    if (Ok(metrics->GPUTemperature(&d))) m.num("gpu_temp_c", d);
    if (Ok(metrics->GPUHotspotTemperature(&d))) m.num("hotspot_c", d);
    if (Ok(metrics->GPUPower(&d))) m.num("power_w", d);
    if (Ok(metrics->GPUTotalBoardPower(&d))) m.num("board_power_w", d);
    if (Ok(metrics->GPUUsage(&d))) m.num("usage_pct", d);
    if (Ok(metrics->GPUClockSpeed(&i))) m.numi("clock_mhz", i);
    if (Ok(metrics->GPUVRAMClockSpeed(&i))) m.numi("vram_clock_mhz", i);
    if (Ok(metrics->GPUFanSpeed(&i))) m.numi("fan_rpm", i);
    if (Ok(metrics->GPUVoltage(&i))) m.numi("voltage_mv", i);
    IADLXGPUMetrics1Ptr m1(metrics);
    if (m1) {
        if (Ok(m1->GPUMemoryTemperature(&d))) m.num("mem_temp_c", d);
    }
    IADLXSystemMetricsPtr sysm;
    if (Ok(c.perf->GetCurrentSystemMetrics(&sysm)) && sysm) {
        if (Ok(sysm->CPUUsage(&d))) m.num("cpu_usage_pct", d);
        adlx_int ramMb = 0;
        if (Ok(sysm->SystemRAM(&ramMb))) m.numi("system_ram_mb", ramMb);
    }
    m.endObj();
    j.os << m.str();
    j.first = false;
}

static void EmitSnapshot(GpuCtx& c, bool ok, const std::string& extraKey = {}, const std::string& extraVal = {}) {
    Json j;
    j.beginObj();
    j.boolean("ok", ok);
    j.key("gpu");
    Json g;
    g.beginObj();
    g.str("name", c.name);
    g.str("type", TypeName(c.type));
    g.numi("vram_mb", c.vramMB);
    g.endObj();
    j.os << g.str();
    j.first = false;
    CollectTuning(c, j);
    CollectMetrics(c, j);
    j.raw("fan", FanJson(c));
    if (!extraKey.empty()) j.raw(extraKey.c_str(), extraVal);
    j.endObj();
    std::cout << j.str() << std::endl;
}

static int ArgInt(int argc, char** argv, const char* name, int def) {
    std::string flag = std::string("--") + name;
    for (int i = 0; i < argc - 1; ++i) {
        if (flag == argv[i]) return std::stoi(argv[i + 1]);
    }
    return def;
}

static bool HasFlag(int argc, char** argv, const char* name) {
    std::string flag = std::string("--") + name;
    for (int i = 0; i < argc; ++i) if (flag == argv[i]) return true;
    return false;
}

static std::string ArgStr(int argc, char** argv, const char* name, const std::string& def) {
    std::string flag = std::string("--") + name;
    for (int i = 0; i < argc - 1; ++i) {
        if (flag == argv[i]) return argv[i + 1];
    }
    return def;
}

static std::vector<std::pair<int, int>> ParsePoints(const std::string& s);

static int CmdInfo(GpuCtx& c) {
    EmitSnapshot(c, true);
    return 0;
}

static int CmdReset(GpuCtx& c) {
    ADLX_RESULT r = c.tune->ResetToFactory(c.gpu);
    Sleep(200);
    EmitSnapshot(c, Ok(r), "reset", Ok(r) ? "true" : "false");
    return Ok(r) ? 0 : 2;
}

static int CmdSet(GpuCtx& c, int argc, char** argv) {
    auto gfx = GetGfx2(c);
    auto vram = GetVram2(c);
    auto pwr = GetPower(c);
    if (gfx == nullptr) Fail("Manual graphics tuning is not supported on this GPU");

    auto apply = [&]() -> ADLX_RESULT {
        ADLX_RESULT last = ADLX_OK;
        if (HasFlag(argc, argv, "min-mhz") && gfx) {
            last = gfx->SetGPUMinFrequency(ArgInt(argc, argv, "min-mhz", 0));
            if (last == ADLX_RESET_NEEDED) return last;
        }
        if (HasFlag(argc, argv, "max-mhz") && gfx) {
            last = gfx->SetGPUMaxFrequency(ArgInt(argc, argv, "max-mhz", 0));
            if (last == ADLX_RESET_NEEDED) return last;
        }
        if (HasFlag(argc, argv, "voltage") && gfx) {
            last = gfx->SetGPUVoltage(ArgInt(argc, argv, "voltage", 0));
            if (last == ADLX_RESET_NEEDED) return last;
        }
        if (HasFlag(argc, argv, "vram") && vram) {
            last = vram->SetMaxVRAMFrequency(ArgInt(argc, argv, "vram", 0));
            if (last == ADLX_RESET_NEEDED) return last;
        }
        if (HasFlag(argc, argv, "power") && pwr) {
            last = pwr->SetPowerLimit(ArgInt(argc, argv, "power", 0));
            if (last == ADLX_RESET_NEEDED) return last;
        }
        if (HasFlag(argc, argv, "fast-timing") && vram) {
            int ft = ArgInt(argc, argv, "fast-timing", 0);
            last = vram->SetMemoryTimingDescription(
                ft ? MEMORYTIMING_FAST_TIMING : MEMORYTIMING_DEFAULT);
            if (last == ADLX_RESET_NEEDED) return last;
        }
        if (HasFlag(argc, argv, "fan-points") || HasFlag(argc, argv, "zero-rpm")) {
            auto pts = ParsePoints(ArgStr(argc, argv, "fan-points", ""));
            int zr = HasFlag(argc, argv, "zero-rpm") ? ArgInt(argc, argv, "zero-rpm", 0) : -1;
            last = ApplyFan(c, pts, zr);
            if (last == ADLX_RESET_NEEDED) return last;
        }
        return last;
    };

    ADLX_RESULT r = apply();
    if (r == ADLX_RESET_NEEDED) {
        c.tune->ResetToFactory(c.gpu);
        Sleep(300);
        gfx = GetGfx2(c);
        vram = GetVram2(c);
        pwr = GetPower(c);
        r = apply();
    }
    EmitSnapshot(c, Ok(r), "applied", Ok(r) ? "true" : "false");
    return Ok(r) ? 0 : 2;
}

class AutoTuneListener : public IADLXGPUAutoTuningCompleteListener {
public:
    HANDLE ev = nullptr;
    std::atomic<bool> uv{false}, oc{false}, vram{false};
    adlx_bool ADLX_STD_CALL OnGPUAutoTuningComplete(IADLXGPUAutoTuningCompleteEvent* e) override {
        if (e) {
            uv = e->IsUndervoltGPUCompleted();
            oc = e->IsOverclockGPUCompleted();
            vram = e->IsOverclockVRAMCompleted();
        }
        if (ev) SetEvent(ev);
        return true;
    }
};

static int CmdAmdAuto(GpuCtx& c, const std::string& mode) {
    adlx_bool supported = false;
    if (!Ok(c.tune->IsSupportedAutoTuning(c.gpu, &supported)) || !supported)
        Fail("AMD one-click auto-tuning is not supported on this GPU");
    IADLXInterfacePtr ifc;
    if (!Ok(c.tune->GetAutoTuning(c.gpu, &ifc)) || ifc == nullptr)
        Fail("GetAutoTuning failed");
    IADLXGPUAutoTuningPtr autoT(ifc);
    AutoTuneListener listener;
    listener.ev = CreateEventW(nullptr, TRUE, FALSE, nullptr);
    adlx_bool modeOk = false;
    ADLX_RESULT r = ADLX_FAIL;
    if (mode == "undervolt") {
        autoT->IsSupportedUndervoltGPU(&modeOk);
        if (!modeOk) Fail("Undervolt profile not supported");
        r = autoT->StartUndervoltGPU(&listener);
    } else if (mode == "overclock") {
        autoT->IsSupportedOverclockGPU(&modeOk);
        if (!modeOk) Fail("Overclock profile not supported");
        r = autoT->StartOverclockGPU(&listener);
    } else if (mode == "vram") {
        autoT->IsSupportedOverclockVRAM(&modeOk);
        if (!modeOk) Fail("VRAM overclock profile not supported");
        r = autoT->StartOverclockVRAM(&listener);
    } else {
        Fail("Unknown auto mode");
    }
    if (r == ADLX_RESET_NEEDED) {
        c.tune->ResetToFactory(c.gpu);
        Sleep(300);
        ResetEvent(listener.ev);
        if (mode == "undervolt") r = autoT->StartUndervoltGPU(&listener);
        else if (mode == "overclock") r = autoT->StartOverclockGPU(&listener);
        else r = autoT->StartOverclockVRAM(&listener);
    }
    WaitForSingleObject(listener.ev, 180000);
    CloseHandle(listener.ev);
    Sleep(400);
    Json extra;
    extra.beginObj();
    extra.boolean("started", Ok(r));
    extra.boolean("undervolt_done", listener.uv.load());
    extra.boolean("overclock_done", listener.oc.load());
    extra.boolean("vram_done", listener.vram.load());
    extra.endObj();
    EmitSnapshot(c, Ok(r), "amd_auto", extra.str());
    return Ok(r) ? 0 : 2;
}

class GpuStressListener : public IADLXGPUStressTestFinishedListener {
public:
    HANDLE ev = nullptr;
    std::atomic<bool> passed{false};
    std::atomic<bool> got{false};
    adlx_bool ADLX_STD_CALL OnGPUStressTestFinished(IADLXGPU3*, adlx_bool result) override {
        passed = result;
        got = true;
        if (ev) SetEvent(ev);
        return true;
    }
};

// Minimal OpenCL GPU burner (loads OpenCL.dll at runtime).
typedef int32_t cl_int;
typedef uint32_t cl_uint;
typedef int64_t cl_long;
typedef uint64_t cl_ulong;
typedef void* cl_platform_id;
typedef void* cl_device_id;
typedef void* cl_context;
typedef void* cl_command_queue;
typedef void* cl_mem;
typedef void* cl_program;
typedef void* cl_kernel;
typedef void* cl_event;
#define CL_SUCCESS 0
#define CL_DEVICE_TYPE_GPU (1u << 2)
#define CL_MEM_READ_WRITE (1u << 0)
#define CL_MEM_COPY_HOST_PTR (1u << 5)

static bool OpenClBurn(int seconds, double* throughput, std::string* err) {
    HMODULE h = LoadLibraryW(L"OpenCL.dll");
    if (!h) {
        *err = "OpenCL.dll not found";
        return false;
    }
#define L(name, sig) auto name = reinterpret_cast<sig>(GetProcAddress(h, #name)); if (!name) { *err = "missing " #name; return false; }
    L(clGetPlatformIDs, cl_int(*)(cl_uint, cl_platform_id*, cl_uint*));
    L(clGetDeviceIDs, cl_int(*)(cl_platform_id, cl_uint, cl_uint, cl_device_id*, cl_uint*));
    L(clGetDeviceInfo, cl_int(*)(cl_device_id, cl_uint, size_t, void*, size_t*));
    L(clCreateContext, cl_context(*)(const void*, cl_uint, const cl_device_id*, void*, void*, cl_int*));
    L(clCreateCommandQueue, cl_command_queue(*)(cl_context, cl_device_id, cl_long, cl_int*));
    L(clCreateBuffer, cl_mem(*)(cl_context, cl_long, size_t, void*, cl_int*));
    L(clCreateProgramWithSource, cl_program(*)(cl_context, cl_uint, const char**, const size_t*, cl_int*));
    L(clBuildProgram, cl_int(*)(cl_program, cl_uint, const cl_device_id*, const char*, void*, void*));
    L(clCreateKernel, cl_kernel(*)(cl_program, const char*, cl_int*));
    L(clSetKernelArg, cl_int(*)(cl_kernel, cl_uint, size_t, const void*));
    L(clEnqueueNDRangeKernel, cl_int(*)(cl_command_queue, cl_kernel, cl_uint, const size_t*, const size_t*, const size_t*, cl_uint, const cl_event*, cl_event*));
    L(clFinish, cl_int(*)(cl_command_queue));
    L(clEnqueueReadBuffer, cl_int(*)(cl_command_queue, cl_mem, cl_uint, size_t, size_t, void*, cl_uint, const cl_event*, cl_event*));
    L(clReleaseKernel, cl_int(*)(cl_kernel));
    L(clReleaseProgram, cl_int(*)(cl_program));
    L(clReleaseMemObject, cl_int(*)(cl_mem));
    L(clReleaseCommandQueue, cl_int(*)(cl_command_queue));
    L(clReleaseContext, cl_int(*)(cl_context));
#undef L
    cl_uint nplat = 0;
    if (clGetPlatformIDs(0, nullptr, &nplat) != CL_SUCCESS || nplat == 0) {
        *err = "no OpenCL platforms";
        return false;
    }
    std::vector<cl_platform_id> plats(nplat);
    clGetPlatformIDs(nplat, plats.data(), nullptr);
    cl_device_id dev = nullptr;
    cl_ulong bestMem = 0;
    for (auto p : plats) {
        cl_uint ndev = 0;
        if (clGetDeviceIDs(p, CL_DEVICE_TYPE_GPU, 0, nullptr, &ndev) != CL_SUCCESS) continue;
        std::vector<cl_device_id> devs(ndev);
        clGetDeviceIDs(p, CL_DEVICE_TYPE_GPU, ndev, devs.data(), nullptr);
        for (auto d : devs) {
            cl_ulong mem = 0;
            clGetDeviceInfo(d, 0x101F /*CL_DEVICE_GLOBAL_MEM_SIZE*/, sizeof(mem), &mem, nullptr);
            char name[256]{};
            clGetDeviceInfo(d, 0x102B /*CL_DEVICE_NAME*/, sizeof(name), name, nullptr);
            if (std::string(name).find("Radeon(TM)") != std::string::npos && mem < 2ull << 30) continue;
            if (mem >= bestMem) {
                bestMem = mem;
                dev = d;
            }
        }
    }
    if (!dev) {
        *err = "no suitable OpenCL GPU";
        return false;
    }
    cl_int errc = 0;
    cl_context ctx = clCreateContext(nullptr, 1, &dev, nullptr, nullptr, &errc);
    cl_command_queue q = clCreateCommandQueue(ctx, dev, 0, &errc);
    const size_t n = 8ull * 1024ull * 1024ull;
    std::vector<float> host(n, 1.0f);
    cl_mem buf = clCreateBuffer(ctx, CL_MEM_READ_WRITE | CL_MEM_COPY_HOST_PTR, n * sizeof(float), host.data(), &errc);
    const char* src =
        "__kernel void burn(__global float* a, int iters){"
        "int i=get_global_id(0); float x=a[i];"
        "for(int k=0;k<iters;++k){ x = mad(x, 1.000000119f, 0.000000119f); x = native_rsqrt(x*x+1e-8f); }"
        "a[i]=x;}";
    cl_program prog = clCreateProgramWithSource(ctx, 1, &src, nullptr, &errc);
    if (clBuildProgram(prog, 1, &dev, "", nullptr, nullptr) != CL_SUCCESS) {
        *err = "OpenCL build failed";
        return false;
    }
    cl_kernel k = clCreateKernel(prog, "burn", &errc);
    int iters = 64;
    clSetKernelArg(k, 0, sizeof(cl_mem), &buf);
    clSetKernelArg(k, 1, sizeof(int), &iters);
    size_t g = n;
    ULONGLONG t0 = GetTickCount64();
    long long launches = 0;
    while (GetTickCount64() - t0 < (ULONGLONG)seconds * 1000ull) {
        if (clEnqueueNDRangeKernel(q, k, 1, nullptr, &g, nullptr, 0, nullptr, nullptr) != CL_SUCCESS) {
            *err = "OpenCL enqueue failed";
            return false;
        }
        ++launches;
        if ((launches & 7) == 0) clFinish(q);
    }
    clFinish(q);
    float probe = 0;
    clEnqueueReadBuffer(q, buf, 1, 0, sizeof(float), &probe, 0, nullptr, nullptr);
    clReleaseKernel(k);
    clReleaseProgram(prog);
    clReleaseMemObject(buf);
    clReleaseCommandQueue(q);
    clReleaseContext(ctx);
    *throughput = (double)launches * (double)n * (double)iters / (double)seconds;
    (void)probe;
    return true;
}

static void EmitTick(GpuCtx& c, const char* kind, int elapsed, int total) {
    Json j;
    j.beginObj();
    j.str("type", "tick");
    j.str("kind", kind);
    j.numi("elapsed_s", elapsed);
    j.numi("total_s", total);
    CollectMetrics(c, j);
    j.endObj();
    std::cout << j.str() << std::endl;
}

static int CmdStressGpu(GpuCtx& c, int seconds) {
    seconds = std::max(5, seconds);
    if (c.perf) {
        (void)c.perf->ClearPerformanceMetricsHistory();
        (void)c.perf->StartPerformanceMetricsTracking();
    }
    GpuStressListener listener;
    listener.ev = CreateEventW(nullptr, TRUE, FALSE, nullptr);
    IADLXGPU3Ptr gpu3;
    bool usedAdlx = false;
    if (Ok(c.gpu->QueryInterface(IADLXGPU3::IID(), (void**)&gpu3)) && gpu3) {
        adlx_bool sup = false;
        if (Ok(gpu3->IsSupportedStressTest(&sup)) && sup) {
            ADLX_RESULT r = gpu3->StartStressTest(&listener, (adlx_uint)seconds);
            usedAdlx = Ok(r);
        }
    }
    std::string oclErr;
    double thr = 0;
    std::thread ocl;
    std::atomic<bool> oclOk{false};
    std::atomic<bool> oclDone{false};
    if (!usedAdlx) {
        ocl = std::thread([&]() {
            oclOk = OpenClBurn(seconds, &thr, &oclErr);
            oclDone = true;
        });
    }
    ULONGLONG t0 = GetTickCount64();
    while (true) {
        int elapsed = (int)((GetTickCount64() - t0) / 1000ull);
        EmitTick(c, usedAdlx ? "adlx" : "opencl", elapsed, seconds);
        if (usedAdlx) {
            if (WaitForSingleObject(listener.ev, 1000) == WAIT_OBJECT_0) break;
            if (elapsed > seconds + 20) break;
        } else {
            if (oclDone) break;
            Sleep(1000);
        }
    }
    if (ocl.joinable()) ocl.join();
    if (listener.ev) CloseHandle(listener.ev);
    bool passed = usedAdlx ? listener.passed.load() : oclOk.load();
    Json j;
    j.beginObj();
    j.str("type", "result");
    j.boolean("ok", true);
    j.boolean("passed", passed);
    j.boolean("adlx_stress", usedAdlx);
    j.num("throughput", thr);
    if (!oclErr.empty() && !usedAdlx) j.str("opencl_error", oclErr);
    CollectMetrics(c, j);
    j.endObj();
    std::cout << j.str() << std::endl;
    if (c.perf) (void)c.perf->StopPerformanceMetricsTracking();
    return passed ? 0 : 3;
}

static std::atomic<uint64_t> g_iters{0};
static std::atomic<uint64_t> g_errors{0};
static std::atomic<bool> g_stop{false};

typedef LONG (WINAPI *FnCallNtPowerInformation)(int, void*, ULONG, void*, ULONG);
struct ProcPowerInfo {
    ULONG Number;
    ULONG MaxMhz;
    ULONG CurrentMhz;
    ULONG MhzLimit;
    ULONG MaxIdleState;
    ULONG CurrentIdleState;
};

static double AvgCurrentMhz() {
    HMODULE h = GetModuleHandleW(L"powrprof.dll");
    if (!h) h = LoadLibraryW(L"powrprof.dll");
    if (!h) return 0;
    auto fn = reinterpret_cast<FnCallNtPowerInformation>(GetProcAddress(h, "CallNtPowerInformation"));
    if (!fn) return 0;
    SYSTEM_INFO si{};
    GetSystemInfo(&si);
    int n = (int)si.dwNumberOfProcessors;
    if (n < 1) return 0;
    if (n > 256) n = 256;
    std::vector<ProcPowerInfo> buf((size_t)n);
    // ProcessorInformation = 11
    if (fn(11, nullptr, 0, buf.data(), (ULONG)(n * (int)sizeof(ProcPowerInfo))) != 0) return 0;
    double s = 0;
    int k = 0;
    for (int i = 0; i < n; i++) {
        ULONG mhz = buf[i].CurrentMhz;
        if (mhz > 200 && mhz < 10000) {
            s += (double)mhz;
            k++;
        }
    }
    return k ? s / k : 0;
}

static double CpuUsagePct() {
    FILETIME idle{}, kernel{}, user{};
    if (!GetSystemTimes(&idle, &kernel, &user)) return -1;
    ULARGE_INTEGER i, k, u;
    i.LowPart = idle.dwLowDateTime; i.HighPart = idle.dwHighDateTime;
    k.LowPart = kernel.dwLowDateTime; k.HighPart = kernel.dwHighDateTime;
    u.LowPart = user.dwLowDateTime; u.HighPart = user.dwHighDateTime;
    static ULARGE_INTEGER pi{}, pk{}, pu{};
    static bool have = false;
    if (!have) {
        pi = i; pk = k; pu = u; have = true;
        return -1;
    }
    unsigned long long dIdle = i.QuadPart - pi.QuadPart;
    unsigned long long dKern = k.QuadPart - pk.QuadPart;
    unsigned long long dUser = u.QuadPart - pu.QuadPart;
    pi = i; pk = k; pu = u;
    unsigned long long tot = dKern + dUser;
    if (tot == 0) return 0;
    double busy = (double)(tot - dIdle) / (double)tot * 100.0;
    if (busy < 0) busy = 0;
    if (busy > 100) busy = 100;
    return busy;
}

static void CpuWorker(int core) {
    if (core >= 0) {
        DWORD_PTR mask = (DWORD_PTR)1 << core;
        SetThreadAffinityMask(GetCurrentThread(), mask);
    }
    alignas(32) float buf[1024];
    for (int i = 0; i < 1024; ++i) buf[i] = 1.0f + (i & 7) * 0.01f;
    uint64_t x = 0x9E3779B97F4A7C15ull ^ (uint64_t)(core + 1);
    while (!g_stop.load(std::memory_order_relaxed)) {
        __m256 acc = _mm256_set1_ps(1.000000119f);
        for (int i = 0; i < 1024; i += 8) {
            __m256 v = _mm256_load_ps(buf + i);
            acc = _mm256_fmadd_ps(v, acc, v);
            acc = _mm256_max_ps(acc, _mm256_set1_ps(-1.0e10f));
            acc = _mm256_min_ps(acc, _mm256_set1_ps(1.0e10f));
            _mm256_store_ps(buf + i, acc);
        }
        x ^= x << 13;
        x ^= x >> 7;
        x ^= x << 17;
        if (x == 0) g_errors.fetch_add(1, std::memory_order_relaxed);
        g_iters.fetch_add(1, std::memory_order_relaxed);
    }
}

static void RamWorker(size_t bytes) {
    std::vector<uint64_t> a(bytes / 8, 0xA5A5A5A5A5A5A5A5ull);
    std::vector<uint64_t> b(bytes / 8, 0);
    while (!g_stop.load(std::memory_order_relaxed)) {
        for (size_t i = 0; i < a.size(); ++i) b[i] = a[i] ^ (i * 0x9E3779B97F4A7C15ull);
        uint64_t cs = 0;
        for (size_t i = 0; i < b.size(); i += 16) cs ^= b[i];
        std::swap(a, b);
        g_iters.fetch_add(1, std::memory_order_relaxed);
        if (cs == 0) g_errors.fetch_add(1, std::memory_order_relaxed);
    }
}

static int CmdStressCpu(int seconds, const std::string& mode, int core) {
    seconds = std::max(3, seconds);
    SYSTEM_INFO si{};
    GetSystemInfo(&si);
    int ncpu = (int)si.dwNumberOfProcessors;
    g_stop = false;
    g_iters = 0;
    g_errors = 0;
    std::vector<std::thread> workers;
    if (mode == "ram") {
        workers.emplace_back(RamWorker, 256ull * 1024ull * 1024ull);
        workers.emplace_back(RamWorker, 256ull * 1024ull * 1024ull);
    } else if (mode == "core") {
        int c = (core < 0) ? 0 : core % ncpu;
        workers.emplace_back(CpuWorker, c);
        workers.emplace_back(CpuWorker, c);
    } else if (mode == "mix") {
        for (int i = 0; i < ncpu; ++i) workers.emplace_back(CpuWorker, i);
    } else {
        for (int i = 0; i < ncpu; ++i) workers.emplace_back(CpuWorker, i);
    }
    ULONGLONG t0 = GetTickCount64();
    while (GetTickCount64() - t0 < (ULONGLONG)seconds * 1000ull) {
        int elapsed = (int)((GetTickCount64() - t0) / 1000ull);
        Json j;
        j.beginObj();
        j.str("type", "tick");
        j.str("kind", "cpu");
        j.str("mode", mode);
        j.numi("elapsed_s", elapsed);
        j.numi("total_s", seconds);
        j.numi("iters", (long long)g_iters.load());
        j.numi("errors", (long long)g_errors.load());
        j.numi("threads", (long long)workers.size());
        j.numi("logical_cpus", ncpu);
        FILETIME idle{}, kernel{}, user{};
        if (GetSystemTimes(&idle, &kernel, &user)) {
            ULARGE_INTEGER k, u;
            k.LowPart = kernel.dwLowDateTime; k.HighPart = kernel.dwHighDateTime;
            u.LowPart = user.dwLowDateTime; u.HighPart = user.dwHighDateTime;
            j.numi("kernel_time", (long long)(k.QuadPart / 10000ull));
            j.numi("user_time", (long long)(u.QuadPart / 10000ull));
        }
        double mhz = AvgCurrentMhz();
        if (mhz > 0) j.num("cpu_clock_mhz", mhz);
        double usage = CpuUsagePct();
        if (usage >= 0) j.num("cpu_usage_pct", usage);
        j.key("metrics");
        Json m;
        m.beginObj();
        if (mhz > 0) m.num("cpu_clock_mhz", mhz);
        if (usage >= 0) m.num("cpu_usage_pct", usage);
        m.endObj();
        j.os << m.str();
        j.first = false;
        j.endObj();
        std::cout << j.str() << std::endl;
        Sleep(1000);
    }
    g_stop = true;
    for (auto& t : workers) t.join();
    bool passed = g_errors.load() == 0;
    Json j;
    j.beginObj();
    j.str("type", "result");
    j.boolean("ok", true);
    j.boolean("passed", passed);
    j.str("mode", mode);
    j.numi("iters", (long long)g_iters.load());
    j.numi("errors", (long long)g_errors.load());
    j.num("throughput", (double)g_iters.load() / (double)seconds);
    j.numi("logical_cpus", ncpu);
    j.endObj();
    std::cout << j.str() << std::endl;
    return passed ? 0 : 3;
}

static std::vector<std::pair<int, int>> ParsePoints(const std::string& s) {
    std::vector<std::pair<int, int>> out;
    std::stringstream ss(s);
    std::string item;
    while (std::getline(ss, item, ',')) {
        auto pos = item.find(':');
        if (pos == std::string::npos) continue;
        try {
            out.emplace_back(std::stoi(item.substr(0, pos)), std::stoi(item.substr(pos + 1)));
        } catch (...) {
        }
    }
    return out;
}

static int CmdFanSet(GpuCtx& c, int argc, char** argv) {
    auto pts = ParsePoints(ArgStr(argc, argv, "points", ArgStr(argc, argv, "fan-points", "")));
    int zr = HasFlag(argc, argv, "zero-rpm") ? ArgInt(argc, argv, "zero-rpm", 0) : -1;
    ADLX_RESULT r = ApplyFan(c, pts, zr);
    if (r == ADLX_RESET_NEEDED) {
        c.tune->ResetToFactory(c.gpu);
        Sleep(300);
        r = ApplyFan(c, pts, zr);
    }
    EmitSnapshot(c, Ok(r), "applied", Ok(r) ? "true" : "false");
    return Ok(r) ? 0 : 2;
}

static int CmdProfileExport(GpuCtx& c) {
    Json p;
    p.beginObj();
    auto gfx = GetGfx2(c);
    adlx_int v = 0;
    if (gfx) {
        if (Ok(gfx->GetGPUMinFrequency(&v))) p.numi("min_mhz", v);
        if (Ok(gfx->GetGPUMaxFrequency(&v))) p.numi("max_mhz", v);
        if (Ok(gfx->GetGPUVoltage(&v))) p.numi("voltage_mv", v);
    }
    auto vram = GetVram2(c);
    if (vram) {
        if (Ok(vram->GetMaxVRAMFrequency(&v))) p.numi("vram_mhz", v);
        ADLX_MEMORYTIMING_DESCRIPTION td = MEMORYTIMING_DEFAULT;
        if (Ok(vram->GetMemoryTimingDescription(&td)))
            p.boolean("fast_timing", td == MEMORYTIMING_FAST_TIMING || td == MEMORYTIMING_FAST_TIMING_LEVEL_2);
    }
    auto pwr = GetPower(c);
    if (pwr && Ok(pwr->GetPowerLimit(&v))) p.numi("power_pct", v);
    p.raw("fan", FanJson(c));
    p.endObj();
    EmitSnapshot(c, true, "profile", p.str());
    return 0;
}

static void Usage() {
    std::cerr <<
        "zenloop-hw <command> [options]\n"
        "  info | get | metrics | reset\n"
        "  set --max-mhz N --min-mhz N --voltage N --vram N --power N --fast-timing 0|1\n"
        "      --fan-points T:S,T:S --zero-rpm 0|1\n"
        "  fan-set --points T:S,T:S --zero-rpm 0|1\n"
        "  profile-export\n"
        "  auto-undervolt | auto-overclock | auto-vram\n"
        "  stress-gpu --seconds N\n"
        "  stress-cpu --seconds N --mode all|core|mix|ram --core N\n"
        "  --gpu N   select GPU index (default: discrete with most VRAM)\n";
}

int main(int argc, char** argv) {
    if (argc < 2) {
        Usage();
        Fail("missing command");
    }
    std::string cmd = argv[1];
    int gpuIndex = ArgInt(argc, argv, "gpu", -1);
    try {
        if (cmd == "stress-cpu") {
            return CmdStressCpu(ArgInt(argc, argv, "seconds", 30), ArgStr(argc, argv, "mode", "all"),
                                ArgInt(argc, argv, "core", -1));
        }
        GpuCtx c = InitGpu(gpuIndex);
        int rc = 0;
        if (cmd == "info" || cmd == "get") rc = CmdInfo(c);
        else if (cmd == "metrics") rc = CmdInfo(c);
        else if (cmd == "reset") rc = CmdReset(c);
        else if (cmd == "set") rc = CmdSet(c, argc, argv);
        else if (cmd == "fan-set") rc = CmdFanSet(c, argc, argv);
        else if (cmd == "profile-export") rc = CmdProfileExport(c);
        else if (cmd == "auto-undervolt") rc = CmdAmdAuto(c, "undervolt");
        else if (cmd == "auto-overclock") rc = CmdAmdAuto(c, "overclock");
        else if (cmd == "auto-vram") rc = CmdAmdAuto(c, "vram");
        else if (cmd == "stress-gpu") rc = CmdStressGpu(c, ArgInt(argc, argv, "seconds", 30));
        else {
            Usage();
            Fail("unknown command: " + cmd);
        }
        g_adlx.Terminate();
        return rc;
    } catch (const std::exception& ex) {
        Fail(ex.what());
    }
    return 1;
}
