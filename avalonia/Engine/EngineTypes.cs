using System;
using System.Threading.Tasks;

namespace PommeBrowser.Engine
{
    /// <summary>Étapes du chargement de la page principale.</summary>
    public enum LoadStage
    {
        Started,
        Redirected,
        Committed,
        Finished
    }

    /// <summary>Raisons du refus d'un certificat.</summary>
    [Flags]
    public enum CertificateErrors
    {
        None = 0,
        UnknownAuthority = 1,
        WrongName = 2,
        NotYetValid = 4,
        Expired = 8,
        Revoked = 16,
        Insecure = 32,
        Other = 64
    }

    /// <summary>Certificat refusé par le moteur (autorité inconnue, nom incorrect, expiré…).</summary>
    public sealed class CertificateProblem
    {
        public required string Uri { get; init; }
        public required string Host { get; init; }

        /// <summary>Certificat au format DER (empreinte, émetteur, dates) ; vide s'il n'a pas pu être lu.</summary>
        public required byte[] Der { get; init; }

        public required CertificateErrors Errors { get; init; }

        /// <summary>Objet natif, rendu au moteur pour accepter le certificat.</summary>
        internal object? Native { get; init; }
    }

    public enum PermissionKind
    {
        Geolocation,
        Camera,
        Microphone,
        CameraAndMicrophone,
        Notifications,
        Clipboard,
        StorageAccess,
        ScreenCapture,
        /// <summary>Pointeur capturé (jeux) : sans risque, relâché avec Échap.</summary>
        PointerLock,
        /// <summary>Liste des caméras et micros : accordée si l'un d'eux l'est déjà.</summary>
        MediaDevices,
        Other
    }

    /// <summary>Autorisation demandée par un site. Une seule réponse est prise en compte.</summary>
    public sealed class PermissionRequest
    {
        readonly Action<bool> _decide;
        int _decided;

        public PermissionRequest(PermissionKind kind, string origin, Action<bool> decide)
        {
            Kind = kind;
            Origin = origin;
            _decide = decide;
        }

        public PermissionKind Kind { get; }
        public string Origin { get; }

        public void Allow() => Decide(true);
        public void Deny() => Decide(false);

        void Decide(bool allow)
        {
            if (System.Threading.Interlocked.Exchange(ref _decided, 1) == 0)
                _decide(allow);
        }
    }

    /// <summary>
    /// Navigation de la page principale sur le point de commencer. Le gestionnaire peut
    /// l'annuler, ou demander de l'ouvrir dans un nouvel onglet (clic du milieu, Ctrl+clic).
    /// </summary>
    public sealed class NavigationRequest
    {
        public required string Uri { get; init; }

        /// <summary>Lien à ouvrir dans un nouvel onglet (target=_blank, clic du milieu, Ctrl+clic).</summary>
        public bool IsNewWindow { get; init; }

        public bool OpenInBackgroundTab { get; init; }
    }

    /// <summary>Téléchargement lancé par une page. Les événements arrivent sur le fil de l'interface.</summary>
    public sealed class EngineDownload
    {
        readonly Action _cancel;

        public EngineDownload(string uri, string suggestedFileName, Action cancel)
        {
            Uri = uri;
            SuggestedFileName = suggestedFileName;
            _cancel = cancel;
        }

        public string Uri { get; }
        public string SuggestedFileName { get; }
        public string? Destination { get; internal set; }
        public long ReceivedBytes { get; internal set; }
        public long TotalBytes { get; internal set; }
        public bool IsFinished { get; internal set; }
        public bool IsCancelled { get; internal set; }
        public string? Error { get; internal set; }

        /// <summary>Lancé depuis un onglet privé : pas gardé dans la liste après la fin.</summary>
        public bool IsPrivate { get; internal init; }

        public DateTime StartedAt { get; } = DateTime.Now;

        public event Action<EngineDownload>? Changed;

        public void Cancel() => _cancel();

        internal void RaiseChanged() => Changed?.Invoke(this);
    }

