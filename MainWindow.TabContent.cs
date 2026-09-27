using Microsoft.Web.WebView2.Wpf;
using MyHomelabBrowser.classes;
using MyHomelabBrowser.classes.Flash;
using MyHomelabBrowser.controles;
using System.Diagnostics;
using System.Windows.Controls;

namespace MyHomelabBrowser
{
    public partial class MainWindow
    {
        // Contenu associé à chaque TabItem (Tag) : page web ou vue interne.
        public abstract class TabContent { }

        public class WebTabContent : TabContent
        {
            public WebView2 Web { get; init; } = null!;
            public bool IsPinned { get; set; }
            public bool IsSuspended { get; set; }
            public bool IsPrivate { get; set; }
            public DateTime LastActivated { get; set; } = DateTime.Now;

            // FLASH
            public FlashDecisionService? FlashService { get; set; }
            public FlashMode FlashMode { get; set; } = FlashMode.None;
            public bool FlashRequired { get; set; }
            public bool? LastFlashButtonVisible { get; set; }
            public FlashMode? LastFlashMode { get; set; }
            public Grid HostGrid { get; set; } = new Grid();
            public bool IsLegacyLaunching { get; set; }
            public string? LegacyLastError { get; set; }
            public LegacyFlashView? LegacyView { get; set; }
            public IntPtr LegacyHwnd { get; set; } = IntPtr.Zero;
            public ExternalWindowDock? LegacyHost;
            public bool IsCustomView { get; set; }
            public Func<UserControl>? CreateView { get; set; }

            public LegacyProcess? LegacyProc { get; set; }
            /// <summary>Onglet Legacy restauré : Basilisk sera lancé à sa première ouverture.</summary>
            public Uri? PendingLegacyUri { get; set; }

            // Passage en HTTPS en cours, et avertissement affiché si le site n'a pas de HTTPS.
            internal HttpsUpgradeAttempt? HttpsUpgrade { get; set; }
            public string? HttpsInterstitialUrl { get; set; }
            public string? HttpsInterstitialNonce { get; set; }
            public LegacyProfileLease? LegacyProfileLease { get; set; }
            public IntPtr LegacyTopHwnd { get; set; } = IntPtr.Zero;
            public bool ForceLegacyOnce { get; set; } = false;
            public bool FlashChecked { get; set; } = false;
            public IntPtr LegacyEmbedHwnd { get; set; } = IntPtr.Zero;

            // Legacy proxy
            public bool IsLegacyExternal { get; set; }
            public string? LegacyUrl { get; set; }
            public int? LegacyPid { get; set; }

            public RuffleMonitor? RuffleMonitor { get; set; }
            public CancellationTokenSource? FlashNavigationCts { get; set; }
            public int FlashNavigationGeneration { get; set; }
            public bool FlashNetworkHookAttached { get; set; }
            public FlashDetectionResult LastFlashDetection { get; set; } = FlashDetectionResult.None;
            public string? RuffleFailureReason { get; set; }
            public RuffleStatus? RuffleFailureStatus { get; set; }

            public FlashUxOverlay? FlashOverlay { get; set; }

            // Cycle de vie de l'onglet
            public TabItem? OwnerTab { get; set; }
            public bool IsClosed { get; set; }
            public bool IsShutDown { get; set; }
            public bool IsLoading { get; set; }
            public string? PendingUrl { get; set; }
            public HistoryEntry? LastHistoryEntry { get; set; }
            public DateTime LastPopupNoticeUtc { get; set; } = DateTime.MinValue;
        }

        class ViewTabContent : TabContent
        {
            public UserControl View { get; init; } = null!;
        }
    }
}
