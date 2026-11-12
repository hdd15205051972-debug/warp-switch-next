using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace WarpSwitchNext
{
    static class ClientHello
    {
        /// <summary>
        /// Splits the first TLS record (a ClientHello) into several smaller TLS records.
        /// The handshake bytes are unchanged; only record boundaries move. Certificate and
        /// public-key verification stay in usque, beyond this byte relay.
        /// Returns null while the record is still incomplete.
        /// </summary>
        public static List<byte[]> Split(byte[] b, int count, int chunk = 48)
        {
            if (count < 5) return null;
            int length = b[3] * 256 + b[4];
            if (b[0] != 22 || length <= 0 || length > 16384) return new List<byte[]> { Slice(b, 0, count) };
            if (count < length + 5) return null;
            if (b[5] != 1) return new List<byte[]> { Slice(b, 0, count) };
            var records = new List<byte[]>();
            int offset = 5;
            while (offset < length + 5)
            {
                int n = Math.Min(chunk, length + 5 - offset);
                var part = new byte[5 + n];
                part[0] = 22; part[1] = b[1]; part[2] = b[2];
                part[3] = (byte)(n >> 8); part[4] = (byte)(n & 255);
                Buffer.BlockCopy(b, offset, part, 5, n);
                records.Add(part);
                offset += n;
            }
            if (count > length + 5) records.Add(Slice(b, length + 5, count - length - 5));
            return records;
        }

        static byte[] Slice(byte[] b, int start, int count)
        {
            var r = new byte[count];
            Buffer.BlockCopy(b, start, r, 0, count);
            return r;
        }
    }

    /// <summary>
    /// Listens on 127.0.0.1 for usque and forwards each connection to a Cloudflare entry,
    /// optionally splitting the TLS ClientHello into small records first.
    /// </summary>
    sealed class HelloRelay
    {
        readonly string endpoint;
        readonly bool fragment;
        readonly int port;
        readonly object gate = new object();
        readonly HashSet<TcpClient> sockets = new HashSet<TcpClient>();
        readonly Random random = new Random();
        TcpListener listener;
        volatile bool stopped;

        public HelloRelay(string endpoint, bool fragment, int port)
        {
            this.endpoint = endpoint; this.fragment = fragment; this.port = port;
        }

        public void Start()
        {
            listener = new TcpListener(IPAddress.Loopback, port) { ExclusiveAddressUse = true };
            try { listener.Start(64); }
            catch (SocketException) { throw new UserError("本机端口 " + port + " 被其他程序占用，无法启动连接。请关闭占用它的程序后重试。"); }
            new Thread(AcceptLoop) { IsBackground = true, Name = "HelloRelay" }.Start();
        }

        public void Stop()
        {
            stopped = true;
            try { listener?.Stop(); } catch { }
            TcpClient[] open;
            lock (gate) { open = new TcpClient[sockets.Count]; sockets.CopyTo(open); sockets.Clear(); }
            foreach (var s in open) Close(s);
        }

        void AcceptLoop()
        {
            while (!stopped)
            {
                TcpClient client;
                try { client = listener.AcceptTcpClient(); }
                catch { break; }
                if (stopped) { Close(client); break; }
                Track(client);
                Task.Run(() => Session(client));
            }
        }

        void Track(TcpClient c) { lock (gate) sockets.Add(c); }

        void Close(TcpClient c)
        {
            lock (gate) sockets.Remove(c);
            try { c.Close(); } catch { }
        }

        int Jitter() { lock (random) return random.Next(2, 7); }

        async Task Session(TcpClient client)
        {
            var upstream = new TcpClient(AddressFamily.InterNetwork);
            Track(upstream);
            try
            {
                client.NoDelay = true; upstream.NoDelay = true;
                var connect = upstream.ConnectAsync(IPAddress.Parse(endpoint), 443);
                if (await Task.WhenAny(connect, Task.Delay(8000)) != connect || connect.IsFaulted) { Observe(connect); return; }
                var fromClient = client.GetStream();
                var toServer = upstream.GetStream();

                // First bytes from usque: the TLS ClientHello. Give up if it never arrives.
                var buffer = new byte[65536];
                var first = new MemoryStream();
                var deadline = Task.Delay(12000);
                while (true)
                {
                    var read = fromClient.ReadAsync(buffer, 0, buffer.Length);
                    if (await Task.WhenAny(read, deadline) != read) { Observe(read); return; }
                    int n = await read;
                    if (n <= 0) return;
                    if (!fragment) { await toServer.WriteAsync(buffer, 0, n); break; }
                    first.Write(buffer, 0, n);
                    var parts = ClientHello.Split(first.GetBuffer(), (int)first.Length);
                    if (parts == null) continue; // wait for the rest of the record
                    for (int i = 0; i < parts.Count; i++)
                    {
                        await toServer.WriteAsync(parts[i], 0, parts[i].Length);
                        await toServer.FlushAsync();
                        if (parts.Count > 1 && i < parts.Count - 1) await Task.Delay(Jitter());
                    }
                    break;
                }

                var up = Pump(fromClient, toServer);
                var down = Pump(toServer, fromClient);
                await Task.WhenAny(up, down);
            }
            catch { }
            finally
            {
                Close(client);
                Close(upstream);
            }
        }

        static async Task Pump(NetworkStream from, NetworkStream to)
        {
            var buffer = new byte[65536];
            try
            {
                while (true)
                {
                    int n = await from.ReadAsync(buffer, 0, buffer.Length);
                    if (n <= 0) break;
                    await to.WriteAsync(buffer, 0, n);
                }
            }
            catch { }
        }

        static void Observe(Task t) => t.ContinueWith(x => { var _ = x.Exception; }, TaskContinuationOptions.OnlyOnFaulted);
    }
}
