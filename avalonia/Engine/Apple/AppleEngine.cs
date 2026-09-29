using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using MyHomelabBrowser.classes;
using PommeBrowser.Linux.Core;
using static MyHomelabBrowser.classes.Localization.Loc;
using static PommeBrowser.Engine.Apple.ObjC;

namespace PommeBrowser.Engine.Apple
{
    /// <summary>
    /// Sessions WKWebView (macOS). Le contrôle d'Avalonia crée la vue et son délégué de navigation ;
    /// PommeBrowser complète ce délégué (certificats, échecs, téléchargements, fin du processus de
    /// la page) et fournit le délégué d'interface (fenêtres, autorisations, boîtes JavaScript,
    /// envoi de fichiers). Tout se passe sur le fil principal (celui de l'interface).
    /// </summary>
    [SupportedOSPlatform("macos")]
    static class AppleEngine
    {
        const string WebKitFramework = "/System/Library/Frameworks/WebKit.framework/WebKit";
        const long AuthUseCredential = 0;
        const long AuthDefault = 1;
        const long AuthCancel = 2;
        const long ResponseAllow = 1;
        const long ResponseDownload = 2;
        const long NavigationLinkActivated = 0;

        static readonly Dictionary<nint, AppleEngineTab> Tabs = new();
        static readonly HashSet<string> AllowedCertificates = new(StringComparer.OrdinalIgnoreCase);
        static bool _installed;
        static nint _uiDelegate;
        static nint _messageHandler;
        static nint _downloadDelegate;

        /// <summary>Filtre anti-pub actif (WKContentRuleList retenu), 0 s'il n'est pas prêt.</summary>
        public static nint ContentFilter { get; private set; }

        public static nint MessageHandler => _messageHandler;

        public static void Register(nint view, AppleEngineTab tab) => Tabs[view] = tab;

        public static void Unregister(nint view) => Tabs.Remove(view);

        static AppleEngineTab? Tab(nint view) => view != 0 && Tabs.TryGetValue(view, out AppleEngineTab? tab) ? tab : null;

        public static void AllowCertificate(string key) => AllowedCertificates.Add(key);

        public static string Authority(string host, long port) => port is 0 or 443 ? host : host + ":" + port;

        // ---------------------------------------------------------------
        // Installation (une fois) des délégués
        // ---------------------------------------------------------------

