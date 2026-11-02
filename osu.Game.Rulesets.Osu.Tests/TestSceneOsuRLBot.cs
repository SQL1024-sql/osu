// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NUnit.Framework;
using osu.Framework.Screens;
using osu.Framework.Testing;
using osu.Game.Beatmaps;
using osu.Game.Beatmaps.Formats;
using osu.Game.IO;
using osu.Game.Rulesets.Judgements;
using osu.Game.Rulesets.Mods;
using osu.Game.Rulesets.Objects;
using osu.Game.Rulesets.Osu.Mods;
using osu.Game.Screens.Play;
using osu.Game.Tests.Visual;

namespace osu.Game.Rulesets.Osu.Tests
{
    /// <summary>
    /// Plays maps with the RL Bot mod (merge_ppyosu) headlessly and writes every judgement, like TestSceneOsuRLReplay.
    /// Env: OSU_RL_BOT_JOBS = file with one "map.osu|out.csv" job per line.
    /// </summary>
    [HeadlessTest]
    public partial class TestSceneOsuRLBot : RateAdjustedBeatmapTestScene
    {
        private ReplayPlayer player = null!;
        private readonly List<JudgementResult> results = new List<JudgementResult>();

        [Test]
        public void TestBot()
        {
            string? jobs = Environment.GetEnvironmentVariable("OSU_RL_BOT_JOBS");

            if (jobs == null)
                Assert.Ignore("OSU_RL_BOT_JOBS not set");

            foreach (string[] job in File.ReadAllLines(jobs).Where(l => l.Length > 0).Select(l => l.Split('|')))
            {
                string map = job[0], output = job[1];
                double endTime = 0;

                AddStep($"load {Path.GetFileName(map)}", () =>
                {
                    using (var reader = new LineBufferedReader(File.OpenRead(map)))
                        Beatmap.Value = CreateWorkingBeatmap(Decoder.GetDecoder<Beatmap>(reader).Decode(reader));
                    Ruleset.Value = new OsuRuleset().RulesetInfo;
                    var mods = new Mod[] { new OsuModClassic() }; // the simulator models classic rules, as the .osr replays do
                    SelectedMods.Value = mods;

                    var playable = Beatmap.Value.GetPlayableBeatmap(Ruleset.Value);
                    endTime = playable.HitObjects.Max(h => h.GetEndTime());
                    var score = new OsuModRLBot().CreateScoreFromReplayData(playable, mods);
                    score.ScoreInfo.Ruleset = Ruleset.Value;
                    score.ScoreInfo.BeatmapInfo = Beatmap.Value.BeatmapInfo;
                    score.ScoreInfo.Mods = mods;

                    var p = player = new ReplayPlayer(score);
                    p.OnLoadComplete += _ => p.GameplayState.ScoreProcessor.NewJudgement += r =>
                    {
                        if (player == p)
                            results.Add(r);
                    };
                    results.Clear();
                    LoadScreen(p);
                });

                AddUntilStep("wait for load", () => player.IsCurrentScreen());

                // until steps time out, so long maps are waited for in chunks of gameplay time
                for (double t = 10000; t < 1_000_000; t += 10000)
                {
                    double target = t;
                    AddUntilStep($"reach {target}", () => player.GameplayState.HasCompleted || target > endTime || Beatmap.Value.Track.CurrentTime >= target);
                }

                AddUntilStep("wait for completion", () => player.GameplayState.HasCompleted);
                AddStep("write results", () =>
                {
                    var sp = player.GameplayState.ScoreProcessor;
                    File.WriteAllLines(output, new[] { $"# accuracy={sp.Accuracy.Value},max_combo={sp.HighestCombo.Value},mods={string.Join('|', sp.Mods.Value.Select(m => m.Acronym))}" }
                        .Concat(results.Select(r => $"{r.HitObject.StartTime},{r.HitObject.GetType().Name},{r.Type},{r.TimeOffset}")));
                });
            }
        }
    }
}
