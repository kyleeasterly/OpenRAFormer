# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository. Please thoroughly review the `openra_coding_standard` section below, as it contains important coding standards and practices that must be followed.

## Build Commands

**Building:**
- `make` - Build in Release mode
- `make check` - Build in Debug mode with code style checks
- `dotnet build` - Alternative build using .NET CLI

**Testing:**
- `make tests` - Run unit tests (NUnit)
- `make test` - Run YAML validation tests for mods
- `make check-scripts` - Validate Lua script syntax
- `./utility.sh all --check-yaml` - Comprehensive YAML validation

**Running:**
- `./launch-game.sh` (Linux/macOS) or `launch-game.cmd` (Windows)
- `./launch-game.sh Game.Mod=ra` - Launch specific mod (ra, cnc, d2k, ts)

**Code Quality:**
- `make check` - Run all code style checks and analyzers
- Code style is enforced via StyleCop.Analyzers and Roslynator.Analyzers
- Configuration in `.editorconfig` (tab indentation, 160 char line limit)

## Architecture Overview

OpenRA uses a modular, data-driven architecture built around an Actor-Component system:

### Core Components

1. **OpenRA.Game** - Core engine providing:
   - Actor system (entities in the game world)
   - World management and game loop
   - Graphics/rendering pipeline
   - Network/multiplayer infrastructure
   - Mod loading and ruleset handling

2. **OpenRA.Mods.Common** - Shared gameplay code:
   - Trait system (components that define actor behavior)
   - Common traits for units, buildings, weapons
   - UI widgets and controls
   - Lua scripting integration
   - Pathfinding algorithms

3. **Mod-specific assemblies** (OpenRA.Mods.Cnc, D2k, etc.):
   - Game-specific traits and logic
   - Custom units and mechanics

### Key Design Patterns

- **Actor-Trait System**: Game entities (actors) gain behavior through composable traits defined in YAML
- **Data-Driven Design**: Game rules, units, and maps defined in YAML files rather than code
- **Platform Abstraction**: OpenRA.Platforms.Default handles platform-specific code (graphics, audio, input)

### Adding New Features

When implementing new game logic:
1. Create traits in appropriate mod assembly (Common for shared, mod-specific otherwise)
2. Follow existing trait patterns - inherit from appropriate base classes
3. Define trait configuration in YAML rules files
4. Use activities for complex actor behaviors
5. Implement orders for player commands

### File Organization

- `mods/*/rules/` - YAML definitions for units, buildings, weapons
- `mods/*/maps/` - Map files and scripted missions
- `OpenRA.Mods.*/Traits/` - C# trait implementations
- `OpenRA.Mods.*/Activities/` - Actor behavior implementations
- `OpenRA.Mods.*/Widgets/` - UI components

## Development Guidelines

1. **Always run `make check` before commits** - ensures code style compliance
2. **Test on multiple platforms** - OpenRA supports Windows, Linux, macOS
3. **Maintain mod compatibility** - changes to Common affect all mods
4. **Follow YAML conventions** - use existing traits/properties where possible
5. **Branch from `bleed`** - main development branch for pull requests

## IMPORTANT: Build, Commit, and Push Workflow

**After making any code changes, ALWAYS follow this workflow:**

1. **Build the project** to verify changes compile:
   ```bash
   make  # or make check for style validation
   ```

2. **Commit changes** with descriptive message:
   ```bash
   git add -A
   git commit -m "Your descriptive commit message"
   ```

3. **Push to remote repository**:
   ```bash
   git push origin bleed  # or current branch name
   ```

This ensures code quality, maintains a clean commit history, and keeps the remote repository up to date.

<openra_coding_standard>
This page outlines OpenRA's coding standard, which is based on the [.NET conventions](http://msdn.microsoft.com/en-us/library/czefa0ke). Please consult the .NET conventions for any points that are not covered here. You should use [StyleCop](https://stylecop.codeplex.com/) or the OpenRA.Utility `--check-code-style` command to check your code prior to submission.  Our automated test suite will fail if your code contains style violations.

