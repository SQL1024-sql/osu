// osu_rl (local research fork, not for upstream): a solo play that reports itself to the osu_rl bridge.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
using osu.Framework.Screens;
using osu.Game.Rulesets.Judgements;
using osu.Game.Rulesets.Objects.Types;
using osu.Game.Rulesets.Replays.Types;
using osu.Game.Screens.Play;
using osuTK;

namespace osu.Game.OsuRL
{
    public partial class OsuRLPlayer : SoloPlayer
    {
        private readonly OsuRLBridge bridge;
        private readonly string? outPath;
        private readonly List<object[]> judgements = new List<object[]>();
        private readonly List<double[]> clock = new List<double[]>();
        private double lastSent = double.NegativeInfinity;
        private string state = "loading";
        private bool written;

        public OsuRLPlayer(OsuRLBridge bridge, string? outPath)
        {
            this.bridge = bridge;
            this.outPath = outPath;
        }

        public static double Qpc => Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency;

        protected override void LoadComplete()
        {
            base.LoadComplete();

            ScoreProcessor.NewJudgement += onJudgement;
            ScoreProcessor.HasCompleted.BindValueChanged(c =>
            {
                if (c.NewValue)
                    finish(false);
            });
        }

        private void onJudgement(JudgementResult r)
        {
            var h = r.HitObject;
            // StackedPosition (where it is drawn) lives in the osu! ruleset; fall back to the unstacked position
            object? stacked = h.GetType().GetProperty("StackedPosition")?.GetValue(h);
            Vector2? pos = stacked is Vector2 v ? v : h is IHasPosition p ? p.Position : null;
            var row = new object[]
            {
                h.StartTime, h.GetType().Name, r.Type.ToString(), Math.Round(r.TimeOffset, 3), Math.Round(r.TimeAbsolute, 3),
                pos?.X ?? double.NaN, pos?.Y ?? double.NaN, r.IsHit
            };
            judgements.Add(row);
            bridge.Send(new { ev = "judge", t = r.TimeAbsolute, start = h.StartTime, kind = row[1], result = row[2], offset = row[3], hit = r.IsHit });
        }

        protected override void Update()
        {
            base.Update();

            if (GameplayClockContainer == null || written)
                return;

            setState(GameplayClockContainer.IsPaused.Value ? "paused" : "playing");

            if (state != "playing")
                return;

            double q = Qpc, t = GameplayClockContainer.CurrentTime;
            clock.Add(new[] { q, t });

            if (q - lastSent >= 0.002)
            {
                lastSent = q;
                bridge.Send(new { ev = "clock", qpc = q, t });
            }
        }

        private void setState(string s)
        {
            if (s == state)
                return;

            state = s;
            bridge.Send(new { ev = "state", state = s });
        }

        public override bool OnExiting(ScreenExitEvent e)
        {
            finish(true);
            return base.OnExiting(e);
        }

        private void finish(bool quit)
        {
            if (written)
                return;

            written = true;
            setState("done");

            var score = Score.ScoreInfo;
            var beatmap = GameplayState.Beatmap;
            var frames = Score.Replay.Frames.OfType<IConvertibleReplayFrame>().Select(f =>
            {
                var l = f.ToLegacy(beatmap);
                return new[] { l.Time, l.MouseX ?? double.NaN, l.MouseY ?? double.NaN, (double)(int)l.ButtonState };
            }).ToList();

            var result = new
            {
                md5 = beatmap.BeatmapInfo.MD5Hash,
                map = beatmap.BeatmapInfo.ToString(),
                mods = Score.ScoreInfo.Mods.Select(m => m.Acronym).ToArray(),
                accuracy = ScoreProcessor.Accuracy.Value,
                max_combo = ScoreProcessor.HighestCombo.Value,
                statistics = score.Statistics.ToDictionary(kv => kv.Key.ToString(), kv => kv.Value),
                failed = GameplayState.HasFailed,
                quit = quit && !ScoreProcessor.HasCompleted.Value,
                judgement_cols = new[] { "start", "kind", "result", "offset", "time", "x", "y", "hit" },
                judgements,
                frame_cols = new[] { "time", "x", "y", "buttons" },
                frames,
                clock_cols = new[] { "qpc", "t" },
                clock,
            };

            string? file = null;

            if (!string.IsNullOrEmpty(outPath))
            {
                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outPath))!);
                    File.WriteAllText(outPath, JsonConvert.SerializeObject(result));
                    file = outPath;
                }
                catch (Exception ex)
                {
                    bridge.Send(new { ev = "error", error = $"could not write {outPath}: {ex.Message}" });
                }
            }

            bridge.Send(new { ev = "done", file, accuracy = result.accuracy, statistics = result.statistics, quit = result.quit, failed = result.failed });
        }
    }
}
