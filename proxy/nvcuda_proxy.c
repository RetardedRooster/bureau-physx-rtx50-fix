// nvcuda.dll proxy for The Bureau: XCOM Declassified (32-bit).
//
// Placed next to TheBureau.exe, it is loaded instead of the system nvcuda.dll. Every export
// jumps to the real driver (C:\Windows\SysWOW64\nvcuda.dll) through thunks.S; a few are hooked:
//  - cuModuleLoadDataEx / cuModuleLoadData: logs the module (owner DLL, image kind, size, hash),
//    dumps it once to cuda_dump\<hash>.bin and, if cuda_ptx\<hash>.ptx exists, loads that PTX
//    instead (the game's modules only contain sm_10..sm_30 SASS, which post-Kepler GPUs reject).
//  - cuModuleGetFunction/TexRef/SurfRef/Global, cuDeviceComputeCapability: logged with results.
// Log: bureau_cuda.log next to the DLL. No CRT: only kernel32.

typedef unsigned int u32;
typedef unsigned long long u64;
typedef int BOOL;
typedef void* HANDLE;
typedef void* HMODULE;
typedef int CUresult;
#define WINAPI __stdcall
#define CUDAAPI __stdcall

__declspec(dllimport) HMODULE WINAPI LoadLibraryA(const char*);
__declspec(dllimport) void* WINAPI GetProcAddress(HMODULE, const char*);
__declspec(dllimport) HANDLE WINAPI CreateFileA(const char*, u32, u32, void*, u32, u32, HANDLE);
__declspec(dllimport) BOOL WINAPI WriteFile(HANDLE, const void*, u32, u32*, void*);
__declspec(dllimport) BOOL WINAPI ReadFile(HANDLE, void*, u32, u32*, void*);
__declspec(dllimport) u32 WINAPI GetFileSize(HANDLE, u32*);
__declspec(dllimport) BOOL WINAPI CloseHandle(HANDLE);
__declspec(dllimport) u32 WINAPI GetModuleFileNameA(HMODULE, char*, u32);
__declspec(dllimport) BOOL WINAPI GetModuleHandleExA(u32, const void*, HMODULE*);
__declspec(dllimport) u32 WINAPI GetFileAttributesA(const char*);
__declspec(dllimport) BOOL WINAPI CreateDirectoryA(const char*, void*);
__declspec(dllimport) void* WINAPI VirtualAlloc(void*, u32, u32, u32);
__declspec(dllimport) BOOL WINAPI VirtualFree(void*, u32, u32);
__declspec(dllimport) u32 WINAPI GetTickCount(void);
__declspec(dllimport) u32 WINAPI GetCurrentThreadId(void);
__declspec(dllimport) void WINAPI InitializeCriticalSection(void*);
__declspec(dllimport) void WINAPI EnterCriticalSection(void*);
__declspec(dllimport) void WINAPI LeaveCriticalSection(void*);

#define GENERIC_READ 0x80000000u
#define GENERIC_WRITE 0x40000000u
#define FILE_SHARE_READ 1u
#define CREATE_ALWAYS 2u
#define OPEN_EXISTING 3u
#define INVALID_FILE_ATTRIBUTES 0xFFFFFFFFu
#define INVALID_HANDLE ((HANDLE)(long)-1)

// ---- tiny libc ----
void* memset(void* d, int c, u32 n) { unsigned char* p = d; while (n--) *p++ = (unsigned char)c; return d; }
void* memcpy(void* d, const void* s, u32 n) { unsigned char* p = d; const unsigned char* q = s; while (n--) *p++ = *q++; return d; }
static u32 slen(const char* s) { u32 n = 0; while (s[n]) n++; return n; }
static char* scpy(char* d, const char* s) { while ((*d = *s++)) d++; return d; }

