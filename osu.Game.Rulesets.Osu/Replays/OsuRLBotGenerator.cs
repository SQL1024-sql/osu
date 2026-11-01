// Copyright (c) osu_rl. Licensed under the MIT Licence.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using osu.Game.Beatmaps;
using osu.Game.Replays;
using osu.Game.Rulesets.Mods;
using osu.Game.Rulesets.Objects;
using osu.Game.Rulesets.Objects.Types;
using osu.Game.Rulesets.Osu.Beatmaps;
using osu.Game.Rulesets.Osu.Mods.RLBot;
using osu.Game.Rulesets.Osu.Objects;
using osu.Game.Rulesets.Osu.Scoring;
using osu.Game.Rulesets.Scoring;
using osuTK;

namespace osu.Game.Rulesets.Osu.Replays
{
    public class OsuRLBotGenerator : OsuAutoGeneratorBase
    {
        public const float MAX_CURSOR_SPEED = 4.8f; // osu px per ms
        public const double SIM_HZ = 120.0;
        public const double DT = 1000.0 / SIM_HZ; // ~8.333 ms
        public const float FOLLOW_AREA = 2.4f;

        private static readonly double[] lookahead_ms = { DT, 50.0, 150.0 };
        private static NeuralNetwork? cachedNetwork;

        public new OsuBeatmap Beatmap => (OsuBeatmap)base.Beatmap;

        public OsuRLBotGenerator(IBeatmap beatmap, IReadOnlyList<Mod> mods)
            : base(beatmap, mods)
        {
        }

        private static NeuralNetwork getNetwork()
        {
            if (cachedNetwork != null)
                return cachedNetwork;

            // 1. Try embedded resource
            var asm = typeof(OsuRLBotGenerator).Assembly;
            var resourceName = asm.GetManifestResourceNames().FirstOrDefault(n => n.EndsWith("model_weights.bin", StringComparison.OrdinalIgnoreCase));
            if (resourceName != null)
            {
                using var s = asm.GetManifestResourceStream(resourceName);
                if (s != null)
                    return cachedNetwork = new NeuralNetwork(s);
            }

            // 2. Try adjacent / known paths
            string[] candidatePaths =
            {
                Path.Combine(AppContext.BaseDirectory, "model_weights.bin"),
                Path.Combine(AppContext.BaseDirectory, "merge_ppyosu", "model_weights.bin"),
                @"C:\Peter\Code\osu_rl\merge_ppyosu\model_weights.bin"
            };

            foreach (var path in candidatePaths)
            {
                if (File.Exists(path))
                    return cachedNetwork = NeuralNetwork.FromFile(path);
            }

            throw new FileNotFoundException("osu_rl model_weights.bin not found. Ensure it is placed in the output directory or merge_ppyosu.");
        }

