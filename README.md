# FbmodDecompiler

A C# command-line tool that uses the Frosty SDK to properly decompile `.fbmod` files to `.fbproject` format.

## Building

### Prerequisites
1. .NET Framework 4.8 SDK

### Steps
1. Build FrostyToolsuite in Visual Studio (Release configuration)
2. Build this project: `dotnet build`
3. Copy required DLLs from FrostyToolsuite output to this project's output folder

## Usage
```
FbmodDecompiler.exe <input.fbmod> <output.fbproject> <game_path>
```

Where:
- `<input.fbmod>` - The compiled mod file to decompile
- `<output.fbproject>` - The output project file path
- `<game_path>` - Path to the game installation (e.g., `C:\Games\PvZ GW2`)

## Example
```
FbmodDecompiler.exe TapeTweaks.fbmod TapeTweaks.fbproject "C:\Games\Plants vs Zombies Garden Warfare 2"
```

## Contact

Discord: verionz