// Minimal formatter: %s %d %u %x %08x %c
static char* fmt_u(char* o, u32 v, u32 base, int width) {
  char t[16]; int n = 0;
  do { u32 d = v % base; t[n++] = (char)(d < 10 ? '0' + d : 'a' + d - 10); v /= base; } while (v);
  while (n < width) t[n++] = '0';
  while (n) *o++ = t[--n];
  return o;
}
static char* vfmt(char* o, const char* f, __builtin_va_list ap) {
  while (*f) {
    if (*f != '%') { *o++ = *f++; continue; }
    f++;
    int width = 0;
    while (*f >= '0' && *f <= '9') width = width * 10 + (*f++ - '0');
    char c = *f++;
    if (c == 's') { const char* s = __builtin_va_arg(ap, const char*); o = scpy(o, s ? s : "(null)"); }
    else if (c == 'd') { int v = __builtin_va_arg(ap, int); if (v < 0) { *o++ = '-'; v = -v; } o = fmt_u(o, (u32)v, 10, width); }
    else if (c == 'u') o = fmt_u(o, __builtin_va_arg(ap, u32), 10, width);
    else if (c == 'x') o = fmt_u(o, __builtin_va_arg(ap, u32), 16, width);
    else if (c == 'c') *o++ = (char)__builtin_va_arg(ap, int);
    else *o++ = c;
  }
  *o = 0;
  return o;
}

static char g_dir[600];          // directory of this DLL, with trailing backslash
static HANDLE g_log = 0;
static unsigned char g_cs[64];   // CRITICAL_SECTION (24 bytes on x86)
static HMODULE g_real = 0;
void* g_real_fns[1024];          // filled for thunks.S, index = order in nvcuda_exports.txt

static void plog(const char* f, ...) {
  char buf[2048];
  __builtin_va_list ap;
  __builtin_va_start(ap, f);
  char* o = buf;
  o = fmt_u(o, GetTickCount(), 10, 0); *o++ = ' ';
  o = fmt_u(o, GetCurrentThreadId(), 10, 0); *o++ = ' ';
  o = vfmt(o, f, ap);
  __builtin_va_end(ap);
  *o++ = '\r'; *o++ = '\n';
  EnterCriticalSection(g_cs);
  if (g_log && g_log != INVALID_HANDLE) { u32 w; WriteFile(g_log, buf, (u32)(o - buf), &w, 0); }
  LeaveCriticalSection(g_cs);
}

static const char* module_of(const void* addr, char* out, u32 cap) {
  HMODULE m = 0;
  out[0] = 0;
  if (GetModuleHandleExA(4 /*FROM_ADDRESS*/ | 2 /*UNCHANGED_REFCOUNT*/, addr, &m) && m) {
    char path[600]; u32 n = GetModuleFileNameA(m, path, sizeof(path));
    const char* b = path; for (u32 i = 0; i < n; i++) if (path[i] == '\\') b = path + i + 1;
    u32 l = slen(b); if (l >= cap) l = cap - 1; memcpy(out, b, l); out[l] = 0;
  }
  return out;
}

static const char* rw_desc(u32 kind) { return kind == 0 ? " reading " : kind == 1 ? " writing " : " executing "; }

// "module+0xoffset" for a code address.
static char* addr_desc(char* o, u32 addr) {
  HMODULE m = 0;
  if (GetModuleHandleExA(4 | 2, (const void*)addr, &m) && m) {
    char name[128]; module_of((const void*)addr, name, sizeof(name));
    o = scpy(o, name); *o++ = '+'; *o++ = '0'; *o++ = 'x';
    o = fmt_u(o, addr - (u32)m, 16, 0);
  } else { *o++ = '?'; *o++ = '0'; *o++ = 'x'; o = fmt_u(o, addr, 16, 8); }
  *o = 0;
  return o;
}

