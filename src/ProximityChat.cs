using System.Net;
using System.Reflection;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Core.Attributes.Registration;
using CounterStrikeSharp.API.Modules.Cvars;
using CounterStrikeSharp.API.Modules.Extensions;
using CounterStrikeSharp.API.Modules.Utils;
using MessagePack;
using Microsoft.Extensions.Logging;
using SocketIOClient;

namespace ProximityChat;

public class ExceptionPayload
{
    public int code { get; set; }
    public string? message { get; set; }
}

public class ServerRestartWarning
{
    public float minutes { get; set; }
}

public partial class ProximityChat : BasePlugin, IPluginConfig<Config>
{
    public override string ModuleName => "Proximity Chat API";
    public override string ModuleAuthor => "b0ink";
    public override string ModuleVersion => PluginVersion ?? "n/a";

    public Config Config { get; set; } = new();

    public Dictionary<ulong, PlayerData> PlayerData = new();

    private CancellationTokenSource? _cts;
    private Task? _socketTask;
    public SocketIOClient.SocketIO? socket = null;

    public string? hostAddress = null;
    public string? hostPort = null;

    public string CurrentMap = "";

    public bool DEBUG_FAKE_PLAYERS = false;

    public bool tryReconnectSocket = true;

    public Vector debugPlayerPosition = new Vector();

    public Dictionary<string, int> DoorRotations = new();
    public List<CPropDoorRotating?> DoorEntities = new();

    public List<string?> fakeBots = new();

    public float _nextSaveAt;

    public override void Load(bool hotReload)
    {
        if (Config.ApiKey == null)
        {
            Logger.LogError($"Invalid no Api Key set in Proximity Chat Config.");
            throw new Exception($"Invalid or no ApiKey set in Proximity Chat Config.");
        }

        CurrentMap = Server.MapName;

        // TODO: try/catch this, CRayTrace should be an optional extension
        CRayTrace.Init();

        RegisterListener<Listeners.OnTick>(() =>
        {
            foreach (var door in DoorEntities)
            {
                if (door == null || !door.IsValid || door.AbsOrigin == null || door.AbsRotation == null)
                {
                    continue;
                }
                var rotation = door.AbsRotation.Clone();
                var doorId = GetDoorKey(door.AbsOrigin);
                int currentRotation = (int)Math.Floor(rotation.Y);
                if (!DoorRotations.ContainsKey(doorId))
                {
                    DoorRotations[doorId] = currentRotation;
                }

                int lastDoorRotation = DoorRotations[doorId];

                if (currentRotation != lastDoorRotation)
                {
                    DoorRotations[doorId] = currentRotation;

                    var origin = door.AbsOrigin.Clone();
                    Task.Run(() =>
                    {
                        socket?.EmitAsync("door-rotation", "proximity-chat", $"{origin.X} {origin.Y} {origin.Z}", currentRotation);
                    });
                }
            }

            var now = Server.CurrentTime;

            if (now < _nextSaveAt)
                return;

            _nextSaveAt = now + 0.1f; // 100ms

            SaveAllPlayersPositions();

            for (int i = 0; i < Server.MaxPlayers; i++)
            {
                var player = Utilities.GetPlayerFromSlot(i);

                if (player == null || !player.IsValid)
                {
                    continue;
                }

                // Prevent players from being removed during map changes
                if (player.Connected == PlayerConnectedState.PlayerDisconnected)
                {
                    var authorisedSteamID = player.AuthorizedSteamID?.SteamId64;
                    if (authorisedSteamID != null && PlayerData.ContainsKey((ulong)authorisedSteamID))
                    {
                        PlayerData.Remove((ulong)authorisedSteamID);
                    }
                    else if (PlayerData.ContainsKey(player.SteamID))
                    {
                        PlayerData.Remove(player.SteamID);
                    }
                }
            }
        });

        RegisterListener<Listeners.OnClientAuthorized>(
            (slot, steamId) =>
            {
                CheckAdmin(steamId);
            }
        );

        RegisterListener<Listeners.OnMapStart>(mapName =>
        {
            CurrentMap = mapName;
            if (_cts == null && _socketTask == null && (socket == null || !socket.Connected))
            {
                Logger.LogInformation("OnMapStart: Init server");
                InitServer();
            }
            else
            {
                NotifyMapChange();
                NotifyServerConfig();
            }
        });

        RegisterListener<Listeners.OnMapEnd>(() =>
        {
            // Triggers all players to have mono audio until they join a team again
            foreach (var player in PlayerData)
            {
                player.Value.IsAlive = false;
                player.Value.Team = 0;
            }
        });
    }

