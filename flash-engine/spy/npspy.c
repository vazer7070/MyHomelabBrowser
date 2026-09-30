/*
 * Espion NPAPI de PommeBrowser : se place entre un navigateur (Basilisk, PommeFlashHost) et le
 * vrai module Flash, et note chaque échange entre eux dans un journal. Même module, même page :
 * les deux journaux se comparent ligne à ligne, et montrent ce que Basilisk fait autrement.
 *
 * Installation (voir Espion-Flash.ps1) : l'espion prend le nom du module dans le dossier des
 * modules, le vrai module est rangé dans le sous-dossier « espion ». L'espion charge
 * espion\<son propre nom> et écrit ses journaux dans espion\journaux.
 *
 * Aucune donnée n'est envoyée nulle part. Les valeurs sensibles sont masquées : valeurs des
 * flashvars et des paramètres d'adresse, cookies (noms seuls), données envoyées (taille seule),
 * identifiants de connexion (jamais lus).
 */
#include <windows.h>
#include <stdarg.h>
#include <stddef.h>
#include <stdio.h>
#include <string.h>
#include "../test-plugin/npapi-min.h"

#define MAX_LINES 40000
#define MAX_TEXT 400

/* Points d'entrée du vrai module : __stdcall en 32 bits (sans effet en 64 bits). */
typedef NPError (WINAPI *GetEntryPointsFn)(NPPluginFuncs *);
typedef NPError (WINAPI *InitializeFn)(NPNetscapeFuncs *);
typedef NPError (WINAPI *ShutdownFn)(void);

static HMODULE self;
static HMODULE real;
static FILE *logFile;
static CRITICAL_SECTION logLock;
static DWORD startTick;
static long lines;
static NPNetscapeFuncs browser;  /* table du navigateur */
static NPNetscapeFuncs wrapped;  /* table donnée au module */
static NPPluginFuncs module;     /* table du module */

/* ------------------------------------------------------------------ */
/* Journal                                                             */
/* ------------------------------------------------------------------ */

/* Journal dans <dossier>, créé au besoin ; faux si le fichier ne peut pas être créé. */
static int openLogIn(const WCHAR *dir)
{
    WCHAR exe[MAX_PATH], file[MAX_PATH];
    CreateDirectoryW(dir, NULL);
    GetModuleFileNameW(NULL, exe, MAX_PATH);
    WCHAR *exeName = wcsrchr(exe, L'\\');
    exeName = exeName ? exeName + 1 : exe;
    SYSTEMTIME now;
    GetLocalTime(&now);
    _snwprintf(file, MAX_PATH, L"%ls\\%04d%02d%02d-%02d%02d%02d-%ls-%lu.log", dir,
               now.wYear, now.wMonth, now.wDay, now.wHour, now.wMinute, now.wSecond, exeName, GetCurrentProcessId());
    logFile = _wfopen(file, L"wb");
    if (!logFile)
        return 0;
    fputs("\xEF\xBB\xBF", logFile); /* UTF-8 */
    return 1;
}

/*
 * Journal à côté du vrai module (espion\journaux). Dossier protégé (module de Windows dans
 * System32, navigateur lancé sans droits d'administrateur) : %LOCALAPPDATA%\PommeBrowser\espion-journaux.
 */
static void openLog(void)
{
    if (logFile)
        return;
    WCHAR path[MAX_PATH], dir[MAX_PATH];
    if (GetModuleFileNameW(self, path, MAX_PATH))
    {
        WCHAR *slash = wcsrchr(path, L'\\');
        if (slash)
        {
            *slash = 0;
            _snwprintf(dir, MAX_PATH, L"%ls\\espion\\journaux", path);
            if (openLogIn(dir))
                return;
        }
    }
    WCHAR local[MAX_PATH];
    DWORD length = GetEnvironmentVariableW(L"LOCALAPPDATA", local, MAX_PATH);
    if (length == 0 || length >= MAX_PATH)
        return;
    _snwprintf(dir, MAX_PATH, L"%ls\\PommeBrowser", local);
    CreateDirectoryW(dir, NULL);
    _snwprintf(dir, MAX_PATH, L"%ls\\PommeBrowser\\espion-journaux", local);
    openLogIn(dir);
}

static void spy(const char *format, ...)
{
    if (!logFile)
        return;
    EnterCriticalSection(&logLock);
    if (lines < MAX_LINES)
    {
        fprintf(logFile, "%8lu [%5lu] ", GetTickCount() - startTick, GetCurrentThreadId());
        va_list args;
        va_start(args, format);
        vfprintf(logFile, format, args);
        va_end(args);
        fputs("\r\n", logFile);
        if (++lines == MAX_LINES)
            fputs("… journal plein\r\n", logFile);
        fflush(logFile);
    }
    LeaveCriticalSection(&logLock);
}

/* Chemin Windows en UTF-8, pour le journal. */
static const char *utf8(char *out, int cap, const WCHAR *value)
{
    if (!WideCharToMultiByte(CP_UTF8, 0, value, -1, out, cap, NULL, NULL))
        snprintf(out, (size_t)cap, "(chemin illisible)");
    return out;
}

