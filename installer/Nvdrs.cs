// NVIDIA driver profile (NVAPI DRS) access for the installer: sets the RTX 50 "PhysX - Enable 32-bit GPU
// Acceleration" switch on the profile of an executable, like NVIDIA Profile Inspector does.
// Setting 0x5067ECC4 was identified by comparing driver profiles: NVIDIA predefines it to 1 for whitelisted
// 32-bit PhysX games (e.g. Batman: Arkham Origins); without it the driver hides the GPU from 32-bit CUDA.
using System;
using System.Runtime.InteropServices;

static class Nvdrs {
  public const uint Setting32BitPhysX = 0x5067ECC4;

  [DllImport("kernel32.dll", CharSet = CharSet.Ansi)] static extern IntPtr LoadLibrary(string name);
  [DllImport("kernel32.dll", CharSet = CharSet.Ansi)] static extern IntPtr GetProcAddress(IntPtr mod, string name);

  [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate IntPtr D_Query(uint id);
  [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int D_Init();
  [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int D_CreateSession(out IntPtr s);
  [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int D_Session(IntPtr s);
  [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int D_FindApp(IntPtr s, [MarshalAs(UnmanagedType.LPWStr)] string app, out IntPtr profile, IntPtr appStruct);
  [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int D_FindProfile(IntPtr s, [MarshalAs(UnmanagedType.LPWStr)] string name, out IntPtr profile);
  [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int D_CreateProfile(IntPtr s, IntPtr profileStruct, out IntPtr profile);
  [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int D_CreateApp(IntPtr s, IntPtr profile, IntPtr appStruct);
  [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int D_SetSetting(IntPtr s, IntPtr profile, IntPtr setting);
  [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int D_GetSetting(IntPtr s, IntPtr profile, uint id, IntPtr setting);
  [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int D_DeleteSetting(IntPtr s, IntPtr profile, uint id);
  [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int D_DeleteProfile(IntPtr s, IntPtr profile);

  const int STR = 4096;                                    // NvAPI_UnicodeString: 2048 UTF-16 units
  const int APP_V1 = 4 + 4 + STR * 3;                      // version, isPredefined, appName, userFriendlyName, launcher
  const int PROFILE_V1 = 4 + STR + 4 * 4;                  // version, profileName, gpuSupport, isPredefined, numOfApps, numOfSettings
  const int SETTING_V1 = 4 + STR + 4 * 5 + 4100 + 4100;    // version, name, id, type, location, isCurrentPredefined, isPredefinedValid, predefined, current

  static D_Query query;

  static T F<T>(uint id) where T : class {
    var p = query(id);
    if (p == IntPtr.Zero) throw new Exception(L.F("NVAPI-függvény nem érhető el (0x{0})", id.ToString("x8")));
    return Marshal.GetDelegateForFunctionPointer(p, typeof(T)) as T;
  }

  static IntPtr Zeroed(int size, int version) {
    var p = Marshal.AllocHGlobal(size);
    for (int i = 0; i < size; i++) Marshal.WriteByte(p, i, 0);
    Marshal.WriteInt32(p, 0, size | (version << 16));
    return p;
  }

  static void PutString(IntPtr p, int offset, string s) {
    var b = System.Text.Encoding.Unicode.GetBytes(s);
    Marshal.Copy(b, 0, p + offset, Math.Min(b.Length, STR - 2));
  }

  static void Check(int r, string what) { if (r != 0) throw new Exception(L.F("NVAPI {0} hiba: {1}", what, r)); }

  static IntPtr Open() {
    if (query == null) {
      var lib = LoadLibrary(IntPtr.Size == 8 ? "nvapi64.dll" : "nvapi.dll");
      if (lib == IntPtr.Zero) throw new Exception(L.T("Az NVIDIA driver (nvapi) nem található."));
      var q = GetProcAddress(lib, "nvapi_QueryInterface");
      if (q == IntPtr.Zero) throw new Exception(L.T("nvapi_QueryInterface nem található."));
      query = (D_Query)Marshal.GetDelegateForFunctionPointer(q, typeof(D_Query));
      Check(F<D_Init>(0x0150E828)(), "Initialize");
    }
    IntPtr s;
    Check(F<D_CreateSession>(0x0694D52E)(out s), "CreateSession");
    Check(F<D_Session>(0x375DBD6B)(s), "LoadSettings");
    return s;
  }

  static void Close(IntPtr s) { F<D_Session>(0xDAD9CFF8)(s); }

  // Profile that owns exe; created (with the exe attached) when missing and createName != null.
  static IntPtr ProfileFor(IntPtr s, string exe, string createName) {
    IntPtr prof;
    var app = Zeroed(APP_V1, 1);
    try {
      if (F<D_FindApp>(0xEEE566B2)(s, exe, out prof, app) == 0) return prof;
    } finally { Marshal.FreeHGlobal(app); }
    if (createName == null) return IntPtr.Zero;
    if (F<D_FindProfile>(0x7E4A9A0B)(s, createName, out prof) != 0) {
      var pi = Zeroed(PROFILE_V1, 1);
      PutString(pi, 4, createName);
      try { Check(F<D_CreateProfile>(0xCC176068)(s, pi, out prof), "CreateProfile"); } finally { Marshal.FreeHGlobal(pi); }
    }
    var a = Zeroed(APP_V1, 1);
    PutString(a, 8, exe);
    try { Check(F<D_CreateApp>(0x4347A9DE)(s, prof, a), "CreateApplication"); } finally { Marshal.FreeHGlobal(a); }
    return prof;
  }

  public const uint SettingFrameRateLimiter = 0x10835002;   // "Frame Rate Limiter" (= Control Panel "Max Frame Rate"), value = fps, 0 = off

  // Current DWORD value of a setting on exe's profile: -1 = no profile / not set.
  public static int GetDword(string exe, uint id) {
    var s = Open();
    try {
      var prof = ProfileFor(s, exe, null);
      if (prof == IntPtr.Zero) return -1;
      var st = Zeroed(SETTING_V1, 1);
      try {
        if (F<D_GetSetting>(0x73BF8338)(s, prof, id, st) != 0) return -1;
        return Marshal.ReadInt32(st, 4 + STR + 20 + 4100);
      } finally { Marshal.FreeHGlobal(st); }
    } finally { Close(s); }
  }

  // Applies settings to exe's profile (created when missing) in one session. value < 0 = delete the user value
  // (back to the driver default). Needs administrator rights.
  public static void SetDwords(string exe, string profileNameIfMissing, uint[] ids, int[] values) {
    var s = Open();
    try {
      var prof = ProfileFor(s, exe, profileNameIfMissing);
      for (int i = 0; i < ids.Length; i++) {
        if (values[i] < 0) { F<D_DeleteSetting>(0xE4A26362)(s, prof, ids[i]); continue; }
        var st = Zeroed(SETTING_V1, 1);
        try {
          Marshal.WriteInt32(st, 4 + STR, (int)ids[i]);                 // settingId
          Marshal.WriteInt32(st, 4 + STR + 4, 0);                       // NVDRS_DWORD_TYPE
          Marshal.WriteInt32(st, 4 + STR + 20 + 4100, values[i]);       // u32CurrentValue
          Check(F<D_SetSetting>(0x577DD202)(s, prof, st), "SetSetting 0x" + ids[i].ToString("x8"));
        } finally { Marshal.FreeHGlobal(st); }
      }
      Check(F<D_Session>(0xFCBC7E14)(s), "SaveSettings");
    } finally { Close(s); }
  }

  public static int Get32BitPhysX(string exe) { return GetDword(exe, Setting32BitPhysX); }

  // Test helper: delete the whole profile that owns exe.
  public static void DeleteProfileOf(string exe) {
    var s = Open();
    try {
      var prof = ProfileFor(s, exe, null);
      if (prof == IntPtr.Zero) return;
      Check(F<D_DeleteProfile>(0x17093206)(s, prof), "DeleteProfile");
      Check(F<D_Session>(0xFCBC7E14)(s), "SaveSettings");
    } finally { Close(s); }
  }

  // RTX 50 (Blackwell) present? These cards need the switch for 32-bit GPU PhysX.
  public static string FindRtx50() {
    try {
      using (var q = new System.Management.ManagementObjectSearcher("SELECT Name FROM Win32_VideoController"))
        foreach (System.Management.ManagementObject o in q.Get()) {
          var n = o["Name"] as string;
          if (n != null && System.Text.RegularExpressions.Regex.IsMatch(n, @"RTX\s*50[0-9]{2}", System.Text.RegularExpressions.RegexOptions.IgnoreCase)) return n;
        }
    } catch { }
    return null;
  }
}
