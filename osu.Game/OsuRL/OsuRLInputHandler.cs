// osu_rl (local research fork, not for upstream): gameplay input that comes over the osu_rl bridge instead of the
// mouse, so a play can go on in the background while the computer is used for something else.

using System.Collections.Concurrent;
using System.Collections.Generic;
using osu.Framework.Input.StateChanges;
using osu.Game.Input.Handlers;
using osuTK;

namespace osu.Game.OsuRL
{
    public interface IOsuRLInput
    {
        /// <summary>The cursor at (x, y) in osu! pixels (the gamefield), with these buttons held (bit 0: first action,
        /// bit 1: second). Any thread; applied in order on the game's next input update.</summary>
        void Push(float x, float y, int buttons);
    }

    /// <summary>
    /// Takes the place of the mouse for a ruleset's input manager (like a replay does, but live): every pushed state
    /// becomes a cursor position followed by the held actions, so a press pushed with the old position and then a move
    /// is judged where the cursor was.
    /// </summary>
    public class OsuRLInputHandler<T> : ReplayInputHandler, IOsuRLInput
        where T : struct
    {
        private readonly T[] actions;
        private readonly ConcurrentQueue<(Vector2 pos, int buttons)> pushed = new ConcurrentQueue<(Vector2, int)>();

        public OsuRLInputHandler(T[] actions)
        {
            this.actions = actions;
        }

        public void Push(float x, float y, int buttons) => pushed.Enqueue((new Vector2(x, y), buttons));

        public override bool IsActive => true;

        // the gameplay clock is not held back by this input (it is not a replay)
        public override double? SetFrameFromTime(double time) => time;

        public override void CollectPendingInputs(List<IInput> inputs)
        {
            while (pushed.TryDequeue(out var s))
            {
                if (GamefieldToScreenSpace == null)
                    continue;

                inputs.Add(new MousePositionAbsoluteInput { Position = GamefieldToScreenSpace(s.pos) });

                var held = new List<T>();
                for (int i = 0; i < actions.Length; i++)
                {
                    if ((s.buttons & (1 << i)) != 0)
                        held.Add(actions[i]);
                }

                inputs.Add(new ReplayState<T> { PressedActions = held });
            }

            base.CollectPendingInputs(inputs);
        }
    }
}
