using System.Text.Json;
using System.Text.Json.Serialization;

namespace MyHomelabBrowser.classes.Profiles.Credentials
{
    /// <summary>
    /// Scripts du coffre, communs aux deux éditions : détection des identifiants envoyés par un
    /// formulaire de connexion, et remplissage des champs. Seul l'envoi du message au navigateur
    /// diffère (WebView2 sous Windows, WebKitGTK sous Linux).
    /// </summary>
    public static class CredentialScripts
    {
        /// <summary>Envoi du message de capture depuis WebView2.</summary>
        public const string WebView2Post = "window.chrome?.webview?.postMessage(message);";

        /// <summary>
        /// Envoi du message de capture depuis WebKitGTK : le script tourne dans un monde isolé,
        /// seul à voir ce gestionnaire (une page ne peut pas envoyer de faux identifiants).
        /// </summary>
        public const string WebKitPost = "window.webkit?.messageHandlers?.pommeCredentials?.postMessage(JSON.stringify(message));";

        static readonly JsonSerializerOptions JsonOptions = new()
        {
            PropertyNameCaseInsensitive = true
        };

        /// <summary>Script de capture, injecté dans chaque page. <paramref name="post"/> envoie la variable « message ».</summary>
        public static string Capture(string post) => CaptureTemplate.Replace("/*POST*/", post, StringComparison.Ordinal);

        /// <summary>
        /// Remplit l'identifiant et le mot de passe d'un formulaire de connexion, ou le code de double
        /// authentification (<paramref name="otp"/>) sur une page qui ne demande qu'un code. Les champs
        /// déjà saisis ne sont pas écrasés. Renvoie 'password', 'otp' ou 'none'.
        /// Avec <paramref name="expectedOrigin"/>, rien n'est rempli si la page a changé d'origine entre-temps.
        /// </summary>
        public static string Fill(string username, string password, string? otp = null, string? expectedOrigin = null)
        {
            var usernameJson = JsonSerializer.Serialize(username);
            var passwordJson = JsonSerializer.Serialize(password);
            var otpJson = JsonSerializer.Serialize(otp);
            var originJson = JsonSerializer.Serialize(expectedOrigin);

            return $$"""
            (() => {
              const expectedOrigin = {{originJson}};
              if (expectedOrigin && location.origin !== expectedOrigin) return 'none';

              let username = {{usernameJson}};
              let password = {{passwordJson}};
              let otp = {{otpJson}};

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

              // Page de double authentification : pas de mot de passe, un champ de code.
              if (passwordFields.length === 0) {
                if (!otp) return 'none';
                const hint = /(otp|totp|2fa|mfa|two.?factor|one.?time|verif|token|code)/i;
                const otpField =
                  Array.from(document.querySelectorAll('input[autocomplete="one-time-code"]')).find(isUsable) ||
                  Array.from(document.querySelectorAll('input')).find(el =>
                    isUsable(el) &&
                    ['text', 'tel', 'number', ''].includes((el.getAttribute('type') || '').toLowerCase()) &&
                    hint.test([el.name, el.id, el.placeholder, el.getAttribute('aria-label')].join(' ')));
                if (!otpField || otpField.value) return 'none';
                setValue(otpField, otp);
                otp = '';
                return 'otp';
              }

              if (passwordFields.some(field =>
                    (field.autocomplete || '').toLowerCase() === 'new-password')) return 'none';

              const passwordField =
                passwordFields.find(field =>
                  (field.autocomplete || '').toLowerCase() === 'current-password') ||
                (passwordFields.length === 1 ? passwordFields[0] : null);

              if (!passwordField) return 'none';

              const root = passwordField.form || document;
              const usernameField =
                root.querySelector('input[autocomplete="username"]') ||
                root.querySelector('input[type="email"]') ||
                root.querySelector('input[name*="user" i], input[id*="user" i]') ||
                root.querySelector('input[name*="email" i], input[id*="email" i]') ||
                root.querySelector('input[type="text"]:not([name*="search" i]):not([id*="search" i])');

              if (usernameField && !usernameField.value.trim())
                setValue(usernameField, username);

              if (!passwordField.value)
                setValue(passwordField, password);

              username = '';
              password = '';
              otp = '';
              return 'password';
            })();
            """;
        }

        /// <summary>
        /// Lit un message du script de capture. <paramref name="sourceOrigin"/> est l'origine de la page
        /// connue du navigateur : c'est la seule source de vérité. L'origine déclarée par le script ne
        /// sert qu'à écarter un message arrivé après un changement de page.
        /// </summary>
        public static bool TryParseSubmission(string? json, string sourceOrigin, out CredentialCandidate? candidate)
        {
            candidate = null;
            if (string.IsNullOrWhiteSpace(json) || string.IsNullOrWhiteSpace(sourceOrigin))
                return false;

            WebMessage? message;
            try
            {
                message = JsonSerializer.Deserialize<WebMessage>(json, JsonOptions);
            }
            catch (JsonException)
            {
                return false;
            }

            if (message?.Type != "cred_submit" || string.IsNullOrEmpty(message.Password))
                return false;

            if (!string.IsNullOrWhiteSpace(message.Origin)
                && (!CredentialOrigin.TryCreateTrusted(message.Origin, out var declaredOrigin)
                    || !string.Equals(declaredOrigin, sourceOrigin, StringComparison.OrdinalIgnoreCase)))
            {
                return false;
            }

            candidate = new CredentialCandidate
            {
                Origin = sourceOrigin,
                Username = message.Username?.Trim() ?? string.Empty,
                Password = message.Password,
                FormAction = message.FormAction
            };
            return true;
        }

        const string CaptureTemplate = """
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

                  const message = {
                    type: 'cred_submit',
                    origin: location.origin,
                    username,
                    password,
                    formAction: form?.action || null
                  };
                  /*POST*/

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

        sealed class WebMessage
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