/* Texte sur une ligne : retours à la ligne et tabulations échappés, longueur bornée. */
static const char *text(char *out, size_t cap, const char *value, size_t length, size_t max)
{
    if (!value)
        return "(null)";
    size_t o = 0;
    for (size_t i = 0; i < length && o + 5 < cap; i++)
    {
        if (i >= max)
        {
            o += (size_t)snprintf(out + o, cap - o, "…(%lu)", (unsigned long)length);
            break;
        }
        unsigned char c = (unsigned char)value[i];
        if (c == '\n') { out[o++] = '\\'; out[o++] = 'n'; }
        else if (c == '\r') { out[o++] = '\\'; out[o++] = 'r'; }
        else if (c == '\t') { out[o++] = ' '; }
        else if (c < 0x20) o += (size_t)snprintf(out + o, cap - o, "\\x%02x", c);
        else out[o++] = (char)c;
    }
    out[o < cap ? o : cap - 1] = 0;
    return out;
}

static const char *cstr(char *out, size_t cap, const char *value)
{
    return text(out, cap, value, value ? strlen(value) : 0, MAX_TEXT);
}

/* Adresse sans les valeurs de ses paramètres (?a=…&b=…). */
static const char *url(char *out, size_t cap, const char *value)
{
    if (!value)
        return "(null)";
    const char *query = strchr(value, '?');
    size_t base = query ? (size_t)(query - value) : strlen(value);
    text(out, cap, value, base, MAX_TEXT);
    if (!query)
        return out;
    size_t o = strlen(out);
    const char *p = query + 1;
    out[o++] = '?';
    while (*p && o + 8 < cap)
    {
        const char *end = strchr(p, '&');
        size_t pair = end ? (size_t)(end - p) : strlen(p);
        const char *equals = memchr(p, '=', pair);
        size_t name = equals ? (size_t)(equals - p) : pair;
        for (size_t i = 0; i < name && o + 8 < cap; i++)
            out[o++] = p[i];
        if (equals && pair > name + 1 && o + 8 < cap)
            o += (size_t)snprintf(out + o, cap - o, "=…");
        if (!end)
            break;
        out[o++] = '&';
        p = end + 1;
    }
    out[o] = 0;
    return out;
}

/* Noms seuls (« a=1&b=2 » → « a&b », cookies « a=1; b=2 » → « a; b »). */
static const char *names(char *out, size_t cap, const char *value, char separator)
{
    if (!value)
        return "(null)";
    size_t o = 0;
    int inValue = 0;
    for (const char *p = value; *p && o + 2 < cap; p++)
    {
        if (*p == separator) { inValue = 0; out[o++] = *p; continue; }
        if (*p == '=') { inValue = 1; continue; }
        if (!inValue && (unsigned char)*p >= 0x20)
            out[o++] = *p;
    }
    out[o] = 0;
    return out;
}

/* ------------------------------------------------------------------ */
/* Objets et identifiants                                              */
/* ------------------------------------------------------------------ */

#define LABELS 512
static struct { NPObject *object; char label[96]; } labels[LABELS];
static int nextLabel;
static CRITICAL_SECTION labelLock;

static void label(NPObject *object, const char *name)
{
    if (!object || !name)
        return;
    EnterCriticalSection(&labelLock);
    for (int i = 0; i < LABELS; i++)
    {
        if (labels[i].object == object)
        {
            LeaveCriticalSection(&labelLock);
            return;
        }
    }
    labels[nextLabel].object = object;
    snprintf(labels[nextLabel].label, sizeof labels[nextLabel].label, "%s", name);
    nextLabel = (nextLabel + 1) % LABELS;
    LeaveCriticalSection(&labelLock);
}

static const char *objectName(char *out, size_t cap, NPObject *object)
{
    if (!object)
        return "objet(null)";
    EnterCriticalSection(&labelLock);
    for (int i = 0; i < LABELS; i++)
    {
        if (labels[i].object == object)
        {
            snprintf(out, cap, "%s", labels[i].label);
            LeaveCriticalSection(&labelLock);
            return out;
        }
    }
    LeaveCriticalSection(&labelLock);
    snprintf(out, cap, "objet(%p)", (void *)object);
    return out;
}

static const char *identifier(char *out, size_t cap, NPIdentifier id)
{
    if (!id || !browser.identifierisstring)
        return "(null)";
    if (browser.identifierisstring(id))
    {
        NPUTF8 *name = browser.utf8fromidentifier(id);
        cstr(out, cap, name);
        if (name)
            browser.memfree(name);
        return out;
    }
    snprintf(out, cap, "#%d", browser.intfromidentifier(id));
    return out;
}