        public static unsafe void Install()
        {
            if (_installed)
                return;
            _installed = true;

            // Délégué de navigation d'Avalonia (ManagedWKNavigationDelegate) : méthodes ajoutées.
            nint navigation = Class("ManagedWKNavigationDelegate");
            if (navigation != 0)
            {
                class_addMethod(navigation, Sel("webView:didStartProvisionalNavigation:"), (nint)(delegate* unmanaged[Cdecl]<nint, nint, nint, nint, void>)&DidStartProvisional, "v@:@@");
                class_addMethod(navigation, Sel("webView:didCommitNavigation:"), (nint)(delegate* unmanaged[Cdecl]<nint, nint, nint, nint, void>)&DidCommit, "v@:@@");
                class_addMethod(navigation, Sel("webView:didFailProvisionalNavigation:withError:"), (nint)(delegate* unmanaged[Cdecl]<nint, nint, nint, nint, nint, void>)&DidFail, "v@:@@@");
                class_addMethod(navigation, Sel("webView:didFailNavigation:withError:"), (nint)(delegate* unmanaged[Cdecl]<nint, nint, nint, nint, nint, void>)&DidFail, "v@:@@@");
                class_addMethod(navigation, Sel("webView:didReceiveAuthenticationChallenge:completionHandler:"), (nint)(delegate* unmanaged[Cdecl]<nint, nint, nint, nint, nint, void>)&DidReceiveChallenge, "v@:@@@?");
                class_addMethod(navigation, Sel("webViewWebContentProcessDidTerminate:"), (nint)(delegate* unmanaged[Cdecl]<nint, nint, nint, void>)&ProcessTerminated, "v@:@");
                if (OperatingSystem.IsMacOSVersionAtLeast(11, 3))
                {
                    class_addMethod(navigation, Sel("webView:decidePolicyForNavigationResponse:decisionHandler:"), (nint)(delegate* unmanaged[Cdecl]<nint, nint, nint, nint, nint, void>)&DecideResponse, "v@:@@@?");
                    class_addMethod(navigation, Sel("webView:navigationResponse:didBecomeDownload:"), (nint)(delegate* unmanaged[Cdecl]<nint, nint, nint, nint, nint, void>)&BecameDownload, "v@:@@@");
                    class_addMethod(navigation, Sel("webView:navigationAction:didBecomeDownload:"), (nint)(delegate* unmanaged[Cdecl]<nint, nint, nint, nint, nint, void>)&BecameDownload, "v@:@@@");
                }
            }
            else
            {
                RuntimeLogBuffer.Append("[WKWebView] Délégué de navigation d'Avalonia introuvable : fonctions réduites.");
            }

            _uiDelegate = New(DefineClass("PommeWKUIDelegate", new[] { "WKUIDelegate" },
                ("webView:createWebViewWithConfiguration:forNavigationAction:windowFeatures:", (nint)(delegate* unmanaged[Cdecl]<nint, nint, nint, nint, nint, nint, nint>)&CreateWebView, "@@:@@@@"),
                ("webViewDidClose:", (nint)(delegate* unmanaged[Cdecl]<nint, nint, nint, void>)&DidClose, "v@:@"),
                ("webView:runJavaScriptAlertPanelWithMessage:initiatedByFrame:completionHandler:", (nint)(delegate* unmanaged[Cdecl]<nint, nint, nint, nint, nint, nint, void>)&Alert, "v@:@@@@?"),
                ("webView:runJavaScriptConfirmPanelWithMessage:initiatedByFrame:completionHandler:", (nint)(delegate* unmanaged[Cdecl]<nint, nint, nint, nint, nint, nint, void>)&Confirm, "v@:@@@@?"),
                ("webView:runJavaScriptTextInputPanelWithPrompt:defaultText:initiatedByFrame:completionHandler:", (nint)(delegate* unmanaged[Cdecl]<nint, nint, nint, nint, nint, nint, nint, void>)&Prompt, "v@:@@@@@?"),
                ("webView:requestMediaCapturePermissionForOrigin:initiatedByFrame:type:decisionHandler:", (nint)(delegate* unmanaged[Cdecl]<nint, nint, nint, nint, nint, long, nint, void>)&MediaPermission, "v@:@@@q@?"),
                ("webView:runOpenPanelWithParameters:initiatedByFrame:completionHandler:", (nint)(delegate* unmanaged[Cdecl]<nint, nint, nint, nint, nint, nint, void>)&OpenPanel, "v@:@@@@?")));

            _messageHandler = New(DefineClass("PommeWKScriptMessageHandler", new[] { "WKScriptMessageHandler" },
                ("userContentController:didReceiveScriptMessage:", (nint)(delegate* unmanaged[Cdecl]<nint, nint, nint, nint, void>)&DidReceiveMessage, "v@:@@")));

            if (OperatingSystem.IsMacOSVersionAtLeast(11, 3))
            {
                _downloadDelegate = New(DefineClass("PommeWKDownloadDelegate", new[] { "WKDownloadDelegate" },
                    ("download:decideDestinationUsingResponse:suggestedFilename:completionHandler:", (nint)(delegate* unmanaged[Cdecl]<nint, nint, nint, nint, nint, nint, void>)&AppleDownload.DecideDestination, "v@:@@@@?"),
                    ("downloadDidFinish:", (nint)(delegate* unmanaged[Cdecl]<nint, nint, nint, void>)&AppleDownload.Finished, "v@:@"),
                    ("download:didFailWithError:resumeData:", (nint)(delegate* unmanaged[Cdecl]<nint, nint, nint, nint, nint, void>)&AppleDownload.Failed, "v@:@@@")));
            }
        }

        /// <summary>
        /// WebKit note une fois pour toutes les méthodes que le délégué sait traiter : il est donc
        /// réattribué après l'ajout de celles de PommeBrowser, et le délégué d'interface est posé.
        /// </summary>
        public static void Attach(nint view)
        {
            nint navigationDelegate = Send(view, Sel("navigationDelegate"));
            if (navigationDelegate != 0)
                Send(view, Sel("setNavigationDelegate:"), navigationDelegate);
            if (_uiDelegate != 0)
                Send(view, Sel("setUIDelegate:"), _uiDelegate);
        }

