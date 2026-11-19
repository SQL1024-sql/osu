// osu_rl (local research fork, not for upstream): commands from the osu_rl bridge (see OsuRL/OsuRLBridge.cs).

using System;
using System.Linq;
using System.Threading;
using Newtonsoft.Json.Linq;
using osu.Framework.Configuration;
using osu.Framework.Screens;
using osu.Game.Configuration;
using osu.Game.Online;
using osu.Game.Online.Multiplayer;
using osu.Game.Online.Rooms;
using osu.Game.OsuRL;
using osu.Game.Screens.Menu;
using osu.Game.Screens.OnlinePlay.Lounge.Components;
using osu.Game.Screens.Play;
using osu.Game.Screens.Select;

namespace osu.Game
{
    public partial class OsuGame
    {
        private OsuRLBridge? osuRLBridge;
        private OsuRLWatcher? osuRLWatcher;

        // bridge setup "cursor": false (background plays): nobody is at the mouse, so outside gameplay no menu cursor
        // follows it over the stream; in gameplay the play's own cursor (the student's) stays
        private bool osuRLHideMenuCursor;

        private bool osuRLHidesMenuCursor => osuRLHideMenuCursor && ScreenStack.CurrentScreen is not Player && !osuRLInOnlinePlay;

        // lazer's multiplayer and playlists: the lounge, a room and its song select, its player and results, spectating a
        // room. The same copy sits in rooms (live.room_play) and plays maps of its own between them (live.stream): a
        // person runs the room (cursor shown), and "play" / "menu" would take the game out of it, so they refuse
        private bool osuRLInOnlinePlay =>
            ScreenStack.CurrentScreen?.GetType().Namespace?.StartsWith("osu.Game.Screens.OnlinePlay", StringComparison.Ordinal) == true;

        // OSU_RL_LAZER_SERVER (a private server, see DevelopmentEndpointConfiguration) is used by a release build too:
        // without this a release build always talks to the official servers, where the AI must never play
        public override bool UseDevelopmentServer =>
            base.UseDevelopmentServer || !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("OSU_RL_LAZER_SERVER"));

        private void startOsuRLBridge()
        {
            string? port = Environment.GetEnvironmentVariable("OSU_RL_BRIDGE");
            if (string.IsNullOrEmpty(port))
                return;

            osuRLBridge = new OsuRLBridge(int.Parse(port), osuRLCommand);
            Add(osuRLWatcher = new OsuRLWatcher(osuRLBridge, () => ScreenStack)); // any play, multiplayer too (live.room_play)

            // plays driven by the bridge go on in the background: no frame rate drop, no fading the music
            Host.MaximumInactiveHz = 1000;
            LocalConfig.SetValue(OsuSetting.VolumeInactive, 1.0);
        }

        private string? osuRLBeatmapFile()
        {
            string hash = Beatmap.Value.BeatmapInfo.Hash;
            if (string.IsNullOrEmpty(hash) || hash.Length < 3)
                return null;

            string path = Storage.GetFullPath(System.IO.Path.Combine("files", hash.Remove(1), hash.Remove(2), hash));
            return System.IO.File.Exists(path) ? path : null;
        }