// Crash diagnostics: log access violations / illegal instructions with the faulting address and
// the code addresses found on the stack (heuristic backtrace). First 8 only (some are handled).
__declspec(dllimport) void* WINAPI AddVectoredExceptionHandler(u32, long (WINAPI*)(void*));
__declspec(dllimport) BOOL WINAPI IsBadReadPtr(const void*, u32);
static long WINAPI on_exception(void* pointers) {
  u32* ep = (u32*)pointers;
  u32* rec = (u32*)ep[0];
  unsigned char* ctx = (unsigned char*)ep[1];
  u32 code = rec[0];
  // OutputDebugString raises DBG_PRINTEXCEPTION_C (A) / DBG_PRINTEXCEPTION_WIDE_C (W): params len, ptr.
  if ((code == 0x40010006u || code == 0x4001000Au) && rec[4] >= 2) {
    static volatile long dbg_count;
    if (++dbg_count <= 400) {
      char line[1024]; char* o = line; o = scpy(o, "DEBUG ");
      u32 len = rec[5]; if (len > 900) len = 900;
      if (code == 0x40010006u) { const char* s = (const char*)rec[6]; for (u32 i = 0; i < len && s[i]; i++) *o++ = (s[i] == '\r' || s[i] == '\n') ? ' ' : s[i]; }
      else { const unsigned short* s = (const unsigned short*)rec[6]; for (u32 i = 0; i < len && s[i]; i++) *o++ = (s[i] < 128 && s[i] != '\r' && s[i] != '\n') ? (char)s[i] : ' '; }
      *o = 0; plog("%s", line);
    }
    return 0;
  }
  if (code == 0xE06D7363u) {   // C++ exception
    static volatile long cxx_count;
    if (++cxx_count <= 20) { char line[300]; char* o = line; o = scpy(o, "C++ exception thrown from "); addr_desc(o, rec[3]); plog("%s", line); }
    return 0;
  }
  if (code != 0xC0000005u && code != 0xC000001Du && code != 0xC0000096u) return 0;
  static volatile long count;
  if (++count > 8) return 0;
  u32 eip = *(u32*)(ctx + 0xB8), esp = *(u32*)(ctx + 0xC4);
  char line[1500]; char* o = line;
  o = scpy(o, "EXCEPTION "); o = fmt_u(o, code, 16, 8); o = scpy(o, " at "); o = addr_desc(o, eip);
  if (code == 0xC0000005u) { o = scpy(o, rw_desc(rec[5])); o = fmt_u(o, rec[6], 16, 8); }
  o = scpy(o, " eax="); o = fmt_u(o, *(u32*)(ctx + 0xB0), 16, 8);
  o = scpy(o, " ecx="); o = fmt_u(o, *(u32*)(ctx + 0xAC), 16, 8);
  o = scpy(o, " edx="); o = fmt_u(o, *(u32*)(ctx + 0xA8), 16, 8);
  o = scpy(o, " esi="); o = fmt_u(o, *(u32*)(ctx + 0xA0), 16, 8);
  o = scpy(o, " edi="); o = fmt_u(o, *(u32*)(ctx + 0x9C), 16, 8);
  plog("%s", line);
  // Objects behind the general registers (and [esi+0x18], the call site of crash #1): vtable owner.
  {
    static const int regoff[] = {0xB0, 0xAC, 0xA8, 0xA4, 0xA0, 0x9C, 0xB4};  // eax ecx edx ebx esi edi ebp
    static const char* regname[] = {"eax", "ecx", "edx", "ebx", "esi", "edi", "ebp"};
    for (int i = 0; i < 8; i++) {
      u32 obj = i < 7 ? *(u32*)(ctx + regoff[i]) : 0;
      const char* nm = i < 7 ? regname[i] : "[esi+18]";
      if (i == 7) { u32 esi = *(u32*)(ctx + 0xA0); if (IsBadReadPtr((void*)(esi + 0x18), 4)) continue; obj = *(u32*)(esi + 0x18); }
      if (obj < 0x10000 || IsBadReadPtr((void*)obj, 4)) continue;
      u32 vt = *(u32*)obj;
      if (vt < 0x10000 || IsBadReadPtr((void*)vt, 16)) continue;
      o = line; o = scpy(o, "  "); o = scpy(o, nm); o = scpy(o, "="); o = fmt_u(o, obj, 16, 8);
      o = scpy(o, " vtable="); o = addr_desc(o, vt); o = scpy(o, " [");
      for (int k = 0; k < 4; k++) { if (k) *o++ = ' '; o = addr_desc(o, ((u32*)vt)[k]); }
      *o++ = ']'; *o = 0;
      plog("%s", line);
    }
  }
  u32 stack_top; __asm__ volatile("movl %%fs:4, %0" : "=r"(stack_top));
  o = line; o = scpy(o, "  stack:");
  int found = 0;
  for (u32 p = esp; p + 4 <= stack_top && p < esp + 0x4000 && found < 16; p += 4) {
    u32 v = *(u32*)p;
    HMODULE m = 0;
    if (v > 0x10000 && GetModuleHandleExA(4 | 2, (const void*)v, &m) && m) {
      *o++ = ' '; o = addr_desc(o, v); found++;
    }
  }
  plog("%s", line);
  return 0;   // continue search: the game's own handlers still run
}