static const char *variant(char *out, size_t cap, const NPVariant *value)
{
    if (!value)
        return "(aucun)";
    char buffer[MAX_TEXT + 64];
    switch (value->type)
    {
    case NPVariantType_Void: return "undefined";
    case NPVariantType_Null: return "null";
    case NPVariantType_Bool: return value->value.boolValue ? "true" : "false";
    case NPVariantType_Int32: snprintf(out, cap, "%d", value->value.intValue); return out;
    case NPVariantType_Double: snprintf(out, cap, "%g", value->value.doubleValue); return out;
    case NPVariantType_String:
        snprintf(out, cap, "\"%s\"", text(buffer, sizeof buffer, value->value.stringValue.UTF8Characters,
                                          value->value.stringValue.UTF8Length, 200));
        return out;
    case NPVariantType_Object: return objectName(out, cap, value->value.objectValue);
    default: snprintf(out, cap, "(type %d)", (int)value->type); return out;
    }
}

static void arguments(char *out, size_t cap, const NPVariant *args, uint32_t count)
{
    size_t o = 0;
    out[0] = 0;
    for (uint32_t i = 0; i < count && o + 4 < cap; i++)
    {
        char one[MAX_TEXT + 64];
        o += (size_t)snprintf(out + o, cap - o, "%s%s", i ? ", " : "", variant(one, sizeof one, &args[i]));
    }
}

/* ------------------------------------------------------------------ */
/* Fonctions du navigateur, appelées par le module (NPN_*)             */
/* ------------------------------------------------------------------ */

static NPError spyGetURL(NPP npp, const char *address, const char *target)
{
    char u[MAX_TEXT + 64], t[128];
    NPError result = browser.geturl(npp, address, target);
    spy("NPN_GetURL(%s, cible %s) -> %d", url(u, sizeof u, address), cstr(t, sizeof t, target), result);
    return result;
}

static NPError spyPostURL(NPP npp, const char *address, const char *target, uint32_t length, const char *buffer, NPBool file)
{
    char u[MAX_TEXT + 64], t[128];
    NPError result = browser.posturl(npp, address, target, length, buffer, file);
    spy("NPN_PostURL(%s, cible %s, %u octets, fichier %d) -> %d", url(u, sizeof u, address), cstr(t, sizeof t, target), length, file, result);
    return result;
}

static NPError spyGetURLNotify(NPP npp, const char *address, const char *target, void *notifyData)
{
    char u[MAX_TEXT + 64], t[128];
    NPError result = browser.geturlnotify(npp, address, target, notifyData);
    spy("NPN_GetURLNotify(%s, cible %s, %p) -> %d", url(u, sizeof u, address), cstr(t, sizeof t, target), notifyData, result);
    return result;
}

static NPError spyPostURLNotify(NPP npp, const char *address, const char *target, uint32_t length, const char *buffer, NPBool file, void *notifyData)
{
    char u[MAX_TEXT + 64], t[128], headers[MAX_TEXT + 64] = "";
    /* En-têtes seuls (avant la ligne vide) : le corps peut contenir des données personnelles. */
    if (buffer && !file)
    {
        const char *end = strstr(buffer, "\r\n\r\n");
        size_t head = end && (size_t)(end - buffer) < length ? (size_t)(end - buffer) : 0;
        text(headers, sizeof headers, buffer, head, 300);
    }
    NPError result = browser.posturlnotify(npp, address, target, length, buffer, file, notifyData);
    spy("NPN_PostURLNotify(%s, cible %s, %u octets, fichier %d, en-têtes [%s], %p) -> %d",
        url(u, sizeof u, address), cstr(t, sizeof t, target), length, file, headers, notifyData, result);
    return result;
}

static NPError spyDestroyStream(NPP npp, NPStream *stream, NPReason reason)
{
    char u[MAX_TEXT + 64];
    NPError result = browser.destroystream(npp, stream, reason);
    spy("NPN_DestroyStream(%s, raison %d) -> %d", url(u, sizeof u, stream ? stream->url : NULL), reason, result);
    return result;
}

static void spyStatus(NPP npp, const char *message)
{
    char m[MAX_TEXT + 64];
    spy("NPN_Status(%s)", cstr(m, sizeof m, message));
    browser.status(npp, message);
}

static const char *spyUserAgent(NPP npp)
{
    static int calls;
    const char *result = browser.uagent(npp);
    if (++calls <= 3)
    {
        char a[MAX_TEXT + 64];
        spy("NPN_UserAgent -> %s", cstr(a, sizeof a, result));
    }
    return result;
}

static const char *browserVariable(int variable)
{
    switch (variable)
    {
    case 1: return "xDisplay";
    case 3: return "netscapeWindow";
    case 4: return "javascriptEnabledBool";
    case 5: return "asdEnabledBool";
    case 6: return "isOfflineBool";
    case 14: return "SupportsXEmbedBool";
    case 15: return "WindowNPObject";
    case 16: return "PluginElementNPObject";
    case 17: return "SupportsWindowless";
    case 18: return "privateModeBool";
    case 21: return "supportsAdvancedKeyHandling";
    case 22: return "documentOrigin";
    case 23: return "CSSZoomFactor";
    case 1000: return "pluginDrawingModel";
    case 1001: return "contentsScaleFactor";
    case 2007: return "supportsAsyncBitmapSurfaceBool";
    case 2008: return "supportsAsyncWindowsDXGISurfaceBool";
    case 2009: return "preferredDXGIAdapter";
    case 4000: return "muteAudioBool";
    default:
        if (variable == (13 | (1 << 29)) || variable == 13) return "Toolkit";
        return "?";
    }
}

