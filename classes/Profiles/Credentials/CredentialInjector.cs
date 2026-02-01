using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using System;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace MyHomelabBrowser.classes.Profiles.Credentials
{
    public static class CredentialInjector
    {
        static readonly JsonSerializerOptions _jsonOpts = new()
        {
            PropertyNameCaseInsensitive = true
        };

        public static async Task Attach(
            WebView2 web,
            Func<bool> isPrivateTab,
            Func<string?> getCurrentProfileUsername,
            Func<bool> isVaultUnlocked,
            Func<string, CredentialEntry?> getCredForHost,
            Action<string, string, string, string?> onCredentialCaptured)
        {
            if (web.CoreWebView2 == null)
                return;

            // ❌ jamais en privé
            if (isPrivateTab())
                return;

            // ===============================
            // 1) JS -> C# (handler stable)
            // ===============================
            web.CoreWebView2.WebMessageReceived += (_, e) =>
            {
                try
                {
                    var json = e.WebMessageAsJson;
                    if (string.IsNullOrWhiteSpace(json))
                        return;

                    var msg = JsonSerializer.Deserialize<WebMsg>(json, _jsonOpts);
                    if (msg == null)
                        return;

                    if (msg.Type != "cred_submit")
                        return;

                    if (string.IsNullOrWhiteSpace(msg.Host))
                        return;

                    if (string.IsNullOrWhiteSpace(msg.Password))
                        return;

                    // 🔐 protection ultime côté C#
                   // if (!isVaultUnlocked())
                      //  return;

                    onCredentialCaptured(
                        msg.Host!,
                        msg.Username ?? "",
                        msg.Password!,
                        msg.FormAction
                    );
                }
                catch
                {
                    // silencieux volontairement
                }
            };

            // ===============================
            // 2) Injection JS (FIABLE)
            // ===============================
            try
            {
                var script = GetScript();

                // pour les futures navigations
                await web.CoreWebView2.AddScriptToExecuteOnDocumentCreatedAsync(script);

                // pour la page déjà chargée
                await web.ExecuteScriptAsync(script);
            }
            catch
            {
                // silencieux
            }

            // ===============================
            // 3) Auto-fill après navigation
            // ===============================
            web.NavigationCompleted += async (_, __) =>
            {
                try
                {
                    if (isPrivateTab())
                        return;

                    if (!isVaultUnlocked())
                        return;

                    var uri = web.Source;
                    if (uri == null || string.IsNullOrWhiteSpace(uri.Host))
                        return;

                    var cred = getCredForHost(uri.Host);
                    if (cred == null)
                        return;

                    var u = JsonSerializer.Serialize(cred.Username);
                    var p = JsonSerializer.Serialize(cred.Password);

                    var js = $@"
(() => {{
  let username = {u};
  let password = {p};

  function setValue(el, val) {{
    if (!el || !val) return;
    el.focus();
    el.value = val;
    el.dispatchEvent(new Event('input', {{ bubbles: true }}));
    el.dispatchEvent(new Event('change', {{ bubbles: true }}));
  }}

  const pwd = document.querySelector('input[type=""password""]');
  if (!pwd) return;

  const user =
    document.querySelector('input[autocomplete=""username""]') ||
    document.querySelector('input[type=""email""]') ||
    document.querySelector(
      'input[name*=""user"" i], input[id*=""user"" i], ' +
      'input[name*=""email"" i], input[id*=""email"" i]'
    ) ||
    document.querySelector(
      'input[type=""text""]:not([name*=""search"" i]):not([id*=""search"" i])'
    );

  if (user && user.value.trim().length === 0)
    setValue(user, username);

  if (pwd.value.trim().length === 0)
    setValue(pwd, password);

  setTimeout(() => {{ password = ''; }}, 0);
}})();";

                    await web.ExecuteScriptAsync(js);
                }
                catch
                {
                    // silencieux
                }
            };
        }

        // ===============================
        // JS injecté (robuste + anti double)
        // ===============================
        static string GetScript()
        {
            return @"
(() => {
  if (window.__mhbCredHooked) return;
  window.__mhbCredHooked = true;

  let lastCaptureKey = null;
  let webviewReady = false;

  // ---------------------------------
  // WebView2 readiness (CRITIQUE)
  // ---------------------------------
  function waitForWebView() {
    if (window.chrome && window.chrome.webview) {
      webviewReady = true;
      return;
    }
    setTimeout(waitForWebView, 50);
  }
  waitForWebView();

  function is2FAForm(form) {
    if (!form) return false;
    return form.classList.contains('two-factor-auth')
        || form.classList.contains('ig-2fa-form');
  }

  function findUserField(root) {
    return (
      root.querySelector('input[autocomplete=""username""]') ||
      root.querySelector('input[type=""email""]') ||
      root.querySelector('input[name*=""user"" i], input[id*=""user"" i]') ||
      root.querySelector('input[name*=""email"" i], input[id*=""email"" i]') ||
      root.querySelector('input[type=""text""]')
    );
  }

  function post(msg) {
    if (!webviewReady) return;
    try {
      window.chrome.webview.postMessage(msg);
    } catch {}
  }

  function tryCapture(context) {
    try {
      if (!context || is2FAForm(context)) return;

      const pwd = context.querySelector('input[type=""password""]');
      if (!pwd) return;

      const password = pwd.value;
      if (!password) return;

      const userField = findUserField(context);
      const username = userField ? (userField.value || '').trim() : '';
      if (!username) return;

      const host = location.host || '';
      if (!host) return;

      const key = host + '|' + username + '|' + password;
      if (key === lastCaptureKey) return;
      lastCaptureKey = key;

      const action =
        (context instanceof HTMLFormElement && context.getAttribute('action')) || null;

      post({
        type: 'cred_submit',
        host,
        username,
        password,
        formAction: action
      });
    } catch {}
  }

  // ===============================
  // 1) submit classique
  // ===============================
  document.addEventListener('submit', e => {
    const form = e.target;
    if (form instanceof HTMLFormElement)
      tryCapture(form);
  }, true);

  // ===============================
  // 2) click bouton (SPA / JS)
  // ===============================
  document.addEventListener('click', e => {
    const btn = e.target.closest('button, input[type=""submit""]');
    if (!btn) return;

    const form = btn.closest('form') || document;
    setTimeout(() => tryCapture(form), 0);
  }, true);

  // ===============================
  // 3) blur password (fallback)
  // ===============================
  document.addEventListener('blur', e => {
    if (e.target?.type === 'password') {
      const form = e.target.closest('form') || document;
      setTimeout(() => tryCapture(form), 0);
    }
  }, true);

  // ===============================
  // 4) CAPTURE PROACTIVE (INTENTION)
  // ===============================
  function tryLiveCapture() {
    tryCapture(document);
  }

  document.addEventListener('input', e => {
    if (
      e.target?.type === 'password' ||
      e.target?.type === 'email' ||
      e.target?.getAttribute?.('autocomplete') === 'username'
    ) {
      setTimeout(tryLiveCapture, 0);
    }
  }, true);

})();";
        }



        sealed class WebMsg
        {
            [JsonPropertyName("type")]
            public string? Type { get; set; }

            [JsonPropertyName("host")]
            public string? Host { get; set; }

            [JsonPropertyName("username")]
            public string? Username { get; set; }

            [JsonPropertyName("password")]
            public string? Password { get; set; }

            [JsonPropertyName("formAction")]
            public string? FormAction { get; set; }
        }
    }
}
