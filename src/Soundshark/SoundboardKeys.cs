using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using Soundshark.Core;
namespace Soundshark;
static class KeyNative
{
    public const uint Tag = 0x53534B4B;
    [DllImport("user32.dll")] public static extern short GetAsyncKeyState(int key);
    [DllImport("user32.dll")] static extern uint SendInput(uint count, [In] Input[] input, int size);
    [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr window, out uint process);
    [StructLayout(LayoutKind.Sequential)] struct Keyboard { public ushort Vk, Scan; public uint Flags, Time; public UIntPtr Extra; }
    [StructLayout(LayoutKind.Sequential)] struct MouseInput { public int X, Y; public uint Data, Flags, Time; public UIntPtr Extra; }
    [StructLayout(LayoutKind.Explicit, Size = 40)] struct Input { [FieldOffset(0)] public uint Type; [FieldOffset(8)] public Keyboard Keyboard; [FieldOffset(8)] public MouseInput Mouse; }
    public static bool MouseKey(uint key) => key is 1 or 2 or 4 or 5 or 6 or 256 or 257 or 258 or 259;
    public static bool Down(int key) => (GetAsyncKeyState(key) & 0x8000) != 0;
    public static void Send(ushort key, bool up)
    {
        if (key>=256) throw new SoundboardException("A mouse wheel cannot be held for PTT.");
        uint extended = key is 0xa3 or 0xa5 or 0x5b or 0x5c or >= 0x21 and <= 0x2e ? 1u : 0;
        var value = new Input { Type = 1, Keyboard = new() { Vk = key, Flags = extended | (up ? 2u : 0), Extra = Tag } };
        if (MouseKey(key)) value = new Input { Type = 0, Mouse = new() { Flags = key switch { 1 => up ? 4u : 2u, 2 => up ? 16u : 8u, 4 => up ? 64u : 32u, _ => up ? 256u : 128u }, Data = key == 5 ? 1u : key == 6 ? 2u : 0u, Extra = Tag } };
        if (SendInput(1, [value], Marshal.SizeOf<Input>()) != 1) throw new SoundboardException("Couldn't press the automatic key. Soundshark and your voice app or game may need the same permission level.");
    }
    public static ushort[] Keys(SoundboardChord chord)
    {
        var result = new List<ushort>();
        void Modifier(uint generic, uint left, uint right, ushort leftKey, ushort rightKey) {
            if ((chord.Modifiers & generic) == 0) return;
            if ((chord.SideModifiers & (left | right)) == 0 || (chord.SideModifiers & left) != 0) result.Add(leftKey);
            if ((chord.SideModifiers & right) != 0) result.Add(rightKey);
        }
        Modifier(2, 1, 2, 0xa2, 0xa3); Modifier(1, 4, 8, 0xa4, 0xa5); Modifier(4, 16, 32, 0xa0, 0xa1);
        if ((chord.Modifiers & 8) != 0) result.Add(0x5b);
        result.Add((ushort)chord.VirtualKey); return result.ToArray();
    }
    public static bool Applies(SoundboardAutoKey key)
    {
        if (key.ApplicationRule == AutoKeyApplicationRule.AnyApplication) return true;
        static string Clean(string name) => name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? name[..^4] : name;
        try {
            if (key.ApplicationRule == AutoKeyApplicationRule.ForegroundApplication) {
                GetWindowThreadProcessId(GetForegroundWindow(), out uint id); using var process = Process.GetProcessById((int)id);
                return key.Applications.Any(a => string.Equals(Clean(a.Trim()), process.ProcessName, StringComparison.OrdinalIgnoreCase));
            }
            return key.Applications.Any(a => { var list = Process.GetProcessesByName(Clean(a.Trim())); try { return list.Length > 0; } finally { foreach (var p in list) p.Dispose(); } });
        } catch { return false; }
    }
}
public interface ISoundboardKeyTransport
{
    bool Down(ushort key);
    void Send(ushort key, bool up);
    bool Applies(SoundboardAutoKey binding);
}
sealed class WindowsKeyTransport : ISoundboardKeyTransport
{
    public bool Down(ushort key) => KeyNative.Down(key);
    public void Send(ushort key, bool up) => KeyNative.Send(key, up);
    public bool Applies(SoundboardAutoKey binding) => KeyNative.Applies(binding);
}
public sealed class AutoKeyLease : IAsyncDisposable
{
    readonly ISoundboardKeyTransport transport;
    AutoKeyLease(ISoundboardKeyTransport transport) { this.transport = transport; }
    readonly List<ushort> held = [];
    readonly List<SoundboardAutoKey> taps = [];
    async Task Tap(SoundboardAutoKey binding, CancellationToken ct)
    {
        var owned = new List<ushort>();
        try { foreach (ushort key in KeyNative.Keys(binding.Chord)) if (!transport.Down(key)) { transport.Send(key, false); owned.Add(key); }
            await Task.Delay(20, ct).ConfigureAwait(false);
        } finally { foreach (ushort key in owned.AsEnumerable().Reverse()) try { transport.Send(key, true); } catch (Exception ex) { AppLog.Warn("soundboard.autokey.release", ex); } }
    }
    public static async Task<AutoKeyLease> Acquire(SoundboardConfiguration config, bool fromHotkey, CancellationToken ct, ISoundboardKeyTransport? transport = null)
    {
        var lease = new AutoKeyLease(transport ?? new WindowsKeyTransport());
        try {
            if (config.AutoKeysEnabled) foreach (var binding in config.AutoKeys.Where(k => k.Enabled && (!k.HotkeyPlaybackOnly || fromHotkey) && lease.transport.Applies(k))) {
                ct.ThrowIfCancellationRequested();
                if (binding.Mode == AutoKeyMode.TapBeforeAndAfter) { await lease.Tap(binding, ct).ConfigureAwait(false); lease.taps.Add(binding); }
                else foreach (ushort key in KeyNative.Keys(binding.Chord)) if (!lease.transport.Down(key)) { lease.transport.Send(key, false); lease.held.Add(key); }
            }
            return lease;
        } catch { await lease.DisposeAsync().ConfigureAwait(false); throw; }
    }
    public async ValueTask DisposeAsync()
    {
        foreach (ushort key in held.AsEnumerable().Reverse()) try { transport.Send(key, true); } catch (Exception ex) { AppLog.Warn("soundboard.autokey.release", ex); }
        held.Clear();
        foreach (var tap in taps) if (transport.Applies(tap)) try { await Tap(tap, CancellationToken.None).ConfigureAwait(false); } catch (Exception ex) { AppLog.Warn("soundboard.autokey.tap", ex); }
        taps.Clear();
    }
}
public sealed class HotkeyBox : TextBox
{
    public SoundboardChord? Chord { get; private set; }
    readonly Func<bool> sides;
    bool listening;
    uint pendingModifier;
    HwndSource? source;
    public HotkeyBox(SoundboardChord? chord, Func<bool>? allowSides = null)
    {
        SetResourceReference(StyleProperty, typeof(TextBox));
        sides=allowSides??(()=>true); Width=180; IsReadOnly=true; Chord=chord; UpdateText();
        ToolTip="Click to listen, then press a key, combination, mouse button or wheel. A second left click while listening binds Mouse Left. Use Clear to remove it.";
        GotKeyboardFocus+=(_,_)=> { listening=true; UpdateText(); };
        LostKeyboardFocus+=(_,_)=> { listening=false; pendingModifier=0; UpdateText(); };
        PreviewKeyDown+=(_,e)=>{
            e.Handled=true;
            var key=e.Key==Key.System?e.SystemKey:e.Key==Key.ImeProcessed?e.ImeProcessedKey:e.Key;
            uint vk=(uint)KeyInterop.VirtualKeyFromKey(key);
            if(ModifierMask(vk)!=0) { listening=true; pendingModifier=vk; return; }
            pendingModifier=0; Capture(vk);
        };
        PreviewKeyUp+=(_,e)=>{
            uint vk=(uint)KeyInterop.VirtualKeyFromKey(e.Key==Key.System?e.SystemKey:e.Key);
            if(listening&&pendingModifier==vk) Capture(vk);
            pendingModifier=0; e.Handled=true;
        };
        PreviewMouseDown+=(_,e)=>{
            uint key=e.ChangedButton switch {MouseButton.Left=>1u,MouseButton.Right=>2u,MouseButton.Middle=>4u,MouseButton.XButton1=>5u,MouseButton.XButton2=>6u,_=>0u};
            if(key==0)return;
            bool wasListening=IsKeyboardFocused&&listening;
            Focus(); e.Handled=true;
            if(key==1&&!wasListening) {listening=true;UpdateText();return;}
            Capture(key);
        };
        PreviewMouseUp+=(_,e)=>e.Handled=true;
        PreviewMouseWheel+=(_,e)=> {if(!IsKeyboardFocusWithin)return;Capture(e.Delta>0?256u:257u);e.Handled=true;};
        ContextMenuOpening+=(_,e)=>e.Handled=true;
        Loaded+=(_,_)=>{ source=PresentationSource.FromVisual(this) as HwndSource;source?.AddHook(WindowInput); };
        Unloaded+=(_,_)=>{source?.RemoveHook(WindowInput);source=null;};
    }
    IntPtr WindowInput(IntPtr window,int message,IntPtr wParam,IntPtr lParam,ref bool handled)
    {
        if(message==0x20e&&IsKeyboardFocusWithin) {Capture((short)((wParam.ToInt64()>>16)&0xffff)>0?258u:259u);handled=true;}
        return IntPtr.Zero;
    }
    public static uint ModifierMask(uint key)=>key switch {0x10 or 0xa0 or 0xa1=>4u,0x11 or 0xa2 or 0xa3=>2u,0x12 or 0xa4 or 0xa5=>1u,0x5b or 0x5c=>8u,_=>0u};
    public static (uint Mods,uint Sides) WithoutMainModifier(uint key,uint mods,uint side)
    {
        uint mask=ModifierMask(key); mods&=~mask;
        if((mods&2)==0)side&=~3u;if((mods&1)==0)side&=~12u;if((mods&4)==0)side&=~48u;
        return(mods,side);
    }
    void Capture(uint vk)
    {
        uint mods=0,side=0;
        if(Keyboard.Modifiers.HasFlag(ModifierKeys.Alt))mods|=1;
        if(Keyboard.Modifiers.HasFlag(ModifierKeys.Control))mods|=2;
        if(Keyboard.Modifiers.HasFlag(ModifierKeys.Shift))mods|=4;
        if(Keyboard.Modifiers.HasFlag(ModifierKeys.Windows))mods|=8;
        if(sides()) {int[] keys=[0xa2,0xa3,0xa4,0xa5,0xa0,0xa1];for(int i=0;i<keys.Length;i++)if(KeyNative.Down(keys[i]))side|=1u<<i;}
        var normalized=WithoutMainModifier(vk,mods,side);
        var value=new SoundboardChord(normalized.Mods,vk,normalized.Sides);
        try {value.Validate();Chord=value;listening=false;UpdateText();}catch(ArgumentException){ }
    }
    public void ClearBinding() {Chord=null;listening=false;pendingModifier=0;UpdateText();}
    void UpdateText()=>Text=listening?"Listening… press an input":Chord==null?"Click To Set Shortcut":Format(Chord);
    public static string Format(SoundboardChord chord)
    {
        var words = new List<string>();
        if ((chord.Modifiers & 2) != 0) words.Add((chord.SideModifiers & 2) != 0 ? "Right Ctrl" : (chord.SideModifiers & 1) != 0 ? "Left Ctrl" : "Ctrl");
        if ((chord.Modifiers & 1) != 0) words.Add((chord.SideModifiers & 8) != 0 ? "Right Alt" : (chord.SideModifiers & 4) != 0 ? "Left Alt" : "Alt");
        if ((chord.Modifiers & 4) != 0) words.Add((chord.SideModifiers & 32) != 0 ? "Right Shift" : (chord.SideModifiers & 16) != 0 ? "Left Shift" : "Shift");
        if ((chord.Modifiers & 8) != 0) words.Add("Win");
        words.Add(chord.VirtualKey switch { 1 => "Mouse Left", 2 => "Mouse Right", 4 => "Mouse Middle", 5 => "Mouse 4 (Back)", 6 => "Mouse 5 (Forward)", 256=>"Wheel Up",257=>"Wheel Down",258=>"Wheel Right",259=>"Wheel Left", _ => KeyInterop.KeyFromVirtualKey((int)chord.VirtualKey).ToString() }); return string.Join(" + ", words);
    }
}
public sealed class SoundboardHotkeys : IDisposable
{
    delegate IntPtr Hook(int code, IntPtr message, IntPtr data);
    [DllImport("user32.dll", SetLastError = true)] static extern IntPtr SetWindowsHookEx(int kind, Hook callback, IntPtr module, uint thread);
    [DllImport("user32.dll")] static extern bool UnhookWindowsHookEx(IntPtr hook);
    [DllImport("user32.dll")] static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr message, IntPtr data);
    [DllImport("kernel32.dll")] static extern IntPtr GetModuleHandle(string? name);
    [StructLayout(LayoutKind.Sequential)] struct KeyData { public uint Key, Scan, Flags, Time; public UIntPtr Extra; }
    [StructLayout(LayoutKind.Sequential)] struct MouseData { public int X, Y; public uint Data, Flags, Time; public UIntPtr Extra; }
    readonly Hook callback, mouseCallback;
    readonly bool installHooks;
    readonly Action<SoundboardBinding> dispatch;
    readonly HashSet<uint> pressed = [], suppressed = [];
    static readonly int[] sideKeys = [0xa2,0xa3,0xa4,0xa5,0xa0,0xa1];
    IntPtr hook, mouseHook;
    SoundboardConfiguration config = new();
    public bool Suspended { get; set; }
    public SoundboardHotkeys(Action<SoundboardBinding> dispatch, bool installHooks = true) { this.dispatch = dispatch; this.installHooks = installHooks; callback = Callback; mouseCallback = MouseCallback; }
    public void Update(SoundboardConfiguration next)
    {
        next.Validate();
        bool keyboard = next.Bindings.Any(b => !KeyNative.MouseKey(b.Chord.VirtualKey)), mouse = next.Bindings.Any(b => KeyNative.MouseKey(b.Chord.VirtualKey));
        IntPtr addedKeyboard = IntPtr.Zero, addedMouse = IntPtr.Zero;
        try {
            if (installHooks && keyboard && hook == IntPtr.Zero) { addedKeyboard = SetWindowsHookEx(13, callback, GetModuleHandle(null), 0); if (addedKeyboard == IntPtr.Zero) throw new SoundboardException("Couldn't enable global keyboard shortcuts."); }
            if (installHooks && mouse && mouseHook == IntPtr.Zero) { addedMouse = SetWindowsHookEx(14, mouseCallback, GetModuleHandle(null), 0); if (addedMouse == IntPtr.Zero) throw new SoundboardException("Couldn't enable global mouse shortcuts."); }
        } catch { if (addedKeyboard != IntPtr.Zero) UnhookWindowsHookEx(addedKeyboard); if (addedMouse != IntPtr.Zero) UnhookWindowsHookEx(addedMouse); throw; }
        if (addedKeyboard != IntPtr.Zero) hook = addedKeyboard; if (addedMouse != IntPtr.Zero) mouseHook = addedMouse;
        if (!keyboard && hook != IntPtr.Zero) { UnhookWindowsHookEx(hook); hook = IntPtr.Zero; }
        if (!mouse && mouseHook != IntPtr.Zero) { UnhookWindowsHookEx(mouseHook); mouseHook = IntPtr.Zero; }
        config = next;
        if (next.Bindings.Length == 0) { pressed.Clear(); suppressed.Clear(); }
    }
    IntPtr Callback(int code, IntPtr message, IntPtr data)
    {
        try { return HandleKey(code, message, data); }
        catch { return CallNextHookEx(hook, code, message, data); }
    }
    IntPtr HandleKey(int code, IntPtr message, IntPtr data)
    {
        if (code < 0) return CallNextHookEx(hook, code, message, data);
        var key = Marshal.PtrToStructure<KeyData>(data);
        if (key.Extra.ToUInt64() == KeyNative.Tag) return CallNextHookEx(hook, code, message, data);
        var (mods, sides) = Modifiers();
        return ProcessInput(key.Key, (key.Flags & 0x80) != 0, mods, sides) ? 1 : CallNextHookEx(hook, code, message, data);
    }
    IntPtr MouseCallback(int code, IntPtr message, IntPtr data)
    {
        try {
            if (code >= 0 && message.ToInt64() is 0x201 or 0x202 or 0x204 or 0x205 or 0x207 or 0x208 or 0x20b or 0x20c or 0x20a or 0x20e) {
                var mouse = Marshal.PtrToStructure<MouseData>(data);
                if (mouse.Extra.ToUInt64() != KeyNative.Tag) {
                    var (mods, sides) = Modifiers();
                    uint key = MouseVirtualKey((int)message, mouse.Data);
                    if(message.ToInt64() is 0x20a or 0x20e) {
                        bool suppress=ProcessInput(key,false,mods,sides);ProcessInput(key,true,mods,sides);
                        if(suppress)return 1;
                        return CallNextHookEx(mouseHook,code,message,data);
                    }
                    bool up = message.ToInt64() is 0x202 or 0x205 or 0x208 or 0x20c;
                    if (key != 0 && ProcessInput(key, up, mods, sides)) return 1;
                }
            }
        } catch { }
        return CallNextHookEx(mouseHook, code, message, data);
    }
    public static uint MouseVirtualKey(int message, uint data) => message switch {
        0x201 or 0x202 => 1, 0x204 or 0x205 => 2, 0x207 or 0x208 => 4,
        0x20b or 0x20c => (data >> 16) switch { 1 => 5u, 2 => 6u, _ => 0u },
        0x20a => (short)(data>>16)>0?256u:257u,0x20e => (short)(data>>16)>0?258u:259u, _ => 0
    };
    static (uint Mods, uint Sides) Modifiers()
    {
        uint mods = 0, sides = 0;
        for (int i = 0; i < sideKeys.Length; i++) if (KeyNative.Down(sideKeys[i])) sides |= 1u << i;
        if ((sides & 3) != 0) mods |= 2; if ((sides & 12) != 0) mods |= 1; if ((sides & 48) != 0) mods |= 4;
        if (KeyNative.Down(0x5b) || KeyNative.Down(0x5c)) mods |= 8;
        return (mods, sides);
    }
    public bool ProcessInput(uint key, bool up, uint mods, uint sides)
    {
        if (up) { pressed.Remove(key); return suppressed.Remove(key); }
        if (!pressed.Add(key)) return suppressed.Contains(key);
        if (Suspended) return false;
        (mods,sides)=HotkeyBox.WithoutMainModifier(key,mods,sides);
        foreach (var binding in config.Bindings) {
            if (!config.HotkeysEnabled && binding.Command != SoundboardCommand.ToggleHotkeys) continue;
            if (binding.Chord.VirtualKey != key || binding.Chord.Modifiers != mods || config.AllowLeftRightModifiers && binding.Chord.SideModifiers != 0 && binding.Chord.SideModifiers != sides) continue;
            _ = Application.Current.Dispatcher.BeginInvoke(() => dispatch(binding));
            if (!config.PassHotkeysToOtherApps) { suppressed.Add(key); return true; }
            break;
        }
        return false;
    }
    public void Dispose() { if (hook != IntPtr.Zero) { UnhookWindowsHookEx(hook); hook = IntPtr.Zero; } if (mouseHook != IntPtr.Zero) { UnhookWindowsHookEx(mouseHook); mouseHook = IntPtr.Zero; } pressed.Clear(); suppressed.Clear(); }
}
