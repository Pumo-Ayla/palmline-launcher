#include <d3d12.h>
#include <nvsdk_ngx.h>
#include <nvsdk_ngx_helpers_d3d.h>
#include <mutex>

// The CGBJ caller retains the command list and all four resources through its
// submission fence. Input textures are compute-readable and output is a UAV.
struct DlssFrame {
    ID3D12Resource *color, *depth, *motion, *output;
    unsigned width, height, outputWidth, outputHeight;
    float jitterX, jitterY, motionScaleX, motionScaleY, exposure, milliseconds;
    unsigned reset, hdr, invertedDepth, jitteredMotion;
};

namespace {
std::mutex gate;
ID3D12Device* device;
NVSDK_NGX_Parameter* parameters;
NVSDK_NGX_Handle* feature;
unsigned inputWidth, inputHeight, outputWidth, outputHeight;
int creationFlags;
}

extern "C" __declspec(dllexport) unsigned CgbjDlssInit(ID3D12Device* host,
        const wchar_t* pluginDirectory, const wchar_t* dataDirectory) {
    std::lock_guard lock(gate);
    if (device || !host) return NVSDK_NGX_Result_FAIL_InvalidParameter;
    NVSDK_NGX_FeatureCommonInfo info{};
    info.PathListInfo.Path = &pluginDirectory;
    info.PathListInfo.Length = 1;
    auto result = NVSDK_NGX_D3D12_Init_with_ProjectID("10a062ff-941c-4c08-85fb-21f30602895f",
        NVSDK_NGX_ENGINE_TYPE_CUSTOM, "CGBJ-f4b48ad", dataDirectory, host, &info);
    if (NVSDK_NGX_FAILED(result)) return result;
    result = NVSDK_NGX_D3D12_GetCapabilityParameters(&parameters);
    int supported = 0;
    if (NVSDK_NGX_SUCCEED(result)) result = parameters->Get(NVSDK_NGX_Parameter_SuperSampling_Available, &supported);
    if (NVSDK_NGX_FAILED(result) || !supported) {
        if (parameters) NVSDK_NGX_D3D12_DestroyParameters(parameters);
        parameters = nullptr;
        NVSDK_NGX_D3D12_Shutdown1(host);
        return NVSDK_NGX_FAILED(result) ? result : NVSDK_NGX_Result_FAIL_FeatureNotSupported;
    }
    device = host;
    return result;
}

extern "C" __declspec(dllexport) unsigned CgbjDlssEvaluate(ID3D12GraphicsCommandList* list, const DlssFrame* f) {
    std::lock_guard lock(gate);
    if (!device || !list || !f || !f->color || !f->depth || !f->motion || !f->output ||
        !f->width || !f->height || !f->outputWidth || !f->outputHeight)
        return NVSDK_NGX_Result_FAIL_InvalidParameter;
    const int flags = NVSDK_NGX_DLSS_Feature_Flags_MVLowRes | NVSDK_NGX_DLSS_Feature_Flags_AutoExposure |
        (f->hdr ? NVSDK_NGX_DLSS_Feature_Flags_IsHDR : 0) |
        (f->invertedDepth ? NVSDK_NGX_DLSS_Feature_Flags_DepthInverted : 0) |
        (f->jitteredMotion ? NVSDK_NGX_DLSS_Feature_Flags_MVJittered : 0);
    // A feature can still be in flight. Fall back to the title's upscaler on
    // size/convention changes until the host has drained and recreated it.
    if (feature && (inputWidth != f->width || inputHeight != f->height || outputWidth != f->outputWidth ||
                    outputHeight != f->outputHeight || creationFlags != flags)) return NVSDK_NGX_Result_FAIL_InvalidParameter;
    bool first = !feature;
    if (first) {
        NVSDK_NGX_DLSS_Create_Params create{};
        create.Feature.InWidth = f->width;
        create.Feature.InHeight = f->height;
        create.Feature.InTargetWidth = f->outputWidth;
        create.Feature.InTargetHeight = f->outputHeight;
        wchar_t mode[16]{};
        GetEnvironmentVariableW(L"PALMLINE_DLSS_QUALITY",mode,16);
        create.Feature.InPerfQualityValue = wcscmp(mode,L"balanced")==0 ? NVSDK_NGX_PerfQuality_Value_Balanced :
            wcscmp(mode,L"performance")==0 ? NVSDK_NGX_PerfQuality_Value_MaxPerf : NVSDK_NGX_PerfQuality_Value_MaxQuality;
        create.InFeatureCreateFlags = flags;
        if (create.Feature.InPerfQualityValue == NVSDK_NGX_PerfQuality_Value_MaxQuality)
            parameters->Set(NVSDK_NGX_Parameter_DLSS_Hint_Render_Preset_Quality, unsigned(NVSDK_NGX_DLSS_Hint_Render_Preset_M));
        auto result = NGX_D3D12_CREATE_DLSS_EXT(list, 1, 1, &feature, parameters, &create);
        if (NVSDK_NGX_FAILED(result)) return result;
        inputWidth = f->width;
        inputHeight = f->height;
        outputWidth = f->outputWidth;
        outputHeight = f->outputHeight;
        creationFlags = flags;
    }
    NVSDK_NGX_D3D12_DLSS_Eval_Params eval{};
    eval.Feature.pInColor = f->color;
    eval.Feature.pInOutput = f->output;
    eval.pInDepth = f->depth;
    eval.pInMotionVectors = f->motion;
    eval.InRenderSubrectDimensions = { f->width, f->height };
    eval.InJitterOffsetX = f->jitterX;
    eval.InJitterOffsetY = f->jitterY;
    eval.InMVScaleX = f->motionScaleX;
    eval.InMVScaleY = f->motionScaleY;
    eval.InReset = first || f->reset;
    eval.InPreExposure = f->exposure;
    eval.InFrameTimeDeltaInMsec = f->milliseconds;
    D3D12_RESOURCE_BARRIER barrier{};
    barrier.Type = D3D12_RESOURCE_BARRIER_TYPE_UAV;
    barrier.UAV.pResource = f->output;
    list->ResourceBarrier(1, &barrier);
    auto result = NGX_D3D12_EVALUATE_DLSS_EXT(list, feature, parameters, &eval);
    list->ResourceBarrier(1, &barrier);
    return result;
}

// Only after the caller has drained its GPU queue.
extern "C" __declspec(dllexport) void CgbjDlssShutdown() {
    std::lock_guard lock(gate);
    if (!device) return;
    if (feature) NVSDK_NGX_D3D12_ReleaseFeature(feature);
    NVSDK_NGX_D3D12_DestroyParameters(parameters);
    NVSDK_NGX_D3D12_Shutdown1(device);
    feature = nullptr;
    parameters = nullptr;
    device = nullptr;
}