// ---- export table plumbing (names in nvcuda_exports.txt order, generated) ----
#include "exports.inc"   // static const char* const k_exports[]; enum { kNumExports = ... };

#define REAL(name) ((fn_##name)GetProcAddress(g_real, #name))

// ---- module image helpers ----
static u32 image_size(const void* img, const char** kind) {
  const u32* w = (const u32*)img;
  if (w[0] == 0xBA55ED50u) { *kind = "fatbin"; return 16 + (u32)(*(const u64*)(w + 2)); }
  if (w[0] == 0x464C457Fu) {   // ELF32 cubin: size = shoff + shnum*shentsize
    const unsigned char* e = img; *kind = "cubin";
    u32 shoff = *(const u32*)(e + 0x20); u32 shentsize = *(const unsigned short*)(e + 0x2e);
    u32 shnum = *(const unsigned short*)(e + 0x30);
    return shoff + shnum * shentsize;
  }
  if (w[0] == 0x466243b1u) { *kind = "fatbin-wrapper"; return 0; }
  *kind = "ptx?"; return slen((const char*)img) + 1;
}
static u32 fnv1a(const void* p, u32 n) {
  const unsigned char* b = p; u32 h = 2166136261u;
  for (u32 i = 0; i < n; i++) { h ^= b[i]; h *= 16777619u; }
  return h;
}
static void hexname(char* o, const char* sub, u32 h, const char* ext) {
  o = scpy(o, g_dir); o = scpy(o, sub); *o++ = '\\';
  o = fmt_u(o, h, 16, 8); scpy(o, ext);
}
static void* read_file(const char* path, u32* size) {
  HANDLE f = CreateFileA(path, GENERIC_READ, FILE_SHARE_READ, 0, OPEN_EXISTING, 0, 0);
  if (f == INVALID_HANDLE) return 0;
  u32 n = GetFileSize(f, 0);
  char* buf = VirtualAlloc(0, n + 1, 0x3000, 4);
  u32 r = 0; ReadFile(f, buf, n, &r, 0); CloseHandle(f);
  buf[r] = 0; *size = r;
  return buf;
}

typedef CUresult (CUDAAPI* fn_cuModuleLoadDataEx)(void**, const void*, u32, int*, void**);
typedef CUresult (CUDAAPI* fn_cuModuleLoadData)(void**, const void*);
typedef CUresult (CUDAAPI* fn_cuModuleGetFunction)(void**, void*, const char*);
typedef CUresult (CUDAAPI* fn_cuModuleGetTexRef)(void**, void*, const char*);
typedef CUresult (CUDAAPI* fn_cuModuleGetSurfRef)(void**, void*, const char*);
typedef CUresult (CUDAAPI* fn_cuModuleGetGlobal_v2)(u32*, u32*, void*, const char*);
typedef CUresult (CUDAAPI* fn_cuDeviceComputeCapability)(int*, int*, int);

