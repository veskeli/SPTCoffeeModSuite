# SPTCoffeeSuite

## Rider workflow

This workspace now has a single top-level solution: `SPTCoffeeModSuite.sln`.
Open that solution in Rider so all three migrated projects are available in one place.

### Run configurations

For normal app startup, create a **.NET Project** run configuration in Rider:

- `SPTCoffeeModManager Run` -> `src/SPTCoffeeModManager/SPTCoffeeModManager.csproj`
- `SPTServerManager Run` -> `src/SPTServerManager/SPTServerManager.csproj`
- `SPTServerConsole Run` -> `src/SPTServerConsole/SPTServerConsole.csproj`

That replaces opening the old per-project solution files just to launch an app.

### Publish configurations

Each project now has a shared publish profile named `RiderPublish`:

- `src/SPTCoffeeModManager/Properties/PublishProfiles/RiderPublish.pubxml`
- `src/SPTServerManager/Properties/PublishProfiles/RiderPublish.pubxml`
- `src/SPTServerConsole/Properties/PublishProfiles/RiderPublish.pubxml`

Those profiles publish a release, self-contained, single-file `win-x64` build into the project's local `publish/` folder.

Create an **MSBuild** run configuration in Rider for each project with:

- **Project file**: the relevant `.csproj`
- **Target**: `Publish`
- **Arguments**: `/p:PublishProfile=RiderPublish`

Suggested names:

- `SPTCoffeeModManager Publish`
- `SPTServerManager Publish`
- `SPTServerConsole Publish`

### Equivalent terminal fallback

```powershell
# SPTCoffeeModManager
dotnet publish .\src\SPTCoffeeModManager\SPTCoffeeModManager.csproj /p:PublishProfile=RiderPublish

# SPTServerManager
dotnet publish .\src\SPTServerManager\SPTServerManager.csproj /p:PublishProfile=RiderPublish

# SPTServerConsole
dotnet publish .\src\SPTServerConsole\SPTServerConsole.csproj /p:PublishProfile=RiderPublish
```

### Notes

- The old `publish.ps1` scripts can stay as fallback helpers, but Rider no longer needs them for the common publish flow.
- Published output still goes to each project's `publish/` directory, which is already ignored by git.

