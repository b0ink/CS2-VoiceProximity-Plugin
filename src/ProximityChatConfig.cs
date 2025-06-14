﻿using CounterStrikeSharp.API.Core;
using MessagePack;

namespace ProximityChat;

[MessagePackObject(true)]
public class Config : BasePluginConfig
{
    [IgnoreMember]
    public string? SocketURL { get; set; } = "https://cs2voiceproximity.chat";

    [IgnoreMember]
    public string? ApiKey { get; set; }

    [IgnoreMember]
    // Comma delimited list of admin groups and flags that have access to updating the config from the voice chat app
    public string ServerConfigAdmins { get; set; } = "#css/admin,@css/proximity-config";

    // Seconds before players are muted after dying
    [Key(0)]
    public float DeadPlayerMuteDelay { get; set; } = 1f;

    // Can dead teammates communicate to each other
    [Key(1)]
    public bool AllowDeadTeamVoice { get; set; } = true;

    // Can dead players speak when spectating C4
    [Key(2)]
    public bool AllowSpectatorC4Voice { get; set; } = true;

    // Maximum occlusion when player is closest to sound source.
    // The lower the number, the more muffled the player will be.
    [Key(3)]
    public float OcclusionNear { get; set; } = 300;

    // The maximum occlusion when player's distance reaches OcclusionEnd
    // The higher the number, the more clearer the player will sound at further distances.
    // Player becomes inaudible at around 25 and below
    [Key(4)]
    public float OcclusionFar { get; set; } = 25;

    // Distance from player where it fully reaches OcclusionFar
    [Key(5)]
    public float OcclusionEndDist { get; set; } = 2000;

    // Controls how quickly occlusion drops off with distance (higher = steeper drop near end, lower = more gradual fade)
    // https://www.desmos.com/calculator
    // Plug in `y=x^{exponent}` and zoom inbetween 0-1 on the X axis. The curve represents the sound occlusion falloff.
    // Set this value to 1 for a linear dropoff
    [Key(6)]
    public float OcclusionFalloffFactor { get; set; } = 3;

    // How quickly player voice volumes are reduced as you move away from them
    [Key(7)]
    public float VolumeFalloffFactor { get; set; } = 0.5f;

    // The max distance the player can be heard
    [Key(8)]
    public float VolumeMaxDistance { get; set; } = 2000;

    // Players are audible if they are within view, regardless of max distance settings
    [Key(9)]
    public bool AlwaysHearVisiblePlayers { get; set; } = true;

    // High-pass filter frequency for dead player voices (0 = disabled, higher = thinner/radio-like)
    [Key(10)]
    public int DeadVoiceFilterFrequency { get; set; } = 750;

    // Whether spectators are allowed to talk to alive players (false = muted, true = audible)
    [Key(11)]
    public bool SpectatorsCanTalk { get; set; } = false;
}
