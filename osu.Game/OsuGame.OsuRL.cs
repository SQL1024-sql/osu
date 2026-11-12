// osu_rl (local research fork, not for upstream): commands from the osu_rl bridge (see OsuRL/OsuRLBridge.cs).

using System;
using System.Linq;
using System.Threading;
using Newtonsoft.Json.Linq;
using osu.Framework.Configuration;
using osu.Framework.Screens;
using osu.Game.Configuration;
using osu.Game.OsuRL;
using osu.Game.Screens.Play;
using osu.Game.Screens.Select;

namespace osu.Game
{
    public partial class OsuGame
    {
        private OsuRLBridge? osuRLBridge;

        // bridge setup "cursor": false (background plays): nobody is at the mouse, so outside gameplay no menu cursor
        // follows it over the stream; in gameplay the play's own cursor (the student's) stays
        private bool osuRLHideMenuCursor;

        private bool osuRLHidesMenuCursor => osuRLHideMenuCursor && ScreenStack.CurrentScreen is not Player;

        private void startOsuRLBridge()
        {
            string? port = Environment.GetEnvironmentVariable("OSU_RL_BRIDGE");
            if (string.IsNullOrEmpty(port))
                return;

            osuRLBridge = new OsuRLBridge(int.Parse(port), osuRLCommand);

            // plays driven by the bridge go on in the background: no frame rate drop, no fading the music
            Host.MaximumInactiveHz = 1000;
            LocalConfig.SetValue(OsuSetting.VolumeInactive, 1.0);
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
                        ["active"] = Host.IsActive.Value,
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

                    Schedule(() => PerformFromScreen(_ =>
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
                    }, new[] { typeof(SongSelect) }));

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
                    Schedule(() => PerformFromScreen(_ => { }, new[] { typeof(SongSelect) }));
                    return new JObject { ["ok"] = true };

                default:
                    return new JObject { ["ok"] = false, ["error"] = $"unknown command {name}" };
            }
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