static NPError spyGetValue(NPP npp, NPNVariable variable, void *value)
{
    NPError result = browser.getvalue(npp, variable, value);
    char shown[MAX_TEXT + 64] = "";
    if (result == NPERR_NO_ERROR && value)
    {
        switch ((int)variable)
        {
        case 4: case 5: case 6: case 14: case 17: case 18: case 21: case 2007: case 2008: case 4000:
            snprintf(shown, sizeof shown, "%d", *(NPBool *)value);
            break;
        case 23: case 1001:
            snprintf(shown, sizeof shown, "%g", *(double *)value);
            break;
        case 1000:
            snprintf(shown, sizeof shown, "%d", *(int *)value);
            break;
        case 3:
            snprintf(shown, sizeof shown, "fenêtre %p", *(void **)value);
            break;
        case 15:
            label(*(NPObject **)value, "window");
            objectName(shown, sizeof shown, *(NPObject **)value);
            break;
        case 16:
            label(*(NPObject **)value, "element");
            objectName(shown, sizeof shown, *(NPObject **)value);
            break;
        case 22:
            cstr(shown, sizeof shown, *(char **)value);
            break;
        default:
            snprintf(shown, sizeof shown, "(brut %p)", *(void **)value);
            break;
        }
    }
    spy("NPN_GetValue(%d %s) -> %d %s", (int)variable, browserVariable((int)variable), result, shown);
    return result;
}

static NPError spySetValue(NPP npp, NPPVariable variable, void *value)
{
    NPError result = browser.setvalue(npp, variable, value);
    spy("NPN_SetValue(%d, %p) -> %d", (int)variable, value, result);
    return result;
}

static void spyInvalidateRect(NPP npp, NPRect *rect)
{
    static long calls;
    if (++calls <= 5 && rect)
        spy("NPN_InvalidateRect(%u,%u %u,%u) [5 premiers]", rect->left, rect->top, rect->right, rect->bottom);
    ((void (*)(NPP, NPRect *))browser.invalidaterect)(npp, rect);
}

static bool spyInvoke(NPP npp, NPObject *object, NPIdentifier method, const NPVariant *args, uint32_t count, NPVariant *result)
{
    char o[128], m[MAX_TEXT + 8], a[1024], r[MAX_TEXT + 64];
    bool ok = browser.invoke(npp, object, method, args, count, result);
    arguments(a, sizeof a, args, count);
    spy("NPN_Invoke(%s.%s(%s)) -> %d %s", objectName(o, sizeof o, object), identifier(m, sizeof m, method), a, ok,
        ok ? variant(r, sizeof r, result) : "");
    return ok;
}

static bool spyInvokeDefault(NPP npp, NPObject *object, const NPVariant *args, uint32_t count, NPVariant *result)
{
    char o[128], a[1024], r[MAX_TEXT + 64];
    bool ok = browser.invokeDefault(npp, object, args, count, result);
    arguments(a, sizeof a, args, count);
    spy("NPN_InvokeDefault(%s(%s)) -> %d %s", objectName(o, sizeof o, object), a, ok, ok ? variant(r, sizeof r, result) : "");
    return ok;
}

static bool spyEvaluate(NPP npp, NPObject *object, NPString *script, NPVariant *result)
{
    char o[128], s[MAX_TEXT + 64], r[MAX_TEXT + 64];
    bool ok = browser.evaluate(npp, object, script, result);
    spy("NPN_Evaluate(%s, %s) -> %d %s", objectName(o, sizeof o, object),
        script ? text(s, sizeof s, script->UTF8Characters, script->UTF8Length, 300) : "(null)", ok,
        ok ? variant(r, sizeof r, result) : "");
    return ok;
}

static bool spyGetProperty(NPP npp, NPObject *object, NPIdentifier name, NPVariant *result)
{
    char o[128], n[MAX_TEXT + 8], r[MAX_TEXT + 64];
    bool ok = browser.getproperty(npp, object, name, result);
    objectName(o, sizeof o, object);
    identifier(n, sizeof n, name);
    if (ok && result && result->type == NPVariantType_Object)
    {
        char path[96];
        snprintf(path, sizeof path, "%.40s.%.40s", o, n);
        label(result->value.objectValue, path);
    }
    spy("NPN_GetProperty(%s.%s) -> %d %s", o, n, ok, ok ? variant(r, sizeof r, result) : "");
    return ok;
}

static bool spySetProperty(NPP npp, NPObject *object, NPIdentifier name, const NPVariant *value)
{
    char o[128], n[MAX_TEXT + 8], v[MAX_TEXT + 64];
    bool ok = ((bool (*)(NPP, NPObject *, NPIdentifier, const NPVariant *))browser.setproperty)(npp, object, name, value);
    spy("NPN_SetProperty(%s.%s = %s) -> %d", objectName(o, sizeof o, object), identifier(n, sizeof n, name), variant(v, sizeof v, value), ok);
    return ok;
}

