# Windows parity fixes (Android = golden source)

1. Dashboard: "NEEDS ATTENTION (n)" list (members with 0-7 days left, soonest first) below Expired Archive. Views/DashboardView.xaml, ViewModels/DashboardViewModel.cs
2. Member cards: whole card opens the profile (Renew still renews). Controls/MemberRowView.xaml
3. Attendance Logs: date + filter popups no longer close themselves. Views/AttendanceLogsView.xaml(.cs)
4. Sync photos: restore now logs photo bytes; sync keeps existing photo when peer sends none; orphaned photos re-attached and republished. Data/Repository.cs, SyncChangeCodec.cs, BackupService.cs, SyncManager.cs
5. Dashboard privacy: master Privacy Mode now hides the dashboard; hidden numbers are blank like Android. Views/DashboardView.xaml

Not compiled here (no Windows/.NET SDK) - build with the GitHub Actions workflow and test.
