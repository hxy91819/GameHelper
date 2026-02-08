## ADDED Requirements

### Requirement: Game configuration list endpoint
The system SHALL expose a GET `/api/games` endpoint that returns all game configurations as a JSON array.

#### Scenario: Retrieve all games
- **WHEN** a GET request is made to `/api/games`
- **THEN** the response status SHALL be 200
- **THEN** the response body SHALL be a JSON array of game objects, each containing `dataKey`, `executableName`, `executablePath`, `displayName`, `isEnabled`, and `hdrEnabled` fields

#### Scenario: No games configured
- **WHEN** a GET request is made to `/api/games` and no games are configured
- **THEN** the response status SHALL be 200
- **THEN** the response body SHALL be an empty JSON array `[]`

### Requirement: Add game endpoint
The system SHALL expose a POST `/api/games` endpoint that creates a new game configuration.

#### Scenario: Successfully add a game
- **WHEN** a POST request is made to `/api/games` with a JSON body containing at least `executableName`
- **THEN** the response status SHALL be 201
- **THEN** the game SHALL be persisted via `IConfigProvider.Save`
- **THEN** the response body SHALL contain the created game object

#### Scenario: Add game with missing executable name
- **WHEN** a POST request is made to `/api/games` with an empty or missing `executableName`
- **THEN** the response status SHALL be 400
- **THEN** the response body SHALL contain an error message

### Requirement: Update game endpoint
The system SHALL expose a PUT `/api/games/{dataKey}` endpoint that updates an existing game configuration.

#### Scenario: Successfully update a game
- **WHEN** a PUT request is made to `/api/games/{dataKey}` with a JSON body containing updated fields
- **THEN** the response status SHALL be 200
- **THEN** the updated configuration SHALL be persisted via `IConfigProvider.Save`

#### Scenario: Update non-existent game
- **WHEN** a PUT request is made to `/api/games/{dataKey}` where `dataKey` does not exist
- **THEN** the response status SHALL be 404

### Requirement: Delete game endpoint
The system SHALL expose a DELETE `/api/games/{dataKey}` endpoint that removes a game configuration.

#### Scenario: Successfully delete a game
- **WHEN** a DELETE request is made to `/api/games/{dataKey}` where `dataKey` exists
- **THEN** the response status SHALL be 204
- **THEN** the game SHALL be removed via `IConfigProvider.Save`

#### Scenario: Delete non-existent game
- **WHEN** a DELETE request is made to `/api/games/{dataKey}` where `dataKey` does not exist
- **THEN** the response status SHALL be 404

### Requirement: Global settings read endpoint
The system SHALL expose a GET `/api/settings` endpoint that returns the global application settings.

#### Scenario: Retrieve settings
- **WHEN** a GET request is made to `/api/settings`
- **THEN** the response status SHALL be 200
- **THEN** the response body SHALL contain `processMonitorType`, `autoStartInteractiveMonitor`, and `launchOnSystemStartup` fields

### Requirement: Global settings update endpoint
The system SHALL expose a PUT `/api/settings` endpoint that updates global application settings.

#### Scenario: Successfully update settings
- **WHEN** a PUT request is made to `/api/settings` with a JSON body containing updated fields
- **THEN** the response status SHALL be 200
- **THEN** the updated settings SHALL be persisted via `IAppConfigProvider`

### Requirement: Statistics list endpoint
The system SHALL expose a GET `/api/stats` endpoint that returns playtime statistics for all games.

#### Scenario: Retrieve all stats
- **WHEN** a GET request is made to `/api/stats`
- **THEN** the response status SHALL be 200
- **THEN** the response body SHALL be a JSON array where each entry contains `gameName`, `displayName`, `totalMinutes`, `recentMinutes` (last 14 days), `sessionCount`, and a `sessions` array

#### Scenario: No playtime data
- **WHEN** a GET request is made to `/api/stats` and no playtime data exists
- **THEN** the response status SHALL be 200
- **THEN** the response body SHALL be an empty JSON array `[]`

### Requirement: Single game statistics endpoint
The system SHALL expose a GET `/api/stats/{gameName}` endpoint that returns playtime statistics for a specific game.

#### Scenario: Retrieve stats for existing game
- **WHEN** a GET request is made to `/api/stats/{gameName}` where data exists
- **THEN** the response status SHALL be 200
- **THEN** the response body SHALL contain the game's playtime statistics

#### Scenario: Retrieve stats for unknown game
- **WHEN** a GET request is made to `/api/stats/{gameName}` where no data exists
- **THEN** the response status SHALL be 404

### Requirement: Web server lifecycle
The Web Server SHALL be an optional component that does not affect existing CLI or monitoring functionality.

#### Scenario: Start web server via CLI flag
- **WHEN** the application is launched with `--web` flag
- **THEN** the embedded Kestrel server SHALL start on the configured port (default 5123)
- **THEN** the server SHALL bind to `127.0.0.1` only

#### Scenario: Start web server with custom port
- **WHEN** the application is launched with `--web --port 8080`
- **THEN** the embedded Kestrel server SHALL start on port 8080

#### Scenario: Web server disabled by default
- **WHEN** the application is launched without `--web` flag
- **THEN** no HTTP server SHALL be started

### Requirement: CORS configuration
The API SHALL support CORS for local development.

#### Scenario: Development CORS
- **WHEN** a request from `http://localhost:3000` is made to the API
- **THEN** the response SHALL include appropriate CORS headers allowing the request