For your convenience this repository supports [EditorConfig](http://editorconfig.org/) to set up source code editors according to this projects format convention.

## The Short Version

* Use tabs for indentation, not spaces.
* Avoid trailing whitespace (tabs, spaces) at the end of code lines.  This includes empty lines.
* Write code, comments, and commit messages in English using standard ASCII encoding.  Patches that add a Byte Order Mark to a file will be rejected.
* Use `var` when declaring temporary variables inside methods.
* Naming:
  * Type, property, and method names are always PascalCase
  * Private variable names are camelCase
  * Public variable names are PascalCase.
* Keep functions short, and return early instead of nesting conditionals.
* Fields should be declared `readonly` if they are initialized in the constructor and then not expected to change.
* Braces around code blocks should reside on their own line, and should be omitted if the block contains a single statement (applies to each block separately for if-else statements).  The two exceptions to this rule are when there is a short statement (such as a property definition) where the entire statement can reside on a single line, or where the inner statement uses braces in which case all outer blocks should also include braces.
* Avoid excessive parenthesis around calculations.  StyleCop will flag this error.
* Include a single space around binary operators. StyleCop will flag this error.
* Do not explicitly declare `private` methods. Methods are private by default in C#.
* Don't use excessive xml documentation. A short `/// <summary />` is usually sufficient.  If you find that you need to explain what each of your parameters does, then you should consider renaming the parameters or splitting the function into smaller chunks.
* Comments go on the line above what you want to comment on.
* Comments start with an upper-case letter and end with a period.
* Most code is written to the C# 5 feature set, but newer features are being adopted on a case-by-case basis. Prefer consistency with the surrounding code over always using the latest features, unless they provide meaningful benefit to your use case.

## Additional Details

### Structure
  * One class per file, except in very special cases. The only common exception is traits, which require a `Foo/FooInfo` pair. TraitsInterfaces is another prominent exception. The rationale here is that having a bunch of largely one-line interfaces all in one file is easier for mod authors. This might change at some point.
  * Files should have the same name as the (primary) class they contain.
  * Don't create new dependencies between assemblies unless you have a really good reason for it.
  * Put classes in the namespace which fits their purpose best. A long-standing example of doing this *wrong* is putting client-side master server support in the `OpenRA.Server` namespace. It belongs in `OpenRA.Network`.

### Naming
  * Type names are PascalCase. If a typename contains an acronym longer than two characters, don't capitalize the whole thing. For two character acronyms, the correct case depends on context. GL, AL, etc should probably be capitalized; but Cg is always written with the lowercase 'g'. RA is capitalized by convention. Command & Conquer should be abbreviated Cnc.
  * All methods, properties, and events are PascalCase, regardless of visibility.
  * Public fields are PascalCase. There is nothing evil about public fields -- they are preferable to 'dumb' properties.
  * Private fields are camelCase.
  * Function parameters are camelCase. This allows the `Foo = foo;` pattern without requiring one side to be qualified with `this.`
  * Local variables are camelCase.
  * The name for a lambda argument which you don't care about is `_`. For example, `_ => Foo.Bar()` is a lambda function which ignores its argument.
  * Spell typenames correctly. If the spelling varies between dialects of English, prefer the US spelling. `Color`, not `Colour`. `Program`, not `Programme`. This is for consistency with the platform APIs, which are almost always US English.
  * If a variable is going to be bound to yaml, it is important for it to have a descriptive name. In many other cases, you should not use long names, especially for locals. 
  * Type parameters are `T`, `U`, `V` and so on. 
  * If there is one function you're talking about, it is reasonable to call it `f`. If there are two or three, `g` and `h` are perfectly good too. If you need four, you're trying to do too much in one function. 
  * `s` is a reasonable name for a string, if there is exactly one; 
  * `i`, `j`, `k` are perfectly good induction variables; 
  * If you have one value of generic type `T`, call it `t`. If you have a sequence [`IEnumerable<T>`], call it `ts`. If you have an `Actor` or an `Action` or an `Activity`, you can often use `a` with no loss of readability.
  * `int2` or `float2` values are often `u` or `v`.
  * An exception object should be named `e` or `ex`. If you don't actually want the object, don't give it a name at all [`catch(FooException) {}` or `catch {}`]. In most cases you *do* want the object, at least to log.

### Use of Variables, etc.
  * Assign to each local variable exactly once, at its point of definition. Use `var` instead of writing a typename if at all possible (there are a few cases where you can't).

### Use of Types:
  * Do not declare your own delegate types, unless it is impossible to use `Action<>` or `Func<>`. A rare but valid case where this *is* impossible is if a delegate type must take an `out` or `ref` parameter. To obtain a delegate type which would match `void Foo(ref int x)`, you cannot say `Action<ref int>`, since `ref int` is not a valid type parameter. This occurs in the Blowfish implementation, don't treat it as a good pattern to copy.
  * If you need pairs of things, use `Pair<>`. `Pair.New()` enables type inference for this. If at some point you need a third item, don't do `Pair<A,Pair<B,C>>`; that way lies madness. That's a good time to make a real type.
  * If computing some value repeatedly is expensive, don't roll your own caching. Use `Lazy<>`, and use the `Lazy.New()` wrapper to enable type inference.
  * If you want caching which can be invalidated, use `Cached<>`, and use the `Cached.New()` wrapper to enable type inference there too.
  * If you want a lazy key-value mapping, don't roll your own on `Dictionary<>`; use `Cache<>`, and `Cache.New()` for the type inference.
  * Avoid nested types. They are poorly supported by various tools (VS included), and cannot be made less verbose by `using`. A partial exception to this is an enum of values to be used only by the enclosing class, but this is usually poor design.

### General guidelines:
  * Convert `break/continue` to `return` by extracting a function. 
  * Favor standard query operators (in `System.Linq`) over hand-rolled loops whenever possible.
  * The `forever` loop is written `for(;;)`, not `while(true)`.
  * Iterator blocks for infinite sequences are *good*. They can be easily composed with a filter such as `.Take(n)`.
  * Iterator blocks which may take an arbitrarily long time to produce an element are *very bad*. For example, this simple iterator for infinitely repeating a sequence is poorly-behaved, since it hangs forever if `ts` is empty:

```csharp
IEnumerable<T> Cycle<T>(this IEnumerable<T> ts)
{
	for(;;)
		foreach(var t in ts)
			yield return t;
}
```

## Additional Tips

### Set Visual Studio to Use Tabs Instead of Spaces

By default, Visual Studio will use spaces instead of tabs for C# code. To make sure you use tabs while writing code for OpenRA, you can set Visual Studio to use tabs for C# code.

![](https://github.com/LavenderMoon/Docs/raw/master/VisualStudioUseTabs.Figure1.png)  
First, select `Tools` from the Visual Studio main menu, and click `Options...`.

![](https://github.com/LavenderMoon/Docs/raw/master/VisualStudioUseTabs.Figure2.png)  
Next, navigate to `Text Editor` > `C#` > `Tabs` and click `Use Tabs`. Visual Studio should now use tabs when you edit C# code.

### Set Visual Studio to Place �System� directives first when sorting usings

By default, Visual Studio will sort using statements alphabetically, instead of placing System usings first for C# code. To make sure usings are sorted correctly while writing code for OpenRA, you can set Visual Studio to place �System� directives first when sorting usings.

First, select `Tools` from the Visual Studio main menu, and click `Options...`.
Next, navigate to `Text Editor` > `C#` > `Advanced` and click `Place �System� directives first when sorting usings`. Visual Studio should now sort usings correctly when you edit C# code.

### Install StyleCop

Your code will not be accepted if it doesn't pass StyleCop's inspections. You can install StyleCop from [its official GitHub page](https://github.com/StyleCop/StyleCop). After you restart Visual Studio, StyleCop should be available from the `Tools` menu, or by pressing `Ctrl+Shift+Y`.

For MonoDevelop there is an addin [MonoDevelop.StyleCop](http://addins.monodevelop.com/Project/Index/54) which integrates it into the IDE as well.
</openra_coding_standard>
