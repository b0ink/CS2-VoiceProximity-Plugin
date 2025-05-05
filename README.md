# CS2 Voice Proximity (Plugin)

**Note: This plugin <ins>does not directly implement proximity chat features</ins>. Instead, it relays player position data to the [Voice Chat App](https://github.com/b0ink/CS2-VoiceProximity-Client), which handles the calculation of positional audio and sound occlusion.**

## Description

This plugin sends player positions and camera angles to the API server, which then broadcasts the data to connected users in the Electron app, where positional audio and sound occlusion are calculated.

## Links

Voice Chat Client (https://github.com/b0ink/CS2-VoiceProximity-Client)

API Server (https://github.com/b0ink/CS2-VoiceProximity-Server)

CS2 Plugin (https://github.com/b0ink/CS2-VoiceProximity-Plugin)

## Database Config

Edit the plugin's config found in `addons/counterstrikesharp/configs/plugins/ProximityChat/ProximityChat.json`.\
<sub>(Note: config will be auto-generated after the plugin is loaded for the first time)</sub>

`SocketUrl`: The address of the socket url. Defaults to the `https://cs2voiceproximity.chat`.

`ApiKey`: The API key used to authenticate the connection from your CS2 server to the API.