    public void InitServer()
    {
        var ipInt = ConVar.Find("hostip")?.GetPrimitiveValue<int>();
        if (ipInt != null)
        {
            byte[] bytes = BitConverter.GetBytes((int)ipInt);
            Array.Reverse(bytes);
            string ipString = new IPAddress(bytes).ToString();
            hostAddress = ipString != null ? ipString : null;
        }

        var port = ConVar.Find("hostport")?.GetPrimitiveValue<int>();
        hostPort = port != null ? port.ToString() : null;

        Server.NextFrame(() =>
        {
            if (_cts != null)
            {
                _cts.Cancel();
                _socketTask?.Wait();
                _cts.Dispose();
                _cts = null;
                _socketTask = null;
            }
            _cts = new CancellationTokenSource();
            _socketTask = Task.Run(() => InitSocketIO(_cts.Token));
        });
    }

    private async Task InitSocketIO(CancellationToken token)
    {
        var query = new List<KeyValuePair<string, string>>();
        if (Config.ApiKey != null)
        {
            query.Add(new KeyValuePair<string, string>("api-key", Config.ApiKey));
        }
        if (hostAddress != null)
        {
            query.Add(new KeyValuePair<string, string>("server-address", hostAddress));
        }
        if (hostPort != null)
        {
            query.Add(new KeyValuePair<string, string>("server-port", hostPort));
        }
        if (PluginVersion != null)
        {
            query.Add(new KeyValuePair<string, string>("plugin-version", PluginVersion));
        }

        if (socket == null || !socket.Connected)
        {
            socket = new SocketIOClient.SocketIO(
                Config.SocketURL,
                new SocketIOOptions
                {
                    //ReconnectionAttempts = 3,
                    Reconnection = false,
                    Query = query,
                }
            );
            socket.OnConnected += (sender, e) =>
            {
                Logger.LogInformation("Socket connected successfully.");
                _ = Task.Run(
                    async () =>
                    {
                        while (!token.IsCancellationRequested)
                        {
                            var playerList = PlayerData.Values.ToList();
                            var payload = MessagePackSerializer.Serialize(playerList);

                            _ = socket.EmitAsync("player-positions", "proximity-chat", payload);
                            int delay = playerList.Count > 1 ? 100 : 1000;
                            await Task.Delay(delay, token);
                        }
                    },
                    token
                );

                Server.NextFrame(() =>
                {
                    AddTimer(
                        3,
                        () =>
                        {
                            NotifyMapChange();
                            NotifyServerConfig();
                        }
                    );
                });
            };

            socket.On(
                "server-config",
                (data) =>
                {
                    try
                    {
                        var bytes = data.GetValue<byte[]>();
                        if (bytes != null)
                        {
                            var updatedConfig = MessagePackSerializer.Deserialize<Config>(bytes);
                            Config.DeadPlayerMuteDelay = updatedConfig.DeadPlayerMuteDelay;
                            Config.AllowDeadTeamVoice = updatedConfig.AllowDeadTeamVoice;
                            Config.AllowSpectatorC4Voice = updatedConfig.AllowSpectatorC4Voice;
                            Config.VolumeFalloffFactor = updatedConfig.VolumeFalloffFactor;
                            Config.VolumeMaxDistance = updatedConfig.VolumeMaxDistance;
                            Config.OcclusionNear = updatedConfig.OcclusionNear;
                            Config.OcclusionFar = updatedConfig.OcclusionFar;
                            Config.OcclusionEndDist = updatedConfig.OcclusionEndDist;
                            Config.OcclusionFalloffFactor = updatedConfig.OcclusionFalloffFactor;
                            Config.AlwaysHearVisiblePlayers = updatedConfig.AlwaysHearVisiblePlayers;
                            Config.DeadVoiceFilterFrequency = updatedConfig.DeadVoiceFilterFrequency;
                            Config.SpectatorsCanTalk = updatedConfig.SpectatorsCanTalk;

                            Server.NextFrame(() =>
                            {
                                Config.Update();
                            });
                            Console.WriteLine($"Config has been updated by client");
                        }
                    }
                    catch (Exception ex)
                    {
                        Logger.LogError(ex.Message);
                    }
                }
            );

            socket.On(
                "server-restart-warning",
                (SocketIOResponse data) =>
                {
                    var payload = data.GetValue<ServerRestartWarning>(0);
                    Server.NextFrame(() =>
                    {
                        Logger.LogWarning($"Socket server will restart in {payload.minutes * 60} seconds. Users will reconnect automatically.");
                        Server.PrintToChatAll(
                            $" {ChatColors.Green}Proximity Chat {ChatColors.Default}| {ChatColors.Red}API server will restart in {ChatColors.Default}{payload.minutes * 60} {ChatColors.Red}seconds. Users will reconnect automatically."
                        );
                    });
                }
            );

            socket.OnDisconnected += (sender, e) =>
            {
                Logger.LogError("Socket disconnected. Please restart the server if it does not reconnect automatically.");
                if (_cts != null)
                {
                    _cts.Cancel();
                    _socketTask?.Wait();
                    _cts.Dispose();
                    _cts = null;
                    _socketTask = null;
                }
                Server.NextFrame(() =>
                {
                    if (tryReconnectSocket)
                    {
                        AddTimer(
                            1,
                            () =>
                            {
                                InitServer();
                            }
                        );
                    }
                });
            };

            // Custom errors from the API
            socket.On(
                "exception",
                (SocketIOResponse error) =>
                {
                    var payload = error.GetValue<ExceptionPayload>(0);
                    Log(payload?.message ?? "Unknown socket exception occurred.", ConsoleColor.Red);
                    tryReconnectSocket = false;
                }
            );

            await socket.ConnectAsync();
        }
    }

