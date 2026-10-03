/*
 * Greffon NPAPI de test pour PommeFlashHost. Il se comporte comme un module Flash vis-à-vis de
 * l'hôte (mêmes points d'entrée, mêmes appels) et rapporte ce qu'il observe par NPN_Status,
 * sous la forme « TEST clé=valeur », que les tests vérifient.
 *
 * Compilation : x86_64-w64-mingw32-gcc -shared -O2 -o npPommeTest.dll testplugin.c
 *               (32 bits : i686-w64-mingw32-gcc … -Wl,--kill-at)
 *           ou  clang [--target=i686-pc-windows-msvc] -shared -O2 -o npPommeTest.dll testplugin.c -luser32
 *     Linux :   gcc -shared -fPIC -O2 -o libnpPommeTest.so testplugin.c $(pkg-config --cflags --libs gtk+-2.0) -lX11 -lXtst -lpthread
 *               (GTK 2, comme le module Flash de Linux : sa fenêtre est un GtkPlug dans la prise de
 *               l'hôte ; XTEST simule le clic et la touche de l'utilisateur)
 */
#ifdef _WIN32
#include <windows.h>
#else
#include <pthread.h>
#include <gtk/gtk.h>
#include <gdk/gdkx.h>
#include <X11/extensions/XTest.h>
#include <X11/keysym.h>
#endif
#include <stdarg.h>
#include <stdio.h>
#include <string.h>
#include "npapi-min.h"

/* Fil du module : celui de la fenêtre ; tout appel du greffon doit s'y faire. */
#ifdef _WIN32
typedef DWORD ThreadId;
static ThreadId currentThread(void) { return GetCurrentThreadId(); }
static int sameThread(ThreadId a, ThreadId b) { return a == b; }
#else
typedef pthread_t ThreadId;
static ThreadId currentThread(void) { return pthread_self(); }
static int sameThread(ThreadId a, ThreadId b) { return pthread_equal(a, b) != 0; }
#endif

static NPNetscapeFuncs *browser;
static uint16_t browserSize, browserVersion;
static ThreadId mainThread;
static NPP instanceNpp;
#ifdef _WIN32
static HWND dialog;
static HWND child;
#else
static GtkWidget *plug;
static GtkWidget *decoy;
static Display *xdisplay;
static Window frame;
static int toolkit, keyDone;
static NPBool xembed;
#endif
static int notifications, timerTicks, asyncDone, streamsDone, finished;
static uint32_t timerId;

static int onMainThread(void) { return sameThread(currentThread(), mainThread); }

static void report(const char *format, ...)
{
    char text[2048] = "TEST ";
    va_list args;
    va_start(args, format);
    vsnprintf(text + 5, sizeof(text) - 5, format, args);
    va_end(args);
    browser->status(instanceNpp, text);
}

static uint32_t fnv1a(uint32_t hash, const unsigned char *data, int32_t length)
{
    for (int32_t i = 0; i < length; i++)
        hash = (hash ^ data[i]) * 16777619u;
    return hash;
}

static void checkFinished(void)
{
    /* Contenu principal + 3 adresses demandées, minuterie et appel asynchrone (Linux : et la touche). */
#ifdef _WIN32
    int keyDone = 1;
#endif
    if (!finished && notifications >= 3 && streamsDone >= 3 && timerTicks >= 3 && asyncDone && keyDone)
    {
        finished = 1;
        report("done");
    }
}

/* ------------------------------------------------------------------ */
/* Objet de test : compte ses libérations                              */
/* ------------------------------------------------------------------ */

static int deallocations;

static NPObject *testAllocate(NPP npp, NPClass *type)
{
    (void)npp;
    NPObject *obj = (NPObject *)browser->memalloc(sizeof(NPObject));
    obj->_class = type;
    return obj;
}

static void testDeallocate(NPObject *obj)
{
    deallocations++;
    browser->memfree(obj);
}

static NPClass testClass = { .structVersion = 3, .allocate = testAllocate, .deallocate = testDeallocate };

/* ------------------------------------------------------------------ */
/* Objet scriptable (NPPVpluginScriptableNPObject), comme celui de     */
/* Flash : la page l'appelle par CallFunction (addCallback).           */
/* ------------------------------------------------------------------ */

