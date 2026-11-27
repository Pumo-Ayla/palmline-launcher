//! Mouse capture and input language belong to the window displaying the swap chain.
use std::{mem::size_of, ptr::{null, null_mut}, sync::OnceLock};
use windows_sys::{core::w, Win32::{
    Foundation::{HWND, LPARAM, RECT, WPARAM},
    Graphics::Gdi::{GetMonitorInfoW, MonitorFromWindow, MONITORINFO, MONITOR_DEFAULTTONEAREST},
    UI::{Input::KeyboardAndMouse::*, WindowsAndMessaging::*},
}};

const STATE: *const u16 = w!("Palmline.KeyboardWindow");
const OLD_LAYOUT: *const u16 = w!("Palmline.PreviousLayout");
const GAME_LAYOUT: *const u16 = w!("Palmline.GameLayout");
const NEW: usize = 1;
const CAPTURED: usize = 2;
const RELEASED: usize = 3;

fn setup_message() -> u32 {
    static MESSAGE: OnceLock<u32> = OnceLock::new();
    *MESSAGE.get_or_init(|| unsafe { RegisterWindowMessageW(w!("Palmline.SetupKeyboardGameWindow")) })
}

fn keyboard_mode() -> bool {
    static ENABLED: OnceLock<bool> = OnceLock::new();
    *ENABLED.get_or_init(|| std::env::var("PALMLINE_KEYBOARD_WINDOW").as_deref() == Ok("1"))
}

fn enabled() -> bool {
    static GAME_WINDOW: OnceLock<bool> = OnceLock::new();
    *GAME_WINDOW.get_or_init(|| std::env::var("PALMLINE_GAME_WINDOW").as_deref() == Ok("1")) || keyboard_mode()
}

/// The renderer can run on a different thread. Let the HWND's own thread apply the layout.
pub fn presented(hwnd: HWND) {
    if enabled() {
        let message = setup_message();
        if message != 0 { unsafe { PostMessageW(hwnd, message, 0, 0); } }
    }
}

fn win_down() -> bool {
    unsafe { GetAsyncKeyState(VK_LWIN.into()) < 0 || GetAsyncKeyState(VK_RWIN.into()) < 0 }
}

pub fn captured(hwnd: HWND) -> bool {
    !hwnd.is_null() && unsafe {
        GetForegroundWindow() == hwnd && GetPropW(hwnd, STATE) as usize == CAPTURED
    } && !win_down()
}

pub fn accepts_keys() -> bool {
    !keyboard_mode() || captured(unsafe { GetForegroundWindow() })
}

fn fullscreen(hwnd: HWND) {
    unsafe {
        let mut monitor = MONITORINFO { cbSize: size_of::<MONITORINFO>() as u32, ..Default::default() };
        if GetMonitorInfoW(MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST), &mut monitor) == 0 { return; }
        let rect = monitor.rcMonitor;
        let style = GetWindowLongW(hwnd, GWL_STYLE) as u32;
        SetWindowLongW(hwnd, GWL_STYLE, ((style & !WS_OVERLAPPEDWINDOW) | WS_POPUP) as i32);
        let ex = GetWindowLongW(hwnd, GWL_EXSTYLE) as u32;
        SetWindowLongW(hwnd, GWL_EXSTYLE, (ex & !(WS_EX_CLIENTEDGE | WS_EX_WINDOWEDGE | WS_EX_DLGMODALFRAME)) as i32);
        SetWindowPos(hwnd, null_mut(), rect.left, rect.top, rect.right - rect.left, rect.bottom - rect.top,
                     SWP_FRAMECHANGED | SWP_NOACTIVATE | SWP_NOZORDER);
    }
}

fn english(hwnd: HWND) {
    unsafe {
        let previous = GetKeyboardLayout(0);
        let is_english = |layout: HKL| layout as usize & 0x3ff == 9;
        if is_english(previous) { return; }
        let mut layouts = vec![null_mut(); GetKeyboardLayoutList(0, null_mut()).max(0) as usize];
        let count = GetKeyboardLayoutList(layouts.len() as i32, layouts.as_mut_ptr());
        let english = layouts.into_iter().take(count.max(0) as usize).find(|&layout| is_english(layout))
            .unwrap_or_else(|| LoadKeyboardLayoutW(w!("00000409"), 0));
        if !english.is_null() && !ActivateKeyboardLayout(english, 0).is_null() {
            SetPropW(hwnd, OLD_LAYOUT, previous);
            SetPropW(hwnd, GAME_LAYOUT, english);
        } else {
            tracing::warn!("English keyboard layout unavailable for the game window");
        }
    }
}

