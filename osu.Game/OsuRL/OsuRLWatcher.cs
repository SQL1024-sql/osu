// osu_rl (local research fork, not for upstream): reports any gameplay to the osu_rl bridge, the way OsuRLPlayer reports
// its own plays: a multiplayer round (MultiplayerPlayer), a solo play started from song select, ... So live.room_play
// can play a real room through the virtual tablet without tosu (which has no offsets for a local build).
//
// Events (while any player is on screen, at any depth of sub-screen stacks; an OsuRLPlayer sends its own clock):
//   {"ev":"seat","state":"playing"|"paused","md5":..,"mods":["HD","DT"],"rate":1.5}   on start and on every change
//   {"ev":"clock","qpc":<s>,"t":<gameplay ms>}                                          every 2 ms of play
//   {"ev":"live","accuracy":0.98,"combo":120,"misses":1}                               every 0.5 s of play
//   {"ev":"played","md5":..,"accuracy":..,"max_combo":..,"misses":..,"statistics":{..}} when the player leaves

using System;
using System.Linq;
using System.Reflection;
using osu.Framework.Graphics;
using osu.Framework.Screens;
using osuTK.Graphics;
using osu.Game.Rulesets.Scoring;
using osu.Game.Screens;
using osu.Game.Screens.Play;

namespace osu.Game.OsuRL
{
    public partial class OsuRLWatcher : Component
    {
        private static readonly PropertyInfo clock_property =
            typeof(Player).GetProperty("GameplayClockContainer", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)!;

        private static readonly PropertyInfo score_processor_property =
            typeof(Player).GetProperty("ScoreProcessor", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)!;

        private readonly OsuRLBridge bridge;
        private readonly Func<ScreenStack> screens;

        private Player? current;
        private string state = "";

        /// <summary>
        /// Bridge "setup" "background": false. The beatmap's background black during any play: the vision student
        /// learnt on maps imported without one, and a set's own image showed through even at 100% dim (a request,
        /// DISCO★PRINCE with katamari2.jpg, at about a third of its brightness: 5% accuracy, 2026-11-17).
        /// </summary>
        public bool HideBackground;
        private double lastClock = double.NegativeInfinity, lastLive = double.NegativeInfinity;

        public OsuRLWatcher(OsuRLBridge bridge, Func<ScreenStack> screens)
        {
            this.bridge = bridge;
            this.screens = screens;
        }

        /// <summary>The player on screen, looking into sub-screen stacks (multiplayer keeps its screens in one).</summary>
        private static Player? playerIn(IScreen? screen)
        {
            for (int depth = 0; screen != null && depth < 8; depth++)
            {
                if (screen is Player player)
                    return player;

                screen = (screen as IHasSubScreenStack)?.SubScreenStack.CurrentScreen;
            }

            return null;
        }

        protected override void Update()
        {
            base.Update();

            var player = playerIn(screens().CurrentScreen);

            if (player != current)
            {
                if (current != null && state != "")
                    played(current);

                current = player;
                state = "";
            }

            if (player == null)
                return;

            if (HideBackground && player.IsCurrentScreen())
            {
                try
                {
                    player.ApplyToBackground(b =>
                    {
                        if (b.Colour != Color4.Black)
                        {
                            b.ClearTransforms(false, nameof(b.Colour)); // Player fades it in from white on entering
                            b.Colour = Color4.Black;
                        }
                    });
                }
                catch (InvalidOperationException)
                {
                    // current but its background is not there yet (just pushed): next frame
                }
            }

            if (clock_property.GetValue(player) is not GameplayClockContainer clock)
                return;

            string now = clock.IsPaused.Value ? "paused" : "playing";

            if (now != state)
            {
                state = now;
                bridge.Send(new
                {
                    ev = "seat",
                    state,
                    md5 = player.Beatmap.Value.BeatmapInfo.MD5Hash,
                    mods = player.Score?.ScoreInfo.Mods.Select(m => m.Acronym).ToArray() ?? Array.Empty<string>(),
                    rate = clock.GetTrueGameplayRate(),
                });
            }

            if (state != "playing")
                return;

            double q = OsuRLPlayer.Qpc;

            if (player is not OsuRLPlayer && q - lastClock >= 0.002)
            {
                lastClock = q;
                bridge.Send(new { ev = "clock", qpc = q, t = clock.CurrentTime });
            }

            if (q - lastLive >= 0.5 && score_processor_property.GetValue(player) is ScoreProcessor score)
            {
                lastLive = q;
                bridge.Send(new { ev = "live", accuracy = score.Accuracy.Value, combo = score.Combo.Value, misses = misses(score) });
            }
        }

        private static int misses(ScoreProcessor score) => score.Statistics.TryGetValue(HitResult.Miss, out int n) ? n : 0;

        private void played(Player player)
        {
            var score = score_processor_property.GetValue(player) as ScoreProcessor;
            bridge.Send(new
            {
                ev = "played",
                md5 = player.Beatmap.Value.BeatmapInfo.MD5Hash,
                accuracy = score?.Accuracy.Value,
                max_combo = score?.HighestCombo.Value,
                misses = score == null ? (int?)null : misses(score),
                statistics = score?.Statistics.ToDictionary(kv => kv.Key.ToString(), kv => kv.Value),
            });
        }
    }
}