static NPObject *scriptable;

static void scriptableDeallocate(NPObject *obj)
{
    browser->memfree(obj);
}

static bool scriptableHasMethod(NPObject *obj, void *name)
{
    (void)obj;
    return name == browser->getstringidentifier("CallFunction");
}

/* Réponse : « retour:<requête> », sur le fil du module. */
static bool scriptableInvoke(NPObject *obj, void *name, const NPVariant *args, uint32_t count, NPVariant *result)
{
    (void)obj;
    if (name != browser->getstringidentifier("CallFunction") || count < 1 || args[0].type != NPVariantType_String)
        return false;
    const NPString *request = &args[0].value.stringValue;
    report("call main=%d request=%.*s", onMainThread(), (int)request->UTF8Length, request->UTF8Characters);
    static const char prefix[] = "retour:";
    uint32_t length = (uint32_t)(sizeof(prefix) - 1) + request->UTF8Length;
    char *text = (char *)browser->memalloc(length + 1);
    memcpy(text, prefix, sizeof(prefix) - 1);
    memcpy(text + sizeof(prefix) - 1, request->UTF8Characters, request->UTF8Length);
    text[length] = 0;
    result->type = NPVariantType_String;
    result->value.stringValue.UTF8Characters = text;
    result->value.stringValue.UTF8Length = length;
    return true;
}

static NPClass scriptableClass = {
    .structVersion = 3, .allocate = testAllocate, .deallocate = scriptableDeallocate,
    .hasMethod = (void *)scriptableHasMethod, .invoke = (void *)scriptableInvoke
};

/* ------------------------------------------------------------------ */
/* Scripts et objets de la page                                        */
/* ------------------------------------------------------------------ */

static void reportString(const char *key, NPVariant *value)
{
    if (value->type == NPVariantType_String)
        report("%s=%.*s", key, (int)value->value.stringValue.UTF8Length, value->value.stringValue.UTF8Characters);
    else
        report("%s=(type %d)", key, (int)value->type);
}

static NPObject *getObject(NPObject *owner, const char *name)
{
    NPVariant value;
    if (!browser->getproperty(instanceNpp, owner, browser->getstringidentifier(name), &value) || value.type != NPVariantType_Object)
        return NULL;
    return value.value.objectValue; /* la référence reçue est rendue par l'appelant */
}

static void inspectPage(void)
{
    NPObject *window = NULL;
    if (browser->getvalue(instanceNpp, NPNVWindowNPObject, &window) != NPERR_NO_ERROR || !window)
    {
        report("window=absent");
        return;
    }

    const char *script = "top.location+\"__flashplugin_unique__\"";
    NPString code = { script, (uint32_t)strlen(script) };
    NPVariant result;
    if (browser->evaluate(instanceNpp, window, &code, &result))
    {
        reportString("evaluate", &result);
        browser->releasevariantvalue(&result);
    }

    NPObject *location = getObject(window, "location");
    if (location)
    {
        NPVariant href;
        if (browser->getproperty(instanceNpp, location, browser->getstringidentifier("href"), &href))
        {
            reportString("href", &href);
            browser->releasevariantvalue(&href);
        }
        browser->releaseobject(location);
    }

    NPObject *document = getObject(window, "document");
    if (document)
    {
        NPVariant domain;
        if (browser->getproperty(instanceNpp, document, browser->getstringidentifier("domain"), &domain))
        {
            reportString("domain", &domain);
            browser->releasevariantvalue(&domain);
        }
        browser->releaseobject(document);
    }
    browser->releaseobject(window);

    NPObject *element = NULL;
    if (browser->getvalue(instanceNpp, NPNVPluginElementNPObject, &element) == NPERR_NO_ERROR && element)
    {
        NPVariant id;
        if (browser->getproperty(instanceNpp, element, browser->getstringidentifier("id"), &id))
        {
            reportString("element-id", &id);
            browser->releasevariantvalue(&id);
        }
        browser->releaseobject(element);
    }
}

