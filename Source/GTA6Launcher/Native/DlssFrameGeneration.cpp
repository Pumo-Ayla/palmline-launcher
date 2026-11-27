#include <windows.h>
#include <dxgi1_6.h>
#include <d3d12.h>
#include <wrl/client.h>
#include <cmath>
#include <cstdio>
#include <mutex>
#include <filesystem>
#include "sl.h"
#include "sl_dlss_g.h"
#include "sl_dlss.h"
#include "sl_reflex.h"
#include "sl_matrix_helpers.h"
#include "sl_security.h"

using Microsoft::WRL::ComPtr;
namespace {
PFun_slInit* init;
PFun_slShutdown* shutdownSl;
PFun_slSetD3DDevice* setDevice;
PFun_slIsFeatureSupported* supported;
PFun_slUpgradeInterface* upgrade;
PFun_slGetFeatureFunction* featureFunction;
PFun_slGetNewFrameToken* newToken;
PFun_slSetConstants* constants;
PFun_slSetTagForFrame* tagFrame;
PFun_slDLSSGGetState* getState;
PFun_slDLSSGSetOptions* setOptions;
PFun_slDLSSSetOptions* srOptions;
PFun_slEvaluateFeature* evaluate;
PFun_slReflexSetOptions* reflexOptions;
PFun_slReflexSleep* reflexSleep;
PFun_slPCLSetMarker* marker;
const sl::ViewportHandle view(0);
sl::FrameToken* token;
unsigned maximum, active, presentIndex;
bool resetNext=true;
std::mutex frameLock;
bool srHasConstants=false;
unsigned srConstantsFrame=0;
sl::Constants lastSceneCamera{};
unsigned lastSceneFrame=0;
bool hasSceneCamera=false;

bool ok(sl::Result result, const char* call) {
    if (result == sl::Result::eOk) return true;
    if (result == sl::Result::eWarnOutOfVRAM) {
        static std::once_flag logged;
        std::call_once(logged,[call] {
            fprintf(stderr,"DLSS: Streamline VRAM budget exceeded during %s; performance may be reduced\n",call);
        });
        return true;
    }
    fprintf(stderr,"DLSS-G: %s failed (%d)\n",call,int(result));
    return false;
}
template<class T> bool load(HMODULE module, T*& fn, const char* name) {
    fn=reinterpret_cast<T*>(GetProcAddress(module,name));
    return fn!=nullptr;
}
template<class T> bool feature(sl::Feature id, T*& fn, const char* name) {
    void* address=nullptr;
    if (!ok(featureFunction(id,name,address),name)) return false;
    fn=reinterpret_cast<T*>(address);
    return fn!=nullptr;
}
bool mode(unsigned multiplier) {
    if(active==multiplier) return true;
    sl::DLSSGOptions options{};
    options.mode=multiplier ? sl::DLSSGMode::eOn : sl::DLSSGMode::eOff;
    options.numFramesToGenerate=multiplier ? multiplier-1 : 1;
    if(!ok(setOptions(view,options),"set options")) return false;
    active=multiplier;
    resetNext=true;
    fprintf(stderr,"DLSS-G: %s multiplier=%u\n",active?"enabled":"suspended",active);
    return true;
}
bool camera(const float* words, sl::Constants& c) {
    sl::float4x4 v,p,pv,pp,prevVP,currentVP,inverseVP;
    auto read=[&](sl::float4x4& dst,int offset) { memcpy(&dst,words+offset,sizeof(dst)); };
    read(v,0); read(p,16); read(pv,56); read(pp,72);
    for (auto offset:{0,16,32,56,72,88,104}) for(int i=0;i<16;++i)
        if(!std::isfinite(words[offset+i])) return false;
    sl::matrixMul(currentVP,v,p);
    sl::matrixMul(prevVP,pv,pp);
    for(int i=0;i<16;++i) {
        if(std::abs((&currentVP[0].x)[i]-words[88+i])>.002f ||
           std::abs((&prevVP[0].x)[i]-words[104+i])>.002f) return false;
    }
    for(auto proj:{&p,&pp}) {
        if((*proj)[0].x<=0 || (*proj)[1].y<=0 || std::abs((*proj)[2].w+1)>.001f ||
           std::abs((*proj)[2].x)>.02f || std::abs((*proj)[2].y)>.02f) return false;
        (*proj)[2].x=(*proj)[2].y=0; // Projection jitter is supplied separately in pixels.
    }
    for(auto mat:{&v,&pv}) for(int i=0;i<3;++i) for(int j=0;j<3;++j) {
        const auto& a=(*mat)[i]; const auto& b=(*mat)[j];
        if(std::abs(a.x*b.x+a.y*b.y+a.z*b.z-(i==j?1.f:0.f))>.005f) return false;
    }
    sl::matrixMul(currentVP,v,p); sl::matrixMul(prevVP,pv,pp);
    sl::matrixFullInvert(inverseVP,currentVP);
    sl::matrixMul(c.clipToPrevClip,inverseVP,prevVP);
    sl::matrixFullInvert(c.prevClipToClip,c.clipToPrevClip);
    c.cameraViewToClip=p;
    sl::matrixFullInvert(c.clipToCameraView,p);
    c.cameraRight={v[0].x,v[1].x,v[2].x};
    c.cameraUp={v[0].y,v[1].y,v[2].y};
    c.cameraFwd={-v[0].z,-v[1].z,-v[2].z};
    if(!std::isfinite(words[48]) || !std::isfinite(words[49]) || !std::isfinite(words[50])) return false;
    c.cameraPos={words[48],words[49],words[50]};
    c.cameraFOV=2*std::atan(1/p[1].y);
    c.cameraAspectRatio=p[1].y/p[0].x;
    return true;
}
}

