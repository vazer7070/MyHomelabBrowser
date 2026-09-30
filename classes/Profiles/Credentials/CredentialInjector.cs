using Microsoft.Web.WebView2.Wpf;

namespace MyHomelabBrowser.classes.Profiles.Credentials
{
    public static class CredentialInjector
    {
        public static async Task Attach(
            WebView2 web,
            Func<bool> isPrivateTab,
            Func<bool> isVaultUnlocked,
            Func<Uri, CredentialEntry?> getCredentialForOrigin,
            Func<CredentialCandidate, Task> onCredentialCaptured)
        {
            ArgumentNullException.ThrowIfNull(web);
            ArgumentNullException.ThrowIfNull(isPrivateTab);
            ArgumentNullException.ThrowIfNull(isVaultUnlocked);
            ArgumentNullException.ThrowIfNull(getCredentialForOrigin);
            ArgumentNullException.ThrowIfNull(onCredentialCaptured);

            if (web.CoreWebView2 == null || isPrivateTab())
                return;

            web.CoreWebView2.WebMessageReceived += async (_, eventArgs) =>
            {
                try
                {
                    if (isPrivateTab())
                        return;

                    if (!Uri.TryCreate(eventArgs.Source, UriKind.Absolute, out var messageSource)
                        || !CredentialOrigin.TryCreateTrusted(messageSource, out var sourceOrigin))
                    {
                        return;
                    }

                    var currentPage = web.Source;
                    if (!CredentialOrigin.TryCreateTrusted(currentPage, out var currentOrigin)
                        || !string.Equals(sourceOrigin, currentOrigin, StringComparison.OrdinalIgnoreCase))
                    {
                        return;
                    }

                    if (CredentialScripts.TryParseSubmission(eventArgs.WebMessageAsJson, sourceOrigin, out var candidate))
                        await onCredentialCaptured(candidate!);
                }
                catch
                {
                    // Une page web ne doit jamais pouvoir faire tomber le navigateur.
                }
            };

            var script = CredentialScripts.Capture(CredentialScripts.WebView2Post);
            await web.CoreWebView2.AddScriptToExecuteOnDocumentCreatedAsync(script);

            try
            {
                await web.ExecuteScriptAsync(script);
            }
            catch
            {
                // Certaines pages internes ou en cours de destruction refusent l'injection.
            }

            web.NavigationCompleted += async (_, _) =>
            {
                try
                {
                    if (isPrivateTab() || !isVaultUnlocked())
                        return;

                    var currentUri = web.Source;
                    if (!CredentialOrigin.TryCreateTrusted(currentUri, out _))
                        return;

                    var credential = getCredentialForOrigin(currentUri);
                    if (credential == null)
                        return;

                    await web.ExecuteScriptAsync(CredentialScripts.Fill(credential.Username, credential.Password));
                }
                catch
                {
                    // Le remplissage est opportuniste et ne bloque jamais la navigation.
                }
            };
        }
    }
}