static void checkIdentifiers(void)
{
    NPIdentifier first = browser->getstringidentifier("abc");
    NPIdentifier second = browser->getstringidentifier("abc");
    NPUTF8 *name = browser->utf8fromidentifier(first);
    report("ident same=%d name=%s", first == second, name ? name : "(null)");
    browser->memfree(name);

    NPIdentifier number = browser->getintidentifier(42);
    report("ident-int string=%d value=%d", browser->identifierisstring(number) ? 1 : 0, browser->intfromidentifier(number));
}

static void checkObjects(void)
{
    NPObject *obj = browser->createobject(instanceNpp, &testClass);
    browser->retainobject(obj);
    int afterRetain = (int)obj->referenceCount;
    browser->releaseobject(obj);
    browser->releaseobject(obj);
    report("object refs=%d deallocated=%d", afterRetain, deallocations);
}

/* ------------------------------------------------------------------ */
/* Appels asynchrones et minuterie                                     */
/* ------------------------------------------------------------------ */

static void asyncCallback(void *data)
{
    report("async main=%d data=%llx", onMainThread(), (unsigned long long)(uintptr_t)data);
    asyncDone = 1;
    checkFinished();
}

#ifdef _WIN32
static DWORD WINAPI workerThread(LPVOID unused)
{
    (void)unused;
    browser->pluginthreadasynccall(instanceNpp, asyncCallback, (void *)0xC0FFEE);
    return 0;
}

static void startWorker(void)
{
    CloseHandle(CreateThread(NULL, 0, workerThread, NULL, 0, NULL));
}
#else
static void *workerThread(void *unused)
{
    (void)unused;
    browser->pluginthreadasynccall(instanceNpp, asyncCallback, (void *)0xC0FFEE);
    return NULL;
}

static void startWorker(void)
{
    pthread_t thread;
    if (pthread_create(&thread, NULL, workerThread, NULL) == 0)
        pthread_detach(thread);
}
#endif

static void timerCallback(NPP npp, uint32_t id)
{
    (void)npp;
    if (id != timerId)
        return;
    if (++timerTicks == 3)
    {
        browser->unscheduletimer(instanceNpp, timerId);
        report("timer ticks=%d main=%d", timerTicks, onMainThread());
        checkFinished();
    }
}

/* Script de la page, comme ExternalInterface.call : exécuté par PommeBrowser, qui répond. */
static void evaluateScript(const char *script, const char *key)
{
    NPObject *window = NULL;
    if (browser->getvalue(instanceNpp, NPNVWindowNPObject, &window) != NPERR_NO_ERROR || !window)
        return;
    NPString code = { script, (uint32_t)strlen(script) };
    NPVariant result;
    if (browser->evaluate(instanceNpp, window, &code, &result))
    {
        reportString(key, &result);
        browser->releasevariantvalue(&result);
    }
    else
    {
        report("%s=failed", key);
    }
    browser->releaseobject(window);
}

/* Contenu principal reçu : le reste du scénario. */
typedef NPError (*GetValueForUrlFunc)(NPP npp, int variable, const char *url, char **value, uint32_t *length);
typedef NPError (*SetValueForUrlFunc)(NPP npp, int variable, const char *url, const char *value, uint32_t length);

/* Cookies de la page (NPN_GetValueForURL, NPNURLVCookie = 501), puis un cookie posé par le greffon. */
static void checkCookies(const char *url)
{
    char *value = NULL;
    uint32_t length = 0;
    NPError error = ((GetValueForUrlFunc)browser->getvalueforurl)(instanceNpp, 501, url, &value, &length);
    if (error == NPERR_NO_ERROR && value)
    {
        report("url-cookie=%.*s", (int)length, value);
        browser->memfree(value);
    }
    else
    {
        report("url-cookie-error=%d", error);
    }
    const char *cookie = "pose=1; Path=/";
    report("set-cookie=%d", ((SetValueForUrlFunc)browser->setvalueforurl)(instanceNpp, 501, url, cookie, (uint32_t)strlen(cookie)));
}

#ifdef _WIN32
/* Clic dans la fenêtre du greffon, tel que Windows le signale (WM_MOUSEACTIVATE, remonté aux
   parents) : l'hôte doit lui donner le clavier, comme un navigateur. */
