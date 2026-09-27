using Microsoft.Web.WebView2.Wpf;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace MyHomelabBrowser.classes.Profiles.Credentials
{
    public static class CredentialInjector
    {
        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            PropertyNameCaseInsensitive = true
        };

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

                    var message = JsonSerializer.Deserialize<WebMessage>(
                        eventArgs.WebMessageAsJson,
                        JsonOptions);

                    if (message?.Type != "cred_submit"
                        || string.IsNullOrEmpty(message.Password))
                    {
                        return;
                    }

                    // L'origine déclarée par JavaScript n'est jamais la source de vérité.
                    // Elle sert uniquement à détecter une incohérence supplémentaire.
                    if (!string.IsNullOrWhiteSpace(message.Origin)
                        && (!CredentialOrigin.TryCreateTrusted(message.Origin, out var declaredOrigin)
                            || !string.Equals(declaredOrigin, sourceOrigin, StringComparison.OrdinalIgnoreCase)))
                    {
                        return;
                    }

                    await onCredentialCaptured(new CredentialCandidate
                    {
                        Origin = sourceOrigin,
                        Username = message.Username?.Trim() ?? string.Empty,
                        Password = message.Password,
                        FormAction = message.FormAction
                    });
                }
                catch
                {
                    // Une page web ne doit jamais pouvoir faire tomber le navigateur.
                }
            };

            var script = GetCaptureScript();
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

                    var usernameJson = JsonSerializer.Serialize(credential.Username);
                    var passwordJson = JsonSerializer.Serialize(credential.Password);

                    var fillScript = $$"""
                    (() => {
                      let username = {{usernameJson}};
                      let password = {{passwordJson}};

                      function isUsable(element) {
                        if (!element || element.disabled || element.readOnly) return false;
                        const style = window.getComputedStyle(element);
                        return style.display !== 'none' && style.visibility !== 'hidden';
                      }

                      function setValue(element, value) {
                        if (!isUsable(element) || !value) return;
                        element.focus();
                        const descriptor = Object.getOwnPropertyDescriptor(
                          HTMLInputElement.prototype,
                          'value'
                        );
                        descriptor?.set?.call(element, value);
                        element.dispatchEvent(new Event('input', { bubbles: true }));
                        element.dispatchEvent(new Event('change', { bubbles: true }));
                      }

                      const passwordFields = Array.from(
                        document.querySelectorAll('input[type="password"]')
                      ).filter(isUsable);

                      if (passwordFields.length === 0) return;
                      if (passwordFields.some(x =>
                            (x.autocomplete || '').toLowerCase() === 'new-password')) return;

                      const passwordField =
                        passwordFields.find(x =>
                          (x.autocomplete || '').toLowerCase() === 'current-password') ||
                        (passwordFields.length === 1 ? passwordFields[0] : null);

                      if (!passwordField) return;

                      const form = passwordField.form || document;
                      const userField =
                        form.querySelector('input[autocomplete="username"]') ||
                        form.querySelector('input[type="email"]') ||
                        form.querySelector('input[name*="user" i], input[id*="user" i]') ||
                        form.querySelector('input[name*="email" i], input[id*="email" i]') ||
                        form.querySelector('input[type="text"]:not([name*="search" i]):not([id*="search" i])');

                      if (userField && !userField.value.trim())
                        setValue(userField, username);

                      if (!passwordField.value)
                        setValue(passwordField, password);

                      username = '';
                      password = '';
                    })();
                    """;

                    await web.ExecuteScriptAsync(fillScript);
                }
                catch
                {
                    // Le remplissage est opportuniste et ne bloque jamais la navigation.
                }
            };
        }

        private static string GetCaptureScript()
        {
            return """
            (() => {
              if (window.__pommeCredentialCaptureV2) return;
              window.__pommeCredentialCaptureV2 = true;

              const recentSubmissions = new Map();

              function isUsable(element) {
                if (!element || element.disabled || element.readOnly) return false;
                const style = window.getComputedStyle(element);
                return style.display !== 'none' && style.visibility !== 'hidden';
              }

              function findUsernameField(root) {
                return root.querySelector('input[autocomplete="username"]') ||
                       root.querySelector('input[type="email"]') ||
                       root.querySelector('input[name*="user" i], input[id*="user" i]') ||
                       root.querySelector('input[name*="email" i], input[id*="email" i]') ||
                       root.querySelector('input[type="text"]:not([name*="search" i]):not([id*="search" i])');
              }

              function containsOneTimeCode(root) {
                return !!root.querySelector(
                  'input[autocomplete="one-time-code"], ' +
                  'input[name*="otp" i], input[id*="otp" i], ' +
                  'input[name*="2fa" i], input[id*="2fa" i]'
                );
              }

              function createFormIdentity(form, username) {
                const action = form instanceof HTMLFormElement ? (form.action || '') : '';
                const id = form instanceof Element ? (form.id || form.getAttribute('name') || '') : '';
                return location.origin + '|' + action + '|' + id + '|' + username;
              }

              function capture(formOrDocument) {
                try {
                  const root = formOrDocument || document;
                  if (containsOneTimeCode(root)) return;

                  const passwordFields = Array.from(
                    root.querySelectorAll('input[type="password"]')
                  ).filter(isUsable);

                  if (passwordFields.length === 0) return;
                  if (passwordFields.some(field =>
                        (field.autocomplete || '').toLowerCase() === 'new-password')) return;

                  const passwordField =
                    passwordFields.find(field =>
                      (field.autocomplete || '').toLowerCase() === 'current-password') ||
                    (passwordFields.length === 1 ? passwordFields[0] : null);

                  // Plusieurs champs sans indication fiable correspondent généralement
                  // à une inscription ou un changement de mot de passe.
                  if (!passwordField) return;

                  let password = passwordField.value || '';
                  if (!password) return;

                  const usernameField = findUsernameField(root);
                  const username = (usernameField?.value || '').trim();
                  if (!username) {
                    password = '';
                    return;
                  }

                  const identity = createFormIdentity(
                    root instanceof HTMLFormElement ? root : (passwordField.form || document),
                    username
                  );
                  const now = Date.now();
                  const previous = recentSubmissions.get(identity) || 0;
                  if (now - previous < 4000) {
                    password = '';
                    return;
                  }

                  recentSubmissions.set(identity, now);
                  setTimeout(() => recentSubmissions.delete(identity), 5000);

                  const form = root instanceof HTMLFormElement
                    ? root
                    : passwordField.form;

                  window.chrome?.webview?.postMessage({
                    type: 'cred_submit',
                    origin: location.origin,
                    username,
                    password,
                    formAction: form?.action || null
                  });

                  password = '';
                } catch { }
              }

              document.addEventListener('submit', event => {
                if (event.target instanceof HTMLFormElement)
                  capture(event.target);
              }, true);

              // Les applications monopage n'émettent pas toujours submit.
              document.addEventListener('click', event => {
                const button = event.target?.closest?.(
                  'button[type="submit"], input[type="submit"], button:not([type])'
                );
                if (!button) return;

                const form = button.closest('form');
                if (form)
                  queueMicrotask(() => capture(form));
              }, true);
            })();
            """;
        }

        private sealed class WebMessage
        {
            [JsonPropertyName("type")]
            public string? Type { get; set; }

            [JsonPropertyName("origin")]
            public string? Origin { get; set; }

            [JsonPropertyName("username")]
            public string? Username { get; set; }

            [JsonPropertyName("password")]
            public string? Password { get; set; }

            [JsonPropertyName("formAction")]
            public string? FormAction { get; set; }
        }
    }
}