        /// <summary>Runs on the bridge thread: game work is scheduled on the update thread and waited for.</summary>
        private JObject osuRLCommand(JObject cmd)
        {
            string name = (string?)cmd["cmd"] ?? "";

            switch (name)
            {
                case "status":
                    return onGame(() => new JObject
                    {
                        ["ok"] = true,
                        ["screen"] = ScreenStack.CurrentScreen?.GetType().Name,
                        ["map"] = Beatmap.Value.BeatmapInfo.ToString(),
                        ["md5"] = Beatmap.Value.BeatmapInfo.MD5Hash,
                        ["beatmap_id"] = Beatmap.Value.BeatmapInfo.OnlineID,
                        // the selected map's .osu in the game's file store (named by its SHA-256, which is BeatmapInfo.Hash)
                        ["osu"] = osuRLBeatmapFile(),
                        ["active"] = Host.IsActive.Value,
                        ["pid"] = Environment.ProcessId, // which window is this copy's when several run
                        // which server and account this copy plays on (live.room_play refuses any but the AI's own)
                        ["server"] = API.Endpoints.APIUrl,
                        ["user"] = API.LocalUser.Value?.Username,
                        ["online"] = API.State.Value.ToString(),
                        // the multiplayer room this copy is in (live.room_play readies up when the map is there)
                        ["multi"] = osuRLMultiplayerStatus(),
                    });

                case "rooms":
                    // the open multiplayer rooms on the server (what the lounge lists), for picking one to join
                    var roomsRequest = new GetRoomsRequest(new LoungeFilterCriteria());
                    API.Perform(roomsRequest);
                    var rooms = new JArray();
                    foreach (var r in roomsRequest.Response ?? new System.Collections.Generic.List<Room>())
                    {
                        if (r.Type == MatchType.Playlists)
                            continue;

                        rooms.Add(new JObject
                        {
                            ["id"] = r.RoomID,
                            ["name"] = r.Name,
                            ["host"] = r.Host?.Username,
                            ["has_password"] = r.HasPassword,
                            ["participants"] = r.ParticipantCount,
                            ["status"] = r.Status.GetType().Name,
                        });
                    }

                    return new JObject { ["ok"] = true, ["rooms"] = rooms };

                case "join":
                    // into a multiplayer room the way a link to it does (the lounge, then the room's screen); the join
                    // itself goes on in the game: status "multi" says when it is in
                    long joinId = (long)cmd["room_id"]!;
                    string joinPassword = (string?)cmd["password"] ?? string.Empty;
                    var roomRequest = new GetRoomRequest(joinId);
                    API.Perform(roomRequest);
                    var joinRoom = roomRequest.Response;
                    if (joinRoom == null)
                        return new JObject { ["ok"] = false, ["error"] = $"no room {joinId}" };
                    if (joinRoom.Type == MatchType.Playlists)
                        return new JObject { ["ok"] = false, ["error"] = $"room {joinId} is a playlist, not a multiplayer room" };
                    if (joinRoom.HasEnded)
                        return new JObject { ["ok"] = false, ["error"] = $"room {joinId} has ended" };

                    Schedule(() => PresentMultiplayerMatch(joinRoom, joinPassword));
                    return new JObject { ["ok"] = true, ["room"] = joinRoom.Name };

                case "ready":
                    // ready up in the room (only once the map is downloaded; lazer fetches a missing one itself)
                    return onGame(() =>
                    {
                        var me = MultiplayerClient.LocalUser;
                        if (MultiplayerClient.Room == null || me == null)
                            return new JObject { ["ok"] = false, ["error"] = "not in a multiplayer room" };
                        if (me.State != MultiplayerUserState.Idle)
                            return new JObject { ["ok"] = true, ["state"] = me.State.ToString() };
                        if (me.BeatmapAvailability.State != DownloadState.LocallyAvailable)
                            return new JObject { ["ok"] = false, ["error"] = $"the map is not here yet ({me.BeatmapAvailability.State})" };

                        MultiplayerClient.ToggleReady().FireAndForget();
                        return new JObject { ["ok"] = true, ["state"] = "Ready" };
                    });

                case "leave":
                    // out of the room and lazer's multiplayer screens, back to the main menu ("menu" refuses there)
                    return onGame(() =>
                    {
                        if (!osuRLInOnlinePlay)
                            return new JObject { ["ok"] = true, ["left"] = false };

                        PerformFromScreen(_ => { }, new[] { typeof(MainMenu) });
                        return new JObject { ["ok"] = true, ["left"] = true };
                    });

                case "import":
                    string[] paths = cmd["paths"]!.Select(p => (string)p!).ToArray();
                    Import(paths).GetAwaiter().GetResult();
                    return new JObject { ["ok"] = true };

                case "setup":
                    return onGame(() =>
                    {
                        var reply = new JObject { ["ok"] = true };

                        if (cmd["skin"] != null)
                        {
                            string skinName = (string)cmd["skin"]!;
                            var skin = SkinManager.Query(s => s.Name == skinName && !s.DeletePending);
                            if (skin == null)
                                return new JObject { ["ok"] = false, ["error"] = $"no skin named {skinName}" };

                            SkinManager.CurrentSkinInfo.Value = skin;
                        }

                        if ((string?)cmd["window"] == "borderless" && Host.Window != null)
                        {
                            Host.Window.WindowMode.Value = WindowMode.Borderless; // stays up (fullscreen minimizes) when another window is in front
                            // borderless covering the screen is fullscreen to SDL: without this it minimizes itself as soon as
                            // another window (OBS, a browser) is clicked, and the capture gets no frames
                            frameworkConfig.SetValue(FrameworkSetting.MinimiseOnFocusLossInFullscreen, false);
                        }

                        if (cmd["cursor"] != null)
                            osuRLHideMenuCursor = !(bool)cmd["cursor"]!;

                        if (cmd["background"] != null && osuRLWatcher != null)
                            osuRLWatcher.HideBackground = !(bool)cmd["background"]!;

                        if (cmd["hud"] != null)
                            LocalConfig.SetValue(OsuSetting.HUDVisibilityMode, (bool)cmd["hud"]! ? HUDVisibilityMode.Always : HUDVisibilityMode.Never);

                        return reply;
                    });

                case "play":
                    string md5 = (string)cmd["md5"]!;
                    string[] acronyms = cmd["mods"]?.Select(m => (string)m!).ToArray() ?? Array.Empty<string>();
                    string? outPath = (string?)cmd["out"];
                    bool bridgeInput = (string?)cmd["input"] == "bridge";
                    var beatmap = BeatmapManager.QueryBeatmap(b => b.MD5Hash == md5);
                    if (beatmap == null)
                        return new JObject { ["ok"] = false, ["error"] = $"no beatmap with MD5 {md5} (import it first)" };

                    var started = new ManualResetEventSlim();
                    string? error = null;

                    Schedule(() =>
                    {
                        if (osuRLInOnlinePlay)
                        {
                            error = "the game is in multiplayer: a map of its own would take it out of the room";
                            started.Set();
                            return;
                        }

                        PerformFromScreen(_ =>
                        {
                            try
                            {
                                Ruleset.Value = beatmap.Ruleset;
                                Beatmap.Value = BeatmapManager.GetWorkingBeatmap(beatmap);
                                var ruleset = beatmap.Ruleset.CreateInstance();
                                var mods = acronyms.Select(a => ruleset.CreateModFromAcronym(a) ?? throw new ArgumentException($"no mod {a}")).ToArray();
                                SelectedMods.Value = mods;
                                Func<Player> create = () => new OsuRLPlayer(osuRLBridge!, outPath, bridgeInput);
                                ScreenStack.CurrentScreen.Push(bridgeInput ? new OsuRLPlayerLoader(create) : new PlayerLoader(create));
                            }
                            catch (Exception e)
                            {
                                error = e.Message;
                            }
                            finally
                            {
                                started.Set();
                            }
                        }, new[] { typeof(SongSelect) });
                    });

                    if (!started.Wait(TimeSpan.FromSeconds(20)))
                        return new JObject { ["ok"] = false, ["error"] = "the game did not get to a screen to start from in 20 s" };

                    return error == null ? new JObject { ["ok"] = true, ["map"] = beatmap.ToString() } : new JObject { ["ok"] = false, ["error"] = error };

                case "focus":
                    // bring the game back in front (a notification or another program took the focus). Windows only
                    // lets the foreground thread hand the focus over, so join its input queue for the moment
                    bool front = OsuRLForeground.Bring();
                    Thread.Sleep(200);
                    return onGame(() => new JObject { ["ok"] = true, ["front"] = front, ["active"] = Host.IsActive.Value });

                case "menu":
                    return onGame(() =>
                    {
                        if (osuRLInOnlinePlay)
                            return new JObject { ["ok"] = false, ["error"] = "the game is in multiplayer: left where it is" };

                        PerformFromScreen(_ => { }, new[] { typeof(SongSelect) });
                        return new JObject { ["ok"] = true };
                    });

                default:
                    return new JObject { ["ok"] = false, ["error"] = $"unknown command {name}" };
            }
        }

        /// <summary>The multiplayer room this copy is in (update thread), or null.</summary>
        private JObject? osuRLMultiplayerStatus()
        {
            var room = MultiplayerClient.Room;
            if (room == null)
                return null;

            var me = MultiplayerClient.LocalUser;
            return new JObject
            {
                ["room_id"] = room.RoomID,
                ["state"] = me?.State.ToString(),
                ["availability"] = me?.BeatmapAvailability.State.ToString(),
                ["host"] = room.Host?.User?.Username,
                ["users"] = room.Users.Count,
                ["room_state"] = room.State.ToString(),
            };
        }

        private JObject onGame(Func<JObject> work)
        {
            JObject? result = null;
            Exception? error = null;
            var done = new ManualResetEventSlim();

            Schedule(() =>
            {
                try
                {
                    result = work();
                }
                catch (Exception e)
                {
                    error = e;
                }
                finally
                {
                    done.Set();
                }
            });

            if (!done.Wait(TimeSpan.FromSeconds(10)))
                return new JObject { ["ok"] = false, ["error"] = "the game did not answer in 10 s" };

            return error == null ? result! : new JObject { ["ok"] = false, ["error"] = error.Message };
        }
    }
}
