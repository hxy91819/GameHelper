## REMOVED Requirements

### Requirement: Next.js web dashboard as primary management interface
**Reason**: Windows-only native desktop direction prioritizes WinUI shell consistency, reduced packaging complexity, and single main UI stack.
**Migration**: Replace dashboard workflows with WinUI shell pages and remove `GameHelper.Web` runtime dependency after parity and acceptance checks pass.

#### Scenario: Dashboard capability retirement
- **WHEN** WinUI shell provides accepted parity for core user journeys
- **THEN** the Next.js dashboard capability is retired from product runtime
