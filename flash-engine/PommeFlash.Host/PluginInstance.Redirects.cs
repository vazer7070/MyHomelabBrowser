namespace PommeFlash.Host
{
    sealed partial class PluginInstance
    {
        // ---------------------------------------------------------------
        // Redirections soumises au module (NPP_URLRedirectNotify)
        // ---------------------------------------------------------------

        /// <summary>Attente maximale de la réponse du module à une redirection.</summary>
        static readonly TimeSpan RedirectTimeout = TimeSpan.FromSeconds(20);

        readonly List<(nint NotifyData, TaskCompletionSource<bool> Answer)> _redirects = new();

        /// <summary>
        /// Redirection d'un chargement notifié, soumise au module depuis le fil du téléchargement
        /// (NPP_URLRedirectNotify, sur le fil du module) ; il répond par NPN_URLRedirectResponse,
        /// comme dans Firefox. Vrai s'il accepte de la suivre ; faux s'il refuse, se ferme ou ne
        /// répond pas à temps.
        /// </summary>
        internal async Task<bool> ApproveRedirectAsync(Uri next, int status, nint notifyData, CancellationToken cancellation)
        {
            var answer = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            UiThread.Post(() =>
            {
                if (answer.Task.IsCompleted)
                    return;
                if (!IsAlive || cancellation.IsCancellationRequested)
                {
                    answer.TrySetResult(false);
                    return;
                }
                lock (_redirects)
                    _redirects.Add((notifyData, answer));
                NotifyRedirect(next, status, notifyData);
            });

            try
            {
                Task finished = await Task.WhenAny(answer.Task, Task.Delay(RedirectTimeout, cancellation)).ConfigureAwait(false);
                if (finished != answer.Task && answer.TrySetResult(false))
                {
                    cancellation.ThrowIfCancellationRequested();
                    HostChannel.Log($"Le module n'a pas répondu à la redirection vers {next.GetLeftPart(UriPartial.Path)} : non suivie.");
                }
                return await answer.Task.ConfigureAwait(false);
            }
            finally
            {
                lock (_redirects)
                    _redirects.RemoveAll(r => r.Answer == answer);
            }
        }

        /// <summary>
        /// NPN_URLRedirectResponse : réponse du module pour les chargements de ce notifyData qui
        /// attendent (0 : tous, à la fermeture).
        /// </summary>
        public void AnswerRedirects(nint notifyData, bool allow)
        {
            List<TaskCompletionSource<bool>> answers;
            lock (_redirects)
            {
                answers = _redirects.Where(r => notifyData == 0 || r.NotifyData == notifyData).Select(r => r.Answer).ToList();
                _redirects.RemoveAll(r => notifyData == 0 || r.NotifyData == notifyData);
            }
            foreach (TaskCompletionSource<bool> answer in answers)
                answer.TrySetResult(allow);
        }
    }
}