static CUresult load_common(void** mod, const void* image, u32 nopt, int* opts, void** vals, const void* caller) {
  char owner[128]; module_of(caller, owner, sizeof(owner));
  const char* kind; u32 size = image_size(image, &kind);
  u32 h = size ? fnv1a(image, size) : 0;
  char path[700];
  if (size) {
    hexname(path, "cuda_dump", h, ".bin");
    if (GetFileAttributesA(path) == INVALID_FILE_ATTRIBUTES) {
      HANDLE f = CreateFileA(path, GENERIC_WRITE, 0, 0, CREATE_ALWAYS, 0, 0);
      if (f != INVALID_HANDLE) { u32 w; WriteFile(f, image, size, &w, 0); CloseHandle(f); }
    }
  }
  fn_cuModuleLoadDataEx real = REAL(cuModuleLoadDataEx);
  // Replacement PTX?
  hexname(path, "cuda_ptx", h, ".ptx");
  u32 psize = 0; char* ptx = size ? read_file(path, &psize) : 0;
  if (ptx) {
    static char errlog[8192]; errlog[0] = 0;
    int o[3] = {5 /*CU_JIT_ERROR_LOG_BUFFER*/, 6 /*..._SIZE_BYTES*/, 0};
    void* v[3] = {errlog, (void*)sizeof(errlog), 0};
    CUresult r = real(mod, ptx, 2, o, v);
    plog("LOAD %s %s size=%u hash=%08x -> REPLACED by cuda_ptx\\%08x.ptx (%u bytes) = %d mod=%x%s%s",
         owner, kind, size, h, h, psize, r, mod ? (u32)*mod : 0, errlog[0] ? " jitlog: " : "", errlog);
    VirtualFree(ptx, 0, 0x8000);
    if (r == 0) return 0;
  }
  CUresult r = real(mod, image, nopt, opts, vals);
  plog("LOAD %s %s size=%u hash=%08x nopt=%u = %d mod=%x", owner, kind, size, h, nopt, r, (r == 0 && mod) ? (u32)*mod : 0);
  return r;
}

__declspec(noinline) CUresult CUDAAPI hk_cuModuleLoadDataEx(void** mod, const void* image, u32 nopt, int* opts, void** vals) {
  return load_common(mod, image, nopt, opts, vals, __builtin_return_address(0));
}
__declspec(noinline) CUresult CUDAAPI hk_cuModuleLoadData(void** mod, const void* image) {
  return load_common(mod, image, 0, 0, 0, __builtin_return_address(0));
}
CUresult CUDAAPI hk_cuModuleGetFunction(void** f, void* m, const char* name) {
  CUresult r = REAL(cuModuleGetFunction)(f, m, name);
  plog("  getFunction mod=%x %s = %d", (u32)m, name, r);
  return r;
}
CUresult CUDAAPI hk_cuModuleGetTexRef(void** t, void* m, const char* name) {
  CUresult r = REAL(cuModuleGetTexRef)(t, m, name);
  plog("  getTexRef mod=%x %s = %d", (u32)m, name, r);
  return r;
}
CUresult CUDAAPI hk_cuModuleGetSurfRef(void** t, void* m, const char* name) {
  CUresult r = REAL(cuModuleGetSurfRef)(t, m, name);
  plog("  getSurfRef mod=%x %s = %d", (u32)m, name, r);
  return r;
}
CUresult CUDAAPI hk_cuModuleGetGlobal_v2(u32* p, u32* sz, void* m, const char* name) {
  CUresult r = REAL(cuModuleGetGlobal_v2)(p, sz, m, name);
  plog("  getGlobal mod=%x %s = %d size=%u", (u32)m, name, r, (r == 0 && sz) ? *sz : 0);
  return r;
}
// The game (TheBureau.exe, PhysX3Gpu) rejects unknown architectures before loading any module:
// report Kepler 3.0, the newest architecture its cubins were built for.
// Enabled only when a file named "cuda_fake_cc" sits next to the DLL.
static int g_fake_cc = 0;
#define FAKE_MAJOR 3
#define FAKE_MINOR 0
CUresult CUDAAPI hk_cuDeviceComputeCapability(int* major, int* minor, int dev) {
  char owner[128]; module_of(__builtin_return_address(0), owner, sizeof(owner));
  CUresult r = REAL(cuDeviceComputeCapability)(major, minor, dev);
  plog("computeCapability(%d) from %s = %d -> real %d.%d, reported %d.%d", dev, owner, r,
       major ? *major : -1, minor ? *minor : -1, g_fake_cc ? FAKE_MAJOR : (major ? *major : -1), g_fake_cc ? FAKE_MINOR : (minor ? *minor : -1));
  if (r == 0 && g_fake_cc) { if (major) *major = FAKE_MAJOR; if (minor) *minor = FAKE_MINOR; }
  return r;
}
typedef CUresult (CUDAAPI* fn_cuDeviceGetAttribute)(int*, int, int);
CUresult CUDAAPI hk_cuDeviceGetAttribute(int* v, int attr, int dev) {
  CUresult r = REAL(cuDeviceGetAttribute)(v, attr, dev);
  int real = (r == 0 && v) ? *v : -1;
  if (r == 0 && v && g_fake_cc && attr == 75) *v = FAKE_MAJOR;   // CU_DEVICE_ATTRIBUTE_COMPUTE_CAPABILITY_MAJOR
  if (r == 0 && v && g_fake_cc && attr == 76) *v = FAKE_MINOR;   // ..._MINOR
  static unsigned char seen[256];
  if (attr >= 0 && attr < 256 && !seen[attr]) {
    seen[attr] = 1;
    char owner[128]; module_of(__builtin_return_address(0), owner, sizeof(owner));
    plog("getAttribute(%d) from %s = %d -> real %d, reported %d", attr, owner, r, real, v ? *v : -1);
  }
  return r;
}
typedef CUresult (CUDAAPI* fn_cuCtxCreate_v2)(void**, u32, int);
CUresult CUDAAPI hk_cuCtxCreate_v2(void** ctx, u32 flags, int dev) {
  char owner[128]; module_of(__builtin_return_address(0), owner, sizeof(owner));
  CUresult r = REAL(cuCtxCreate_v2)(ctx, flags, dev);
  plog("ctxCreate flags=%x dev=%d from %s = %d", flags, dev, owner, r);
  return r;
}
// Launch failures: log the first few per error code.
typedef CUresult (CUDAAPI* fn_cuLaunchGridAsync)(void*, int, int, void*);
typedef CUresult (CUDAAPI* fn_cuLaunchGrid)(void*, int, int);
static void launch_result(const char* api, void* f, CUresult r) {
  static u32 ok_count, fail_count;
  if (r == 0) { if (++ok_count == 1 || ok_count == 1000 || ok_count == 100000) plog("%s: %u successful launches", api, ok_count); return; }
  if (++fail_count <= 20) plog("%s func=%x FAILED = %d", api, (u32)f, r);
}
CUresult CUDAAPI hk_cuLaunchGridAsync(void* f, int w, int h, void* stream) {
  CUresult r = REAL(cuLaunchGridAsync)(f, w, h, stream); launch_result("launchGridAsync", f, r); return r;
}
CUresult CUDAAPI hk_cuLaunchGrid(void* f, int w, int h) {
  CUresult r = REAL(cuLaunchGrid)(f, w, h); launch_result("launchGrid", f, r); return r;
}

