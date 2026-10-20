# Moz Checker — Windows desktop workspace (0.4.0)

The user explicitly rejected the previous dark mint dashboard, slogan, rounded cards and sidebar. This direction replaces that design for the C# desktop. Legacy Qt/Python styling is not the source of truth for WPF.

- Use the standard light Windows controls, Segoe UI 13, white data grid, gray toolbar and blue system selection. No hero text, metric cards, decorative branding or custom animation.
- Top toolbar: import, input reset, start, pause, resume, retry and settings. Input collapses after starting; the table receives the available window area.
- One imported list creates one examination, without a 1,000-domain split or a file-size cap. Large input shows an explicitly labeled 200-domain preview while retaining the entire parsed list for start.
- Scope selector defaults to all examinations, including the previous version's split jobs. Progress is total/done/pending/actually active within the selected scope. Actual running job IDs and session-state counts are stated separately.
- Four views: Available, Other results, Queue/in progress, Event log. Pending rows never masquerade as completed results or events.
- Events are persisted in SQLite with sequence number, UTC timestamp rendered in local computer time, examination, domain, stage, status and message. Newest first. Older pages are anchored; Terbaru follows the live stream again. Old result timestamps are not fabricated into events during migration.
- Main grids are virtualized and read 200 records per page. Every page shows its range and total. This is presentation paging, not a domain-processing limit. Search/filter/sort apply across the entire selected scope, and export streams all matching available results across all pages.
- Counters are maintained in SQLite, and background workers read pending records in small cursor pages. Neither UI refresh nor engine startup deserializes a half-million-record result list.
- Native focus rings, labeled controls, keyboard shortcuts and standard dialogs. The verified UI/UX Pro Max WPF guidance supports native keyboard navigation, proportional Grid layout and virtualization; it does not prescribe a marketing dashboard.
- API settings/evidence use the same light native resources. Primary text #202020 and muted text #555555 have sufficient contrast against the white/light gray surfaces. Do not claim high-DPI/screen-reader certification from runner screenshots.