        public override Replay Generate()
        {
            if (Beatmap.HitObjects.Count == 0)
                return Replay;

            var net = getNetwork();
            int k = (net.InputDimension - 20) / 6; // e.g. 3 or 5

            var hitWindows = new OsuHitWindows();
            hitWindows.SetDifficulty(Beatmap.Difficulty.OverallDifficulty);
            float w50 = (float)hitWindows.WindowFor(HitResult.Meh);
            float radius = (float)(Beatmap.HitObjects[0].Radius);

            double firstPreempt = Beatmap.HitObjects[0].TimePreempt;
            double startTime = Math.Max(0, Beatmap.HitObjects[0].StartTime - firstPreempt);
            double endTime = Beatmap.HitObjects.Max(h => h.GetEndTime()) + 500;

            Vector2 cursor = new Vector2(256, 192);
            bool key1 = false;
            bool key2 = false;

            var hitObjects = Beatmap.HitObjects;
            int totalObjects = hitObjects.Count;
            bool[] started = new bool[totalObjects];
            bool[] done = new bool[totalObjects];
            int nextIdx = 0;

            // Active tracking state
            Slider? activeSlider = null;
            bool isTracking = false;
            Spinner? activeSpinner = null;
            double spinnerRotation = 0;
            Vector2 lastSpinnerCursor = cursor;

            float[] obs = new float[net.InputDimension];
            float stepSpeed = MAX_CURSOR_SPEED * (float)DT;

            for (double t = startTime; t <= endTime; t += DT)
            {
                // 1. Advance nextIdx past already started objects
                while (nextIdx < totalObjects && started[nextIdx])
                    nextIdx++;

                // 2. Start any spinners whose start time has arrived
                for (int i = nextIdx; i < totalObjects; i++)
                {
                    if (hitObjects[i] is Spinner sp && !started[i] && t >= sp.StartTime)
                    {
                        started[i] = true;
                        activeSpinner = sp;
                        spinnerRotation = 0;
                        lastSpinnerCursor = cursor;
                    }
                }

                // 3. Mark late misses for unstarted objects past hit window
                for (int i = nextIdx; i < totalObjects; i++)
                {
                    var h = hitObjects[i];
                    if (!started[i] && h is not Spinner && t > h.StartTime + w50)
                    {
                        started[i] = true;
                        if (h is Slider sl)
                        {
                            activeSlider = sl;
                            isTracking = false;
                        }
                        else
                        {
                            done[i] = true;
                        }
                    }
                }

                // 4. Update active slider end
                if (activeSlider != null && t > activeSlider.EndTime)
                {
                    activeSlider = null;
                    isTracking = false;
                }

                // 5. Update active spinner end
                if (activeSpinner != null && t > activeSpinner.EndTime)
                {
                    activeSpinner = null;
                    spinnerRotation = 0;
                }

                // 6. Build Observation vector
                int ptr = 0;
                obs[ptr++] = cursor.X / 512f;
                obs[ptr++] = cursor.Y / 384f;
                obs[ptr++] = key1 ? 1f : 0f;
                obs[ptr++] = key2 ? 1f : 0f;
                obs[ptr++] = radius / 64f;
                obs[ptr++] = w50 / 200f;

                // Upcoming K objects (unstarted only)
                int added = 0;
                for (int i = nextIdx; i < totalObjects && added < k; i++)
                {
                    if (started[i])
                        continue;

                    var h = hitObjects[i];
                    double preempt = h.TimePreempt;
                    if (h.StartTime - t <= preempt)
                    {
                        Vector2 pos = h.StackedPosition;
                        obs[ptr++] = (pos.X - cursor.X) / 256f;
                        obs[ptr++] = (pos.Y - cursor.Y) / 256f;
                        obs[ptr++] = (float)((h.StartTime - t) / 200f);
                        obs[ptr++] = 1.0f;
                        obs[ptr++] = h is Slider ? 1f : 0f;
                        obs[ptr++] = h is Spinner ? 1f : 0f;
                    }
                    else
                    {
                        for (int j = 0; j < 6; j++) obs[ptr++] = 0f;
                    }
                    added++;
                }
                while (added < k)
                {
                    for (int j = 0; j < 6; j++) obs[ptr++] = 0f;
                    added++;
                }

                // Active Slider observation
                if (activeSlider != null)
                {
                    obs[ptr++] = 1f;
                    obs[ptr++] = isTracking ? 1f : 0f;
                    obs[ptr++] = (float)((activeSlider.EndTime - t) / 200f);

                    foreach (double la in lookahead_ms)
                    {
                        double p = Math.Clamp((t + la - activeSlider.StartTime) / activeSlider.Duration, 0.0, 1.0);
                        Vector2 laBall = activeSlider.StackedPositionAt(p);
                        obs[ptr++] = (laBall.X - cursor.X) / 256f;
                        obs[ptr++] = (laBall.Y - cursor.Y) / 256f;
                    }
                }
                else
                {
                    int sliderDims = 3 + 2 * lookahead_ms.Length;
                    for (int j = 0; j < sliderDims; j++) obs[ptr++] = 0f;
                }

                // Active Spinner observation
                if (activeSpinner != null)
                {
                    float progress = activeSpinner.SpinsRequired <= 0 ? 1.0f : (float)(spinnerRotation / (activeSpinner.SpinsRequired * 2 * Math.PI));
                    obs[ptr++] = 1f;
                    obs[ptr++] = Math.Min(1f, progress);
                    obs[ptr++] = (float)((activeSpinner.EndTime - t) / 1000f);
                    obs[ptr++] = (cursor.X - SPINNER_CENTRE.X) / 256f;
                    obs[ptr++] = (cursor.Y - SPINNER_CENTRE.Y) / 256f;
                }
                else
                {
                    for (int j = 0; j < 5; j++) obs[ptr++] = 0f;
                }

                // 7. Run Neural Network Policy
                float[] action = net.Predict(obs);
                float dx = action[0];
                float dy = action[1];
                bool nextKey1 = action[2] > 0;
                bool nextKey2 = action[3] > 0;

                // 8. Move Cursor
                cursor.X = Math.Clamp(cursor.X + dx * stepSpeed, -100f, 612f);
                cursor.Y = Math.Clamp(cursor.Y + dy * stepSpeed, -100f, 484f);

                // 9. Key press detection (transitions from unpressed -> pressed)
                bool press1 = nextKey1 && !key1;
                bool press2 = nextKey2 && !key2;
                bool hasPress = press1 || press2;

                if (hasPress)
                {
                    for (int j = nextIdx; j < totalObjects; j++)
                    {
                        var h = hitObjects[j];
                        if (started[j] || h is Spinner)
                            continue;

                        double timeOffset = t - h.StartTime;
                        if (Math.Abs(timeOffset) <= w50 && Vector2.Distance(cursor, h.StackedPosition) <= radius)
                        {
                            started[j] = true;
                            if (h is Slider sl)
                            {
                                activeSlider = sl;
                                isTracking = true;
                            }
                            else
                            {
                                done[j] = true;
                            }
                            break;
                        }
                    }
                }

                // 10. Update slider tracking state
                if (activeSlider != null)
                {
                    double curProgress = Math.Clamp((t - activeSlider.StartTime) / activeSlider.Duration, 0.0, 1.0);
                    Vector2 ballPos = activeSlider.StackedPositionAt(curProgress);
                    float followRadius = radius * FOLLOW_AREA;
                    bool inFollow = Vector2.Distance(cursor, ballPos) <= followRadius;
                    isTracking = (nextKey1 || nextKey2) && inFollow;
                }

                // 11. Update spinner rotation
                if (activeSpinner != null && (nextKey1 || nextKey2))
                {
                    Vector2 d0 = lastSpinnerCursor - SPINNER_CENTRE;
                    Vector2 d1 = cursor - SPINNER_CENTRE;
                    float a0 = MathF.Atan2(d0.Y, d0.X);
                    float a1 = MathF.Atan2(d1.Y, d1.X);
                    float delta = a1 - a0;
                    while (delta > MathF.PI) delta -= MathF.PI * 2;
                    while (delta < -MathF.PI) delta += MathF.PI * 2;
                    spinnerRotation += Math.Abs(delta);
                }
                lastSpinnerCursor = cursor;

                // 12. Add Frame to Replay
                var actions = new List<OsuAction>();
                if (nextKey1) actions.Add(OsuAction.LeftButton);
                if (nextKey2) actions.Add(OsuAction.RightButton);

                AddFrameToReplay(new OsuReplayFrame(Math.Round(t), cursor, actions.ToArray()));

                key1 = nextKey1;
                key2 = nextKey2;
            }

            return Replay;
        }
    }
}
