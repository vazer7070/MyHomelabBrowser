/*
 * Déclarations NPAPI minimales pour le greffon de test de PommeFlashHost, écrites d'après la
 * spécification publique (npapi.h, npfunctions.h, npruntime.h). Windows x64.
 */
#ifndef POMME_NPAPI_MIN_H
#define POMME_NPAPI_MIN_H

#include <stdint.h>
#include <stdbool.h>

/*
 * Points d'entrée NP_* : __stdcall (WINAPI) en 32 bits, exportés sans décoration, comme ceux de
 * Flash : le navigateur les cherche par leur nom. MinGW 32 bits : lier avec -Wl,--kill-at.
 */
#if defined(_M_IX86) && !defined(__MINGW32__)
#define NP_EXPORT
#pragma comment(linker, "/EXPORT:NP_GetEntryPoints=_NP_GetEntryPoints@4")
#pragma comment(linker, "/EXPORT:NP_Initialize=_NP_Initialize@4")
#pragma comment(linker, "/EXPORT:NP_Shutdown=_NP_Shutdown@0")
#else
#define NP_EXPORT __declspec(dllexport)
#endif

typedef unsigned char NPBool;
typedef int16_t NPError;
typedef int16_t NPReason;
typedef char *NPMIMEType;
typedef char NPUTF8;
typedef void *NPIdentifier;

#define NPERR_NO_ERROR 0
#define NPERR_GENERIC_ERROR 1
#define NPRES_DONE 0
#define NPRES_NETWORK_ERR 1
#define NP_NORMAL 1
#define NP_EMBED 1

typedef struct _NPP { void *pdata; void *ndata; } NPP_t, *NPP;
typedef struct _NPRect { uint16_t top, left, bottom, right; } NPRect;
typedef struct _NPWindow {
    void *window; int32_t x, y; uint32_t width, height; NPRect clipRect; int type;
} NPWindow;
typedef struct _NPStream {
    void *pdata; void *ndata; const char *url; uint32_t end; uint32_t lastmodified;
    void *notifyData; const char *headers;
} NPStream;
typedef struct _NPSavedData { int32_t len; void *buf; } NPSavedData;

typedef enum {
    NPNVnetscapeWindow = 3, NPNVjavascriptEnabledBool = 4, NPNVWindowNPObject = 15,
    NPNVPluginElementNPObject = 16, NPNVSupportsWindowless = 17, NPNVprivateModeBool = 18,
    NPNVdocumentOrigin = 22
} NPNVariable;
typedef enum { NPPVpluginNameString = 1, NPPVpluginScriptableNPObject = 15 } NPPVariable;

typedef struct NPObject NPObject;
typedef struct NPClass NPClass;
typedef struct _NPString { const NPUTF8 *UTF8Characters; uint32_t UTF8Length; } NPString;
typedef enum {
    NPVariantType_Void, NPVariantType_Null, NPVariantType_Bool, NPVariantType_Int32,
    NPVariantType_Double, NPVariantType_String, NPVariantType_Object
} NPVariantType;
typedef struct _NPVariant {
    NPVariantType type;
    union { bool boolValue; int32_t intValue; double doubleValue; NPString stringValue; NPObject *objectValue; } value;
} NPVariant;

typedef NPObject *(*NPAllocateFunctionPtr)(NPP npp, NPClass *aClass);
typedef void (*NPDeallocateFunctionPtr)(NPObject *npobj);
struct NPClass {
    uint32_t structVersion;
    NPAllocateFunctionPtr allocate;
    NPDeallocateFunctionPtr deallocate;
    void *invalidate, *hasMethod, *invoke, *invokeDefault, *hasProperty, *getProperty,
         *setProperty, *removeProperty, *enumerate, *construct;
};
struct NPObject { NPClass *_class; uint32_t referenceCount; };