static bool spyRemoveProperty(NPP npp, NPObject *object, NPIdentifier name)
{
    char o[128], n[MAX_TEXT + 8];
    bool ok = ((bool (*)(NPP, NPObject *, NPIdentifier))browser.removeproperty)(npp, object, name);
    spy("NPN_RemoveProperty(%s.%s) -> %d", objectName(o, sizeof o, object), identifier(n, sizeof n, name), ok);
    return ok;
}

static bool spyHasProperty(NPP npp, NPObject *object, NPIdentifier name)
{
    char o[128], n[MAX_TEXT + 8];
    bool ok = ((bool (*)(NPP, NPObject *, NPIdentifier))browser.hasproperty)(npp, object, name);
    spy("NPN_HasProperty(%s.%s) -> %d", objectName(o, sizeof o, object), identifier(n, sizeof n, name), ok);
    return ok;
}

static bool spyHasMethod(NPP npp, NPObject *object, NPIdentifier name)
{
    char o[128], n[MAX_TEXT + 8];
    bool ok = ((bool (*)(NPP, NPObject *, NPIdentifier))browser.hasmethod)(npp, object, name);
    spy("NPN_HasMethod(%s.%s) -> %d", objectName(o, sizeof o, object), identifier(n, sizeof n, name), ok);
    return ok;
}

static void spySetException(NPObject *object, const NPUTF8 *message)
{
    char o[128], m[MAX_TEXT + 64];
    spy("NPN_SetException(%s, %s)", objectName(o, sizeof o, object), cstr(m, sizeof m, message));
    ((void (*)(NPObject *, const NPUTF8 *))browser.setexception)(object, message);
}

static bool spyEnumerate(NPP npp, NPObject *object, NPIdentifier **ids, uint32_t *count)
{
    char o[128];
    bool ok = ((bool (*)(NPP, NPObject *, NPIdentifier **, uint32_t *))browser.enumerate)(npp, object, ids, count);
    spy("NPN_Enumerate(%s) -> %d (%u)", objectName(o, sizeof o, object), ok, ok && count ? *count : 0);
    return ok;
}

static bool spyConstruct(NPP npp, NPObject *object, const NPVariant *args, uint32_t count, NPVariant *result)
{
    char o[128], a[1024];
    bool ok = ((bool (*)(NPP, NPObject *, const NPVariant *, uint32_t, NPVariant *))browser.construct)(npp, object, args, count, result);
    arguments(a, sizeof a, args, count);
    spy("NPN_Construct(%s(%s)) -> %d", objectName(o, sizeof o, object), a, ok);
    return ok;
}

static NPError spyGetValueForURL(NPP npp, int variable, const char *address, char **value, uint32_t *length)
{
    char u[MAX_TEXT + 64], v[MAX_TEXT + 64] = "";
    NPError result = ((NPError (*)(NPP, int, const char *, char **, uint32_t *))browser.getvalueforurl)(npp, variable, address, value, length);
    if (result == NPERR_NO_ERROR && value && *value)
        names(v, sizeof v, *value, variable == 501 ? ';' : '&');
    spy("NPN_GetValueForURL(%s, %s) -> %d [%s]", variable == 501 ? "cookie" : variable == 502 ? "proxy" : "?",
        url(u, sizeof u, address), result, v);
    return result;
}

static NPError spySetValueForURL(NPP npp, int variable, const char *address, const char *value, uint32_t length)
{
    char u[MAX_TEXT + 64], v[MAX_TEXT + 64] = "";
    if (value && variable == 501)
    {
        char copy[1024];
        text(copy, sizeof copy, value, length, 1000);
        names(v, sizeof v, copy, ';');
    }
    NPError result = ((NPError (*)(NPP, int, const char *, const char *, uint32_t))browser.setvalueforurl)(npp, variable, address, value, length);
    spy("NPN_SetValueForURL(%s, %s, [%s]) -> %d", variable == 501 ? "cookie" : "?", url(u, sizeof u, address), v, result);
    return result;
}

static NPError spyGetAuthenticationInfo(NPP npp, const char *protocol, const char *host, int32_t port, const char *scheme,
                                        const char *realm, char **user, uint32_t *userLength, char **password, uint32_t *passwordLength)
{
    char p[64], h[256], s[64], r[256];
    NPError result = ((NPError (*)(NPP, const char *, const char *, int32_t, const char *, const char *, char **, uint32_t *, char **, uint32_t *))
                      browser.getauthenticationinfo)(npp, protocol, host, port, scheme, realm, user, userLength, password, passwordLength);
    spy("NPN_GetAuthenticationInfo(%s, %s:%d, %s, %s) -> %d", cstr(p, sizeof p, protocol), cstr(h, sizeof h, host), port,
        cstr(s, sizeof s, scheme), cstr(r, sizeof r, realm), result);
    return result;
}

static uint32_t spyScheduleTimer(NPP npp, uint32_t interval, NPBool repeat, void (*callback)(NPP, uint32_t))
{
    static long calls;
    uint32_t id = browser.scheduletimer(npp, interval, repeat, callback);
    if (++calls <= 10)
        spy("NPN_ScheduleTimer(%u ms, répété %d) -> %u [10 premières]", interval, repeat, id);
    return id;
}