static void checkClickFocus(void)
{
    SetFocus(NULL);
    SendMessageA(child, WM_MOUSEACTIVATE, (WPARAM)GetAncestor(child, GA_ROOT), MAKELONG(HTCLIENT, WM_LBUTTONDOWN));
    report("click-focus=%d", child != NULL && GetFocus() == child);
}
#else
/* Le clavier est-il dans le cadre de l'hôte (lui ou une de ses fenêtres) ? */
static int focusInFrame(void)
{
    Window focus = None;
    int revert;
    XGetInputFocus(xdisplay, &focus, &revert);
    for (int depth = 0; focus > 1 && depth < 64; depth++)
    {
        if (focus == frame)
            return 1;
        Window root, parent, *children = NULL;
        unsigned int count;
        if (!XQueryTree(xdisplay, focus, &root, &parent, &children, &count))
            return 0;
        if (children)
            XFree(children);
        if (parent == root)
            return 0;
        focus = parent;
    }
    return 0;
}

static gboolean onPlugKey(GtkWidget *widget, GdkEventKey *event, gpointer data)
{
    (void)widget;
    (void)data;
    report("plug-key=%u", event->keyval);
    keyDone = 1;
    checkFinished();
    return TRUE;
}

/* Étape 3 : le clavier doit être revenu dans le cadre ; le pointeur part ailleurs, puis une
   touche est tapée (XTEST) : elle doit arriver à la fenêtre du greffon. */
static gboolean focusStep3(gpointer data)
{
    (void)data;
    report("click-focus=%d", focusInFrame());
    XTestFakeMotionEvent(xdisplay, -1, 900, 700, 0);
    KeyCode key = XKeysymToKeycode(xdisplay, XK_a);
    XTestFakeKeyEvent(xdisplay, key, True, 0);
    XTestFakeKeyEvent(xdisplay, key, False, 0);
    XFlush(xdisplay);
    return FALSE;
}

/* Étape 2 : le clavier est ailleurs (une autre fenêtre) ; clic de l'utilisateur dans le greffon (XTEST). */
static gboolean focusStep2(gpointer data)
{
    (void)data;
    XSetInputFocus(xdisplay, GDK_WINDOW_XID(gtk_widget_get_window(decoy)), RevertToParent, CurrentTime);
    XSync(xdisplay, False);
    report("focus-away=%d", !focusInFrame());
    gint x = 0, y = 0;
    gdk_window_get_origin(gtk_widget_get_window(plug), &x, &y);
    XTestFakeMotionEvent(xdisplay, -1, x + 20, y + 20, 0);
    XTestFakeButtonEvent(xdisplay, 1, True, 0);
    XTestFakeButtonEvent(xdisplay, 1, False, 0);
    XFlush(xdisplay);
    g_timeout_add(300, focusStep3, NULL);
    return FALSE;
}

/* Comme un utilisateur : le clavier est dans une autre fenêtre, il clique dans le contenu puis tape. */
static void checkClickFocus(void)
{
    void *netscapeWindow = NULL;
    browser->getvalue(instanceNpp, NPNVnetscapeWindow, &netscapeWindow);
    frame = (Window)(uintptr_t)netscapeWindow;
    if (!plug || !xdisplay || !frame)
    {
        report("click-focus=0");
        keyDone = 1;
        return;
    }
    gint x = 0, y = 0;
    gdk_window_get_origin(gtk_widget_get_window(plug), &x, &y);
    if (x < 0 || y < 0 || x + 20 >= gdk_screen_width() || y + 20 >= gdk_screen_height())
    {
        /* Fenêtre cachée (hors de l'écran, en attendant que le navigateur la loge) : pas de clic possible. */
        report("click-focus=skipped");
        keyDone = 1;
        return;
    }
    decoy = gtk_window_new(GTK_WINDOW_TOPLEVEL);
    gtk_window_move(GTK_WINDOW(decoy), 600, 500);
    gtk_window_set_default_size(GTK_WINDOW(decoy), 50, 50);
    gtk_widget_show(decoy);
    g_timeout_add(200, focusStep2, NULL);
}
#endif

