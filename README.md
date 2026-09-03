# CRT TV Simulator

CRT TV Simulator is a small Windows desktop app built with C#, .NET 10, and Avalonia UI. It recreates the feel of a physical CRT television, with live content shown inside a 3D TV model and the classic bouncing DVD logo used as the standard signal when there is no active input.

## Project Overview

This app was built as a creative desktop project to practise C# application development, animation logic, asset management, input handling, and interface design. The TV acts as the main display: an active input can provide the content, while the app falls back to the DVD screensaver when no signal is available.

The built-in DVD screensaver includes a satisfaction slider that changes how often the logo aims toward clean corner hits, making the animation feel more controlled or more chaotic depending on the user's input.

## What I Built

- Created a Windows desktop app using C# and .NET 10.
- Migrated the interface to Avalonia UI for a modern desktop UI structure.
- Built a custom animated DVD fallback signal that moves around a fixed screen area.
- Added a 3D CRT television model to act as the physical display housing.
- Designed the screen surface so it can display changing input content instead of only the DVD logo.
- Planned the input flow so active content takes priority and the DVD screensaver appears when no input is connected.
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

- **C#** for the application logic, animation, input handling, and screen rendering.
- **.NET 10** for the Windows desktop application.
- **Avalonia UI 12.1.1** for the desktop window, controls, layout, and styling.
- **Ab4d.SharpEngine 4.0.9594** for rendering the interactive 3D CRT television.
- **Ab4d.SharpEngine.glTF 4.0.9594** for importing the retro TV model.
- **NAudio 3.0.1** for playing the bundled MP3 bounce sounds.
- **SkiaSharp bitmap support** through the SharpEngine/Avalonia rendering stack.
- **AXAML** for the Avalonia interface layout.
- **Git and GitHub** for version control and sharing the project.

## User Interface Features

The app includes a clean retro-inspired interface with:

- A CRT television display with a DVD logo no-signal screen.
- A screen pipeline being developed to accept image, video, camera, or captured-screen input.
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

## Inputs And No-Signal Behaviour

The long-term goal is for the app to behave like a simulated CRT television rather than a single-purpose screensaver. When an active input is available, its content should be rendered on the TV screen. When there is no active input, the bouncing DVD logo becomes the default no-signal screen.

Potential inputs include local images and videos, a webcam feed, or desktop capture. External HDMI, console, or other physical video sources require a compatible capture device so Windows can receive the signal.

Power transitions are part of the TV simulation: powering off fades and flickers the screen to black, while powering on restores the active input or starts the DVD fallback screen.

## Bundled Font

The app uses:

`DvdLogoApp/Assets/Fonts/Jost-500-Medium.otf`

Jost is a free geometric sans font with a Futura-like style. It is bundled with the project so the app keeps the same visual style when opened on another computer.

The font license is included here:

`DvdLogoApp/Assets/Fonts/OFL-Jost.txt`

## 3D Model Credit

The 3D TV model uses "Retro 90's TV" by HiddenGhillieDhu from Sketchfab, licensed under CC-BY-4.0.

Source: `https://sketchfab.com/3d-models/retro-90s-tv-d1a52fcfd95d4901af3b6ae1359cc242`

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