static NPError spyPopUpContextMenu(NPP npp, void *menu)
{
    NPError result = ((NPError (*)(NPP, void *))browser.popupcontextmenu)(npp, menu);
    spy("NPN_PopUpContextMenu -> %d", result);
    return result;
}

static NPBool spyHandleEvent(NPP npp, void *event, NPBool handled)
{
    NPBool result = ((NPBool (*)(NPP, void *, NPBool))browser.handleevent)(npp, event, handled);
    spy("NPN_HandleEvent(traité %d) -> %d", handled, result);
    return result;
}

static void spyUnfocusInstance(NPP npp, int direction)
{
    spy("NPN_UnfocusInstance(%d)", direction);
    ((void (*)(NPP, int))browser.unfocusinstance)(npp, direction);
}

static void spyURLRedirectResponse(NPP npp, void *notifyData, NPBool allow)
{
    spy("NPN_URLRedirectResponse(%p, accepte %d)", notifyData, allow);
    ((void (*)(NPP, void *, NPBool))browser.urlredirectresponse)(npp, notifyData, allow);
}

static NPError spyInitAsyncSurface(NPP npp, void *size, int format, void *initData, void *surface)
{
    NPError result = ((NPError (*)(NPP, void *, int, void *, void *))browser.initasyncsurface)(npp, size, format, initData, surface);
    spy("NPN_InitAsyncSurface(format %d) -> %d", format, result);
    return result;
}

/* ------------------------------------------------------------------ */
/* Fonctions du module, appelées par le navigateur (NPP_*)             */
/* ------------------------------------------------------------------ */

static NPError spyNew(NPMIMEType type, NPP npp, uint16_t mode, int16_t argc, char **argn, char **argv, NPSavedData *saved)
{
    char t[128];
    spy("NPP_New(%s, mode %u, %d arguments)", cstr(t, sizeof t, type), mode, argc);
    for (int i = 0; i < argc; i++)
    {
        char n[128], v[MAX_TEXT + 64];
        const char *name = argn && argn[i] ? argn[i] : "(null)";
        const char *value = argv && argv[i] ? argv[i] : NULL;
        if (_stricmp(name, "flashvars") == 0)
            spy("  %s = [%u caractères] %s", cstr(n, sizeof n, name), value ? (unsigned)strlen(value) : 0u, names(v, sizeof v, value, '&'));
        else if (_stricmp(name, "src") == 0 || _stricmp(name, "data") == 0 || _stricmp(name, "movie") == 0)
            spy("  %s = %s", cstr(n, sizeof n, name), url(v, sizeof v, value));
        else
            spy("  %s = %s", cstr(n, sizeof n, name), cstr(v, sizeof v, value));
    }
    NPError result = module.newp(type, npp, mode, argc, argn, argv, saved);
    spy("NPP_New -> %d", result);
    return result;
}

static NPError spyDestroy(NPP npp, NPSavedData **save)
{
    NPError result = module.destroy(npp, save);
    spy("NPP_Destroy -> %d", result);
    return result;
}

static NPError spySetWindow(NPP npp, NPWindow *window)
{
    NPError result = module.setwindow(npp, window);
    if (window)
        spy("NPP_SetWindow(fenêtre %p, %d,%d %ux%u, découpe %u,%u-%u,%u, type %d) -> %d", window->window, window->x, window->y,
            window->width, window->height, window->clipRect.left, window->clipRect.top, window->clipRect.right,
            window->clipRect.bottom, window->type, result);
    else
        spy("NPP_SetWindow(null) -> %d", result);
    return result;
}

static NPError spyNewStream(NPP npp, NPMIMEType type, NPStream *stream, NPBool seekable, uint16_t *stype)
{
    char u[MAX_TEXT + 64], t[128], h[MAX_TEXT + 64] = "";
    NPError result = module.newstream(npp, type, stream, seekable, stype);
    if (stream && stream->headers)
    {
        const char *end = strpbrk(stream->headers, "\r\n");
        text(h, sizeof h, stream->headers, end ? (size_t)(end - stream->headers) : strlen(stream->headers), 200);
    }
    spy("NPP_NewStream(%s, %s, taille %u, %p, [%s], recherche %d) -> %d type %u",
        url(u, sizeof u, stream ? stream->url : NULL), cstr(t, sizeof t, type), stream ? stream->end : 0,
        stream ? stream->notifyData : NULL, h, seekable, result, stype ? *stype : 0);
    return result;
}

static NPError spyDestroyStreamModule(NPP npp, NPStream *stream, NPReason reason)
{
    char u[MAX_TEXT + 64];
    spy("NPP_DestroyStream(%s, raison %d)", url(u, sizeof u, stream ? stream->url : NULL), reason);
    return module.destroystream(npp, stream, reason);
}

static void spyAsFile(NPP npp, NPStream *stream, const char *file)
{
    char u[MAX_TEXT + 64];
    spy("NPP_StreamAsFile(%s)", url(u, sizeof u, stream ? stream->url : NULL));
    module.asfile(npp, stream, file);
}

