// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NUnit.Framework;
using osu.Framework.Extensions;
using osu.Framework.Screens;
using osu.Framework.Testing;
using osu.Game.Beatmaps;
using osu.Game.Beatmaps.Formats;
using osu.Game.IO;
using osu.Game.Rulesets.Judgements;
using osu.Game.Rulesets.Objects;
using osu.Game.Scoring;
using osu.Game.Scoring.Legacy;
using osu.Game.Screens.Play;
using osu.Game.Tests.Visual;

namespace osu.Game.Rulesets.Osu.Tests
{
    /// <summary>
    /// Plays osu_rl replays headlessly with lazer's real judgement logic.
    /// Env: OSU_RL_JOBS = file with one "map.osu|replay.osr|out.csv" job per line; each out.csv gets every judgement.
    /// </summary>
    [HeadlessTest]
    public partial class TestSceneOsuRLReplay : RateAdjustedBeatmapTestScene
    {
        private ReplayPlayer player = null!;
        private readonly List<JudgementResult> results = new List<JudgementResult>();

        [Test]
        public void TestReplays()
        {
            string? jobs = Environment.GetEnvironmentVariable("OSU_RL_JOBS");

            if (jobs == null)
                Assert.Ignore("OSU_RL_JOBS not set");

            foreach (string[] job in File.ReadAllLines(jobs).Where(l => l.Length > 0).Select(l => l.Split('|')))
            {
                string map = job[0], replay = job[1], output = job[2];

                AddStep($"load {Path.GetFileName(replay)}", () =>
                {
                    using (var reader = new LineBufferedReader(File.OpenRead(map)))
                        Beatmap.Value = CreateWorkingBeatmap(Decoder.GetDecoder<Beatmap>(reader).Decode(reader));
                    Ruleset.Value = new OsuRuleset().RulesetInfo;

                    string mapMd5;
                    using (var s = File.OpenRead(map))
                        mapMd5 = s.ComputeMD5Hash();

                    Score score;
                    using (var s = File.OpenRead(replay))
                        score = new FileScoreDecoder(Beatmap.Value, mapMd5).Parse(s);

                    SelectedMods.Value = score.ScoreInfo.Mods;
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
                double endTime;
                using (var reader = new LineBufferedReader(File.OpenRead(map)))
                    endTime = Decoder.GetDecoder<Beatmap>(reader).Decode(reader).HitObjects.Max(h => h.GetEndTime());
                for (double t = 10000; t < endTime; t += 10000)
                {
                    double target = t;
                    AddUntilStep($"reach {target}", () => player.GameplayState.HasCompleted || Beatmap.Value.Track.CurrentTime >= target);
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

        private class FileScoreDecoder : LegacyScoreDecoder
        {
            private readonly WorkingBeatmap beatmap;
            private readonly string md5;

            public FileScoreDecoder(WorkingBeatmap beatmap, string md5)
            {
                this.beatmap = beatmap;
                this.md5 = md5;
            }

            protected override Ruleset GetRuleset(int rulesetId) => new OsuRuleset();

            protected override WorkingBeatmap GetBeatmap(string md5Hash)
            {
                Assert.That(md5Hash, Is.EqualTo(md5), "replay beatmap hash does not match the .osu file");
                return beatmap;
            }
        }
    }
}