static void continueScenario(const char *movieUrl)
{
    checkClickFocus();
    evaluateScript("try { __flash__toXML(pommeAdd(2,3)) ; } catch (e) { \"<undefined/>\"; }", "script");
    evaluateScript("pommeRefuse()", "script-refused");
    checkCookies(movieUrl);
    browser->geturlnotify(instanceNpp, "data.txt", NULL, (void *)0x1234);
    browser->geturlnotify(instanceNpp, "missing.txt", NULL, (void *)0x5678);
    const char *post = "Content-Type: text/plain\r\nContent-Length: 5\r\n\r\nhello";
    browser->posturlnotify(instanceNpp, "echo", NULL, (uint32_t)strlen(post), post, 0, (void *)0x9ABC);
    browser->geturl(instanceNpp, "https://example.org/page", "_blank");
    browser->geturl(instanceNpp, "javascript:window.alert('pomme')", NULL);
    timerId = browser->scheduletimer(instanceNpp, 30, 1, timerCallback);
    startWorker();
}

/* ------------------------------------------------------------------ */
/* Fonctions du greffon                                                */
/* ------------------------------------------------------------------ */

typedef struct { uint32_t hash; uint32_t bytes; int ordered; } StreamState;

static NPError NPP_New(NPMIMEType type, NPP npp, uint16_t mode, int16_t argc, char **argn, char **argv, NPSavedData *saved)
{
    (void)saved;
    instanceNpp = npp;
    mainThread = currentThread();
    report("init size=%u version=%u", browserSize, browserVersion);
#ifndef _WIN32
    /* Demandés à NP_Initialize, sans instance, comme Flash le fait. */
    report("toolkit=%d xembed=%d", toolkit, xembed);
#endif
    report("new mime=%s mode=%u argc=%d", type, mode, argc);
    for (int i = 0; i < argc; i++)
        report("arg %s=%s", argn[i], argv[i] ? argv[i] : "(null)");

    report("ua=%s", browser->uagent(npp));

    char *origin = NULL;
    if (browser->getvalue(npp, NPNVdocumentOrigin, &origin) == NPERR_NO_ERROR && origin)
    {
        report("origin=%s", origin);
        browser->memfree(origin);
    }
    NPBool flag = 0;
    browser->getvalue(npp, NPNVjavascriptEnabledBool, &flag);
    report("javascript=%d", flag);
    flag = 1;
    browser->getvalue(npp, NPNVSupportsWindowless, &flag);
    report("windowless=%d", flag);

    inspectPage();
    checkIdentifiers();
    checkObjects();

#ifdef _WIN32
    /* Comme une boîte de dialogue de Flash : une fenêtre à part, avec un texte, que l'hôte note
       dans son journal. Hors de l'écran et sans prendre le clavier. */
    dialog = CreateWindowExA(WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE, "STATIC", "TEST dialogue", WS_POPUP | WS_VISIBLE,
                             -3000, -3000, 240, 80, NULL, NULL, NULL, NULL);
    if (dialog)
        CreateWindowExA(0, "STATIC", "Texte du dialogue de test", WS_CHILD | WS_VISIBLE, 0, 0, 240, 40, dialog, NULL, NULL, NULL);
#endif
    return NPERR_NO_ERROR;
}

static NPError NPP_Destroy(NPP npp, NPSavedData **save)
{
    (void)npp;
    (void)save;
    report("destroy");
    if (scriptable)
    {
        browser->releaseobject(scriptable);
        scriptable = NULL;
    }
#ifdef _WIN32
    if (dialog)
    {
        DestroyWindow(dialog);
        dialog = NULL;
    }
#else
    if (plug)
    {
        gtk_widget_destroy(plug);
        plug = NULL;
    }
    if (decoy)
    {
        gtk_widget_destroy(decoy);
        decoy = NULL;
    }
#endif
    return NPERR_NO_ERROR;
}

