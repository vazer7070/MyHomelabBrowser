using System.Net.Http.Headers;
using System.Text;
using PommeFlash.Host.Native;

namespace PommeFlash.Host
{
    /// <summary>
    /// Contenu donné au module (NPStream) : téléchargé sur un autre fil, puis remis par morceaux
    /// sur le fil du module, au rythme qu'il accepte (NPP_WriteReady / NPP_Write). À la fin :
    /// NPP_DestroyStream, puis NPP_URLNotify si le module l'a demandé.
    /// </summary>
    sealed class PluginStream
    {
        const int ChunkSize = 64 * 1024;

        readonly PluginInstance _owner;
        readonly string _requestedUrl;
        readonly Uri _uri;
        readonly PostData? _post;
        readonly bool _notify;
        readonly nint _notifyData;
        readonly Queue<byte[]> _chunks = new();
        readonly CancellationTokenSource _cancel = new();
        unsafe NPStream* _native;
        ushort _type = Np.StreamNormal;
        int _chunkOffset;
        int _offset;
        bool _complete;
        bool _stopped;
        bool _closed;
        bool _retryScheduled;
        string? _filePath;
        FileStream? _file;

        public PluginStream(PluginInstance owner, string requestedUrl, Uri uri, PostData? post, bool notify, nint notifyData)
        {
            _owner = owner;
            _requestedUrl = requestedUrl;
            _uri = uri;
            _post = post;
            _notify = notify;
            _notifyData = notifyData;
        }

        public unsafe NPStream* Native => _native;

        /// <summary>Réponse reçue : adresse finale, type, taille, en-têtes.</summary>
        sealed record Response(string Url, string MimeType, long Length, DateTimeOffset? LastModified, string Headers);

        public void Start() => _ = Task.Run(DownloadAsync);

        async Task DownloadAsync()
        {
            try
            {
                if (_uri.IsFile)
                    await ReadFileAsync().ConfigureAwait(false);
                else if (_uri.Scheme == "data")
                    ReadDataUrl();
                else
                    await ReadHttpAsync().ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                UiThread.Post(() =>
                {
                    HostChannel.Log($"Échec du chargement de {_uri.GetLeftPart(UriPartial.Path)} : {ex.Message}");
                    Close(Np.ReasonNetworkError);
                });
            }
        }

        async Task ReadHttpAsync()
        {
            using var request = new HttpRequestMessage(_post != null ? HttpMethod.Post : HttpMethod.Get, _uri);
            if (_owner.Options.Page.Scheme is "http" or "https")
                request.Headers.Referrer = _owner.Options.Page;
            if (_post != null)
            {
                request.Content = new ByteArrayContent(_post.Body);
                foreach ((string name, string value) in _post.Headers)
                {
                    if (name.Equals("Content-Length", StringComparison.OrdinalIgnoreCase))
                        continue;
                    if (!request.Headers.TryAddWithoutValidation(name, value))
                        request.Content.Headers.TryAddWithoutValidation(name, value);
                }
            }

            using HttpResponseMessage response = await _owner.Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, _cancel.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                UiThread.Post(() =>
                {
                    HostChannel.Log($"HTTP {(int)response.StatusCode} : {_uri.GetLeftPart(UriPartial.Path)}");
                    Close(Np.ReasonNetworkError);
                });
                return;
            }

            HostChannel.Trace("response:" + _uri.GetLeftPart(UriPartial.Path),
                $"HTTP {(int)response.StatusCode} {response.Content.Headers.ContentType?.MediaType ?? "sans type"}" +
                $" ({response.Content.Headers.ContentLength?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "?"} octets) : {_uri.GetLeftPart(UriPartial.Path)}");
            var headers = new StringBuilder();
            headers.Append("HTTP/").Append(response.Version.ToString(2)).Append(' ')
                .Append((int)response.StatusCode).Append(' ').Append(response.ReasonPhrase).Append('\n');
            foreach (KeyValuePair<string, IEnumerable<string>> header in response.Headers.Concat(response.Content.Headers))
                headers.Append(header.Key).Append(": ").Append(string.Join(", ", header.Value)).Append('\n');

