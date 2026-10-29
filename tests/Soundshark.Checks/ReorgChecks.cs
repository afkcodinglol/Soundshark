using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Soundshark;
using Soundshark.Core;

internal static partial class Program
{
    static async Task ReorgChecks()
    {
        var old=new SoundboardConfiguration {HotkeysEnabled=false,PassHotkeysToOtherApps=false,AllowLeftRightModifiers=false,AutoKeysEnabled=true,AutoKeys=[new(){Enabled=true,Chord=new(0,0x54)}]};
        var normalized=old.ForSoundOutput();
        Check(normalized.HotkeysEnabled&&normalized.PassHotkeysToOtherApps&&normalized.AllowLeftRightModifiers&&!normalized.AutoKeysEnabled&&normalized.AutoKeys.Length==0,"upgrades enable automatic shortcut defaults and discard removed global Auto Keys");
        var chord=new SoundboardChord(2,5,2);
        var config=normalized with {SoundPtt=new(StringComparer.OrdinalIgnoreCase) { ["One.wav"]=new(true,chord) }};
        config.Validate();
        var keys=new FakeKeys();var lease=await AutoKeyLease.Acquire(config.ForSoundPtt("ONE.WAV"),false,CancellationToken.None,keys);
        Check(keys.Held.SetEquals(new ushort[]{0xa3,5}),"per-sound PTT holds the selected right-side modifier and mouse button");
        await lease.DisposeAsync();Check(keys.Held.Count==0,"per-sound PTT releases all owned inputs on completion");
        keys=new FakeKeys();lease=await AutoKeyLease.Acquire(config.ForSoundPtt("Two.wav"),true,CancellationToken.None,keys);await lease.DisposeAsync();
        Check(keys.Events.Count==0,"another sound does not inherit a sound-specific PTT binding");
        keys=new FakeKeys{FailOn=5};try{await AutoKeyLease.Acquire(config.ForSoundPtt("One.wav"),false,CancellationToken.None,keys);throw new Exception("Expected PTT failure");}catch(IOException){}
        Check(keys.Held.Count==0,"failed PTT acquisition releases a modifier already pressed");
        try {(config with {SoundPtt=new(){["One.wav"]=new(true,new(0,256))}}).Validate();throw new Exception("Wheel PTT accepted");}catch(ArgumentException){Check(true,"PTT rejects wheel impulses that cannot be held");}
        foreach(uint key in new uint[]{1,2,4,5,6,0x1b,0x7b,0xa3,0xb0,256,257,258,259})new SoundboardChord(0,key).Validate();
        Check(true,"keyboard Escape F12 modifiers media keys and mouse wheel inputs validate");
        Check(SoundboardHotkeys.MouseVirtualKey(0x20a,120u<<16)==256 && SoundboardHotkeys.MouseVirtualKey(0x20e,unchecked((uint)-120)<<16)==259,"vertical and horizontal wheel events map to separate shortcuts");
        var fired=new List<SoundboardBinding>();using(var hook=new SoundboardHotkeys(fired.Add,false)) {
            hook.Update(config with {Bindings=[new(){Command=SoundboardCommand.Stop,Chord=new(0,0xa3)}]});
            Check(!hook.ProcessInput(0xa3,false,2,2),"modifier-only shortcut passes the physical key through");hook.ProcessInput(0xa3,true,0,0);await Task.Delay(40);
            Check(fired.Count==1,"right Ctrl alone dispatches exactly once with automatic side detection");
        }
        var main=new MainWindow {Left=-20000,Top=-20000,ShowActivated=false};main.Show();await Task.Delay(100);
        Check(Descendants<CheckBox>(main.Studio).Contains(main.DiscordToggle),"Discord Soundboard toggle is in the Studio recording panel");
        var settings=new UiSettingsWindow(main){ShowActivated=false};settings.Show();await Task.Delay(40);
        Check(!Descendants<CheckBox>(settings).Any(c=>c.Content is string s && new[]{"Newest Sounds First","Compact Library Tiles","Show File Details"}.Contains(s)),"UI Settings no longer contains Library preferences or file-detail toggle");Snapshot(settings,"ui-settings-reorganized.png");settings.Close();
        string first=Path.Combine(Paths.Library,"One.wav"),second=Path.Combine(Paths.Library,"Two.wav");
        foreach(string file in new[]{first,second})using(var writer=new NAudio.Wave.WaveFileWriter(file,new NAudio.Wave.WaveFormat(48000,16,2)))writer.Write(new byte[48000*4],0,48000*4);
        main.Settings.LibraryPlayCounts["One.wav"]=10;await main.Settings.Save();await main.RefreshLibrary();
        var tabs=(TabControl)main.FindName("Tabs");tabs.SelectedIndex=1;await Task.Delay(100);
        var library=(LibraryPane)((ContentControl)main.FindName("LibraryHost")).Content;
        var rows=Descendants<ListBox>(library).Single();
        Check(rows.Items.Cast<LibraryPane.SoundRow>().Single(r=>r.Path==first).PlayCountText=="Play Count: 10" && rows.Items.Cast<LibraryPane.SoundRow>().Single(r=>r.Path==second).PlayCountText=="Play Count: 0","each sound has an independent play count under its file information");
        Check(Descendants<TextBlock>(library).Any(t=>t.Text=="Play Count: 10")&&!Descendants<TextBlock>(library).Any(t=>t.Text.StartsWith("Played:")),"Library tiles render individual counts without an aggregate counter");
        var newest=Descendants<CheckBox>(library).Single(c=>c.Name=="NewestSoundsFirst");var compact=Descendants<CheckBox>(library).Single(c=>c.Name=="CompactLibraryTiles");
        newest.IsChecked=true;newest.RaiseEvent(new RoutedEventArgs(CheckBox.ClickEvent));compact.IsChecked=false;compact.RaiseEvent(new RoutedEventArgs(CheckBox.ClickEvent));
        var deadline=DateTime.UtcNow.AddSeconds(3);while((!Settings.Load().Appearance.NewestFirst||Settings.Load().Appearance.CompactLibrary)&&DateTime.UtcNow<deadline)await Task.Delay(25);
        Check(Settings.Load().Appearance.NewestFirst&&!Settings.Load().Appearance.CompactLibrary&&Settings.Load().Appearance.ShowFileDetails,"grouped Library layout toggles save their preferences with file details always shown");Snapshot(main,"library-reorganized.png");
        var soundSettings=new SoundboardSettingsWindow(main){ShowActivated=false};soundSettings.Show();await Task.Delay(70);
        var configTabs=Descendants<TabControl>(soundSettings).Single();
        Check(configTabs.Items.Cast<TabItem>().Select(t=>t.Header.ToString()).SequenceEqual(new[]{"Audio","Hotkeys","Advanced"}),"Microphone EQ and Auto Keys tabs are removed");
        configTabs.SelectedIndex=1;await Task.Delay(50);
        Check(!Descendants<CheckBox>(soundSettings).Any() && Descendants<TextBlock>(soundSettings).Any(t=>t.Text=="Playback Controls"),"Hotkeys tab contains playback controls and no preference toggles");Snapshot(soundSettings,"playback-hotkeys.png");soundSettings.Close();
        var shortcut=new SoundShortcutWindow(main,first){ShowActivated=false};shortcut.Show();await Task.Delay(80);
        var hold=Descendants<CheckBox>(shortcut).Single(c=>c.Name=="HoldSoundPtt");var box=Descendants<HotkeyBox>(shortcut).Single(k=>k.Name=="SoundPttKey");
        hold.IsChecked=true;hold.RaiseEvent(new RoutedEventArgs(CheckBox.ClickEvent));
        box.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice,Environment.TickCount,MouseButton.XButton1){RoutedEvent=UIElement.PreviewMouseDownEvent});
        Check(box.Chord?.VirtualKey==5&&box.IsEnabled,"per-sound PTT box directly captures a mouse side button");
        var trigger=Descendants<HotkeyBox>(shortcut).Single(k=>k.Name!="SoundPttKey");
        trigger.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice,Environment.TickCount,MouseButton.Right){RoutedEvent=UIElement.PreviewMouseDownEvent});
        Check(trigger.Chord?.VirtualKey==2,"sound trigger captures the right button without opening a menu");
        trigger.ClearBinding();
        trigger.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice,Environment.TickCount,MouseButton.Left){RoutedEvent=UIElement.PreviewMouseDownEvent});
        Check(trigger.Chord==null,"first left click arms input capture without accidentally replacing the binding");
        trigger.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice,Environment.TickCount,MouseButton.Left){RoutedEvent=UIElement.PreviewMouseDownEvent});
        Check(trigger.Chord?.VirtualKey==1,"second left click directly binds the left mouse button");
        trigger.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice,PresentationSource.FromVisual(shortcut),0,Key.Escape){RoutedEvent=UIElement.PreviewKeyDownEvent});
        Check(trigger.Chord?.VirtualKey==0x1b,"Escape is bindable as an input rather than being consumed by clearing");
        trigger.RaiseEvent(new MouseWheelEventArgs(Mouse.PrimaryDevice,Environment.TickCount,120){RoutedEvent=UIElement.PreviewMouseWheelEvent});
        Check(trigger.Chord?.VirtualKey==256,"sound trigger captures the mouse wheel directly");Snapshot(shortcut,"sound-specific-ptt.png");
        Descendants<Button>(shortcut).Single(b=>Equals(b.Content,"Save")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        deadline=DateTime.UtcNow.AddSeconds(3);while(shortcut.IsVisible&&DateTime.UtcNow<deadline)await Task.Delay(25);
        var saved=Settings.Load().Soundboard;
        Check(!shortcut.IsVisible&&saved.SoundPtt["One.wav"].Chord?.VirtualKey==5&&saved.Bindings.Single().Chord.VirtualKey==256,"per-sound PTT and wheel trigger persist together");
        keys=new FakeKeys();lease=await AutoKeyLease.Acquire(config.ForSoundPtt("One.wav"),false,CancellationToken.None,keys);
        using(var player=new Playback()) {
            string pcm=Paths.NewPcm();using(var file=File.Create(pcm))file.SetLength(48000*8);
            var workspace=new Workspace();workspace.Load(pcm,"PTT lifecycle",false);
            player.Play(workspace,0,workspace.Frames,false,keys:lease);player.Stop();Check(keys.Held.Count==0,"stopping actual preview playback releases its PTT lease");
            keys=new FakeKeys();lease=await AutoKeyLease.Acquire(config.ForSoundPtt("One.wav"),false,CancellationToken.None,keys);
            player.Play(workspace,0,workspace.Frames,false,keys:lease);
            deadline=DateTime.UtcNow.AddSeconds(3);while(player.Active&&DateTime.UtcNow<deadline)await Task.Delay(20);
            Check(!player.Active&&keys.Held.Count==0,"natural preview completion releases its PTT lease");
            keys=new FakeKeys();lease=await AutoKeyLease.Acquire(config.ForSoundPtt("One.wav"),false,CancellationToken.None,keys);
            bool failed=false;try {player.Play(workspace,0,workspace.Frames,false,endpointId:"missing-ptt-test-endpoint",keys:lease);}catch{failed=true;}
            Check(failed&&keys.Held.Count==0,"preview device failure releases keys acquired before audio startup");
        }
        tabs.SelectedIndex=0;Descendants<Expander>(main.Studio).Single().IsExpanded=true;await Task.Delay(50);Snapshot(main,"studio-recording-options.png");
        main.Studio.Workspace.Dirty=false;main.Close();Console.WriteLine($"{passed} reorganization checks passed");
    }
}
