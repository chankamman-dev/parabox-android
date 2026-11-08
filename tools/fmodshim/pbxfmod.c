// FMOD 2.01 -> 2.03 adapter for Patrick's Parabox.
//
// The game's C# wrapper (FMODUnity.dll) is FMOD 2.01.11; the only Android libraries at hand are FMOD
// 2.03.15. Nearly all of the 570 entry points are unchanged; ptx (Hooks.cs) points the few that are not
// at the functions here ("pbx_" + the original entry point, library "pbxfmod"):
//   - System_Create gained a header version argument; Studio_System_Create checks its version.
//   - System_GetVersion gained a build number pointer; the wrapper is told 2.01.11 so its check passes.
//   - CPU usage moved into structs.
//   - Cue functions were renamed to sustain point / key off; min/max distance became one call.
//   - FMOD_ADVANCEDSETTINGS gained maxSpatialObjects at the end (FMOD checks cbSize), and
//     FMOD_STUDIO_PARAMETER_DESCRIPTION gained a GUID at the end (written past the 2.01 struct).
// The real functions are looked up in libfmodstudio.so (which pulls in libfmod.so), the library the
// wrapper's DllImports name.
#include <dlfcn.h>
#include <stddef.h>
#include <stdlib.h>
#include <string.h>
#include <android/log.h>
#include "fmod.h"
#include "fmod_studio.h"

#define WRAPPER_VERSION 0x00020111u

#define LOG(...) __android_log_print(ANDROID_LOG_INFO, "PbxFmod", __VA_ARGS__)

static void *lib;

static void *sym(const char *name) {
    if (!lib) lib = dlopen("libfmodstudio.so", RTLD_NOW);
    void *p = lib ? dlsym(lib, name) : NULL;
    if (!p) LOG("missing %s: %s", name, dlerror());
    return p;
}

