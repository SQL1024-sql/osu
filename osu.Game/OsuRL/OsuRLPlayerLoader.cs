// osu_rl (local research fork, not for upstream): the loader for a play driven over the bridge in the background.

using System;
using osu.Game.Screens.Play;

namespace osu.Game.OsuRL
{
    public partial class OsuRLPlayerLoader : PlayerLoader
    {
        public OsuRLPlayerLoader(Func<Player> createPlayer)
            : base(createPlayer)
        {
            // the window may be behind others: start anyway
            WindowShouldBeActiveForGameplayStart = false;
        }

        // nobody is at the mouse (it is not the play's input): do not wait for it to rest on the loader or go idle
        protected override bool ReadyForGameplay => true;
    }
}
