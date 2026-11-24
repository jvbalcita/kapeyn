using System;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Threading;
using System.Windows.Forms;

namespace Kapeyn
{
    // The tray app listens on a per-user named pipe. The "kapeyn" command sends one line and reads the reply.
    sealed class IpcServer
    {
        readonly Control invoker;
        readonly Func<string, string> handler;
        volatile bool stopping;

        public IpcServer(Control invoker, Func<string, string> handler)
        {
            this.invoker = invoker;
            this.handler = handler;
        }

        public void Start()
        {
            var t = new Thread(Loop);
            t.IsBackground = true;
            t.Name = "Kapeyn IPC";
            t.Start();
        }

        public void Stop()
        {
            stopping = true;
        }

        void Loop()
        {
            while (!stopping)
            {
                try
                {
                    using (var pipe = new NamedPipeServerStream(Program.PipeName, PipeDirection.InOut, 1,
                                                                PipeTransmissionMode.Byte, PipeOptions.None))
                    {
                        pipe.WaitForConnection();
                        if (stopping) break;

                        string line = ReadLine(pipe);
                        string reply;
                        try
                        {
                            reply = (string)invoker.Invoke(new Func<string>(delegate { return handler(line); }));
                        }
                        catch (Exception ex)
                        {
                            reply = "error: " + ex.Message;
                        }
                        byte[] bytes = Encoding.UTF8.GetBytes(reply ?? "");
                        pipe.Write(bytes, 0, bytes.Length);
                        pipe.Flush();
                        try { pipe.WaitForPipeDrain(); } catch { }
                    }
                }
                catch
                {
                    if (stopping) break;
                    Thread.Sleep(250);
                }
            }
        }

        static string ReadLine(Stream s)
        {
            var buf = new MemoryStream();
            int b;
            while (buf.Length < 4096 && (b = s.ReadByte()) >= 0 && b != '\n') buf.WriteByte((byte)b);
            return Encoding.UTF8.GetString(buf.ToArray()).Trim();
        }
    }

    static class IpcClient
    {
        /// Sends one command to the running tray app. Returns false if it isn't reachable.
        public static bool TrySend(string command, int connectTimeoutMs, out string reply)
        {
            reply = null;
            try
            {
                using (var c = new NamedPipeClientStream(".", Program.PipeName, PipeDirection.InOut))
                {
                    c.Connect(connectTimeoutMs);
                    byte[] bytes = Encoding.UTF8.GetBytes(command + "\n");
                    c.Write(bytes, 0, bytes.Length);
                    c.Flush();
                    using (var r = new StreamReader(c, Encoding.UTF8)) reply = r.ReadToEnd();
                    return true;
                }
            }
            catch { return false; }
        }
    }
}
