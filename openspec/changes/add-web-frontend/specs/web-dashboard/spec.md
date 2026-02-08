## ADDED Requirements

### Requirement: Application layout
The web dashboard SHALL provide a responsive sidebar layout with navigation links to the Configuration and Statistics pages.

#### Scenario: Navigate between pages
- **WHEN** the user clicks "Configuration" in the sidebar
- **THEN** the configuration management page SHALL be displayed
- **WHEN** the user clicks "Statistics" in the sidebar
- **THEN** the statistics dashboard page SHALL be displayed

#### Scenario: Responsive layout on small screens
- **WHEN** the viewport width is below 768px
- **THEN** the sidebar SHALL collapse into a mobile-friendly navigation menu

### Requirement: Game configuration list view
The configuration page SHALL display all configured games in a table with columns: Display Name, Executable Name, Enabled, HDR, and Actions.

#### Scenario: Display game list
- **WHEN** the user opens the configuration page
- **THEN** a table SHALL be rendered showing all games fetched from `GET /api/games`
- **THEN** each row SHALL show the game's display name, executable name, enabled status, HDR status, and action buttons (edit, delete)

#### Scenario: Empty game list
- **WHEN** no games are configured
- **THEN** the page SHALL display an empty state message with a prompt to add a game

### Requirement: Add game dialog
The configuration page SHALL provide a dialog to add a new game.

#### Scenario: Open add dialog
- **WHEN** the user clicks the "Add Game" button
- **THEN** a dialog SHALL appear with input fields for executable name, display name, enabled toggle, and HDR toggle

#### Scenario: Submit new game
- **WHEN** the user fills in the required fields and clicks "Save"
- **THEN** a POST request SHALL be sent to `/api/games`
- **THEN** on success, the game list SHALL refresh to include the new entry
- **THEN** the dialog SHALL close

#### Scenario: Validation error
- **WHEN** the user submits the form with an empty executable name
- **THEN** a validation error SHALL be displayed inline without closing the dialog

### Requirement: Edit game dialog
The configuration page SHALL allow editing an existing game's configuration.

#### Scenario: Open edit dialog
- **WHEN** the user clicks the "Edit" button on a game row
- **THEN** a dialog SHALL appear pre-filled with the game's current configuration

#### Scenario: Save edited game
- **WHEN** the user modifies fields and clicks "Save"
- **THEN** a PUT request SHALL be sent to `/api/games/{dataKey}`
- **THEN** on success, the game list SHALL refresh with updated data

### Requirement: Delete game confirmation
The configuration page SHALL require confirmation before deleting a game.

#### Scenario: Delete with confirmation
- **WHEN** the user clicks the "Delete" button on a game row
- **THEN** a confirmation dialog SHALL appear
- **WHEN** the user confirms deletion
- **THEN** a DELETE request SHALL be sent to `/api/games/{dataKey}`
- **THEN** on success, the game SHALL be removed from the list

#### Scenario: Cancel deletion
- **WHEN** the user clicks "Cancel" in the confirmation dialog
- **THEN** the game SHALL NOT be deleted

### Requirement: Global settings panel
The configuration page SHALL include a settings section for global application settings.

#### Scenario: Display current settings
- **WHEN** the user opens the configuration page
- **THEN** the global settings section SHALL display current values for process monitor type, auto-start monitor, and launch on system startup, fetched from `GET /api/settings`

#### Scenario: Update settings
- **WHEN** the user modifies a setting and clicks "Save"
- **THEN** a PUT request SHALL be sent to `/api/settings`
- **THEN** on success, a toast notification SHALL confirm the update

### Requirement: Statistics overview dashboard
The statistics page SHALL display an overview of all game playtime data with summary cards and a chart.

#### Scenario: Display statistics overview
- **WHEN** the user opens the statistics page
- **THEN** summary cards SHALL show total playtime, total sessions, and number of tracked games
- **THEN** a bar chart SHALL display playtime per game (sorted by recent activity)

#### Scenario: No statistics data
- **WHEN** no playtime data exists
- **THEN** the page SHALL display an empty state message

### Requirement: Per-game statistics detail
The statistics page SHALL allow viewing detailed session history for a specific game.

#### Scenario: View game detail
- **WHEN** the user clicks on a game in the statistics overview
- **THEN** the page SHALL display that game's session history in a table with columns: Start Time, End Time, Duration
- **THEN** a line chart SHALL show playtime trend over the last 14 days

### Requirement: Technology stack
The web dashboard SHALL be built with Next.js (App Router), TypeScript, Tailwind CSS, shadcn/ui components, and Recharts for data visualization.

#### Scenario: Project setup
- **WHEN** the `GameHelper.Web` project is initialized
- **THEN** it SHALL use Next.js 14+ with App Router and TypeScript
- **THEN** it SHALL use Tailwind CSS for styling
- **THEN** it SHALL use shadcn/ui for UI components
- **THEN** it SHALL use Recharts for chart rendering
- **THEN** it SHALL use SWR for data fetching and caching