static NPError NPP_SetWindow(NPP npp, NPWindow *window)
{
    (void)npp;
#ifdef _WIN32
    HWND parent = (HWND)window->window;
    report("window valid=%d width=%u height=%u type=%d", parent != NULL && IsWindow(parent), window->width, window->height, window->type);
    /* Comme Flash : une fenêtre à lui dans celle du navigateur. */
    if (!child && parent)
        child = CreateWindowExA(0, "STATIC", "Greffon de test PommeFlash", WS_CHILD | WS_VISIBLE, 0, 0, (int)window->width, (int)window->height, parent, NULL, NULL, NULL);
    else if (child)
        MoveWindow(child, 0, 0, (int)window->width, (int)window->height, TRUE);
#else
    /* Comme Flash sous Linux : la fenêtre donnée est une prise XEmbed (GtkSocket) du navigateur,
       décrite avec l'affichage X11 (ws_info) ; le greffon y branche un GtkPlug, avec la GTK 2 que
       l'hôte a chargée (même processus : la prise l'adopte directement). */
    NPSetWindowCallbackStruct *info = (NPSetWindowCallbackStruct *)window->ws_info;
    Display *display = info ? (Display *)info->display : NULL;
    Window parent = (Window)(uintptr_t)window->window;
    XWindowAttributes attributes;
    int valid = display && parent && XGetWindowAttributes(display, parent, &attributes);
    report("window valid=%d width=%u height=%u type=%d", valid, window->width, window->height, window->type);
    if (info)
    {
        void *shared = NULL;
        browser->getvalue(instanceNpp, NPNVxDisplay, &shared);
        report("ws-info type=%d display=%d visual=%d depth=%u", info->type, shared != NULL && shared == info->display,
               info->visual != NULL, info->depth);
    }
    if (valid && !plug)
    {
        xdisplay = display;
        /* Rien de focalisable dedans : les touches doivent quand même arriver à la fenêtre. */
        plug = gtk_plug_new((GdkNativeWindow)parent);
        GtkWidget *area = gtk_drawing_area_new();
        gtk_widget_add_events(area, GDK_BUTTON_PRESS_MASK | GDK_BUTTON_RELEASE_MASK); /* la souris, comme Flash */
        gtk_container_add(GTK_CONTAINER(plug), area);
        g_signal_connect(plug, "key-press-event", G_CALLBACK(onPlugKey), NULL);
        gtk_widget_show_all(plug);
        report("plug embedded=%d", gtk_plug_get_embedded(GTK_PLUG(plug)) ? 1 : 0);
    }
#endif
    return NPERR_NO_ERROR;
}

static NPError NPP_NewStream(NPP npp, NPMIMEType type, NPStream *stream, NPBool seekable, uint16_t *stype)
{
    (void)npp;
    (void)seekable;
    StreamState *state = (StreamState *)browser->memalloc(sizeof(StreamState));
    state->hash = 2166136261u;
    state->bytes = 0;
    state->ordered = 1;
    stream->pdata = state;
    *stype = NP_NORMAL;

    char firstLine[128] = "";
    if (stream->headers)
    {
        size_t length = strcspn(stream->headers, "\r\n");
        if (length >= sizeof(firstLine))
            length = sizeof(firstLine) - 1;
        memcpy(firstLine, stream->headers, length);
        firstLine[length] = 0;
    }
    report("stream-open url=%s mime=%s end=%u notify=%llx status=%s", stream->url, type, stream->end,
           (unsigned long long)(uintptr_t)stream->notifyData, firstLine);
    return NPERR_NO_ERROR;
}

/* Petites bouchées : l'hôte doit respecter ce rythme. */
static int32_t NPP_WriteReady(NPP npp, NPStream *stream)
{
    (void)npp;
    (void)stream;
    return 1000;
}

static int32_t NPP_Write(NPP npp, NPStream *stream, int32_t offset, int32_t length, void *buffer)
{
    (void)npp;
    StreamState *state = (StreamState *)stream->pdata;
    if (length > 1000 || (uint32_t)offset != state->bytes)
        state->ordered = 0;
    state->hash = fnv1a(state->hash, (const unsigned char *)buffer, length);
    state->bytes += (uint32_t)length;
    return length;
}

