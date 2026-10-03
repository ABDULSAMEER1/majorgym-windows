# UI fixes - back buttons + Expired Archive bulk delete

## 1. Dedicated back button on every sub-screen
New shared style `GymBackButton` (Assets/Theme.xaml): a visible 40x40 rounded button with a back arrow, top-left,
beside the page heading (hover = accent border, keyboard-focusable, "Back" tooltip). Previously several pages had only a
bare arrow glyph or a plain "<- Back" text. Now used on:
Total / Active / Expiring Soon / Expired / Due Members (FilteredMembersView), Renew Membership, Renewed (back to the
member profile), Expired Archive, Archived Member detail, Member Profile, Add/Edit Member, Attendance, Attendance
History, Backup History. Back goes where it always did (Dashboard for the member lists, Profile for Renew, etc.).

## 2. Expired Archive
* Search bar (name / phone / last plan).
* Multi-select: tick box on every row; **Shift+click** selects a range (from the last clicked row); **Ctrl+click** adds or
  removes one; **Ctrl+A** selects everything shown; **Esc** clears; once anything is selected a plain click on a card toggles it
  instead of opening it (the arrow on the right always opens the member's details).
* "Select first [N]" count filter + Select all + Clear.
* **Delete N members** (works for 1 too) -> inline confirmation -> permanent delete in a single transaction
  (`Repository.DeleteArchivedMembersPermanently`). Local-only exactly like the existing single delete on the detail screen.
* Selection is cleared for any row hidden by the search, so a bulk delete only ever touches what is on screen.

Not compiled/run in the authoring environment (no .NET SDK) - build with the GitHub Actions workflow and test.