    private void NotifyMapChange()
    {
        if (socket == null || !socket.Connected)
        {
            return;
        }
        Console.WriteLine($"Notifying server of current map: {CurrentMap}");
        Task.Run(async () =>
        {
            await socket.EmitAsync("current-map", "proximity-chat", CurrentMap);
        });
    }

    private void NotifyServerConfig()
    {
        if (socket == null || !socket.Connected)
        {
            return;
        }
        Server.NextFrame(() =>
        {
            Config.Reload();
            var payload = MessagePackSerializer.Serialize(Config);
            Task.Run(async () =>
            {
                await socket.EmitAsync("server-config", "proximity-chat", payload);
            });
        });
    }

    public TraceOptions traceOptions = new TraceOptions(
        interactsAs: 0,
        interactsWith: InteractionLayers.WorldGeometry | InteractionLayers.Solid | InteractionLayers.Window,
        interactsExclude: InteractionLayers.Player | InteractionLayers.NPC,
        drawBeam: false
    );

    public void SaveAllPlayersPositions()
    {
        var players = Utilities.GetPlayers().Where(IsValid);
        foreach (var player in players)
        {
            //if (player.IsBot) continue; // ignore bots

            bool useObserverPawn = false;
            if (player.PlayerPawn.Value!.LifeState != (byte)LifeState_t.LIFE_ALIVE)
            {
                // Keep the camera position in the same spot for a few seconds before teleporting to the player they're spectating
                float timeSinceDeath = Server.CurrentTime - player.PlayerPawn.Value.DeathTime;
                if (timeSinceDeath >= 3)
                {
                    useObserverPawn = true;
                }
            }
            SavePlayerData(player, useObserverPawn);
            foreach (var target in players)
            {
                if (target.Index == player.Index)
                {
                    // Ignore self
                    continue;
                }
                var playerOrigin = GetEyePosition(player.PlayerPawn.Value);
                var targetOrigin = GetEyePosition(target.PlayerPawn.Value);

                if (playerOrigin == null || targetOrigin == null)
                {
                    continue;
                }

                var SoundLeft = CalculatePoint(playerOrigin, targetOrigin, 31, true);
                var SoundRight = CalculatePoint(playerOrigin, targetOrigin, 31, false);
                var ListenerLeft = CalculatePoint(targetOrigin, playerOrigin, 31, true);
                var ListenerRight = CalculatePoint(targetOrigin, playerOrigin, 31, false);

                var success = CRayTrace.TraceEndShape(playerOrigin, targetOrigin, player.PlayerPawn.Value, traceOptions, out TraceResult result);
                List<bool> lines = new();

                lines.Add(Trace(playerOrigin, targetOrigin, player));

                // MEDIUM
                lines.Add(Trace(SoundLeft, ListenerLeft, player));
                lines.Add(Trace(SoundRight, ListenerRight, player));

                // HIGH
                lines.Add(Trace(SoundLeft, targetOrigin, player));
                lines.Add(Trace(SoundRight, targetOrigin, player));

                // VERYHIGH
                lines.Add(Trace(playerOrigin, ListenerLeft, player));
                lines.Add(Trace(playerOrigin, ListenerRight, player));

                // ULTRA
                lines.Add(Trace(SoundLeft, ListenerRight, player));
                lines.Add(Trace(SoundRight, ListenerLeft, player));
            }
        }
    }