        // ---------------------------------------------------------------
        // Délégué de navigation
        // ---------------------------------------------------------------

        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
        static void DidStartProvisional(nint self, nint cmd, nint view, nint navigation) => Guard(() => Tab(view)?.OnProvisionalStart());

        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
        static void DidCommit(nint self, nint cmd, nint view, nint navigation) => Guard(() => Tab(view)?.OnCommitted());

        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
        static void DidFail(nint self, nint cmd, nint view, nint navigation, nint error) => Guard(() => Tab(view)?.OnFailed(error));

        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
        static void ProcessTerminated(nint self, nint cmd, nint view) => Guard(() => Tab(view)?.OnProcessTerminated());

        /// <summary>
        /// Certificat refusé par le système : accepté s'il a été approuvé pour cet hôte, sinon la
        /// connexion est coupée et la page de PommeBrowser l'explique. Identifiants HTTP (Basic,
        /// Digest) : demandés à l'utilisateur. La réponse doit toujours être donnée à WebKit.
        /// </summary>
        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
        static void DidReceiveChallenge(nint self, nint cmd, nint view, nint challenge, nint completion)
        {
            long disposition = AuthDefault;
            nint credential = 0;
            bool answerLater = false;
            try
            {
                nint space = Send(challenge, Sel("protectionSpace"));
                string? method = ToManaged(Send(space, Sel("authenticationMethod")));
                string host = ToManaged(Send(space, Sel("host"))) ?? string.Empty;
                long port = GetLong(space, Sel("port"));

                if (method == "NSURLAuthenticationMethodServerTrust")
                {
                    nint trust = Send(space, Sel("serverTrust"));
                    if (trust != 0 && !SecTrustEvaluateWithError(trust, out nint error))
                    {
                        if (error != 0)
                            CFRelease(error);
                        byte[] der = LeafCertificate(trust);
                        string key = Authority(host, port) + "|" + Convert.ToHexString(SHA256.HashData(der));
                        if (der.Length > 0 && AllowedCertificates.Contains(key))
                        {
                            disposition = AuthUseCredential;
                            credential = Send(Class("NSURLCredential"), Sel("credentialForTrust:"), trust);
                        }
                        else
                        {
                            disposition = AuthCancel;
                            Tab(view)?.OnCertificateRejected(host, port, der, key);
                        }
                    }
                }
                else if (method is "NSURLAuthenticationMethodHTTPBasic" or "NSURLAuthenticationMethodHTTPDigest" && Tab(view) is { } tab
                         && GetLong(challenge, Sel("previousFailureCount")) < 3)
                {
                    answerLater = true;
                    nint kept = KeepBlock(completion);
                    string realm = ToManaged(Send(space, Sel("realm"))) ?? string.Empty;
                    _ = AskCredentialsAsync(tab, Authority(host, port), realm, kept);
                }
            }
            catch (Exception ex)
            {
                RuntimeLogBuffer.Append("[WKWebView] " + ex.Message);
            }
            finally
            {
                if (!answerLater)
                    CallBlock(completion, disposition, credential);
            }
        }

        static async Task AskCredentialsAsync(AppleEngineTab tab, string authority, string realm, nint completion)
        {
            (string User, string Password)? answer = null;
            try
            {
                answer = await tab.AskCredentialsAsync(authority, realm);
            }
            finally
            {
                nint credential = answer is { } value
                    ? Send(Class("NSURLCredential"), Sel("credentialWithUser:password:persistence:"), String(value.User), String(value.Password), 1)
                    : 0;
                CallBlock(completion, answer != null ? AuthUseCredential : AuthCancel, credential);
                DropBlock(completion);
            }
        }

        static byte[] LeafCertificate(nint trust)
        {
            if (SecTrustGetCertificateCount(trust) <= 0)
                return Array.Empty<byte>();
            nint data = SecCertificateCopyData(SecTrustGetCertificateAtIndex(trust, 0));
            try
            {
                return Data(data);
            }
            finally
            {
                if (data != 0)
                    CFRelease(data);
            }
        }

