// libwalldepth.so: runs the pinned depth network on the Hexagon NPU through QNN.
//
// Load path (deviation D1): an offline-compiled HTP context binary, not a DLC. No on-device
// graph preparation, so init is faster and has fewer failure points. Tensor metadata comes
// from QnnSystem's binary-info parser and is copied out, so the binary (tens of MB) is freed
// after load.
#include "walldepth.h"
#include "tensor_convert.h"

#include <android/log.h>
#include <dlfcn.h>

#include <chrono>
#include <condition_variable>
#include <cstdarg>
#include <cstdio>
#include <cstdlib>
#include <cstring>
#include <fstream>
#include <mutex>
#include <string>
#include <thread>
#include <vector>

#include "QnnInterface.h"
#include "System/QnnSystemInterface.h"
#include "HTP/QnnHtpDevice.h"
#include "HTP/QnnHtpPerfInfrastructure.h"

#define LOGI(...) __android_log_print(ANDROID_LOG_INFO, "walldepth", __VA_ARGS__)
#define LOGE(...) __android_log_print(ANDROID_LOG_ERROR, "walldepth", __VA_ARGS__)

namespace {

constexpr int kSize = 518;

typedef Qnn_ErrorHandle_t (*GetProvidersFn)(const QnnInterface_t*** providers, uint32_t* count);
typedef Qnn_ErrorHandle_t (*GetSystemProvidersFn)(const QnnSystemInterface_t*** providers, uint32_t* count);

// A graph tensor copied out of the binary's metadata. The QNN tensor view points into this
// struct's own members, so an OwnedTensor must never be copied or moved after Build().
struct OwnedTensor {
    uint32_t id = 0;
    std::string name;
    Qnn_TensorType_t type{};
    Qnn_TensorDataFormat_t dataFormat{};
    Qnn_DataType_t dataType{};
    Qnn_QuantizeParams_t quant{};     // scale/offset only (checked), so the copy owns no pointers
    std::vector<uint32_t> dims;
    std::vector<uint8_t> buffer;
    Qnn_Tensor_t tensor{};
    wd::DType dtype = wd::DType::F32;
    wd::Quant q;

    OwnedTensor() = default;
    OwnedTensor(const OwnedTensor&) = delete;
    OwnedTensor& operator=(const OwnedTensor&) = delete;

    size_t Count() const { size_t n = 1; for (uint32_t d : dims) n *= d; return n; }
};

enum class State { Idle, Pending, Done, Failed };

struct Engine {
    void* htpLib = nullptr;
    void* sysLib = nullptr;
    QNN_INTERFACE_VER_TYPE qnn{};
    Qnn_LogHandle_t log = nullptr;
    Qnn_BackendHandle_t backend = nullptr;
    Qnn_DeviceHandle_t device = nullptr;
    Qnn_ContextHandle_t context = nullptr;
    Qnn_GraphHandle_t graph = nullptr;
    OwnedTensor input, output;
    wd::Layout layout = wd::Layout::NHWC;
    bool burst = false;
    std::string description;

    std::vector<uint8_t> staging;   // copy of the caller's RGB, so the caller may reuse its buffer
    std::vector<float> result;
    double lastMs = 0;

