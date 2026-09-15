# Coding Standards

How code in Music Assistant for Windows is written, so every change reads like the rest of the app.

## Introduction

This project follows two Microsoft guides, plus a handful of house rules on top:

- [.NET C# coding conventions](https://learn.microsoft.com/en-us/dotnet/csharp/fundamentals/coding-style/coding-conventions)
- [Windows app development best practices](https://learn.microsoft.com/en-us/windows/apps/get-started/best-practices)

Most of the C# rules are enforced by the build. `.editorconfig` holds the style rules, and the project file turns on `EnforceCodeStyleInBuild` and `GenerateDocumentationFile`, so drift shows up as a build warning. A change is done when the build has **0 warnings**.

> One deliberate deviation: the 65-character line limit in the C# conventions exists for code shown on the docs site and on phones, so it isn't applied here.

### Prerequisites

- .NET 9 SDK
- Windows App SDK 1.8 (restored from NuGet)
- US English everywhere: code, comments, UI text, commit messages and release notes

## C# Language

- Use modern C# features and the latest language version. Avoid outdated constructs.
- Use language keywords for types (`string`, `int`, `nint`), not runtime names (`String`, `Int32`, `IntPtr` in new code).
- Prefer `int` over unsigned types, except where an API or wire format needs them.
- Use `var` only when the type is obvious from the right side: `new`, an explicit cast or a literal. Otherwise write the type.
- Use `var` for `for` loop counters. Write the element type in `foreach` loops.
- Don't put the type in a variable name. Name what the value means.
- Use string interpolation for short strings and `StringBuilder` for strings built in loops. Prefer raw string literals over escape sequences for long text.
- Use collection expressions (`[]`, `[.. items]`) to create collections.
- Use target-typed `new()` when the type is already written on the left, and object initializers instead of setting properties one by one.
- Use `required` properties instead of constructors when a value must be set.
- Use camel case for primary constructor parameters on classes and structs, Pascal case on records.
- Use `Func<>` and `Action<>` instead of declaring delegate types, unless interop needs a named delegate.
- Use a lambda for an event handler that never has to be removed.
- Use `&&` and `||`, never `&` and `|`, for conditions.
- Call static members through the class that declares them.
- Use LINQ for collection work, with meaningful names. Put `where` before other clauses.
- Use `async`/`await` for I/O. `async void` is only for event handlers.
- Use file-scoped namespaces, with `using` directives outside the namespace and `System` directives first.
- Use the braceless `using` declaration instead of a `try`/`finally` that only calls `Dispose`.

### Exceptions

- Catch only the exceptions you can handle, by their specific types.
- A general catch is only allowed at a boundary where one failure must not take down more than itself: an event handler, a fire-and-forget task, a render thread or a per-connection loop. It must use the shared filter:

```csharp
catch (Exception ex) when (ExceptionFilters.IsRecoverable(ex))
{
    App.Log($"Speaker output stopped: {ex.Message}");
}
```

- Log and carry on when a third-party or network failure hits. Don't let it crash the app.
- Use specific exception types when throwing, with a message that says what went wrong.

## Style and Layout

- Four spaces for indentation, no tabs.
- Allman braces: every opening and closing brace on its own line.
- One statement and one declaration per line.
- Break long statements across lines, with the line break before the binary operator.
- Put parentheses around each clause of a compound condition:

```csharp
if ((request is null) || !IsUpgrade(request, out string key))
{
    return;
}
```

- Guard clauses first, then the work.
- Blank line between methods, between properties, and between unrelated groups of statements.
- Line up the `=` of related assignments and fields into a column:

```csharp
listener = candidate;
Port     = port;
```

- Split long classes into sections with a banner:

```csharp
// =========================================================================
// HANDSHAKE AND HELLO
// =========================================================================
```

## Comments and Documentation

- Every class, method, property, field and event that is public gets an XML doc comment: `<summary>`, plus `<param>`, `<returns>` and `<exception>` where they apply. Private members get one too whenever the reason behind them isn't obvious.
- Class docs explain what the class does and why. Put the author and the reference links in `<remarks>`:

```csharp
/// <remarks>
/// @author Devin Green (Artistro08)
/// @link https://github.com/Sendspin/spec/blob/main/connection.md#server-initiated-connections
/// </remarks>
```

- Use `//` comments, never `/* */`.
- Put a comment on its own line above the code, never at the end of a line.
- Start with a capital letter, end with a period, and leave one space after `//`.
- Comments say why the code does something, or what the next lines do. They never restate the syntax.
- Mark a deliberate shortcut that has a known limit with `ponytail:`, naming the limit and the upgrade path.
- Leave a `// TODO:` with the reason when something is blocked upstream, instead of quietly working around it.

## Windows App Practices

### User Experience

- Use WinUI common controls before building custom ones. They bring Windows styling, input handling and accessibility for free.
- Support Light, Dark and contrast themes. Use `{ThemeResource}` brushes, and re-apply any color set in code when `ActualThemeChanged` fires.
- Use Segoe Fluent Icons for glyphs, written as `` escapes in code.
- Use Mica on the window and Acrylic only on light-dismiss surfaces like flyouts and menus.
- Keep the system title bar behavior: the caption buttons, Snap Layouts and rounded corners must keep working with the custom title bar.
- Make every page work down to the minimum window size (720 × 520) and at high DPI, with scrolling wherever content can overflow.
- Offer right-click menus on items, matching the "..." menu, and keyboard shortcuts for common actions.

> Hover effects are done in code (`PointerEntered` / `PointerExited`), not with `VisualStateManager` setters in a `ControlTemplate`. Those crashed WinUI natively on first hover.

### Accessibility

- Give every icon-only button an `AutomationProperties.Name`.
- Make every action reachable from the keyboard, with a visible focus indicator.
- Never show meaning with color alone.

### Performance

- Keep memory low: decode images at the size they're shown, virtualize long lists, and keep the GC settings in the project file.
- Size caches on purpose and trim them (the art cache is capped).
- Don't wake the CPU in the background. Timers run only while they're needed.
- Build for both x64 and ARM64.

### Threading

- Background events never touch the UI directly. Hand them to the UI thread with `DispatcherQueue.TryEnqueue`.
- Never call a blocking or joining method from the thread it would wait on. Queue the work to the thread pool instead.
- Guard state that more than one thread touches with a lock, and create and dispose timers under that same lock.

### Security and Privacy

- Store tokens and keys in Windows Credential Manager (`PasswordVault`). Never put secrets in settings files, logs or source code.
- Treat everything from the network as untrusted: validate it, cap its size, and drop only that connection when it's bad.
- Don't spend real resources (audio devices, threads) on a connection before it has authenticated, and cap how many unauthenticated connections are held.
- Use the platform's cryptography libraries. Don't write your own primitives beyond what a protocol spec requires.
- Never require admin rights to install or run. The app ships as a signed, per-user MSIX.
- Keep NuGet dependencies to the minimum (today only SIPSorcery and Concentus), keep them current, and remove any that go unused.

## Workflow

1. Do each piece of work on a feature branch (`feature/...` or `fix/...`).
2. Build with 0 warnings, then install the MSIX and test the change in the installed app, not only a debug run.
3. Run a code audit against this document before merging.
4. Merge into `main` with `--no-ff`.

Build the installer package with:

```powershell
dotnet build MusicAssistant\MusicAssistant.csproj -c Release -p:Platform=x64 -p:WindowsPackageType=MSIX -p:AppxPackageSigningEnabled=true -p:PackageCertificateThumbprint=<thumbprint>
```

Commit messages and changelog lines are plain past-tense sentences that start with a verb: Added, Fixed, Updated, Removed, Moved.

### Files Used

- [`.editorconfig`](.editorconfig)
- [`MusicAssistant/MusicAssistant.csproj`](MusicAssistant/MusicAssistant.csproj)
- [`MusicAssistant/ExceptionFilters.cs`](MusicAssistant/ExceptionFilters.cs)