        /// <summary>Fichier que WebKit ne sait pas afficher, ou envoyé « en pièce jointe » : téléchargement.</summary>
        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
        static void DecideResponse(nint self, nint cmd, nint view, nint navigationResponse, nint decisionHandler)
        {
            long policy = ResponseAllow;
            try
            {
                bool mainFrame = GetBool(navigationResponse, Sel("isForMainFrame"));
                bool canShow = GetBool(navigationResponse, Sel("canShowMIMEType"));
                nint response = Send(navigationResponse, Sel("response"));
                bool attachment = false;
                if (IsKindOf(response, "NSHTTPURLResponse") && RespondsTo(response, "valueForHTTPHeaderField:"))
                {
                    string? disposition = ToManaged(Send(response, Sel("valueForHTTPHeaderField:"), String("Content-Disposition")));
                    attachment = disposition != null && disposition.TrimStart().StartsWith("attachment", StringComparison.OrdinalIgnoreCase);
                }
                if (mainFrame && (!canShow || attachment))
                    policy = ResponseDownload;
            }
            catch (Exception ex)
            {
                RuntimeLogBuffer.Append("[WKWebView] " + ex.Message);
            }
            finally
            {
                CallBlock(decisionHandler, policy);
            }
        }

        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
        static void BecameDownload(nint self, nint cmd, nint view, nint navigation, nint download)
            => Guard(() =>
            {
                if (_downloadDelegate != 0)
                    AppleDownload.Track(download, _downloadDelegate, Tab(view)?.IsPrivate ?? false);
            });

        // ---------------------------------------------------------------
        // Délégué d'interface
        // ---------------------------------------------------------------

        /// <summary>
        /// Lien target=_blank : nouvel onglet. Fenêtre ouverte par un script (window.open, connexion
        /// OAuth…) : fenêtre séparée reliée à la page. WebKit bloque déjà celles ouvertes sans clic.
        /// </summary>
        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
        static nint CreateWebView(nint self, nint cmd, nint view, nint configuration, nint action, nint features)
        {
            try
            {
                string? url = UrlString(Send(Send(action, Sel("request")), Sel("URL")));
                if (GetLong(action, Sel("navigationType")) == NavigationLinkActivated && !string.IsNullOrEmpty(url))
                {
                    Tab(view)?.OnNewTabRequested(url);
                    return 0;
                }
                return ApplePopupWindow.Create(configuration, features, _uiDelegate);
            }
            catch (Exception ex)
            {
                RuntimeLogBuffer.Append("[WKWebView] " + ex.Message);
                return 0;
            }
        }

        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
        static void DidClose(nint self, nint cmd, nint view)
            => Guard(() =>
            {
                if (!ApplePopupWindow.Close(view))
                    Tab(view)?.OnCloseRequested();
            });

        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
        static void Alert(nint self, nint cmd, nint view, nint message, nint frame, nint completion)
        {
            nint kept = KeepBlock(completion);
            string text = ToManaged(message) ?? string.Empty;
            _ = AnswerLaterAsync(kept, async () =>
            {
                if (Owner(view) is { } owner)
                    await Views.Dialogs.Dialogs.AlertAsync(owner, PageTitle(view), text);
                CallBlock(kept);
            });
        }

        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
        static void Confirm(nint self, nint cmd, nint view, nint message, nint frame, nint completion)
        {
            nint kept = KeepBlock(completion);
            string text = ToManaged(message) ?? string.Empty;
            _ = AnswerLaterAsync(kept, async () =>
            {
                bool accepted = Owner(view) is { } owner && await Views.Dialogs.Dialogs.ConfirmAsync(owner, PageTitle(view), text, Tr("OK"));
                CallBlockBool(kept, accepted);
            });
        }

        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
        static void Prompt(nint self, nint cmd, nint view, nint prompt, nint defaultText, nint frame, nint completion)
        {
            nint kept = KeepBlock(completion);
            string text = ToManaged(prompt) ?? string.Empty;
            string initial = ToManaged(defaultText) ?? string.Empty;
            _ = AnswerLaterAsync(kept, async () =>
            {
                string? answer = null;
                if (Owner(view) is { } owner)
                {
                    var dialog = new Views.Dialogs.FormDialog(PageTitle(view), Tr("OK"));
                    dialog.AddText(text);
                    TextBox entry = dialog.AddEntry(string.Empty, initial);
                    if (await dialog.ShowAsync(owner))
                        answer = entry.Text ?? string.Empty;
                }
                CallBlock(kept, answer != null ? String(answer) : 0);
            });
        }

