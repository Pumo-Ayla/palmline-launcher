//! Optional native super resolution at the FidelityFX upscaling boundary.
//! The title records its own output first, so unsupported frames have a complete fallback.
use std::collections::HashMap;
use std::ffi::c_void;
use std::os::windows::ffi::OsStrExt;
use std::sync::{LazyLock, Mutex, OnceLock};
use windows::core::Interface;
use windows_sys::Win32::System::LibraryLoader::{GetProcAddress, LoadLibraryExW, LOAD_LIBRARY_SEARCH_DLL_LOAD_DIR, LOAD_LIBRARY_SEARCH_SYSTEM32};

type Create = unsafe extern "system" fn(*mut usize, *const u8, *const c_void) -> u32;
type Dispatch = unsafe extern "system" fn(*mut usize, *const u8) -> u32;
type Destroy = unsafe extern "system" fn(*mut usize, *const c_void) -> u32;
static PROVIDER: OnceLock<(Create, Dispatch, Destroy)> = OnceLock::new();
static CONTEXTS: LazyLock<Mutex<HashMap<usize, u32>>> = LazyLock::new(Default::default);

/// # Safety
/// All three addresses must be the named public FidelityFX entry points, live for the run.
pub unsafe fn install(create: usize, dispatch: usize, destroy: usize) {
    unsafe { let _ = PROVIDER.set((std::mem::transmute::<usize,Create>(create),
        std::mem::transmute::<usize,Dispatch>(dispatch), std::mem::transmute::<usize,Destroy>(destroy))); }
}

pub fn entry(name: &str) -> Option<usize> {
    PROVIDER.get()?;
    match name {
        "ffxCreateContext" => Some(create as *const () as usize),
        "ffxDispatch" => Some(dispatch as *const () as usize),
        "ffxDestroyContext" => Some(destroy as *const () as usize),
        _ => None,
    }
}

// Public x64 FidelityFX API layout: header, command list, seven FfxApiResource
// records, jitter/motion scales, dimensions and camera/exposure parameters.
// Use byte copies since console descriptors may be packed or a different version.
fn word(b: &[u8], at: usize) -> u32 { u32::from_le_bytes(b[at..at+4].try_into().unwrap()) }
fn wide(b: &[u8], at: usize) -> u64 { u64::from_le_bytes(b[at..at+8].try_into().unwrap()) }
unsafe extern "system" fn create(ctx: *mut usize, desc: *const u8, alloc: *const c_void) -> u32 {
    let result = unsafe { PROVIDER.get().unwrap().0(ctx, desc, alloc) };
    let mut head = [0; 20];
    if result == 0 && !ctx.is_null() && crate::host::read_into(desc, &mut head) {
        let kind = wide(&head, 0);
        tracing::info!(kind, "FidelityFX context created");
        if kind != 0x10000 { return result; }
        let flags = word(&head,16);
        CONTEXTS.lock().unwrap_or_else(|e|e.into_inner()).insert(unsafe { *ctx }, flags);
        tracing::warn!(flags, "DLSS: FidelityFX upscale context");
    }
    result
}
unsafe extern "system" fn destroy(ctx: *mut usize, alloc: *const c_void) -> u32 {
    let key = (!ctx.is_null()).then(|| unsafe { *ctx });
    let result = unsafe { PROVIDER.get().unwrap().2(ctx, alloc) };
    if result == 0 && let Some(key) = key {
        CONTEXTS.lock().unwrap_or_else(|e|e.into_inner()).remove(&key);
    }
    result
}
unsafe extern "system" fn dispatch(ctx: *mut usize, desc: *const u8) -> u32 {
    let result = unsafe { PROVIDER.get().unwrap().1(ctx, desc) };
    if result != 0 || ctx.is_null() { return result; }
    let flags = CONTEXTS.lock().unwrap_or_else(|e|e.into_inner()).get(&unsafe { *ctx }).copied();
    let Some(flags) = flags else { return result };
    let mut b = [0; 432];
    if !crate::host::read_into(desc,&mut b) || wide(&b,0) != 0x10001 { return result; }
    let mut frame = [0u64; 16];
    for (dst,slot) in [0,1,2,6].iter().enumerate() {
        frame[dst] = wide(&b,24+slot*48);
    }
    for (i,at) in [376,380,384,388,360,364,368,372,404,400].iter().enumerate() {
        frame[4+i] = u64::from(word(&b,*at));
    }
    frame[14] = u64::from(b[408] != 0) | (u64::from(word(&b,412))<<32);
    frame[15] = u64::from(flags) | (u64::from(word(&b,416))<<32);
    // Capture the title's logical frame while it records this dispatch. The
    // presentation thread can still be displaying an earlier frame at replay.
    frame[4] |= u64::from(crate::queue::presented().wrapping_add(1) as u32) << 32;
    static FIRST: std::sync::Once = std::sync::Once::new();
    FIRST.call_once(|| {
        let mut extension = wide(&b, 8);
        let mut types = Vec::new();
        for _ in 0..8 {
            let mut header = [0; 16];
            if extension == 0 || !crate::host::read_into(extension as *const u8, &mut header) { break; }
            types.push(wide(&header, 0));
            extension = wide(&header, 8);
        }
        tracing::warn!(sizes=?frame[4..8].iter().map(|&n|n as u32).collect::<Vec<_>>(), flags, extensions=?types, "DLSS: first upscale dispatch");
    });
    crate::list::queue_upscale(wide(&b,16) as _, &frame);
    result
}