    public bool Trace(Vector from, Vector to, CBaseEntity ignore)
    {
        var success = CRayTrace.TraceEndShape(
            from,
            to,
            ignore, // ignore speaker
            traceOptions,
            out TraceResult result
        );

        if (!success)
            return false;

        return result.DidHit; // or refine if needed
    }

    public Vector CalculatePoint(Vector a, Vector b, float m, bool positive)
    {
        // Distance in XZ plane
        float dx = a.X - b.X;
        float dz = a.Z - b.Z;

        float n = MathF.Sqrt(dx * dx + dz * dz);
        if (n == 0f)
            return new Vector(a.X, a.Y, a.Z); // avoid divide-by-zero

        float mn = m / n;

        float x,
            z;

        if (positive)
        {
            x = a.X + mn * (a.Z - b.Z);
            z = a.Z - mn * (a.X - b.X);
        }
        else
        {
            x = a.X - mn * (a.Z - b.Z);
            z = a.Z + mn * (a.X - b.X);
        }

        return new Vector(x, a.Y, z);
    }

    public void SavePlayerData(CCSPlayerController? player, bool useObserverPawn)
    {
        if (player == null || !IsValid(player))
        {
            return;
        }

        if (player.AuthorizedSteamID?.SteamId64 == null && !DEBUG_FAKE_PLAYERS)
        {
            return;
        }

        var playerSteamId = (ulong)(player.AuthorizedSteamID?.SteamId64 ?? 0);

        float OriginX = 0f;
        float OriginY = 0f;
        float OriginZ = 0f;
        float LookAtX = 0f;
        float LookAtY = 0f;
        float LookAtZ = 0f;

        bool spectatingC4 = false;

        CBasePlayerPawn? pawn = player!.PlayerPawn.Value;
        bool gotOriginAndAngles = false;
        if (useObserverPawn)
        {
            var observerMode = GetObserverMode(player);
            var observingTarget = GetObserverTarget(player);
            if (observingTarget != null && IsValid(observingTarget))
            {
                pawn = observingTarget.Pawn.Value;
            }

            if (GetObserverEntity(player)?.DesignerName == "planted_c4")
            {
                var vAngle = player.Pawn.Value!.V_angle.Clone();
                var c4Position = player.Pawn.Value!.AbsOrigin?.Clone();

                if (c4Position == null)
                {
                    return;
                }

                // Calculate third person position when spectating planted c4
                Vector forward = new();
                NativeAPI.AngleVectors(vAngle.Handle, forward.Handle, 0, 0);

                // TODO: Could we TraceRay to the c4 to detect if cameraPos is inside an object, and reduce camDistance until the trace is clear?
                const float camDistance = 100;
                float lowestZ = c4Position.Z + 25;

                Vector offset = new(-forward.X * camDistance, -forward.Y * camDistance, -forward.Z * camDistance);
                Vector cameraPos = new(c4Position.X + offset.X, c4Position.Y + offset.Y, c4Position.Z + offset.Z);

                OriginX = cameraPos.X;
                OriginY = cameraPos.Y;
                // Prevent the camera from going under the floor
                OriginZ = cameraPos.Z < lowestZ ? lowestZ : cameraPos.Z;

                LookAtX = c4Position.X;
                LookAtY = c4Position.Y;
                LookAtZ = c4Position.Z;

                gotOriginAndAngles = true;
                spectatingC4 = true;
            }
            else if (observerMode == ObserverMode_t.OBS_MODE_ROAMING)
            {
                QAngle angles = player.Pawn.Value!.V_angle.Clone();
                Vector? origin = GetFreecamPlayerPosition(player);
                if (origin != null)
                {
                    OriginX = origin.X;
                    OriginY = origin.Y;
                    OriginZ = origin.Z;

                    var LookAt = CalculateForward(origin, angles)!;

                    LookAtX = LookAt.X;
                    LookAtY = LookAt.Y;
                    LookAtZ = LookAt.Z;
                    gotOriginAndAngles = true;
                }
            }
            else if (observerMode == ObserverMode_t.OBS_MODE_CHASE && pawn!.DesignerName == "player")
            {
                var vAngle = player.Pawn.Value!.V_angle.Clone();
                var position = GetEyePosition(pawn);
                if (position == null)
                {
                    return;
                }

                // Calculate third person position when spectating a player in thirdperson view (Chase cam)
                Vector forward = new();
                NativeAPI.AngleVectors(vAngle.Handle, forward.Handle, 0, 0);

                const float camDistance = 150; // matches cam_idealdist cvar

                Vector offset = new(-forward.X * camDistance, -forward.Y * camDistance, -forward.Z * camDistance);
                Vector cameraPos = new(position.X + offset.X, position.Y + offset.Y, position.Z + offset.Z);

                OriginX = cameraPos.X;
                OriginY = cameraPos.Y;
                OriginZ = cameraPos.Z;
                LookAtX = position.X;
                LookAtY = position.Y;
                LookAtZ = position.Z;
                gotOriginAndAngles = true;
            }
        }

        if (pawn == null || !pawn.IsValid)
        {
            return;
        }

        if (!gotOriginAndAngles)
        {
            var origin = GetEyePosition(pawn)?.Clone();
            var angles = pawn.V_angle.Clone();
            if (origin == null || angles == null)
            {
                return;
            }

            OriginX = origin.X;
            OriginY = origin.Y;
            OriginZ = origin.Z;

            var LookAt = CalculateForward(origin, angles)!;
            LookAtX = LookAt.X;
            LookAtY = LookAt.Y;
            LookAtZ = LookAt.Z;
        }

        var playerIsAlive = IsAlive(player) ? 1 : 0;
        var Team = player.TeamNum;

        if (!PlayerData.ContainsKey(playerSteamId))
        {
            PlayerData[playerSteamId] = new PlayerData(playerSteamId.ToString(), player.PlayerName);
        }
        if (PlayerData[playerSteamId].IsAdmin == null)
        {
            CheckAdmin(player.AuthorizedSteamID);
        }
        if (playerSteamId != 0)
        {
            SaveData(playerSteamId, player.PlayerName, OriginX, OriginY, OriginZ, LookAtX, LookAtY, LookAtZ, Team, playerIsAlive, spectatingC4);
        }
        else
        {
            if (DEBUG_FAKE_PLAYERS)
            {
                int index = fakeBots.IndexOf(player.PlayerName);
                ulong steamid;
                if (index + 1 < 10)
                {
                    ulong.TryParse($"1000000000000000{index + 1}", out steamid);
                }
                else
                {
                    ulong.TryParse($"100000000000000{index + 1}", out steamid);
                }
                SaveData(steamid, player.PlayerName, OriginX, OriginY, OriginZ, LookAtX, LookAtY, LookAtZ, Team, playerIsAlive, spectatingC4);
            }
        }
    }

