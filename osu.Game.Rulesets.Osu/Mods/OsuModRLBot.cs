// Copyright (c) osu_rl. Licensed under the MIT Licence.

using System;
using System.Collections.Generic;
using System.Linq;
using osu.Framework.Graphics.Sprites;
using osu.Framework.Localisation;
using osu.Game.Beatmaps;
using osu.Game.Graphics;
using osu.Game.Rulesets.Mods;
using osu.Game.Rulesets.Osu.Replays;

namespace osu.Game.Rulesets.Osu.Mods
{
    public class OsuModRLBot : ModAutoplay
    {
        public override string Name => "RL Bot";
        public override string Acronym => "RL";
        public override LocalisableString Description => "Plays beatmaps with a deep reinforcement learning policy trained in osu_rl.";
        public override IconUsage? Icon => FontAwesome.Solid.Robot;
        public override ModType Type => ModType.Automation;

        public override Type[] IncompatibleMods => base.IncompatibleMods.Concat(new[]
        {
            typeof(OsuModAutoplay),
            typeof(OsuModCinema),
            typeof(OsuModAutopilot),
            typeof(OsuModRelax)
        }).ToArray();

        public override ModReplayData CreateReplayData(IBeatmap beatmap, IReadOnlyList<Mod> mods)
            => new ModReplayData(new OsuRLBotGenerator(beatmap, mods).Generate(), new ModCreatedUser { Username = "osu_rl Bot" });
    }
}