        /// <summary>Caméra et micro (macOS 12) : mêmes questions et mêmes choix par site que sur les autres systèmes.</summary>
        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
        static void MediaPermission(nint self, nint cmd, nint view, nint origin, nint frame, long type, nint decisionHandler)
        {
            const long Grant = 1;
            const long Deny = 2;
            nint kept = KeepBlock(decisionHandler);
            try
            {
                string protocol = ToManaged(Send(origin, Sel("protocol"))) ?? "https";
                string host = ToManaged(Send(origin, Sel("host"))) ?? string.Empty;
                long port = GetLong(origin, Sel("port"));
                string site = protocol + "://" + host + (port > 0 ? ":" + port : string.Empty);
                PermissionKind kind = type switch
                {
                    0 => PermissionKind.Camera,
                    1 => PermissionKind.Microphone,
                    _ => PermissionKind.CameraAndMicrophone
                };

                if (Tab(view) is not { } tab)
                {
                    CallBlock(kept, Deny);
                    DropBlock(kept);
                    return;
                }
                tab.OnPermissionRequested(new PermissionRequest(kind, site, allow => Dispatcher.UIThread.Post(() =>
                {
                    CallBlock(kept, allow ? Grant : Deny);
                    DropBlock(kept);
                })));
            }
            catch (Exception ex)
            {
                RuntimeLogBuffer.Append("[WKWebView] " + ex.Message);
                CallBlock(kept, Deny);
                DropBlock(kept);
            }
        }

        /// <summary>Champ « fichier » d'un formulaire : choix du fichier à envoyer.</summary>
        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
        static void OpenPanel(nint self, nint cmd, nint view, nint parameters, nint frame, nint completion)
        {
            nint kept = KeepBlock(completion);
            bool multiple = GetBool(parameters, Sel("allowsMultipleSelection"));
            _ = AnswerLaterAsync(kept, async () =>
            {
                nint urls = 0;
                if (Owner(view) is { } owner)
                {
                    IReadOnlyList<IStorageFile> files = await owner.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions { AllowMultiple = multiple });
                    if (files.Count > 0)
                    {
                        urls = Send(Class("NSMutableArray"), Sel("array"));
                        foreach (IStorageFile file in files)
                        {
                            if (file.TryGetLocalPath() is { } path)
                                Send(urls, Sel("addObject:"), FileUrl(path));
                        }
                    }
                }
                CallBlock(kept, urls);
            });
        }

        /// <summary>Réponse donnée après une boîte de dialogue ; le bloc de WebKit est toujours appelé puis libéré.</summary>
        static async Task AnswerLaterAsync(nint block, Func<Task> answer)
        {
            try
            {
                await answer();
            }
            catch (Exception ex)
            {
                RuntimeLogBuffer.Append("[WKWebView] " + ex.Message);
            }
            finally
            {
                DropBlock(block);
            }
        }

        static Window? Owner(nint view) => TopLevel.GetTopLevel(Tab(view)?.Host) as Window;

        static string PageTitle(nint view)
            => Uri.TryCreate(UrlString(Send(view, Sel("URL"))), UriKind.Absolute, out Uri? page) && page.Host.Length > 0 ? page.Host : "PommeBrowser";

        // ---------------------------------------------------------------
        // Messages des scripts de PommeBrowser
        // ---------------------------------------------------------------

        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
        static void DidReceiveMessage(nint self, nint cmd, nint controller, nint message)
            => Guard(() =>
            {
                nint view = Send(message, Sel("webView"));
                string name = ToManaged(Send(message, Sel("name"))) ?? string.Empty;
                string body = Describe(Send(message, Sel("body"))) ?? string.Empty;
                Tab(view)?.OnScriptMessage(name, body);
            });

        // ---------------------------------------------------------------
        // Anti-pub : WKContentRuleListStore (mêmes règles que WebKitGTK)
        // ---------------------------------------------------------------

        static nint _store;

        static nint Store(string directory)
        {
            if (_store == 0)
            {
                Directory.CreateDirectory(directory);
                _store = Retain(Send(Class("WKContentRuleListStore"), Sel("storeWithURL:"), FileUrl(directory)));
            }
            return _store;
        }

        public static unsafe Task<nint> LoadFilterAsync(string storeDirectory, string id)
        {
            var completion = new TaskCompletionSource<nint>(TaskCreationOptions.RunContinuationsAsynchronously);
            Dispatcher.UIThread.Post(() =>
            {
                nint block = CreateBlock((nint)(delegate* unmanaged[Cdecl]<nint, nint, nint, void>)&OnRuleList, "v@?@@", completion);
                Send(Store(storeDirectory), Sel("lookUpContentRuleListForIdentifier:completionHandler:"), String(id), block);
            });
            return completion.Task;
        }