    public void SaveData(ulong playerSteamId, string playerName, float OriginX, float OriginY, float OriginZ, float LookAtX, float LookAtY, float LookAtZ, byte Team, int playerIsAlive, bool spectatingC4)
    {
        if (!PlayerData.ContainsKey(playerSteamId))
        {
            PlayerData[playerSteamId] = new PlayerData(playerSteamId.ToString(), playerName);
        }

        PlayerData[playerSteamId].Name = playerName;
        PlayerData[playerSteamId].SteamId = playerSteamId.ToString();

        // Scale up the floats and store them as integers
        PlayerData[playerSteamId].OriginX = (int)(OriginX * 10000);
        PlayerData[playerSteamId].OriginY = (int)(OriginY * 10000);
        PlayerData[playerSteamId].OriginZ = (int)(OriginZ * 10000);
        PlayerData[playerSteamId].LookAtX = (int)(LookAtX * 10000);
        PlayerData[playerSteamId].LookAtY = (int)(LookAtY * 10000);
        PlayerData[playerSteamId].LookAtZ = (int)(LookAtZ * 10000);

        PlayerData[playerSteamId].Team = Team;
        PlayerData[playerSteamId].IsAlive = playerIsAlive == 1 ? true : false;
        PlayerData[playerSteamId].SpectatingC4 = spectatingC4;
    }

