# DiagnosticsTools Sample

This sample demonstrates how to use the DiagnosticsTools library in an Avalonia application.

## Overview

DiagnosticsTools provides a powerful DevTools window for debugging and inspecting Avalonia UI applications at runtime.

## How to Use

### 1. Add Project Reference

In your `.csproj` file, add a reference to DiagnosticsTools (only in Debug configuration):

```xml
<ItemGroup>
  <!--Condition below is needed to remove DiagnosticsTools from build output in Release configuration.-->
  <ProjectReference Include="path\to\DiagnosticsTools.csproj" Condition="'$(Configuration)' == 'Debug'" />
</ItemGroup>
```

### 2. Attach DevTools to Your Application

In your `App.axaml.cs` file, call `AttachDevTools()` in the `OnFrameworkInitializationCompleted` method:

```csharp
public override void OnFrameworkInitializationCompleted()
{
    if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
    {
        desktop.MainWindow = new MainWindow();
    }

    base.OnFrameworkInitializationCompleted();

#if DEBUG
    // Attach DevTools to open with F12 key
    this.AttachDevTools();
#endif
}
```

### 3. Run Your Application

Build and run your application in Debug mode. Press **F12** to open the DevTools window.

## Features

Once DevTools is open, you can:

- **Inspect Visual Tree**: Navigate through the visual tree of your application
- **Edit Properties**: Modify control properties in real-time
- **View Layout**: Visualize layout information with the Layout Explorer
- **Monitor Events**: Track events as they occur in your application
- **Hot Keys**: View and configure keyboard shortcuts
- **Style Inspector**: Examine applied styles and resources

## Custom Configuration

You can customize DevTools behavior by passing options:

```csharp
this.AttachDevTools(new Avalonia.Diagnostics.DevToolsOptions()
{
    StartupScreenIndex = 1, // Start on a specific tab
    ShowAsChildWindow = false, // Show as separate window
});
```

## Building and Running

```bash
# Build
dotnet build

# Run
dotnet run
```

Press **F12** when the application window is open to launch DevTools.

## Notes

- DevTools is only included in Debug builds
- The DevTools window can be detached and moved to a separate monitor
- All changes made in DevTools are runtime-only and don't affect your source code