typedef struct _NPPluginFuncs {
    uint16_t size; uint16_t version;
    NPError (*newp)(NPMIMEType, NPP, uint16_t, int16_t, char **, char **, NPSavedData *);
    NPError (*destroy)(NPP, NPSavedData **);
    NPError (*setwindow)(NPP, NPWindow *);
    NPError (*newstream)(NPP, NPMIMEType, NPStream *, NPBool, uint16_t *);
    NPError (*destroystream)(NPP, NPStream *, NPReason);
    void (*asfile)(NPP, NPStream *, const char *);
    int32_t (*writeready)(NPP, NPStream *);
    int32_t (*write)(NPP, NPStream *, int32_t, int32_t, void *);
    void *print, *event;
    void (*urlnotify)(NPP, const char *, NPReason, void *);
    void *javaClass;
    NPError (*getvalue)(NPP, NPPVariable, void *);
    NPError (*setvalue)(NPP, NPNVariable, void *);
    void *gotfocus, *lostfocus, *urlredirectnotify, *clearsitedata, *getsiteswithdata, *didComposite;
} NPPluginFuncs;

typedef struct _NPNetscapeFuncs {
    uint16_t size; uint16_t version;
    NPError (*geturl)(NPP, const char *, const char *);
    NPError (*posturl)(NPP, const char *, const char *, uint32_t, const char *, NPBool);
    void *requestread, *newstream, *write;
    NPError (*destroystream)(NPP, NPStream *, NPReason);
    void (*status)(NPP, const char *);
    const char *(*uagent)(NPP);
    void *(*memalloc)(uint32_t);
    void (*memfree)(void *);
    void *memflush, *reloadplugins, *getJavaEnv, *getJavaPeer;
    NPError (*geturlnotify)(NPP, const char *, const char *, void *);
    NPError (*posturlnotify)(NPP, const char *, const char *, uint32_t, const char *, NPBool, void *);
    NPError (*getvalue)(NPP, NPNVariable, void *);
    NPError (*setvalue)(NPP, NPPVariable, void *);
    void *invalidaterect, *invalidateregion, *forceredraw;
    NPIdentifier (*getstringidentifier)(const NPUTF8 *);
    void (*getstringidentifiers)(const NPUTF8 **, int32_t, NPIdentifier *);
    NPIdentifier (*getintidentifier)(int32_t);
    bool (*identifierisstring)(NPIdentifier);
    NPUTF8 *(*utf8fromidentifier)(NPIdentifier);
    int32_t (*intfromidentifier)(NPIdentifier);
    NPObject *(*createobject)(NPP, NPClass *);
    NPObject *(*retainobject)(NPObject *);
    void (*releaseobject)(NPObject *);
    bool (*invoke)(NPP, NPObject *, NPIdentifier, const NPVariant *, uint32_t, NPVariant *);
    bool (*invokeDefault)(NPP, NPObject *, const NPVariant *, uint32_t, NPVariant *);
    bool (*evaluate)(NPP, NPObject *, NPString *, NPVariant *);
    bool (*getproperty)(NPP, NPObject *, NPIdentifier, NPVariant *);
    void *setproperty, *removeproperty, *hasproperty, *hasmethod;
    void (*releasevariantvalue)(NPVariant *);
    void *setexception, *pushpopupsenabledstate, *poppopupsenabledstate, *enumerate;
    void (*pluginthreadasynccall)(NPP, void (*)(void *), void *);
    void *construct, *getvalueforurl, *setvalueforurl, *getauthenticationinfo;
    uint32_t (*scheduletimer)(NPP, uint32_t, NPBool, void (*)(NPP, uint32_t));
    void (*unscheduletimer)(NPP, uint32_t);
    void *popupcontextmenu, *convertpoint, *handleevent, *unfocusinstance, *urlredirectresponse,
         *initasyncsurface, *finalizeasyncsurface, *setcurrentasyncsurface;
} NPNetscapeFuncs;

#endif