extern "C" __declspec(dllexport) bool CgbjFgInit(const wchar_t* directory,const wchar_t* logs) {
    auto path=std::filesystem::path(directory)/L"sl.interposer.dll";
    if(!sl::security::verifyEmbeddedSignature(path.c_str())) return false;
    auto dll=LoadLibraryExW(path.c_str(),nullptr,LOAD_LIBRARY_SEARCH_DLL_LOAD_DIR|LOAD_LIBRARY_SEARCH_SYSTEM32);
    if(!dll || !load(dll,init,"slInit") || !load(dll,shutdownSl,"slShutdown") ||
       !load(dll,setDevice,"slSetD3DDevice") || !load(dll,supported,"slIsFeatureSupported") ||
       !load(dll,upgrade,"slUpgradeInterface") || !load(dll,featureFunction,"slGetFeatureFunction") ||
       !load(dll,newToken,"slGetNewFrameToken") || !load(dll,constants,"slSetConstants") ||
       !load(dll,tagFrame,"slSetTagForFrame") || !load(dll,evaluate,"slEvaluateFeature")) return false;
    sl::Preferences pref{};
    const sl::Feature features[]={sl::kFeatureDLSS_G,sl::kFeatureReflex,sl::kFeaturePCL,sl::kFeatureDLSS};
    pref.flags=sl::PreferenceFlags::eUseManualHooking|sl::PreferenceFlags::eUseDXGIFactoryProxy|
        sl::PreferenceFlags::eDisableCLStateTracking|sl::PreferenceFlags::eUseFrameBasedResourceTagging;
    pref.pathsToPlugins=&directory; pref.numPathsToPlugins=1; pref.pathToLogsAndData=logs;
    wchar_t sr[2]{};
    pref.featuresToLoad=features;
    pref.numFeaturesToLoad=GetEnvironmentVariableW(L"PALMLINE_DLSS_SR",sr,2)==1 && sr[0]==L'1' ? 4 : 3;
    pref.renderAPI=sl::RenderAPI::eD3D12; pref.engine=sl::EngineType::eCustom;
    pref.engineVersion="CGBJ-f4b48ad"; pref.projectId="10a062ff-941c-4c08-85fb-21f30602895f";
    return ok(init(pref,sl::kSDKVersion),"init");
}
extern "C" __declspec(dllexport) unsigned CgbjFgDevice(ID3D12Device* device) {
    auto luid=device->GetAdapterLuid();
    sl::AdapterInfo adapter{}; adapter.deviceLUID=(uint8_t*)&luid; adapter.deviceLUIDSizeInBytes=sizeof(luid);
    if(!ok(supported(sl::kFeatureDLSS_G,adapter),"device support") || !ok(setDevice(device),"set device") ||
       !feature(sl::kFeatureDLSS_G,getState,"slDLSSGGetState") || !feature(sl::kFeatureDLSS_G,setOptions,"slDLSSGSetOptions") ||
       !feature(sl::kFeatureReflex,reflexOptions,"slReflexSetOptions") ||
       !feature(sl::kFeatureReflex,reflexSleep,"slReflexSleep") || !feature(sl::kFeaturePCL,marker,"slPCLSetMarker")) return 0;
    sl::DLSSGState state{}; sl::ReflexOptions ro{}; ro.mode=sl::ReflexMode::eLowLatency;
    if(!ok(reflexOptions(ro),"Reflex") || !ok(getState(view,state,nullptr),"capabilities")) return 0;
    maximum=state.numFramesToGenerateMax ? state.numFramesToGenerateMax+1 : 0;
    if(supported(sl::kFeatureDLSS,adapter)==sl::Result::eOk)
        feature(sl::kFeatureDLSS,srOptions,"slDLSSSetOptions");
    fprintf(stderr,"DLSS-G: device maximum multiplier=%u\n",maximum);
    return maximum;
}
extern "C" __declspec(dllexport) IDXGIFactory2* CgbjFgFactory(IDXGIFactory2* native) {
    void* value=native;
    if(!maximum || !ok(upgrade(&value),"factory proxy") || value==native) return nullptr;
    return static_cast<IDXGIFactory2*>(value);
}
extern "C" __declspec(dllexport) bool CgbjFgBegin(unsigned frame) {
    std::lock_guard guard(frameLock);
    token=nullptr;
    if(!ok(newToken(token,&frame),"frame token") || !token) return false;
    if(active) reflexSleep(*token);
    return true;
}
// The caller retains both returned copies through its submission fence. Allocate
// the pair before recording anything so allocation failure leaves the source list untouched.
extern "C" __declspec(dllexport) bool CgbjFgCopyInputs(ID3D12Device* device,ID3D12GraphicsCommandList* list,
        ID3D12Resource* depth,ID3D12Resource* motion,ID3D12Resource** out) {
    ID3D12Resource* source[]={depth,motion};
    ComPtr<ID3D12Resource> copies[2];
    D3D12_HEAP_PROPERTIES heap{}; heap.Type=D3D12_HEAP_TYPE_DEFAULT; heap.CreationNodeMask=heap.VisibleNodeMask=1;
    for(int i=0;i<2;++i) {
        auto desc=source[i]->GetDesc(); desc.Flags=D3D12_RESOURCE_FLAG_NONE;
        if(FAILED(device->CreateCommittedResource(&heap,D3D12_HEAP_FLAG_NONE,&desc,
            D3D12_RESOURCE_STATE_COPY_DEST,nullptr,IID_PPV_ARGS(&copies[i])))) return false;
    }
    for(int i=0;i<2;++i) {
        D3D12_RESOURCE_BARRIER b{}; b.Type=D3D12_RESOURCE_BARRIER_TYPE_TRANSITION;
        b.Transition={source[i],D3D12_RESOURCE_BARRIER_ALL_SUBRESOURCES,
            D3D12_RESOURCE_STATE_NON_PIXEL_SHADER_RESOURCE,D3D12_RESOURCE_STATE_COPY_SOURCE};
        list->ResourceBarrier(1,&b); list->CopyResource(copies[i].Get(),source[i]);
        std::swap(b.Transition.StateBefore,b.Transition.StateAfter); list->ResourceBarrier(1,&b);
        b.Transition.pResource=copies[i].Get(); b.Transition.StateBefore=D3D12_RESOURCE_STATE_COPY_DEST;
        list->ResourceBarrier(1,&b); out[i]=copies[i].Detach();
    }
    return true;
}
extern "C" __declspec(dllexport) void CgbjFgMarker(unsigned stage) {
    std::lock_guard guard(frameLock);
    if(token && stage<=5) marker(static_cast<sl::PCLMarker>(stage),*token);
}
extern "C" __declspec(dllexport) bool CgbjFgPrepare(unsigned multiplier,const float* matrices,
        const float* temporal,unsigned flags,ID3D12Resource* depth,ID3D12Resource* motion,
        ID3D12Resource* scene,unsigned sceneState,ID3D12Resource* ui,unsigned uiState,
        ID3D12GraphicsCommandList* list) {
    std::lock_guard guard(frameLock);
    sl::Constants c{};
    if(!token || !matrices || !depth || !motion || !scene || !ui ||
       multiplier<2 || multiplier>maximum) { mode(0); return false; }
    if(!camera(matrices,c)) {
        static bool warned=false;
        if(!warned) fprintf(stderr,"DLSS-G: camera validation rejected view=%g projection=%g prev=%g\n",matrices[0],matrices[16],matrices[56]);
        warned=true; mode(0); return false;
    }
    lastSceneCamera=c;
    lastSceneFrame=unsigned(*token);
    hasSceneCamera=true;
    auto d=depth->GetDesc(), m=motion->GetDesc(), s=scene->GetDesc(), u=ui->GetDesc();
    static bool dimensionsLogged=false;
    if(!dimensionsLogged) fprintf(stderr,"DLSS-G: validated camera; depth=%llux%u motion=%llux%u scene=%llux%u ui=%llux%u\n",
        d.Width,d.Height,m.Width,m.Height,s.Width,s.Height,u.Width,u.Height);
    dimensionsLogged=true;
    if(d.Width!=m.Width || d.Height!=m.Height || !d.Width || !d.Height ||
       !(temporal[4]>0 && temporal[5]>temporal[4]) || !std::isfinite(temporal[5])) { mode(0); return false; }
    c.cameraNear=temporal[4]; c.cameraFar=temporal[5];
    c.cameraPinholeOffset={0,0};
    c.jitterOffset={temporal[0],temporal[1]}; c.mvecScale={temporal[2]/float(d.Width),temporal[3]/d.Height};
    c.depthInverted=(flags&8)?sl::Boolean::eTrue:sl::Boolean::eFalse;
    c.cameraMotionIncluded=sl::Boolean::eTrue; c.motionVectors3D=sl::Boolean::eFalse;
    c.motionVectorsJittered=(flags&4)?sl::Boolean::eTrue:sl::Boolean::eFalse;
    if(!mode(multiplier)) return false;
    c.reset=(resetNext || (flags&0x80000000u))?sl::Boolean::eTrue:sl::Boolean::eFalse;
    sl::Resource resources[]={{sl::ResourceType::eTex2d,depth,D3D12_RESOURCE_STATE_NON_PIXEL_SHADER_RESOURCE},
        {sl::ResourceType::eTex2d,motion,D3D12_RESOURCE_STATE_NON_PIXEL_SHADER_RESOURCE},
        {sl::ResourceType::eTex2d,scene,sceneState},{sl::ResourceType::eTex2d,ui,uiState}};
    sl::ResourceTag tags[]={{&resources[0],sl::kBufferTypeDepth,sl::ResourceLifecycle::eValidUntilPresent},
        {&resources[1],sl::kBufferTypeMotionVectors,sl::ResourceLifecycle::eValidUntilPresent},
        {&resources[2],sl::kBufferTypeHUDLessColor,sl::ResourceLifecycle::eOnlyValidNow},
        {&resources[3],sl::kBufferTypeUIColorAndAlpha,sl::ResourceLifecycle::eOnlyValidNow}};
    // A different-resolution console UI plane is scaled by CGBJ's compositor.
    // For that layout Streamline extracts UI from the back buffer and HUD-less color.
    const unsigned tagCount=(s.Width==u.Width && s.Height==u.Height)?4:3;
    if(!ok(constants(c,*token,view),"camera constants") || !ok(tagFrame(*token,view,tags,tagCount,list),"frame resources")) {
        mode(0); return false;
    }
    resetNext=false;
    return true;
}
// Use the same NGX instance as DLSS-G. Tag resources at the upscale dispatch,
// where the title's inputs and the writable output are still live.
extern "C" __declspec(dllexport) unsigned CgbjFgUpscaleForFrame(ID3D12GraphicsCommandList* list,
        ID3D12Resource* const* textures,const unsigned* sizes,const float* values,
        const float* matrices,const float* temporal,unsigned flags,unsigned reset,unsigned frame) {
    std::lock_guard guard(frameLock);
    if(!srOptions || !token) return 0;
    const unsigned displayFrame=unsigned(*token);
    // Use the game's frame index even if the host hasn't presented it yet.
    // Keep both tokens inside the SDK's bounded in-flight ring.
    if(std::abs(int64_t(int32_t(frame-displayFrame)))>2) return 0;
    if(srHasConstants && srConstantsFrame==frame) return 2;
    sl::Constants c{};
    if(!matrices || !camera(matrices,c)) {
        // The title supplies camera motion in its velocity texture. Streamline
        // DLSS therefore bypasses matrix-based motion reconstruction. Supply a
        // validated adjacent camera for SR metadata if its command list hasn't
        // reached the submission thread yet. DLSS-G still requires this frame's
        // actual transforms. Never carry the camera across a cut or a frame gap.
        if(!hasSceneCamera || displayFrame-lastSceneFrame>1 || reset) return 0;
        c=lastSceneCamera;
    }
    sl::FrameToken* srToken=token;
    if(frame!=displayFrame && (!ok(newToken(srToken,&frame),"upscale frame token") || !srToken)) return 0;
    const sl::ViewportHandle srView(1);
    sl::DLSSOptions opt{};
    static const auto quality=[] {
        wchar_t mode[16]{};
        GetEnvironmentVariableW(L"PALMLINE_DLSS_QUALITY",mode,16);
        return wcscmp(mode,L"balanced")==0 ? sl::DLSSMode::eBalanced :
            wcscmp(mode,L"performance")==0 ? sl::DLSSMode::eMaxPerformance : sl::DLSSMode::eMaxQuality;
    }();
    opt.mode=quality;
    opt.qualityPreset=sl::DLSSPreset::ePresetM;
    opt.outputWidth=sizes[2]; opt.outputHeight=sizes[3];
    opt.colorBuffersHDR=(flags&1)?sl::Boolean::eTrue:sl::Boolean::eFalse;
    opt.preExposure=values[4]>0?values[4]:1.0f;
    opt.useAutoExposure=sl::Boolean::eTrue;
    c.cameraNear=temporal[0]; c.cameraFar=temporal[1];
    c.cameraPinholeOffset={0,0};
    c.jitterOffset={values[0],values[1]};
    c.mvecScale={values[2]/sizes[0],values[3]/sizes[1]};
    c.depthInverted=(flags&8)?sl::Boolean::eTrue:sl::Boolean::eFalse;
    c.motionVectorsJittered=(flags&4)?sl::Boolean::eTrue:sl::Boolean::eFalse;
    c.cameraMotionIncluded=sl::Boolean::eTrue; c.motionVectors3D=sl::Boolean::eFalse;
    static bool first=true;
    c.reset=(first || reset)?sl::Boolean::eTrue:sl::Boolean::eFalse;
    sl::Resource resources[4];
    sl::ResourceTag tags[4];
    const sl::BufferType types[]={sl::kBufferTypeScalingInputColor,sl::kBufferTypeDepth,
        sl::kBufferTypeMotionVectors,sl::kBufferTypeScalingOutputColor};
    for(int i=0;i<4;++i) {
        resources[i]={sl::ResourceType::eTex2d,textures[i],
            unsigned(i==3?D3D12_RESOURCE_STATE_UNORDERED_ACCESS:D3D12_RESOURCE_STATE_NON_PIXEL_SHADER_RESOURCE)};
        tags[i]={&resources[i],types[i],sl::ResourceLifecycle::eValidUntilEvaluate};
    }
    if(!ok(srOptions(srView,opt),"upscale options") || !ok(constants(c,*srToken,srView),"upscale constants")) return 0;
    srConstantsFrame=frame;
    srHasConstants=true;
    if(!ok(tagFrame(*srToken,srView,tags,4,list),"upscale resources")) return 0;
    const sl::BaseStructure* inputs[]={&srView};
    D3D12_RESOURCE_BARRIER barrier{}; barrier.Type=D3D12_RESOURCE_BARRIER_TYPE_UAV;
    barrier.UAV.pResource=textures[3];
    list->ResourceBarrier(1,&barrier);
    auto result=evaluate(sl::kFeatureDLSS,*srToken,inputs,1,list);
    list->ResourceBarrier(1,&barrier);
    if(!ok(result,"upscale evaluation")) return 0;
    if(first) fprintf(stderr,"DLSS: Streamline preset M evaluated %ux%u -> %ux%u\n",sizes[0],sizes[1],sizes[2],sizes[3]);
    first=false;
    return 1;
}
extern "C" __declspec(dllexport) void CgbjFgEnd() {
    std::lock_guard guard(frameLock);
    if(!token) return;
    marker(sl::PCLMarker::ePresentEnd,*token);
    if(active) {
        sl::DLSSGState state{};
        if(ok(getState(view,state,nullptr),"present feedback") && (presentIndex++<16 || presentIndex%120==0))
            fprintf(stderr,"DLSS-G: real frame=%u presented=%u status=%u multiplier=%u\n",
                unsigned(*token),state.numFramesActuallyPresented,unsigned(state.status),active);
    }
    token=nullptr;
}
