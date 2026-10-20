# Moz Checker — research workspace 0.5.0

The user rejected both a decorative dark dashboard and the subsequent unstyled gray form. The current direction is a composed Windows research tool: restrained blue, clear hierarchy, dense tables, and generous enough control spacing.

- Canvas #F4F6F9, surface #FFFFFF, text #1D2939, secondary #526174, accent #2457A7, border #CBD3DE. Segoe UI 13; application name 21; resume message 16. A 4/8px spacing rhythm, 4/5px corner radii, no hero slogans, gradients, metric cards or decorative animations.
- The workspace opens on existing research, not an empty input form. Show a blue-tinted resume strip with remaining domain count, preserved completion count and one clear action. Collapse new-list input when stored work exists.
- New input, Resume and Retry are distinct actions: create a session, continue its checkpoint, or repeat eligible failed sources. Scope applies to the action; selected-session resume does not run unrelated queued work.
- Keep existing split sessions intact and offer all-session totals. No domain/file cap or new splitting. A 200-domain input preview and 200-row table page are presentation only.
- Header has compact brand, import/new-list/settings. Workspace has scope, progress, four tabs and a single search/filter/action toolbar. The data table receives remaining height.
- Consistent buttons, quiet dividers, 34px rows, 40px column headers, bold domain text, subtle selection. Visible keyboard focus never changes geometry. Generator fields have visible labels. Settings/evidence share the same visual resources.
- Persistent events are newest first, timestamped locally from UTC; waiting rows remain separate. History pages are anchored. No invented historical events.
- Grid pagination/search/filter/sort/export cover the full selected scope. Virtualize rows and columns. Read work in bounded SQLite cursor pages; import/query/export/resume/retry operate off the UI thread.
- WPF keyboard/Grid/virtualization recommendations were relevant. Generic catalog product styles returned irrelevant website/research-platform recommendations and were rejected; this tool's composition and palette are deliberate project-specific choices.
- Verify native screenshots with synthetic resumed data in regular and compact layouts, plus the empty state. The runner clamps a requested 1440×900 window to an actual 1044×788 capture; the compact capture is 1040×700. Record actual sizes rather than claiming an unobserved full-size render. These checks are not a high-DPI/accessibility certification.