#[repr(C)]
struct NativeFrame {
    textures: [*mut c_void; 4],
    sizes: [u32; 4],
    values: [f32; 6],
    flags: [u32; 4],
}
type Evaluate = unsafe extern "C" fn(*mut c_void,*const NativeFrame)->u32;
type Init = unsafe extern "C" fn(*mut c_void,*const u16,*const u16)->u32;
fn backend() -> Option<Evaluate> {
    static BACKEND: OnceLock<Option<Evaluate>> = OnceLock::new();
    *BACKEND.get_or_init(|| {
        let exe = std::env::current_exe().ok()?;
        let root = exe.ancestors().find(|p| p.join("Community/projects.xml").is_file())?;
        let folder = root.join("Runtime/DLSS");
        let utf16 = |p: &std::path::Path| p.as_os_str().encode_wide().chain(Some(0)).collect::<Vec<_>>();
        let dll = utf16(&folder.join("CgbjDlss.dll"));
        let log = utf16(&std::env::current_dir().ok()?);
        unsafe {
            let lib = LoadLibraryExW(dll.as_ptr(),std::ptr::null_mut(),LOAD_LIBRARY_SEARCH_DLL_LOAD_DIR|LOAD_LIBRARY_SEARCH_SYSTEM32);
            if lib.is_null() { tracing::warn!("DLSS: native backend unavailable"); return None; }
            let init: Init = std::mem::transmute(GetProcAddress(lib,c"CgbjDlssInit".as_ptr().cast())?);
            let evaluate: Evaluate = std::mem::transmute(GetProcAddress(lib,c"CgbjDlssEvaluate".as_ptr().cast())?);
            let status = init(crate::host::host()?.device.as_raw(),utf16(&folder).as_ptr(),log.as_ptr());
            tracing::warn!(status, "DLSS: NGX initialization");
            (status == 1).then_some(evaluate)
        }
    })
}

pub(crate) fn evaluate(list: &crate::host::HostList, frame: &[u64; 16]) -> bool {
    let sizes = [frame[4] as u32,frame[5] as u32,frame[6] as u32,frame[7] as u32];
    let values = std::array::from_fn(|i| f32::from_bits(frame[8+i] as u32));
    if sizes.iter().any(|&s| s == 0 || s > 16384) || values.iter().any(|v| !v.is_finite()) { return false; }
    let mut textures = [std::ptr::null_mut();4];
    for i in 0..4 {
        let guest = frame[i] as crate::object::This;
        if guest.is_null() || !crate::resource::is_resource(guest) { return false; }
        let Some(r) = crate::resource::host_resource(guest) else { return false };
        let d = unsafe { r.GetDesc() };
        let [w,h] = if i == 3 {[sizes[2],sizes[3]]} else {[sizes[0],sizes[1]]};
        if d.Width != u64::from(w) || d.Height != h || (i==3 && d.Flags.0 & 4 == 0) { return false; }
        textures[i] = r.as_raw();
    }
    static SR: LazyLock<bool> = LazyLock::new(||std::env::var("PALMLINE_DLSS_SR").as_deref()==Ok("1"));
    let fg=crate::frame_generation::active();
    let call = if *SR && !fg {backend()} else {None};
    if call.is_none() && !fg {return false;}
    for (i, &r) in frame[..4].iter().enumerate() {
        crate::list::ensure_state(list,r as _,if i==3 {8} else {0x40});
    }
    let flags = frame[15] as u32;
    if fg {
        crate::frame_generation::snapshot(list,&textures,frame);
        if *SR {
            let result=crate::frame_generation::upscale(list,&textures,&sizes,&values,frame);
            // Periodically report actual upscale dispatches, independently of
            // the swap chain's generated-frame count.
            static COUNTS: Mutex<[u64;3]> = Mutex::new([0;3]);
            let mut counts=COUNTS.lock().unwrap_or_else(|e|e.into_inner());
            counts[result.min(2) as usize]+=1;
            let total: u64=counts.iter().sum();
            if total==1 || total%120==0 {
                tracing::warn!(evaluated=counts[1], fallback=counts[0], duplicate=counts[2],
                    "DLSS SR: upscale dispatch totals");
            }
            return true;
        }
    }
    let Some(call)=call else {return false};
    let args = NativeFrame {textures,sizes,values,flags:[frame[14] as u32,flags & 1,(flags>>3)&1,(flags>>2)&1]};
    let result = unsafe { call(list.list.as_raw(),&args) };
    static LAST: std::sync::atomic::AtomicU32 = std::sync::atomic::AtomicU32::new(0);
    if LAST.swap(result,std::sync::atomic::Ordering::Relaxed) != result {
        tracing::warn!(result, ?sizes, "DLSS: NGX evaluation");
    }
    true // NGX may have changed command-list state even on an error.
}