    /// <summary>
    /// Fonctions du moteur web propres à chaque système pour un onglet (WebKitGTK, WebView2,
    /// WKWebView). Les événements arrivent sur le fil de l'interface d'Avalonia.
    /// </summary>
    public interface IEngineTab : IDisposable
    {
        bool IsPrivate { get; }
        string? Title { get; }
        string? Uri { get; }
        bool IsLoading { get; }
        double Progress { get; }
        bool CanGoBack { get; }
        bool CanGoForward { get; }

        /// <summary>Facteur de zoom de la page (1 = 100 %).</summary>
        double Zoom { get; set; }

        /// <summary>Titre, adresse, progression ou historique de navigation modifiés.</summary>
        event Action? StateChanged;

        /// <summary>Icône du site (PNG), null s'il n'en a pas.</summary>
        event Action<byte[]?>? FaviconChanged;

        /// <summary>Étape du chargement de la page principale, avec son adresse.</summary>
        event Action<LoadStage, string?>? LoadChanged;

        /// <summary>Échec du chargement de la page principale (adresse, message).</summary>
        event Action<string, string>? LoadFailed;

        /// <summary>Certificat refusé : la page n'est pas affichée.</summary>
        event Action<CertificateProblem>? CertificateError;

        /// <summary>Lien à ouvrir dans un nouvel onglet (la page d'origine ne change pas).</summary>
        event Action<NavigationRequest>? NewTabRequested;

        /// <summary>Le site demande une autorisation ; sans réponse, elle est refusée à la fermeture de l'onglet.</summary>
        event Action<PermissionRequest>? PermissionRequested;

        /// <summary>Lien survolé (null : plus de lien sous le pointeur).</summary>
        event Action<string?>? LinkHovered;

        /// <summary>La page passe en plein écran (vidéo…) ou en sort.</summary>
        event Action<bool>? FullscreenRequested;

        /// <summary>La page demande à fermer l'onglet (window.close).</summary>
        event Action? CloseRequested;

        /// <summary>Le processus de la page s'est arrêté (message pour l'utilisateur).</summary>
        event Action<string>? Crashed;

        /// <summary>Contenu non sécurisé (HTTP) chargé par une page HTTPS.</summary>
        event Action? InsecureContentDetected;

        /// <summary>Nombre de résultats de la recherche dans la page (0 : aucun).</summary>
        event Action<int>? FindMatchesCounted;

        /// <summary>Message d'un script de PommeBrowser (monde isolé) : nom du canal, contenu.</summary>
        event Action<string, string>? ScriptMessage;

        /// <summary>Raccourci du navigateur tapé pendant que la page a le focus (voir BrowserShortcuts).</summary>
        event Action<Avalonia.Input.Key, Avalonia.Input.KeyModifiers>? ShortcutPressed;

        void Navigate(string uri);
        void GoBack();
        void GoForward();
        void Reload(bool bypassCache = false);
        void Stop();
        void Focus();

        /// <summary>
        /// Le clavier suit le focus d'Avalonia : à la page quand sa vue a le focus, sinon à la
        /// fenêtre (nécessaire avec les vues natives intégrées sous X11). <paramref name="force"/> :
        /// réappliqué même sans changement (fenêtre réactivée).
        /// </summary>
        void SyncKeyboard(bool force = false);

        void Find(string text, bool matchCase);
        void FindNext(bool backward);
        void StopFind();

        void Print();
        void ShowDevTools();

        /// <summary>Exécute un script dans la page ; isolé : invisible pour les scripts de la page.</summary>
        Task<string?> EvaluateAsync(string script, bool isolated);

        /// <summary>Script injecté à chaque chargement (monde isolé de PommeBrowser).</summary>
        void AddUserScript(string id, string source, bool allFrames, bool atDocumentStart);
        void RemoveUserScript(string id);

        /// <summary>Canal de messages des scripts isolés vers PommeBrowser.</summary>
        void RegisterMessageHandler(string name);

        /// <summary>Accepte le certificat pour cet hôte (jusqu'à la fermeture de l'application).</summary>
        void AllowCertificate(CertificateProblem problem);

        /// <summary>Réapplique le filtre anti-pub à la page affichée (filtre ou réglages modifiés).</summary>
        void RefreshContentFilter();
    }
}