static int16_t spyHandleEventModule(NPP npp, void *event)
{
    static long calls;
    int16_t result = ((int16_t (*)(NPP, void *))module.event)(npp, event);
    /* Mode sans fenêtre : le navigateur passe les messages Windows (NPEvent : event, wParam, lParam). */
    if (++calls <= 40 && event)
        spy("NPP_HandleEvent(message 0x%04x) -> %d [40 premiers]", *(uint16_t *)event, result);
    return result;
}

static void spyURLNotify(NPP npp, const char *address, NPReason reason, void *notifyData)
{
    char u[MAX_TEXT + 64];
    spy("NPP_URLNotify(%s, raison %d, %p)", url(u, sizeof u, address), reason, notifyData);
    module.urlnotify(npp, address, reason, notifyData);
}

static NPError spyGetValueModule(NPP npp, NPPVariable variable, void *value)
{
    NPError result = module.getvalue(npp, variable, value);
    char shown[MAX_TEXT + 64] = "";
    if (result == NPERR_NO_ERROR && value)
    {
        if (variable == 1 || variable == 2)
            cstr(shown, sizeof shown, *(char **)value);
        else if (variable == 15)
        {
            label(*(NPObject **)value, "objet du module");
            snprintf(shown, sizeof shown, "%p", *(void **)value);
        }
        else if (variable == 3 || variable == 4 || variable == 14 || variable == 18 || variable == 21 || variable == 22)
            snprintf(shown, sizeof shown, "%d", *(NPBool *)value);
        else
            snprintf(shown, sizeof shown, "(brut %p)", *(void **)value);
    }
    spy("NPP_GetValue(%d) -> %d %s", (int)variable, result, shown);
    return result;
}

static NPError spySetValueModule(NPP npp, NPNVariable variable, void *value)
{
    NPError result = module.setvalue(npp, variable, value);
    spy("NPP_SetValue(%d %s, %p) -> %d", (int)variable, browserVariable((int)variable), value, result);
    return result;
}

static NPBool spyGotFocus(NPP npp, int direction)
{
    NPBool result = ((NPBool (*)(NPP, int))module.gotfocus)(npp, direction);
    spy("NPP_GotFocus(%d) -> %d", direction, result);
    return result;
}

static void spyLostFocus(NPP npp)
{
    spy("NPP_LostFocus");
    ((void (*)(NPP))module.lostfocus)(npp);
}

static void spyURLRedirectNotify(NPP npp, const char *address, int32_t status, void *notifyData)
{
    char u[MAX_TEXT + 64];
    spy("NPP_URLRedirectNotify(%s, %d, %p)", url(u, sizeof u, address), status, notifyData);
    ((void (*)(NPP, const char *, int32_t, void *))module.urlredirectnotify)(npp, address, status, notifyData);
}

static NPError spyClearSiteData(const char *site, uint64_t flags, uint64_t maxAge)
{
    char s[256];
    NPError result = ((NPError (*)(const char *, uint64_t, uint64_t))module.clearsitedata)(site, flags, maxAge);
    spy("NPP_ClearSiteData(%s) -> %d", cstr(s, sizeof s, site), result);
    return result;
}

static char **spyGetSitesWithData(void)
{
    char **result = ((char **(*)(void))module.getsiteswithdata)();
    spy("NPP_GetSitesWithData");
    return result;
}

/* ------------------------------------------------------------------ */
/* Points d'entrée                                                     */
/* ------------------------------------------------------------------ */

static int loadReal(void)
{
    if (real)
        return 1;
    openLog();
    WCHAR path[MAX_PATH], target[MAX_PATH];
    GetModuleFileNameW(self, path, MAX_PATH);
    WCHAR *slash = wcsrchr(path, L'\\');
    if (!slash)
        return 0;
    *slash = 0;
    _snwprintf(target, MAX_PATH, L"%ls\\espion\\%ls", path, slash + 1);
    real = LoadLibraryExW(target, NULL, LOAD_WITH_ALTERED_SEARCH_PATH);
    DWORD error = real ? 0 : GetLastError();
    WCHAR exe[MAX_PATH];
    char exeText[MAX_PATH * 3], targetText[MAX_PATH * 3];
    GetModuleFileNameW(NULL, exe, MAX_PATH);
    spy("Espion PommeBrowser dans %s (processus %lu)", utf8(exeText, sizeof exeText, exe), GetCurrentProcessId());
    spy("Vrai module : %s -> %s (erreur %lu)", utf8(targetText, sizeof targetText, target), real ? "chargé" : "introuvable", error);
    return real != NULL;
}

#define FITS(table, field, size) (offsetof(table, field) + sizeof(void *) <= (size_t)(size))

