// osu_rl (local research fork, not for upstream): a control and analysis link for scripts on this machine.
//
// Started when OSU_RL_BRIDGE=<port> is set. One TCP client at a time on 127.0.0.1:<port>, one JSON object per line
// each way. Commands (the reply is {"ok":true,...} or {"ok":false,"error":...}):
//   {"cmd":"status"}
//   {"cmd":"import","paths":["...osz","...osk"]}
//   {"cmd":"setup","skin":"<skin name>","hud":false}             skin by name, HUD always hidden or shown
//   {"cmd":"play","md5":"<beatmap md5>","mods":["NF"],"out":"<file.json>"}
//   {"cmd":"menu"}                                               back to song select
//   {"cmd":"focus"}                                              bring the window back in front
//   {"cmd":"input","x":256,"y":192,"b":1}                        (no reply) a play started with "input":"bridge":
//                                                                cursor in osu! pixels, held buttons (bit 0: M1, 1: M2)
// "play" also takes "input":"bridge": the play is driven by input commands, not the mouse, and goes on when the
// window is not in front.
// Events, while playing: {"ev":"state","state":"loading|playing|paused|done"}, {"ev":"clock","qpc":s,"t":ms}
// (qpc: Stopwatch.GetTimestamp() in seconds, the same clock as Python's time.perf_counter on Windows; t: gameplay
// time), {"ev":"judge",...} per judgement, and {"ev":"done","file":...,"accuracy":...} with everything written to "out".

using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using osu.Framework.Logging;

namespace osu.Game.OsuRL
{
    public class OsuRLBridge : IDisposable
    {
        private readonly TcpListener listener;
        private readonly Func<JObject, JObject> handle;
        private readonly object writeLock = new object();
        private StreamWriter? writer;
        private volatile bool stopped;

        /// <param name="handle">Runs a command and returns its reply; called on the bridge thread (schedule game work).</param>
        public OsuRLBridge(int port, Func<JObject, JObject> handle)
        {
            this.handle = handle;
            listener = new TcpListener(IPAddress.Loopback, port);
            listener.Start();
            new Thread(acceptLoop) { IsBackground = true, Name = "osu_rl bridge" }.Start();
            Logger.Log($"osu_rl bridge listening on 127.0.0.1:{port}");
        }

        private void acceptLoop()
        {
            while (!stopped)
            {
                TcpClient client;

                try
                {
                    client = listener.AcceptTcpClient();
                }
                catch (Exception) when (stopped)
                {
                    return;
                }

                client.NoDelay = true;
                // a new client replaces the old one
                new Thread(() => serve(client)) { IsBackground = true, Name = "osu_rl bridge client" }.Start();
            }
        }

        private void serve(TcpClient client)
        {
            using (client)
            {
                var stream = client.GetStream();
                var reader = new StreamReader(stream, new UTF8Encoding(false));
                var w = new StreamWriter(stream, new UTF8Encoding(false)) { AutoFlush = true, NewLine = "\n" };

                lock (writeLock)
                    writer = w;

                try
                {
                    string? line;

                    while ((line = reader.ReadLine()) != null)
                    {
                        if (string.IsNullOrWhiteSpace(line))
                            continue;

                        JObject reply;

                        try
                        {
                            var cmd = JObject.Parse(line);

                            if ((string?)cmd["cmd"] == "input")
                            {
                                // gameplay input, up to a few hundred a second: straight to the play, no reply
                                Input?.Push((float)cmd["x"]!, (float)cmd["y"]!, (int?)cmd["b"] ?? 0);
                                continue;
                            }

                            reply = handle(cmd);
                            if (cmd["id"] != null)
                                reply["id"] = cmd["id"];
                        }
                        catch (Exception e)
                        {
                            reply = new JObject { ["ok"] = false, ["error"] = e.Message };
                        }

                        writeLine(w, reply.ToString(Formatting.None));
                    }
                }
                catch (IOException)
                {
                }
                finally
                {
                    lock (writeLock)
                    {
                        if (writer == w)
                            writer = null;
                    }
                }
            }
        }

        public bool Connected => writer != null;

        /// <summary>Where "input" commands go: the play that takes bridge input, while there is one.</summary>
        public volatile IOsuRLInput? Input;

        /// <summary>Sends an event to the client, if one is connected (from any thread).</summary>
        public void Send(object ev)
        {
            StreamWriter? w = writer;
            if (w == null)
                return;

            writeLine(w, JsonConvert.SerializeObject(ev));
        }

        private void writeLine(StreamWriter w, string text)
        {
            lock (writeLock)
            {
                try
                {
                    w.WriteLine(text);
                }
                catch (Exception)
                {
                    if (writer == w)
                        writer = null;
                }
            }
        }

        public void Dispose()
        {
            stopped = true;
            listener.Stop();
        }
    }
}
