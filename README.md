# CS2 Voice Proximity (Plugin)

**Note: This plugin <ins>does not directly implement proximity chat features</ins>. Instead, it relays player position data to the [Voice Chat App](https://github.com/b0ink/CS2-VoiceProximity-Client), which handles the calculation of positional audio and sound occlusion.**

## Description

This plugin sends player positions and camera angles to the API server, which then broadcasts the data to connected users in the Voice app, where positional audio and sound occlusion are calculated.

## Links

[Voice Chat Client](https://github.com/b0ink/CS2-VoiceProximity-Client)

[API Server](https://github.com/b0ink/CS2-VoiceProximity-Server)

[CS2 Plugin](https://github.com/b0ink/CS2-VoiceProximity-Plugin)

## Plugin Config

Edit the plugin's config found in `addons/counterstrikesharp/configs/plugins/ProximityChat/ProximityChat.json`.\
<sub>(Note: config will be auto-generated after the plugin is loaded for the first time)</sub>

```jsonc
{
  // The address of the socket to relay player positions to.
  // Clients must be using the same URL (Region) defined here.
  "SocketURL": "https://au.cs2voiceproximity.chat",

  // The API key used to authenticate the connection from your CS2 server to the API.
  // Note: API keys are region-specific and only work with their corresponding socket URL.
  "ApiKey": "YOUR_API_KEY_HERE",

  // Number of milliseconds to wait before muting a player after they die.
  "DeadPlayerMuteDelay": 1000,

  // Allows dead teammates to communicate with each other while spectating.
  "AllowDeadTeamVoice": true,

  // Determines if dead players spectating the C4 can be heard by any alive players.
  "AllowSpectatorC4Voice": true,

  // How quickly player voice volumes are reduced as you move away from them.
  "RolloffFactor": 1,

  // The distance at which the volume reduction starts taking effect.
  "RefDistance": 39
}
```

## Official socket urls

- Oceania: `https://au.cs2voiceproximity.chat`
- Europe: `https://eu.cs2voiceproximity.chat`
