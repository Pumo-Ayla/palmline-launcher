//! Optional Streamline presentation on the CGBJ graphics queue.
use std::ffi::c_void;
use std::os::windows::ffi::OsStrExt;
use std::path::Path;
use std::sync::{LazyLock, Mutex, OnceLock};
use std::sync::atomic::{AtomicBool, Ordering};
use windows::core::Interface;
use windows::Win32::Graphics::{Direct3D12::*, Dxgi::IDXGIFactory2};
use windows_sys::Win32::System::LibraryLoader::*;
use crate::host::HostList;

type Ptr = *mut c_void;
type Init = unsafe extern "C" fn(*const u16,*const u16)->bool;
struct Api {
    device: unsafe extern "C" fn(Ptr)->u32,
    factory: unsafe extern "C" fn(Ptr)->Ptr,
    copy: unsafe extern "C" fn(Ptr,Ptr,Ptr,Ptr,*mut Ptr)->bool,
    upscale: unsafe extern "C" fn(Ptr,*const Ptr,*const u32,*const f32,*const f32,*const f32,u32,u32,u32)->u32,
    begin: unsafe extern "C" fn(u32)->bool,
    marker: unsafe extern "C" fn(u32),
    prepare: unsafe extern "C" fn(u32,*const f32,*const f32,u32,Ptr,Ptr,Ptr,u32,Ptr,u32,Ptr)->bool,
    end: unsafe extern "C" fn(),
}
static API: OnceLock<Option<Api>>=OnceLock::new();
static MULTIPLIER: LazyLock<u32>=LazyLock::new(|| std::env::var("PALMLINE_DLSSG").ok()
    .and_then(|v| v.parse::<u32>().ok()).filter(|v| (2..=6).contains(v)).unwrap_or(0));
