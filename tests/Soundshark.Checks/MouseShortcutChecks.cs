using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Soundshark;
using Soundshark.Core;

internal static partial class Program
{
    static async Task MouseShortcutChecks()
    {
        foreach (uint key in new uint[] { 1, 2, 4, 5, 6 }) {
            var chord = new SoundboardChord(2, key); chord.Validate();
            Check(HotkeyBox.Format(chord).StartsWith("Ctrl + Mouse"), "mouse button " + key + " validates and has a readable shortcut name");
        }
        Check(SoundboardHotkeys.MouseVirtualKey(0x20b, 1u << 16) == 5 && SoundboardHotkeys.MouseVirtualKey(0x20c, 2u << 16) == 6 && SoundboardHotkeys.MouseVirtualKey(0x207, 0) == 4 && SoundboardHotkeys.MouseVirtualKey(0x200, 0) == 0, "Windows side and middle button messages map correctly while mouse movement is ignored");
        var one = new SoundboardBinding { Chord = new(2, 5, 1), SoundFilename = "one.wav", Route = SoundboardRoute.Preview };
        var two = new SoundboardBinding { Chord = new(0, 6), SoundFilename = "two.wav", Route = SoundboardRoute.Both };
        var config = new SoundboardConfiguration { AllowLeftRightModifiers = true, PassHotkeysToOtherApps = false, Bindings = [one, two] };
        var fired = new List<SoundboardBinding>();
        using var hotkeys = new SoundboardHotkeys(fired.Add, installHooks: false); hotkeys.Update(config);
        Check(!hotkeys.ProcessInput(5, false, 0, 0) && !hotkeys.ProcessInput(5, true, 0, 0), "unmatched mouse chords pass through normally");
        Check(hotkeys.ProcessInput(5, false, 2, 1) && hotkeys.ProcessInput(5, false, 2, 1) && hotkeys.ProcessInput(5, true, 0, 0), "matched mouse down repeats and release are suppressed consistently");
        await Task.Delay(25);
        Check(fired.Count == 1 && fired[0] == one, "one mouse press dispatches the exact sound and Preview route only once");
        hotkeys.ProcessInput(5, false, 2, 2); hotkeys.ProcessInput(5, true, 0, 0); await Task.Delay(20);
        Check(fired.Count == 1, "mouse shortcuts respect distinct left and right modifier settings");
        hotkeys.Suspended = true; hotkeys.ProcessInput(6, false, 0, 0); hotkeys.ProcessInput(6, true, 0, 0); hotkeys.Suspended = false;
        await Task.Delay(20); Check(fired.Count == 1, "shortcut capture suspension prevents mouse-triggered playback while editing");
        hotkeys.Update(config with { PassHotkeysToOtherApps = true });
        Check(!hotkeys.ProcessInput(6, false, 0, 0) && !hotkeys.ProcessInput(6, true, 0, 0), "mouse pass-through preserves both physical button transitions");
        await Task.Delay(20); Check(fired.Count == 2 && fired[1].Route == SoundboardRoute.Both, "pass-through mouse bindings still dispatch their saved Both route");
        hotkeys.Update(config with { HotkeysEnabled = false }); hotkeys.ProcessInput(6, false, 0, 0); hotkeys.ProcessInput(6, true, 0, 0);
        await Task.Delay(20); Check(fired.Count == 2, "global shortcut disable also disables mouse playback bindings");
        try { (config with { Bindings = [one, one with { SoundFilename = "duplicate.wav" }] }).Validate(); throw new Exception("duplicate mouse shortcut accepted"); }
        catch (ArgumentException) { Check(true, "duplicate mouse shortcuts are rejected by the shared conflict checks"); }
        var roundTrip = JsonSerializer.Deserialize<SoundboardConfiguration>(JsonSerializer.Serialize(config))!; roundTrip.Validate();
        Check(roundTrip.Bindings[0].Chord == one.Chord && roundTrip.Bindings[1].Route == two.Route, "mouse bindings and playback routes survive settings serialization");
        var box = new HotkeyBox(null);
        box.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice,Environment.TickCount,MouseButton.Right) { RoutedEvent=UIElement.PreviewMouseDownEvent });
        Check(box.Chord?.VirtualKey==2,"right mouse button is captured directly without a context menu");
        box.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice,Environment.TickCount,MouseButton.Middle) { RoutedEvent=UIElement.PreviewMouseDownEvent });
        Check(box.Chord?.VirtualKey==4,"middle mouse button is captured directly");
        box.ClearBinding(); Check(box.Chord==null,"Clear removes a captured shortcut");
        var keys = new FakeKeys();
        var automatic = new SoundboardConfiguration { AutoKeysEnabled = true, AutoKeys = [new() { Enabled = true, Chord = new(2, 5) }] };
        var lease = await AutoKeyLease.Acquire(automatic, false, CancellationToken.None, keys); await lease.DisposeAsync();
        Check(keys.Events.SequenceEqual(new[] { ((ushort)0xa2, false), ((ushort)5, false), ((ushort)5, true), ((ushort)0xa2, true) }) && keys.Held.Count == 0, "automatic mouse bindings balance modifier and button press/release ownership");
        using (var native = new SoundboardHotkeys(_ => { })) { native.Update(config with { Bindings = [two, new() { Chord = new(2, 0x4b), Command = SoundboardCommand.Stop }] }); native.Update(new()); }
        Check(true, "Windows accepts and removes combined keyboard and mouse hooks cleanly");
    }
}