            var info = new Response(
                (response.RequestMessage?.RequestUri ?? _uri).AbsoluteUri,
                response.Content.Headers.ContentType?.MediaType ?? "application/octet-stream",
                response.Content.Headers.ContentLength ?? 0,
                response.Content.Headers.LastModified,
                headers.ToString());
            UiThread.Post(() => Open(info));

            await using Stream body = await response.Content.ReadAsStreamAsync(_cancel.Token).ConfigureAwait(false);
            await PumpBodyAsync(body).ConfigureAwait(false);
        }

        async Task ReadFileAsync()
        {
            var file = new FileInfo(_uri.LocalPath);
            string mime = file.Extension.Equals(".swf", StringComparison.OrdinalIgnoreCase) ? HostOptions.FlashMimeType : "application/octet-stream";
            var info = new Response(_uri.AbsoluteUri, mime, file.Length, file.LastWriteTimeUtc, string.Empty);
            UiThread.Post(() => Open(info));
            await using FileStream stream = file.OpenRead();
            await PumpBodyAsync(stream).ConfigureAwait(false);
        }

        void ReadDataUrl()
        {
            string text = _uri.OriginalString;
            int comma = text.IndexOf(',');
            if (comma < 0)
                throw new FormatException("Adresse data: invalide.");
            string meta = text[5..comma];
            string payload = text[(comma + 1)..];
            byte[] data = meta.EndsWith(";base64", StringComparison.OrdinalIgnoreCase)
                ? Convert.FromBase64String(Uri.UnescapeDataString(payload))
                : Encoding.UTF8.GetBytes(Uri.UnescapeDataString(payload));
            string mime = meta.Split(';')[0] is { Length: > 0 } type ? type : "text/plain";
            var info = new Response(_requestedUrl, mime, data.Length, null, string.Empty);
            UiThread.Post(() =>
            {
                Open(info);
                Enqueue(data);
                Complete();
            });
        }

        async Task PumpBodyAsync(Stream body)
        {
            var buffer = new byte[ChunkSize];
            int read;
            while ((read = await body.ReadAsync(buffer, _cancel.Token).ConfigureAwait(false)) > 0)
            {
                byte[] chunk = buffer.AsSpan(0, read).ToArray();
                UiThread.Post(() => Enqueue(chunk));
            }
            UiThread.Post(Complete);
        }

        // ---------------------------------------------------------------
        // Fil du module
        // ---------------------------------------------------------------