fn confine(hwnd: HWND) -> bool {
    unsafe {
        let mut rect = RECT::default();
        // The popup has neither a title bar nor a resize frame.
        GetWindowRect(hwnd, &mut rect) != 0 && rect.right > rect.left && rect.bottom > rect.top
            && GetForegroundWindow() == hwnd && IsIconic(hwnd) == 0 && ClipCursor(&rect) != 0
    }
}

fn capture(hwnd: HWND) {
    if !win_down() && confine(hwnd) {
        unsafe {
            SetPropW(hwnd, STATE, CAPTURED as _);
            SetCursor(null_mut());
        }
        if keyboard_mode() { english(hwnd); }
    }
}

fn release(hwnd: HWND) {
    unsafe {
        let active = GetPropW(hwnd, STATE) as usize == CAPTURED;
        SetPropW(hwnd, STATE, RELEASED as _);
        if active {
            ClipCursor(null());
            SetCursor(LoadCursorW(null_mut(), IDC_ARROW));
        }
        let old = RemovePropW(hwnd, OLD_LAYOUT);
        let english = RemovePropW(hwnd, GAME_LAYOUT);
        if !old.is_null() && GetKeyboardLayout(0) == english { ActivateKeyboardLayout(old, 0); }
    }
}

/// Used by both the title-created window and the renderer's fallback window.
static LAST_WHEEL: std::sync::Mutex<Option<(bool,std::time::Instant)>>=std::sync::Mutex::new(None);

pub fn wheel_held(up:bool) -> bool {
    LAST_WHEEL.lock().unwrap().is_some_and(|(direction,t)| direction==up && t.elapsed().as_millis()<90)
}

pub fn message(hwnd: HWND, msg: u32, key: WPARAM, detail: LPARAM) -> Option<isize> {
    let state = unsafe { GetPropW(hwnd, STATE) } as usize;
    if msg >= 0xc000 && msg == setup_message() && enabled() {
        if state == 0 {
            unsafe { SetPropW(hwnd, STATE, NEW as _); }
            fullscreen(hwnd);
            // Honour the Play click only while the launcher still has foreground permission.
            let launcher = std::env::var("PALMLINE_LAUNCHER_HWND").ok().and_then(|s| s.parse::<usize>().ok());
            let foreground = unsafe { GetForegroundWindow() };
            if !foreground.is_null() && launcher == Some(foreground as usize) {
                unsafe { SetForegroundWindow(hwnd); }
            }
            if unsafe { GetPropW(hwnd, STATE) } as usize == NEW { capture(hwnd); }
        }
        return Some(0);
    }
    if state == 0 { return None; }
    match msg {
        WM_MOUSEWHEEL if state == CAPTURED && keyboard_mode() => {
            let delta=(key>>16) as i16;
            if delta!=0 { *LAST_WHEEL.lock().unwrap()=Some((delta>0,std::time::Instant::now())); }
        }
        WM_KEYDOWN | WM_SYSKEYDOWN if key == VK_LWIN as usize || key == VK_RWIN as usize => release(hwnd),
        WM_KILLFOCUS | WM_CANCELMODE | WM_CLOSE => release(hwnd),
        WM_ACTIVATEAPP if key == 0 => release(hwnd),
        WM_ACTIVATE if key & 0xffff == WA_INACTIVE as usize => release(hwnd),
        WM_SIZE if key == SIZE_MINIMIZED as usize => release(hwnd),
        WM_NCDESTROY => { release(hwnd); unsafe { RemovePropW(hwnd, STATE); } }
        WM_SETFOCUS if state == NEW => capture(hwnd),
        WM_LBUTTONUP if state != CAPTURED => { capture(hwnd); return Some(0); }
        WM_LBUTTONDOWN if state != CAPTURED => return Some(0),
        WM_DISPLAYCHANGE | WM_DPICHANGED => {
            fullscreen(hwnd);
            if state == CAPTURED { confine(hwnd); }
            return Some(0);
        }
        WM_WINDOWPOSCHANGED if state == CAPTURED => { confine(hwnd); }
        WM_SETCURSOR if state == CAPTURED && detail & 0xffff == HTCLIENT as isize => {
            unsafe { SetCursor(null_mut()); }
            return Some(1);
        }
        _ => {}
    }
    None
}
