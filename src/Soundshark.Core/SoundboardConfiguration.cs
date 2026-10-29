namespace Soundshark.Core;

// Shared configuration for either microphone backend. No device is opened by loading settings.
public enum MicrophoneBackend { NotConfigured, VirtualCable, NativeMicrophone }
public enum SoundboardRoute { Preview, Microphone, Both }
public enum SoundboardVolumeMode { Automatic, FixedAdjustment, Unchanged }
public enum AutoKeyMode { HoldDuringPlayback, TapBeforeAndAfter }
public enum AutoKeyApplicationRule { AnyApplication, ForegroundApplication, RunningApplication }
public enum SoundboardCommand
{
    PlaySound, Stop, PauseResume, Restart, Previous, Next, Random,
    SeekBackward, SeekForward, SpeakerVolumeDown, SpeakerVolumeUp, ToggleSpeakers,
    ToggleMicrophoneRoute, ToggleHotkeys, ToggleAutoKeys, StartRecording, StopRecording
}

public sealed record SoundboardChord(uint Modifiers, uint VirtualKey, uint SideModifiers = 0)
{
    public void Validate()
    {
        if ((Modifiers & ~15u) != 0 || (SideModifiers & ~63u) != 0 || (VirtualKey is < 8 or > 254 && VirtualKey is not (1 or 2 or 4 or 5 or 6 or 256 or 257 or 258 or 259)))
            throw new ArgumentException("Choose a supported keyboard or mouse input.");
        if (((SideModifiers & 3) != 0 && (Modifiers & 2) == 0) || ((SideModifiers & 12) != 0 && (Modifiers & 1) == 0) || ((SideModifiers & 48) != 0 && (Modifiers & 4) == 0))
            throw new ArgumentException("Shortcut side modifiers must match the selected Ctrl, Alt or Shift keys.");
    }
}

public sealed record SoundboardBinding
{
    public SoundboardChord Chord { get; init; } = new(0, 0);
    public SoundboardCommand Command { get; init; } = SoundboardCommand.PlaySound;
    public string SoundFilename { get; init; } = "";
    public SoundboardRoute Route { get; init; } = SoundboardRoute.Microphone;
}

public sealed record SoundPttBinding(bool Enabled, SoundboardChord? Chord);

public sealed record SoundboardAutoKey
{
    public bool Enabled { get; init; }
    public SoundboardChord Chord { get; init; } = new(0, 0);
    public bool HotkeyPlaybackOnly { get; init; }
    public AutoKeyMode Mode { get; init; }
    public AutoKeyApplicationRule ApplicationRule { get; init; }
    public string[] Applications { get; init; } = [];
}

public sealed record SoundboardConfiguration
{
    public MicrophoneBackend Backend { get; init; }
    public string MicrophoneEndpointId { get; init; } = "";
    public string TransmissionEndpointId { get; init; } = "";
    public string MonitorEndpointId { get; init; } = "";
    public double SpeakerVolume { get; init; } = 1;
    public double TransmittedSoundVolume { get; init; } = 1;
    public double VoiceVolume { get; init; } = 1;
    public bool HearTransmittedSounds { get; init; }
    public bool BlockVoiceDuringSounds { get; init; }
    public SoundboardVolumeMode VolumeMode { get; init; } = SoundboardVolumeMode.Automatic;
    public double TargetLufs { get; init; } = -18;
    public double FixedAdjustmentDb { get; init; } = -15;
    public double MaximumBoostDb { get; init; } = 24;
    public bool LinkStereoChannels { get; init; } = true;
    public bool NormalizeSpeakerPreview { get; init; }
    public bool UseActivationSound { get; init; }
    public string ActivationSoundPath { get; init; } = "";
    public int StartDelayMs { get; init; }
    // Positive values delay transmission relative to local clip monitoring.
    public int RelativeMicrophoneDelayMs { get; init; }
    public int AutoKeyLeadInMs { get; init; }
    public int AutoKeyReleaseDelayMs { get; init; }
    public bool HotkeysEnabled { get; init; } = true;
    public bool PassHotkeysToOtherApps { get; init; } = true;
    public bool AllowLeftRightModifiers { get; init; } = true;
    public bool StopHotkeySoundsAtEnd { get; init; } = true;
    public bool RepeatSounds { get; init; }
    public bool AutoKeysEnabled { get; init; }
    public Dictionary<string,SoundPttBinding> SoundPtt { get; init; } = new(StringComparer.OrdinalIgnoreCase);
    public SoundboardBinding[] Bindings { get; init; } = [];
    public SoundboardAutoKey[] AutoKeys { get; init; } = [];

    // Legacy advanced fields remain readable, but microphone sound output always
    // uses one protected baseline. The user can only attenuate it with 0–100%.
    public static bool PlaybackCommand(SoundboardCommand command) => command is not (SoundboardCommand.ToggleHotkeys or SoundboardCommand.ToggleAutoKeys or SoundboardCommand.StartRecording or SoundboardCommand.StopRecording);
    public SoundboardConfiguration ForSoundPtt(string filename)
    {
        var ptt=SoundPtt.FirstOrDefault(p=>p.Key.Equals(Path.GetFileName(filename),StringComparison.OrdinalIgnoreCase)).Value;
        return this with { AutoKeysEnabled=ptt?.Enabled==true, AutoKeys=ptt is {Enabled:true,Chord:not null} ? [new SoundboardAutoKey { Enabled=true,Chord=ptt.Chord,Mode=AutoKeyMode.HoldDuringPlayback }] : [] };
    }
    public SoundboardConfiguration ForSoundOutput() => this with {
        HotkeysEnabled=true, PassHotkeysToOtherApps=true, AllowLeftRightModifiers=true,
        AutoKeysEnabled=false,AutoKeys=[], Bindings=Bindings.Where(b=>PlaybackCommand(b.Command)).ToArray(),
        SoundPtt=SoundPtt.GroupBy(p=>p.Key,StringComparer.OrdinalIgnoreCase).ToDictionary(g=>g.Key,g=>g.Last().Value,StringComparer.OrdinalIgnoreCase),
        VolumeMode = SoundboardVolumeMode.Automatic, TargetLufs = -18,
        MaximumBoostDb = 24, LinkStereoChannels = true,
        TransmittedSoundVolume = Math.Clamp(double.IsFinite(TransmittedSoundVolume) ? TransmittedSoundVolume : 1, 0, 1)
    };