// Graphics interop / array plumbing (the path from simulation results to rendering): every failure
// (first 20 per function) and the first 6 successes per function, with caller and raw arguments.
#define MAX_TRACKED 64
static const char* g_tr_name[MAX_TRACKED];
static u32 g_tr_ok[MAX_TRACKED], g_tr_fail[MAX_TRACKED];
static void note_call(const char* name, CUresult r, const void* caller, u32 a, u32 b, u32 c, u32 d) {
  int i = 0;
  EnterCriticalSection(g_cs);
  for (; i < MAX_TRACKED && g_tr_name[i] && g_tr_name[i] != name; i++) {}
  if (i < MAX_TRACKED && !g_tr_name[i]) g_tr_name[i] = name;
  u32 n = 0;
  if (i < MAX_TRACKED) n = r == 0 ? ++g_tr_ok[i] : ++g_tr_fail[i];
  LeaveCriticalSection(g_cs);
  if ((r == 0 && n <= 6) || (r != 0 && n <= 20)) {
    char owner[128]; module_of(caller, owner, sizeof(owner));
    plog("%s%s(%x, %x, %x, %x) from %s = %d", r ? "FAIL " : "", name, a, b, c, d, owner, r);
  }
}
#define TRACK1(fn) typedef CUresult (CUDAAPI* fn_##fn)(u32); \
  __declspec(noinline) CUresult CUDAAPI hk_##fn(u32 a) { CUresult r = REAL(fn)(a); \
    note_call(#fn, r, __builtin_return_address(0), a, 0, 0, 0); return r; }
#define TRACK2(fn) typedef CUresult (CUDAAPI* fn_##fn)(u32, u32); \
  __declspec(noinline) CUresult CUDAAPI hk_##fn(u32 a, u32 b) { CUresult r = REAL(fn)(a, b); \
    note_call(#fn, r, __builtin_return_address(0), a, b, 0, 0); return r; }
#define TRACK3(fn) typedef CUresult (CUDAAPI* fn_##fn)(u32, u32, u32); \
  __declspec(noinline) CUresult CUDAAPI hk_##fn(u32 a, u32 b, u32 c) { CUresult r = REAL(fn)(a, b, c); \
    note_call(#fn, r, __builtin_return_address(0), a, b, c, 0); return r; }
#define TRACK4(fn) typedef CUresult (CUDAAPI* fn_##fn)(u32, u32, u32, u32); \
  __declspec(noinline) CUresult CUDAAPI hk_##fn(u32 a, u32 b, u32 c, u32 d) { CUresult r = REAL(fn)(a, b, c, d); \
    note_call(#fn, r, __builtin_return_address(0), a, b, c, d); return r; }
TRACK3(cuGraphicsD3D11RegisterResource)
TRACK3(cuGraphicsD3D10RegisterResource)
TRACK3(cuGraphicsD3D9RegisterResource)
TRACK3(cuGraphicsGLRegisterBuffer)
TRACK1(cuGraphicsUnregisterResource)
TRACK3(cuGraphicsMapResources)
TRACK3(cuGraphicsUnmapResources)
TRACK2(cuGraphicsResourceSetMapFlags)
TRACK3(cuGraphicsResourceGetMappedPointer_v2)
TRACK4(cuGraphicsSubResourceGetMappedArray)
TRACK4(cuD3D11CtxCreate_v2)
TRACK4(cuD3D10CtxCreate_v2)
TRACK4(cuD3D9CtxCreate_v2)
TRACK2(cuMemcpy2DAsync_v2)
TRACK2(cuArray3DGetDescriptor_v2)
TRACK3(cuSurfRefSetArray)
TRACK3(cuTexRefSetArray)
TRACK4(cuMemcpyHtoDAsync_v2)
TRACK4(cuMemcpyDtoHAsync_v2)
TRACK3(cuMemHostAlloc)
TRACK3(cuMemHostGetDevicePointer_v2)
TRACK2(cuMemAlloc_v2)
TRACK1(cuStreamSynchronize)
TRACK2(cuStreamCreate)
TRACK2(cuEventCreate)
TRACK2(cuEventRecord)
TRACK1(cuEventQuery)
TRACK1(cuEventSynchronize)
TRACK4(cuMemsetD32Async)
TRACK4(cuTexRefSetAddress_v2)
TRACK3(cuTexRefSetFormat)

BOOL WINAPI DllMain(HMODULE self, u32 reason, void* reserved) {
  (void)reserved;
  if (reason != 1) return 1;
  InitializeCriticalSection(g_cs);
  u32 n = GetModuleFileNameA(self, g_dir, sizeof(g_dir) - 1);
  while (n && g_dir[n - 1] != '\\') n--;
  g_dir[n] = 0;
  char path[700];
  scpy(scpy(path, g_dir), "bureau_cuda.log");
  g_log = CreateFileA(path, GENERIC_WRITE, FILE_SHARE_READ, 0, CREATE_ALWAYS, 0, 0);
  scpy(scpy(path, g_dir), "cuda_dump"); CreateDirectoryA(path, 0);
  scpy(scpy(path, g_dir), "cuda_fake_cc"); g_fake_cc = GetFileAttributesA(path) != INVALID_FILE_ATTRIBUTES;
  g_real = LoadLibraryA("C:\\Windows\\SysWOW64\\nvcuda.dll");
  if (!g_real) { plog("FATAL: cannot load real nvcuda.dll"); return 0; }
  u32 missing = 0;
  for (u32 i = 0; i < kNumExports; i++) {
    g_real_fns[i] = GetProcAddress(g_real, k_exports[i]);
    if (!g_real_fns[i]) missing++;
  }
  plog("nvcuda proxy loaded, %u exports forwarded (%u missing), fake_cc=%d, dir=%s", kNumExports, missing, g_fake_cc, g_dir);
  AddVectoredExceptionHandler(1, on_exception);
  return 1;
}
