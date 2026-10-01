/*
 * Greffon NPAPI de test pour PommeFlashHost. Il se comporte comme un module Flash vis-à-vis de
 * l'hôte (mêmes points d'entrée, mêmes appels) et rapporte ce qu'il observe par NPN_Status,
 * sous la forme « TEST clé=valeur », que les tests vérifient.
 *
 * Compilation : x86_64-w64-mingw32-gcc -shared -O2 -o npPommeTest.dll testplugin.c
 *               (32 bits : i686-w64-mingw32-gcc … -Wl,--kill-at)
 *           ou  clang [--target=i686-pc-windows-msvc] -shared -O2 -o npPommeTest.dll testplugin.c -luser32
 */
#include <windows.h>
#include <stdio.h>
#include <string.h>
#include "npapi-min.h"


static NPNetscapeFuncs *browser;
static uint16_t browserSize, browserVersion;
static DWORD mainThread;
static NPP instanceNpp;
static HWND dialog;
static int notifications, timerTicks, asyncDone, streamsDone, finished;
static uint32_t timerId;

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
    /* Contenu principal + 3 adresses demandées, minuterie et appel asynchrone. */
    if (!finished && notifications >= 3 && streamsDone >= 3 && timerTicks >= 3 && asyncDone)
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
    report("async main=%d data=%llx", GetCurrentThreadId() == mainThread, (unsigned long long)(uintptr_t)data);
    asyncDone = 1;
    checkFinished();
}

static DWORD WINAPI workerThread(LPVOID unused)
{
    (void)unused;
    browser->pluginthreadasynccall(instanceNpp, asyncCallback, (void *)0xC0FFEE);
    return 0;
}

static void timerCallback(NPP npp, uint32_t id)
{
    (void)npp;
    if (id != timerId)
        return;
    if (++timerTicks == 3)
    {
        browser->unscheduletimer(instanceNpp, timerId);
        report("timer ticks=%d main=%d", timerTicks, GetCurrentThreadId() == mainThread);
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

static void continueScenario(const char *movieUrl)
{
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
    CloseHandle(CreateThread(NULL, 0, workerThread, NULL, 0, NULL));
}

/* ------------------------------------------------------------------ */
/* Fonctions du greffon                                                */
/* ------------------------------------------------------------------ */

typedef struct { uint32_t hash; uint32_t bytes; int ordered; } StreamState;

static NPError NPP_New(NPMIMEType type, NPP npp, uint16_t mode, int16_t argc, char **argn, char **argv, NPSavedData *saved)
{
    (void)saved;
    instanceNpp = npp;
    mainThread = GetCurrentThreadId();
    report("init size=%u version=%u", browserSize, browserVersion);
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

    /* Comme une boîte de dialogue de Flash : une fenêtre à part, avec un texte, que l'hôte note
       dans son journal. Hors de l'écran et sans prendre le clavier. */
    dialog = CreateWindowExA(WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE, "STATIC", "TEST dialogue", WS_POPUP | WS_VISIBLE,
                             -3000, -3000, 240, 80, NULL, NULL, NULL, NULL);
    if (dialog)
        CreateWindowExA(0, "STATIC", "Texte du dialogue de test", WS_CHILD | WS_VISIBLE, 0, 0, 240, 40, dialog, NULL, NULL, NULL);
    return NPERR_NO_ERROR;
}

static NPError NPP_Destroy(NPP npp, NPSavedData **save)
{
    (void)npp;
    (void)save;
    report("destroy");
    if (dialog)
    {
        DestroyWindow(dialog);
        dialog = NULL;
    }
    return NPERR_NO_ERROR;
}

static NPError NPP_SetWindow(NPP npp, NPWindow *window)
{
    (void)npp;
    static HWND child;
    HWND parent = (HWND)window->window;
    report("window valid=%d width=%u height=%u type=%d", parent != NULL && IsWindow(parent), window->width, window->height, window->type);
    /* Comme Flash : une fenêtre à lui dans celle du navigateur. */
    if (!child && parent)
        child = CreateWindowExA(0, "STATIC", "Greffon de test PommeFlash", WS_CHILD | WS_VISIBLE, 0, 0, (int)window->width, (int)window->height, parent, NULL, NULL, NULL);
    else if (child)
        MoveWindow(child, 0, 0, (int)window->width, (int)window->height, TRUE);
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
    (void)npp;
    if (variable == NPPVpluginNameString)
    {
        *(const char **)value = "Pomme Test";
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

NP_EXPORT NPError WINAPI NP_GetEntryPoints(NPPluginFuncs *funcs)
{
    if (!funcs || funcs->size < sizeof(NPPluginFuncs))
        return 3; /* NPERR_INVALID_FUNCTABLE_ERROR */
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

NP_EXPORT NPError WINAPI NP_Shutdown(void)
{
    return NPERR_NO_ERROR;
}