    std::thread worker;
    std::mutex m;
    std::condition_variable cv;
    State state = State::Idle;
    bool stop = false;
    std::string error;
};

Engine* g = nullptr;
std::mutex g_life;          // serialises wd_init against wd_shutdown
std::string g_lastError;    // init errors, before an Engine exists

void CopyOut(const std::string& s, char* buf, int len)
{
    if (!buf || len <= 0) return;
    std::snprintf(buf, static_cast<size_t>(len), "%s", s.c_str());
}

void QnnLog(const char* fmt, QnnLog_Level_t level, uint64_t /*timestamp*/, va_list args)
{
    int prio = level == QNN_LOG_LEVEL_ERROR ? ANDROID_LOG_ERROR : level == QNN_LOG_LEVEL_WARN ? ANDROID_LOG_WARN : ANDROID_LOG_INFO;
    __android_log_vprint(prio, "walldepth-qnn", fmt, args);
}

// ---- version-tolerant accessors (see the evidence note in the plan) ----

template <class F> bool VisitTensor(const Qnn_Tensor_t& t, F&& f)
{
    switch (t.version) {
        case QNN_TENSOR_VERSION_1: f(t.v1); return true;
        case QNN_TENSOR_VERSION_2: f(t.v2); return true;
        default: return false;
    }
}

template <class F> bool VisitGraphs(const QnnSystemContext_BinaryInfo_t* info, F&& f)
{
    switch (info->version) {
        case QNN_SYSTEM_CONTEXT_BINARY_INFO_VERSION_1: f(info->contextBinaryInfoV1.numGraphs, info->contextBinaryInfoV1.graphs); return true;
        case QNN_SYSTEM_CONTEXT_BINARY_INFO_VERSION_2: f(info->contextBinaryInfoV2.numGraphs, info->contextBinaryInfoV2.graphs); return true;
        case QNN_SYSTEM_CONTEXT_BINARY_INFO_VERSION_3: f(info->contextBinaryInfoV3.numGraphs, info->contextBinaryInfoV3.graphs); return true;
        default: return false;
    }
}

template <class F> bool VisitGraphInfo(const QnnSystemContext_GraphInfo_t& gi, F&& f)
{
    switch (gi.version) {
        case QNN_SYSTEM_CONTEXT_GRAPH_INFO_VERSION_1: f(gi.graphInfoV1); return true;
        case QNN_SYSTEM_CONTEXT_GRAPH_INFO_VERSION_2: f(gi.graphInfoV2); return true;
        case QNN_SYSTEM_CONTEXT_GRAPH_INFO_VERSION_3: f(gi.graphInfoV3); return true;
        default: return false;
    }
}

bool CopyTensor(const Qnn_Tensor_t& src, OwnedTensor& dst, std::string& err)
{
    bool known = VisitTensor(src, [&](const auto& t) {
        dst.id = t.id;
        dst.name = t.name ? t.name : "";
        dst.type = t.type;
        dst.dataFormat = t.dataFormat;
        dst.dataType = t.dataType;
        dst.quant = t.quantizeParams;
        dst.dims.assign(t.dimensions, t.dimensions + t.rank);
    });
    if (!known) { err = "unsupported Qnn_Tensor_t version " + std::to_string(src.version); return false; }

    switch (dst.dataType) {
        case QNN_DATATYPE_FLOAT_32: dst.dtype = wd::DType::F32; break;
        case QNN_DATATYPE_FLOAT_16: dst.dtype = wd::DType::F16; break;
        case QNN_DATATYPE_UFIXED_POINT_8: dst.dtype = wd::DType::U8; break;
        case QNN_DATATYPE_UFIXED_POINT_16: dst.dtype = wd::DType::U16; break;
        default:
            err = dst.name + ": unsupported data type " + std::to_string(static_cast<int>(dst.dataType));
            return false;
    }
    if (wd::IsQuantised(dst.dtype)) {
        // Per-channel encodings would need per-channel arrays; this model (Task 7) uses one scale.
        if (dst.quant.encodingDefinition != QNN_DEFINITION_DEFINED
            || dst.quant.quantizationEncoding != QNN_QUANTIZATION_ENCODING_SCALE_OFFSET) {
            err = dst.name + ": only per-tensor scale/offset quantisation is supported";
            return false;
        }
        dst.q.scale = dst.quant.scaleOffsetEncoding.scale;
        dst.q.offset = dst.quant.scaleOffsetEncoding.offset;
    }
    dst.buffer.assign(dst.Count() * wd::BytesPer(dst.dtype), 0);

    // Version 1 tensors are accepted by graphExecute in every SDK release.
    std::memset(&dst.tensor, 0, sizeof dst.tensor);
    dst.tensor.version = QNN_TENSOR_VERSION_1;
    Qnn_TensorV1_t& v = dst.tensor.v1;
    v.id = dst.id;
    v.name = dst.name.c_str();
    v.type = dst.type;
    v.dataFormat = dst.dataFormat;
    v.dataType = dst.dataType;
    v.quantizeParams = dst.quant;
    v.rank = static_cast<uint32_t>(dst.dims.size());
    v.dimensions = dst.dims.data();
    v.memType = QNN_TENSORMEMTYPE_RAW;
    v.clientBuf.data = dst.buffer.data();
    v.clientBuf.dataSize = static_cast<uint32_t>(dst.buffer.size());
    return true;
}

const char* TypeName(wd::DType t)
{
    switch (t) { case wd::DType::F32: return "f32"; case wd::DType::F16: return "f16"; case wd::DType::U8: return "u8"; default: return "u16"; }
}

std::string Describe(const OwnedTensor& t)
{
    std::string dims;
    for (size_t i = 0; i < t.dims.size(); ++i) dims += (i ? "x" : "") + std::to_string(t.dims[i]);
    char q[96] = "";
    if (wd::IsQuantised(t.dtype)) std::snprintf(q, sizeof q, " scale=%g offset=%d", t.q.scale, t.q.offset);
    return t.name + " [" + dims + "] " + TypeName(t.dtype) + q;
}

// Ask the HTP for its highest clocks and no sleep (QNN's documented "burst" settings), the
// profile Phase 1 measured with qnn-net-run. Failure only costs speed, so it is not fatal.
bool SetBurst(Engine& e)
{
    QnnDevice_Infrastructure_t infra = nullptr;
    if (!e.qnn.deviceGetInfrastructure || e.qnn.deviceGetInfrastructure(&infra) != QNN_SUCCESS || !infra) return false;
    auto* htp = static_cast<QnnHtpDevice_Infrastructure_t*>(infra);
    if (htp->infraType != QNN_HTP_DEVICE_INFRASTRUCTURE_TYPE_PERF) return false;
    QnnHtpDevice_PerfInfrastructure_t& perf = htp->perfInfra;
    uint32_t id = 0;
    if (perf.createPowerConfigId(0, 0, &id) != QNN_SUCCESS) return false;

    QnnHtpPerfInfrastructure_PowerConfig_t c;
    std::memset(&c, 0, sizeof c);
    c.option = QNN_HTP_PERF_INFRASTRUCTURE_POWER_CONFIGOPTION_DCVS_V3;
    auto& d = c.dcvsV3Config;
    d.contextId = id;
    d.setDcvsEnable = 1;
    d.dcvsEnable = 0;
    d.powerMode = QNN_HTP_PERF_INFRASTRUCTURE_POWERMODE_PERFORMANCE_MODE;
    d.setSleepLatency = 1;
    d.sleepLatency = 40;
    d.setSleepDisable = 1;
    d.sleepDisable = 1;
    d.setBusParams = 1;
    d.busVoltageCornerMin = d.busVoltageCornerTarget = d.busVoltageCornerMax = DCVS_VOLTAGE_VCORNER_MAX_VOLTAGE_CORNER;
    d.setCoreParams = 1;
    d.coreVoltageCornerMin = d.coreVoltageCornerTarget = d.coreVoltageCornerMax = DCVS_VOLTAGE_VCORNER_MAX_VOLTAGE_CORNER;
    const QnnHtpPerfInfrastructure_PowerConfig_t* configs[] = {&c, nullptr};
    return perf.setPowerConfig(id, configs) == QNN_SUCCESS;
}

void WorkerLoop(Engine* e)
{
    for (;;) {
        std::unique_lock<std::mutex> lk(e->m);
        e->cv.wait(lk, [&] { return e->stop || e->state == State::Pending; });
        if (e->stop) return;
        lk.unlock();

        // While Pending, wd_submit refuses new frames, so only this thread touches the buffers.
        auto t0 = std::chrono::steady_clock::now();
        bool ok = wd::PackInput(e->staging.data(), kSize, e->layout, e->input.dtype, e->input.q,
                                e->input.buffer.data(), e->input.buffer.size());
        auto t1 = std::chrono::steady_clock::now();
        Qnn_ErrorHandle_t rc = ok ? e->qnn.graphExecute(e->graph, &e->input.tensor, 1, &e->output.tensor, 1, nullptr, nullptr)
                                  : QNN_SUCCESS;
        auto t2 = std::chrono::steady_clock::now();
        ok = ok && rc == QNN_SUCCESS
             && wd::UnpackOutput(e->output.buffer.data(), e->result.size(), e->output.dtype, e->output.q, e->result.data());
        double packMs = std::chrono::duration<double, std::milli>(t1 - t0).count();
        double execMs = std::chrono::duration<double, std::milli>(t2 - t1).count();

        lk.lock();
        if (ok) {
            // The reported time is the NPU execute only: that is what the rate governor and the
            // Phase 1 gate are about. Packing is logged separately.
            e->lastMs = execMs;
            e->state = State::Done;
            LOGI("inference %.1f ms (pack %.1f ms)", execMs, packMs);
        } else {
            e->error = "graphExecute failed: QNN error " + std::to_string(static_cast<unsigned long>(rc));
            e->state = State::Failed;
            LOGE("%s", e->error.c_str());
        }
    }
}

void FreeEngine(Engine* e)
{
    if (e->worker.joinable()) {
        { std::lock_guard<std::mutex> lk(e->m); e->stop = true; }
        e->cv.notify_all();
        e->worker.join();
    }
    if (e->context && e->qnn.contextFree) e->qnn.contextFree(e->context, nullptr);
    if (e->device && e->qnn.deviceFree) e->qnn.deviceFree(e->device);
    if (e->backend && e->qnn.backendFree) e->qnn.backendFree(e->backend);
    if (e->log && e->qnn.logFree) e->qnn.logFree(e->log);
    if (e->sysLib) dlclose(e->sysLib);
    if (e->htpLib) dlclose(e->htpLib);
    delete e;
}

bool Init(Engine& e, const char* contextPath, const char* libDir, std::string& err)
{
    // The HTP stub hands the skeleton library to the DSP; FastRPC looks for it on this path.
    std::string adsp = std::string(libDir) + ";/vendor/lib/rfsa/adsp;/vendor/dsp/cdsp;/system/lib/rfsa/adsp;/dsp";
    setenv("ADSP_LIBRARY_PATH", adsp.c_str(), 1);

    e.htpLib = dlopen("libQnnHtp.so", RTLD_NOW | RTLD_LOCAL);
    if (!e.htpLib) { err = std::string("dlopen libQnnHtp.so: ") + dlerror(); return false; }
    auto getProviders = reinterpret_cast<GetProvidersFn>(dlsym(e.htpLib, "QnnInterface_getProviders"));
    const QnnInterface_t** providers = nullptr;
    uint32_t n = 0;
    if (!getProviders || getProviders(&providers, &n) != QNN_SUCCESS || n == 0) { err = "QnnInterface_getProviders failed"; return false; }
    bool found = false;
    for (uint32_t i = 0; i < n && !found; ++i) {
        if (providers[i]->apiVersion.coreApiVersion.major == QNN_API_VERSION_MAJOR
            && providers[i]->apiVersion.coreApiVersion.minor >= QNN_API_VERSION_MINOR) {
            e.qnn = providers[i]->QNN_INTERFACE_VER_NAME;
            found = true;
        }
    }
    if (!found) { err = "libQnnHtp.so API version does not match the headers this plugin was built with"; return false; }

    e.sysLib = dlopen("libQnnSystem.so", RTLD_NOW | RTLD_LOCAL);
    if (!e.sysLib) { err = std::string("dlopen libQnnSystem.so: ") + dlerror(); return false; }
    auto getSys = reinterpret_cast<GetSystemProvidersFn>(dlsym(e.sysLib, "QnnSystemInterface_getProviders"));
    const QnnSystemInterface_t** sysProviders = nullptr;
    if (!getSys || getSys(&sysProviders, &n) != QNN_SUCCESS || n == 0) { err = "QnnSystemInterface_getProviders failed"; return false; }
    QNN_SYSTEM_INTERFACE_VER_TYPE sys{};
    found = false;
    for (uint32_t i = 0; i < n && !found; ++i) {
        if (sysProviders[i]->systemApiVersion.major == QNN_SYSTEM_API_VERSION_MAJOR
            && sysProviders[i]->systemApiVersion.minor >= QNN_SYSTEM_API_VERSION_MINOR) {
            sys = sysProviders[i]->QNN_SYSTEM_INTERFACE_VER_NAME;
            found = true;
        }
    }
    if (!found) { err = "libQnnSystem.so API version does not match the headers"; return false; }

    std::vector<uint8_t> blob;
    {
        std::ifstream f(contextPath, std::ios::binary | std::ios::ate);
        if (!f) { err = std::string("cannot open ") + contextPath; return false; }
        blob.resize(static_cast<size_t>(f.tellg()));
        f.seekg(0);
        if (!f.read(reinterpret_cast<char*>(blob.data()), static_cast<std::streamsize>(blob.size()))) { err = "cannot read the context binary"; return false; }
    }

    // Tensor metadata from the binary, copied out before the system context is freed.
    QnnSystemContext_Handle_t sysCtx = nullptr;
    if (sys.systemContextCreate(&sysCtx) != QNN_SUCCESS) { err = "systemContextCreate failed"; return false; }
    const QnnSystemContext_BinaryInfo_t* info = nullptr;
    Qnn_ContextBinarySize_t infoSize = 0;
    std::string graphName;
    bool metaOk = sys.systemContextGetBinaryInfo(sysCtx, blob.data(), blob.size(), &info, &infoSize) == QNN_SUCCESS && info;
    if (!metaOk) err = "systemContextGetBinaryInfo failed (is this a context binary for this SDK?)";
    if (metaOk && !VisitGraphs(info, [&](uint32_t numGraphs, const QnnSystemContext_GraphInfo_t* graphs) {
            if (numGraphs != 1) { err = "expected 1 graph, found " + std::to_string(numGraphs); metaOk = false; return; }
            if (!VisitGraphInfo(graphs[0], [&](const auto& gi) {
                    graphName = gi.graphName ? gi.graphName : "";
                    if (gi.numGraphInputs != 1 || gi.numGraphOutputs != 1) {
                        err = "expected 1 input and 1 output";
                        metaOk = false;
                        return;
                    }
                    metaOk = CopyTensor(gi.graphInputs[0], e.input, err) && CopyTensor(gi.graphOutputs[0], e.output, err);
                })) {
                err = "unsupported graph info version " + std::to_string(graphs[0].version);
                metaOk = false;
            }
        })) {
        err = "unsupported binary info version " + std::to_string(info->version);
        metaOk = false;
    }
    sys.systemContextFree(sysCtx);
    if (!metaOk) return false;

    // Input must be 518x518x3 in NHWC or NCHW; output must hold one value per pixel.
    const auto& d = e.input.dims;
    if (d.size() == 4 && d[0] == 1 && d[1] == kSize && d[2] == kSize && d[3] == 3) e.layout = wd::Layout::NHWC;
    else if (d.size() == 4 && d[0] == 1 && d[1] == 3 && d[2] == kSize && d[3] == kSize) e.layout = wd::Layout::NCHW;
    else { err = "unexpected input shape: " + Describe(e.input); return false; }
    if (e.output.Count() != static_cast<size_t>(kSize) * kSize) { err = "unexpected output shape: " + Describe(e.output); return false; }

    if (e.qnn.logCreate) e.qnn.logCreate(QnnLog, QNN_LOG_LEVEL_WARN, &e.log);
    if (e.qnn.backendCreate(e.log, nullptr, &e.backend) != QNN_SUCCESS) { err = "backendCreate failed"; return false; }
    if (e.qnn.deviceCreate && e.qnn.deviceCreate(e.log, nullptr, &e.device) != QNN_SUCCESS) { err = "deviceCreate failed"; return false; }
    if (e.qnn.contextCreateFromBinary(e.backend, e.device, nullptr, blob.data(), blob.size(), &e.context, nullptr) != QNN_SUCCESS) {
        err = "contextCreateFromBinary failed (SDK/runtime version or SoC mismatch?)";
        return false;
    }
    if (e.qnn.graphRetrieve(e.context, graphName.c_str(), &e.graph) != QNN_SUCCESS) { err = "graphRetrieve failed for " + graphName; return false; }

    e.burst = SetBurst(e);
    if (!e.burst) LOGE("could not set HTP burst clocks; running at default clocks");
    e.staging.assign(static_cast<size_t>(kSize) * kSize * 3, 0);
    e.result.assign(e.output.Count(), 0.f);
    e.description = "graph " + graphName + "; in " + Describe(e.input) + (e.layout == wd::Layout::NHWC ? " NHWC" : " NCHW")
                    + "; out " + Describe(e.output) + "; burst " + (e.burst ? "on" : "off");
    LOGI("ready: %s", e.description.c_str());
    e.worker = std::thread(WorkerLoop, &e);
    return true;
}

}  // namespace