    public void Validate()
    {
        static void Range(double value, double min, double max, string label)
        {
            if (!double.IsFinite(value) || value < min || value > max)
                throw new ArgumentException($"{label} must be between {min} and {max}.");
        }
        if (!Enum.IsDefined(Backend) || !Enum.IsDefined(VolumeMode)) throw new ArgumentException("Choose a supported routing and volume mode.");
        Range(SpeakerVolume, 0, 2, "Speaker volume");
        Range(TransmittedSoundVolume, 0, 2, "Sound volume");
        Range(VoiceVolume, 0, 2, "Voice volume");
        Range(TargetLufs, -53, -3, "Target loudness");
        Range(FixedAdjustmentDb, -30, 30, "Fixed volume adjustment");
        Range(MaximumBoostDb, 0, 30, "Maximum volume boost");
        Range(StartDelayMs, 0, 5000, "Start delay");
        Range(RelativeMicrophoneDelayMs, -1000, 1000, "Microphone timing offset");
        Range(AutoKeyLeadInMs, 0, 5000, "Automatic key lead-in");
        Range(AutoKeyReleaseDelayMs, 0, 5000, "Automatic key release delay");
        if (UseActivationSound && string.IsNullOrWhiteSpace(ActivationSoundPath)) throw new ArgumentException("Choose an activation sound or turn that option off.");
        if (Bindings == null || AutoKeys == null || Bindings.Length > 2048 || AutoKeys.Length > 32) throw new ArgumentException("The shortcut configuration is invalid or too large.");
        if (SoundPtt == null || SoundPtt.Count>2048) throw new ArgumentException("The per-sound PTT configuration is invalid.");
        foreach(var item in SoundPtt) {
            if (string.IsNullOrWhiteSpace(item.Key) || item.Key.IndexOfAny(['/', '\\', ':'])>=0 || item.Value==null) throw new ArgumentException("Choose a Library sound for push-to-talk.");
            if(!item.Value.Enabled)continue;
            if(item.Value.Chord==null)throw new ArgumentException("Choose a PTT key combination or turn off Hold down PTT button.");
            item.Value.Chord.Validate();
            if(item.Value.Chord.VirtualKey>=256)throw new ArgumentException("Mouse-wheel inputs cannot be held. Choose a key or mouse button for PTT.");
        }
        var assigned = new HashSet<SoundboardChord>();
        foreach (var binding in Bindings)
        {
            if (binding == null || binding.Chord == null) throw new ArgumentException("A shortcut is incomplete.");
            binding.Chord.Validate();
            if (!Enum.IsDefined(binding.Command) || !Enum.IsDefined(binding.Route)) throw new ArgumentException("A shortcut has an unsupported action.");
            var identity = AllowLeftRightModifiers ? binding.Chord : binding.Chord with { SideModifiers = 0 };
            if (assigned.Any(k => k.Modifiers == identity.Modifiers && k.VirtualKey == identity.VirtualKey && (k.SideModifiers == 0 || identity.SideModifiers == 0 || k.SideModifiers == identity.SideModifiers)) || !assigned.Add(identity)) throw new ArgumentException("Two actions use the same shortcut. Choose a different shortcut for one of them.");
            if (binding.Command == SoundboardCommand.PlaySound && (string.IsNullOrWhiteSpace(binding.SoundFilename) || binding.SoundFilename.IndexOfAny(['/', '\\', ':']) >= 0 || !FileRules.AudioExtensions.Contains(Path.GetExtension(binding.SoundFilename).ToLowerInvariant())))
                throw new ArgumentException("Choose a Library sound for each sound shortcut.");
        }
        foreach(var ptt in SoundPtt.Values.Where(p=>p.Enabled && p.Chord!=null)) {
            var chord=ptt.Chord!;
            if(assigned.Any(k=>k.VirtualKey==chord.VirtualKey && k.Modifiers==chord.Modifiers && (k.SideModifiers==0 || chord.SideModifiers==0 || k.SideModifiers==chord.SideModifiers))) throw new ArgumentException("A PTT key also starts a Soundshark action. Choose a different PTT combination.");
        }
        foreach (var key in AutoKeys)
        {
            if (key == null || key.Chord == null || !Enum.IsDefined(key.Mode) || !Enum.IsDefined(key.ApplicationRule)) throw new ArgumentException("An automatic key is incomplete.");
            if (!key.Enabled) continue;
            key.Chord.Validate();
            if (assigned.Any(k => k.Modifiers == key.Chord.Modifiers && k.VirtualKey == key.Chord.VirtualKey && (!AllowLeftRightModifiers || k.SideModifiers == 0 || key.Chord.SideModifiers == 0 || k.SideModifiers == key.Chord.SideModifiers))) throw new ArgumentException("An automatic key also starts a Soundshark action. Choose different shortcuts to avoid triggering playback again.");
            if (key.ApplicationRule != AutoKeyApplicationRule.AnyApplication && (key.Applications == null || !key.Applications.Any(a => !string.IsNullOrWhiteSpace(a))))
                throw new ArgumentException("Choose at least one application for the automatic key condition.");
        }
    }
}
