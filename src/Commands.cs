using System.Reflection;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Core.Attributes.Registration;
using CounterStrikeSharp.API.Modules.Admin;
using CounterStrikeSharp.API.Modules.Commands;
using CounterStrikeSharp.API.Modules.Events;
using CounterStrikeSharp.API.Modules.Extensions;
using CounterStrikeSharp.API.Modules.Menu;
using CounterStrikeSharp.API.Modules.Utils;
using MessagePack;

namespace ProximityChat;

public partial class ProximityChat : BasePlugin, IPluginConfig<Config>
{
    public PropertyInfo? configPropertyEditing = null;
    public ulong configEditedBySteamId = 0;

    [ConsoleCommand("css_proximity", "Opens a menu to update the Proximity Chat config")]
    [RequiresPermissions("#css/admin")]
    public void Command_ProximityChatMenu(CCSPlayerController? caller, CommandInfo info)
    {
        if (caller == null || !IsValid(caller))
        {
            return;
        }

        var menu = new ChatMenu("Proximity Chat Config");
        foreach (var prop in typeof(Config).GetProperties())
        {
            var name = prop.Name;
            var propType = prop.PropertyType;

            if (prop.GetCustomAttributes(typeof(IgnoreMemberAttribute), true).Length > 0 || name == "Version" || name == "ConfigVersion")
                continue;

            string displayOption = "";
            if (prop.PropertyType == typeof(bool))
            {
                var value = (bool)(prop.GetValue(Config) ?? false);
                displayOption = $"{name}: {(value ? ChatColors.Green : ChatColors.Red)}{value}";
            }
            else
            {
                displayOption = $"{name}: {ChatColors.Magenta}{prop.GetValue(Config)}";
            }

            menu.AddMenuOption(
                displayOption,
                (player, selection) =>
                {
                    var subMenu = new ChatMenu($"Change {name}");

                    if (prop.PropertyType == typeof(bool))
                    {
                        subMenu.AddMenuOption(
                            "True",
                            (player, selection) =>
                            {
                                prop.SetValue(Config, true);
                                Config.Update();
                                NotifyServerConfig();
                                caller.PrintToChat($"Set {name} to {ChatColors.Green}true");
                            }
                        );
                        subMenu.AddMenuOption(
                            "false",
                            (player, selection) =>
                            {
                                prop.SetValue(Config, false);
                                Config.Update();
                                NotifyServerConfig();
                                caller.PrintToChat($"Set {name} to {ChatColors.Red}false");
                            }
                        );
                        MenuManager.OpenChatMenu(caller, subMenu);
                    }
                    else if (prop.PropertyType == typeof(float))
                    {
                        var value = prop.GetValue(Config);
                        configPropertyEditing = prop;
                        configEditedBySteamId = caller.AuthorizedSteamID?.SteamId64 ?? 0;
                        caller.PrintToChat($"Type in chat the new value of {ChatColors.Green}{name}");
                    }
                }
            );
        }
        MenuManager.OpenChatMenu(caller, menu);
    }

    [GameEventHandler]
    public HookResult Event_PlayerChat(EventPlayerChat @event, GameEventInfo info)
    {
        var player = Utilities.GetPlayerFromUserid(@event.Userid);
        if (player == null || !IsValid(player))
        {
            return HookResult.Continue;
        }

        if (configPropertyEditing == null)
        {
            return HookResult.Continue;
        }

        var steamId = player.AuthorizedSteamID?.SteamId64 ?? 0;
        if (configEditedBySteamId != steamId || steamId == 0)
        {
            return HookResult.Continue;
        }

        float newValue;
        if (!float.TryParse(@event.Text, out newValue))
        {
            player.PrintToChat($"Invalid value: {ChatColors.Red}{@event.Text}{ChatColors.Default}");
            configPropertyEditing = null;
            configEditedBySteamId = 0;
            return HookResult.Continue;
        }

        var name = configPropertyEditing.Name;
        configPropertyEditing.SetValue(Config, newValue);

        Config.Update();
        NotifyServerConfig();

        player.PrintToChat($"Updated {ChatColors.Green}{name} {ChatColors.Default}to {ChatColors.Magenta}{newValue}");

        configPropertyEditing = null;
        configEditedBySteamId = 0;

        return HookResult.Continue;
    }

    [ConsoleCommand("css_setdebugpos")]
    [RequiresPermissions("#css/admin")]
    public void Command_setdebugpos(CCSPlayerController? caller, CommandInfo info)
    {
        var origin = GetEyePosition(caller?.Pawn.Value);
        if (origin != null)
        {
            debugPlayerPosition.X = origin.X;
            debugPlayerPosition.Y = origin.Y;
            debugPlayerPosition.Z = origin.Z;
        }
    }

    [ConsoleCommand("css_fakeplayers")]
    [RequiresPermissions("#css/admin")]
    public void Command_fakeplayers(CCSPlayerController? caller, CommandInfo info)
    {
        DEBUG_FAKE_PLAYERS = !DEBUG_FAKE_PLAYERS;
        info.ReplyToCommand($"fake players?: {DEBUG_FAKE_PLAYERS}");
    }

    [ConsoleCommand("css_updatemap")]
    [RequiresPermissions("#css/admin")]
    public void Command_UpdateMap(CCSPlayerController? caller, CommandInfo info)
    {
        CurrentMap = Server.MapName;
        NotifyMapChange();
        info.ReplyToCommand($"Attempting to notify server of current map: {Server.MapName}");
    }

    [ConsoleCommand("css_updateconfig")]
    [RequiresPermissions("#css/admin")]
    public void Command_UpdateConfig(CCSPlayerController? caller, CommandInfo info)
    {
        NotifyServerConfig();
    }

    [ConsoleCommand("css_savepositions")]
    [RequiresPermissions("#css/admin")]
    public void Command_SavePositions(CCSPlayerController? caller, CommandInfo info)
    {
        SaveAllPlayersPositions();
    }
}
