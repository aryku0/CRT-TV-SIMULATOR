# DVD Logo Satisfaction

DVD Logo Satisfaction is a small Windows desktop app built with C#, .NET 10, and Avalonia UI. The project recreates the classic bouncing DVD logo screen saver with added interactive controls, sound effects, custom styling, and CRT-inspired visuals.

## Project Overview

This app was built as a creative desktop UI project to practise C# application development, animation logic, asset management, and user interface design. The main experience starts with a retro monitor sound effect, fades into a DVD logo, and then lets the user launch an animated bouncing logo inside a defined screen area.

The app includes a satisfaction slider that changes how often the logo aims toward clean corner hits, making the animation feel more controlled or more chaotic depending on the user's input.

## What I Built

- Created a Windows desktop app using C# and .NET 10.
- Migrated the interface to Avalonia UI for a modern desktop UI structure.
- Built a custom animated DVD logo that moves around a fixed bounce area.
- Added collision detection so the logo reacts when it hits walls or corners.
- Added random bounce sound effects using bundled MP3 files.
- Added random logo colour changes on selected bounces.
- Built a satisfaction slider that influences how accurately the logo targets corners.
- Added logic to prevent the logo from repeatedly hitting the same corner too often.
- Added fullscreen support with a custom fullscreen icon.
- Added Escape key support to leave fullscreen mode.
- Designed rounded, soft UI controls inspired by Apple-style interface elements.
- Used a bundled Futura-like font so the app looks consistent on other machines.
- Organised visual and audio assets inside the project so the app can be shared through GitHub.

## Technologies Used

- **C#** for the main application logic.
- **.NET 10** as the application framework.
- **Avalonia UI** for the desktop interface.
- **NAudio** for playing MP3 sound effects.
- **AXAML** for the app layout.
- **Git and GitHub** for version control and sharing the project.

## User Interface Features

The app includes a clean retro-inspired interface with:

- A DVD logo start screen.
- A custom Start button.
- A satisfaction slider.
- A fullscreen icon button.
- Smooth fading and dissolving controls.
- A CRT-style screen feel.
- Rounded UI elements.
- A bundled geometric sans font for consistent typography.

## Animation And Interaction

The DVD logo moves at a fixed speed and bounces around the screen area. The satisfaction slider changes the chance of the logo taking a more accurate path toward a corner hit.

At 100% satisfaction, the logo is designed to aim perfectly. At lower values, there is a controlled chance that the logo will miss, making the animation less predictable and more natural.

## Bundled Font

The app uses:

`DvdLogoApp/Assets/Fonts/Jost-500-Medium.otf`

Jost is a free geometric sans font with a Futura-like style. It is bundled with the project so the app keeps the same visual style when opened on another computer.

The font license is included here:

`DvdLogoApp/Assets/Fonts/OFL-Jost.txt`

## How To Open The Project

Open:

`DvdLogoApp.slnx`

in Visual Studio 2026, then run the app from Visual Studio.

## Skills Demonstrated

This project demonstrates:

- Desktop app development in C#.
- UI layout using Avalonia.
- Working with custom assets.
- Basic game-style animation logic.
- Collision detection.
- Randomised behaviour.
- Audio playback.
- Fullscreen window handling.
- GitHub project organisation.
- Writing readable, commented code.
