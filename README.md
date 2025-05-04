# CS2 Voice Proximity (Plugin)

**Note: This plugin <ins>does not directly implement proximity chat features</ins>. Instead, it relays player position data to the [Voice Chat App](https://github.com/b0ink/CS2-VoiceProximity-Client), which handles the calculation of positional audio and sound occlusion.**

## Description

This plugin saves the positions and camera angles of players into your database and is used by the API Server together with the Electron App Client to calculate positional audio and sound occlusion.

## Links

Voice Chat Client (https://github.com/b0ink/CS2-VoiceProximity-Client)

API Server (https://github.com/b0ink/CS2-VoiceProximity-Server)

CS2 Plugin (https://github.com/b0ink/CS2-VoiceProximity-Plugin)

## Database Config

Edit the plugin's config found in `addons/counterstrikesharp/configs/plugins/ProximityChat/ProximityChat.json`.\
<sub>(Note: config will be auto-generated after the plugin is loaded for the first time)</sub>

`DatabaseHost`: The host address of your MySql database.

`DatabasePort `: Database port.

`DatabaseUser`: Database user.

`DatabasePassword`: Database password.

`DatabaseName`: Database name.