        unsafe void Open(Response info)
        {
            if (_closed || !_owner.IsAlive)
                return;

            _native = (NPStream*)NpMemory.AllocZeroed((nuint)sizeof(NPStream));
            _native->url = NpMemory.Utf8(info.Url);
            _native->end = info.Length is > 0 and <= uint.MaxValue ? (uint)info.Length : 0;
            _native->lastmodified = info.LastModified is { } modified ? (uint)Math.Max(0, modified.ToUnixTimeSeconds()) : 0;
            _native->notifyData = _notifyData;
            _native->headers = info.Headers.Length > 0 ? NpMemory.Utf8(info.Headers) : 0;

            nint mime = NpMemory.Utf8(info.MimeType);
            ushort type = Np.StreamNormal;
            short error = _owner.Library.NewStream(_owner.Npp, mime, _native, false, &type);
            NpMemory.Free(mime);
            if (error != Np.NoError)
            {
                // Refusé par le module : pas de NPP_DestroyStream pour un flux jamais ouvert.
                FreeNative();
                Close(Np.ReasonNetworkError);
                return;
            }

            _type = type;
            if (_type is Np.StreamAsFile or Np.StreamAsFileOnly)
            {
                _filePath = Path.Combine(Path.GetTempPath(), "pommeflash-" + Guid.NewGuid().ToString("N") + Path.GetExtension(_uri.AbsolutePath));
                _file = new FileStream(_filePath, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
            }
            Pump();
        }

        void Enqueue(byte[] chunk)
        {
            if (_closed)
                return;
            _chunks.Enqueue(chunk);
            Pump();
        }

        void Complete()
        {
            if (_closed)
                return;
            _complete = true;
            Pump();
        }

        /// <summary>Donne au module ce qu'il accepte ; réessaie un peu plus tard s'il n'est pas prêt.</summary>
        unsafe void Pump()
        {
            if (_closed || _stopped || _native == null || !_owner.IsAlive)
                return;

            while (_chunks.Count > 0)
            {
                byte[] chunk = _chunks.Peek();
                int remaining = chunk.Length - _chunkOffset;

                if (_type == Np.StreamAsFileOnly)
                {
                    _file!.Write(chunk, _chunkOffset, remaining);
                    _offset += remaining;
                    _chunks.Dequeue();
                    _chunkOffset = 0;
                    continue;
                }

                int ready = _owner.Library.WriteReady(_owner.Npp, _native);
                if (_closed || _stopped)
                    return;
                if (ready <= 0)
                {
                    RetryLater();
                    return;
                }

                int length = Math.Min(ready, remaining);
                int written;
                fixed (byte* data = &chunk[_chunkOffset])
                    written = _owner.Library.Write(_owner.Npp, _native, _offset, length, data);
                if (_closed || _stopped)
                    return;
                if (written < 0)
                {
                    Close(Np.ReasonNetworkError);
                    return;
                }
                if (written == 0)
                {
                    RetryLater();
                    return;
                }

                written = Math.Min(written, length);
                _file?.Write(chunk, _chunkOffset, written);
                _offset += written;
                _chunkOffset += written;
                if (_chunkOffset >= chunk.Length)
                {
                    _chunks.Dequeue();
                    _chunkOffset = 0;
                }
            }

            if (!_complete)
                return;

            if (_file != null)
            {
                _file.Dispose();
                _file = null;
                nint path = NpMemory.Utf8(_filePath!);
                _owner.Library.StreamAsFile(_owner.Npp, _native, path);
                NpMemory.Free(path);
                if (_closed || _stopped)
                    return;
            }
            Close(Np.ReasonDone);
        }

        void RetryLater()
        {
            if (_retryScheduled)
                return;
            _retryScheduled = true;
            UiThread.Delay(15, () =>
            {
                _retryScheduled = false;
                Pump();
            });
        }

        /// <summary>Le module a abandonné le flux : plus rien ne lui est remis.</summary>
        public void Stop()
        {
            _stopped = true;
            _cancel.Cancel();
        }

        /// <summary>Fin du flux : NPP_DestroyStream (s'il a été ouvert), puis NPP_URLNotify (si demandé).</summary>
        public unsafe void Close(short reason)
        {
            if (_closed)
                return;
            _closed = true;
            _cancel.Cancel();
            _chunks.Clear();

            if (_owner.IsAlive)
            {
                if (_native != null)
                    _owner.Library.DestroyStream(_owner.Npp, _native, reason);
                if (_notify)
                {
                    nint url = NpMemory.Utf8(_requestedUrl);
                    _owner.Library.UrlNotify(_owner.Npp, url, reason, _notifyData);
                    NpMemory.Free(url);
                }
            }

            FreeNative();
            _file?.Dispose();
            _file = null;
            if (_filePath != null)
            {
                try
                {
                    File.Delete(_filePath);
                }
                catch (IOException)
                {
                }
            }
            _owner.Forget(this);
        }

        unsafe void FreeNative()
        {
            if (_native == null)
                return;
            NpMemory.Free(_native->url);
            NpMemory.Free(_native->headers);
            NpMemory.Free((nint)_native);
            _native = null;
        }

        public override string ToString() => _requestedUrl;
    }
}