static NPError NPP_DestroyStream(NPP npp, NPStream *stream, NPReason reason)
{
    (void)npp;
    StreamState *state = (StreamState *)stream->pdata;
    report("stream-done url=%s bytes=%u hash=%08x ordered=%d reason=%d", stream->url, state->bytes, state->hash, state->ordered, reason);
    int isMovie = strstr(stream->url, "movie.swf") != NULL;
    browser->memfree(state);
    stream->pdata = NULL;
    streamsDone++;
    if (isMovie)
        continueScenario(stream->url);
    checkFinished();
    return NPERR_NO_ERROR;
}

static void NPP_URLNotify(NPP npp, const char *url, NPReason reason, void *notifyData)
{
    (void)npp;
    report("notify url=%s reason=%d data=%llx", url, reason, (unsigned long long)(uintptr_t)notifyData);
    notifications++;
    checkFinished();
}

static NPError NPP_GetValue(NPP npp, NPPVariable variable, void *value)
{
    if (variable == NPPVpluginNameString)
    {
        *(const char **)value = "Pomme Test";
        return NPERR_NO_ERROR;
    }
    if (variable == NPPVpluginDescriptionString)
    {
        *(const char **)value = "Greffon NPAPI de test de PommeFlashHost";
        return NPERR_NO_ERROR;
    }
    if (variable == NPPVpluginScriptableNPObject && npp)
    {
        /* Une référence gardée par le greffon, une donnée au navigateur. */
        if (!scriptable)
            scriptable = browser->createobject(npp, &scriptableClass);
        *(NPObject **)value = browser->retainobject(scriptable);
        return NPERR_NO_ERROR;
    }
    return NPERR_GENERIC_ERROR;
}

static NPError NPP_SetValue(NPP npp, NPNVariable variable, void *value)
{
    (void)npp;
    (void)variable;
    (void)value;
    return NPERR_GENERIC_ERROR;
}

static void fillPluginFuncs(NPPluginFuncs *funcs)
{
    funcs->version = 29;
    funcs->newp = NPP_New;
    funcs->destroy = NPP_Destroy;
    funcs->setwindow = NPP_SetWindow;
    funcs->newstream = NPP_NewStream;
    funcs->destroystream = NPP_DestroyStream;
    funcs->writeready = NPP_WriteReady;
    funcs->write = NPP_Write;
    funcs->urlnotify = NPP_URLNotify;
    funcs->getvalue = NPP_GetValue;
    funcs->setvalue = NPP_SetValue;
}

#ifdef _WIN32
NP_EXPORT NPError WINAPI NP_GetEntryPoints(NPPluginFuncs *funcs)
{
    if (!funcs || funcs->size < sizeof(NPPluginFuncs))
        return 3; /* NPERR_INVALID_FUNCTABLE_ERROR */
    fillPluginFuncs(funcs);
    return NPERR_NO_ERROR;
}

NP_EXPORT NPError WINAPI NP_Initialize(NPNetscapeFuncs *funcs)
{
    if (!funcs)
        return 3;
    browser = funcs;
    browserSize = funcs->size;
    browserVersion = funcs->version;
    return NPERR_NO_ERROR;
}
#else
/* Linux : une seule entrée reçoit la table du navigateur et remplit celle du module. */
NP_EXPORT NPError NP_Initialize(NPNetscapeFuncs *funcs, NPPluginFuncs *pluginFuncs)
{
    if (!funcs || !pluginFuncs || pluginFuncs->size < sizeof(NPPluginFuncs))
        return 3;
    browser = funcs;
    browserSize = funcs->size;
    browserVersion = funcs->version;
    fillPluginFuncs(pluginFuncs);
    /* Comme Flash : boîte à outils (GTK 2 attendu) et XEmbed demandés dès maintenant, sans instance. */
    browser->getvalue(NULL, NPNVToolkit, &toolkit);
    browser->getvalue(NULL, NPNVSupportsXEmbedBool, &xembed);
    return NPERR_NO_ERROR;
}

NP_EXPORT const char *NP_GetMIMEDescription(void)
{
    return "application/x-shockwave-flash:swf:Shockwave Flash";
}

NP_EXPORT NPError NP_GetValue(void *instance, NPPVariable variable, void *value)
{
    (void)instance;
    return NPP_GetValue(NULL, variable, value);
}
#endif

NP_EXPORT NPError WINAPI NP_Shutdown(void)
{
    return NPERR_NO_ERROR;
}
