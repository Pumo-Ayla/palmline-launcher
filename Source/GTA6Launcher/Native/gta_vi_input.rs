// Included inside the community runtime's Windows GameInput host.
use super::{focused, GetAsyncKeyState, PadState, A, B, X, Y, LEFT_SHOULDER, RIGHT_SHOULDER,
            LEFT_STICK, RIGHT_STICK, MENU, VIEW, UP, DOWN, LEFT, RIGHT};
use std::time::{Duration, Instant};
use windows_sys::Win32::Foundation::{POINT, RECT};
use windows_sys::Win32::UI::WindowsAndMessaging::{GetCursorPos, GetForegroundWindow, GetWindowRect, SetCursorPos};

#[derive(Clone, Copy)]
enum Output { Button(u32), Partner, LT, RT, Axis(usize, f32) }
struct Rule { layer: &'static str, key: u8, output: Output }

fn rules() -> Vec<Rule> {
    include_str!("GtaVi.bindings").lines().filter(|s| !s.trim().is_empty() && !s.starts_with('#')).map(|s| {
        let mut fields = s.split_whitespace();
        let layer = fields.next().unwrap();
        let key = u8::from_str_radix(fields.next().unwrap(), 16).unwrap();
        let output = match fields.next().unwrap() {
            "LX-" => Output::Axis(0,-1.), "LX+" => Output::Axis(0,1.),
            "LY-" => Output::Axis(1,-1.), "LY+" => Output::Axis(1,1.),
            "RX-" => Output::Axis(2,-1.), "RX+" => Output::Axis(2,1.),
            "RY-" => Output::Axis(3,-1.), "RY+" => Output::Axis(3,1.),
            "LT" => Output::LT, "RT" => Output::RT, "PartnerRB" => Output::Partner,
            s => Output::Button(match s {
                "A"=>A, "B"=>B, "X"=>X, "Y"=>Y, "LS"=>LEFT_STICK, "RS"=>RIGHT_STICK,
                "LB"=>LEFT_SHOULDER, "RB"=>RIGHT_SHOULDER, "Up"=>UP, "Down"=>DOWN,
                "Left"=>LEFT, "Right"=>RIGHT, "View"=>VIEW, "Menu"=>MENU, _=>panic!("unknown preset control {s}")
            })
        };
        Rule {layer,key,output}
    }).collect()
}

pub(super) struct MappedPad {
    pub(super) enabled: bool, rules: Vec<Rule>, custom: bool, driving: bool, switch_held: bool,
    partner_since: Option<Instant>, centered: bool,
}
impl Default for MappedPad {
    fn default() -> Self {
        let custom = std::env::var("PALMLINE_CUSTOM_PAD").ok().and_then(|s| custom_rules(&s));
        Self { enabled: custom.is_some() || std::env::var("PALMLINE_GTA6_PRESET").as_deref()==Ok("1"),
               custom:custom.is_some(), rules:custom.unwrap_or_else(rules),
               driving:false, switch_held:false, partner_since:None, centered:false }
    }
}
// MappingStep order through Menu. Share and Guide are system buttons outside PadState.
fn custom_rules(text: &str) -> Option<Vec<Rule>> {
    let outputs=[Output::Button(A),Output::Button(B),Output::Button(X),Output::Button(Y),
        Output::Button(LEFT_SHOULDER),Output::Button(RIGHT_SHOULDER),Output::LT,Output::RT,
        Output::Button(UP),Output::Button(DOWN),Output::Button(LEFT),Output::Button(RIGHT),
        Output::Button(LEFT_STICK),Output::Button(RIGHT_STICK),Output::Button(VIEW),Output::Button(MENU)];
    let keys=text.split(',').map(|v|u8::from_str_radix(v,16)).collect::<Result<Vec<_>,_>>().ok()?;
    if keys.len()!=outputs.len() || keys.contains(&0) { return None; }
    let mut result:Vec<_>=keys.into_iter().zip(outputs).map(|(key,output)|Rule{layer:"both",key,output}).collect();
    for (key,axis,sign) in [(b'W',1,1.),(b'S',1,-1.),(b'A',0,-1.),(b'D',0,1.)] {
        result.push(Rule{layer:"both",key,output:Output::Axis(axis,sign)});
    }
    Some(result)
}
impl MappedPad {
    fn reading(&mut self, down: impl Fn(u8)->bool, look:(f32,f32), now:Instant) -> PadState {
        let switch = !self.custom && down(0x75);
        if switch && !self.switch_held { self.driving = !self.driving; }
        self.switch_held = switch;
        let partner = if down(0x12) {
            now.duration_since(*self.partner_since.get_or_insert(now)) >= Duration::from_millis(450)
        } else { self.partner_since=None; false };
        let layer = if self.driving { "drive" } else { "foot" };
        let aiming = !self.driving && down(0x02);
        let mut pad=PadState::default();
        let mut axes=[0.,0.,look.0,look.1];
        for rule in &self.rules {
            let applies = rule.layer=="both" || rule.layer==layer || (rule.layer=="aim" && aiming);
            if !applies || !down(rule.key) { continue; }
            match rule.output {
                Output::Button(bit) => pad.buttons |= bit,
                Output::Partner if partner => pad.buttons |= RIGHT_SHOULDER,
                Output::Partner => (), Output::LT => pad.left_trigger=1., Output::RT => pad.right_trigger=1.,
                Output::Axis(index,value) => axes[index]+=value,
            }
        }
        let length=axes[0].hypot(axes[1]).max(1.);
        pad.left_x=axes[0]/length; pad.left_y=axes[1]/length;
        pad.right_x=axes[2].clamp(-1.,1.); pad.right_y=axes[3].clamp(-1.,1.);
        pad
    }
    pub(super) fn poll(&mut self) -> PadState {
        let down=|key:u8| match key {
            0xe8 | 0xff => victory_gpu::keyboard_window::wheel_held(key==0xe8),
            _ => unsafe { GetAsyncKeyState(key.into()) < 0 },
        };
        let hwnd=unsafe { GetForegroundWindow() };
        if !focused() || !victory_gpu::keyboard_window::captured(hwnd) {
            self.centered=false; self.partner_since=None; self.switch_held=true;
            return PadState::default();
        }
        let mut look=(0.,0.);
        // Only read/recenter the pointer inside this process's foreground game window.
        unsafe {
            let mut rect=RECT::default(); let mut cursor=POINT::default();
            if GetWindowRect(hwnd,&mut rect)!=0 && GetCursorPos(&mut cursor)!=0 {
                let x=rect.left+(rect.right-rect.left)/2; let y=rect.top+(rect.bottom-rect.top)/2;
                if victory_gpu::keyboard_window::captured(hwnd) && SetCursorPos(x,y)!=0 {
                    if self.centered { look=(((cursor.x-x) as f32/24.).clamp(-1.,1.),((y-cursor.y) as f32/24.).clamp(-1.,1.)); }
                    self.centered=true;
                } else { self.centered=false; }
            } else { self.centered=false; }
        }
        self.reading(down,look,Instant::now())
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    #[test]
    fn preset_reaches_gameinput() {
        use super::super::super::{gamepad_state, Reading, Value, READING};
        let mut host=MappedPad::default();
        let mut custom=MappedPad::default();
        custom.custom=true;
        custom.rules=custom_rules("46,51,45,52,05,06,02,01,26,28,25,27,E8,FF,4D,50").unwrap();
        let input=custom.reading(|k| [b'F',b'W',0x01,0xe8,0x75].contains(&k),(0.,0.),Instant::now());
        assert_eq!((input.buttons,input.left_y,input.right_trigger),(A|LEFT_STICK,1.,1.));
        assert!(!custom.driving);
        let start=Instant::now();
        let mut read=|keys:&[u8],ms:u64| {
            let pad=host.reading(|k| keys.contains(&k),(0.25,0.),start+Duration::from_millis(ms));
            let reading=Reading { vtable:&READING,refs:1.into(),device:std::ptr::null(),sequence:1,timestamp:0,value:Value::Pad(pad) };
            let mut result=PadState::default();
            assert!(gamepad_state(&reading,&mut result));
            result
        };
        let foot=read(b"SEQ",0);
        assert_eq!((foot.left_y,foot.left_trigger,foot.buttons),(-1.,0.,Y|RIGHT_SHOULDER));
        let drive=read(&[0x75,b'S',b'E',b'Q',b'H',1],1);
        assert_eq!((drive.left_y,drive.left_trigger,drive.right_trigger,drive.buttons),(0.,1.,0.,LEFT_STICK|LEFT|B|RIGHT_SHOULDER));
        assert_eq!(read(&[0x75,b'S'],2).left_trigger,1.);
        assert_eq!(read(&[0x12],3).buttons,RIGHT);
        assert_eq!(read(&[0x12],503).buttons,RIGHT|RIGHT_SHOULDER);
        assert_eq!(read(&[0x14],504).buttons,LEFT_STICK|RIGHT_STICK);
        let released=read(&[],505);
        assert_eq!((released.buttons,released.left_trigger,released.right_x),(0,0.,0.25));
        assert_eq!(read(&[0x75,b'S'],506).left_y,-1.);
        let foot=read(&[b'W',1,2,b'1',0x20],507);
        assert_eq!((foot.left_y,foot.right_trigger,foot.left_trigger,foot.buttons),(1.,1.,1.,A|Y));
        assert_eq!(read(b"1",508).buttons,0);
        let steering=read(&[0x75,b'W',b'D',1,2,b'1'],509);
        assert_eq!((steering.left_x,steering.left_y,steering.left_trigger,steering.right_trigger,steering.buttons),
                   (1.,0.,0.,1.,RIGHT_SHOULDER));
        for (key,button) in [(0x25,LEFT),(0x27,RIGHT),(0x26,UP),(0x28,DOWN),(0x04,UP),(0x08,B),(0x0D,A)] {
            let menu=read(&[key],510);
            assert_eq!((menu.buttons,menu.right_x,menu.right_y),(button,0.25,0.));
        }
        let neutral=read(&[],511);
        assert_eq!((neutral.buttons,neutral.left_x,neutral.left_y,neutral.left_trigger,neutral.right_trigger),(0,0.,0.,0.,0.));
    }
}