NP_EXPORT NPError WINAPI NP_GetEntryPoints(NPPluginFuncs *funcs)
{
    if (!funcs || !loadReal())
        return NPERR_GENERIC_ERROR;
    GetEntryPointsFn getEntryPoints = (GetEntryPointsFn)(void *)GetProcAddress(real, "NP_GetEntryPoints");
    if (!getEntryPoints)
        return NPERR_GENERIC_ERROR;

    memset(&module, 0, sizeof module);
    module.size = sizeof module;
    NPError result = getEntryPoints(&module);
    spy("NP_GetEntryPoints(table du navigateur %u octets, version %u) -> %d, table du module %u octets version %u",
        funcs->size, funcs->version, result, module.size, module.version);
    if (result != NPERR_NO_ERROR)
        return result;

    uint16_t size = funcs->size ? funcs->size : sizeof *funcs;
    NPPluginFuncs given = module;
    given.newp = module.newp ? spyNew : NULL;
    given.destroy = module.destroy ? spyDestroy : NULL;
    given.setwindow = module.setwindow ? spySetWindow : NULL;
    given.newstream = module.newstream ? spyNewStream : NULL;
    given.destroystream = module.destroystream ? spyDestroyStreamModule : NULL;
    given.asfile = module.asfile ? spyAsFile : NULL;
    given.event = module.event ? (void *)spyHandleEventModule : NULL;
    given.urlnotify = module.urlnotify ? spyURLNotify : NULL;
    given.getvalue = module.getvalue ? spyGetValueModule : NULL;
    given.setvalue = module.setvalue ? spySetValueModule : NULL;
    given.gotfocus = module.gotfocus ? (void *)spyGotFocus : NULL;
    given.lostfocus = module.lostfocus ? (void *)spyLostFocus : NULL;
    given.urlredirectnotify = module.urlredirectnotify ? (void *)spyURLRedirectNotify : NULL;
    given.clearsitedata = module.clearsitedata ? (void *)spyClearSiteData : NULL;
    given.getsiteswithdata = module.getsiteswithdata ? (void *)spyGetSitesWithData : NULL;
    given.size = size;
    given.version = module.version;
    memcpy(funcs, &given, size < sizeof given ? size : sizeof given);
    return result;
}

NP_EXPORT NPError WINAPI NP_Initialize(NPNetscapeFuncs *funcs)
{
    if (!funcs || !loadReal())
        return NPERR_GENERIC_ERROR;
    InitializeFn initialize = (InitializeFn)(void *)GetProcAddress(real, "NP_Initialize");
    if (!initialize)
        return NPERR_GENERIC_ERROR;

    memset(&browser, 0, sizeof browser);
    memcpy(&browser, funcs, funcs->size < sizeof browser ? funcs->size : sizeof browser);
    wrapped = browser;
    size_t size = funcs->size;
#define WRAP(field, function) if (FITS(NPNetscapeFuncs, field, size) && browser.field) *(void **)&wrapped.field = (void *)(function)
    WRAP(geturl, spyGetURL);
    WRAP(posturl, spyPostURL);
    WRAP(geturlnotify, spyGetURLNotify);
    WRAP(posturlnotify, spyPostURLNotify);
    WRAP(destroystream, spyDestroyStream);
    WRAP(status, spyStatus);
    WRAP(uagent, spyUserAgent);
    WRAP(getvalue, spyGetValue);
    WRAP(setvalue, spySetValue);
    WRAP(invalidaterect, spyInvalidateRect);
    WRAP(invoke, spyInvoke);
    WRAP(invokeDefault, spyInvokeDefault);
    WRAP(evaluate, spyEvaluate);
    WRAP(getproperty, spyGetProperty);
    WRAP(setproperty, spySetProperty);
    WRAP(removeproperty, spyRemoveProperty);
    WRAP(hasproperty, spyHasProperty);
    WRAP(hasmethod, spyHasMethod);
    WRAP(setexception, spySetException);
    WRAP(enumerate, spyEnumerate);
    WRAP(construct, spyConstruct);
    WRAP(getvalueforurl, spyGetValueForURL);
    WRAP(setvalueforurl, spySetValueForURL);
    WRAP(getauthenticationinfo, spyGetAuthenticationInfo);
    WRAP(scheduletimer, spyScheduleTimer);
    WRAP(popupcontextmenu, spyPopUpContextMenu);
    WRAP(handleevent, spyHandleEvent);
    WRAP(unfocusinstance, spyUnfocusInstance);
    WRAP(urlredirectresponse, spyURLRedirectResponse);
    WRAP(initasyncsurface, spyInitAsyncSurface);
#undef WRAP

    NPError result = initialize(&wrapped);
    spy("NP_Initialize(table du navigateur %u octets, version %u) -> %d", funcs->size, funcs->version, result);
    return result;
}

NP_EXPORT NPError WINAPI NP_Shutdown(void)
{
    ShutdownFn shutdown = real ? (ShutdownFn)(void *)GetProcAddress(real, "NP_Shutdown") : NULL;
    NPError result = shutdown ? shutdown() : NPERR_NO_ERROR;
    spy("NP_Shutdown -> %d", result);
    return result;
}

BOOL WINAPI DllMain(HINSTANCE instance, DWORD reason, LPVOID reserved)
{
    (void)reserved;
    if (reason == DLL_PROCESS_ATTACH)
    {
        self = instance;
        startTick = GetTickCount();
        InitializeCriticalSection(&logLock);
        InitializeCriticalSection(&labelLock);
    }
    else if (reason == DLL_PROCESS_DETACH && logFile)
    {
        fclose(logFile);
        logFile = NULL;
    }
    return TRUE;
}