    [EntityOutputHook("prop_door_rotating", "OnBreak")]
    public HookResult OnBreak(CEntityIOOutput output, string name, CEntityInstance activator, CEntityInstance caller, CVariant value, float delay)
    {
        // This hook will run twice when a door is blown up by a nade
        // In one hook the activator will be the nade, second hook the door will also be the activator

        if (caller.IsValid && caller.DesignerName == "prop_door_rotating" && activator.IsValid && activator.DesignerName == "prop_door_rotating")
        {
            var door = caller.As<CPropDoorRotating>();
            if (door != null && door.IsValid && door.AbsOrigin != null)
            {
                var origin = door.AbsOrigin.Clone();
                var doorKey = GetDoorKey(origin);

                AddTimer(
                    0.1f,
                    () =>
                    {
                        DoorRotations[doorKey] = 999;
                        Task.Run(() =>
                        {
                            socket?.EmitAsync("door-rotation", "proximity-chat", $"{origin.X} {origin.Y} {origin.Z}", 999);
                        });
                    }
                );
            }
        }
        return HookResult.Continue;
    }

    [GameEventHandler]
    public HookResult Event_RoundStart(EventRoundStart @event, GameEventInfo info)
    {
        DoorRotations.Clear();

        var doors = new List<CPropDoorRotating>();
        doors.AddRange(Utilities.FindAllEntitiesByDesignerName<CPropDoorRotating>("prop_door_rotating"));
        doors.AddRange(Utilities.FindAllEntitiesByDesignerName<CPropDoorRotating>("func_door_rotating"));

        foreach (var door in doors)
        {
            if (door.AbsOrigin == null || door.AbsRotation == null)
            {
                continue;
            }
            DoorEntities.Add(door);
            var key = GetDoorKey(door.AbsOrigin);
            DoorRotations[key] = 999;
        }

        fakeBots.Clear();
        foreach (var bot in Utilities.GetPlayers().Where(IsValid).Where(p => p.IsBot))
        {
            fakeBots.Add(bot.PlayerName);
        }

        return HookResult.Continue;
    }

    public override void Unload(bool hotReload)
    {
        if (_cts != null)
        {
            _cts.Cancel();
            _socketTask?.Wait();
            _cts.Dispose();
            _cts = null;
            _socketTask = null;
        }

        if (socket != null && socket.Connected)
        {
            socket.Dispose();
            socket = null;
        }
    }

    public void OnConfigParsed(Config config)
    {
        this.Config = config;
    }

    private void Log(string message, ConsoleColor color = ConsoleColor.White)
    {
        Console.ForegroundColor = ConsoleColor.Red;
        Console.WriteLine($"[{this.ModuleName}] {message}");
        Console.ResetColor();
    }

    public AssemblyName assemblyName = typeof(ProximityChat).Assembly.GetName();
    public string? AssemblyVersion => assemblyName.Version?.ToString(); // Version: x.x.x.x
    public string? PluginVersion => AssemblyVersion?.Remove(AssemblyVersion.Length - 2); // truncate to x.x.x
}
