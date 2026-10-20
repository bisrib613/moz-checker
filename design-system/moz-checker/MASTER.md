# Moz Checker — desktop design system

## Scope and provenance

Native Windows desktop UI built with PySide6/Qt Widgets; no web framework, external font service, or UI server. Reviewed using [UI/UX Pro Max](https://github.com/nextlevelbuilder/ui-ux-pro-max-skill).

The refined `analytics dashboard productivity` design-system query returned the **Data-Dense Dashboard** style (data tables, KPI cards, compact grids, clear filtering). That style and stack-independent keyboard/contrast/form guidance fit this product. The same query's Enterprise Gateway/marketing pattern did not fit and was discarded. There is no Qt Widgets stack in the skill's stack catalog; layout and implementation below are project-specific desktop decisions, not purported Qt search results.

## Visual direction

- Dark research workspace with a permanent input sidebar and a larger results area.
- Mint marks the primary action and available candidates; all states also have text, not color alone.
- Segoe UI on Windows with local sans-serif fallbacks. Monospace is limited to domain input, technical evidence, and logs. No remote font downloads.
- Semantic colors, original local SVG line icons, consistent 1.7 stroke, 6/12px corner radii and 4/8px spacing rhythm. See `src/moz_checker/presentation.py` for actual tokens.
- No decorative animation. Focus, hover, press, and disabled states have visible feedback without changing control bounds.

## Information hierarchy

1. Sidebar: input/import or keyword generator, one primary hunting action, local Moz quota.
2. Main header: workspace title and export.
3. Counters: Available, Sudah diperiksa, Riwayat lolos, Registered. These are derived from real saved results, never seeded demo numbers.
4. Selected hunting, progress, and job controls.
5. Available-only table with search and filter dialog; separate log for all outcomes.
6. Evidence on request in a separate dialog, so it does not consume table height. Dates/registrar and worker details are in this evidence view; authority metrics appear first in the table.

## Interaction

- Existing SQLite files and pipeline semantics remain compatible.
- Registered, redemption, pending delete and unknown never enter the shortlist or results CSV.
- Empty states distinguish a new workspace, no available candidates, and no filter matches.
- Search is case-insensitive. Reset clears the search and all filters. Active advanced filters show a count.
- Filter and evidence dialogs can be closed with Escape. API settings separate keys from hunting rules and allow scrolling at smaller heights.
- Ctrl+Enter: start/queue; Ctrl+O: import; Ctrl+F: focus shortlist search; Ctrl+comma: settings.
- Export is disabled on the log tab and when there are no visible results. Job actions reflect the selected hunting's state.
- Repeated background refreshes preserve the table and selection when results did not change.

## Size and accessibility checks

- Default workspace 1440×900 logical pixels; minimum 1040×700. Desktop scope; phone breakpoints from the skill are not applicable.
- Under 800px height, hide secondary captions/icon, compact spacing, and shorten the input area; keep the primary action in view. Sidebar and settings can scroll instead of clipping controls.
- Visible field labels, accessible names for controls and contextual stat cards, full native keyboard interaction, text descriptions of all states.
- Contrast measured for primary, muted, accent, error, and blue text against the relevant dark surfaces; target ≥4.5:1 for text. No claim of formal screen-reader certification or manual high-DPI Windows validation.
- Preview images with populated rows must be labeled simulated and use a temporary database. Never seed sample domains/metrics into the user's application data.

## WPF migration (0.3.0)

Visual direction and information hierarchy above carry over to native WPF. `dotnet/MozChecker.Desktop/App.xaml` defines semantic brushes and control states. The verified UI/UX Pro Max WPF search recommends native keyboard navigation and proportional Grid layout; the implementation uses both, with virtualized DataGrid rows/columns. Labels and automation names accompany fields. Native combo boxes retain platform popup semantics. The minimum desktop size remains 1040×700; sidebar and dialogs scroll. Mobile safe-area/phone checks are outside this Windows desktop scope. No decorative motion is introduced. Native Windows render and smoke checks are recorded in CI, with screen-reader and high-DPI validation still manual.

WPF contrast measurements: primary text 14.52:1 or higher and secondary text 8.30:1 or higher against the three workspace surfaces. The control-border token is #5E7B6F (minimum 3.48:1 against those surfaces); keyboard focus uses the mint accent. Disabled controls are exempt from the active-control contrast threshold. These are token measurements, not screen-reader or Windows high-contrast certification.