        public static unsafe Task<nint> CompileFilterAsync(string storeDirectory, string id, string json)
        {
            var completion = new TaskCompletionSource<nint>(TaskCreationOptions.RunContinuationsAsynchronously);
            Dispatcher.UIThread.Post(() =>
            {
                nint block = CreateBlock((nint)(delegate* unmanaged[Cdecl]<nint, nint, nint, void>)&OnRuleList, "v@?@@", completion);
                Send(Store(storeDirectory), Sel("compileContentRuleListForIdentifier:encodedContentRuleList:completionHandler:"), String(id), String(json), block);
            });
            return completion.Task;
        }

        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
        static void OnRuleList(nint block, nint list, nint error)
        {
            var completion = BlockContext<TaskCompletionSource<nint>>(block);
            FreeBlock(block);
            if (list != 0)
            {
                completion?.TrySetResult(Retain(list));
            }
            else if (error != 0 && GetLong(error, Sel("code")) != 7)
            {
                // 7 : règles introuvables (première compilation) ; le reste est une vraie erreur.
                completion?.TrySetException(new InvalidOperationException(ToManaged(Send(error, Sel("localizedDescription")))));
            }
            else
            {
                completion?.TrySetResult(0);
            }
        }

        public static void SetContentFilter(nint filter)
            => Dispatcher.UIThread.Post(() =>
            {
                nint old = ContentFilter;
                ContentFilter = filter;
                foreach (AppleEngineTab tab in Tabs.Values)
                    tab.ApplyContentFilter(force: true);
                if (old != 0 && old != filter)
                    Release(old);
            });

        // ---------------------------------------------------------------
        // Réglages et données
        // ---------------------------------------------------------------

        public static void ApplySettings()
            => Dispatcher.UIThread.Post(() =>
            {
                foreach (AppleEngineTab tab in Tabs.Values)
                    tab.ApplyAppearance();
            });

        /// <summary>Données des sites du profil (cookies, stockage, cache) depuis <paramref name="since"/>.</summary>
        public static unsafe Task ClearDataAsync(TimeSpan? since, bool cookiesAndSiteData, bool cache)
        {
            var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            Dispatcher.UIThread.Post(() =>
            {
                try
                {
                    nint store = 0;
                    foreach (AppleEngineTab tab in Tabs.Values)
                    {
                        if (!tab.IsPrivate)
                        {
                            store = tab.DataStore;
                            break;
                        }
                    }
                    if (store == 0)
                        store = Send(Class("WKWebsiteDataStore"), Sel("defaultDataStore"));

                    nint cacheTypes = Send(Class("NSMutableSet"), Sel("set"));
                    foreach (string name in new[] { "WKWebsiteDataTypeDiskCache", "WKWebsiteDataTypeMemoryCache", "WKWebsiteDataTypeFetchCache", "WKWebsiteDataTypeOfflineWebApplicationCache" })
                    {
                        nint type = Constant(WebKitFramework, name);
                        if (type != 0)
                            Send(cacheTypes, Sel("addObject:"), type);
                    }

                    nint types = Send(Class("NSMutableSet"), Sel("set"));
                    if (cookiesAndSiteData)
                    {
                        Send(types, Sel("unionSet:"), Send(Class("WKWebsiteDataStore"), Sel("allWebsiteDataTypes")));
                        Send(types, Sel("minusSet:"), cacheTypes);
                    }
                    if (cache)
                        Send(types, Sel("unionSet:"), cacheTypes);

                    nint date = since is { } span
                        ? SendObjectDouble(Class("NSDate"), Sel("dateWithTimeIntervalSinceNow:"), -span.TotalSeconds)
                        : Send(Class("NSDate"), Sel("distantPast"));
                    nint block = CreateBlock((nint)(delegate* unmanaged[Cdecl]<nint, void>)&OnDataRemoved, "v@?", completion);
                    Send(store, Sel("removeDataOfTypes:modifiedSince:completionHandler:"), types, date, block);
                }
                catch (Exception ex)
                {
                    completion.TrySetException(ex);
                }
            });
            return completion.Task;
        }

        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
        static void OnDataRemoved(nint block)
        {
            var completion = BlockContext<TaskCompletionSource<bool>>(block);
            FreeBlock(block);
            completion?.TrySetResult(true);
        }

        /// <summary>Une exception ne doit jamais remonter dans le code natif qui appelle le délégué.</summary>
        static void Guard(Action action)
        {
            try
            {
                action();
            }
            catch (Exception ex)
            {
                RuntimeLogBuffer.Append("[WKWebView] " + ex.Message);
            }
        }
    }
}
