// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
using NUnit.Framework;
using osu.Framework.Extensions;
using osu.Framework.Screens;
using osu.Framework.Testing;
using osu.Game.Beatmaps;
using osu.Game.Beatmaps.Formats;
using osu.Game.IO;
using osu.Game.Online.API;
using osu.Game.Online.API.Requests.Responses;
using osu.Game.Rulesets.Objects;
using osu.Game.Rulesets.Scoring;
using osu.Game.Scoring;
using osu.Game.Scoring.Legacy;
using osu.Game.Screens.Play;
using osu.Game.Tests.Visual;

namespace osu.Game.Rulesets.Osu.Tests
{
    /// <summary>
    /// Scores an osu_rl replay with lazer's own processors, for the AI player in online rooms (osu_rl live/).
    /// Env: OSU_RL_LIVE_JOB = file with one line "map.osu|replay.osr|out.json|[APIMod json array]".
    /// out.json: the spectator header after every judgement, the maximum statistics, and the score submission body.
    /// </summary>
    [HeadlessTest]
    public partial class TestSceneOsuRLLive : RateAdjustedBeatmapTestScene
    {
        private ReplayPlayer player = null!;
        private readonly List<object> headers = new List<object>();

        [Test]
        public void TestScore()
        {
            string? jobFile = Environment.GetEnvironmentVariable("OSU_RL_LIVE_JOB");

            if (jobFile == null)
                Assert.Ignore("OSU_RL_LIVE_JOB not set");

            string[] job = File.ReadAllText(jobFile).Trim().Split('|');
            string map = job[0], replay = job[1], output = job[2];
            var ruleset = new OsuRuleset();
            var mods = JsonConvert.DeserializeObject<APIMod[]>(job.Length > 3 && job[3].Length > 0 ? job[3] : "[]")!.Select(m => m.ToMod(ruleset)).ToArray();
            double endTime = 0;

            AddStep("load", () =>
            {
                using (var reader = new LineBufferedReader(File.OpenRead(map)))
                    Beatmap.Value = CreateWorkingBeatmap(Decoder.GetDecoder<Beatmap>(reader).Decode(reader));
                Ruleset.Value = ruleset.RulesetInfo;
                SelectedMods.Value = mods;
                endTime = Beatmap.Value.Beatmap.HitObjects.Max(h => h.GetEndTime());

                string mapMd5;
                using (var s = File.OpenRead(map))
                    mapMd5 = s.ComputeMD5Hash();

                Score score;
                using (var s = File.OpenRead(replay))
                    score = new FileScoreDecoder(Beatmap.Value, mapMd5).Parse(s);
                score.ScoreInfo.Mods = mods; // the room's mods, not the .osr's

                var p = player = new ReplayPlayer(score);
                // as MultiplayerPlayer: judgements keep scoring after health runs out
                p.OnLoadComplete += _ => p.GameplayState.ScoreProcessor.ApplyNewJudgementsWhenFailed = true;
                p.OnLoadComplete += _ => p.GameplayState.ScoreProcessor.NewJudgement += r =>
                {
                    if (player != p)
                        return;

                    var sp = p.GameplayState.ScoreProcessor;
                    var stats = sp.GetScoreProcessorStatistics();
                    headers.Add(new
                    {
                        time = r.TimeAbsolute,
                        totalScore = sp.TotalScore.Value,
                        accuracy = sp.Accuracy.Value,
                        combo = sp.Combo.Value,
                        maxCombo = sp.HighestCombo.Value,
                        statistics = byId(sp.Statistics),
                        scoreProcessorStatistics = new
                        {
                            baseScore = stats.BaseScore,
                            maximumBaseScore = stats.MaximumBaseScore,
                            accuracyJudgementCount = stats.AccuracyJudgementCount,
                            comboPortion = stats.ComboPortion,
                            bonusPortion = stats.BonusPortion,
                        },
                    });
                };
                headers.Clear();
                LoadScreen(p);
            });

            AddUntilStep("wait for load", () => player.IsCurrentScreen());

            // until steps time out, so long maps are waited for in chunks of gameplay time
            for (double t = 10000; t < 1_000_000; t += 10000)
            {
                double target = t;
                AddUntilStep($"reach {target}", () => player.GameplayState.HasCompleted || target > endTime || Beatmap.Value.Track.CurrentTime >= target);
            }

            AddUntilStep("wait for completion", () => player.GameplayState.HasCompleted || player.GameplayState.HasFailed);
            AddStep("write", () =>
            {
                var state = player.GameplayState;
                var info = state.Score.ScoreInfo.DeepClone();
                state.ScoreProcessor.PopulateScore(info);
                info.Mods = mods;
                info.Passed = !state.HasFailed;
                if (state.HasFailed)
                    state.ScoreProcessor.FailScore(info);

                File.WriteAllText(output, JsonConvert.SerializeObject(new
                {
                    headers,
                    maximumStatistics = byId(state.ScoreProcessor.MaximumStatistics),
                    submission = SoloScoreInfo.ForSubmission(info),
                }, new JsonSerializerSettings { ReferenceLoopHandling = ReferenceLoopHandling.Ignore }));
            });
        }

        // the hubs carry HitResult keys as numbers (MessagePack enums)
        private static Dictionary<string, int> byId(IReadOnlyDictionary<HitResult, int> statistics)
            => statistics.Where(kv => kv.Value != 0).ToDictionary(kv => ((int)kv.Key).ToString(), kv => kv.Value);

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
