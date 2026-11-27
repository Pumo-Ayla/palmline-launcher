#include <windows.h>
#include <d3d12.h>
#include <wrl/client.h>
#include <filesystem>

extern "C" __declspec(dllimport) unsigned CgbjDlssInit(ID3D12Device*, const wchar_t*, const wchar_t*);
extern "C" __declspec(dllimport) void CgbjDlssShutdown();

int wmain(int argc, wchar_t** argv) {
    // Use the same default D3D12 adapter and NGX initialization as the renderer.
    Microsoft::WRL::ComPtr<ID3D12Device> device;
    wchar_t module[32768];
    const auto size = GetModuleFileNameW(nullptr, module, ARRAYSIZE(module));
    if (!size || size >= ARRAYSIZE(module)) return 1;
    auto plugins = std::filesystem::path(module).parent_path();
    std::error_code error;
    auto cache = std::filesystem::temp_directory_path(error);
    if(argc==2 && wcscmp(argv[1],L"--frame-generation")==0) {
        auto folder=plugins/L"FrameGeneration";
        auto bridge=LoadLibraryExW((folder/L"CgbjFrameGeneration.dll").c_str(),nullptr,
            LOAD_LIBRARY_SEARCH_DLL_LOAD_DIR|LOAD_LIBRARY_SEARCH_SYSTEM32);
        if(!bridge || error) return 0;
        auto init=reinterpret_cast<bool(*)(const wchar_t*,const wchar_t*)>(GetProcAddress(bridge,"CgbjFgInit"));
        auto query=reinterpret_cast<unsigned(*)(ID3D12Device*)>(GetProcAddress(bridge,"CgbjFgDevice"));
        if(!init || !query || !init(folder.c_str(),cache.c_str()) ||
           FAILED(D3D12CreateDevice(nullptr,D3D_FEATURE_LEVEL_12_0,IID_PPV_ARGS(&device)))) return 0;
        return int(query(device.Get()));
    }
    if (FAILED(D3D12CreateDevice(nullptr, D3D_FEATURE_LEVEL_12_0, IID_PPV_ARGS(&device)))) return 1;
    if (error || CgbjDlssInit(device.Get(), plugins.c_str(), cache.c_str()) != 1) return 1;
    CgbjDlssShutdown();
    return 0;
}
