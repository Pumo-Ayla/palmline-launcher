#include <d3d12.h>
#include <dxgi1_6.h>
#include <cstring>
#pragma comment(lib, "dxgi.lib")
#include <wrl/client.h>
#include <cstdio>

using Microsoft::WRL::ComPtr;

static int gpuInventory() {
    ComPtr<IDXGIFactory1> factory;
    HRESULT hr = CreateDXGIFactory1(IID_PPV_ARGS(&factory));
    if (FAILED(hr)) {
        std::fprintf(stderr, "CreateDXGIFactory1: 0x%08lX\n", (unsigned long)hr);
        return 1;
    }
    ComPtr<IDXGIFactory6> preferred;
    factory.As(&preferred);
    bool found = false;
    for (UINT i = 0; ; ++i) {
        ComPtr<IDXGIAdapter1> adapter;
        hr = preferred ? preferred->EnumAdapterByGpuPreference(i, DXGI_GPU_PREFERENCE_HIGH_PERFORMANCE, IID_PPV_ARGS(&adapter))
                       : factory->EnumAdapters1(i, &adapter);
        if (hr == DXGI_ERROR_NOT_FOUND) break;
        if (FAILED(hr)) {
            std::fprintf(stderr, "DXGI enumeration [%u]: 0x%08lX\n", i, (unsigned long)hr);
            return found ? 0 : 1;
        }
        DXGI_ADAPTER_DESC1 info{};
        hr = adapter->GetDesc1(&info);
        if (FAILED(hr)) {
            std::fprintf(stderr, "GetDesc1 [%u]: 0x%08lX\n", i, (unsigned long)hr);
            continue;
        }
        char name[512]{};
        WideCharToMultiByte(CP_UTF8,0,info.Description,-1,name,sizeof(name),nullptr,nullptr);
        if (info.Flags & DXGI_ADAPTER_FLAG_SOFTWARE) {
            std::fprintf(stderr, "%s: software adapter\n", name);
            continue;
        }
        ComPtr<ID3D12Device> device;
        hr = D3D12CreateDevice(adapter.Get(), D3D_FEATURE_LEVEL_12_0, IID_PPV_ARGS(&device));
        std::fprintf(stderr, "%s: D3D12 FL 12_0 HRESULT=0x%08lX", name, (unsigned long)hr);
        if (FAILED(hr)) {
            std::fputc('\n', stderr);
            continue;
        }
        D3D12_FEATURE_DATA_SHADER_MODEL shader{D3D_SHADER_MODEL_6_6};
        D3D12_FEATURE_DATA_D3D12_OPTIONS7 options{};
        HRESULT sm = device->CheckFeatureSupport(D3D12_FEATURE_SHADER_MODEL, &shader, sizeof(shader));
        HRESULT mesh = device->CheckFeatureSupport(D3D12_FEATURE_D3D12_OPTIONS7, &options, sizeof(options));
        std::fprintf(stderr, ", SM 6.6=%s (0x%08lX), mesh shaders=%s (0x%08lX)\n",
            FAILED(sm) ? "unknown" : shader.HighestShaderModel >= D3D_SHADER_MODEL_6_6 ? "yes" : "no", (unsigned long)sm,
            FAILED(mesh) ? "unknown" : options.MeshShaderTier != D3D12_MESH_SHADER_TIER_NOT_SUPPORTED ? "yes" : "no", (unsigned long)mesh);
        if (!found) {
            const char* vendor = info.VendorId==0x10de ? "NVIDIA" : info.VendorId==0x1002 ? "AMD" : info.VendorId==0x8086 ? "Intel" : "Other";
            std::printf("%s\t%s\n", vendor, name);
            found = true;
        }
    }
    return found ? 0 : 4;
}

int main(int argc, char** argv) {
    if (argc == 2 && std::strcmp(argv[1], "--gpu") == 0) return gpuInventory();
    // The wave and heap probe must use the same default device as victory-fork.
    ComPtr<ID3D12Device> device;
    if (FAILED(D3D12CreateDevice(nullptr, D3D_FEATURE_LEVEL_12_0, IID_PPV_ARGS(&device)))) return 1;
    D3D12_FEATURE_DATA_D3D12_OPTIONS1 options{};
    if (FAILED(device->CheckFeatureSupport(D3D12_FEATURE_D3D12_OPTIONS1, &options, sizeof(options))) ||
        options.WaveLaneCountMax == 0) return 2;
    D3D12_DESCRIPTOR_HEAP_DESC desc{};
    desc.Type = D3D12_DESCRIPTOR_HEAP_TYPE_CBV_SRV_UAV;
    desc.Flags = D3D12_DESCRIPTOR_HEAP_FLAG_SHADER_VISIBLE;
    desc.NumDescriptors = 1u << 21;
    ComPtr<ID3D12DescriptorHeap> heap;
    if (FAILED(device->CreateDescriptorHeap(&desc, IID_PPV_ARGS(&heap)))) {
        desc.NumDescriptors = 1000000;
        if (FAILED(device->CreateDescriptorHeap(&desc, IID_PPV_ARGS(&heap)))) return 3;
    }
    std::printf("%u %u\n", options.WaveLaneCountMax, desc.NumDescriptors - 1);
    return 0;
}
