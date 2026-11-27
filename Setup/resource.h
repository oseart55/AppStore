/* Resource ids for AppStoreSetup. */
#define IDI_SETUP      100
#define IDR_NETCF      101   /* Microsoft .NET CF 3.5 for Windows Mobile (CAB) */
#define IDR_APPSTORE   102   /* AppStore.exe                                    */
#define IDR_CLOCKSYNC  103   /* ClockSync.exe                                   */

/* ClockSync has no version resource: keep this in step with the ClockSync.exe
   that setup.rc bundles (its GitHub release tag). App Store uses it to tell
   whether a newer ClockSync is out. */
#define CLOCKSYNC_TAG  "v1.0.1"
