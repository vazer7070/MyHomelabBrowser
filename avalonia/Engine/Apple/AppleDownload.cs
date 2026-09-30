using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Avalonia.Threading;
using MyHomelabBrowser.classes;
using PommeBrowser.Linux.Core;
using static PommeBrowser.Engine.Apple.ObjC;

namespace PommeBrowser.Engine.Apple
{
    /// <summary>
    /// Téléchargements de WKWebView (WKDownload, macOS 11.3) : enregistrés sans question dans le
    /// dossier des téléchargements, jamais par-dessus un fichier existant, suivis dans la liste.
    /// </summary>
    [SupportedOSPlatform("macos")]
    sealed class AppleDownload
    {
        static readonly Dictionary<nint, AppleDownload> Active = new();

        readonly nint _download;
        readonly bool _isPrivate;
        EngineDownload? _public;
        DispatcherTimer? _progress;

        AppleDownload(nint download, bool isPrivate)
        {
            _download = download;
            _isPrivate = isPrivate;
        }

        public static void Track(nint download, nint downloadDelegate, bool isPrivate)
        {
            if (download == 0 || Active.ContainsKey(download))
                return;
            Retain(download);
            Active[download] = new AppleDownload(download, isPrivate);
            Send(download, Sel("setDelegate:"), downloadDelegate);
        }

        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
        internal static void DecideDestination(nint self, nint cmd, nint download, nint response, nint suggestedName, nint completion)
        {
            nint destination = 0;
            try
            {
                if (!Active.TryGetValue(download, out AppleDownload? state))
                    return;

                string directory = EngineHost.Settings.DownloadDirectory;
                Directory.CreateDirectory(directory);
                string path = DownloadNames.UniquePath(directory, ToManaged(suggestedName));
                destination = FileUrl(path);

                string uri = UrlString(Send(Send(download, Sel("originalRequest")), Sel("URL"))) ?? string.Empty;
                long expected = response != 0 ? GetLong(response, Sel("expectedContentLength")) : -1;
                var info = new EngineDownload(uri, Path.GetFileName(path), () => Send(download, Sel("cancel:"), 0))
                {
                    Destination = path,
                    IsPrivate = state._isPrivate,
                    TotalBytes = Math.Max(0, expected)
                };
                state._public = info;
                state._progress = new DispatcherTimer(TimeSpan.FromMilliseconds(400), DispatcherPriority.Background, (_, _) => state.UpdateProgress());
                state._progress.Start();
                EngineHost.RaiseDownloadStarted(info);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
            {
                RuntimeLogBuffer.Append("[Téléchargements] " + ex.Message);
            }
            finally
            {
                // Sans destination (dossier inaccessible…), WebKit annule le téléchargement.
                CallBlock(completion, destination);
            }
        }

        void UpdateProgress()
        {
            if (_public is not { } info)
                return;
            nint progress = Send(_download, Sel("progress"));
            if (progress == 0)
                return;
            info.ReceivedBytes = GetLong(progress, Sel("completedUnitCount"));
            long total = GetLong(progress, Sel("totalUnitCount"));
            if (total > 0)
                info.TotalBytes = total;
            info.RaiseChanged();
        }

        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
        internal static void Finished(nint self, nint cmd, nint download) => End(download, null, cancelled: false);

        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
        internal static void Failed(nint self, nint cmd, nint download, nint error, nint resumeData)
        {
            const long Cancelled = -999;
            bool cancelled = error != 0 && GetLong(error, Sel("code")) == Cancelled;
            string? message = error != 0 && !cancelled ? ToManaged(Send(error, Sel("localizedDescription"))) : null;
            End(download, message, cancelled);
        }

        static void End(nint download, string? error, bool cancelled)
        {
            if (!Active.Remove(download, out AppleDownload? state))
                return;
            state._progress?.Stop();
            if (state._public is { } info)
            {
                state.UpdateProgress();
                info.IsCancelled = cancelled;
                info.Error = error;
                if (error == null && !cancelled)
                    info.TotalBytes = info.ReceivedBytes;
                info.IsFinished = true;
                info.RaiseChanged();
            }
            Release(download);
        }
    }
}
