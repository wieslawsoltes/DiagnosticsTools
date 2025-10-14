# DiagnosticsTools

A standalone diagnostic tools library for Avalonia UI applications, extracted from the core Avalonia repository.

## About

This project is based on the original `Avalonia.Diagnostics` code from the [AvaloniaUI repository](https://github.com/AvaloniaUI/Avalonia). It has been extracted into a standalone project that uses Avalonia NuGet packages instead of project references, making it easier to develop and maintain diagnostic tools independently.

## Original Source

The original code can be found in the main Avalonia repository:
- **Repository**: https://github.com/AvaloniaUI/Avalonia
- **Original Location**: `src/Avalonia.Diagnostics`

## License

This project inherits the MIT License from the original Avalonia project.

See [AVALONIA-LICENSE.md](./AVALONIA-LICENSE.md) for the full Avalonia license text.

Copyright (c) AvaloniaUI OÜ

## Features

- DevTools window with visual tree inspection
- Property editor for runtime UI manipulation
- Layout explorer with visual guidelines
- Event monitoring and debugging
- Hot key configuration
- Style and resource inspection

## Usage

See the [sample project](./samples/DiagnosticsToolsSample) for an example of how to integrate DiagnosticsTools into your Avalonia application.

## Building

```bash
dotnet build DiagnosticsTools.sln
```

## Attribution

This project contains code originally authored by the AvaloniaUI team and contributors. We are grateful for their work in creating these powerful diagnostic tools for the Avalonia community.
