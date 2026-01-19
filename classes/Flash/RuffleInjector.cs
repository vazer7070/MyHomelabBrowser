using System.Threading.Tasks;

// Force WPF WebView2 (évite l'ambiguïté WinForms/WPF)
using WpfWebView2 = Microsoft.Web.WebView2.Wpf.WebView2;

namespace MyHomelabBrowser.classes.Flash
{
    public static class RuffleInjector
    {
        public static async Task InjectAsync(WpfWebView2 webView)
        {
            // 1) Tracker (safe)
            string tracker = """
    (() => {
      if (!window.__mhbRuffle) {
        window.__mhbRuffle = {
          injectedAt: Date.now(),
          started: false,
          lastFrameAt: 0,
          errors: [],
          markStarted() { this.started = true; },
          markFrame() { this.lastFrameAt = Date.now(); }
        };
        const origErr = console.error;
        console.error = function(...args) {
          try {
            window.__mhbRuffle.errors.push(String(args && args[0] ? args[0] : 'console.error'));
            if (window.__mhbRuffle.errors.length > 50) window.__mhbRuffle.errors.shift();
          } catch {}
          return origErr.apply(console, args);
        };
      }
      return true;
    })();
    """;

            await webView.ExecuteScriptAsync(tracker);

            // 2) Injection Ruffle + auto-attach + frame tick
            string script = """
    (async () => {
      function hasFlashDom() {
        // embed flash
        if (document.querySelector('embed[type="application/x-shockwave-flash"]')) return true;
        if (document.querySelector('embed[src*=".swf"]')) return true;

        // object flash
        if (document.querySelector('object[type="application/x-shockwave-flash"]')) return true;
        if (document.querySelector('object[data*=".swf"]')) return true;

        // object param movie / autres params .swf
        const objs = document.querySelectorAll('object');
        for (const o of objs) {
          const params = o.querySelectorAll('param[name][value]');
          for (const p of params) {
            const name = (p.getAttribute('name') || '').toLowerCase();
            const val = (p.getAttribute('value') || '').toLowerCase();
            if (name === 'movie' && val.includes('.swf')) return true;
            if (val.includes('.swf')) return true;
          }
        }

        return false;
      }

      if (!hasFlashDom()) return "no-flash-dom";

      // déjà présent ?
      if (window.RufflePlayer) {
        try { window.__mhbRuffle.markStarted(); } catch {}
        return "already-present";
      }

      // inject script
      await new Promise((resolve) => {
        const s = document.createElement('script');
        s.src = 'https://unpkg.com/@ruffle-rs/ruffle';
        s.onload = resolve;
        s.onerror = resolve;
        document.head.appendChild(s);
      });

      if (!window.RufflePlayer) {
        try { window.__mhbRuffle.errors.push('ruffle-script-not-loaded'); } catch {}
        return "load-failed";
      }

      try {
        window.RufflePlayer.config = { autoplay: 'on' };

        // auto-attach : ruffle remplace embed/object automatiquement quand chargé
        try { window.__mhbRuffle.markStarted(); } catch {}

        // tick frame pour détecter freeze
        function tick() {
          try { window.__mhbRuffle.markFrame(); } catch {}
          requestAnimationFrame(tick);
        }
        requestAnimationFrame(tick);

        return "ok";
      } catch (e) {
        try { window.__mhbRuffle.errors.push('inject-ex:' + String(e)); } catch {}
        return "exception";
      }
    })();
    """;

            await webView.ExecuteScriptAsync(script);
        }


        public static async Task<RuffleStatus> GetStatusAsync(WpfWebView2 webView)
        {
            string js = """
            (() => {
              const s = window.__mhbRuffle;
              if (!s) return { exists:false };
              return {
                exists: true,
                started: !!s.started,
                injectedAt: s.injectedAt || 0,
                lastFrameAt: s.lastFrameAt || 0,
                errors: s.errors || []
              };
            })();
            """;

            var json = await webView.ExecuteScriptAsync(js);
            return RuffleStatus.FromWebViewJson(json);
        }
    }
}