static PROXY: AtomicBool=AtomicBool::new(false);
static SUBMITTED: AtomicBool=AtomicBool::new(false);
pub fn requested()->bool { *MULTIPLIER!=0 }
pub fn active()->bool { PROXY.load(Ordering::Acquire) }
fn api()->Option<&'static Api> { API.get()?.as_ref() }
fn wide(p:&Path)->Vec<u16> { p.as_os_str().encode_wide().chain(Some(0)).collect() }
pub(crate) fn init() {
    if !requested() { return; }
    API.get_or_init(|| unsafe {
        let exe=std::env::current_exe().ok()?;
        let root=exe.ancestors().find(|p| p.join("Community/projects.xml").is_file())?;
        let dir=root.join("Runtime/DLSS/FrameGeneration");
        let lib=LoadLibraryExW(wide(&dir.join("CgbjFrameGeneration.dll")).as_ptr(),std::ptr::null_mut(),
            LOAD_LIBRARY_SEARCH_DLL_LOAD_DIR|LOAD_LIBRARY_SEARCH_SYSTEM32);
        if lib.is_null() { return None; }
        macro_rules! export { ($name:literal) => {std::mem::transmute(GetProcAddress(lib,concat!($name,"\0").as_ptr())?)} }
        let start:Init=export!("CgbjFgInit");
        let api=Api{device:export!("CgbjFgDevice"),factory:export!("CgbjFgFactory"),copy:export!("CgbjFgCopyInputs"),
            upscale:export!("CgbjFgUpscaleForFrame"),begin:export!("CgbjFgBegin"),marker:export!("CgbjFgMarker"),prepare:export!("CgbjFgPrepare"),end:export!("CgbjFgEnd")};
        let success=start(wide(&dir).as_ptr(),wide(&std::env::current_dir().ok()?).as_ptr());
        tracing::warn!(success,"DLSS-G: Streamline initialization");
        success.then_some(api)
    });
}
pub(crate) fn factory(device:&ID3D12Device, raw:&IDXGIFactory2)->Option<IDXGIFactory2> {
    let a=api()?;
    unsafe {
        if (a.device)(device.as_raw()) < *MULTIPLIER { return None; }
        let proxy=(a.factory)(raw.as_raw());
        (!proxy.is_null()).then(||IDXGIFactory2::from_raw(proxy))
    }
}
pub(crate) fn started(frame:u32) {
    if let Some(a)=api() { unsafe {
        if (a.begin)(frame) {
            (a.marker)(0); SUBMITTED.store(false,Ordering::Release); PROXY.store(true,Ordering::Release);
        }
    }}
}
#[derive(Default,Clone)]
pub(crate) struct Inputs {
    pub camera:Option<Box<[f32;120]>>,
    images:Option<([ID3D12Resource;2],[f32;6],u32)>,
}
static FRAME:LazyLock<Mutex<Inputs>>=LazyLock::new(Default::default);
pub(crate) fn upscale(list:&HostList,textures:&[Ptr;4],sizes:&[u32;4],values:&[f32;6],frame:&[u64;16])->u32 {
    let Some(api)=api() else { return 0 };
    let recorded_camera=list.fg.lock().unwrap_or_else(|e|e.into_inner()).camera.clone();
    let camera=recorded_camera.or_else(||FRAME.lock().unwrap_or_else(|e|e.into_inner()).camera.clone());
    let a=f32::from_bits((frame[14]>>32) as u32);
    let b=f32::from_bits((frame[15]>>32) as u32);
    let planes=[a.min(b),a.max(b)];
    unsafe {(api.upscale)(list.list.as_raw(),textures.as_ptr(),sizes.as_ptr(),values.as_ptr(),
        camera.as_ref().map_or(std::ptr::null(),|c|c.as_ptr()),planes.as_ptr(),
        frame[15] as u32,frame[14] as u32,(frame[4]>>32) as u32)}
}
pub(crate) fn snapshot(list:&HostList,textures:&[Ptr;4],frame:&[u64;16]) {
    let Some(a)=api() else {return};
    let Some(host)=crate::host::host() else {return};
    let mut copies=[std::ptr::null_mut();2];
    unsafe {
        if !(a.copy)(host.device.as_raw(),list.list.as_raw(),textures[1],textures[2],copies.as_mut_ptr()) {return;}
        let resources=copies.map(|p| ID3D12Resource::from_raw(p));
        for r in &resources {list.keep(r);}
        let mut temporal=[0.;6];
        for i in 0..4 {temporal[i]=f32::from_bits(frame[8+i] as u32);}
        let near=f32::from_bits((frame[14]>>32) as u32);
        let far=f32::from_bits((frame[15]>>32) as u32);
        temporal[4]=near.min(far); temporal[5]=near.max(far);
        list.fg.lock().unwrap_or_else(|e|e.into_inner()).images=Some((resources,temporal,
            frame[15] as u32 | (frame[14] as u32)<<31));
    }
}
pub(crate) fn submit(runs:&[&HostList]) {
    if !active() { return; }
    let Some(a)=api() else{return};
    if !SUBMITTED.swap(true,Ordering::AcqRel) {unsafe{(a.marker)(1); (a.marker)(2);}}
    let mut frame=FRAME.lock().unwrap_or_else(|e|e.into_inner());
    for list in runs {
        let recorded=list.fg.lock().unwrap_or_else(|e|e.into_inner());
        if let Some(c)=&recorded.camera {frame.camera=Some(c.clone());}
        if let Some(i)=&recorded.images {frame.images=Some(i.clone());}
    }
}
pub(crate) fn prepare(list:&HostList,scene:&ID3D12Resource,state:u32,ui:Option<(&ID3D12Resource,u32)>) {
    if !active(){return;}
    let a=api().unwrap();
    let frame=std::mem::take(&mut *FRAME.lock().unwrap_or_else(|e|e.into_inner()));
    if let (Some(camera),Some((images,temporal,flags)),Some((overlay,overlay_state)))=(frame.camera,frame.images,ui) {
        for r in &images {list.keep(r);}
        static FIRST:std::sync::Once=std::sync::Once::new();
        FIRST.call_once(|| tracing::warn!(?temporal,flags,"DLSS-G: matched camera, motion, depth and two display planes"));
        unsafe {(a.prepare)(*MULTIPLIER,camera.as_ptr(),temporal.as_ptr(),flags,images[0].as_raw(),
            images[1].as_raw(),scene.as_raw(),state,overlay.as_raw(),overlay_state,list.list.as_raw());}
    } else { unsafe {
        (a.prepare)(0,std::ptr::null(),std::ptr::null(),0,std::ptr::null_mut(),std::ptr::null_mut(),
            std::ptr::null_mut(),0,std::ptr::null_mut(),0,list.list.as_raw());
    }}
}
pub(crate) fn present_start() {
    if active(){let a=api().unwrap(); unsafe{(a.marker)(3); (a.marker)(4);}}
}
pub(crate) fn present_end(next:u32) {
    if active(){unsafe{(api().unwrap().end)();} started(next);}
}
