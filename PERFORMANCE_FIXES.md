# Performance pass - smooth, low-latency UI

Built on the user's latest repo (all earlier fixes kept). Not compiled/run in the authoring environment (no .NET SDK):
build with the GitHub Actions workflow and test.

## What was slow, and the fix
1. **Long lists drew every row at once.** Members, Total/Active/Expiring/Expired/Due and Expired Archive now use a
   virtualizing list (`GymVirtualList` in Assets/Theme.xaml): only the rows on screen are built, containers are recycled,
   scrolling is per-pixel.
2. **Search boxes re-filtered and re-drew on every keystroke** (typing AND holding Backspace). Every search box now filters
   ~150 ms after the user pauses (`Debouncer`), swaps the list with ONE change notification (`BulkObservableCollection`
   instead of Clear + Add per row) and re-uses row objects built once instead of creating them per keystroke.
   Boxes covered: Members, Total Members, Due Members, Expired Archive, Attendance Logs. Attendance search also no longer
   re-reads the database per keystroke - it filters the day's records already in memory.
3. **Fingerprint templates were decrypted for every member on every screen.** `Repository.GetAll()` decrypts each member's
   template (Windows DPAPI) even where it is never used. List screens, the Dashboard, Attendance rows and the 30-day archive
   sweep now use light reads (`GetAllForList`, `GetAllByNameForList`, `GetByIdForList`) that skip the template. (Profile,
   Renew, Edit and every Save still use the full read - nothing that saves is ever given a template-less member.)
4. **Scanner member list refreshed on a 10-second timer** (a pause on the UI thread that grew with the member count, and up
   to 10 s before a new finger worked). Now like Android's `observeAll().collect`: the repository raises
   `EnrolledFingerprintsChanged` only when a fingerprint is added/replaced/removed, a member is deleted/archived, a backup is
   restored or a sync batch is applied; the loop then reloads once (quick DB read on the UI thread, decryption on a worker
   thread). Renewals and check-in attendance stamps do NOT trigger it. A 5-minute safety-net reload remains.
5. **Startup did housekeeping before the window appeared.** The expired-member archive sweep and attendance cleanup now run
   right after the main window is shown (UI idle). If the sweep archived anyone and a read-only list is open, it is rebuilt.
6. **Member photos were re-read and re-decoded from disk constantly, and each ring rebuilt itself 3-4 times per row.**
   Small in-memory photo cache (keyed by file path + last-write time + size, so a replaced photo still refreshes; 400 max);
   `StatusRing` coalesces its property changes into one refresh and shares one frozen glow effect per status colour.
7. **Database:** indexes on members(name) and members(expiryMillis); 16 MB page cache and in-memory temp store. Durability
   (`synchronous`) deliberately unchanged.
8. **Build:** the installer is published ReadyToRun (precompiled, faster cold start) with an automatic fallback to the normal
   publish if that step ever fails; English-only satellite resources.

## Behaviour that is unchanged
Look and feel, sorting (SQL BINARY name order), search matching rules, status/expiry logic, dashboard numbers, selection and
delete rules in Expired Archive, and all scanner/enrollment behaviour.
