namespace ThrumHapticsSettings
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.IO.Pipes;
    using System.Text;

    /// <summary>
    /// Talks to the plugin over its local named pipe.
    /// </summary>
    /// <remarks>
    /// The plugin remains the owner of the settings: only it can reach the SDK's
    /// setting store, and only it can raise haptic events. This application is a
    /// thin client that reads values, writes changes, and asks for previews.
    ///
    /// Deliberately a named pipe rather than a socket - local IPC that never
    /// touches the network stack, so neither process opens a listening port.
    /// </remarks>
    internal sealed class SettingsClient : IDisposable
    {
        private readonly NamedPipeClientStream _pipe;
        private readonly StreamReader _reader;
        private readonly StreamWriter _writer;

        /// <summary>
        /// Serialises pipe access.
        /// </summary>
        /// <remarks>
        /// The UI thread issues Set and Play while a background watcher issues
        /// Ping. A request/response protocol on one duplex stream cannot tolerate
        /// two callers interleaving - one would read the other's reply.
        /// </remarks>
        private readonly Object _gate = new();

        private SettingsClient(NamedPipeClientStream pipe)
        {
            this._pipe = pipe;
            this._reader = new StreamReader(pipe, Encoding.UTF8, false, 1024, leaveOpen: true);
            this._writer = new StreamWriter(pipe, new UTF8Encoding(false), 1024, leaveOpen: true)
            {
                AutoFlush = true,
            };
        }

        /// <summary>Connects to the plugin, or returns null if it is not running.</summary>
        public static SettingsClient TryConnect(String pipeName, Int32 timeoutMs = 3000)
        {
            try
            {
                var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut);
                pipe.Connect(timeoutMs);

                return new SettingsClient(pipe);
            }
            catch (Exception)
            {
                // The plugin may be reloading, or Options+ may not be running.
                return null;
            }
        }

        /// <summary>Current value of every event the plugin knows about.</summary>
        public Dictionary<String, (Boolean Enabled, String Waveform)> GetAll()
        {
            var result = new Dictionary<String, (Boolean, String)>(StringComparer.OrdinalIgnoreCase);

            lock (this._gate)
            {
                this._writer.WriteLine("GET");

                while (true)
                {
                    var line = this._reader.ReadLine();

                    // "." terminates the list; null means the plugin went away
                    // mid-read, which is survivable - we just show what we have.
                    if (line == null || line == ".")
                    {
                        break;
                    }

                    var parts = line.Split('|');

                    if (parts.Length >= 3)
                    {
                        result[parts[0]] = (Boolean.TryParse(parts[1], out var on) && on, parts[2]);
                    }
                }
            }

            return result;
        }

        public void Set(String eventId, Boolean enabled, String waveform)
        {
            lock (this._gate)
            {
                this._writer.WriteLine($"SET|{eventId}|{enabled}|{waveform}");
                this._reader.ReadLine();
            }
        }

        /// <summary>Asks the plugin to play a waveform, for live preview.</summary>
        public void Play(String waveform)
        {
            lock (this._gate)
            {
                this._writer.WriteLine($"PLAY|{waveform}");
                this._reader.ReadLine();
            }
        }

        /// <summary>
        /// Returns false once the plugin is no longer reachable.
        /// </summary>
        /// <remarks>
        /// This window must not outlive the plugin. It runs from an executable
        /// inside the plugin's own folder, so while it is alive Windows cannot
        /// delete that folder - which would leave Logi Options+ unable to fully
        /// uninstall the plugin, and a stale folder behind on disk.
        ///
        /// IsConnected alone is not enough: it does not go false until an I/O
        /// operation actually fails, so an explicit round trip is needed.
        /// </remarks>
        public Boolean Ping()
        {
            lock (this._gate)
            {
                try
                {
                    if (!this._pipe.IsConnected)
                    {
                        return false;
                    }

                    this._writer.WriteLine("PING");

                    // A null reply means the far end closed the pipe.
                    return this._reader.ReadLine() != null;
                }
                catch (Exception)
                {
                    return false;
                }
            }
        }

        public void Dispose()
        {
            try
            {
                if (this._pipe.IsConnected)
                {
                    this._writer.WriteLine("BYE");
                }
            }
            catch (Exception)
            {
                // Already gone; nothing useful to do while shutting down.
            }
            finally
            {
                this._reader.Dispose();
                this._writer.Dispose();
                this._pipe.Dispose();
            }
        }
    }
}