#define REAL(ret, name, args) \
    static ret (*r_##name) args; \
    if (!r_##name) r_##name = (ret (*) args) sym(#name); \
    if (!r_##name) return FMOD_ERR_INTERNAL;

// ---- creation and version ----
FMOD_RESULT pbx_FMOD5_System_Create(FMOD_SYSTEM **system) {
    REAL(FMOD_RESULT, FMOD5_System_Create, (FMOD_SYSTEM **, unsigned int))
    FMOD_RESULT r = r_FMOD5_System_Create(system, FMOD_VERSION);
    LOG("core system create (2.01 wrapper on %x): %d", FMOD_VERSION, r);
    return r;
}

FMOD_RESULT pbx_FMOD_Studio_System_Create(FMOD_STUDIO_SYSTEM **system, unsigned int headerversion) {
    REAL(FMOD_RESULT, FMOD_Studio_System_Create, (FMOD_STUDIO_SYSTEM **, unsigned int))
    FMOD_RESULT r = r_FMOD_Studio_System_Create(system, FMOD_VERSION);
    LOG("studio system create (wrapper %x, runtime %x): %d", headerversion, FMOD_VERSION, r);
    return r;
}

FMOD_RESULT pbx_FMOD5_System_GetVersion(FMOD_SYSTEM *system, unsigned int *version) {
    REAL(FMOD_RESULT, FMOD5_System_GetVersion, (FMOD_SYSTEM *, unsigned int *, unsigned int *))
    unsigned int v = 0, build = 0;
    FMOD_RESULT r = r_FMOD5_System_GetVersion(system, &v, &build);
    if (version) *version = WRAPPER_VERSION;
    return r;
}

// ---- CPU usage ----
FMOD_RESULT pbx_FMOD5_System_GetCPUUsage(FMOD_SYSTEM *system, float *dsp, float *stream, float *geometry, float *update, float *total) {
    REAL(FMOD_RESULT, FMOD5_System_GetCPUUsage, (FMOD_SYSTEM *, FMOD_CPU_USAGE *))
    FMOD_CPU_USAGE u;
    memset(&u, 0, sizeof u);
    FMOD_RESULT r = r_FMOD5_System_GetCPUUsage(system, &u);
    if (dsp) *dsp = u.dsp;
    if (stream) *stream = u.stream;
    if (geometry) *geometry = u.geometry;
    if (update) *update = u.update;
    if (total) *total = u.dsp + u.stream + u.geometry + u.update + u.convolution1 + u.convolution2;
    return r;
}

FMOD_RESULT pbx_FMOD5_System_GetCPUUsageEx(FMOD_SYSTEM *system, float *conv1, float *conv2) {
    REAL(FMOD_RESULT, FMOD5_System_GetCPUUsage, (FMOD_SYSTEM *, FMOD_CPU_USAGE *))
    FMOD_CPU_USAGE u;
    memset(&u, 0, sizeof u);
    FMOD_RESULT r = r_FMOD5_System_GetCPUUsage(system, &u);
    if (conv1) *conv1 = u.convolution1;
    if (conv2) *conv2 = u.convolution2;
    return r;
}

typedef struct { float dsp, stream, geometry, update, studio; } CPU_USAGE_201;

FMOD_RESULT pbx_FMOD_Studio_System_GetCPUUsage(FMOD_STUDIO_SYSTEM *system, CPU_USAGE_201 *usage) {
    REAL(FMOD_RESULT, FMOD_Studio_System_GetCPUUsage, (FMOD_STUDIO_SYSTEM *, FMOD_STUDIO_CPU_USAGE *, FMOD_CPU_USAGE *))
    FMOD_STUDIO_CPU_USAGE s;
    FMOD_CPU_USAGE c;
    memset(&s, 0, sizeof s);
    memset(&c, 0, sizeof c);
    FMOD_RESULT r = r_FMOD_Studio_System_GetCPUUsage(system, &s, &c);
    if (usage) {
        usage->dsp = c.dsp;
        usage->stream = c.stream;
        usage->geometry = c.geometry;
        usage->update = c.update;
        usage->studio = s.update;
    }
    return r;
}

// ---- renamed / merged event functions ----
FMOD_RESULT pbx_FMOD_Studio_EventDescription_GetMinimumDistance(FMOD_STUDIO_EVENTDESCRIPTION *d, float *distance) {
    REAL(FMOD_RESULT, FMOD_Studio_EventDescription_GetMinMaxDistance, (FMOD_STUDIO_EVENTDESCRIPTION *, float *, float *))
    float mn = 0, mx = 0;
    FMOD_RESULT r = r_FMOD_Studio_EventDescription_GetMinMaxDistance(d, &mn, &mx);
    if (distance) *distance = mn;
    return r;
}

FMOD_RESULT pbx_FMOD_Studio_EventDescription_GetMaximumDistance(FMOD_STUDIO_EVENTDESCRIPTION *d, float *distance) {
    REAL(FMOD_RESULT, FMOD_Studio_EventDescription_GetMinMaxDistance, (FMOD_STUDIO_EVENTDESCRIPTION *, float *, float *))
    float mn = 0, mx = 0;
    FMOD_RESULT r = r_FMOD_Studio_EventDescription_GetMinMaxDistance(d, &mn, &mx);
    if (distance) *distance = mx;
    return r;
}

FMOD_RESULT pbx_FMOD_Studio_EventDescription_HasCue(FMOD_STUDIO_EVENTDESCRIPTION *d, FMOD_BOOL *cue) {
    REAL(FMOD_RESULT, FMOD_Studio_EventDescription_HasSustainPoint, (FMOD_STUDIO_EVENTDESCRIPTION *, FMOD_BOOL *))
    return r_FMOD_Studio_EventDescription_HasSustainPoint(d, cue);
}

FMOD_RESULT pbx_FMOD_Studio_EventInstance_TriggerCue(FMOD_STUDIO_EVENTINSTANCE *e) {
    REAL(FMOD_RESULT, FMOD_Studio_EventInstance_KeyOff, (FMOD_STUDIO_EVENTINSTANCE *))
    return r_FMOD_Studio_EventInstance_KeyOff(e);
}

// ---- FMOD_ADVANCEDSETTINGS: 2.01 ends before maxSpatialObjects ----
#define ADV_201 offsetof(FMOD_ADVANCEDSETTINGS, maxSpatialObjects)

FMOD_RESULT pbx_FMOD5_System_SetAdvancedSettings(FMOD_SYSTEM *system, FMOD_ADVANCEDSETTINGS *settings) {
    REAL(FMOD_RESULT, FMOD5_System_SetAdvancedSettings, (FMOD_SYSTEM *, FMOD_ADVANCEDSETTINGS *))
    REAL(FMOD_RESULT, FMOD5_System_GetAdvancedSettings, (FMOD_SYSTEM *, FMOD_ADVANCEDSETTINGS *))
    FMOD_ADVANCEDSETTINGS full;
    memset(&full, 0, sizeof full);
    full.cbSize = sizeof full;
    r_FMOD5_System_GetAdvancedSettings(system, &full);   // keep the runtime's defaults for the new fields
    memcpy((char *) &full + sizeof(int), (char *) settings + sizeof(int), ADV_201 - sizeof(int));
    full.cbSize = sizeof full;
    return r_FMOD5_System_SetAdvancedSettings(system, &full);
}

FMOD_RESULT pbx_FMOD5_System_GetAdvancedSettings(FMOD_SYSTEM *system, FMOD_ADVANCEDSETTINGS *settings) {
    REAL(FMOD_RESULT, FMOD5_System_GetAdvancedSettings, (FMOD_SYSTEM *, FMOD_ADVANCEDSETTINGS *))
    FMOD_ADVANCEDSETTINGS full;
    memset(&full, 0, sizeof full);
    full.cbSize = sizeof full;
    FMOD_RESULT r = r_FMOD5_System_GetAdvancedSettings(system, &full);
    memcpy((char *) settings + sizeof(int), (char *) &full + sizeof(int), ADV_201 - sizeof(int));
    return r;
}

// ---- FMOD_STUDIO_PARAMETER_DESCRIPTION: 2.01 ends before guid ----
#define PD_201 offsetof(FMOD_STUDIO_PARAMETER_DESCRIPTION, guid)

static FMOD_RESULT pd_out(FMOD_RESULT r, const FMOD_STUDIO_PARAMETER_DESCRIPTION *full, void *out) {
    if (out) memcpy(out, full, PD_201);
    return r;
}

FMOD_RESULT pbx_FMOD_Studio_EventDescription_GetParameterDescriptionByIndex(FMOD_STUDIO_EVENTDESCRIPTION *d, int index, void *parameter) {
    REAL(FMOD_RESULT, FMOD_Studio_EventDescription_GetParameterDescriptionByIndex, (FMOD_STUDIO_EVENTDESCRIPTION *, int, FMOD_STUDIO_PARAMETER_DESCRIPTION *))
    FMOD_STUDIO_PARAMETER_DESCRIPTION full;
    memset(&full, 0, sizeof full);
    return pd_out(r_FMOD_Studio_EventDescription_GetParameterDescriptionByIndex(d, index, &full), &full, parameter);
}

FMOD_RESULT pbx_FMOD_Studio_EventDescription_GetParameterDescriptionByName(FMOD_STUDIO_EVENTDESCRIPTION *d, const char *name, void *parameter) {
    REAL(FMOD_RESULT, FMOD_Studio_EventDescription_GetParameterDescriptionByName, (FMOD_STUDIO_EVENTDESCRIPTION *, const char *, FMOD_STUDIO_PARAMETER_DESCRIPTION *))
    FMOD_STUDIO_PARAMETER_DESCRIPTION full;
    memset(&full, 0, sizeof full);
    return pd_out(r_FMOD_Studio_EventDescription_GetParameterDescriptionByName(d, name, &full), &full, parameter);
}

FMOD_RESULT pbx_FMOD_Studio_EventDescription_GetParameterDescriptionByID(FMOD_STUDIO_EVENTDESCRIPTION *d, FMOD_STUDIO_PARAMETER_ID id, void *parameter) {
    REAL(FMOD_RESULT, FMOD_Studio_EventDescription_GetParameterDescriptionByID, (FMOD_STUDIO_EVENTDESCRIPTION *, FMOD_STUDIO_PARAMETER_ID, FMOD_STUDIO_PARAMETER_DESCRIPTION *))
    FMOD_STUDIO_PARAMETER_DESCRIPTION full;
    memset(&full, 0, sizeof full);
    return pd_out(r_FMOD_Studio_EventDescription_GetParameterDescriptionByID(d, id, &full), &full, parameter);
}

FMOD_RESULT pbx_FMOD_Studio_System_GetParameterDescriptionByName(FMOD_STUDIO_SYSTEM *s, const char *name, void *parameter) {
    REAL(FMOD_RESULT, FMOD_Studio_System_GetParameterDescriptionByName, (FMOD_STUDIO_SYSTEM *, const char *, FMOD_STUDIO_PARAMETER_DESCRIPTION *))
    FMOD_STUDIO_PARAMETER_DESCRIPTION full;
    memset(&full, 0, sizeof full);
    return pd_out(r_FMOD_Studio_System_GetParameterDescriptionByName(s, name, &full), &full, parameter);
}

FMOD_RESULT pbx_FMOD_Studio_System_GetParameterDescriptionByID(FMOD_STUDIO_SYSTEM *s, FMOD_STUDIO_PARAMETER_ID id, void *parameter) {
    REAL(FMOD_RESULT, FMOD_Studio_System_GetParameterDescriptionByID, (FMOD_STUDIO_SYSTEM *, FMOD_STUDIO_PARAMETER_ID, FMOD_STUDIO_PARAMETER_DESCRIPTION *))
    FMOD_STUDIO_PARAMETER_DESCRIPTION full;
    memset(&full, 0, sizeof full);
    return pd_out(r_FMOD_Studio_System_GetParameterDescriptionByID(s, id, &full), &full, parameter);
}

FMOD_RESULT pbx_FMOD_Studio_System_GetParameterDescriptionList(FMOD_STUDIO_SYSTEM *s, void *array, int capacity, int *count) {
    REAL(FMOD_RESULT, FMOD_Studio_System_GetParameterDescriptionList, (FMOD_STUDIO_SYSTEM *, FMOD_STUDIO_PARAMETER_DESCRIPTION *, int, int *))
    if (capacity <= 0 || !array) return r_FMOD_Studio_System_GetParameterDescriptionList(s, NULL, capacity, count);
    FMOD_STUDIO_PARAMETER_DESCRIPTION *full = calloc((size_t) capacity, sizeof *full);
    if (!full) return FMOD_ERR_MEMORY;
    int n = 0;
    FMOD_RESULT r = r_FMOD_Studio_System_GetParameterDescriptionList(s, full, capacity, &n);
    // The 2.01 array elements are the 2.01 size, padded like the struct itself.
    size_t stride = (PD_201 + 7) & ~(size_t) 7;
    for (int i = 0; i < n && i < capacity; i++) memcpy((char *) array + i * stride, &full[i], PD_201);
    if (count) *count = n;
    free(full);
    return r;
}

// ---- diagnostics: "setprop debug.pbxfmod.meter 1" logs the master output level every ~5 s ----
#include <sys/system_properties.h>

FMOD_RESULT pbx_FMOD_Studio_System_Update(FMOD_STUDIO_SYSTEM *system) {
    REAL(FMOD_RESULT, FMOD_Studio_System_Update, (FMOD_STUDIO_SYSTEM *))
    FMOD_RESULT r = r_FMOD_Studio_System_Update(system);
    static int frames, enabled = -1;
    static FMOD_DSP *head;
    if (enabled < 0) {
        char v[PROP_VALUE_MAX] = "";
        __system_property_get("debug.pbxfmod.meter", v);
        enabled = v[0] == '1';
        if (enabled) LOG("metering on");
    }
    if (!enabled || ++frames % 300) return r;
    if (!head) {
        FMOD_SYSTEM *core = NULL;
        FMOD_CHANNELGROUP *master = NULL;
        FMOD_RESULT (*getCore)(FMOD_STUDIO_SYSTEM *, FMOD_SYSTEM **) = sym("FMOD_Studio_System_GetCoreSystem");
        FMOD_RESULT (*getMaster)(FMOD_SYSTEM *, FMOD_CHANNELGROUP **) = sym("FMOD5_System_GetMasterChannelGroup");
        FMOD_RESULT (*getDsp)(FMOD_CHANNELGROUP *, int, FMOD_DSP **) = sym("FMOD5_ChannelGroup_GetDSP");
        FMOD_RESULT (*meter)(FMOD_DSP *, FMOD_BOOL, FMOD_BOOL) = sym("FMOD5_DSP_SetMeteringEnabled");
        if (!getCore || !getMaster || !getDsp || !meter) return r;
        if (getCore(system, &core) || getMaster(core, &master) || getDsp(master, FMOD_CHANNELCONTROL_DSP_HEAD, &head)) { head = NULL; return r; }
        meter(head, 0, 1);
        return r;
    }
    FMOD_RESULT (*info)(FMOD_DSP *, FMOD_DSP_METERING_INFO *, FMOD_DSP_METERING_INFO *) = sym("FMOD5_DSP_GetMeteringInfo");
    FMOD_DSP_METERING_INFO out;
    memset(&out, 0, sizeof out);
    if (info && info(head, NULL, &out) == FMOD_OK)
        LOG("master level: %d ch, rms %.4f %.4f, peak %.4f %.4f", out.numchannels, out.rmslevel[0], out.rmslevel[1], out.peaklevel[0], out.peaklevel[1]);
    return r;
}
