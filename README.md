# Batch Script Control Panel (WPF)

## Prerequisites

- .NET SDK 8.0+
- Windows

## Run

```powershell
cd BatchScriptControlPanel
dotnet run
```

## Features

- Light, minimal WPF interface
- Left-side script catalog with add/remove/select
- One-click run for selected BAT script
- DAG workflow runner with fan-out and fan-in dependencies
- Visual node builder (`Node Builder`) to link scripts on a canvas
- Parallel workflow execution with configurable `Max parallel`
- `Stop on first failure` option for workflow execution
- Workflow presets (save/load/delete named graph configurations)
- Safeguards: removal impact confirmation, preset auto-clean on script delete, preset validation badge
- Replace selected script path (`Change`)
- Live output log streaming
- Run/Stop/Clear controls
- Status and last exit code display
- Script catalog persistence across restarts

## Catalog storage

- Saved to `%LocalAppData%\\BatchScriptControlPanel\\scripts_catalog.json`

## Notes

- Only add trusted BAT files.
- Script execution runs through `cmd.exe /c` with output captured in the app.
- In Node Builder: drag nodes to arrange, right-click source then right-click target to toggle a link, then `Apply to Chain`.
- A node can feed multiple nodes, and multiple nodes can feed one node (acyclic graph required).
- Use `Save Preset` to store current workflow edges + run settings.
- Selecting a preset in the dropdown now auto-loads it (with validation checks).