extern "C" {

int wd_init(const char* context_path, const char* native_lib_dir, char* err, int err_len)
{
    std::lock_guard<std::mutex> life(g_life);
    if (g) return 0;
    if (!context_path || !native_lib_dir) { g_lastError = "null path"; CopyOut(g_lastError, err, err_len); return -1; }
    auto* e = new Engine();
    std::string msg;
    if (!Init(*e, context_path, native_lib_dir, msg)) {
        LOGE("init failed: %s", msg.c_str());
        g_lastError = msg;
        CopyOut(msg, err, err_len);
        FreeEngine(e);
        return -1;
    }
    g = e;
    return 0;
}

int wd_input_size(void) { return g ? static_cast<int>(g->staging.size()) : 0; }
int wd_output_size(void) { return g ? static_cast<int>(g->result.size()) : 0; }

int wd_submit(const uint8_t* rgb, int len)
{
    Engine* e = g;
    if (!e) return -1;
    std::lock_guard<std::mutex> lk(e->m);
    if (e->state == State::Failed) return -1;
    if (e->state != State::Idle) return 0;
    if (!rgb || len != static_cast<int>(e->staging.size())) { e->error = "wd_submit: wrong input length"; e->state = State::Failed; return -1; }
    std::memcpy(e->staging.data(), rgb, static_cast<size_t>(len));
    e->state = State::Pending;
    e->cv.notify_one();
    return 1;
}

int wd_poll(float* out, int len, double* inference_ms)
{
    Engine* e = g;
    if (!e) return -1;
    std::lock_guard<std::mutex> lk(e->m);
    if (e->state == State::Failed) return -1;
    if (e->state != State::Done) return 0;
    if (!out || len != static_cast<int>(e->result.size())) { e->error = "wd_poll: wrong output length"; e->state = State::Failed; return -1; }
    std::memcpy(out, e->result.data(), e->result.size() * sizeof(float));
    if (inference_ms) *inference_ms = e->lastMs;
    e->state = State::Idle;
    return 1;
}

int wd_describe(char* buf, int len)
{
    std::string s = g ? g->description : "not initialised";
    CopyOut(s, buf, len);
    return static_cast<int>(s.size());
}

int wd_last_error(char* buf, int len)
{
    std::string s;
    if (g) { std::lock_guard<std::mutex> lk(g->m); s = g->error; }
    if (s.empty()) s = g_lastError;
    CopyOut(s, buf, len);
    return static_cast<int>(s.size());
}

void wd_shutdown(void)
{
    std::lock_guard<std::mutex> life(g_life);
    if (!g) return;
    FreeEngine(g);
    g = nullptr;
}

}  // extern "C"
